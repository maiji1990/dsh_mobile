package dshmobile.nativebridge

import android.content.Context
import android.util.Log
import com.dshbox.app.common.AppResult
import com.dshbox.app.common.Constants
import com.dshbox.app.sandbox.BundleManager
import com.dshbox.app.sandbox.SandboxConfig
import java.io.File

/**
 * Makes `dsh` and `pnpm` usable inside the guest (TASK-009): `dsh plugin …` forwards to pnpm, and the device has
 * no global pnpm — only Node's corepack. Ported from DSHBox `SandboxService` (GPL-3.0, v1.5.1):
 * `provisionCorepackCache`, `DSH_CLI_SHIM`, `PNPM_CLI_SHIM`. Not ported: DSHBox's apt/dpkg wrappers (need its
 * fakeroot setup) and the opencode wrapper (separate product).
 *
 * The wrappers live in `user-data/.dsh/dshbox/bin`, which DSHBox's sandbox-manager already puts on the guest PATH
 * (for DSH itself and for guest commands), so DSH's plugin manager finds `pnpm` the same way it does in DSHBox.
 */
internal object GuestToolchain {
    private const val TAG = "DshToolchain"
    private const val ASSET_DIR = "toolchain-cache"
    private const val COREPACK_ASSET = "corepack-pnpm-11.1.2.tar.zst"
    private const val READY_MARKER = ".ready-sha256"

    /** Unpacks the APK's corepack cache (offline pnpm 11.1.2) into guest `/opt/dshbox-cache/corepack`. Idempotent. */
    fun provisionCorepack(context: Context, config: SandboxConfig): Boolean {
        val cacheDir = File(config.guestCacheDir, "corepack")
        val marker = File(cacheDir, READY_MARKER)
        val expected = runCatching {
            context.assets.open("$ASSET_DIR/$COREPACK_ASSET.sha256").bufferedReader().use { it.readText().trim().substringBefore(' ') }
        }.getOrElse { Log.w(TAG, "corepack cache asset missing", it); return false }
        if (cacheDir.isDirectory && marker.isFile && marker.readText().trim() == expected) return true

        val stage = File(config.guestCacheDir, "corepack-stage-${System.currentTimeMillis()}").apply { mkdirs() }
        try {
            val tarball = File(stage, COREPACK_ASSET)
            context.assets.open("$ASSET_DIR/$COREPACK_ASSET").use { input -> tarball.outputStream().use { input.copyTo(it) } }
            val bundles = BundleManager(config)
            if (!bundles.verifySha256(tarball, expected)) {
                Log.w(TAG, "corepack cache sha256 mismatch; pnpm would fall back to a network download")
                return false
            }
            val unpacked = File(stage, "out")
            if (bundles.extractTarGz(tarball, unpacked) is AppResult.Failure) {
                Log.w(TAG, "corepack cache extraction failed")
                return false
            }
            cacheDir.deleteRecursively()
            if (!unpacked.renameTo(cacheDir)) {
                unpacked.copyRecursively(cacheDir, overwrite = true)
            }
            marker.writeText(expected)
            Log.i(TAG, "corepack cache (pnpm 11.1.2) provisioned")
            return true
        } catch (t: Throwable) {
            Log.w(TAG, "corepack cache provisioning failed: ${t.message}")
            return false
        } finally {
            stage.deleteRecursively()
        }
    }

    /** Writes the `dsh` and `pnpm` wrappers (overwritten every process start, like DSHBox). */
    fun writeShims(config: SandboxConfig): Boolean = runCatching {
        val bin = File(File(config.userDataDir, Constants.DSHBOX_ASSETS_RELATIVE_DIR), "bin").apply { mkdirs() }
        for ((name, body) in listOf("dsh" to DSH_SHIM, "pnpm" to PNPM_SHIM)) {
            val f = File(bin, name)
            f.writeText(body + "\n")
            f.setExecutable(true, false)
        }
        true
    }.getOrElse { Log.w(TAG, "writing dsh/pnpm wrappers failed: ${it.message}"); false }

    /** Same as DSHBox's DSH_CLI_SHIM: official CLI from the managed DSH layer, with the hard-link shim preloaded. */
    private val DSH_SHIM = """
        #!/bin/sh
        # DSH Mobile: `dsh` = the app-managed DSH layer's official CLI (same version and profiles as the WebUI).
        # The hard-link shim is required: DSH publishes files with link(), which Android's data partition refuses.
        # Note: `dsh web` would start a second web server and collide with the app's port 3080.
        [ -n "${'$'}DSH_HOME" ] || DSH_HOME=/root/projects/.dsh
        export DSH_HOME
        BIN="${'$'}{DSH_BIN:-/opt/dshapp/runtime/node_modules/@deepseek-ai/dsh/lib/bin.js}"
        SHIM=""
        [ -f /opt/dshbox/link-shim.mjs ] && SHIM="--import /opt/dshbox/link-shim.mjs"
        exec /usr/local/bin/node --expose-internals ${'$'}SHIM "${'$'}BIN" "${'$'}@"
    """.trimIndent()

    /**
     * Same as DSHBox's PNPM_CLI_SHIM: corepack pnpm pinned to the bundled 11.1.2 (offline), store kept where the
     * profile already recorded it (pnpm compares storeDir as a string and fails with ERR_PNPM_UNEXPECTED_STORE
     * otherwise), copy imports (hard links/clones fail with EACCES on Android), registry left to the caller.
     */
    private val PNPM_SHIM = """
        #!/bin/sh
        # DSH Mobile: `pnpm` via Node's bundled corepack (no global pnpm on device).
        if [ -d /opt/dshbox-cache ]; then
          export COREPACK_HOME=/opt/dshbox-cache/corepack
        fi
        STORE=""
        if [ -f ./.modules.yaml ]; then
          RECORDED=${'$'}(sed -n 's/.*"storeDir"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' ./.modules.yaml 2>/dev/null | head -n 1)
          if [ -z "${'$'}RECORDED" ]; then
            RECORDED=${'$'}(sed -n 's/^storeDir:[[:space:]]*//p' ./.modules.yaml 2>/dev/null | head -n 1)
          fi
          if [ -n "${'$'}RECORDED" ] && [ -d "${'$'}RECORDED" ]; then
            case "${'$'}RECORDED" in
              */v[0-9]*) STORE=${'$'}{RECORDED%/*} ;;
              *) STORE="${'$'}RECORDED" ;;
            esac
          fi
        fi
        if [ -z "${'$'}STORE" ]; then
          if [ -d /opt/dshbox-cache ]; then
            STORE=/opt/dshbox-cache/pnpm-store
          else
            STORE=/root/projects/.dsh/pnpm-store
          fi
        fi
        export pnpm_config_store_dir="${'$'}STORE"
        export pnpm_config_package_import_method=copy
        mkdir -p "${'$'}STORE" 2>/dev/null || true
        exec /usr/local/bin/corepack pnpm "${'$'}@"
    """.trimIndent()
}
