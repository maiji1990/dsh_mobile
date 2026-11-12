package dshmobile.nativebridge

import android.content.ContentResolver
import android.content.Context
import android.content.ContextWrapper
import interlock.relay.core.interlock.ApprovalOutcome
import interlock.relay.core.interlock.ApprovalPrompt
import interlock.relay.core.interlock.InterlockBroker
import interlock.relay.core.interlock.InterlockChoice
import interlock.relay.core.interlock.InterlockGate
import interlock.relay.core.interlock.InterlockPresenter
import interlock.relay.core.interlock.SystemStateProbe
import interlock.relay.core.interlock.TierStore
import interlock.relay.core.log.RunLog
import interlock.relay.core.protocol.AccessTier
import interlock.relay.core.protocol.CapabilityId
import interlock.relay.core.protocol.CapabilityRegistry
import interlock.relay.core.protocol.GrantGap
import interlock.relay.core.protocol.ProbeResult
import interlock.relay.core.protocol.RelayError
import interlock.relay.core.protocol.RelayResponse
import interlock.relay.core.protocol.SurfaceKind
import interlock.relay.core.protocol.SystemGrantState
import interlock.relay.core.protocol.TierCeiling
import interlock.relay.core.protocol.effectStateFor
import interlock.relay.core.protocol.retryPolicyOf
import interlock.relay.core.spi.RelayPrefs
import interlock.relay.core.transport.EnvelopeCodec
import kotlinx.coroutines.runBlocking
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.nio.file.Files

/**
 * PR-07 segment 5: the LEGACY approval-gate decision (DSHBox interlock-relay-core InterlockGate.authorize with the
 * real InterlockBroker, TierStore, RunLog and SystemStateProbe) over the shared cases in
 * tests/TestVectors/relay-gate.json; the C# port runs the same cases (RelayGateParityTests). The presenter is a fake
 * that answers the scripted choice and records whether a prompt was shown at all. The probe runs on a ContextWrapper
 * whose permissions come from the case; accessibility, the notification listener and the microphone app-op are static
 * Android state the JVM test cannot set, so only the 'jvm' fact sets run here (the rest is C# + device).
 */
class LegacyRelayGateParityTest {
    private fun repo(): File {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "global.json").isFile) dir = dir.parentFile
        return checkNotNull(dir) { "repo root (global.json) not found" }
    }

    private val v by lazy { JSONObject(File(repo(), "tests/TestVectors/relay-gate.json").readText(Charsets.UTF_8)) }

    private val runLog by lazy { RunLog(Files.createTempDirectory("relay-gate-log").toFile()) }

    private fun descriptor(wire: String) = checkNotNull(CapabilityRegistry.find(wire)) { wire }

    private fun strings(a: JSONArray) = (0 until a.length()).map { a.getString(it) }

    private fun opt(o: JSONObject, key: String): String? = if (o.has(key) && !o.isNull(key)) o.getString(key) else null

    private class Prefs(private val values: Map<String, String>) : RelayPrefs {
        override fun getString(file: String, key: String, defValue: String?): String? =
            if (file == RelayPrefs.FILE_MAIN) values[key] ?: defValue else defValue
        override fun putString(file: String, key: String, value: String) = error("the gate must not write tiers here")
        override fun getBoolean(file: String, key: String, defValue: Boolean) = defValue
        override fun putBoolean(file: String, key: String, value: Boolean) = error("no writes")
    }

    private fun prefsOf(o: JSONObject) = Prefs(o.keys().asSequence().associateWith { o.getString(it) })

    /** Permissions from the case; Settings.Secure (stubbed) reads null, so accessibility / listener are unlisted. */
    private class FakeContext(private val granted: Set<String>) : ContextWrapper(null) {
        override fun getApplicationContext(): Context = this
        override fun getPackageName(): String = "org.example.dshmobile"
        override fun getContentResolver(): ContentResolver? = null
        override fun getSystemService(name: String): Any? = null
        override fun checkPermission(permission: String, pid: Int, uid: Int): Int = if (permission in granted) 0 else -1
    }

    private fun gap(g: GrantGap) = when (g) {
        GrantGap.Accessibility -> "accessibility"
        GrantGap.NotificationListener -> "notificationListener"
        GrantGap.Shizuku -> "shizuku"
        is GrantGap.RuntimePermission -> "permission:" + g.permission
    }

    @Test
    fun legacyPolicyTablesMatchSharedVectors() {
        val policy = v.getJSONObject("policy")
        assertEquals(CapabilityId.entries.size, policy.length())
        for (id in CapabilityId.entries) {
            val p = policy.getJSONObject(id.wire)
            val d = descriptor(id.wire)
            assertEquals(id.wire, p.getString("ceiling"), d.ceiling.name)
            assertEquals(id.wire, strings(p.getJSONArray("permissions")), d.runtimePermissions)
        }
        val verdicts = v.getJSONArray("verdict")
        assertEquals(54, verdicts.length())
        for (i in 0 until verdicts.length()) {
            val c = verdicts.getJSONObject(i)
            val system = SystemGrantState.valueOf(c.getString("system"))
            val tier = AccessTier.valueOf(c.getString("tier"))
            val got = InterlockGate.verdict(system, tier, TierCeiling.valueOf(c.getString("ceiling")))
            val name = c.toString()
            if (c.isNull("expect")) {
                assertNull(name, got)
            } else {
                val e = c.getJSONObject("expect")
                assertEquals(name, false, got!!.allowed)
                assertEquals(name, false, got.requiresApproval)
                assertEquals(name, e.getString("error"), got.error?.code)
                assertEquals(name, e.getString("label"), got.decisionLabel)
            }
            assertEquals(name, InterlockGate.systemBlocks(system), system.name in setOf("RUNTIME_ASKABLE", "MANUAL_ONLY", "UNAVAILABLE_ON_DEVICE"))
        }
        val approvals = v.getJSONArray("requiresApproval")
        assertEquals(36, approvals.length())
        for (i in 0 until approvals.length()) {
            val c = approvals.getJSONObject(i)
            assertEquals(c.toString(), c.getBoolean("expect"), InterlockGate.requiresApproval(
                AccessTier.valueOf(c.getString("tier")), TierCeiling.valueOf(c.getString("ceiling")),
                c.getBoolean("sessionGranted"), c.getBoolean("consentScreen"),
            ))
        }
    }

    @Test
    fun legacyOutcomesChoicesAndLabelsMatchSharedVectors() {
        val broker = InterlockBroker({ error("not presented") }, runLog)
        val companion = InterlockGate.Companion
        val label = companion.javaClass.methods.single { it.name.startsWith("approvalLabel") }
        // Exhaustive `when` (no else): a new upstream outcome stops this from compiling.
        fun nameOf(o: ApprovalOutcome) = when (o) {
            ApprovalOutcome.AllowedOnce -> "AllowedOnce"
            ApprovalOutcome.AllowedForSession -> "AllowedForSession"
            ApprovalOutcome.Denied -> "Denied"
            ApprovalOutcome.TimedOut -> "TimedOut"
            ApprovalOutcome.AwaitingConsent -> "AwaitingConsent"
            ApprovalOutcome.WaitingTurn -> "WaitingTurn"
            ApprovalOutcome.CannotPresent -> "CannotPresent"
        }
        val byName = listOf(
            ApprovalOutcome.AllowedOnce, ApprovalOutcome.AllowedForSession, ApprovalOutcome.Denied, ApprovalOutcome.TimedOut,
            ApprovalOutcome.AwaitingConsent, ApprovalOutcome.WaitingTurn, ApprovalOutcome.CannotPresent,
        ).associateBy(::nameOf)
        val outcomes = v.getJSONArray("outcomes")
        assertEquals(byName.size, outcomes.length())
        for (i in 0 until outcomes.length()) {
            val c = outcomes.getJSONObject(i)
            val outcome = byName.getValue(c.getString("outcome"))
            assertEquals(c.toString(), opt(c, "errorFor"), broker.errorFor(outcome)?.code)
            assertEquals(c.toString(), c.getString("label"), label.invoke(companion, outcome, broker.errorFor(outcome) ?: RelayError.GATE_HOST_DENIED))
        }
        val choices = v.getJSONObject("choices")
        assertEquals(InterlockChoice.entries.size, choices.length())
        for (choice in InterlockChoice.entries) {
            val b = InterlockBroker({ choice }, runLog)
            val prompt = ApprovalPrompt(
                capability = CapabilityId.UI_NODE, argsIdentity = "a", volatileTarget = false, titleRes = 0, summaryRes = 0,
                targetLabel = null, risk = interlock.relay.core.interlock.RiskLevel.LOW, callIndexInSession = 1, paramDetail = null,
                allowsSessionGrant = true,
            )
            val got = runBlocking { b.request(prompt, budgetMs = 30_000) }
            assertEquals(choice.name, choices.getString(choice.name), nameOf(got))
        }
    }

    @Test
    fun legacyTierStoreMatchesSharedVectors() {
        val cases = v.getJSONArray("tiers")
        for (i in 0 until cases.length()) {
            val c = cases.getJSONObject(i)
            val tiers = TierStore(prefsOf(c.getJSONObject("prefs")))
            assertEquals(c.getString("name"), c.getString("expect"), tiers.tierOf(checkNotNull(CapabilityId.fromWire(c.getString("capability")))).name)
        }
    }

    @Test
    fun legacySystemStateProbeMatchesSharedVectors() {
        val sets = v.getJSONArray("probe")
        var ran = 0
        for (i in 0 until sets.length()) {
            val set = sets.getJSONObject(i)
            if (!set.getBoolean("jvm")) continue
            val f = set.getJSONObject("facts")
            val shell = f.getBoolean("shell")
            val trusted = f.getBoolean("trusted")
            val probe = SystemStateProbe(
                FakeContext(strings(f.getJSONArray("granted")).toSet()),
                shellAvailable = { shell },
                trustedDisplayAvailable = { trusted },
                sdkInt = f.getInt("sdk"),
            )
            val rows = set.getJSONArray("rows")
            for (j in 0 until rows.length()) {
                val r = rows.getJSONObject(j)
                val route = opt(r, "route")?.let { checkNotNull(SurfaceKind.fromWire(it)) }
                val got = probe.probe(descriptor(r.getString("capability")), route)
                val where = "${f.getString("name")}: ${r.getString("capability")}@$route"
                assertEquals(where, r.getString("state"), got.state.name)
                assertEquals(where, strings(r.getJSONArray("gaps")), got.gaps.map(::gap))
                ran++
            }
        }
        assertTrue("no probe row ran", ran > 0)
    }

    @Test
    fun legacyAuthorizeMatchesSharedScenarios() {
        val scenarios = v.getJSONArray("authorize")
        for (i in 0 until scenarios.length()) {
            val s = scenarios.getJSONObject(i)
            val name = s.getString("name")
            var clock = 0L
            var choice = InterlockChoice.ALLOW_ONCE
            var shown: ApprovalPrompt? = null
            val broker = InterlockBroker(InterlockPresenter { p -> shown = p; choice }, runLog)
            val gate = InterlockGate(TierStore(prefsOf(s.getJSONObject("prefs"))), broker, runLog, now = { clock })
            val steps = s.getJSONArray("steps")
            for (j in 0 until steps.length()) {
                val st = steps.getJSONObject(j)
                clock = st.getLong("at")
                choice = InterlockChoice.valueOf(st.getString("choice"))
                val e = st.getJSONObject("expect")
                val repeat = st.optInt("repeat", 1)
                val distinct = st.optBoolean("distinct", false)
                val from = st.optInt("from", 0)
                val indexes = mutableListOf<Int>()
                repeat(repeat) { k ->
                    shown = null
                    val ident = st.getString("argsIdentity").let { if (distinct) "$it-${from + k}" else it }
                    val r = runBlocking {
                        gate.authorize(
                            descriptor = descriptor(st.getString("capability")),
                            targetLabel = null,
                            paramDetail = null,
                            argsIdentity = ident,
                            volatileTarget = false,
                            budgetMs = st.getLong("budgetMs"),
                            consentScreen = st.optBoolean("consentScreen", false),
                            system = ProbeResult(SystemGrantState.valueOf(st.getString("system")), emptyList()),
                            hostBringToFront = st.optBoolean("hostBringToFront", false),
                        )
                    }
                    val where = "$name @ $clock ${st.getString("capability")} $ident"
                    assertEquals(where, e.getBoolean("allowed"), r.allowed)
                    assertEquals(where, e.getBoolean("requiresApproval"), r.requiresApproval)
                    assertEquals(where, opt(e, "error"), r.error?.code)
                    assertEquals(where, e.getString("label"), r.decisionLabel)
                    assertEquals("$where presented", e.getBoolean("presented"), shown != null)
                    shown?.let {
                        assertEquals(where, e.getBoolean("allowsSessionGrant"), it.allowsSessionGrant)
                        indexes += it.callIndexInSession
                    }
                }
                e.optJSONArray("callIndexInSession")?.let { assertEquals(name, (0 until it.length()).map(it::getInt), indexes) }
            }
        }
    }

    @Test
    fun legacyRefusalsMatchSharedVectors() {
        val consentKind = Class.forName("interlock.relay.core.runtime.RelayCoordinatorKt").getDeclaredMethod("consentKindFor", RelayError::class.java)
            .apply { isAccessible = true }
        val cases = v.getJSONArray("refusals")
        for (i in 0 until cases.length()) {
            val c = cases.getJSONObject(i)
            val cap = checkNotNull(CapabilityId.fromWire(c.getString("capability")))
            val error = checkNotNull(RelayError.fromCode(c.getString("error")))
            val response = RelayResponse(
                id = c.getString("id"), ok = false, data = JSONObject(), artifacts = emptyList(),
                surface = SurfaceKind.FOREGROUND, degradedFrom = null,
                reason = error.code + "/" + c.getString("label"), error = error, elapsedMs = 7,
                retryPolicy = retryPolicyOf(cap), effectState = effectStateFor(ok = false, error = error),
                consentKind = consentKind.invoke(null, error) as String?,
            )
            assertEquals(c.getString("id"), canon(JSONObject(c.getString("json"))), canon(JSONObject(EnvelopeCodec.encode(response))))
        }
        // finishAfterGate's reason and its use of the decided surface, pinned to the source.
        val src = File(repo(), "third_party/dshbox/pilot/interlock-relay-core/src/main/java/interlock/relay/core/runtime/RelayCoordinator.kt")
            .readText(Charsets.UTF_8).replace(Regex("\\s+"), " ")
        for (needle in listOf(
            "val error = gate.error ?: RelayError.GATE_HOST_DENIED",
            "append(error.code).append('/').append(gate.decisionLabel) if (error == RelayError.GATE_NO_FOREGROUND) { approvalDeadEndHint()?.let { append(\"; \").append(it) } }",
            "logRejection = false, surface = decision.surface, degradedFrom = decision.degradedFrom, ) } logApprovalVerdict(request.id, gate)",
            "const val APPROVAL_REPLY_MARGIN_MS = 1_500L",
            "budgetMs = budgetMs - APPROVAL_REPLY_MARGIN_MS,",
        )) {
            assertTrue("RelayCoordinator.kt no longer contains: $needle", src.contains(needle))
        }
    }

    private fun canon(x: Any?): Any? = when (x) {
        is JSONObject -> x.keys().asSequence().sorted().associateWith { canon(x.opt(it)) }
        is JSONArray -> (0 until x.length()).map { canon(x.opt(it)) }
        is Number -> x.toString()
        JSONObject.NULL -> null
        else -> x
    }
}
