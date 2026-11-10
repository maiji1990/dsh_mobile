package dshmobile.nativebridge

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** The plugin spec / name is interpolated into a fixed guest command, so the allowlist is the injection boundary. */
class PluginSpecTest {
    private val spec = RuntimeController.PACKAGE_SPEC
    private val name = RuntimeController.PACKAGE_NAME

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
}
