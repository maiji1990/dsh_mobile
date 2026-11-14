package dshmobile.nativebridge

import com.dshbox.app.common.AppResult
import com.dshbox.app.sandbox.BundleManager
import com.dshbox.app.sandbox.DefaultSandboxManager
import com.dshbox.app.sandbox.DshLayer
import com.dshbox.app.sandbox.SandboxConfig
import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

/**
 * Runs tests/TestVectors/dsh-layer.json through the LEGACY DSHBox DshLayer.installFromBundle and
 * tests/TestVectors/runtime-installed.json through DefaultSandboxManager.isRuntimeInstalled, comparing the whole
 * files/ tree afterwards. The C# DshLayerInstaller / RuntimeInstallCheck run the same scenarios (DshLayerParityTests).
 * The DSH fixtures are plain tar, so no zstd native library is needed on the JVM.
 */
class LegacyDshLayerParityTest {
    @get:Rule
    val tmp = TemporaryFolder()

    private fun repo(): File {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "global.json").isFile) dir = dir.parentFile
        return checkNotNull(dir) { "repo root (global.json) not found" }
    }

    private fun materialize(root: File, tree: JSONObject) {
        for (path in tree.keys().asSequence().sorted()) {
            val f = File(root, path)
            val v = tree.getString(path)
            if (v == "dir") f.mkdirs() else { f.parentFile.mkdirs(); f.writeText(v) }
        }
    }

    private fun snapshot(root: File): Map<String, String> =
        root.walkTopDown().filter { it != root }.associate { f ->
            f.relativeTo(root).path.replace('\\', '/') to if (f.isDirectory) "dir" else f.readText()
        }

    private fun tree(o: JSONObject): Map<String, String> = o.keys().asSequence().associateWith { o.getString(it) }

    private fun JSONObject.stringOrNull(key: String): String? = if (isNull(key)) null else getString(key)

    @Test
    fun legacyDshLayerMatchesSharedScenarios() {
        val repo = repo()
        val scenarios = JSONObject(File(repo, "tests/TestVectors/dsh-layer.json").readText()).getJSONArray("scenarios")
        assertTrue(scenarios.length() >= 12)
        for (i in 0 until scenarios.length()) {
            val s = scenarios.getJSONObject(i)
            val label = s.getString("name")
            val files = File(tmp.root, "files-$i").apply { mkdirs() }
            materialize(files, s.getJSONObject("before"))
            val config = SandboxConfig(appFilesDir = files, appCacheDir = File(tmp.root, "cache-$i"))
            val layer = DshLayer(File(config.runtimeDir, "runtime-current"), BundleManager(config))
            val r = runBlocking {
                layer.installFromBundle(
                    File(repo, "tests/TestVectors/bundles/" + s.getString("bundle")),
                    s.stringOrNull("sha256"),
                    s.stringOrNull("newVersion"),
                    s.getBoolean("allowDowngrade"),
                )
            }
            when (r) {
                is AppResult.Success -> {
                    assertEquals("$label: result", s.getString("result"), "OK")
                    assertEquals("$label: changed", s.getBoolean("changed"), r.value.changed)
                    assertEquals("$label: version", s.stringOrNull("version"), r.value.version)
                }
                is AppResult.Failure -> assertEquals("$label: result", s.getString("result"), r.error.code)
            }
            assertEquals("$label: installed version", s.stringOrNull("version"), layer.installedVersion())
            assertEquals(label, tree(s.getJSONObject("after")).toSortedMap(), snapshot(files).toSortedMap())
        }
    }

    @Test
    fun legacyInstallCheckMatchesSharedScenarios() {
        val repo = repo()
        val scenarios = JSONObject(File(repo, "tests/TestVectors/runtime-installed.json").readText()).getJSONArray("scenarios")
        assertTrue(scenarios.length() >= 8)
        for (i in 0 until scenarios.length()) {
            val s = scenarios.getJSONObject(i)
            val label = s.getString("name")
            val files = File(tmp.root, "check-$i").apply { mkdirs() }
            materialize(files, s.getJSONObject("before"))
            val manager = DefaultSandboxManager(SandboxConfig(appFilesDir = files, appCacheDir = File(tmp.root, "ccache-$i")))
            assertEquals("$label: installed", s.getBoolean("installed"), manager.isRuntimeInstalled())
            assertEquals(label, tree(s.getJSONObject("after")).toSortedMap(), snapshot(files).toSortedMap())
        }
    }
}
