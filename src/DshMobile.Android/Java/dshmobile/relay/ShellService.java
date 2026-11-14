/*
 * Translated from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
 *   src/main/java/interlock/relay/core/exec/shizuku/RelayShellService.kt (onTransact/dispatch, ShellRunner,
 *   Outcome, TrustedDisplay) and the ShellProtocol codes in ShizukuState.kt.
 * Registered in docs/UPSTREAMS.md; see ADR-0009.
 *
 * Why Java: Shizuku instantiates this class by reflection inside an app_process that runs as the shell uid (2000)
 * with this APK on the class path and no .NET runtime. It is the remote executor only: it runs what the app-side
 * C# backend (DshMobile.Runtime ShizukuBackend) built from whitelisted verbs and per-token quoting. No policy,
 * approval or argument validation is decided here beyond the upstream size/name caps.
 */
package dshmobile.relay;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.PixelFormat;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.media.Image;
import android.media.ImageReader;
import android.os.Binder;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.IBinder;
import android.os.Parcel;
import android.os.ParcelFileDescriptor;
import android.os.SystemClock;
import android.view.Display;
import android.view.Surface;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.io.Reader;
import java.lang.reflect.Method;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;
import java.util.regex.Pattern;

public class ShellService extends Binder {
    public static final int CODE_RUN = IBinder.FIRST_CALL_TRANSACTION;
    public static final int CODE_INSTALL = IBinder.FIRST_CALL_TRANSACTION + 1;
    public static final int CODE_DISPLAY_CREATE = IBinder.FIRST_CALL_TRANSACTION + 2;
    public static final int CODE_DISPLAY_RELEASE = IBinder.FIRST_CALL_TRANSACTION + 3;
    public static final int CODE_DISPLAY_CAPTURE = IBinder.FIRST_CALL_TRANSACTION + 4;
    public static final int CODE_DISPLAY_QUERY = IBinder.FIRST_CALL_TRANSACTION + 5;
    public static final int CODE_SCREENSHOT = IBinder.FIRST_CALL_TRANSACTION + 7;
    /** Shizuku's UserService destroy transaction. */
    public static final int CODE_DESTROY = 16777115;

    private static final int MAX_OUTPUT_CHARS = 32_000;
    private static final String TRUNCATED_MARK = "\n…[output truncated]";
    private static final long TIMEOUT_JOIN_MS = 2_000L;
    private static final int EXIT_TIMED_OUT = 124;
    private static final int EXIT_REJECTED = -1;
    private static final String SHELL_TMP = "/data/local/tmp";
    private static final Pattern STAGED_NAME = Pattern.compile("[A-Za-z0-9][A-Za-z0-9._-]{0,79}\\.apk");
    private static final int COPY_BUFFER_BYTES = 64 * 1024;
    private static final long MAX_STAGED_BYTES = 128L * 1024L * 1024L;
    private static final long EXIT_GRACE_MS = 120L;

    private final Context injectedContext;

    public ShellService() {
        this(null);
    }

    /** Shizuku 13+ passes a Context when the class declares this constructor. */
    public ShellService(Context context) {
        injectedContext = context;
    }

    @Override
    protected boolean onTransact(int code, Parcel data, Parcel reply, int flags) {
        try {
            return dispatch(code, data, reply, flags);
        } catch (Throwable t) {
            return false;
        }
    }

    private boolean dispatch(int code, Parcel data, Parcel reply, int flags) throws Exception {
        switch (code) {
            case CODE_RUN: {
                String command = data.readString();
                long timeoutMs = data.readLong();
                if (command == null || command.trim().isEmpty()) {
                    return false;
                }
                writeOutcome(reply, ShellRunner.run(command, timeoutMs));
                return true;
            }
            case CODE_INSTALL: {
                ParcelFileDescriptor descriptor = data.readFileDescriptor();
                String name = data.readString();
                long timeoutMs = data.readLong();
                writeOutcome(reply, ShellRunner.install(descriptor, name, timeoutMs));
                return true;
            }
            case CODE_DISPLAY_CREATE: {
                int width = data.readInt();
                int height = data.readInt();
                int dpi = data.readInt();
                Object[] created = TrustedDisplay.create(injectedContext, width, height, dpi);
                if (reply != null) {
                    reply.writeInt((Integer) created[0]);
                    reply.writeString((String) created[1]);
                }
                return true;
            }
            case CODE_DISPLAY_RELEASE: {
                Object[] released = TrustedDisplay.release();
                if (reply != null) {
                    reply.writeInt((Integer) released[0]);
                    reply.writeString((String) released[1]);
                }
                return true;
            }
            case CODE_DISPLAY_CAPTURE: {
                long timeoutMs = data.readLong();
                int jpegQuality = data.readInt();
                int maxWidth = data.readInt();
                Object[] captured = TrustedDisplay.capture(timeoutMs, jpegQuality, maxWidth);
                String reason = (String) captured[1];
                if (reply != null) {
                    reply.writeInt(reason.isEmpty() ? 0 : -1);
                    reply.writeString(reason);
                }
                ParcelFileDescriptor descriptor = (ParcelFileDescriptor) captured[0];
                if (descriptor != null) {
                    try {
                        if (reply != null) {
                            reply.writeFileDescriptor(descriptor.getFileDescriptor());
                        }
                    } finally {
                        closeQuietly(descriptor);
                    }
                }
                return true;
            }
            case CODE_SCREENSHOT:
                screenshot(reply);
                return true;
            case CODE_DISPLAY_QUERY:
                if (reply != null) {
                    reply.writeInt(TrustedDisplay.displayId);
                    reply.writeString(TrustedDisplay.describe());
                }
                return true;
            case CODE_DESTROY:
                try {
                    TrustedDisplay.release();
                } catch (Throwable ignored) {
                    // exiting anyway
                }
                scheduleExit();
                return true;
            default:
                return super.onTransact(code, data, reply, flags);
        }
    }

    private static void writeOutcome(Parcel reply, Outcome outcome) {
        if (reply == null) {
            return;
        }
        reply.writeInt(outcome.exitCode);
        reply.writeString(outcome.stdout);
        reply.writeString(outcome.stderr);
    }

    /** screencap of the default display into the shell temp dir, handed back as a read-only descriptor and unlinked. */
    private static void screenshot(Parcel reply) {
        File target = new File(SHELL_TMP, "relay-shot-" + System.nanoTime() + ".png");
        String failure;
        try {
            Process process = new ProcessBuilder("/system/bin/screencap", "-p", target.getAbsolutePath())
                .redirectErrorStream(true).start();
            if (!process.waitFor(5_000L, TimeUnit.MILLISECONDS)) {
                process.destroyForcibly();
                failure = "screencap timed out";
            } else if (process.exitValue() != 0 || !target.isFile() || target.length() <= 0L) {
                failure = "screencap exit=" + process.exitValue();
            } else {
                failure = "";
            }
        } catch (Throwable t) {
            failure = "screencap failed: " + t.getClass().getSimpleName();
        }
        ParcelFileDescriptor descriptor = null;
        String reason = failure;
        if (reason.isEmpty()) {
            try {
                descriptor = ParcelFileDescriptor.open(target, ParcelFileDescriptor.MODE_READ_ONLY);
            } catch (Throwable t) {
                reason = "screencap open failed: " + t.getClass().getSimpleName();
            }
        }
        if (reason.isEmpty()) {
            target.setReadable(false, false);
            target.setReadable(true, true);
            target.setWritable(false, false);
        }
        if (!target.delete() && reason.isEmpty()) {
            reason = "frame was captured but " + target.getName() + " could not be removed from the shell temp dir";
        }
        if (reply != null) {
            reply.writeInt(reason.isEmpty() ? 0 : -1);
            reply.writeString(reason);
        }
        if (descriptor != null) {
            try {
                if (reply != null) {
                    reply.writeFileDescriptor(descriptor.getFileDescriptor());
                }
            } finally {
                closeQuietly(descriptor);
            }
        }
    }

    private static void scheduleExit() {
        Thread exit = new Thread(() -> {
            try {
                Thread.sleep(EXIT_GRACE_MS);
            } catch (InterruptedException ignored) {
                // exit now
            }
            System.exit(0);
        }, "relay-service-exit");
        exit.setDaemon(true);
        exit.start();
    }

    private static void closeQuietly(java.io.Closeable closeable) {
        try {
            if (closeable != null) {
                closeable.close();
            }
        } catch (Throwable ignored) {
            // nothing to do
        }
    }

    /** Upstream RelayShellService.Outcome. */
    public static final class Outcome {
        public final int exitCode;
        public final String stdout;
        public final String stderr;

        Outcome(int exitCode, String stdout, String stderr) {
            this.exitCode = exitCode;
            this.stdout = stdout;
            this.stderr = stderr;
        }
    }

    /** Upstream ShellRunner: sh -c with bounded output, a hard timeout and a staged-copy install. */
    static final class ShellRunner {
        private ShellRunner() {
        }

        static Outcome install(ParcelFileDescriptor descriptor, String name, long timeoutMs) {
            if (name == null || !STAGED_NAME.matcher(name).matches()) {
                return new Outcome(EXIT_REJECTED, "", "bad staged name");
            }
            if (descriptor == null) {
                return new Outcome(EXIT_REJECTED, "", "no file descriptor");
            }
            File target = new File(SHELL_TMP, name);
            try {
                boolean copied;
                try {
                    copied = writeCopy(descriptor, target);
                } catch (Throwable t) {
                    copied = false;
                } finally {
                    closeQuietly(descriptor);
                }
                if (!copied) {
                    target.delete();
                    return new Outcome(EXIT_REJECTED, "", "staged copy failed or exceeded size cap");
                }
                return run("pm install -r " + SHELL_TMP + "/" + name, timeoutMs);
            } finally {
                target.delete();
            }
        }

        private static boolean writeCopy(ParcelFileDescriptor descriptor, File target) throws Exception {
            try (FileInputStream input = new FileInputStream(descriptor.getFileDescriptor());
                 FileOutputStream output = new FileOutputStream(target)) {
                byte[] buffer = new byte[COPY_BUFFER_BYTES];
                long total = 0L;
                while (true) {
                    int read = input.read(buffer);
                    if (read < 0) {
                        break;
                    }
                    total += read;
                    if (total > MAX_STAGED_BYTES) {
                        return false;
                    }
                    output.write(buffer, 0, read);
                }
                return total > 0L;
            }
        }

        static Outcome run(String command, long timeoutMs) {
            TrustedDisplay.touch();
            final Process process;
            try {
                process = new ProcessBuilder("/system/bin/sh", "-c", command).redirectErrorStream(false).start();
            } catch (Throwable t) {
                return new Outcome(-1, "", "spawn failed: " + t.getClass().getSimpleName());
            }
            final Object lock = new Object();
            final StringBuilder stdout = new StringBuilder();
            final StringBuilder stderr = new StringBuilder();
            Thread out = startReader("relay-shell-out", () -> readBounded(process.getInputStream(), stdout, lock));
            Thread err = startReader("relay-shell-err", () -> readBounded(process.getErrorStream(), stderr, lock));
            boolean finished;
            try {
                finished = process.waitFor(timeoutMs, TimeUnit.MILLISECONDS);
            } catch (Throwable t) {
                finished = false;
            }
            if (!finished) {
                process.destroyForcibly();
            }
            joinQuietly(out);
            joinQuietly(err);
            if (out.isAlive() || err.isAlive()) {
                closeQuietly(process.getInputStream());
                closeQuietly(process.getErrorStream());
            }
            String o;
            String e;
            synchronized (lock) {
                o = stdout.toString();
                e = stderr.toString();
            }
            return new Outcome(finished ? process.exitValue() : EXIT_TIMED_OUT, o, e);
        }

        private static void joinQuietly(Thread thread) {
            try {
                thread.join(TIMEOUT_JOIN_MS);
            } catch (Throwable ignored) {
                // reported by isAlive
            }
        }

        private static Thread startReader(String name, Runnable body) {
            Thread thread = new Thread(body, name);
            thread.setDaemon(true);
            thread.start();
            return thread;
        }

        /** Keeps draining after the cap so the child never blocks on a full pipe; stores at most MAX_OUTPUT_CHARS. */
        private static void readBounded(InputStream stream, StringBuilder sink, Object lock) {
            try (Reader reader = new InputStreamReader(stream, StandardCharsets.UTF_8)) {
                char[] buffer = new char[8 * 1024];
                boolean storing = true;
                while (true) {
                    int read = reader.read(buffer);
                    if (read < 0) {
                        break;
                    }
                    if (!storing) {
                        continue;
                    }
                    synchronized (lock) {
                        int room = MAX_OUTPUT_CHARS - sink.length();
                        if (room >= read) {
                            sink.append(buffer, 0, read);
                        } else if (room > 0) {
                            sink.append(buffer, 0, room);
                            sink.append(TRUNCATED_MARK);
                            storing = false;
                        } else {
                            sink.append(TRUNCATED_MARK);
                            storing = false;
                        }
                    }
                }
            } catch (Throwable ignored) {
                // the stream closed under us: what was read stays
            }
        }
    }

    /** Upstream TrustedDisplay: one trusted virtual display owned by the shell uid, its frame pump and idle release. */
    static final class TrustedDisplay {
        static volatile int displayId = -1;
        private static volatile VirtualDisplay display;
        private static volatile String displayUniqueId;
        static final int CODE_UNVERIFIED = -2;
        private static volatile ImageReader images;
        private static volatile Thread pump;
        private static volatile Thread watchdog;
        private static volatile HandlerThread looperThread;
        private static Context cachedContext;
        private static final AtomicReference<Image> HELD = new AtomicReference<>();
        private static volatile int generation;
        private static volatile long lastUsedMs;

        private static final String DISPLAY_NAME = "relay-bg";
        private static final String DISPLAY_THREAD = "relay-display";
        private static final String SHELL_PACKAGE = "com.android.shell";
        private static final int FLAG_PUBLIC = 1;
        private static final int FLAG_OWN_CONTENT_ONLY = 8;
        private static final int FLAG_DESTROY_CONTENT_ON_REMOVAL = 256;
        private static final int FLAG_SHOULD_SHOW_SYSTEM_DECORATIONS = 512;
        private static final int FLAG_TRUSTED = 1024;
        private static final int FLAG_OWN_DISPLAY_GROUP = 2048;
        private static final int FLAG_ALWAYS_UNLOCKED = 4096;
        private static final int FLAGS = FLAG_TRUSTED | FLAG_OWN_DISPLAY_GROUP | FLAG_ALWAYS_UNLOCKED
            | FLAG_OWN_CONTENT_ONLY | FLAG_DESTROY_CONTENT_ON_REMOVAL | FLAG_SHOULD_SHOW_SYSTEM_DECORATIONS | FLAG_PUBLIC;
        private static final int MAX_IMAGES = 2;
        private static final int MIN_SIDE = 320;
        private static final int MAX_SIDE = 2560;
        private static final int MIN_DPI = 120;
        private static final int MAX_DPI = 640;
        private static final long FRAME_POLL_MS = 40L;
        private static final long MAX_CAPTURE_WAIT_MS = 5_000L;
        private static final int PNG_QUALITY = 100;
        private static final long IDLE_CHECK_MS = 30_000L;
        private static final long IDLE_RELEASE_MS = 5L * 60L * 1000L;
        private static final long THREAD_JOIN_MS = 1_000L;

        private enum Presence { PRESENT, ABSENT, UNKNOWN }

        private TrustedDisplay() {
        }

        static void touch() {
            lastUsedMs = SystemClock.elapsedRealtime();
        }

        static String describe() {
            if (displayId < 0) {
                return "";
            }
            ImageReader reader = images;
            int w = reader == null ? 0 : reader.getWidth();
            int h = reader == null ? 0 : reader.getHeight();
            return "id=" + displayId + " " + w + "x" + h;
        }

        private static Presence displayPresent(Context injected, int id, String expected) {
            if (id < 0) {
                return Presence.ABSENT;
            }
            DisplayManager manager;
            try {
                Context host = hostContext(injected);
                manager = host == null ? null : host.getSystemService(DisplayManager.class);
            } catch (Throwable t) {
                manager = null;
            }
            if (manager == null) {
                return Presence.UNKNOWN;
            }
            Display found;
            try {
                found = manager.getDisplay(id);
            } catch (Throwable t) {
                return Presence.UNKNOWN;
            }
            if (found == null) {
                return Presence.ABSENT;
            }
            String actual = uniqueIdOf(found);
            if (expected != null && actual != null && !actual.equals(expected)) {
                return Presence.ABSENT;
            }
            return Presence.PRESENT;
        }

        private static String uniqueIdOf(Display display) {
            try {
                Object id = display == null ? null : display.getClass().getMethod("getUniqueId").invoke(display);
                return id instanceof String ? (String) id : null;
            } catch (Throwable t) {
                return null;
            }
        }

        static synchronized Object[] create(Context injected, int width, int height, int dpi) {
            if (display != null) {
                switch (displayPresent(injected, displayId, displayUniqueId)) {
                    case PRESENT:
                        return new Object[] {displayId, "display already exists"};
                    case UNKNOWN:
                        return new Object[] {CODE_UNVERIFIED, "cannot verify existing display " + displayId
                            + ", left it running: release it with action=release, then create again"};
                    default:
                        Object[] cleared = release();
                        if ((Integer) cleared[0] < 0) {
                            return new Object[] {-1, "stale display " + displayId + " could not be cleared: " + cleared[1]};
                        }
                        break;
                }
            }
            int w = Math.max(MIN_SIDE, Math.min(MAX_SIDE, width));
            int h = Math.max(MIN_SIDE, Math.min(MAX_SIDE, height));
            int d = Math.max(MIN_DPI, Math.min(MAX_DPI, dpi));
            exemptHiddenMembers();
            HandlerThread thread = new HandlerThread(DISPLAY_THREAD);
            thread.start();
            looperThread = thread;
            try {
                ImageReader reader = ImageReader.newInstance(w, h, PixelFormat.RGBA_8888, MAX_IMAGES);
                Context host = hostContext(injected);
                DisplayManager manager = host == null ? null : host.getSystemService(DisplayManager.class);
                if (manager == null) {
                    throw new IllegalStateException("no display service");
                }
                Method enter = DisplayManager.class.getDeclaredMethod("createVirtualDisplay",
                    String.class, int.class, int.class, int.class, Surface.class, int.class,
                    VirtualDisplay.Callback.class, Handler.class);
                enter.setAccessible(true);
                VirtualDisplay created = (VirtualDisplay) enter.invoke(manager, DISPLAY_NAME, w, h, d, reader.getSurface(),
                    FLAGS, null, new Handler(thread.getLooper()));
                if (created == null || created.getDisplay() == null) {
                    throw new IllegalStateException("created display has no id");
                }
                int id = created.getDisplay().getDisplayId();
                images = reader;
                display = created;
                displayId = id;
                displayUniqueId = uniqueIdOf(created.getDisplay());
                Image stale = HELD.getAndSet(null);
                if (stale != null) {
                    stale.close();
                }
                touch();
                startPump(reader);
                startWatchdog();
                return new Object[] {id, ""};
            } catch (Throwable t) {
                Throwable cause = t instanceof java.lang.reflect.InvocationTargetException && t.getCause() != null ? t.getCause() : t;
                try {
                    if (display != null) {
                        display.release();
                    }
                } catch (Throwable ignored) {
                    // best effort
                }
                display = null;
                displayId = -1;
                displayUniqueId = null;
                Image stale = HELD.getAndSet(null);
                if (stale != null) {
                    stale.close();
                }
                thread.quitSafely();
                looperThread = null;
                try {
                    if (images != null) {
                        images.close();
                    }
                } catch (Throwable ignored) {
                    // best effort
                }
                images = null;
                return new Object[] {-1, cause.getClass().getSimpleName() + ": " + (cause.getMessage() == null ? "no message" : cause.getMessage())};
            }
        }

        static synchronized Object[] release() {
            VirtualDisplay current = display;
            if (current == null) {
                return new Object[] {0, "no display"};
            }
            ImageReader reader = images;
            display = null;
            displayId = -1;
            displayUniqueId = null;
            images = null;
            generation++;
            stopThread(pump);
            pump = null;
            stopThread(watchdog);
            watchdog = null;
            Throwable failure = null;
            try {
                current.release();
            } catch (Throwable t) {
                failure = t;
            }
            try {
                if (reader != null) {
                    reader.close();
                }
            } catch (Throwable ignored) {
                // best effort
            }
            Image stale = HELD.getAndSet(null);
            if (stale != null) {
                try {
                    stale.close();
                } catch (Throwable ignored) {
                    // best effort
                }
            }
            HandlerThread looper = looperThread;
            if (looper != null) {
                looper.quitSafely();
            }
            looperThread = null;
            return failure == null ? new Object[] {1, ""} : new Object[] {-1, "release failed: " + failure.getClass().getSimpleName()};
        }

        private static void stopThread(Thread thread) {
            if (thread == null || thread == Thread.currentThread()) {
                return;
            }
            try {
                thread.interrupt();
                thread.join(THREAD_JOIN_MS);
            } catch (Throwable ignored) {
                // daemon threads end with the process
            }
        }

        private static boolean sleepQuietly(long ms) {
            try {
                Thread.sleep(ms);
                return true;
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                return false;
            }
        }

        private static void startWatchdog() {
            if (watchdog != null && watchdog.isAlive()) {
                return;
            }
            final int own = generation;
            Thread t = new Thread(() -> {
                boolean alive = true;
                while (alive && displayId >= 0) {
                    alive = sleepQuietly(IDLE_CHECK_MS);
                    if (!alive || displayId < 0) {
                        continue;
                    }
                    if (own != generation) {
                        break;
                    }
                    long idleMs = SystemClock.elapsedRealtime() - lastUsedMs;
                    if (idleMs > IDLE_RELEASE_MS) {
                        try {
                            releaseIfCurrent(own);
                        } catch (Throwable ignored) {
                            // next round
                        }
                    }
                }
            }, "relay-display-idle");
            t.setDaemon(true);
            t.start();
            watchdog = t;
        }

        private static synchronized void releaseIfCurrent(int own) {
            if (own == generation) {
                release();
            }
        }

        static Object[] capture(long timeoutMs, int jpegQuality, int maxWidth) {
            ImageReader reader = images;
            if (reader == null) {
                return new Object[] {null, "no display"};
            }
            touch();
            long deadline = SystemClock.elapsedRealtime() + Math.max(1L, Math.min(MAX_CAPTURE_WAIT_MS, timeoutMs));
            Image frame = HELD.getAndSet(null);
            while (frame == null && SystemClock.elapsedRealtime() < deadline) {
                if (!sleepQuietly(FRAME_POLL_MS)) {
                    return new Object[] {null, "interrupted"};
                }
                Image latest;
                try {
                    latest = reader.acquireLatestImage();
                } catch (Throwable t) {
                    latest = null;
                }
                frame = latest != null ? latest : HELD.getAndSet(null);
            }
            if (frame == null) {
                return new Object[] {null, "no frame before deadline"};
            }
            File target = new File(SHELL_TMP, "relay-cap-" + System.nanoTime() + "." + (jpegQuality > 0 ? "jpg" : "png"));
            ParcelFileDescriptor descriptor = null;
            String failure;
            try (FileOutputStream out = new FileOutputStream(target)) {
                boolean compressed = encodeInto(frame, jpegQuality, maxWidth, out);
                if (!compressed) {
                    failure = "frame encode failed: compress";
                } else {
                    try {
                        descriptor = ParcelFileDescriptor.open(target, ParcelFileDescriptor.MODE_READ_ONLY);
                        failure = "";
                    } catch (Throwable t) {
                        failure = "frame encode failed: " + t.getClass().getSimpleName();
                    }
                }
            } catch (Throwable t) {
                failure = "frame encode failed: " + t.getClass().getSimpleName();
            } finally {
                try {
                    frame.close();
                } catch (Throwable ignored) {
                    // released with the reader
                }
            }
            target.delete();
            if (!failure.isEmpty()) {
                closeQuietly(descriptor);
                return new Object[] {null, failure};
            }
            return new Object[] {descriptor, ""};
        }

        private static boolean encodeInto(Image frame, int jpegQuality, int maxWidth, OutputStream out) {
            Image.Plane plane = frame.getPlanes()[0];
            ByteBuffer buffer = plane.getBuffer();
            int pixelStride = plane.getPixelStride();
            int rowPadding = plane.getRowStride() - pixelStride * frame.getWidth();
            Bitmap bitmap = Bitmap.createBitmap(frame.getWidth() + rowPadding / pixelStride, frame.getHeight(), Bitmap.Config.ARGB_8888);
            bitmap.copyPixelsFromBuffer(buffer);
            Bitmap cropped = rowPadding == 0 ? bitmap : Bitmap.createBitmap(bitmap, 0, 0, frame.getWidth(), frame.getHeight());
            try {
                if (maxWidth >= 1 && maxWidth < cropped.getWidth()) {
                    Bitmap scaled = Bitmap.createScaledBitmap(cropped, maxWidth, maxWidth * cropped.getHeight() / cropped.getWidth(), true);
                    if (scaled != cropped && cropped != bitmap) {
                        cropped.recycle();
                    }
                    cropped = scaled;
                }
                return jpegQuality > 0
                    ? cropped.compress(Bitmap.CompressFormat.JPEG, jpegQuality, out)
                    : cropped.compress(Bitmap.CompressFormat.PNG, PNG_QUALITY, out);
            } finally {
                if (cropped != bitmap) {
                    try {
                        cropped.recycle();
                    } catch (Throwable ignored) {
                        // best effort
                    }
                }
                bitmap.recycle();
            }
        }

        private static void startPump(ImageReader reader) {
            Thread t = new Thread(() -> {
                boolean running = true;
                while (running && !Thread.currentThread().isInterrupted() && reader == images) {
                    Image frame;
                    try {
                        frame = reader.acquireLatestImage();
                    } catch (Throwable e) {
                        frame = null;
                    }
                    if (frame != null) {
                        if (reader == images) {
                            Image previous = HELD.getAndSet(frame);
                            if (previous != null) {
                                previous.close();
                            }
                        } else {
                            frame.close();
                        }
                    }
                    running = sleepQuietly(FRAME_POLL_MS);
                }
            }, "relay-frame-pump");
            t.setDaemon(true);
            t.start();
            pump = t;
        }

        private static Context hostContext(Context injected) {
            if (cachedContext != null) {
                return cachedContext;
            }
            Context base = injected != null ? injected : systemContext();
            Context shell = null;
            try {
                if (base != null) {
                    shell = base.createPackageContext(SHELL_PACKAGE, Context.CONTEXT_IGNORE_SECURITY);
                }
            } catch (Throwable ignored) {
                // fall back to the base context
            }
            cachedContext = shell != null ? shell : base;
            return cachedContext;
        }

        private static Context systemContext() {
            try {
                Class<?> activityThread = Class.forName("android.app.ActivityThread");
                Method systemMain = activityThread.getDeclaredMethod("systemMain");
                systemMain.setAccessible(true);
                Object main = systemMain.invoke(null);
                Method getSystemContext = activityThread.getDeclaredMethod("getSystemContext");
                getSystemContext.setAccessible(true);
                return (Context) getSystemContext.invoke(main);
            } catch (Throwable t) {
                return null;
            }
        }

        private static void exemptHiddenMembers() {
            try {
                Class<?> vmRuntime = Class.forName("dalvik.system.VMRuntime");
                Method getRuntime = vmRuntime.getDeclaredMethod("getRuntime");
                getRuntime.setAccessible(true);
                Object runtime = getRuntime.invoke(null);
                Method exemptions = vmRuntime.getDeclaredMethod("setHiddenApiExemptions", String[].class);
                exemptions.setAccessible(true);
                exemptions.invoke(runtime, (Object) new String[] {"L"});
            } catch (Throwable ignored) {
                // the display call reports the denial
            }
        }
    }
}
