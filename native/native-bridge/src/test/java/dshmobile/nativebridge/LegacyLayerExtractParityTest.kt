package dshmobile.nativebridge

import com.dshbox.app.common.AppResult
import com.dshbox.app.sandbox.BundleManager
import com.dshbox.app.sandbox.SandboxConfig
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File
import java.security.MessageDigest

/**
 * Extracts the shared layer fixtures (tests/TestVectors/layers, expectations in layer-extract.json) with the LEGACY
 * DSHBox BundleManager.extractTarGz. The C# LayerExtractor runs the same file (LayerExtractParityTests).
 *
 * Limits of the desktop JVM, stated rather than hidden: android.system.Os.symlink is a no-op stub here, so symlink
 * entries are not compared and cases marked `kotlinSkip` are not run; owner mode bits are not compared (Windows
 * JVM). Everything else - result codes, paths, kinds, sizes, content hashes - must match exactly.
 */
class LegacyLayerExtractParityTest {
    @get:Rule
    val tmp = TemporaryFolder()

    private fun repo(): File {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "global.json").isFile) dir = dir.parentFile
        return checkNotNull(dir) { "repo root (global.json) not found" }
    }

    private fun sha256(f: File) = MessageDigest.getInstance("SHA-256").digest(f.readBytes()).joinToString("") { "%02x".format(it) }

    /** path -> "kind|size|sha" for every dir/file under [root], root excluded. */
    private fun scan(root: File): Map<String, String> =
        root.walkTopDown().filter { it != root }.associate { f ->
            val rel = f.relativeTo(root).path.replace('\\', '/')
            rel to if (f.isDirectory) "dir" else "file|${f.length()}|${sha256(f)}"
        }

    @Test
    fun legacyExtractionMatchesSharedExpectations() {
        val repo = repo()
        val cases = JSONObject(File(repo, "tests/TestVectors/layer-extract.json").readText()).getJSONArray("cases")
        var ran = 0
        for (i in 0 until cases.length()) {
            val c = cases.getJSONObject(i)
            if (c.has("kotlinSkip")) continue
            val label = c.getString("name")
            val archive = File(repo, "tests/TestVectors/layers/" + c.getString("archive"))
            val dest = File(tmp.root, "case-$i")
            val expect = c.getJSONObject("expect")
            val r = BundleManager(SandboxConfig(appFilesDir = File(tmp.root, "files-$i"))).extractTarGz(archive, dest)
            val code = if (r is AppResult.Failure) r.error.code else "OK"
            assertEquals("$label: result", expect.getString("result"), code)
            ran++
            if (code != "OK") continue
            val expected = HashMap<String, String>()
            val entries = expect.getJSONArray("entries")
            for (j in 0 until entries.length()) {
                val e = entries.getJSONObject(j)
                when (e.getString("kind")) {
                    "dir" -> expected[e.getString("path")] = "dir"
                    "file" -> expected[e.getString("path")] = "file|${e.getLong("size")}|${e.getString("sha256")}"
                    "symlink" -> Unit // Os.symlink is a stub on the JVM
                }
            }
            assertEquals(label, expected.toSortedMap(), scan(dest).toSortedMap())
        }
        assertTrue("ran only $ran legacy cases", ran >= 10)
    }
}
