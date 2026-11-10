package dshmobile.nativebridge

import com.dshbox.app.common.Versions
import com.dshbox.app.sandbox.RuntimeBundleLayout
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * Runs the shared golden vectors (tests/TestVectors) against the LEGACY DSHBox implementation that the bridge
 * still ships. The C# port in DshMobile.Runtime runs the very same files; if both pass, the two implementations
 * agree on every vector (migration plan PR-02 exit gate "同层包 hash、目录、行为和失败用例一致").
 */
class LegacyRuntimeParityTest {
    private fun vectors(name: String): JSONObject {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "global.json").isFile) dir = dir.parentFile
        checkNotNull(dir) { "repo root (global.json) not found" }
        return JSONObject(File(dir, "tests/TestVectors/$name").readText())
    }

    private fun JSONObject.stringMap(key: String): Map<String, String> {
        val o = optJSONObject(key) ?: return emptyMap()
        return o.keys().asSequence().associateWith { o.getString(it) }
    }

    @Test
    fun versionsCompare() {
        val arr = vectors("versions.json").getJSONArray("compare")
        assertTrue(arr.length() > 0)
        for (i in 0 until arr.length()) {
            val v = arr.getJSONObject(i)
            val a = v.getString("a")
            val b = v.getString("b")
            assertEquals("compare($a, $b)", v.getInt("sign"), Integer.signum(Versions.compare(a, b)))
        }
    }

    @Test
    fun layerOfArchiveName() {
        val arr = vectors("runtime-bundle-layout.json").getJSONArray("layerOfArchiveName")
        for (i in 0 until arr.length()) {
            val v = arr.getJSONObject(i)
            val expected = if (v.isNull("layer")) null else v.getString("layer")
            assertEquals(v.getString("name"), expected, RuntimeBundleLayout.layerOfArchiveName(v.getString("name")))
        }
    }

    @Test
    fun analyze() {
        val arr = vectors("runtime-bundle-layout.json").getJSONArray("analyze")
        assertTrue(arr.length() > 0)
        for (i in 0 until arr.length()) {
            val v = arr.getJSONObject(i)
            val label = v.getString("name")
            val entries = v.getJSONArray("entries").let { a -> List(a.length()) { a.getString(it) } }
            when (val r = RuntimeBundleLayout.analyze(entries)) {
                is RuntimeBundleLayout.Result.Unsafe -> {
                    assertEquals(label, "unsafe", v.getString("result"))
                    assertEquals(label, v.getString("unsafeEntry"), r.entryName)
                }
                is RuntimeBundleLayout.Result.Ok -> {
                    assertEquals(label, "ok", v.getString("result"))
                    assertEquals(label, v.stringMap("archives"), r.archives)
                    assertEquals(label, v.stringMap("sidecars"), r.sidecars)
                    assertEquals(label, if (v.isNull("profilePath")) null else v.getString("profilePath"), r.profilePath)
                    for ((raw, target) in v.stringMap("targets")) assertEquals("$label: $raw", target, r.targets[raw])
                }
            }
        }
    }
}
