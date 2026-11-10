package dshmobile.nativebridge

import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.util.Log
import com.dshbox.app.common.Constants
import com.dshbox.app.sandbox.SandboxConfig
import com.dshbox.app.sandbox.SandboxState
import com.dshbox.browser.api.BrowserContract
import com.dshbox.browser.bridge.BrowserCliBroker
import com.dshbox.browser.runtime.BrowserActivity
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

/**
 * Browser automation host wiring (TASK-011, ADR-0005). DSHBox's browser stack runs unmodified:
 *  - [BrowserCliBroker] (main process) serves the guest CLI `/opt/browser/bin/browser` over a private socket and
 *    forwards to `BrowserCommandService` in the `:browser` process;
 *  - `BrowserActivity` (`:browser` process) is the browser UI the user sees and controls;
 *  - the `browser-use` Cordis adapter (24 `browser_*` tools) is installed into the web profile only when the user
 *    turns it on, with DSHBox's own install / uninstall scripts.
 * Lifecycle mirrors DSHBox's `DshApp.initializeBrowserTools`: broker up while the sandbox runs, otherwise stopped
 * and every Browser control grant revoked.
 */
internal class BrowserTools(private val appContext: Context, private val config: SandboxConfig) {
    private val broker: BrowserCliBroker? = runCatching {
        BrowserCliBroker(appContext, config.browserEntryDir).also { it.entryDir.mkdirs() }
    }.onFailure { Log.w(TAG, "browser CLI broker unavailable", it) }.getOrNull()

    @Volatile private var brokerRunning = false
    @Volatile private var lastBrokerError: String? = null

    /** Publishes the CLI assets into `files/browser-tools` (bound at guest `/opt/browser` by sandbox-manager). */
    fun prepareAssets() {
        runCatching { broker?.prepareAssets() }.onFailure {
            lastBrokerError = "assets: ${it.message}"
            Log.w(TAG, "browser CLI assets unavailable", it)
        }
    }

    /** Called on every sandbox state change. Never throws: a broken browser must not take the runtime down. */
    @Synchronized
    fun onSandboxState(state: SandboxState) {
        val b = broker ?: return
        runCatching {
            when (state) {
                SandboxState.STARTING, SandboxState.RUNNING, SandboxState.RECOVERING -> {
                    b.start()
                    brokerRunning = true
                    lastBrokerError = null
                }
                else -> {
                    b.stop()
                    brokerRunning = false
                    // Same as DSHBox: when the sandbox goes away, no AI keeps control of the browser.
                    appContext.sendBroadcast(
                        Intent(BrowserContract.ACTION_REVOKE_CONTROL).setPackage(appContext.packageName),
                        BrowserContract.internalPermission(appContext.packageName),
                    )
                }
            }
        }.onFailure {
            lastBrokerError = "${state.name}: ${it.message}"
            Log.w(TAG, "browser CLI channel unavailable in $state", it)
        }
    }

    /** Opens the browser UI for the user; [returnTo] is the activity its "back to app" button returns to. */
    fun open(returnTo: ComponentName?) {
        val intent = Intent(appContext, BrowserActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_REORDER_TO_FRONT or Intent.FLAG_ACTIVITY_NO_ANIMATION)
        if (returnTo != null) intent.putExtra(BrowserContract.EXTRA_RETURN_COMPONENT, returnTo)
        appContext.startActivity(intent)
    }

    // ---------------------------------------------------------------- browser-use adapter (browser_* tools)

    /** Host copy of the stage dir (guest [STAGE_GUEST_DIR]); refreshed from the APK before every install/uninstall. */
    private val stageDir: File get() = File(config.userDataDir, "${Constants.DSHBOX_PLUGINS_RELATIVE_DIR}/$PLUGIN_LEAF")

    /** The adapter's ownership marker inside the web profile, written by its install.sh. */
    private val installedMarker: File
        get() = File(config.userDataDir, ".dsh/profiles/${Constants.DSH_WEB_PROFILE}/node_modules/@local/browser-use/browser-use-install.json")

    fun isAdapterInstalled(): Boolean = installedMarker.isFile

    /** Copies the APK's `plugins/browser-use` tree to the stage dir (atomic swap; the scripts must be complete). */
    fun stageAdapter() {
        val parent = stageDir.parentFile!!.apply { mkdirs() }
        val tmp = File(parent, ".$PLUGIN_LEAF-stage-${System.nanoTime()}")
        try {
            copyAssetTree("plugins/$PLUGIN_LEAF", tmp)
            check(File(tmp, "install.sh").isFile && File(tmp, "uninstall.sh").isFile) { "browser-use installer assets missing" }
            stageDir.deleteRecursively()
            check(tmp.renameTo(stageDir)) { "could not publish browser-use stage dir" }
        } finally {
            tmp.deleteRecursively()
        }
    }

    fun status(): JSONObject = JSONObject()
        .put("brokerAvailable", broker != null)
        .put("brokerRunning", brokerRunning)
        .put("cliReady", File(config.browserEntryDir, "bin/browser").isFile)
        .put("channelPublished", File(config.browserEntryDir, "run/channel.json").isFile)
        .put("adapterInstalled", isAdapterInstalled())
        .put("lastError", lastBrokerError ?: JSONObject.NULL)
        .put("guestCli", "$GUEST_ENTRY/bin/browser")
        .put("adapterLogLines", JSONArray(adapterLogLines()))

    /**
     * Lines in DSH's own log (`files/logs/process-dsh.log`, app-private) that name the adapter. The adapter only
     * logs on failure (TOOLS_UNAVAILABLE, DEFINITION_FAILED, PARTIAL_REGISTRATION, …), so an empty list after a
     * DSH start with the adapter installed means no registration failure was reported. Only these lines are read
     * back, never the rest of the log; at most the last 5, each cut to 300 chars.
     */
    fun adapterLogLines(): List<String> {
        val log = File(config.logsDir, "process-dsh.log")
        if (!log.isFile) return emptyList()
        return runCatching {
            log.useLines { lines -> lines.filter { "@local/browser-use" in it }.toList() }
                .takeLast(5).map { it.take(300) }
        }.getOrDefault(emptyList())
    }

    fun close() {
        runCatching { broker?.close() }
        brokerRunning = false
    }

    private fun copyAssetTree(asset: String, dest: File) {
        val children = appContext.assets.list(asset).orEmpty()
        if (children.isEmpty()) {
            dest.parentFile?.mkdirs()
            appContext.assets.open(asset).use { input -> dest.outputStream().use { input.copyTo(it) } }
            if (dest.name.endsWith(".sh")) dest.setExecutable(true, true)
            return
        }
        dest.mkdirs()
        for (child in children) copyAssetTree("$asset/$child", File(dest, child))
    }

    companion object {
        private const val TAG = "DshBrowserTools"
        const val PLUGIN_LEAF = "browser-use"
        const val STAGE_GUEST_DIR = "${Constants.DSHBOX_PLUGINS_GUEST_DIR}/$PLUGIN_LEAF"
        const val GUEST_ENTRY = "/opt/browser"

        /** Guest command for install (true) / uninstall (false), exactly as DSHBox's AdapterPluginController runs it. */
        fun adapterCommand(install: Boolean): String =
            "bash $STAGE_GUEST_DIR/${if (install) "install.sh" else "uninstall.sh"} ${Constants.DSH_WEB_PROFILE_GUEST_DIR}"
    }
}
