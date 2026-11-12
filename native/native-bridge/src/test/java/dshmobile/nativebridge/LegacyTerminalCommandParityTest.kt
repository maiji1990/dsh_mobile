package dshmobile.nativebridge

import com.dshbox.terminal.TerminalCommandFactory
import com.dshbox.terminal.TerminalEnvFactory
import com.dshbox.terminal.TerminalPathsResolver
import com.dshbox.terminal.TerminalProcFake
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

/**
 * PR-06: builds the terminal login-shell argv + env exactly the way the LEGACY terminal does (TerminalController →
 * DshTerminalManager.newSession: TerminalPathsResolver.resolve, TerminalProcFake.ensureBindArgs,
 * TerminalCommandFactory.sandboxLoginShell(paths, null, procBinds), TerminalEnvFactory.sandboxEnv) for the shared
 * cases in tests/TestVectors/terminal-command.json. The C# TerminalCommandBuilder runs the same cases.
 */
class LegacyTerminalCommandParityTest {
    @get:Rule
    val tmp = TemporaryFolder()

    private fun repo(): File {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "global.json").isFile) dir = dir.parentFile
        return checkNotNull(dir) { "repo root (global.json) not found" }
    }

    private fun strings(a: JSONArray): List<String> = (0 until a.length()).map { a.getString(it) }

    @Test
    fun legacyTerminalCommandMatchesSharedVectors() {
        val cases = JSONObject(File(repo(), "tests/TestVectors/terminal-command.json").readText()).getJSONArray("cases")
        assertTrue(cases.length() >= 6)
        for (i in 0 until cases.length()) {
            val c = cases.getJSONObject(i)
            val name = c.getString("name")
            val files = File(tmp.root, "files-$i").canonicalFile.apply { mkdirs() }
            val native = File(tmp.root, "native-$i").canonicalFile.apply { mkdirs() }
            strings(c.getJSONArray("dirs")).forEach { File(files, it).mkdirs() }
            strings(c.getJSONArray("files")).forEach { File(files, it).apply { parentFile?.mkdirs(); writeText("") } }
            strings(c.getJSONArray("nativeLibs")).forEach { File(native, it).writeText("") }
            // Separators are normalized only in host-path values (PS1 contains real backslashes).
            fun norm(v: String): String {
                val r = v.replace(files.path, "@FILES@").replace(native.path, "@NATIVE@")
                return if (r == v) v else r.replace('\\', '/')
            }

            val paths = TerminalPathsResolver.resolve(files, native.path)
            if (c.isNull("argv")) {
                assertNull(name, paths)
                continue
            }
            checkNotNull(paths) { "$name: legacy resolve() returned null" }
            val procBinds = TerminalProcFake.ensureBindArgs(paths)
            val argv = TerminalCommandFactory.sandboxLoginShell(paths, null, procBinds)
            assertEquals("$name argv", strings(c.getJSONArray("argv")), argv.map(::norm))
            assertEquals("$name env", strings(c.getJSONArray("env")), TerminalEnvFactory.sandboxEnv(paths).map(::norm))
        }
    }
}
