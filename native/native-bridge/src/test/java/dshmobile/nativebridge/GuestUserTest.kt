package dshmobile.nativebridge

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Test

class GuestUserTest {
    private val base = "root:x:0:0:root:/root:/bin/bash\ndaemon:x:1:1::/usr/sbin:/usr/sbin/nologin\n"

    @Test
    fun appendsEntryForAppUid() {
        val out = GuestUser.withEntry(base, 10267, 10267)
        assertEquals(base + "dshmobile:x:10267:10267::/root:/bin/bash\n", out)
    }

    @Test
    fun isIdempotent_andReplacesStaleUid() {
        val once = GuestUser.withEntry(base, 10267, 10267)
        assertEquals(once, GuestUser.withEntry(once, 10267, 10267))
        val moved = GuestUser.withEntry(once, 10999, 10999)
        assertFalse(moved.contains(":10267:"))
        assertEquals(1, moved.lines().count { it.startsWith("dshmobile:") })
    }

    @Test
    fun keepsOtherUsersUntouched() {
        val out = GuestUser.withEntry(base + "\n\nalice:x:1000:1000::/home/alice:/bin/sh", 5, 5)
        assertEquals(listOf("root", "daemon", "alice", "dshmobile"), out.lines().filter { it.isNotEmpty() }.map { it.substringBefore(':') })
    }
}
