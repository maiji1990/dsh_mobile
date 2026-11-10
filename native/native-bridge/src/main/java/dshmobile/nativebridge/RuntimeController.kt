package dshmobile.nativebridge

import android.content.Context
import android.os.PowerManager
import android.os.StatFs
import android.util.Log
import com.dshbox.app.common.AppResult
import com.dshbox.app.sandbox.BundledRuntimeInstaller
import com.dshbox.app.sandbox.DefaultSandboxManager
import com.dshbox.app.sandbox.DshState
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

        /** Guest self-check that drives the ui_* facade over MCP stdio (TASK-010). */
        private const val GUEST_UI_CHECK = "/opt/dshbox-cache/dshmobile/ui-facade-check.mjs"

        /** Guest self-check that drives the browser-use adapter's own client (TASK-011). */
        private const val GUEST_BROWSER_CHECK = "/opt/dshbox-cache/dshmobile/browser-check.mjs"

        /** Profiles `dsh.probe` accepts; the name is the only caller-supplied token and must match exactly. */
        val PROBE_PROFILES = setOf("web", "headless", "sdk", "sdk-minimal", "acp", "plugin")

        val METHODS = setOf(
            "runtime.status", "runtime.prepare", "runtime.importBundle", "runtime.health",
            "runtime.diagnostics", "sandbox.start", "sandbox.stop", "dsh.start", "dsh.stop", "dsh.probe",
            "plugin.list", "plugin.add", "plugin.remove",
            "relay.start", "relay.stop", "relay.status", "relay.selftest",
            "browser.status", "browser.open", "browser.tools.enable", "browser.tools.disable", "browser.selftest",
        )

        /** Profiles whose plugins may be managed (the shipped templates; desktop is reserved by DSH). */
        val PLUGIN_PROFILES = setOf("web", "headless", "sdk", "sdk-minimal", "acp")

        /**
         * npm registry spec only: optional @scope, name, optional version/range/tag. No paths, URLs, git or
         * file: specs, and no shell metacharacters — the spec is interpolated into a fixed guest command.
         */
        val PACKAGE_SPEC = Regex("^(@[a-z0-9][a-z0-9._-]{0,213}/)?[a-z0-9][a-z0-9._-]{0,213}(@[A-Za-z0-9._^~<>=*-]{1,64})?$")
        val PACKAGE_NAME = Regex("^(@[a-z0-9][a-z0-9._-]{0,213}/)?[a-z0-9][a-z0-9._-]{0,213}$")

        /** The wrapper written by [GuestToolchain]; on PATH too, but called explicitly. */
        private const val GUEST_DSH = "/root/projects/.dsh/dshbox/bin/dsh"
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
    /** Whether the ui_* patch layer existed when DSH was last started by us; null when DSH was not started here. */
    @Volatile private var uiToolsAtDshStart: Boolean? = null
    /** Same for the browser-use adapter (browser_* tools), read by DSH from the profile at its start. */
    @Volatile private var browserToolsAtDshStart: Boolean? = null
    /** Browser automation host wiring (TASK-011): broker follows the sandbox state. */
    private val browser = BrowserTools(appContext, config)
    private val watcher: Job

    init {
        watcher = scope.launch {
            launch {
                browser.prepareAssets()
                manager.sandboxState.collect { browser.onSandboxState(it) }
            }
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

    private val webUi by lazy { WebUiController(manager, "http://${config.dshHost}:${config.dshPort}", scope) }

    /** Main thread only. See [WebUiController]. */
    fun attachWebUi(view: android.webkit.WebView, listener: WebUiListener?) = webUi.attach(view, listener)

    fun detachWebUi() = webUi.detach()

    fun reloadWebUi() = webUi.reload()

    fun completeWebFileChooser(uris: Array<android.net.Uri>?) = webUi.completeFileChooser(uris)

    /** Created on first use from the main thread (DshTerminalManager binds to the constructing Looper). */
    @Volatile private var terminalOrNull: TerminalController? = null

    fun terminal(): TerminalController =
        terminalOrNull ?: TerminalController(appContext, config, scope).also { terminalOrNull = it }

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
        browser.close()
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
        runCatching {
            // ui_* MCP facade (TASK-010); DSH only loads it while UiToolsRegistration's patch layer exists.
            copyAsset("dshmobile/ui-facade.mjs", File(File(config.guestCacheDir, "dshmobile"), "ui-facade.mjs"))
            copyAsset("dshmobile/ui-facade-check.mjs", File(File(config.guestCacheDir, "dshmobile"), "ui-facade-check.mjs"))
            copyAsset("dshmobile/browser-check.mjs", File(File(config.guestCacheDir, "dshmobile"), "browser-check.mjs"))
        }.onFailure { Log.w(TAG, "ui facade provisioning failed", it) }
        // `dsh plugin` needs pnpm: offline corepack cache + dsh/pnpm wrappers (TASK-009).
        GuestToolchain.provisionCorepack(appContext, config)
        GuestToolchain.writeShims(config)
    }

    /**
     * Relay status plus the ui_* tool registration: `registered` is the patch layer on disk, `loadedByDsh` what
     * the running DSH read at its start (null when DSH is not running or was not started here), and
     * `dshRestartNeeded` whether the two differ, so the UI can say "restart DSH" instead of implying the tools
     * are live.
     */
    private fun relayStatus(): JSONObject {
        val registered = UiToolsRegistration.isRegistered(config.userDataDir)
        val dshUp = manager.dshState.value.let { it == DshState.RUNNING || it == DshState.READY || it == DshState.STARTING }
        val loaded = uiToolsAtDshStart.takeIf { dshUp }
        return MobileAutomation.status(appContext).put(
            "uiTools",
            JSONObject()
                .put("registered", registered)
                .put("loadedByDsh", loaded ?: JSONObject.NULL)
                .put("dshRestartNeeded", loaded != null && loaded != registered)
                .put("conflict", UiToolsRegistration.hasConflict(config.userDataDir))
                .put("serverName", "ui"),
        )
    }

    /** Browser broker / adapter state plus whether the running DSH loaded the browser_* tools (cf. [relayStatus]). */
    private fun browserStatus(): JSONObject {
        val installed = browser.isAdapterInstalled()
        val dshUp = manager.dshState.value.let { it == DshState.RUNNING || it == DshState.READY || it == DshState.STARTING }
        val loaded = browserToolsAtDshStart.takeIf { dshUp }
        return browser.status()
            .put("sandboxState", manager.sandboxState.value.name)
            .put("toolsLoadedByDsh", loaded ?: JSONObject.NULL)
            .put("dshRestartNeeded", loaded != null && loaded != installed)
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
            "sandbox.stop" -> {
                // Terminal shells live inside the sandbox rootfs: stop them first (as DSHBox does).
                terminalOrNull?.stopAll()
                // The relay mailbox is only meaningful while the sandbox runs (optional module, ADR-0004).
                MobileAutomation.stop()
                UiToolsRegistration.unregister(config.userDataDir)
                manager.stopSandbox()
                uiToolsAtDshStart = null
                browserToolsAtDshStart = null
                done.ok(status())
            }
            "dsh.start" -> when (val r = manager.startDsh()) {
                is AppResult.Success -> {
                    // Patch layers are read at DSH start: remember whether this DSH got the ui_* tools.
                    uiToolsAtDshStart = UiToolsRegistration.isRegistered(config.userDataDir)
                    browserToolsAtDshStart = browser.isAdapterInstalled()
                    done.ok(
                    status().put("dsh", JSONObject()
                        .put("version", r.value.dshVersion ?: JSONObject.NULL)
                        .put("baseUrl", r.value.baseUrl)
                        .put("ready", r.value.ready)),
                    )
                }
                is AppResult.Failure -> done.fail(r)
            }
            "dsh.stop" -> { manager.stopDsh(); uiToolsAtDshStart = null; browserToolsAtDshStart = null; done.ok(status()) }
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
            "relay.start" -> {
                MobileAutomation.start(appContext)
                UiToolsRegistration.register(config.userDataDir)
                done.ok(relayStatus())
            }
            "relay.stop" -> {
                MobileAutomation.stop()
                UiToolsRegistration.unregister(config.userDataDir)
                done.ok(relayStatus())
            }
            "relay.status" -> done.ok(relayStatus())
            "browser.status" -> done.ok(browserStatus())
            "browser.open" -> {
                // Explicit Intent to the library's BrowserActivity (`:browser` process); returns to our launcher activity.
                browser.open(appContext.packageManager.getLaunchIntentForPackage(appContext.packageName)?.component)
                done.ok(browserStatus())
            }
            "browser.tools.enable", "browser.tools.disable" -> {
                // DSHBox's own browser-use installer / uninstaller, against the web profile (ADR-0005). The stage dir is
                // refreshed from the APK first so the scripts always match this build. No caller input reaches the shell.
                val install = method == "browser.tools.enable"
                runCatching { browser.stageAdapter() }.onFailure {
                    throw BridgeException(INTERNAL, "browser-use stage failed: ${it.message}")
                }
                done.ok(runGuestScript(BrowserTools.adapterCommand(install)).put("adapterInstalled", browser.isAdapterInstalled()))
            }
            "browser.selftest" -> {
                // Fixed read-only check through the adapter's own client (status + observe); never takes AI control.
                runCatching { browser.stageAdapter() }.onFailure {
                    throw BridgeException(INTERNAL, "browser-use stage failed: ${it.message}")
                }
                // Then: does DSH's composed web config include the adapter (it only registers tools when loaded)?
                // `(exit $rc)` keeps the browser check's exit code as the command's result.
                val d = "${'$'}"
                done.ok(runGuestScript(
                    "node $GUEST_BROWSER_CHECK ${BrowserTools.STAGE_GUEST_DIR}; rc=${d}?; echo; " +
                        "printf 'dump-config web, lines naming @local/browser-use: '; " +
                        "(cd /root/projects && $GUEST_DSH --profile web --dump-config 2>/dev/null | grep -c '@local/browser-use'); " +
                        "(exit ${d}rc)",
                ))
            }
            "relay.selftest" -> {
                // Fixed, read-only commands through the real guest CLI and mailbox: Relay's own readiness check, then
                // the ui_* facade driven as an MCP stdio server exactly as DSH's dsh-mcp-client would
                // (ui_list_devices, ui_snapshot, ui_get_element). No caller-supplied input reaches the shell.
                // The facade itself retries while Relay is waiting for the approval (Relay only waits ~8 s inline);
                // 50 s per call keeps two approvals inside the UI's 2 min deadline.
                val lines = ArrayList<String>()
                val marker = "DSHMOBILE_EXIT="
                val d = "${'$'}"
                manager.runGuestCommand(
                    "/opt/pilot/bin/relay doctor; echo; node $GUEST_UI_CHECK --approval-wait-ms 50000; echo $marker${d}?",
                    onLine = { synchronized(lines) { lines.add(it); if (lines.size > 400) lines.removeAt(0) } },
                )
                val all = synchronized(lines) { lines.toList() }
                val exit = all.lastOrNull { it.startsWith(marker) }?.removePrefix(marker)?.trim()?.toIntOrNull()
                done.ok(JSONObject()
                    .put("ok", exit == 0)
                    .put("exitCode", exit ?: JSONObject.NULL)
                    .put("output", all.filterNot { it.startsWith(marker) }.joinToString(NL))
                    .put("error", JSONObject.NULL))
            }
            "plugin.list" -> {
                val profile = requireProfile(payload)
                done.ok(runPluginCommand("plugin --profile $profile list --json --depth 0"))
            }
            "plugin.add" -> {
                val profile = requireProfile(payload)
                val spec = payload.optString("spec", "")
                if (!PACKAGE_SPEC.matches(spec)) throw BridgeException(BAD_REQUEST, "spec must be an npm registry package spec")
                done.ok(runPluginCommand("plugin --profile $profile add $spec"))
            }
            "plugin.remove" -> {
                val profile = requireProfile(payload)
                val name = payload.optString("name", "")
                if (!PACKAGE_NAME.matches(name)) throw BridgeException(BAD_REQUEST, "name must be an npm package name")
                done.ok(runPluginCommand("plugin --profile $profile remove $name"))
            }
            else -> throw BridgeException("METHOD_NOT_FOUND", "unknown method: $method")
        }
    }

    private fun requireProfile(payload: JSONObject): String {
        val profile = payload.optString("profile", "web")
        if (profile !in PLUGIN_PROFILES) throw BridgeException(BAD_REQUEST, "profile must be one of $PLUGIN_PROFILES")
        return profile
    }

    /**
     * Runs `dsh <args>` in the guest (args are built only from validated tokens). Reports the real exit status
     * and output; a non-zero exit is returned as `ok=false` in the payload, never turned into success.
     */
    private suspend fun runPluginCommand(args: String): JSONObject = runGuestScript("cd /root/projects && $GUEST_DSH $args")

    /**
     * Runs a fixed guest command (never caller input) and returns `{ok, exitCode, output, error}` with the real exit
     * code. Guest commands get a plain PATH; only DSHBox's DSH role adds the wrapper dirs, so give them the same PATH
     * DSH itself runs with (`dsh plugin` needs `pnpm`; sandbox-manager buildProotEnv, role "dsh").
     */
    private suspend fun runGuestScript(command: String): JSONObject {
        val lines = ArrayList<String>()
        val marker = "DSHMOBILE_EXIT="
        val path = "export PATH=/opt/dshbox:/root/projects/.dsh/dshbox/bin:${'$'}PATH"
        val r = manager.runGuestCommand("$path; $command; echo $marker${'$'}?", onLine = {
            synchronized(lines) { lines.add(it); if (lines.size > 2000) lines.removeAt(0) }
        })
        val all = synchronized(lines) { lines.toList() }
        val exit = all.lastOrNull { it.startsWith(marker) }?.removePrefix(marker)?.trim()?.toIntOrNull()
        val output = all.filterNot { it.startsWith(marker) }
        return JSONObject()
            .put("ok", exit == 0)
            .put("exitCode", exit ?: JSONObject.NULL)
            .put("output", output.joinToString("\n"))
            .put("error", (r as? AppResult.Failure)?.error?.message ?: JSONObject.NULL)
    }

    private val NL = "\n"

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
