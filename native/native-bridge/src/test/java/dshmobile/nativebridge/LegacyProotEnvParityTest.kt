package dshmobile.nativebridge

import com.dshbox.app.sandbox.DefaultSandboxManager
import com.dshbox.app.sandbox.SandboxConfig
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

/**
 * Computes the PRoot environment with the LEGACY DefaultSandboxManager.buildProotEnv (private: called through
 * reflection, test-only) for the shared cases in tests/TestVectors/proot-env.json. The C# ProotCommandBuilder runs
 * the same cases (ProotCommandParityTests).
 */
class LegacyProotEnvParityTest {
    @get:Rule
    val tmp = TemporaryFolder()

    private fun repo(): File {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "global.json").isFile) dir = dir.parentFile
        return checkNotNull(dir) { "repo root (global.json) not found" }
    }

    @Test
    fun legacyEnvMatchesSharedVectors() {
        val doc = JSONObject(File(repo(), "tests/TestVectors/proot-env.json").readText())
        val fragments = doc.getJSONObject("envFragments")
        val cases = doc.getJSONArray("cases")
        assertTrue(cases.length() >= 4)
        val method = DefaultSandboxManager::class.java.getDeclaredMethod("buildProotEnv", File::class.java, String::class.java)
            .apply { isAccessible = true }
        for (i in 0 until cases.length()) {
            val c = cases.getJSONObject(i)
            val files = File(tmp.root, "files-$i").canonicalFile
            val native = File(tmp.root, "native-$i").canonicalFile.apply { mkdirs() }
            val current = File(files, "runtime/runtime-current")
            for (key in fragments.keys()) File(current, key).apply { parentFile.mkdirs(); writeText(fragments.getString(key)) }
            if (c.getBoolean("withProfile")) File(current, "runtime-profile.json").writeText(doc.getString("profile"))
            val libs = c.getJSONArray("nativeLibs")
            for (j in 0 until libs.length()) File(native, libs.getString(j)).writeText("")
            val manager = DefaultSandboxManager(SandboxConfig(appFilesDir = files, nativeLibraryDir = native.path))
            @Suppress("UNCHECKED_CAST")
            val env = method.invoke(manager, current, c.getString("role")) as Map<String, String>
            fun norm(v: String) = v.replace(files.path, "@FILES@").replace(native.path, "@NATIVE@").replace('\\', '/')
            val expected = c.getJSONObject("env").let { o -> o.keys().asSequence().associateWith { o.getString(it) } }
            assertEquals(c.getString("name"), expected.toSortedMap(), env.mapValues { norm(it.value) }.toSortedMap())
        }
    }
}
