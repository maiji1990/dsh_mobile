package dshmobile.nativebridge

import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * Runs tests/TestVectors/dsh-health-policy.json through the LEGACY DSHBox decideDshHealth. The policy and its state
 * types are `internal` to the sandbox-manager module, so they are reached through reflection (test-only). The C#
 * DshHealthPolicy runs the same vectors (DshHealthPolicyParityTests).
 */
class LegacyDshHealthPolicyParityTest {
    private fun repo(): File {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "global.json").isFile) dir = dir.parentFile
        return checkNotNull(dir) { "repo root (global.json) not found" }
    }

    private val stateClass = Class.forName("com.dshbox.app.sandbox.DshWindowState")
    private val probeClass = Class.forName("com.dshbox.app.sandbox.DshHealthProbe")
    private val decide = Class.forName("com.dshbox.app.sandbox.DshHealthPolicyKt").getDeclaredMethod(
        "decideDshHealth", stateClass, probeClass, java.lang.Long.TYPE, java.lang.Long.TYPE, Integer.TYPE,
    ).apply { isAccessible = true }

    private fun state(a: JSONArray): Any = stateClass.getDeclaredConstructor(
        java.lang.Boolean.TYPE, Integer.TYPE, java.lang.Long::class.java, java.lang.Long.TYPE, Integer.TYPE,
    ).apply { isAccessible = true }.newInstance(
        a.getBoolean(0), a.getInt(1), if (a.isNull(2)) null else a.getLong(2), a.getLong(3), a.getInt(4),
    )

    private fun probe(a: JSONArray): Any = probeClass.getDeclaredConstructor(
        java.lang.Long.TYPE, java.lang.Boolean.TYPE, java.lang.Boolean.TYPE, java.lang.Boolean.TYPE, java.lang.Long.TYPE,
    ).apply { isAccessible = true }.newInstance(a.getLong(0), a.getBoolean(1), a.getBoolean(2), a.getBoolean(3), a.getLong(4))

    private fun describe(s: Any): String {
        fun get(name: String) = stateClass.getDeclaredMethod(name).apply { isAccessible = true }.invoke(s)
        return listOf(get("getWasReady"), get("getRestartAttempts"), get("getUnresponsiveSinceMs"), get("getStartedAtMs"), get("getHealthyStreak"))
            .joinToString(",") { it?.toString() ?: "null" }
    }

    private fun describe(a: JSONArray): String =
        (0 until a.length()).joinToString(",") { if (a.isNull(it)) "null" else a.get(it).toString() }

    @Test
    fun legacyPolicyMatchesSharedVectors() {
        val doc = JSONObject(File(repo(), "tests/TestVectors/dsh-health-policy.json").readText())
        val p = doc.getJSONObject("params")
        val cases = doc.getJSONArray("cases")
        assertTrue(cases.length() >= 12)
        for (i in 0 until cases.length()) {
            val c = cases.getJSONObject(i)
            val d = decide.invoke(null, state(c.getJSONArray("state")), probe(c.getJSONArray("probe")),
                p.getLong("readyTimeoutMs"), p.getLong("unresponsiveGraceMs"), p.getInt("maxRestartAttempts"))!!
            assertEquals(c.getString("name"), c.getString("decision"), d.javaClass.simpleName)
            val next = d.javaClass.getMethod("getNext").apply { isAccessible = true }.invoke(d)!!
            assertEquals(c.getString("name"), describe(c.getJSONArray("next")), describe(next))
        }
    }
}
