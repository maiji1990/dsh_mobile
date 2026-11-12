package dshmobile.nativebridge

import com.dshbox.app.common.Constants
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

/**
 * PR-07: the LEGACY UiToolsRegistration over the shared cases in tests/TestVectors/ui-tools-registration.json.
 * The C# DshMobile.Runtime.Automation.UiToolsRegistration runs the same cases (UiToolsRegistrationParityTests).
 */
class LegacyUiToolsRegistrationParityTest {
    @get:Rule
    val tmp = TemporaryFolder()

    private fun repo(): File {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "global.json").isFile) dir = dir.parentFile
        return checkNotNull(dir) { "repo root (global.json) not found" }
    }

    private fun setup(i: Int, step: String, initial: String?): File {
        val userData = File(tmp.root, "case-$i-$step/user-data").apply { mkdirs() }
        val f = UiToolsRegistration.overlayFile(userData)
        when (initial) {
            null -> {}
            "@dir" -> f.mkdirs()
            else -> {
                f.parentFile!!.mkdirs()
                f.writeBytes((if (initial == "@render") UiToolsRegistration.render() else initial).toByteArray(Charsets.UTF_8))
            }
        }
        return userData
    }

    private fun assertAfter(name: String, userData: File, initial: String?, after: String?) {
        val f = UiToolsRegistration.overlayFile(userData)
        when (val expected = if (after == "@initial") initial else after) {
            null -> assertFalse("$name: expected absent", f.exists())
            "@dir" -> assertTrue("$name: expected the directory to stay", f.isDirectory)
            else -> {
                assertTrue("$name: expected a file", f.isFile)
                val want = if (expected == "@render") UiToolsRegistration.render() else expected
                assertEquals(name, want, String(f.readBytes(), Charsets.UTF_8))
            }
        }
        assertFalse("$name: temp file left behind", File(f.parentFile, f.name + ".tmp").exists())
    }

    @Test
    fun legacyRegistrationMatchesSharedVectors() {
        val v = JSONObject(File(repo(), "tests/TestVectors/ui-tools-registration.json").readText(Charsets.UTF_8))
        assertEquals(v.getString("render"), UiToolsRegistration.render())
        assertEquals(v.getString("overlayRelativePath"), Constants.DSH_OFFICIAL_PLUGIN_OVERLAY_RELATIVE_PATH)
        val cases = v.getJSONArray("cases")
        assertTrue(cases.length() >= 10)
        for (i in 0 until cases.length()) {
            val c = cases.getJSONObject(i)
            val name = c.getString("name")
            val initial = if (c.isNull("initial")) null else c.getString("initial")

            var userData = setup(i, "register", initial)
            assertEquals("$name isRegistered", c.getBoolean("isRegistered"), UiToolsRegistration.isRegistered(userData))
            assertEquals("$name hasConflict", c.getBoolean("hasConflict"), UiToolsRegistration.hasConflict(userData))
            val reg = c.getJSONObject("register")
            assertEquals("$name register", reg.getBoolean("result"), UiToolsRegistration.register(userData))
            assertAfter("$name / register", userData, initial, if (reg.isNull("after")) null else reg.getString("after"))

            userData = setup(i, "unregister", initial)
            val unreg = c.getJSONObject("unregister")
            assertEquals("$name unregister", unreg.getBoolean("result"), UiToolsRegistration.unregister(userData))
            assertAfter("$name / unregister", userData, initial, if (unreg.isNull("after")) null else unreg.getString("after"))
        }
    }
}
