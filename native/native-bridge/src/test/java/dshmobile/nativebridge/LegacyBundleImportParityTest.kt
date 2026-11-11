package dshmobile.nativebridge

import com.dshbox.app.common.AppResult
import com.dshbox.app.sandbox.DefaultSandboxManager
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
 * Runs the LayerInstaller scenarios (tests/TestVectors/install-scenarios.json, bundles in tests/TestVectors/bundles)
 * through the LEGACY DSHBox DefaultSandboxManager.importRuntimeBundle and compares the whole files/ tree afterwards.
 * The C# LayerInstaller runs the same scenarios (LayerInstallerParityTests). Layers in these bundles are plain tar,
 * so no zstd native library is needed on the JVM.
 */
class LegacyBundleImportParityTest {
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

    @Test
    fun legacyImportMatchesSharedScenarios() {
        val repo = repo()
        val scenarios = JSONObject(File(repo, "tests/TestVectors/install-scenarios.json").readText()).getJSONArray("scenarios")
        assertTrue(scenarios.length() >= 4)
        for (i in 0 until scenarios.length()) {
            val s = scenarios.getJSONObject(i)
            val label = s.getString("name")
            val files = File(tmp.root, "files-$i").apply { mkdirs() }
            materialize(files, s.getJSONObject("before"))
            val manager = DefaultSandboxManager(SandboxConfig(appFilesDir = files, appCacheDir = File(tmp.root, "cache-$i")))
            val r = runBlocking { manager.importRuntimeBundle(File(repo, "tests/TestVectors/bundles/" + s.getString("bundle"))) }
            val code = if (r is AppResult.Failure) r.error.code else "OK"
            assertEquals("$label: result", s.getString("result"), code)
            val expected = s.getJSONObject("after").let { o -> o.keys().asSequence().associateWith { o.getString(it) } }
            assertEquals(label, expected.toSortedMap(), snapshot(files).toSortedMap())
        }
    }
}
