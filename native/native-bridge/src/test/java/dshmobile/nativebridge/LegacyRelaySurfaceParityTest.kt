package dshmobile.nativebridge

import interlock.relay.core.protocol.CapabilityId
import interlock.relay.core.protocol.CapabilityRegistry
import interlock.relay.core.protocol.RelayError
import interlock.relay.core.protocol.RelayResponse
import interlock.relay.core.protocol.SurfaceKind
import interlock.relay.core.protocol.effectStateFor
import interlock.relay.core.protocol.retryPolicyOf
import interlock.relay.core.surface.SurfaceDecision
import interlock.relay.core.surface.SurfacePolicy
import interlock.relay.core.surface.SurfacePreference
import interlock.relay.core.transport.EnvelopeCodec
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * PR-07 segment 4: the LEGACY execution-surface decision (DSHBox interlock-relay-core SurfacePolicy over the real
 * CapabilityRegistry descriptors), the refusals around the gate and the execution quota (RateLimiter EXECUTION_LIMITS)
 * over the shared cases in tests/TestVectors/relay-surface.json; the C# ports run the same cases
 * (RelaySurfaceParityTests). The coordinator's own steps (bring-to-front, refusal text, quota after the gate) live in
 * private members of RelayCoordinator, so they are pinned by a source contract scan plus the real upstream encoder and
 * semantics tables; the device shadow checks the live decision. Encodings are compared as structures (Maven org.json).
 */
class LegacyRelaySurfaceParityTest {
    private fun repo(): File {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "global.json").isFile) dir = dir.parentFile
        return checkNotNull(dir) { "repo root (global.json) not found" }
    }

    private val v by lazy { JSONObject(File(repo(), "tests/TestVectors/relay-surface.json").readText(Charsets.UTF_8)) }

    private val core by lazy { File(repo(), "third_party/dshbox/pilot/interlock-relay-core/src/main/java/interlock/relay/core") }

    private fun consentKindFor(error: RelayError?): String? =
        Class.forName("interlock.relay.core.runtime.RelayCoordinatorKt").getDeclaredMethod("consentKindFor", RelayError::class.java)
            .apply { isAccessible = true }.invoke(null, error) as String?

    private fun canon(x: Any?): Any? = when (x) {
        is JSONObject -> x.keys().asSequence().sorted().associateWith { canon(x.opt(it)) }
        is JSONArray -> (0 until x.length()).map { canon(x.opt(it)) }
        is Number -> x.toString()
        JSONObject.NULL -> null
        else -> x
    }

    private fun kind(wire: String): SurfaceKind = checkNotNull(SurfaceKind.fromWire(wire)) { wire }

    private fun opt(o: JSONObject, key: String): String? = if (o.has(key) && !o.isNull(key)) o.getString(key) else null

    private fun describe(d: SurfaceDecision) = "${d.surface.wire}|${d.degradedFrom?.wire}|${d.reason}|${d.requiresMode?.wire}"

    private fun describe(e: JSONObject) = "${opt(e, "surface")}|${opt(e, "degradedFrom")}|${opt(e, "reason")}|${opt(e, "requiresMode")}"

    private fun policyOf(c: JSONObject): SurfacePolicy {
        val cannot = c.optJSONArray("cannotServe")?.let { a -> (0 until a.length()).map { kind(a.getString(it)) }.toSet() } ?: emptySet()
        val preference = SurfacePreference.valueOf(c.getString("preference"))
        val shell = c.getBoolean("shell")
        return SurfacePolicy(
            preference = { preference },
            shellAvailable = { shell },
            backendCanServe = { _, s -> s !in cannot },
            sdkInt = c.getInt("sdk"),
        )
    }

    private fun descriptor(wire: String) = checkNotNull(CapabilityRegistry.find(wire)) { wire }

    @Test
    fun legacyRegistryMatchesSharedVectors() {
        val registry = v.getJSONObject("registry")
        assertEquals(CapabilityId.entries.map { it.wire }, registry.keys().asSequence().toList().sortedBy { w -> CapabilityId.entries.indexOfFirst { it.wire == w } })
        assertEquals(CapabilityId.entries.size, registry.length())
        for (id in CapabilityId.entries) {
            val e = registry.getJSONObject(id.wire)
            val d = descriptor(id.wire)
            val surfaces = e.getJSONArray("surfaces")
            assertEquals(id.wire, (0 until surfaces.length()).map { surfaces.getString(it) }, d.surfaces.map { it.wire })
            val backends = e.getJSONArray("backends")
            assertEquals(id.wire, (0 until backends.length()).map { backends.getString(it) }, d.backends.map { it.name.lowercase() })
        }
    }

    @Test
    fun legacySurfacePolicyMatchesSharedVectors() {
        val cases = v.getJSONArray("decide")
        assertTrue(cases.length() > 0)
        for (i in 0 until cases.length()) {
            val c = cases.getJSONObject(i)
            val base = descriptor(c.getString("capability"))
            val d = c.optJSONArray("surfaces")?.let { a -> base.copy(surfaces = (0 until a.length()).mapTo(LinkedHashSet()) { kind(a.getString(it)) }) } ?: base
            assertEquals(c.getString("name"), describe(c.getJSONObject("expect")), describe(policyOf(c).decide(d)))
        }
    }

    @Test
    fun legacyStageMatchesSharedVectors() {
        val self = v.getString("selfPackage")
        val cases = v.getJSONArray("stage")
        assertTrue(cases.length() > 0)
        for (i in 0 until cases.length()) {
            val c = cases.getJSONObject(i)
            val name = c.getString("name")
            val d = descriptor(c.getString("capability"))
            val args = JSONObject(c.getString("args"))
            // RelayCoordinator.isHostBringToFront (private): exactly app.launch {"package": self}.
            val bringToFront = d.id == CapabilityId.APP_LAUNCH && args.length() == 1 && args.optString("package") == self
            val decision = if (bringToFront) SurfaceDecision(SurfaceKind.FOREGROUND) else policyOf(c).decide(d)
            assertEquals(name, describe(c.getJSONObject("decision")), describe(decision))
            val error = when {
                decision.requiresMode != null -> RelayError.SURFACE_MODE_REQUIRED
                !c.getBoolean("implemented") -> RelayError.CAPABILITY_NOT_IMPLEMENTED
                else -> null
            }
            if (c.isNull("rejected")) {
                assertEquals(name, null, error)
                continue
            }
            val rejected = c.getJSONObject("rejected")
            assertEquals(name, rejected.getString("code"), error?.code)
            val reason = if (error == RelayError.SURFACE_MODE_REQUIRED) {
                "capability ${d.id.wire} only runs on the ${decision.requiresMode!!.wire} execution mode; " +
                    "switch the surface preference or bring that display up, then retry"
            } else {
                d.id.wire
            }
            assertEquals(name, canon(JSONObject(rejected.getString("json"))), canon(JSONObject(EnvelopeCodec.encode(rejection(c.getString("id"), d.id, error!!, reason, decision, 3)))))
        }
    }

    /** RelayCoordinator.rejected() with a decided surface: the shape both refusal steps and the quota use. */
    private fun rejection(id: String, cap: CapabilityId, error: RelayError, reason: String, decision: SurfaceDecision, elapsed: Long) = RelayResponse(
        id = id,
        ok = false,
        data = JSONObject(),
        artifacts = emptyList(),
        surface = decision.surface,
        degradedFrom = decision.degradedFrom,
        reason = reason,
        error = error,
        elapsedMs = elapsed,
        retryPolicy = retryPolicyOf(cap),
        effectState = effectStateFor(ok = false, error = error),
        consentKind = consentKindFor(error),
    )

    @Test
    fun legacyExecutionQuotaMatchesSharedVectors() {
        val rateLimiter = Class.forName("interlock.relay.core.interlock.RateLimiter")
        val companion = rateLimiter.getDeclaredField("Companion").apply { isAccessible = true }.get(null)
        val execLimits = companion.javaClass.getMethod("getEXECUTION_LIMITS").invoke(companion)
        @Suppress("UNCHECKED_CAST")
        val limitOf = execLimits as (CapabilityId) -> Int
        val cases = v.getJSONArray("execQuota")
        for (i in 0 until cases.length()) {
            val c = cases.getJSONObject(i)
            var clock = 0L
            val now: () -> Long = { clock }
            val limiter = rateLimiter.declaredConstructors.single().apply { isAccessible = true }.newInstance(now, execLimits)
            val accepted = rateLimiter.getMethod("accepted", CapabilityId::class.java)
            val id = checkNotNull(CapabilityId.fromWire(c.getString("capability")))
            assertEquals(c.getString("name"), c.getInt("limit"), limitOf(id))
            val events = c.getJSONArray("events")
            var n = 0
            for (j in 0 until events.length()) {
                val e = events.getJSONObject(j)
                clock = e.getLong("at")
                repeat(e.getInt("repeat")) {
                    // RelayCoordinator.finishAfterGate: `!gate.requiresApproval && !execRate.accepted(id)` refuses.
                    val refused = !e.getBoolean("approval") && !(accepted.invoke(limiter, id) as Boolean)
                    assertEquals("${c.getString("name")} event $n at $clock", e.getBoolean("accepted"), !refused)
                    n++
                }
            }
        }
        val rejects = v.getJSONArray("execReject")
        for (i in 0 until rejects.length()) {
            val c = rejects.getJSONObject(i)
            val d = c.getJSONObject("decision")
            val decision = SurfaceDecision(kind(d.getString("surface")), opt(d, "degradedFrom")?.let(::kind), opt(d, "reason"))
            val cap = checkNotNull(CapabilityId.fromWire(c.getString("capability")))
            val text = EnvelopeCodec.encode(rejection(c.getString("id"), cap, RelayError.RATE_LIMITED, RelayError.RATE_LIMITED.code + "/execution_rate", decision, 4))
            assertEquals(c.getString("name"), canon(JSONObject(c.getString("json"))), canon(JSONObject(text)))
        }
    }

    /** The private coordinator steps the vectors encode, pinned to the upstream source so a bump cannot drift silently. */
    @Test
    fun coordinatorSourceContract() {
        val src = File(core, "runtime/RelayCoordinator.kt").readText(Charsets.UTF_8).replace(Regex("\\s+"), " ")
        for (needle in listOf(
            "val decision = if (bringToFront) {",
            "SurfaceDecision(SurfaceKind.FOREGROUND) } else { policy.decide(descriptor) }",
            "decision.requiresMode?.let { mode ->",
            "RelayError.SURFACE_MODE_REQUIRED, \"capability \${descriptor.id.wire} only runs on the \${mode.wire} execution mode; \" + \"switch the surface preference or bring that display up, then retry\"",
            "if (!dispatcher.isImplemented(descriptor)) {",
            "RelayError.CAPABILITY_NOT_IMPLEMENTED, descriptor.id.wire, descriptor, elapsed(), surface = decision.surface, degradedFrom = decision.degradedFrom,",
            "if (!gate.requiresApproval && !execRate.accepted(descriptor.id)) {",
            "RelayError.RATE_LIMITED.code + \"/execution_rate\"",
            "private val execRate = RateLimiter(now, RateLimiter.EXECUTION_LIMITS)",
        )) {
            assertTrue("RelayCoordinator.kt no longer contains: $needle", src.contains(needle))
        }
        // Order: mode required before not implemented, both before the system probe and the gate.
        val mode = src.indexOf("decision.requiresMode?.let")
        val impl = src.indexOf("if (!dispatcher.isImplemented(descriptor))")
        val probe = src.indexOf("probe.probe(descriptor, decision.surface)")
        assertTrue(mode in 0 until impl && impl < probe)
        val container = File(core, "runtime/RelayContainer.kt").readText(Charsets.UTF_8).replace(Regex("\\s+"), " ")
        assertTrue(container.contains("SurfacePolicy( preference = { surfacePreferences.preference() }, shellAvailable = { shizuku.available }, backendCanServe = dispatcher::canServe, )"))
        assertTrue(container.contains("trustedDisplayId = { if (trustedDisplayReady()) shizukuBackend.displayId else -1 }"))
        assertTrue(container.contains("private fun trustedDisplayReady(): Boolean = shizuku.available && shizukuBackend.displayAlive()"))
    }
}
