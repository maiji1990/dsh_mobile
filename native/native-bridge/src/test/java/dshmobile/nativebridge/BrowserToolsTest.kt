package dshmobile.nativebridge

import org.junit.Assert.assertEquals
import org.junit.Test

class BrowserToolsTest {
    @Test
    fun installerCommandsMatchDshboxAndTakeNoCallerInput() {
        // Same command DSHBox's AdapterPluginController runs; fixed paths only (ADR-0005).
        assertEquals(
            "bash /root/projects/.dsh/dshbox/dshbox-plugins/browser-use/install.sh /root/projects/.dsh/profiles/web",
            BrowserTools.adapterCommand(install = true),
        )
        assertEquals(
            "bash /root/projects/.dsh/dshbox/dshbox-plugins/browser-use/uninstall.sh /root/projects/.dsh/profiles/web",
            BrowserTools.adapterCommand(install = false),
        )
    }

    @Test
    fun guestPathsMatchTheSandboxBinds() {
        assertEquals("/opt/browser", BrowserTools.GUEST_ENTRY)
        assertEquals("/root/projects/.dsh/dshbox/dshbox-plugins/browser-use", BrowserTools.STAGE_GUEST_DIR)
    }
}
