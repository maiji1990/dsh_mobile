package dshmobile.nativebridge

import interlock.relay.core.interlock.ApprovalDeadEnd
import interlock.relay.core.interlock.ApprovalPrompt
import interlock.relay.core.interlock.ConsentSurfaces
import interlock.relay.core.interlock.InterlockChoice
import interlock.relay.core.interlock.InterlockQueue
import interlock.relay.core.interlock.InterlockTargets
import interlock.relay.core.interlock.OverlayApprovalChannel
import interlock.relay.core.interlock.RiskLevel
import interlock.relay.core.protocol.CapabilityId
import interlock.relay.core.protocol.CapabilityRegistry
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.async
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.lang.reflect.Method
import java.util.concurrent.atomic.AtomicInteger

/**
 * PR-07 step 4: the LEGACY approval targets, consent surfaces and approval queue (DSHBox interlock-relay-core
 * InterlockTargets / ConsentSurfaces / InterlockQueue / ParkedAnswerMailbox) over tests/TestVectors/relay-approval.json;
 * the C# port runs the same vectors (RelayApprovalParityTests). Internal members are reached by reflection; the queue's
 * random id seed is pinned to 0 so request ids read 1, 2, 3...
 */
class LegacyRelayApprovalParityTest {
    private fun repo(): File {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "global.json").isFile) dir = dir.parentFile
        return checkNotNull(dir) { "repo root (global.json) not found" }
    }

    private val v by lazy { JSONObject(File(repo(), "tests/TestVectors/relay-approval.json").readText(Charsets.UTF_8)) }

    private fun descriptor(wire: String) = checkNotNull(CapabilityRegistry.find(wire)) { wire }

    private fun str(o: JSONObject, name: String): String? = if (!o.has(name) || o.isNull(name)) null else o.getString(name)

    private fun method(cls: Class<*>, name: String, arity: Int): Method =
        cls.declaredMethods.first { (it.name == name || it.name.startsWith("$name$")) && it.parameterCount == arity }
            .also { it.isAccessible = true }

    private val brokerKt by lazy { Class.forName("interlock.relay.core.interlock.InterlockBrokerKt") }

    private fun isAnswer(c: InterlockChoice) = method(brokerKt, "isAnswer", 1).invoke(null, c) as Boolean

    private fun downgraded(c: InterlockChoice, p: ApprovalPrompt) = method(brokerKt, "downgradedFor", 2).invoke(null, c, p) as InterlockChoice

    private fun same(a: ApprovalPrompt, b: ApprovalPrompt) = method(brokerKt, "sameQuestionAs", 2).invoke(null, a, b) as Boolean

    private fun terminates(p: ApprovalPrompt) = method(brokerKt, "getTerminatesWhenAnswerDropped", 1).invoke(null, p) as Boolean

    @Test
    fun legacyTargetsMatchSharedVectors() {
        val targets = v.getJSONArray("targets")
        assertTrue(targets.length() > 40)
        for (i in 0 until targets.length()) {
            val c = targets.getJSONObject(i)
            if (c.optBoolean("csharpOnly")) continue // libcore org.json semantics; Maven org.json differs (see vector comment)
            val (target, detail) = InterlockTargets.of(descriptor(c.getString("capability")), c.getJSONObject("args")) { "${it}c" }
            assertEquals(c.getString("name") + " target", str(c, "target"), target)
            assertEquals(c.getString("name") + " detail", str(c, "detail"), detail)
        }
        val volatile = v.getJSONArray("volatile")
        for (i in 0 until volatile.length()) {
            val c = volatile.getJSONObject(i)
            assertEquals(c.toString(), c.getBoolean("expect"), InterlockTargets.isVolatileTarget(descriptor(c.getString("capability")), c.getJSONObject("args")))
        }
        val volatileFor = method(InterlockTargets::class.java, "volatileFor", 2)
        val cases = v.getJSONArray("volatileFor")
        for (i in 0 until cases.length()) {
            val c = cases.getJSONObject(i)
            val id = checkNotNull(CapabilityId.fromWire(c.getString("capability")))
            assertEquals(c.toString(), c.getBoolean("expect"), volatileFor.invoke(InterlockTargets, id, c.getBoolean("hasTarget")))
        }
    }

    @Test
    fun legacyConsentSurfacesMatchSharedVectors() {
        val consent = v.getJSONObject("consent")
        val uids = consent.getJSONObject("uids")
        val throwing = consent.getString("throwingUid")
        val saved = ConsentSurfaces.uidResolver
        ConsentSurfaces.uidResolver = { pkg -> if (pkg == throwing) error("scripted") else if (uids.has(pkg)) uids.getInt(pkg) else null }
        try {
            fun each(key: String, check: (JSONObject) -> Boolean) {
                val a = consent.getJSONArray(key)
                for (i in 0 until a.length()) {
                    val c = a.getJSONObject(i)
                    assertEquals("$key $c", c.getBoolean("expect"), check(c))
                }
            }
            each("systemOwned") { ConsentSurfaces.isSystemOwned(str(it, "pkg")) }
            each("consentPackage") { ConsentSurfaces.isConsentPackage(str(it, "pkg")) }
            each("control") { ConsentSurfaces.looksLikeConsentControl(str(it, "pkg"), str(it, "viewId")) }
            each("fromArgs") { ConsentSurfaces.looksLikeConsentFromArgs(it.getJSONObject("args")) }
            each("node") { ConsentSurfaces.looksLikeConsentNode(str(it, "pkg"), str(it, "viewId")) }
        } finally {
            ConsentSurfaces.uidResolver = saved
        }
    }

    private fun prompt(p: JSONObject) = ApprovalPrompt(
        capability = checkNotNull(CapabilityId.fromWire(p.getString("cap"))), argsIdentity = p.getString("args"),
        volatileTarget = p.getBoolean("volatile"), titleRes = 0, summaryRes = 0, targetLabel = str(p, "label"), risk = RiskLevel.LOW,
        callIndexInSession = 1, paramDetail = null, allowsSessionGrant = p.getBoolean("session"), deadlineAtMs = p.getLong("deadline"),
    )

    @Test
    fun legacyChoicesPromptsAndReasonsMatchSharedVectors() {
        val noSession = ApprovalPrompt(CapabilityId.UI_NODE, "a", false, 0, 0, null, RiskLevel.LOW, 1, null, false, 0)
        val session = noSession.copy(allowsSessionGrant = true)
        val choices = v.getJSONArray("choices")
        assertEquals(InterlockChoice.entries.size, choices.length())
        for (i in 0 until choices.length()) {
            val c = choices.getJSONObject(i)
            val choice = InterlockChoice.valueOf(c.getString("choice"))
            assertEquals(c.toString(), c.getBoolean("isAnswer"), isAnswer(choice))
            assertEquals(c.toString(), c.getString("downgradedNoSession"), downgraded(choice, noSession).name)
            assertEquals(c.toString(), c.getString("downgradedSession"), downgraded(choice, session).name)
        }
        val sq = v.getJSONArray("sameQuestion")
        for (i in 0 until sq.length()) {
            val c = sq.getJSONObject(i)
            assertEquals(c.toString(), c.getBoolean("expect"), same(prompt(c.getJSONObject("a")), prompt(c.getJSONObject("b"))))
        }
        val term = v.getJSONArray("terminates")
        for (i in 0 until term.length()) {
            val c = term.getJSONObject(i)
            val p = noSession.copy(volatileTarget = c.getBoolean("volatile"), allowsSessionGrant = c.getBoolean("session"))
            assertEquals(c.toString(), c.getBoolean("expect"), terminates(p))
        }
        val reasonFor = method(InterlockQueue.Companion::class.java, "reasonFor", 1)
        val reasons = v.getJSONArray("reasons")
        for (i in 0 until reasons.length()) {
            val c = reasons.getJSONObject(i)
            val d = c.getJSONArray("deadEnds")
            val set = (0 until d.length()).map { ApprovalDeadEnd.valueOf(d.getString(it)) }.toSet()
            assertEquals(c.toString(), c.getString("reason"), reasonFor.invoke(InterlockQueue.Companion, set))
        }
    }

    private class Env {
        var foreground = false
        var canNotify = false
        var blind = false
        var otherCard = false
        var overlayCanShow = false
        var overlayPresented = false
        var clock = 0L
    }

    private class FakeOverlay(private val env: Env) : OverlayApprovalChannel {
        val script = ArrayDeque<InterlockChoice>()
        var withdrawn = 0

        override fun canShow() = env.overlayCanShow

        override suspend fun present(prompt: ApprovalPrompt): InterlockChoice {
            val choice = script.removeFirstOrNull() ?: InterlockChoice.UNABLE_TO_SHOW
            env.overlayPresented = choice == InterlockChoice.AWAITING
            return choice
        }

        override fun isPresented() = env.overlayPresented

        override fun withdraw() {
            withdrawn++
            env.overlayPresented = false
        }

        override var onParkedChoice: ((ApprovalPrompt, InterlockChoice) -> Unit)? = null
        override var onHidden: (() -> Unit)? = null
    }

    private fun routingText(p: InterlockQueue.Presentation): String =
        JSONArray().put(p.request?.id ?: JSONObject.NULL).put(p.via.wire).put(p.waitingCount)
            .put(JSONArray(p.deadEnds.map { it.name }.sorted())).toString()

    @Test
    fun legacyQueueMatchesSharedScenarios() {
        val scenarios = v.getJSONArray("queue")
        assertEquals(5, scenarios.length())
        for (i in 0 until scenarios.length()) runScenario(scenarios.getJSONObject(i))
    }

    private fun runScenario(sc: JSONObject) = runBlocking {
        val name = sc.getString("name")
        val envJson = sc.getJSONObject("env")
        val env = Env().apply {
            foreground = envJson.optBoolean("foreground")
            canNotify = envJson.optBoolean("canNotify")
            overlayCanShow = envJson.optBoolean("overlayCanShow")
            clock = sc.getLong("clock")
        }
        val overlay = if (envJson.optBoolean("overlay")) FakeOverlay(env) else null
        val routing = mutableListOf<String>()
        val revoked = mutableListOf<Int>()
        val queue = InterlockQueue(
            isForegroundCapable = { env.foreground },
            onRouting = { routing.add(routingText(it)) },
            overlay = overlay,
            overlayBlind = { env.blind },
            canNotify = { env.canNotify },
            otherCardOnScreen = { env.otherCard },
            onRevokePresenters = { revoked.add(it.id) },
            now = { env.clock },
            inlineWaitMs = sc.getLong("inlineWaitMs"),
        )
        InterlockQueue::class.java.getDeclaredField("ids").apply { isAccessible = true }.let { (it.get(queue) as AtomicInteger).set(0) }
        val promptsJson = sc.getJSONObject("prompts")
        val prompts = promptsJson.keys().asSequence().associateWith { prompt(promptsJson.getJSONObject(it)) }
        val slots = mutableMapOf<String, Deferred<InterlockChoice>>()
        val steps = sc.getJSONArray("steps")
        for (i in 0 until steps.length()) {
            val st = steps.getJSONObject(i)
            val where = "$name step $i ${st.getString("op")}"
            routing.clear()
            revoked.clear()
            overlay?.withdrawn = 0
            var result: Any? = null
            when (st.getString("op")) {
                "present" -> result = withTimeout(5_000) { queue.present(prompts.getValue(st.getString("q"))) }.name
                "start" -> slots[st.getString("slot")] = async(start = CoroutineStart.UNDISPATCHED) { queue.present(prompts.getValue(st.getString("q"))) }
                "await" -> result = withTimeout(10_000) { slots.getValue(st.getString("slot")).await() }.name
                "resolve" -> result = queue.resolveCurrent(
                    InterlockChoice.valueOf(st.getString("choice")),
                    if (st.isNull("id")) InterlockQueue.ANY_REQUEST else st.getInt("id"),
                )
                "expire" -> result = queue.expireIfCurrent(st.getInt("id"))
                "parked" -> checkNotNull(checkNotNull(overlay).onParkedChoice)(prompts.getValue(st.getString("q")), InterlockChoice.valueOf(st.getString("choice")))
                "hidden" -> checkNotNull(checkNotNull(overlay).onHidden)()
                "reroute" -> queue.reroute()
                "snapshot" -> {
                    val snap = queue.pendingSnapshot()
                    val want = st.getJSONObject("snapshot")
                    assertEquals("$where displayed", if (want.isNull("displayed")) null else want.getInt("displayed"), snap.displayed?.id)
                    assertEquals("$where waiting", want.getInt("waiting"), snap.waitingCount)
                }
                "cancelAll" -> queue.cancelAll()
                "clock" -> env.clock += st.getLong("advance")
                "overlayScript" -> {
                    val r = st.getJSONArray("results")
                    for (k in 0 until r.length()) checkNotNull(overlay).script.addLast(InterlockChoice.valueOf(r.getString(k)))
                }
                "env" -> {
                    val set = st.getJSONObject("set")
                    for (k in set.keys()) {
                        val b = set.getBoolean(k)
                        when (k) {
                            "foreground" -> env.foreground = b
                            "canNotify" -> env.canNotify = b
                            "overlayBlind" -> env.blind = b
                            "otherCard" -> env.otherCard = b
                            "overlayCanShow" -> env.overlayCanShow = b
                            else -> error(k)
                        }
                    }
                }
                else -> error(where)
            }
            if (st.has("result")) assertEquals("$where result", st.get("result").let { if (it is Boolean) it else it.toString() }, result)
            val wantRouting = st.getJSONArray("routing").let { a -> (0 until a.length()).map { a.getJSONArray(it).toString() } }
            assertEquals("$where routing", wantRouting, routing)
            val wantRevoked = st.getJSONArray("revoked").let { a -> (0 until a.length()).map { a.getInt(it) } }
            assertEquals("$where revoked", wantRevoked, revoked)
            assertEquals("$where withdrawn", st.getInt("withdrawn"), overlay?.withdrawn ?: 0)
            if (st.has("reason")) assertEquals("$where reason", str(st, "reason"), queue.lastDeadEndReason())
        }
        for ((slot, d) in slots) assertTrue("$name: $slot never finished", d.isCompleted)
        assertNotNull(queue)
    }
}
