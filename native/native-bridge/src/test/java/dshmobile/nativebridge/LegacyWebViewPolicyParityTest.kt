package dshmobile.nativebridge

import com.dshbox.dsh.renderer.api.DshOriginPolicy
import com.dshbox.dsh.renderer.api.DshTokenRevisionTracker
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * Runs tests/TestVectors/webview-policy.json against the LEGACY code: DSHBox DshOriginPolicy (java.net.URI),
 * DshTokenRevisionTracker and WebUiController.urlWithToken. The C# port runs the same file (WebViewPolicyParityTests).
 * Mismatches are collected and reported together, so a vector file can be corrected against the real JVM in one go.
 */
class LegacyWebViewPolicyParityTest {
    private fun doc(): JSONObject {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "global.json").isFile) dir = dir.parentFile
        return JSONObject(File(checkNotNull(dir), "tests/TestVectors/webview-policy.json").readText())
    }

    @Test
    fun originPolicy() {
        val bases = doc().getJSONArray("originPolicy")
        val wrong = mutableListOf<String>()
        var checked = 0
        for (i in 0 until bases.length()) {
            val b = bases.getJSONObject(i)
            val base = b.getString("base")
            val policy = runCatching { DshOriginPolicy.create(base) }.getOrNull()
            if ((policy != null) != b.getBoolean("valid")) wrong += "base '$base': valid=${policy != null}"
            checked++
            if (policy == null || !b.has("candidates")) continue
            val cs = b.getJSONArray("candidates")
            for (j in 0 until cs.length()) {
                val c = cs.getJSONObject(j)
                val actual = policy.isTrusted(c.getString("url"))
                if (actual != c.getBoolean("trusted")) wrong += "base '$base' url '${c.getString("url")}': trusted=$actual"
                checked++
            }
        }
        assertTrue("checked only $checked", checked > 50)
        assertEquals(wrong.joinToString("\n"), 0, wrong.size)
    }

    @Test
    fun tokenTracker() {
        val seqs = doc().getJSONArray("tokenTracker")
        for (i in 0 until seqs.length()) {
            val seq = seqs.getJSONObject(i)
            val t = DshTokenRevisionTracker()
            val steps = seq.getJSONArray("steps")
            for (j in 0 until steps.length()) {
                val s = steps.getJSONObject(j)
                val label = "${seq.getString("name")} #$j"
                if (s.optBoolean("clear")) {
                    t.clearForRelease()
                } else {
                    val v = if (s.isNull("update")) null else s.getString("update")
                    assertEquals(label, s.getBoolean("changed"), t.update(v))
                }
                assertEquals(label, s.getBoolean("hasToken"), t.token != null)
                assertEquals(label, if (s.isNull("revision")) null else s.getLong("revision"), t.revision)
                assertTrue(label, t.token == null || !t.toString().contains(t.token!!))
            }
        }
    }

    @Test
    fun urlWithToken() {
        val cases = doc().getJSONArray("urlWithToken")
        for (i in 0 until cases.length()) {
            val c = cases.getJSONObject(i)
            val token = if (c.isNull("token")) null else c.getString("token")
            assertEquals(c.getString("base") + " / " + token, c.getString("url"), WebUiController.urlWithToken(c.getString("base"), token))
        }
    }
}
