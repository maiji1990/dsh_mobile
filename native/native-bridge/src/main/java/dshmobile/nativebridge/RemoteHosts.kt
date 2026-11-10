package dshmobile.nativebridge

import android.content.Context
import android.util.Log
import com.jcraft.jsch.ChannelExec
import com.jcraft.jsch.HostKey
import com.jcraft.jsch.HostKeyRepository
import com.jcraft.jsch.JSch
import com.jcraft.jsch.JSchException
import com.jcraft.jsch.Session
import com.jcraft.jsch.UserInfo
import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.net.HttpURLConnection
import java.net.URL
import java.util.UUID
import java.util.concurrent.TimeUnit

/** One remote DSH host as the user configured it. Contains no secret. */
internal data class RemoteHostProfile(
    val id: String,
    val label: String,
    val host: String,
    val port: Int,
    val user: String,
    val dshPort: Int,
    val pinnedFingerprint: String?,
) {
    fun toJson(): JSONObject = JSONObject()
        .put("id", id).put("label", label).put("host", host).put("port", port).put("user", user)
        .put("dshPort", dshPort).put("pinnedFingerprint", pinnedFingerprint ?: JSONObject.NULL)

    companion object {
        private val HOST = Regex("^(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,62})(?:\\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,62}))*|[0-9a-fA-F:.]{2,45})$")
        private val USER = Regex("^[A-Za-z0-9_][A-Za-z0-9._-]{0,63}$")
        private val FINGERPRINT = Regex("^SHA256:[A-Za-z0-9+/]{43}$")

        /**
         * Validates caller input. Host/user are interpolated into nothing (JSch takes them as values), but they are
         * still restricted so a profile can never carry shell or option syntax.
         */
        fun fromRequest(p: JSONObject, existing: RemoteHostProfile?): RemoteHostProfile {
            val host = p.optString("host").trim()
            val user = p.optString("user").trim()
            val port = p.optInt("port", 22)
            val dshPort = p.optInt("dshPort", 3091)
            val label = p.optString("label").trim().ifEmpty { "$user@$host" }
            if (!HOST.matches(host)) throw BridgeException(RuntimeController.BAD_REQUEST, "host must be a host name or IP address")
            if (!USER.matches(user)) throw BridgeException(RuntimeController.BAD_REQUEST, "user may contain letters, digits, . _ - (max 64)")
            if (port !in 1..65535) throw BridgeException(RuntimeController.BAD_REQUEST, "port must be 1..65535")
            if (dshPort !in 1024..65535) throw BridgeException(RuntimeController.BAD_REQUEST, "dshPort must be 1024..65535")
            if (label.length > 64 || label.any { it.isISOControl() }) throw BridgeException(RuntimeController.BAD_REQUEST, "label must be at most 64 printable characters")
            // Changing where the profile points invalidates the pin: a new address must be confirmed again.
            val samePeer = existing != null && existing.host == host && existing.port == port
            return RemoteHostProfile(
                id = existing?.id ?: UUID.randomUUID().toString(),
                label = label, host = host, port = port, user = user, dshPort = dshPort,
                pinnedFingerprint = if (samePeer) existing?.pinnedFingerprint else null,
            )
        }

        fun fromJson(o: JSONObject): RemoteHostProfile = RemoteHostProfile(
            id = o.getString("id"), label = o.getString("label"), host = o.getString("host"), port = o.getInt("port"),
            user = o.getString("user"), dshPort = o.getInt("dshPort"),
            pinnedFingerprint = o.optString("pinnedFingerprint").takeIf { FINGERPRINT.matches(it) },
        )

        fun isFingerprint(s: String) = FINGERPRINT.matches(s)
    }
}

/**
 * Host-key policy (ADR-0006): only the fingerprint the user confirmed is accepted. Unknown keys are recorded for the
 * UI and rejected (no session is ever opened on an unconfirmed key); a different key is CHANGED and rejected.
 */
internal class PinnedHostKeys(private val pinned: String?) : HostKeyRepository {
    @Volatile var seenFingerprint: String? = null
        private set
    @Volatile var seenType: String? = null
        private set

    override fun check(host: String?, key: ByteArray?): Int {
        val k = key ?: return HostKeyRepository.NOT_INCLUDED
        val fp = SshWire.fingerprint(k)
        seenFingerprint = fp
        seenType = runCatching { String(k, 4, readUint32(k, 0)) }.getOrNull()
        return when {
            pinned == null -> HostKeyRepository.NOT_INCLUDED
            pinned == fp -> HostKeyRepository.OK
            else -> HostKeyRepository.CHANGED
        }
    }

    override fun add(hostkey: HostKey?, ui: UserInfo?) = Unit // pins are only ever set by the user, never here
    override fun remove(host: String?, type: String?) = Unit
    override fun remove(host: String?, type: String?, key: ByteArray?) = Unit
    override fun getKnownHostsRepositoryID(): String = "dshmobile-pinned"
    override fun getHostKey(): Array<HostKey> = emptyArray()
    override fun getHostKey(host: String?, type: String?): Array<HostKey> = emptyArray()

    private fun readUint32(b: ByteArray, at: Int): Int =
        ((b[at].toInt() and 0xff) shl 24) or ((b[at + 1].toInt() and 0xff) shl 16) or
            ((b[at + 2].toInt() and 0xff) shl 8) or (b[at + 3].toInt() and 0xff)
}

/**
 * Remote DSH host over SSH (TASK-012, ADR-0006). One connection at a time; every method is called under the
 * RuntimeController op lock or from IO. The launch token of the remote `dsh web` lives only in [launchToken]
 * (never in JSON, never logged).
 */
internal class RemoteHosts(context: Context) {
    private val prefs = context.applicationContext.getSharedPreferences("dshmobile_remote_hosts", Context.MODE_PRIVATE)

    @Volatile private var session: Session? = null
    @Volatile private var current: RemoteHostProfile? = null
    @Volatile private var dshChannel: ChannelExec? = null
    @Volatile private var launchToken: String? = null
    @Volatile private var localPort: Int? = null
    @Volatile private var state = "IDLE"
    @Volatile private var code = "OK"
    @Volatile private var message: String? = null
    @Volatile private var probe: JSONObject? = null
    @Volatile private var lastUnknownFingerprint: Pair<String, String>? = null // hostId -> fingerprint

    // ------------------------------------------------------------------ profiles

    fun list(): List<RemoteHostProfile> = runCatching {
        val arr = JSONArray(prefs.getString(KEY_HOSTS, "[]"))
        (0 until arr.length()).map { RemoteHostProfile.fromJson(arr.getJSONObject(it)) }
    }.getOrDefault(emptyList())

    private fun store(all: List<RemoteHostProfile>) {
        prefs.edit().putString(KEY_HOSTS, JSONArray(all.map { it.toJson() }).toString()).apply()
    }

    fun save(p: JSONObject): RemoteHostProfile {
        val all = list()
        val id = requestedId(p)
        val existing = id?.let { wanted -> all.firstOrNull { it.id == wanted } }
        if (id != null && existing == null) throw BridgeException(RuntimeController.BAD_REQUEST, "unknown host id")
        val saved = RemoteHostProfile.fromRequest(p, existing)
        store(all.filterNot { it.id == saved.id } + saved)
        return saved
    }

    fun remove(id: String) {
        if (current?.id == id) disconnect("host removed")
        store(list().filterNot { it.id == id })
    }

    private fun profile(id: String): RemoteHostProfile =
        list().firstOrNull { it.id == id } ?: throw BridgeException(RuntimeController.BAD_REQUEST, "unknown host id")

    /**
     * Pins [fingerprint] for host [id], but only if it is exactly the key this app just saw for that host on an
     * unconfirmed connection attempt. A caller cannot pin an arbitrary value.
     */
    fun trust(id: String, fingerprint: String) {
        val seen = lastUnknownFingerprint
        if (!RemoteHostProfile.isFingerprint(fingerprint) || seen == null || seen.first != id || seen.second != fingerprint) {
            throw BridgeException(RuntimeController.BAD_REQUEST, "fingerprint does not match the key last presented by this host")
        }
        store(list().map { if (it.id == id) it.copy(pinnedFingerprint = fingerprint) else it })
        lastUnknownFingerprint = null
    }

    // ------------------------------------------------------------------ connection

    /** Connects and runs the capability probe. Never throws for SSH outcomes: they become [code] in the status. */
    fun connect(id: String): JSONObject {
        disconnect(null)
        val p = profile(id)
        current = p
        set("CONNECTING", "OK", null)
        SshKeys.ensure()
        val keys = PinnedHostKeys(p.pinnedFingerprint)
        val s = JSch().apply {
            hostKeyRepository = keys
            addIdentity(SshKeys.KeystoreIdentity(), null)
        }.getSession(p.user, p.host, p.port)
        s.setConfig("StrictHostKeyChecking", "yes")
        s.setConfig("PreferredAuthentications", "publickey")
        s.serverAliveInterval = 15_000
        s.serverAliveCountMax = 3
        try {
            s.connect(CONNECT_TIMEOUT_MS)
        } catch (e: JSchException) {
            val fp = keys.seenFingerprint
            when {
                p.pinnedFingerprint == null && fp != null -> {
                    lastUnknownFingerprint = p.id to fp
                    return fail("HOST_KEY_UNKNOWN", "confirm the host key fingerprint before connecting").put("fingerprint", fp)
                        .put("keyType", keys.seenType ?: JSONObject.NULL)
                }
                p.pinnedFingerprint != null && fp != null && fp != p.pinnedFingerprint ->
                    return fail("HOST_KEY_CHANGED", "host key differs from the pinned one; connection refused").put("fingerprint", fp)
                (e.message ?: "").contains("Auth fail", ignoreCase = true) ->
                    return fail("AUTH_FAILED", "public key not accepted; add this phone's key to authorized_keys")
                (e.message ?: "").contains("timeout", ignoreCase = true) -> return fail("TIMEOUT", e.message)
                else -> return fail("UNREACHABLE", e.message)
            }
        }
        session = s
        set("CONNECTED", "OK", null)
        probe = runProbe(s)
        if (probe?.optBoolean("dsh") != true) set("CONNECTED", "DSH_MISSING", "dsh not found on the remote PATH")
        return status()
    }

    /** `node --version` and `dsh --version` as separate exec channels: works under Windows cmd.exe and POSIX shells. */
    private fun runProbe(s: Session): JSONObject {
        val node = exec(s, "node --version", 20)
        val dsh = exec(s, "dsh --version", 60)
        return JSONObject()
            .put("node", node.first == 0)
            .put("nodeVersion", node.second.lineSequence().firstOrNull { it.isNotBlank() }?.trim()?.take(80) ?: JSONObject.NULL)
            .put("dsh", dsh.first == 0)
            .put("dshVersion", dsh.second.lineSequence().firstOrNull { it.isNotBlank() }?.trim()?.take(80) ?: JSONObject.NULL)
    }

    /** Runs one fixed command; returns exit status (null if unknown) and up to 8 KiB of combined output. */
    private fun exec(s: Session, command: String, timeoutSec: Long): Pair<Int?, String> {
        val ch = s.openChannel("exec") as ChannelExec
        ch.setCommand(command)
        ch.setInputStream(null)
        val err = ByteArrayOutputStream()
        ch.setErrStream(err)
        val out = ch.inputStream
        ch.connect(CONNECT_TIMEOUT_MS)
        val buf = ByteArrayOutputStream()
        val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(timeoutSec)
        val tmp = ByteArray(4096)
        while (System.nanoTime() < deadline) {
            while (out.available() > 0) {
                val n = out.read(tmp)
                if (n < 0) break
                if (buf.size() < 8192) buf.write(tmp, 0, n)
            }
            if (ch.isClosed && out.available() == 0) break
            Thread.sleep(50)
        }
        val exit = if (ch.isClosed) ch.exitStatus else null
        ch.disconnect()
        return exit to (buf.toString(Charsets.UTF_8.name()) + err.toString(Charsets.UTF_8.name())).take(8192)
    }

    /**
     * Starts `dsh --profile web --port <dshPort>` on the remote (loopback only, DSH's default), captures the launch
     * token from its `dsh web: …?token=…` line into memory, opens the local forward and checks that DSH answers 401
     * without the token (auth enforced).
     */
    fun startDsh(): JSONObject {
        val s = session?.takeIf { it.isConnected } ?: return fail("NOT_CONNECTED", "connect first")
        val p = current ?: return fail("NOT_CONNECTED", "connect first")
        stopDshChannel()
        set("STARTING", "OK", null)
        val ch = s.openChannel("exec") as ChannelExec
        ch.setCommand("dsh --profile web --port ${p.dshPort}")
        ch.setInputStream(null)
        ch.setPty(false)
        val out = ch.inputStream
        val err = ch.errStream
        ch.connect(CONNECT_TIMEOUT_MS)
        dshChannel = ch
        val tail = ArrayDeque<String>()
        val token = readTokenLine(listOf(out, err), tail, DSH_START_TIMEOUT_MS) { ch.isClosed }
        if (token == null) {
            stopDshChannel()
            return fail("DSH_START_FAILED", "remote dsh web printed no launch URL; tail: ${tail.joinToString(" | ").take(400)}")
        }
        launchToken = token
        val lport = try {
            s.setPortForwardingL("127.0.0.1", 0, "127.0.0.1", p.dshPort)
        } catch (e: JSchException) {
            stopDshChannel()
            return fail("FORWARD_FAILED", e.message)
        }
        localPort = lport
        val http = httpStatus(lport)
        set("READY", if (http == 401) "OK" else "DSH_START_FAILED", if (http == 401) null else "forwarded DSH answered HTTP $http instead of 401")
        return status().put("httpUnauthenticated", http ?: JSONObject.NULL)
    }

    /** Reads lines from the remote process until `dsh web: …token=…` appears. Only the token is kept. */
    private fun readTokenLine(streams: List<InputStream>, tail: ArrayDeque<String>, timeoutMs: Long, closed: () -> Boolean): String? {
        val deadline = System.currentTimeMillis() + timeoutMs
        val partial = streams.map { StringBuilder() }
        val tmp = ByteArray(2048)
        while (System.currentTimeMillis() < deadline) {
            var progressed = false
            streams.forEachIndexed { idx, stream ->
                while (stream.available() > 0) {
                    val n = stream.read(tmp)
                    if (n <= 0) break
                    progressed = true
                    partial[idx].append(String(tmp, 0, n, Charsets.UTF_8))
                    while (true) {
                        val nl = partial[idx].indexOf('\n')
                        if (nl < 0) break
                        val line = partial[idx].substring(0, nl).trimEnd('\r')
                        partial[idx].delete(0, nl + 1)
                        tokenFrom(line)?.let { return it }
                        tail.addLast(redact(line).take(160))
                        while (tail.size > 6) tail.removeFirst()
                    }
                }
            }
            if (!progressed && closed()) return null
            if (!progressed) Thread.sleep(100)
        }
        return null
    }

    private fun httpStatus(port: Int): Int? = runCatching {
        (URL("http://127.0.0.1:$port/").openConnection() as HttpURLConnection).run {
            connectTimeout = 5_000
            readTimeout = 10_000
            instanceFollowRedirects = false
            try { responseCode } finally { disconnect() }
        }
    }.onFailure { Log.w(TAG, "forwarded GET failed: ${it.javaClass.simpleName}") }.getOrNull()

    /**
     * Stops our remote `dsh web`: TERM signal (ignored by Windows sshd), then the channel is closed. Whether the remote
     * process really ended is checked through the forward and reported, never assumed (ADR-0006).
     */
    fun stopDsh(): JSONObject {
        val lport = localPort
        stopDshChannel()
        Thread.sleep(1500)
        val stillUp = lport?.let { port ->
            // Forward still exists until disconnect; a 401 here means the remote DSH outlived its channel. With the
            // session up, a failed request means nothing answers at the remote port any more (device: Windows
            // OpenSSH ends `dsh` when the channel closes). Without a session we cannot tell: unknown.
            val http = httpStatus(port)
            when {
                http != null -> http in 200..499
                session?.isConnected == true -> false
                else -> null
            }
        }
        session?.let { s -> lport?.let { runCatching { s.delPortForwardingL("127.0.0.1", it) } } }
        localPort = null
        set(if (session?.isConnected == true) "CONNECTED" else "DISCONNECTED", "OK", null)
        return status().put("remoteDshStillRunning", stillUp ?: JSONObject.NULL)
    }

    private fun stopDshChannel() {
        dshChannel?.let { ch ->
            runCatching { ch.sendSignal("TERM") }
            runCatching { ch.disconnect() }
        }
        dshChannel = null
        launchToken = null
    }

    fun disconnect(reason: String?) {
        stopDshChannel()
        runCatching { session?.disconnect() }
        session = null
        localPort = null
        probe = null
        if (reason != null || current != null) set("DISCONNECTED", "OK", reason)
        current = null
    }

    /** Called by status(): a dropped session (keepalive failed, network gone) is reported, and its token voided. */
    private fun refreshLiveness() {
        val s = session ?: return
        if (!s.isConnected) {
            session = null
            dshChannel = null
            launchToken = null
            localPort = null
            probe = null // describes a session that no longer exists
            set("DISCONNECTED", "UNREACHABLE", "connection lost; connect again")
        } else if (state == "READY" && dshChannel?.isClosed == true) {
            launchToken = null
            set("CONNECTED", "DSH_START_FAILED", "remote dsh web exited")
        }
    }

    fun status(): JSONObject {
        refreshLiveness()
        val p = current
        return JSONObject()
            .put("state", state)
            .put("code", code)
            .put("message", message ?: JSONObject.NULL)
            .put("hostId", p?.id ?: JSONObject.NULL)
            .put("label", p?.label ?: JSONObject.NULL)
            .put("probe", probe ?: JSONObject.NULL)
            .put("localPort", localPort ?: JSONObject.NULL)
            .put("dshPort", p?.dshPort ?: JSONObject.NULL)
            .put("hasLaunchToken", launchToken != null)
    }

    fun identity(): JSONObject {
        SshKeys.ensure()
        return JSONObject()
            .put("publicKey", SshKeys.openSshLine())
            .put("fingerprint", SshKeys.fingerprint())
            .put("algorithm", SshWire.ALG)
            .put("hardwareBacked", SshKeys.hardwareBacked() ?: JSONObject.NULL)
    }

    fun hostsJson(): JSONArray = JSONArray(list().map { it.toJson() })

    private fun set(state: String, code: String, message: String?) {
        this.state = state
        this.code = code
        this.message = message?.take(500)
    }

    /** Records a failed outcome. Connection-level failures drop the session; DSH start/forward failures keep it. */
    private fun fail(code: String, message: String?): JSONObject {
        if (code != "DSH_START_FAILED" && code != "FORWARD_FAILED") {
            runCatching { session?.disconnect() }
            session = null
        }
        set(if (session?.isConnected == true) "CONNECTED" else "DISCONNECTED", code, message)
        return status()
    }

    companion object {
        private const val TAG = "DshRemote"
        private const val KEY_HOSTS = "hosts"
        private const val CONNECT_TIMEOUT_MS = 15_000
        private const val DSH_START_TIMEOUT_MS = 120_000L

        /** `dsh web: http://host:port/?token=<value>` → value (same line format DSHBox's sandbox-manager parses). */
        fun tokenFrom(line: String): String? {
            val at = line.indexOf("dsh web:")
            if (at < 0) return null
            val t = line.indexOf("token=", at)
            if (t < 0) return null
            val start = t + "token=".length
            var end = start
            while (end < line.length && !line[end].isWhitespace() && line[end] != '&' && line[end] != '#') end++
            return line.substring(start, end).takeIf { it.isNotEmpty() }
        }

        /**
         * The host id of a save request, or null for a new host. JSON `null` must count as absent: org.json's
         * `optString` turns it into the string "null" (seen on device: "unknown host id" when adding a host).
         */
        fun requestedId(p: JSONObject): String? =
            if (!p.has("id") || p.isNull("id")) null else p.optString("id").takeIf { it.isNotBlank() }

        /** Remote output kept for error messages never carries a token. */
        fun redact(line: String): String = line.replace(Regex("token=[^\\s&#]+"), "token=***")
    }
}
