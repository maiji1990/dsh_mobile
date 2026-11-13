package dshmobile.nativebridge

import interlock.relay.core.interlock.InterlockPrompt
import interlock.relay.core.interlock.PromptCodes
import interlock.relay.core.interlock.PromptOutcome
import interlock.relay.core.log.RunLog
import interlock.relay.core.protocol.CapabilityId
import interlock.relay.core.spi.RelayPathPolicy
import interlock.relay.core.storage.QuotaLedger
import interlock.relay.core.storage.RelayPaths
import interlock.relay.core.storage.StorageReaper
import interlock.relay.core.transport.ControlChannelServer
import interlock.relay.core.transport.ControlRequest
import interlock.relay.core.transport.MailboxServer
import interlock.relay.core.transport.RequestPhase
import interlock.relay.core.transport.RequestState
import interlock.relay.core.transport.RequestStateStore
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.SendChannel
import org.json.JSONArray
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.lang.reflect.Method
import java.nio.file.Files

/**
 * PR-07 segment 8: the LEGACY control channel (DSHBox interlock-relay-core ControlChannelServer, ControlChannelLogic,
 * PromptCodes) over the shared cases in tests/TestVectors/relay-control.json; the C# port runs the same cases
 * (RelayControlParityTests). FileObserver is a no-op stub on the JVM, so the test pokes the loop's signal channel.
 */
class LegacyRelayControlParityTest {
    private fun repo(): File {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "global.json").isFile) dir = dir.parentFile
        return checkNotNull(dir) { "repo root (global.json) not found" }
    }

    private val v by lazy { JSONObject(File(repo(), "tests/TestVectors/relay-control.json").readText(Charsets.UTF_8)) }
    private val root: File = Files.createTempDirectory("relay-control").toFile()

    @After
    fun cleanup() {
        root.deleteRecursively()
    }

    private fun objects(a: JSONArray) = (0 until a.length()).map { a.getJSONObject(it) }

    private fun strings(a: JSONArray) = (0 until a.length()).map { a.getString(it) }

    /** Structural JSON equality (android.jar's org.json has no similar()). */
    private fun same(a: Any?, b: Any?): Boolean = when {
        a is JSONObject && b is JSONObject ->
            a.length() == b.length() && a.keys().asSequence().all { b.has(it) && same(a.get(it), b.get(it)) }
        a is JSONArray && b is JSONArray -> a.length() == b.length() && (0 until a.length()).all { same(a.get(it), b.get(it)) }
        a is Number && b is Number -> if (a is Double || a is Float || b is Double || b is Float) a.toDouble() == b.toDouble() else a.toLong() == b.toLong()
        else -> a == b
    }

    private val logic: Any by lazy { Class.forName("interlock.relay.core.transport.ControlChannelLogic").getField("INSTANCE").get(null) }

    private fun logicCall(name: String, vararg args: Any?): Any? =
        logic.javaClass.methods.first { it.name == name && it.parameterCount == args.size }.invoke(logic, *args)

    private fun companion(name: String, arity: Int): Method =
        ControlChannelServer.Companion::class.java.declaredMethods
            .first { (it.name == name || it.name.startsWith("$name$")) && it.parameterCount == arity }
            .apply { isAccessible = true }

    private fun companionCall(name: String, vararg args: Any?): Any? = companion(name, args.size).invoke(ControlChannelServer.Companion, *args)

    private fun state(o: Any?): RequestState? = (o as? JSONObject)?.let { s ->
        RequestState(
            id = s.getString("id"), digest = s.getString("digest"), capability = s.getString("capability"),
            phase = RequestPhase.valueOf(s.getString("phase")), createdAtMs = s.getLong("createdAtMs"), ttlMs = s.getLong("ttlMs"),
            mayHaveDispatched = s.optBoolean("mayHaveDispatched", false),
            resultJson = if (s.has("resultJson")) s.getString("resultJson") else null,
            cancelRequested = s.optBoolean("cancelRequested", false),
        )
    }

    @Test
    fun legacyLogicMatchesSharedVectors() {
        for (c in objects(v.getJSONArray("submitEnvelope"))) {
            val s = c.getJSONObject("submit")
            val submit = ControlRequest.Submit(
                s.getString("id"), CapabilityId.fromWire(s.getString("capability"))!!, s.getJSONObject("args"),
                s.getLong("hostTtlMs"), s.getString("digest"),
            )
            val text = logicCall("submitEnvelopeV1", submit, c.getLong("ts")) as String
            assertTrue(text, same(c.getJSONObject("envelope"), JSONObject(text)))
        }
        for (c in objects(v.getJSONArray("statusPayload"))) {
            val got = logicCall("statusPayload", state(c.opt("state")), c.getString("target")) as JSONObject
            assertTrue("$c\n$got", same(c.getJSONObject("payload"), got))
        }
        for (c in objects(v.getJSONArray("healthPayload"))) {
            val a = c.getJSONArray("args")
            val got = logicCall(
                "healthPayload", a.getBoolean(0), a.getInt(1), if (a.isNull(2)) null else a.getLong(2), a.getInt(3), a.getBoolean(4),
            ) as JSONObject
            assertTrue("$c\n$got", same(c.getJSONObject("payload"), got))
        }
        val plans = v.getJSONArray("cancelPlan")
        for (i in 0 until plans.length()) {
            val c = plans.getJSONArray(i)
            val st = if (c.isNull(0)) null else RequestState("t", "d", "ui.node", RequestPhase.valueOf(c.getString(0)), 0, null, 5000)
            val plan = logicCall("cancelPlan", st, c.getBoolean(1))!!
            val name = plan.javaClass.simpleName.let { n ->
                if (n == "AlreadySettled") "AlreadySettled:" + (plan.javaClass.getMethod("getPhase").invoke(plan) as RequestPhase).name else n
            }
            assertEquals(c.toString(), c.getString(2), name)
        }
        val stale = v.getJSONArray("askStale")
        for (i in 0 until stale.length()) {
            val c = stale.getJSONArray(i)
            assertEquals(c.toString(), c.getLong(3), companionCall("askStaleMs", c.getLong(0), c.getLong(1), c.getLong(2)))
        }
        val codes = v.getJSONArray("promptCodes")
        for (i in 0 until codes.length()) {
            val c = codes.getJSONArray(i)
            assertEquals(c.getString(1), PromptCodes.codeOf(c.getString(0)))
            assertEquals(c.getInt(2), PromptCodes.exitCodeFor(c.getString(0)))
            assertEquals(c.getBoolean(3), PromptCodes.retryable(c.getString(0)))
        }
        val constants = v.getJSONObject("promptConstants")
        for (k in constants.keys()) assertEquals(k, constants.getString(k), PromptCodes::class.java.getField(k).get(null))
        val ask = v.getJSONObject("askPayloads")
        val chosen = ask.getJSONObject("chosen")
        val args = chosen.getJSONArray("args")
        assertTrue(same(chosen.getJSONObject("payload"), companionCall("askChosenPayload", args.getInt(0), args.getString(1))))
        assertTrue(same(ask.getJSONObject("rejectAll").getJSONObject("payload"), companionCall("askRejectAllPayload")))
        assertTrue(same(ask.getJSONObject("reask").getJSONObject("payload"), companionCall("askReaskPayload")))
        val failure = ask.getJSONObject("failure")
        assertTrue(same(failure.getJSONObject("payload"), companionCall("askFailurePayload", failure.getString("cause"))))
    }

    private class ScriptedFailure : RuntimeException()

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
        paths.ensureMailboxDirs()
        val runLog = RunLog(paths.logsDir)
        val reaper = StorageReaper(paths, QuotaLedger(paths.quotaFile), runLog, now = now, wallNow = { System.currentTimeMillis() })
        val store = RequestStateStore(paths.requestStateDir, now = now, runLog = runLog)
        val h = v.getJSONObject("health")
        val health = MailboxServer.WorkLoopHealth(
            h.getBoolean("channelLive"), h.getInt("queueDepth"), h.getLong("oldestQueuedAgeMs"), h.getInt("inboxDepth"), h.getBoolean("stalled"),
        )
        val release = CompletableDeferred<Unit>()
        val presenter = InterlockPrompt { question, _, _ ->
            when (question) {
                "pick" -> PromptOutcome.Chosen(1, "B")
                "none" -> PromptOutcome.RejectedAll
                "again" -> PromptOutcome.Reasked
                "slow" -> PromptOutcome.TimedOut(covered = true)
                "nope" -> PromptOutcome.Unavailable(PromptCodes.NO_SURFACE_HIDDEN)
                "boom" -> throw ScriptedFailure()
                "hold" -> {
                    release.await()
                    PromptOutcome.Chosen(0, "X")
                }
                else -> error(question)
            }
        }
        val server = ControlChannelServer(
            paths, reaper, store, runLog, health = { health },
            askPresenter = if (s.getBoolean("presenter")) presenter else null, now = now,
        )
        @Suppress("UNCHECKED_CAST")
        val signals = ControlChannelServer::class.java.getDeclaredField("signals").apply { isAccessible = true }.get(server) as SendChannel<Unit>
        s.optJSONArray("preState")?.let { pre ->
            for (op in objects(pre)) {
                val id = op.getString("id")
                when (op.getString("op")) {
                    "create" -> store.create(id, op.getString("digest"), "ui.node", 30_000)
                    "terminal" -> store.recordTerminal(id, RequestPhase.valueOf(op.getString("phase")), op.getString("result"))
                    "admit" -> store.markAdmitted(id)
                    "guard" -> store.armExecDispatchGuard(id)
                    else -> error(op.toString())
                }
            }
        }
        s.optJSONObject("inboxFiles")?.let { f -> for (k in f.keys()) File(paths.inboxDir, k).writeText(f.getString(k), Charsets.UTF_8) }
        val staging = File(root, "drop").apply { mkdirs() }
        fun drop(f: JSONObject) {
            val temp = File(staging, f.getString("name"))
            temp.writeText(f.getString("text"), Charsets.UTF_8)
            if (f.has("ageMs")) assertTrue(temp.setLastModified(System.currentTimeMillis() - f.getLong("ageMs")))
            assertTrue(temp.renameTo(File(paths.controlInboxDir, f.getString("name"))))
            signals.trySend(Unit)
        }
        fun log() = paths.logsDir.listFiles()!!.filter { it.isFile }.joinToString("\n") { it.readText(Charsets.UTF_8) }
        fun until(what: String, condition: () -> Boolean) {
            val deadline = System.nanoTime() + 30_000_000_000L
            while (!condition()) {
                assertTrue("$name: timed out waiting for $what", System.nanoTime() < deadline)
                signals.trySend(Unit)
                Thread.sleep(50)
            }
        }
        val names = { d: File -> d.listFiles()!!.filter { it.isFile }.map { it.name }.sorted() }
        val wallAtStart = System.currentTimeMillis()
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        server.start(scope)
        try {
            assertTrue(server.isRunning)
            for (f in objects(s.getJSONArray("files"))) drop(f)
            s.optJSONObject("redeliver")?.let { again ->
                until("the first ask") { log().contains("control op=ask result=accepted") }
                drop(again)
                val dup = s.getString("duplicateLog")
                until("the duplicate refusal") { log().contains(dup) }
                assertFalse("duplicate must not reply", File(paths.controlOutboxDir, again.getString("name")).exists())
                release.complete(Unit)
            }
            val control = s.getJSONObject("controlOutbox")
            val left = strings(s.getJSONArray("controlInboxLeft"))
            until("all replies") {
                control.keys().asSequence().all { File(paths.controlOutboxDir, it).isFile } && names(paths.controlInboxDir).size == left.size
            }
            Thread.sleep(200)
            assertEquals(control.keys().asSequence().sorted().toList(), names(paths.controlOutboxDir))
            for (k in control.keys()) {
                val text = File(paths.controlOutboxDir, k).readText(Charsets.UTF_8)
                assertTrue("$name control-outbox/$k: $text", same(control.getJSONObject(k), JSONObject(text)))
            }
            assertEquals(left.sorted(), names(paths.controlInboxDir))
            val inbox = s.getJSONObject("inbox")
            assertEquals(inbox.keys().asSequence().sorted().toList(), names(paths.inboxDir))
            for (k in inbox.keys()) {
                val envelope = JSONObject(File(paths.inboxDir, k).readText(Charsets.UTF_8))
                val ts = envelope.getLong("ts")
                assertTrue("ts $ts", ts in wallAtStart..System.currentTimeMillis())
                envelope.remove("ts")
                assertTrue(envelope.toString(), same(inbox.getJSONObject(k), envelope))
            }
            s.optJSONObject("phases")?.let { p -> for (k in p.keys()) assertEquals("$name phase $k", p.getString(k), store.get(k)?.phase?.name) }
            s.optJSONObject("cancelRequested")?.let { p -> for (k in p.keys()) assertEquals(k, p.getBoolean(k), store.get(k)!!.cancelRequested) }
            val text = log()
            for (line in strings(s.getJSONArray("logContains"))) assertTrue("$name: log lacks '$line'", text.contains(line))
        } finally {
            release.complete(Unit)
            server.stop()
            scope.cancel()
        }
    }

    @Test
    fun legacyControlOpsMatchSharedVectors() = scenario("ops")

    @Test
    fun legacyControlNoPresenterMatchesSharedVectors() = scenario("no-presenter")

    @Test
    fun legacyControlDuplicateAskMatchesSharedVectors() = scenario("duplicate-ask")
}
