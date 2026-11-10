package dshmobile.nativebridge

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class UiToolsRegistrationTest {
    @get:Rule
    val tmp = TemporaryFolder()

    @Test
    fun rendersOneInsertEntryForTheMcpClient() {
        val yaml = UiToolsRegistration.render()
        // A single top-level sequence item: a second top-level node would make DSH drop the whole layer.
        assertEquals(1, yaml.lines().count { it.startsWith("- ") })
        assertTrue(yaml.contains("- insert:\n    - id: dshmobile-ui\n      name: '@deepseek-ai/dsh-mcp-client'\n"))
        assertTrue(yaml.contains("        serverName: ui\n"))
        assertTrue(yaml.contains("        transport: stdio\n"))
        assertTrue(yaml.contains("        command: !!js process.execPath\n"))
        assertTrue(yaml.contains("        args: ['/opt/dshbox-cache/dshmobile/ui-facade.mjs']\n"))
        assertTrue(yaml.contains("        toolCallTimeoutMs: 120000\n"))
        assertFalse("never a literal newline escape", yaml.contains("\\n"))
    }

    @Test
    fun registerThenUnregisterRemovesTheLayer() {
        val userData = tmp.newFolder("user-data")
        assertFalse(UiToolsRegistration.isRegistered(userData))
        assertTrue(UiToolsRegistration.register(userData))
        val file = UiToolsRegistration.overlayFile(userData)
        assertTrue(file.isFile)
        assertTrue(file.path.replace('\\', '/').endsWith(".dsh/dshbox/dsh-official-plugin/dsh-official-plugin-overlay.yml"))
        assertTrue(UiToolsRegistration.isRegistered(userData))
        UiToolsRegistration.register(userData) // idempotent
        assertEquals(UiToolsRegistration.render(), file.readText())
        assertTrue(UiToolsRegistration.unregister(userData))
        assertFalse(file.exists())
        assertTrue("nothing to remove is fine", UiToolsRegistration.unregister(userData))
    }

    @Test
    fun neverDeletesALayerItDidNotWrite() {
        val userData = tmp.newFolder("user-data")
        val file = UiToolsRegistration.overlayFile(userData)
        file.parentFile!!.mkdirs()
        file.writeText("- id: some-entry\n  disabled: false\n")
        assertFalse(UiToolsRegistration.isRegistered(userData))
        assertTrue(UiToolsRegistration.hasConflict(userData))
        assertFalse(UiToolsRegistration.register(userData))
        assertFalse(UiToolsRegistration.unregister(userData))
        assertEquals("- id: some-entry\n  disabled: false\n", file.readText())
    }
}
