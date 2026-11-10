package dshmobile.nativebridge

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test
import java.io.File

/**
 * The plugin spec / name reaches a guest `sh -c` line, so the allowlist plus per-token quoting is the injection
 * boundary. The shared vectors (tests/TestVectors/plugin-commands.json) are ALSO run by the C# DshMobile.Runtime
 * port, which keeps legacy and new implementation byte-identical until the Kotlin one is removed (PR-00/PR-05).
 */
class PluginSpecTest {
    private val spec = RuntimeController.PACKAGE_SPEC
    private val name = RuntimeController.PACKAGE_NAME

    private fun vectors(): JSONObject {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "global.json").isFile) dir = dir.parentFile
        checkNotNull(dir) { "repo root (global.json) not found above ${File("").absolutePath}" }
        return JSONObject(File(dir, "tests/TestVectors/plugin-commands.json").readText())
    }

    @Test
    fun acceptsRegistrySpecs() {
        for (ok in listOf("lodash", "@deepseek-ai/dsh-plugin-x", "pkg@1.2.3", "pkg@^1.2", "@scope/pkg@latest", "a.b_c-d", "pkg@>=1.0.0")) {
            assertTrue(ok, spec.matches(ok))
        }
    }

    @Test
    fun rejectsShellPathUrlAndGitSpecs() {
        for (bad in listOf(
            "", "pkg; rm -rf /", "pkg && id", "pkg|sh", "\$(id)", "`id`", "pkg x", "pkg\nid",
            "../evil", "/abs/path", "file:../x", "git+https://x/y.git", "https://x/y.tgz", "github:a/b",
            "Pkg", "@Scope/pkg", "pkg@1.0 ; id", "-rf", "--registry=http://evil",
        )) {
            assertFalse(bad, spec.matches(bad))
        }
    }

    @Test
    fun removeTakesNameOnly() {
        assertTrue(name.matches("@scope/pkg"))
        assertFalse(name.matches("pkg@1.0.0"))
        assertFalse(name.matches("pkg --global"))
    }

    @Test
    fun sharedQuotingVectors() {
        val arr = vectors().getJSONArray("quoting")
        assertTrue(arr.length() > 0)
        for (i in 0 until arr.length()) {
            val v = arr.getJSONObject(i)
            assertEquals(v.getString("arg"), v.getString("quoted"), RuntimeController.shellQuote(v.getString("arg")))
        }
    }

    @Test
    fun sharedPluginCommandVectors() {
        val arr = vectors().getJSONArray("commands")
        assertTrue(arr.length() > 0)
        for (i in 0 until arr.length()) {
            val v = arr.getJSONObject(i)
            val operand = if (v.isNull("operand")) null else v.getString("operand")
            val label = "${v.getString("verb")} ${v.getString("profile")} $operand"
            if (v.getBoolean("valid")) {
                val argv = RuntimeController.pluginCommand(v.getString("verb"), v.getString("profile"), operand)
                val expected = v.getJSONArray("argv").let { a -> List(a.length()) { a.getString(it) } }
                assertEquals(label, expected, argv)
                assertEquals(label, v.getString("line"), RuntimeController.shellJoin(argv))
            } else {
                try {
                    RuntimeController.pluginCommand(v.getString("verb"), v.getString("profile"), operand)
                    fail("expected BAD_REQUEST for $label")
                } catch (e: BridgeException) {
                    assertEquals(label, "BAD_REQUEST", e.code)
                }
            }
        }
    }

    /** The regression PR-00 fixes: an unquoted `>=` range would have made sh redirect into a file named `=1.0.0`. */
    @Test
    fun rangeSpecIsNeverAShellRedirect() {
        val line = RuntimeController.shellJoin(RuntimeController.pluginCommand("add", "web", "pkg@>=1.0.0"))
        assertTrue(line, line.endsWith("'pkg@>=1.0.0'"))
        assertFalse(line, Regex("(^|\\s)[^'\\s]*>").containsMatchIn(line))
    }
}
