package dshmobile.nativebridge

import interlock.relay.core.log.RunLog
import interlock.relay.core.protocol.RelayError
import interlock.relay.core.protocol.RelayRequest
import interlock.relay.core.protocol.RelayResponse
import interlock.relay.core.spi.RelayPathPolicy
import interlock.relay.core.storage.QuotaLedger
import interlock.relay.core.storage.RelayPaths
import interlock.relay.core.storage.StorageReaper
import interlock.relay.core.transport.CancelOutcome
import interlock.relay.core.transport.DispatchGuardOutcome
import interlock.relay.core.transport.HandlerResult
import interlock.relay.core.transport.MailboxServer
import interlock.relay.core.transport.ParkedRequest
import interlock.relay.core.transport.RelayRequestHandler
import interlock.relay.core.transport.RequestPhase
import interlock.relay.core.transport.RequestState
import interlock.relay.core.transport.RequestStateStore
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import org.json.JSONArray
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.lang.reflect.Method
import java.nio.file.Files
import java.util.concurrent.ConcurrentHashMap

/**
 * PR-07 segment 7: the LEGACY mailbox loop and request state store (DSHBox interlock-relay-core MailboxServer,
 * RequestStateStore) over the shared cases in tests/TestVectors/relay-mailbox.json; the C# port runs the same cases
 * (RelayMailboxParityTests). Internal companion functions are reached by reflection (module-mangled JVM names).
 */
class LegacyRelayMailboxParityTest {
    private fun repo(): File {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "global.json").isFile) dir = dir.parentFile
        return checkNotNull(dir) { "repo root (global.json) not found" }
    }

    private val v by lazy { JSONObject(File(repo(), "tests/TestVectors/relay-mailbox.json").readText(Charsets.UTF_8)) }
    private val root: File = Files.createTempDirectory("relay-mailbox").toFile()

    @After
    fun cleanup() {
        root.deleteRecursively()
    }

    private fun objects(a: JSONArray) = (0 until a.length()).map { a.getJSONObject(it) }

    private fun strings(a: JSONArray) = (0 until a.length()).map { a.getString(it) }

    private fun companion(name: String, arity: Int): Method =
        MailboxServer.Companion::class.java.declaredMethods
            .first { (it.name == name || it.name.startsWith("$name$")) && it.parameterCount == arity }
            .apply { isAccessible = true }

    private fun call(name: String, vararg args: Any?): Any? = companion(name, args.size).invoke(MailboxServer.Companion, *args)

    /** Structural JSON equality (android.jar's org.json has no similar()): numbers by value, keys unordered. */
    private fun same(a: Any?, b: Any?): Boolean = when {
        a is JSONObject && b is JSONObject ->
            a.length() == b.length() && a.keys().asSequence().all { b.has(it) && same(a.get(it), b.get(it)) }
        a is JSONArray && b is JSONArray -> a.length() == b.length() && (0 until a.length()).all { same(a.get(it), b.get(it)) }
        a is Number && b is Number -> if (a is Double || a is Float || b is Double || b is Float) a.toDouble() == b.toDouble() else a.toLong() == b.toLong()
        else -> a == b
    }

    private fun optLong(o: JSONObject, n: String): Long? = if (o.has(n) && !o.isNull(n)) o.getLong(n) else null

    private fun state(o: Any?): RequestState? = (o as? JSONObject)?.let { s ->
        RequestState(
            id = s.getString("id"), digest = s.getString("digest"), capability = s.getString("capability"),
            phase = RequestPhase.valueOf(s.getString("phase")), createdAtMs = s.getLong("createdAtMs"),
            admittedAtMs = optLong(s, "admittedAtMs"), ttlMs = s.getLong("ttlMs"), terminalAtMs = optLong(s, "terminalAtMs"),
            resultJson = if (s.has("resultJson")) s.getString("resultJson") else null,
        )
    }

    @Test
    fun legacyPureFunctionsMatchSharedVectors() {
        for (c in objects(v.getJSONArray("orderPending"))) {
            val e = c.getJSONArray("entries")
            val entries = (0 until e.length()).map { i ->
                val p = e.getJSONArray(i)
                p.getString(0) to (if (p.isNull(1)) null else p.getLong(1))
            }
            assertEquals(c.toString(), strings(c.getJSONArray("ordered")), call("orderPending", entries))
        }
        for (c in objects(v.getJSONArray("evictionVictims"))) {
            assertEquals(strings(c.getJSONArray("victims")), call("evictionVictims", strings(c.getJSONArray("names")), c.getInt("capacity")))
        }
        val settle = v.getJSONArray("needsSettle")
        for (i in 0 until settle.length()) {
            val c = settle.getJSONArray(i)
            assertEquals(c.toString(), c.getBoolean(3), call("needsSettle", c.getLong(0), c.getLong(1), c.getBoolean(2)))
        }
        val parts = v.getJSONArray("incomingPart")
        for (i in 0 until parts.length()) {
            val c = parts.getJSONArray(i)
            assertEquals(c.toString(), c.getBoolean(1), call("isIncomingPartName", c.getString(0)))
        }
        for (c in objects(v.getJSONArray("claimBlocked"))) {
            assertEquals(c.toString(), c.getBoolean("blocked"),
                call("claimBlockedByInFlight", state(c.opt("state")), c.getLong("now"), c.getBoolean("processing")))
        }
        for (c in objects(v.getJSONArray("queueCounts"))) {
            fun map(n: String) = c.getJSONObject(n).let { o -> o.keys().asSequence().associateWith { o.getLong(it) } }
            val counts = call("queueCounts", strings(c.getJSONArray("inbox")), strings(c.getJSONArray("processing")),
                map("seen"), map("admitted"), c.getLong("now")) as MailboxServer.QueueCounts
            assertEquals(c.getInt("depth"), counts.depth)
            assertEquals(optLong(c, "oldest"), counts.oldestAgeMs)
        }
        for (c in objects(v.getJSONArray("terminalFrame"))) {
            val recorded = state(c.opt("state"))
            val payload = call("terminalFramePayload", c.getString("id"), recorded) as JSONObject
            // encodeControl: {"v":2,"id","op":"submit","ok":…,…payload}; ok = response ok && recorded
            val frame = JSONObject().put("v", 2).put("id", c.getString("id")).put("op", "submit")
                .put("ok", c.getBoolean("responseOk") && recorded != null)
            for (k in payload.keys()) frame.put(k, payload.get(k))
            assertTrue("$c\n$frame", same(c.getJSONObject("frame"), frame))
        }
    }

    @Test
    fun legacyStateStoreStepsMatchSharedVectors() {
        val s = v.getJSONObject("stateSteps")
        val wall = s.getLong("wallMs")
        var t = 0L
        val dir = File(root, "requests")
        val store = RequestStateStore(dir, now = { t }, wallNow = { wall })
        fun text(id: String) = File(dir, "$id.json").takeIf { it.isFile }?.readText(Charsets.UTF_8)
        objects(s.getJSONArray("steps")).forEachIndexed { i, step ->
            val where = "step $i $step"
            t = step.getLong("t")
            val id = step.optString("id")
            val outcome: String? = when (step.getString("op")) {
                "create" -> store.create(id, step.getString("digest"), step.getString("cap"), step.getLong("ttl")).javaClass.simpleName
                "admit" -> store.markAdmitted(id)?.phase?.name
                "waiting" -> store.markWaiting(id, step.getBoolean("relayUser"))?.phase?.name
                "cancel" -> when (val r = store.requestCancel(id)) {
                    is CancelOutcome.Requested -> "Requested:${r.provablyUndispatched}"
                    else -> r.javaClass.simpleName
                }
                "guard" -> when (val r = store.armExecDispatchGuard(id)) {
                    is DispatchGuardOutcome.NotExecutable -> "NotExecutable:${r.state?.phase?.name ?: "null"}"
                    else -> r.javaClass.simpleName
                }
                "terminal" -> store.recordTerminal(
                    id, RequestPhase.valueOf(step.getString("phase")),
                    if (step.isNull("result")) null else step.getString("result"),
                )?.phase?.name
                "purge" -> {
                    store.purgeExpired(step.getLong("now"))
                    null
                }
                "recover" -> {
                    val got = store.recoverUnfinished().sortedBy { it.id }.map { "${it.id}:${it.phase.name}" }
                    val want = step.getJSONArray("recovered").let { a -> (0 until a.length()).map { "${a.getJSONArray(it).getString(0)}:${a.getJSONArray(it).getString(1)}" } }
                    assertEquals(where, want, got)
                    null
                }
                else -> error(where)
            }
            if (step.has("outcome")) assertEquals(where, if (step.isNull("outcome")) null else step.getString("outcome"), outcome)
            if (step.has("record")) {
                if (step.isNull("record")) {
                    assertNull(where, text(id))
                } else {
                    assertTrue("$where got ${text(id)}", same(step.getJSONObject("record"), JSONObject(checkNotNull(text(id)) { where })))
                }
            }
            if (step.has("records")) {
                val records = step.getJSONObject("records")
                for (k in records.keys()) {
                    if (records.isNull(k)) {
                        assertNull("$where $k", text(k))
                    } else {
                        assertTrue("$where $k got ${text(k)}", same(records.getJSONObject(k), JSONObject(checkNotNull(text(k)) { "$where $k" })))
                    }
                }
            }
        }
    }

    // ---- end-to-end ----

    private class ScriptedFailure : RuntimeException()

    /** Drops every elapsedMs and reduces each error object to its code: the structure both sides compare. */
    private fun normalize(n: Any?): Any? = when (n) {
        is JSONObject -> JSONObject().also { copy ->
            for (k in n.keys()) {
                if (k == "elapsedMs") continue
                val value = n.get(k)
                copy.put(k, if (k == "error" && value is JSONObject) JSONObject().put("code", value.get("code")) else normalize(value))
            }
        }
        is JSONArray -> JSONArray().also { a -> for (i in 0 until n.length()) a.put(normalize(n.get(i))) }
        else -> n
    }

    private fun assertResponse(want: JSONObject, actualText: String, where: String) {
        val expected = JSONObject(want.toString())
        val actual = normalize(JSONObject(actualText)) as JSONObject
        val reason = expected.optString("reason", "")
        if (reason.startsWith("@")) {
            assertTrue("$where reason ${actual.optString("reason")}", actual.optString("reason").startsWith(reason.substring(1)))
            expected.remove("reason")
            actual.remove("reason")
        }
        assertTrue("$where: $actual", same(expected, actual))
    }

    private fun fileText(f: JSONObject): String = if (f.has("padId")) {
        "{\"v\":1,\"id\":\"${f.getString("padId")}\",\"capability\":\"ui.snapshot\",\"args\":{\"pad\":\"${"p".repeat(f.getInt("padChars"))}\"}}"
    } else {
        f.getString("text")
    }

    private fun ok(id: String, data: JSONObject) = RelayResponse(
        id = id, ok = true, data = data, artifacts = emptyList(), surface = null, degradedFrom = null, reason = null, error = null, elapsedMs = 0,
    )

    private fun failed(id: String, error: RelayError) = RelayResponse(
        id = id, ok = false, data = JSONObject(), artifacts = emptyList(), surface = null, degradedFrom = null, reason = null, error = error, elapsedMs = 0,
    )

    private val policy = object : RelayPathPolicy {
        override val guestEntry = "/opt/pilot"
        override val hostDirName = "pilot"
        override val cliName = "relay"
        override val artifactPrefix = "relay-"
        override val assetClient = "relay/relay-client.cjs"
        override val mediaAlbumDir = "DSH Mobile"
    }

    private fun scenario(name: String) {
        val s = objects(v.getJSONArray("scenarios")).single { it.getString("name") == name }
        val base = System.nanoTime()
        val now = { 1_000L + (System.nanoTime() - base) / 1_000_000L }
        val paths = RelayPaths(root, policy)
        paths.ensureWritableDirs()
        val runLog = RunLog(paths.logsDir)
        val reaper = StorageReaper(paths, QuotaLedger(paths.quotaFile), runLog, now = now, wallNow = { System.currentTimeMillis() })
        val store = RequestStateStore(paths.requestStateDir, now = now, runLog = runLog)
        val budgets = ConcurrentHashMap<String, Long>()
        lateinit var server: MailboxServer
        val handler = RelayRequestHandler { r: RelayRequest, budget: Long ->
            budgets[r.id] = budget
            when (r.capability) {
                "ui.node" -> throw ScriptedFailure()
                "ui.tap" -> {
                    delay(5_000)
                    HandlerResult.Done(ok(r.id, JSONObject().put("late", true)))
                }
                "screen.observe" -> HandlerResult.Done(ok(r.id, JSONObject().put("blob", "x".repeat(600_000))))
                "ui.click", "ui.longClick" -> {
                    val click = r.capability == "ui.click"
                    val park = ParkedRequest(
                        requestId = r.id,
                        resume = { if (click) ok(r.id, JSONObject().put("resumed", r.capability)) else failed(r.id, RelayError.GATE_CANCELLED) },
                        approvalDeadlineAtMs = if (click) now() + 60_000 else now() - 20_000,
                        executionDeadlineAtMs = now() + 15_000,
                        approvalOwner = "test",
                    )
                    assertTrue(server.register(park))
                    HandlerResult.Parked(park)
                }
                else -> HandlerResult.Done(ok(r.id, JSONObject().put("cap", r.capability).put("args", r.args)))
            }
        }
        server = MailboxServer(paths, reaper, runLog, handler, now = now, stateStore = store)
        fun ops(key: String) {
            val ops = s.optJSONArray(key) ?: return
            for (op in objects(ops)) {
                val id = op.getString("id")
                when (op.getString("op")) {
                    "create" -> store.create(id, "d1", op.getString("cap"), 30_000)
                    "guard" -> store.armExecDispatchGuard(id)
                    "admit" -> store.markAdmitted(id)
                    else -> error(op.toString())
                }
            }
        }
        val staging = File(root, "drop").apply { mkdirs() }
        fun drop() {
            for (f in objects(s.getJSONArray("files"))) {
                val temp = File(staging, f.getString("name"))
                temp.writeText(fileText(f), Charsets.UTF_8)
                assertTrue(temp.renameTo(File(paths.inboxDir, f.getString("name"))))
            }
        }
        ops("preStart")
        val preStartFiles = s.optBoolean("preStartFiles", false)
        if (preStartFiles) drop()
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        server.start(scope)
        try {
            assertTrue(server.isRunning)
            ops("postStart")
            if (!preStartFiles) drop()
            val outbox = s.getJSONObject("outbox")
            val tally = s.optJSONObject("outboxTally")
            val expectedCount = outbox.length() + (tally?.let { t -> t.keys().asSequence().sumOf { t.getInt(it) } } ?: 0)
            val resume = s.optJSONArray("resume")?.let { strings(it) } ?: emptyList()
            var resumed = false
            val deadline = System.nanoTime() + 40_000_000_000L
            fun jsonFiles(d: File) = d.listFiles()!!.filter { it.isFile }
            while (System.nanoTime() < deadline) {
                if (!resumed && resume.isNotEmpty() && resume.all {
                        store.get(it)?.phase == RequestPhase.WAITING_RELAY_USER && File(paths.processingDir, "$it.json").exists()
                    }
                ) {
                    val waiting = s.getJSONObject("waitingPhases")
                    for (k in waiting.keys()) assertEquals(waiting.getString(k), store.get(k)!!.phase.name)
                    for (id in resume) {
                        server.resumeParked(id)
                        server.resumeParked(id) // second trigger is a no-op
                    }
                    resumed = true
                }
                val done = jsonFiles(paths.outboxDir).count { it.name.endsWith(".json") } >= expectedCount &&
                    jsonFiles(paths.processingDir).isEmpty() &&
                    jsonFiles(paths.inboxDir).size == s.getJSONArray("inboxLeft").length()
                if (done) break
                Thread.sleep(50)
            }
            Thread.sleep(200)
            for (k in outbox.keys()) {
                val file = File(paths.outboxDir, k)
                assertTrue("$name: missing outbox/$k", file.isFile)
                assertResponse(outbox.getJSONObject(k), file.readText(Charsets.UTF_8), "$name outbox/$k")
            }
            val names = { d: File -> jsonFiles(d).map { it.name }.sorted() }
            if (tally != null) {
                val got = jsonFiles(paths.outboxDir).map { JSONObject(it.readText(Charsets.UTF_8)) }
                    .groupBy { if (it.getBoolean("ok")) "ok" else it.getJSONObject("error").getString("code") }
                for (k in tally.keys()) assertEquals(k, tally.getInt(k), got[k]?.size ?: 0)
                assertEquals(tally.length(), got.size)
                val reasons = s.getJSONObject("tallyReasons")
                for (k in reasons.keys()) {
                    got.getValue(k).forEach { assertTrue(it.toString(), it.getString("reason").startsWith(reasons.getString(k).substring(1))) }
                }
            } else {
                assertEquals(outbox.keys().asSequence().sorted().toList(), names(paths.outboxDir))
            }
            val control = s.getJSONObject("controlOutbox")
            assertEquals(control.keys().asSequence().sorted().toList(), names(paths.controlOutboxDir))
            for (k in control.keys()) {
                assertResponse(control.getJSONObject(k), File(paths.controlOutboxDir, k).readText(Charsets.UTF_8), "$name control-outbox/$k")
            }
            s.optJSONObject("phases")?.let { phases ->
                for (k in phases.keys()) assertEquals("$name phase $k", phases.getString(k), store.get(k)?.phase?.name)
            }
            assertEquals(strings(s.getJSONArray("inboxLeft")).sorted(), names(paths.inboxDir))
            assertTrue(jsonFiles(paths.processingDir).isEmpty())
            s.optJSONObject("budgets")?.let { b ->
                for (k in b.keys()) {
                    val range = b.getJSONArray(k)
                    val got = budgets.getValue(k)
                    assertTrue("$k budget $got", got in range.getLong(0)..range.getLong(1))
                }
            }
            val log = paths.logsDir.listFiles()!!.filter { it.isFile }.joinToString("\n") { it.readText(Charsets.UTF_8) }
            for (line in strings(s.getJSONArray("logContains"))) assertTrue("$name: log lacks '$line'", log.contains(line))
        } finally {
            server.stop()
            scope.cancel()
        }
    }

    @Test
    fun legacyMailboxBasicMatchesSharedVectors() = scenario("basic")

    @Test
    fun legacyMailboxTimeoutsMatchSharedVectors() = scenario("timeouts-and-oversize")

    @Test
    fun legacyMailboxStatefulMatchesSharedVectors() = scenario("stateful")

    @Test
    fun legacyMailboxParkedMatchesSharedVectors() = scenario("parked")

    @Test
    fun legacyMailboxEvictionMatchesSharedVectors() = scenario("eviction")
}
