package dshmobile.nativebridge;

import android.content.Context;
import android.util.Log;

import com.dshbox.app.common.Versions;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.util.List;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.RejectedExecutionException;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicLong;

/**
 * The only DSH Mobile native type exposed to .NET (see docs/PROVIDER_CONTRACTS.md).
 *
 * <p>Control plane only: requests are small JSON documents executed one at a
 * time on a single worker thread (single writer). High-throughput data (PTY,
 * screenshots, files) must not go through {@link #execute}.
 */
public final class DshNativeFacade {
    public static final int PROTOCOL_VERSION = 1;
    static final int DEFAULT_DEADLINE_MS = 30_000;
    private static final String TAG = "DshNativeFacade";

    // Stable error codes. Keep in sync with DshMobile.Core.Bridge.BridgeErrorCodes (checked by contract tests).
    static final String OK = "OK";
    static final String BAD_REQUEST = "BAD_REQUEST";
    static final String PROTOCOL_MISMATCH = "PROTOCOL_MISMATCH";
    static final String METHOD_NOT_FOUND = "METHOD_NOT_FOUND";
    static final String NOT_INITIALIZED = "NOT_INITIALIZED";
    static final String DUPLICATE_REQUEST = "DUPLICATE_REQUEST";
    static final String DEADLINE_EXCEEDED = "DEADLINE_EXCEEDED";
    static final String CLOSED = "CLOSED";
    static final String UNAVAILABLE = "UNAVAILABLE";
    static final String RUNTIME_ERROR = "RUNTIME_ERROR";
    static final String INTERNAL = "INTERNAL";

    private final ExecutorService worker;
    private final Set<String> inFlight = ConcurrentHashMap.newKeySet();
    private final List<NativeCallback> subscribers = new CopyOnWriteArrayList<>();
    private final AtomicLong pingSeq = new AtomicLong();
    private final PlatformInfo injectedInfo;

    private volatile PlatformInfo platform;
    private volatile Context appContext;
    private volatile RuntimeController runtime;
    private volatile boolean closed;

    public DshNativeFacade() {
        this(null);
    }

    /** Test hook: supply platform facts instead of reading android.os.Build. */
    DshNativeFacade(PlatformInfo info) {
        this.injectedInfo = info;
        this.worker = Executors.newSingleThreadExecutor(r -> {
            Thread t = new Thread(r, "dsh-native-bridge");
            t.setDaemon(true);
            return t;
        });
    }

    public static String getBridgeVersion() {
        return BuildConfig.BRIDGE_VERSION;
    }

    /** Idempotent. Only the application context is retained. */
    public void initialize(Context context) {
        if (closed) {
            return;
        }
        if (platform != null) {
            return;
        }
        if (context != null) {
            appContext = context.getApplicationContext() != null ? context.getApplicationContext() : context;
        }
        platform = injectedInfo != null ? injectedInfo : PlatformInfo.fromDevice();
        if (appContext != null) {
            // DSHBox sandbox-manager needs a real Context; JVM unit tests run without one.
            runtime = new RuntimeController(appContext, this::publishEvent);
        }
        publishLifecycle("INITIALIZED");
    }

    /**
     * Executes one request. The callback receives exactly one response JSON on the
     * worker thread (after {@link #close()}, the CLOSED response may arrive inline).
     */
    public void execute(String requestJson, NativeCallback callback) {
        if (callback == null) {
            throw new IllegalArgumentException("callback is required");
        }
        final long receivedNanos = System.nanoTime();
        final Request request;
        try {
            request = Request.parse(requestJson);
        } catch (BridgeException e) {
            dispatch(callback, null, () -> deliver(callback, error(null, e.code, e.getMessage())));
            return;
        }
        if (!inFlight.add(request.requestId)) {
            dispatch(callback, null, () -> deliver(callback,
                    error(request.requestId, DUPLICATE_REQUEST, "requestId already in flight")));
            return;
        }
        dispatch(callback, request.requestId, () -> {
            JSONObject immediate = null;
            try {
                immediate = preflight(request, receivedNanos);
                if (immediate == null && runtime != null && runtime.handles(request.method)) {
                    // Long-running runtime operation: completes later on an IO coroutine.
                    long remaining = request.deadlineMs - TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - receivedNanos);
                    runtime.execute(request.method, request.payload, Math.max(1, remaining), (ok, code, message, payload) -> {
                        try {
                            deliver(callback, ok ? okSafe(request.requestId, payload) : error(request.requestId, code, message, payload));
                        } finally {
                            inFlight.remove(request.requestId);
                        }
                    });
                    return;
                }
                if (immediate == null) {
                    immediate = run(request);
                }
            } catch (RuntimeException e) {
                immediate = error(request.requestId, INTERNAL, e.getClass().getSimpleName() + ": " + e.getMessage());
            }
            try {
                deliver(callback, immediate);
            } finally {
                inFlight.remove(request.requestId);
            }
        });
    }

    /**
     * Hosts the official DSH WebUI in {@code webView} (TASK-006). Main thread only. The DSH launch token
     * is handled entirely on the native side and is never returned to the caller.
     */
    public void attachWebUi(android.webkit.WebView webView, WebUiListener listener) {
        requireMainThread();
        requireRuntime().attachWebUi(webView, listener);
    }

    /** Releases the WebView hooks; safe to call repeatedly. Main thread only. */
    public void detachWebUi() {
        requireMainThread();
        RuntimeController rc = runtime;
        if (rc != null) {
            rc.detachWebUi();
        }
    }

    /** Reloads the WebUI with the current launch token. Main thread only. */
    public void reloadWebUi() {
        requireMainThread();
        requireRuntime().reloadWebUi();
    }

    /**
     * Completes a pending {@link WebUiListener#onFileChooserRequested} with the URIs the user picked
     * (null or empty = cancelled). Main thread only; extra calls are ignored.
     */
    public void completeWebFileChooser(android.net.Uri[] uris) {
        requireMainThread();
        RuntimeController rc = runtime;
        if (rc != null) {
            rc.completeWebFileChooser(uris);
        }
    }

    // ------------------------------------------------------------------ TASK-008 terminal (main thread)

    /**
     * Returns a terminal View bound to the active PRoot shell session (creating the first one), or null when
     * no runtime is installed. PTY data flows natively between emulator and View, never through execute().
     */
    public android.view.View createTerminalView(android.content.Context activityContext, TerminalListener listener) {
        requireMainThread();
        return requireRuntime().terminal().createView(activityContext, listener);
    }

    /** Detaches the current View; sessions keep running. */
    public void releaseTerminalView() {
        requireMainThread();
        RuntimeController rc = runtime;
        if (rc != null) {
            rc.terminal().releaseView();
        }
    }

    /** New shell session (activated); returns its id, or null when the runtime is missing. */
    public String newTerminalSession() {
        requireMainThread();
        return requireRuntime().terminal().newSession();
    }

    public void activateTerminalSession(String id) {
        requireMainThread();
        requireRuntime().terminal().activate(id);
    }

    public void closeTerminalSession(String id) {
        requireMainThread();
        requireRuntime().terminal().close(id);
    }

    /** JSON array of sessions (id, title, running, exited, exitCode, active); no terminal content. */
    public String terminalSessionsJson() {
        requireMainThread();
        return requireRuntime().terminal().sessionsJson();
    }

    /** ESC, TAB, UP, DOWN, LEFT, RIGHT, HOME, END, PGUP, PGDN or CTRL_C; returns false for unknown names. */
    public boolean terminalSendKey(String name) {
        requireMainThread();
        return requireRuntime().terminal().sendKey(name);
    }

    /** Latches Ctrl for the next typed letter. */
    public void setTerminalCtrl(boolean on) {
        requireMainThread();
        requireRuntime().terminal().setCtrl(on);
    }

    public void terminalPaste() {
        requireMainThread();
        requireRuntime().terminal().paste();
    }

    private RuntimeController requireRuntime() {
        RuntimeController rc = runtime;
        if (rc == null) {
            throw new IllegalStateException("initialize(context) has not been called with a real Context");
        }
        return rc;
    }

    private static void requireMainThread() {
        if (android.os.Looper.myLooper() != android.os.Looper.getMainLooper()) {
            throw new IllegalStateException("WebUI methods must be called on the main thread");
        }
    }

    public void subscribe(NativeCallback callback) {
        if (callback != null && !subscribers.contains(callback)) {
            subscribers.add(callback);
        }
    }

    public void unsubscribe(NativeCallback callback) {
        subscribers.remove(callback);
    }

    /** Requests accepted but not yet answered. The M1 stress test requires this to return to 0. */
    public int getInFlightCount() {
        return inFlight.size();
    }

    /** Idempotent. Queued requests are answered with CLOSED; new ones are rejected with CLOSED. */
    public void close() {
        if (closed) {
            return;
        }
        closed = true;
        RuntimeController rc = runtime;
        if (rc != null) {
            rc.close();
        }
        publishLifecycle("CLOSED");
        worker.shutdown();
        try {
            worker.awaitTermination(2, TimeUnit.SECONDS);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }
        subscribers.clear();
        appContext = null;
    }

    // ---------------------------------------------------------------- internals

    /** Common checks for every request; returns an error response, or null to proceed. */
    private JSONObject preflight(Request request, long receivedNanos) {
        if (closed) {
            return error(request.requestId, CLOSED, "facade closed");
        }
        long waitedMs = TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - receivedNanos);
        if (waitedMs > request.deadlineMs) {
            return error(request.requestId, DEADLINE_EXCEEDED, "queued " + waitedMs + "ms > deadline " + request.deadlineMs + "ms");
        }
        if (platform == null) {
            return error(request.requestId, NOT_INITIALIZED, "initialize() not called");
        }
        if (runtime == null && RuntimeController.Companion.getMETHODS().contains(request.method)) {
            return error(request.requestId, UNAVAILABLE, "runtime requires initialize(context) with a real Context");
        }
        return null;
    }

    private JSONObject run(Request request) {
        PlatformInfo info = platform;
        try {
            return ok(request.requestId, handle(request, info));
        } catch (BridgeException e) {
            return error(request.requestId, e.code, e.getMessage());
        } catch (RuntimeException | JSONException e) {
            Log.e(TAG, "method " + request.method + " failed", e);
            return error(request.requestId, INTERNAL, e.getClass().getSimpleName() + ": " + e.getMessage());
        }
    }

    private JSONObject handle(Request request, PlatformInfo info) throws BridgeException, JSONException {
        switch (request.method) {
            case "bridge.info": {
                JSONObject upstream = new JSONObject().put("dshbox", BuildConfig.DSHBOX_UPSTREAM);
                JSONArray abis = new JSONArray();
                for (String abi : info.abis) {
                    abis.put(abi);
                }
                return new JSONObject()
                        .put("bridgeVersion", BuildConfig.BRIDGE_VERSION)
                        .put("protocol", PROTOCOL_VERSION)
                        .put("upstream", upstream)
                        .put("kotlinVersion", kotlin.KotlinVersion.CURRENT.toString())
                        .put("sdkInt", info.sdkInt)
                        .put("abis", abis)
                        .put("deviceModel", info.deviceModel);
            }
            case "bridge.ping":
                return new JSONObject()
                        .put("echo", request.payload)
                        .put("seq", pingSeq.incrementAndGet());
            case "dshbox.versions.compare": {
                String a = requireString(request.payload, "a");
                String b = requireString(request.payload, "b");
                // DSHBox Kotlin (common module, unmodified): Java -> Kotlin object call.
                int raw = Versions.INSTANCE.compare(a, b);
                return new JSONObject().put("result", Integer.signum(raw));
            }
            default:
                throw new BridgeException(METHOD_NOT_FOUND, "unknown method: " + request.method);
        }
    }

    private static String requireString(JSONObject payload, String key) throws BridgeException {
        Object v = payload.opt(key);
        if (!(v instanceof String)) {
            throw new BridgeException(BAD_REQUEST, "payload." + key + " must be a string");
        }
        return (String) v;
    }

    private void dispatch(NativeCallback callback, String requestId, Runnable task) {
        try {
            worker.execute(task);
        } catch (RejectedExecutionException e) {
            if (requestId != null) {
                inFlight.remove(requestId);
            }
            deliver(callback, error(requestId, CLOSED, "facade closed"));
        }
    }

    private void publishLifecycle(String state) {
        try {
            publishEvent("bridge.lifecycle", new JSONObject().put("state", state));
        } catch (JSONException ignored) {
            // cannot happen for a constant key
        }
    }

    private void publishEvent(String event, JSONObject payload) {
        final String json;
        try {
            json = new JSONObject()
                    .put("protocol", PROTOCOL_VERSION)
                    .put("type", "event")
                    .put("event", event)
                    .put("payload", payload)
                    .toString();
        } catch (JSONException e) {
            return;
        }
        try {
            worker.execute(() -> {
                for (NativeCallback s : subscribers) {
                    safeInvoke(s, json);
                }
            });
        } catch (RejectedExecutionException ignored) {
            // closed
        }
    }

    private static void deliver(NativeCallback callback, JSONObject response) {
        safeInvoke(callback, response.toString());
    }

    private static void safeInvoke(NativeCallback callback, String json) {
        try {
            callback.onEvent(json);
        } catch (Throwable t) {
            // An exception thrown by the .NET side must never kill the single worker thread.
            Log.e(TAG, "callback threw", t);
        }
    }

    private static JSONObject ok(String requestId, JSONObject payload) throws JSONException {
        return envelope(requestId, true, OK, null).put("payload", payload);
    }

    private static JSONObject okSafe(String requestId, JSONObject payload) {
        try {
            return ok(requestId, payload == null ? new JSONObject() : payload);
        } catch (JSONException e) {
            return error(requestId, INTERNAL, "response encoding failed");
        }
    }

    private static JSONObject error(String requestId, String code, String message) {
        return error(requestId, code, message, null);
    }

    private static JSONObject error(String requestId, String code, String message, JSONObject details) {
        try {
            return envelope(requestId, false, code, message).put("payload", details == null ? JSONObject.NULL : details);
        } catch (JSONException e) {
            throw new IllegalStateException(e);
        }
    }

    private static JSONObject envelope(String requestId, boolean ok, String code, String message) throws JSONException {
        return new JSONObject()
                .put("protocol", PROTOCOL_VERSION)
                .put("requestId", requestId == null ? JSONObject.NULL : requestId)
                .put("ok", ok)
                .put("code", code)
                .put("message", message == null ? JSONObject.NULL : message);
    }

    /** Parsed, validated request. */
    static final class Request {
        final String requestId;
        final String method;
        final int deadlineMs;
        final JSONObject payload;

        private Request(String requestId, String method, int deadlineMs, JSONObject payload) {
            this.requestId = requestId;
            this.method = method;
            this.deadlineMs = deadlineMs;
            this.payload = payload;
        }

        static Request parse(String json) throws BridgeException {
            if (json == null) {
                throw new BridgeException(BAD_REQUEST, "request is null");
            }
            final JSONObject o;
            try {
                o = new JSONObject(json);
            } catch (JSONException e) {
                throw new BridgeException(BAD_REQUEST, "invalid JSON");
            }
            if (!o.has("protocol")) {
                throw new BridgeException(BAD_REQUEST, "missing protocol");
            }
            if (o.optInt("protocol", -1) != PROTOCOL_VERSION) {
                throw new BridgeException(PROTOCOL_MISMATCH, "expected protocol " + PROTOCOL_VERSION);
            }
            String id = o.optString("requestId", "");
            if (id.isEmpty()) {
                throw new BridgeException(BAD_REQUEST, "missing requestId");
            }
            String method = o.optString("method", "");
            if (method.isEmpty()) {
                throw new BridgeException(BAD_REQUEST, "missing method");
            }
            int deadline = o.has("deadlineMs") ? o.optInt("deadlineMs", -1) : DEFAULT_DEADLINE_MS;
            if (deadline <= 0) {
                throw new BridgeException(BAD_REQUEST, "deadlineMs must be > 0");
            }
            Object p = o.opt("payload");
            JSONObject payload;
            if (p == null || p == JSONObject.NULL) {
                payload = new JSONObject();
            } else if (p instanceof JSONObject) {
                payload = (JSONObject) p;
            } else {
                throw new BridgeException(BAD_REQUEST, "payload must be an object");
            }
            return new Request(id, method, deadline, payload);
        }
    }
}
