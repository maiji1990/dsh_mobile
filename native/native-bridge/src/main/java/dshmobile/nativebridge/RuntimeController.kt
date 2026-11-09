package dshmobile.nativebridge

import android.content.Context
import android.os.PowerManager
import android.os.StatFs
import android.util.Log
import com.dshbox.app.common.AppResult
import com.dshbox.app.sandbox.BundledRuntimeInstaller
import com.dshbox.app.sandbox.DefaultSandboxManager
import com.dshbox.app.sandbox.SandboxConfig
import com.dshbox.app.sandbox.SandboxManager
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withTimeout
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

/** Completion for one async runtime request. Exactly one call per request. */
fun interface RuntimeCompletion {
    fun complete(ok: Boolean, code: String, message: String?, payload: JSONObject?)
}

/** Pushes `runtime.state` events to the facade's subscribers. */
fun interface RuntimeEventSink {
    fun publish(event: String, payload: JSONObject)
}

/**
 * Drives DSHBox's unmodified `sandbox-manager` for the .NET host (TASK-004).
 *
 * Long operations (layer import, DSH start) run on IO coroutines, so they never block the
 * facade's single control thread; runtime operations are serialized among themselves by [opLock].
 * Results map DSHBox `AppResult.Failure` to the stable bridge code `RUNTIME_ERROR` and keep the
 * upstream code in the payload — nothing is reported as success unless DSHBox said so.
 */
internal class RuntimeController(
    context: Context,
    private val events: RuntimeEventSink,
) {
    companion object {
        private const val TAG = "DshRuntime"
        const val RUNTIME_ERROR = "RUNTIME_ERROR"
        const val BAD_REQUEST = "BAD_REQUEST"
        const val DEADLINE_EXCEEDED = "DEADLINE_EXCEEDED"
        const val INTERNAL = "INTERNAL"
        const val OK = "OK"

        /** Guest path where DSHBox binds the DSH layer (DefaultSandboxManager). */
        private const val GUEST_DSH_BIN = "/opt/dshapp/runtime/node_modules/@deepseek-ai/dsh/lib/bin.js"

        /** Fixed, read-only diagnostics. The bridge never runs caller-supplied shell commands. */
        private val DIAGNOSTICS = linkedMapOf(
            "arch" to "uname -m",
            "debian" to "cat /etc/debian_version",
            "node" to "node --version",
            "dsh" to "node $GUEST_DSH_BIN --version",
        )

        /** Guest path of our probe script (host: config.guestCacheDir/dshmobile, bound at /opt/dshbox-cache). */
        private const val GUEST_PROBE = "/opt/dshbox-cache/dshmobile/dsh-probe.mjs"

        /** Profiles `dsh.probe` accepts; the name is the only caller-supplied token and must match exactly. */
        val PROBE_PROFILES = setOf("web", "headless", "sdk", "sdk-minimal", "acp", "plugin")

        val METHODS = setOf(
            "runtime.status", "runtime.prepare", "runtime.importBundle", "runtime.health",
            "runtime.diagnostics", "sandbox.start", "sandbox.stop", "dsh.start", "dsh.stop", "dsh.probe",
        )
    }

    private val appContext: Context = context.applicationContext ?: context
    private val config = SandboxConfig(
        appFilesDir = appContext.filesDir,
        nativeLibraryDir = appContext.applicationInfo.nativeLibraryDir,
        appCacheDir = appContext.cacheDir,
    )
    private val powerManager = appContext.getSystemService(PowerManager::class.java)
    private val manager: SandboxManager = DefaultSandboxManager(
        config,
        isDeviceInteractive = { powerManager?.isInteractive ?: true },
    )
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val opLock = Mutex()
    @Volatile private var initialized = false
    private val watcher: Job

    init {
        watcher = scope.launch {
            combine(manager.sandboxState, manager.dshState, manager.dshVersion) { s, d, v -> Triple(s, d, v) }
                .distinctUntilChanged()
                .collect { (s, d, v) ->
                    events.publish(
                        "runtime.state",
                        JSONObject().put("sandboxState", s.name).put("dshState", d.name).put("dshVersion", v ?: JSONObject.NULL),
                    )
                }
        }
    }

    fun handles(method: String): Boolean = method in METHODS

    fun execute(method: String, payload: JSONObject, deadlineMs: Long, done: RuntimeCompletion) {
        scope.launch {
            try {
                withTimeout(deadlineMs) {
                    opLock.withLock {
                        ensureInitialized()
                        dispatch(method, payload, done)
                    }
                }
            } catch (e: TimeoutCancellationException) {
                done.complete(false, DEADLINE_EXCEEDED, "$method exceeded ${deadlineMs}ms", null)
            } catch (e: BridgeException) {
                done.complete(false, e.code, e.message, null)
            } catch (t: Throwable) {
                Log.e(TAG, "$method failed", t)
                done.complete(false, INTERNAL, "${t.javaClass.simpleName}: ${t.message}", null)
            }
        }
    }

    fun close() {
        watcher.cancel()
        scope.cancel()
    }

    // ------------------------------------------------------------------ methods

    private suspend fun ensureInitialized() {
        if (!initialized) {
            manager.initialize()
            provisionGuestAssets()
            initialized = true
        }
    }

    /**
     * Stages APK assets into host dirs that DSHBox binds into the guest. Overwritten once per process,
     * like DSHBox's SandboxService does on every boot, so an updated APK always ships the current files:
     *  - dshbox/link-shim.mjs + libdshbox-link.so -> config.dshShimDir (guest /opt/dshbox). DSHBox's
     *    DefaultSandboxManager preloads the shim (`node --import`) only when this file exists; without it,
     *    DSH's link()-based publishing (sessions, write tool, attachments, plugin installs) fails with
     *    EPERM because Android's app data partition refuses hard links.
     *  - dshmobile/dsh-probe.mjs -> config.guestCacheDir/dshmobile (guest /opt/dshbox-cache/dshmobile).
     */
    private fun provisionGuestAssets() {
        fun copyAsset(asset: String, dest: File) {
            dest.parentFile?.mkdirs()
            val tmp = File(dest.parentFile, dest.name + ".tmp")
            appContext.assets.open(asset).use { input -> tmp.outputStream().use { input.copyTo(it) } }
            if (!tmp.renameTo(dest)) {
                tmp.copyTo(dest, overwrite = true)
                tmp.delete()
            }
        }
        runCatching {
            copyAsset("dshbox/link-shim.mjs", config.dshShimFile)
            copyAsset("dshbox/libdshbox-link.so", File(config.dshShimDir, "libdshbox-link.so"))
        }.onFailure { Log.w(TAG, "link shim provisioning failed; DSH will run without hard-link compat", it) }
        runCatching {
            copyAsset("dshmobile/dsh-probe.mjs", File(File(config.guestCacheDir, "dshmobile"), "dsh-probe.mjs"))
        }.onFailure { Log.w(TAG, "probe script provisioning failed", it) }
    }

    private suspend fun dispatch(method: String, payload: JSONObject, done: RuntimeCompletion) {
        when (method) {
            "runtime.status" -> done.ok(status())
            "runtime.prepare" -> {
                val steps = JSONArray()
                when (val r = BundledRuntimeInstaller(appContext, config).installIfAbsent()) {
                    // Since DSHBox 1.4 the APK carries only android-side + DSH, so the layered installer
                    // (which needs base.tar.* + runtime-profile.json in assets) normally has nothing to do.
                    is AppResult.Success -> steps.put(if (r.value) "layered runtime installed from APK assets" else "no full layered runtime in APK (expected; base/node come from import)")
                    is AppResult.Failure -> return done.fail(r)
                }
                if (manager.isRuntimeInstalled()) {
                    steps.put(provisionBundledDsh())
                } else {
                    steps.put("base/node layers missing: import a runtime bundle (runtime.importBundle)")
                }
                done.ok(status().put("steps", steps))
            }
            "runtime.importBundle" -> {
                val file = resolveImportFile(payload.optString("path", ""))
                // DSHBox's swapRuntimeBody renames staged layers INTO runtime-current/. On a fresh install
                // nothing has created that directory yet, so the rename fails and its copyRecursively
                // fallback then dies on the guest's dangling /var/run symlink. Create the parent first.
                File(config.runtimeDir, "runtime-current").mkdirs()
                when (val r = manager.importRuntimeBundle(file)) {
                    is AppResult.Success -> {
                        // Provision first so the returned status reflects the installed DSH version.
                        val provision = provisionBundledDsh()
                        done.ok(status().put("dshProvision", provision))
                    }
                    is AppResult.Failure -> done.fail(r)
                }
            }
            "runtime.health" -> when (val r = manager.healthCheck()) {
                is AppResult.Success -> done.ok(
                    JSONObject()
                        .put("dshProcessRunning", r.value.dshProcessRunning)
                        .put("portOpen", r.value.portOpen)
                        .put("webUiReady", r.value.webUiReady)
                        .put("lastError", r.value.lastError ?: JSONObject.NULL),
                )
                is AppResult.Failure -> done.fail(r)
            }
            "runtime.diagnostics" -> {
                val out = JSONObject()
                for ((key, command) in DIAGNOSTICS) {
                    val lines = ArrayList<String>()
                    val r = manager.runGuestCommand(command, onLine = { if (lines.size < 20) lines.add(it) })
                    out.put(key, JSONObject()
                        .put("ok", r is AppResult.Success)
                        .put("output", lines.joinToString("\n"))
                        .put("error", (r as? AppResult.Failure)?.error?.message ?: JSONObject.NULL))
                }
                done.ok(out)
            }
            "sandbox.start" -> { manager.startSandbox(); done.ok(status()) }
            "sandbox.stop" -> { manager.stopSandbox(); done.ok(status()) }
            "dsh.start" -> when (val r = manager.startDsh()) {
                is AppResult.Success -> done.ok(
                    status().put("dsh", JSONObject()
                        .put("version", r.value.dshVersion ?: JSONObject.NULL)
                        .put("baseUrl", r.value.baseUrl)
                        .put("ready", r.value.ready)),
                )
                is AppResult.Failure -> done.fail(r)
            }
            "dsh.stop" -> { manager.stopDsh(); done.ok(status()) }
            "dsh.probe" -> {
                val profile = payload.optString("profile", "")
                if (profile !in PROBE_PROFILES) throw BridgeException(BAD_REQUEST, "profile must be one of $PROBE_PROFILES")
                val lines = ArrayList<String>()
                val r = manager.runGuestCommand("node $GUEST_PROBE $profile", onLine = { synchronized(lines) { lines.add(it); if (lines.size > 400) lines.removeAt(0) } })
                val resultLine = synchronized(lines) { lines.lastOrNull { it.startsWith("DSHPROBE ") } }
                if (resultLine == null) {
                    val why = (r as? AppResult.Failure)?.error?.message ?: "probe produced no result line"
                    throw BridgeException(INTERNAL, "$why; tail: ${synchronized(lines) { lines.takeLast(8).joinToString(" | ") }}")
                }
                done.ok(JSONObject(resultLine.removePrefix("DSHPROBE ")))
            }
            else -> throw BridgeException("METHOD_NOT_FOUND", "unknown method: $method")
        }
    }

    private fun status(): JSONObject {
        val free = runCatching { StatFs(appContext.filesDir.absolutePath).availableBytes }.getOrDefault(-1L)
        return JSONObject()
            .put("sandboxState", manager.sandboxState.value.name)
            .put("dshState", manager.dshState.value.name)
            .put("dshVersion", manager.dshVersion.value ?: JSONObject.NULL)
            .put("dshUnresponsive", manager.dshUnresponsive.value)
            .put("runtimeInstalled", manager.isRuntimeInstalled())
            .put("nodeLayerInstalled", manager.isNodeLayerInstalled())
            // Only whether a launch token exists; the token itself never crosses the control plane.
            .put("hasLaunchToken", manager.dshLaunchToken.value != null)
            .put("linkShim", config.dshShimFile.isFile)
            .put("endpoint", "http://${config.dshHost}:${config.dshPort}")
            .put("freeBytes", free)
    }

    /**
     * Import sources are restricted to this app's own storage (internal files/cache or the
     * app-specific external dir, where `adb push` and SAF copies land). Canonical paths defeat
     * `..` and symlink escapes.
     */
    private fun resolveImportFile(path: String): File {
        if (path.isEmpty()) throw BridgeException(BAD_REQUEST, "payload.path is required")
        val file = File(path).canonicalFile
        val roots = listOfNotNull(appContext.filesDir, appContext.cacheDir, appContext.getExternalFilesDir(null))
            .map { it.canonicalFile }
        if (roots.none { file.path.startsWith(it.path + File.separator) }) {
            throw BridgeException(BAD_REQUEST, "path must be inside the app's own storage")
        }
        if (!file.isFile) throw BridgeException(BAD_REQUEST, "no such file: ${file.path}")
        return file
    }

    /**
     * Installs the APK-bundled DSH layer (assets/dsh/<version>.tar.zst + .sha256) with DSHBox's own
     * version arbitration (installed-newer wins). Ported from DSHBox `SandboxService.provisionBundledDsh`
     * (GPL-3.0, v1.5.1) — same semantics, no UI side effects.
     */
    private suspend fun provisionBundledDsh(): String {
        val assets = appContext.assets
        val entries = runCatching { assets.list("dsh") }.getOrNull() ?: return "no DSH baseline in APK"
        val tarball = entries.firstOrNull { it.endsWith(".tar.zst") }
            ?: entries.firstOrNull { it.endsWith(".tar.gz") } ?: return "no DSH baseline in APK"
        val version = tarball.removeSuffix(".tar.zst").removeSuffix(".tar.gz").removeSuffix("-patched")
        val out = File(appContext.cacheDir, "dsh-bundled-$version.tar")
        return try {
            assets.open("dsh/$tarball").use { input -> out.outputStream().use { input.copyTo(it) } }
            val sha = if (entries.contains("$tarball.sha256")) {
                assets.open("dsh/$tarball.sha256").use {
                    it.readBytes().decodeToString().trim().split(Regex("\\s+")).firstOrNull()
                }
            } else {
                null
            }
            when (val r = manager.updateDsh(out, sha, version)) {
                is AppResult.Success ->
                    if (r.value.changed) "DSH ${r.value.version} installed" else "DSH kept at ${r.value.version}"
                is AppResult.Failure -> "DSH provision failed: ${r.error.code} ${r.error.message}"
            }
        } finally {
            out.delete()
        }
    }

    private fun RuntimeCompletion.ok(payload: JSONObject) = complete(true, OK, null, payload)

    private fun RuntimeCompletion.fail(r: AppResult.Failure) = complete(
        false,
        RUNTIME_ERROR,
        "${r.error.code}: ${r.error.message}",
        JSONObject().put("upstreamCode", r.error.code).put("recoverable", r.error.recoverable),
    )
}
