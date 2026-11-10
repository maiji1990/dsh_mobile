package dshmobile.nativebridge

import com.jcraft.jsch.HostKeyRepository
import org.json.JSONObject
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.math.BigInteger
import java.security.KeyPairGenerator
import java.security.Signature
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec
import java.util.Base64

class SshWireTest {
    // Generated with OpenSSH `ssh-keygen -t ecdsa -b 256`; fingerprint from `ssh-keygen -lf`.
    private val vectorBlob = Base64.getDecoder().decode(
        "AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBJvX3yMyoKRRfc5yC0oD+7pgEvqjrZVnE63xChjDsLHuR/bvlBGEFJBkj/QEX4dhMbBwZkomegmIGYl3quVl+1I=",
    )

    @Test
    fun fingerprintMatchesOpenSsh() {
        assertEquals("SHA256:/O0DopfcupCOPu5e6/7zGNQITxFKE51poNy7jxKvT5Y", SshWire.fingerprint(vectorBlob))
    }

    @Test
    fun publicBlobMatchesOpenSshEncoding() {
        // Point is the last 65 bytes of the blob: 0x04 || X || Y.
        val point = vectorBlob.copyOfRange(vectorBlob.size - 65, vectorBlob.size)
        val x = BigInteger(1, point.copyOfRange(1, 33))
        val y = BigInteger(1, point.copyOfRange(33, 65))
        assertArrayEquals(vectorBlob, SshWire.ecdsaPublicBlob(x, y))
        assertTrue(SshWire.openSshLine(vectorBlob, "dsh-mobile").startsWith("ecdsa-sha2-nistp256 AAAAE2Vj"))
    }

    @Test
    fun derSignatureConvertsToSshBlobAndStillVerifies() {
        val gen = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }
        repeat(20) {
            val kp = gen.generateKeyPair()
            val data = "session-id-$it".toByteArray()
            val der = Signature.getInstance("SHA256withECDSA").run { initSign(kp.private); update(data); sign() }
            val ssh = SshWire.derToSshSignature(der)
            // string("ecdsa-sha2-nistp256") || string(mpint r || mpint s)
            val algLen = readU32(ssh, 0)
            assertEquals("ecdsa-sha2-nistp256", String(ssh, 4, algLen))
            var at = 4 + algLen
            val innerLen = readU32(ssh, at); at += 4
            assertEquals(ssh.size, at + innerLen)
            val rLen = readU32(ssh, at); at += 4
            val r = BigInteger(ssh.copyOfRange(at, at + rLen)); at += rLen
            val sLen = readU32(ssh, at); at += 4
            val s = BigInteger(ssh.copyOfRange(at, at + sLen))
            assertTrue("mpints are non-negative", r.signum() > 0 && s.signum() > 0)
            assertEquals(SshWire.parseDerSignature(der), r to s)
            // Re-encode as DER and verify with the JDK: proves no bits were lost.
            val reDer = derOf(r, s)
            val ok = Signature.getInstance("SHA256withECDSA").run { initVerify(kp.public); update(data); verify(reDer) }
            assertTrue(ok)
            val pub = kp.public as ECPublicKey
            assertEquals(4 + 19 + 4 + 8 + 4 + 65, SshWire.ecdsaPublicBlob(pub.w.affineX, pub.w.affineY).size)
        }
    }

    private fun readU32(b: ByteArray, at: Int) =
        ((b[at].toInt() and 0xff) shl 24) or ((b[at + 1].toInt() and 0xff) shl 16) or ((b[at + 2].toInt() and 0xff) shl 8) or (b[at + 3].toInt() and 0xff)

    private fun derOf(r: BigInteger, s: BigInteger): ByteArray {
        fun int(v: BigInteger) = v.toByteArray().let { byteArrayOf(0x02, it.size.toByte()) + it }
        val body = int(r) + int(s)
        return byteArrayOf(0x30, body.size.toByte()) + body
    }
}

class RemoteHostsTest {
    private fun req(vararg kv: Pair<String, Any>) = JSONObject().apply { kv.forEach { (k, v) -> put(k, v) } }

    @Test
    fun validProfileGetsAnIdAndNoPin() {
        val p = RemoteHostProfile.fromRequest(req("host" to "127.0.0.1", "port" to 2222, "user" to "wangbc"), null)
        assertTrue(p.id.isNotEmpty())
        assertEquals(3091, p.dshPort)
        assertEquals("wangbc@127.0.0.1", p.label)
        assertNull(p.pinnedFingerprint)
    }

    @Test
    fun rejectsShellOrOptionSyntax() {
        for (host in listOf("-oProxyCommand=x", "a b", "host;id", "", "h\$(x)")) {
            assertThrows(BridgeException::class.java) { RemoteHostProfile.fromRequest(req("host" to host, "user" to "u"), null) }
        }
        for (user in listOf("-l", "a b", "u;id", "", "x".repeat(65))) {
            assertThrows(BridgeException::class.java) { RemoteHostProfile.fromRequest(req("host" to "pc.lan", "user" to user), null) }
        }
        assertThrows(BridgeException::class.java) { RemoteHostProfile.fromRequest(req("host" to "pc", "user" to "u", "port" to 0), null) }
        assertThrows(BridgeException::class.java) { RemoteHostProfile.fromRequest(req("host" to "pc", "user" to "u", "dshPort" to 80), null) }
    }

    @Test
    fun changingTheAddressDropsThePin() {
        val pinned = RemoteHostProfile("id1", "pc", "pc.lan", 22, "u", 3091, "SHA256:/O0DopfcupCOPu5e6/7zGNQITxFKE51poNy7jxKvT5Y")
        assertEquals(pinned.pinnedFingerprint, RemoteHostProfile.fromRequest(req("host" to "pc.lan", "port" to 22, "user" to "other"), pinned).pinnedFingerprint)
        assertNull(RemoteHostProfile.fromRequest(req("host" to "pc2.lan", "port" to 22, "user" to "u"), pinned).pinnedFingerprint)
        assertNull(RemoteHostProfile.fromRequest(req("host" to "pc.lan", "port" to 2222, "user" to "u"), pinned).pinnedFingerprint)
    }

    @Test
    fun hostKeyPolicyAcceptsOnlyThePinnedKey() {
        val blob = Base64.getDecoder().decode(
            "AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBJvX3yMyoKRRfc5yC0oD+7pgEvqjrZVnE63xChjDsLHuR/bvlBGEFJBkj/QEX4dhMbBwZkomegmIGYl3quVl+1I=",
        )
        val fp = "SHA256:/O0DopfcupCOPu5e6/7zGNQITxFKE51poNy7jxKvT5Y"
        PinnedHostKeys(null).let {
            assertEquals(HostKeyRepository.NOT_INCLUDED, it.check("[127.0.0.1]:2222", blob))
            assertEquals(fp, it.seenFingerprint)
            assertEquals("ecdsa-sha2-nistp256", it.seenType)
        }
        assertEquals(HostKeyRepository.OK, PinnedHostKeys(fp).check("pc", blob))
        assertEquals(HostKeyRepository.CHANGED, PinnedHostKeys("SHA256:" + "A".repeat(43)).check("pc", blob))
    }

    @Test
    fun jsonNullIdMeansNewHost() {
        // What the C# client sends for a new host (System.Text.Json writes "id":null).
        assertNull(RemoteHosts.requestedId(JSONObject("""{"id":null,"host":"127.0.0.1","port":2222,"user":"wangbc"}""")))
        assertNull(RemoteHosts.requestedId(JSONObject("""{"host":"127.0.0.1"}""")))
        assertNull(RemoteHosts.requestedId(JSONObject("""{"id":"  "}""")))
        assertEquals("abc", RemoteHosts.requestedId(JSONObject("""{"id":"abc"}""")))
    }

    @Test
    fun launchTokenIsParsedAndNeverKeptInText() {
        assertEquals("abc123", RemoteHosts.tokenFrom("dsh web: http://127.0.0.1:3091/?token=abc123"))
        assertEquals("abc123", RemoteHosts.tokenFrom("[boot] dsh web: http://127.0.0.1:3091/?token=abc123&x=1 ready"))
        assertNull(RemoteHosts.tokenFrom("listening on http://127.0.0.1:3091/?token=abc123")) // not the dsh web line
        assertNull(RemoteHosts.tokenFrom("dsh web: http://127.0.0.1:3091/"))
        assertEquals("url ?token=*** end", RemoteHosts.redact("url ?token=secret end"))
    }
}
