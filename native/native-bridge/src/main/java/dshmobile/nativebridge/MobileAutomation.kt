package dshmobile.nativebridge

import android.content.ComponentName
import android.content.Context
import android.provider.Settings
import com.dshbox.app.common.Constants
import interlock.relay.core.protocol.CapabilityDescriptor
import interlock.relay.core.protocol.CapabilityId
import interlock.relay.core.protocol.CapabilityRegistry
import interlock.relay.core.runtime.RelayConfig
import interlock.relay.core.runtime.RelayRuntime
import interlock.relay.core.spi.RelayCapabilities
import interlock.relay.core.spi.RelayPathPolicy
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

/**
 * Mobile automation execution layer (TASK-010, ADR-0004): DSHBox's Interlock Relay core, unmodified, with a
 * DSH Mobile host configuration. Optional module, off by default: it only runs after an explicit
 * `relay.start`, and `sandbox.stop` always stops it.
 */
internal object MobileAutomation {
    /**
     * V1 allowlist: read-only UI capabilities only. Anything not listed is never registered, so the sandbox
     * cannot call it (calendar, contacts, location, media, audio, clipboard, notifications, install, shell,
     * Shizuku, virtual displays, screen recording …). Write actions arrive once a consent surface exists.
     */
    val ALLOWED_WIRE = setOf("ui.snapshot", "ui.node")

    private val capabilities = object : RelayCapabilities {
        override fun all(): List<CapabilityDescriptor> =
            CapabilityRegistry.allDescriptors.filter { it.id.wire in ALLOWED_WIRE }

        override fun isUsable(id: CapabilityId): Boolean = id.wire in ALLOWED_WIRE
    }

    /**
     * Same deployment paths DSHBox uses, because DSHBox's sandbox-manager already binds `files/pilot` at guest
     * `/opt/pilot` for both DSH and guest commands. CLI name is neutral (`relay`, not a brand name).
     */
    private object Paths : RelayPathPolicy {
        override val guestEntry: String = "/opt/pilot"
        override val hostDirName: String = "pilot"
        override val cliName: String = "relay"
        override val artifactPrefix: String = "relay-"
        override val assetClient: String = RelayPathPolicy.DEFAULT_ASSET_CLIENT
        override val mediaAlbumDir: String = "DSH Mobile"
    }

    private const val A11Y_SERVICE = "interlock.relay.core.exec.a11y.RelayAccessibilityService"

    fun start(context: Context) {
        // Gated calls are shown as an approval notification (RelayApproval.kt). If notifications are disabled the
        // surface reports it cannot show, and Relay fails the call closed (E_GATE_NO_FOREGROUND).
        val app = context.applicationContext
        RelayRuntime.start(app, RelayConfig(surfaces = DshMobileRelaySurfaces(app), capabilities = capabilities, pathPolicy = Paths))
    }

    fun stop() = RelayRuntime.stop()

    fun status(context: Context): JSONObject {
        val app = context.applicationContext
        return JSONObject()
            .put("phase", RelayRuntime.phase(app).name)
            .put("channelRunning", RelayRuntime.isChannelRunning())
            .put("degradedReason", RelayRuntime.degradedReason(app) ?: JSONObject.NULL)
            .put("accessibilityEnabled", isAccessibilityEnabled(app))
            .put("allowed", JSONArray(ALLOWED_WIRE.sorted()))
            .put("guestCli", "${Paths.guestEntry}/bin/${Paths.cliName}")
    }

    /** Whether the user has enabled the Relay accessibility service in system settings (never enabled by us). */
    fun isAccessibilityEnabled(context: Context): Boolean {
        val enabled = Settings.Secure.getString(context.contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES) ?: return false
        val ours = ComponentName(context.packageName, A11Y_SERVICE)
        return enabled.split(':').any { ComponentName.unflattenFromString(it) == ours }
    }
}

/**
 * Registers the guest `ui_*` facade (`ui-facade.mjs`, an MCP stdio server) with DSH, only while mobile automation
 * is on (TASK-010, ADR-0004).
 *
 * DSH is not modified: the entry is an official `dsh-mcp-client` plugin row added with DSH's own `--patch` layer
 * syntax (`- insert:`). The unmodified DSHBox sandbox-manager passes a fixed set of host-owned `--patch` files to
 * DSH, each only when present and non-empty; DSH Mobile writes none of the others, so it uses the host layer
 * `DSH_OFFICIAL_PLUGIN_OVERLAY_RELATIVE_PATH` for this single entry. The user's profile `cordis.patch.yml` is
 * never touched. Patch layers are read when DSH starts, so a change takes effect on the next DSH start.
 */
internal object UiToolsRegistration {
    /** Guest path of the facade (host: config.guestCacheDir/dshmobile, bound at /opt/dshbox-cache). */
    const val GUEST_FACADE = "/opt/dshbox-cache/dshmobile/ui-facade.mjs"

    /** First line of the layer; only a file carrying it is ever deleted by us. */
    private const val MARKER = "# dshmobile:ui-tools v1"

    /** Must stay above the facade's own approval wait (90 s) so DSH never cuts a call that is still waiting. */
    private const val TOOL_CALL_TIMEOUT_MS = 120_000

    fun overlayFile(userDataDir: File): File = File(userDataDir, Constants.DSH_OFFICIAL_PLUGIN_OVERLAY_RELATIVE_PATH)

    /** The whole layer: one top-level YAML sequence (a second top-level node would make DSH drop the layer). */
    fun render(): String = buildString {
        append(MARKER).append('\n')
        append("# Written by DSH Mobile while 手机操控 is on; deleted when it is turned off. Read-only ui_* tools.\n")
        append("- insert:\n")
        append("    - id: dshmobile-ui\n")
        append("      name: '@deepseek-ai/dsh-mcp-client'\n")
        append("      config:\n")
        append("        serverName: ui\n")
        append("        transport: stdio\n")
        append("        command: !!js process.execPath\n")
        append("        args: ['").append(GUEST_FACADE).append("']\n")
        append("        toolCallTimeoutMs: ").append(TOOL_CALL_TIMEOUT_MS).append('\n')
        append("        failOnStartupError: false\n")
    }

    fun isRegistered(userDataDir: File): Boolean {
        val f = overlayFile(userDataDir)
        return f.isFile && runCatching { f.useLines { it.firstOrNull() == MARKER } }.getOrDefault(false)
    }

    /** Present but not ours: we neither overwrite nor delete it, and say so in relay.status. */
    fun hasConflict(userDataDir: File): Boolean = overlayFile(userDataDir).exists() && !isRegistered(userDataDir)

    /**
     * Writes the layer atomically (an empty or half-written patch file would stop DSH from starting).
     * Returns false without writing when the file holds someone else's content.
     */
    fun register(userDataDir: File): Boolean {
        val f = overlayFile(userDataDir)
        if (hasConflict(userDataDir)) return false
        f.parentFile?.mkdirs()
        val tmp = File(f.parentFile, f.name + ".tmp")
        tmp.writeText(render(), Charsets.UTF_8)
        if (!tmp.renameTo(f)) {
            tmp.copyTo(f, overwrite = true)
            tmp.delete()
        }
        return true
    }

    /**
     * Removes the layer if it is ours. Returns false (and leaves the file) when someone else's content is there,
     * so we never delete a layer we did not write.
     */
    fun unregister(userDataDir: File): Boolean {
        val f = overlayFile(userDataDir)
        if (!f.exists()) return true
        if (!isRegistered(userDataDir)) return false
        return f.delete()
    }
}
