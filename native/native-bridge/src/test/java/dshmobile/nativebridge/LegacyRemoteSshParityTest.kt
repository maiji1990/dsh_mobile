package dshmobile.nativebridge

import com.jcraft.jsch.HostKeyRepository
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.math.BigInteger
import java.util.Base64

/**
 * tests/TestVectors/remote-ssh.json (PR-09 first segment) against the LEGACY RemoteHostProfile / PinnedHostKeys / SshWire
 * / RemoteHosts companion; the C# port (RemoteSshParityTests) runs the same file. `csharpOnly` cases depend on Android
 * libcore org.json and are skipped here (JVM unit tests use the real org.json). `trust` is C# only: the legacy rule is
 * inline in RemoteHosts.trust (covered by a C# source contract).
 */
class LegacyRemoteSshParityTest {
    private val v: JSONObject by lazy {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "global.json").isFile) dir = dir.parentFile
        JSONObject(File(checkNotNull(dir), "tests/TestVectors/remote-ssh.json").readText())
    }

    private fun cases(section: String, atLeast: Int): List<JSONObject> {
        val arr = v.getJSONArray(section)
        val all = (0 until arr.length()).map { arr.getJSONObject(it) }
        assertTrue("$section: ${all.size} cases", all.size >= atLeast)
        return all.filterNot { it.optBoolean("csharpOnly") }
    }

    private fun hex(s: String): ByteArray = ByteArray(s.length / 2) { s.substring(2 * it, 2 * it + 2).toInt(16).toByte() }
    private fun ByteArray.hex(): String = joinToString("") { "%02x".format(it.toInt() and 0xff) }
    private fun b64(s: String): ByteArray = Base64.getDecoder().decode(s)
    private fun JSONObject.str(name: String): String? = if (isNull(name)) null else getString(name)

    @Test
    fun publicBlobLineAndFingerprint() {
        for (c in cases("publicBlob", 5)) {
            val x = BigInteger(1, hex(c.getString("x")))
            val y = BigInteger(1, hex(c.getString("y")))
            if (c.optBoolean("error")) {
                assertThrows(c.getString("name"), IllegalArgumentException::class.java) { SshWire.ecdsaPublicBlob(x, y) }
                continue
            }
            val blob = SshWire.ecdsaPublicBlob(x, y)
            assertEquals(c.getString("name"), c.getString("blob"), Base64.getEncoder().encodeToString(blob))
            assertEquals(c.getString("fingerprint"), SshWire.fingerprint(blob))
            assertEquals(c.getString("line"), SshWire.openSshLine(blob, "dsh-mobile"))
        }
    }

    @Test
    fun fingerprints() {
        for (c in cases("fingerprint", 3)) assertEquals(c.getString("fingerprint"), SshWire.fingerprint(b64(c.getString("blob"))))
    }

    @Test
    fun derToSsh() {
        for (c in cases("derToSsh", 15)) {
            val der = hex(c.getString("der"))
            if (c.optBoolean("error")) {
                assertThrows(c.getString("name"), Exception::class.java) { SshWire.derToSshSignature(der) }
                continue
            }
            assertEquals(c.getString("name"), c.getString("ssh"), SshWire.derToSshSignature(der).hex())
        }
    }

    private fun profile(o: JSONObject) = RemoteHostProfile(
        o.getString("id"), o.getString("label"), o.getString("host"), o.getInt("port"), o.getString("user"), o.getInt("dshPort"),
        o.str("pinnedFingerprint"),
    )

    @Test
    fun profileRequests() {
        for (c in cases("profileRequest", 50)) {
            val name = c.getString("name")
            val existing = if (c.isNull("existing")) null else profile(c.getJSONObject("existing"))
            val request = c.getJSONObject("request")
            if (c.has("error")) {
                val e = assertThrows(name, BridgeException::class.java) { RemoteHostProfile.fromRequest(request, existing) }
                assertEquals(name, RuntimeController.BAD_REQUEST, e.code)
                assertEquals(name, c.getString("error"), e.message)
                continue
            }
            val want = profile(c.getJSONObject("profile"))
            val got = RemoteHostProfile.fromRequest(request, existing)
            if (want.id == "<new>") {
                assertTrue(name, got.id.isNotBlank())
                assertNotEquals(name, "<new>", got.id)
                assertEquals(name, want, got.copy(id = "<new>"))
            } else {
                assertEquals(name, want, got)
            }
        }
    }

    @Test
    fun requestedIds() {
        for (c in cases("requestedId", 8)) assertEquals(c.toString(), c.str("id"), RemoteHosts.requestedId(c.getJSONObject("request")))
    }

    /** Same expression as RemoteHosts.list() (which needs SharedPreferences); a C# source contract pins that body. */
    private fun parseStored(stored: String?): List<RemoteHostProfile> = runCatching {
        val arr = JSONArray(stored ?: "[]")
        (0 until arr.length()).map { RemoteHostProfile.fromJson(arr.getJSONObject(it)) }
    }.getOrDefault(emptyList())

    @Test
    fun storedLists() {
        for (c in cases("stored", 10)) {
            val arr = c.getJSONArray("profiles")
            val want = (0 until arr.length()).map { profile(arr.getJSONObject(it)) }
            assertEquals(c.getString("name"), want, parseStored(c.str("stored")))
        }
    }

    @Test
    fun hostKeys() {
        val names = mapOf("Ok" to HostKeyRepository.OK, "NotIncluded" to HostKeyRepository.NOT_INCLUDED, "Changed" to HostKeyRepository.CHANGED)
        for (c in cases("hostKey", 8)) {
            val name = c.getString("name")
            val keys = PinnedHostKeys(c.str("pinned"))
            val key = c.str("key")?.let { b64(it) }
            assertEquals(name, names.getValue(c.getString("decision")), keys.check("[127.0.0.1]:2222", key))
            assertEquals(name, c.str("seenFingerprint"), keys.seenFingerprint)
            assertEquals(name, c.str("seenType"), keys.seenType)
        }
    }

    // ---------------------------------------------------------- PR-09 second segment (ADR-0012)

    /** The legacy connect never sets server_host_key, so JSch's default decides which host key a server presents. */
    @Test
    fun hostKeyAlgorithmsAreJschDefault() {
        val arr = v.getJSONArray("hostKeyAlgorithms")
        val want = (0 until arr.length()).map { arr.getString(it) }
        assertEquals(12, want.size)
        assertEquals(want.joinToString(","), com.jcraft.jsch.JSch.getConfig("server_host_key"))
    }

    /**
     * Same expressions as the catch block of RemoteHosts.connect (it needs a JSch session); a C# source contract pins
     * that block. `kind` stands for the JSch message test the legacy code makes.
     */
    private fun classify(pinned: String?, fp: String?, message: String?): Pair<String, String?> = when {
        pinned == null && fp != null -> "HOST_KEY_UNKNOWN" to "confirm the host key fingerprint before connecting"
        pinned != null && fp != null && fp != pinned -> "HOST_KEY_CHANGED" to "host key differs from the pinned one; connection refused"
        (message ?: "").contains("Auth fail", ignoreCase = true) -> "AUTH_FAILED" to "public key not accepted; add this phone's key to authorized_keys"
        (message ?: "").contains("timeout", ignoreCase = true) -> "TIMEOUT" to message
        else -> "UNREACHABLE" to message
    }

    private fun kindOf(message: String?): String = when {
        (message ?: "").contains("Auth fail", ignoreCase = true) -> "AuthRejected"
        (message ?: "").contains("timeout", ignoreCase = true) -> "Timeout"
        else -> "Other"
    }

    @Test
    fun connectOutcomes() {
        for (c in cases("jschKind", 8)) assertEquals(c.toString(), c.getString("kind"), kindOf(c.str("message")))
        // A real JSch message of each kind. TIMEOUT / UNREACHABLE carry the library's own text, so only the code is compared.
        val jschText = mapOf("AuthRejected" to "Auth fail", "Timeout" to "timeout: socket is not established", "Other" to "java.net.ConnectException: Connection refused")
        for (c in cases("connectOutcome", 18)) {
            val (code, msg) = classify(c.str("pinned"), c.str("seen"), jschText.getValue(c.getString("kind")))
            assertEquals(c.getString("name"), c.getString("code"), code)
            if (code != "TIMEOUT" && code != "UNREACHABLE") assertEquals(c.getString("name"), c.str("message"), msg)
        }
    }

    /** Same expressions as RemoteHosts.runProbe / exec (they need a JSch channel); a C# source contract pins them. */
    @Test
    fun probeSummaries() {
        for (c in cases("probe", 15)) {
            val exit: Int? = if (c.isNull("exit")) null else c.getInt("exit")
            val output = (c.getString("stdout") + c.getString("stderr")).take(8192)
            val present = exit == 0
            val version = output.lineSequence().firstOrNull { it.isNotBlank() }?.trim()?.take(80)
            assertEquals(c.getString("name"), c.getBoolean("present"), present)
            assertEquals(c.getString("name"), c.str("version"), version)
        }
    }

    // ---------------------------------------------------------- PR-09 third segment (ADR-0013)

    /**
     * The line handling of RemoteHosts.readTokenLine (it reads JSch channel streams) on the vector's decoded chunks:
     * the same statements, with the real RemoteHosts.tokenFrom / redact; a C# source contract pins them.
     */
    private fun readLaunch(chunks: List<Pair<Int, String>>): Pair<String?, String> {
        val partial = (0..1).map { StringBuilder() }
        val tail = ArrayDeque<String>()
        for ((idx, text) in chunks) {
            partial[idx].append(text)
            while (true) {
                val nl = partial[idx].indexOf('\n')
                if (nl < 0) break
                val line = partial[idx].substring(0, nl).trimEnd('\r')
                partial[idx].delete(0, nl + 1)
                RemoteHosts.tokenFrom(line)?.let { return it to "" }
                tail.addLast(RemoteHosts.redact(line).take(160))
                while (tail.size > 6) tail.removeFirst()
            }
        }
        return null to "remote dsh web printed no launch URL; tail: ${tail.joinToString(" | ").take(400)}"
    }

    @Test
    fun launchOutput() {
        for (c in cases("launchOutput", 10)) {
            val arr = c.getJSONArray("chunks")
            val chunks = (0 until arr.length()).map { arr.getJSONObject(it).let { o -> o.getInt("stream") to o.getString("text") } }
            val (token, failure) = readLaunch(chunks)
            assertEquals(c.getString("name"), c.str("token"), token)
            if (token == null) assertEquals(c.getString("name"), c.str("failure"), failure)
        }
    }

    /** Same expressions as the end of RemoteHosts.startDsh and the stillUp block of stopDsh. */
    @Test
    fun startAndStopVerdicts() {
        for (c in cases("afterStart", 6)) {
            val http: Int? = if (c.isNull("http")) null else c.getInt("http")
            assertEquals(c.toString(), c.getString("code"), if (http == 401) "OK" else "DSH_START_FAILED")
            assertEquals(c.toString(), c.str("message"), if (http == 401) null else "forwarded DSH answered HTTP $http instead of 401")
        }
        for (c in cases("stillRunning", 28)) {
            val lport: Int? = if (c.getBoolean("hadForward")) 1 else null
            val http: Int? = if (c.isNull("http")) null else c.getInt("http")
            val connected = c.getBoolean("sessionConnected")
            val stillUp = lport?.let {
                when {
                    http != null -> http in 200..499
                    connected -> false
                    else -> null
                }
            }
            val want: Boolean? = if (c.isNull("stillRunning")) null else c.getBoolean("stillRunning")
            assertEquals(c.toString(), want, stillUp)
        }
    }

    @Test
    fun launchTokensAndRedaction() {
        for (c in cases("launchToken", 10)) assertEquals(c.getString("line"), c.str("token"), RemoteHosts.tokenFrom(c.getString("line")))
        for (c in cases("redact", 6)) assertEquals(c.getString("out"), RemoteHosts.redact(c.getString("in")))
    }
}
