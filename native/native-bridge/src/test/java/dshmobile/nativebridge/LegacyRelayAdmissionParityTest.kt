package dshmobile.nativebridge

import interlock.relay.core.protocol.CapabilityArgsSpec
import interlock.relay.core.protocol.CapabilityId
import interlock.relay.core.protocol.EffectState
import interlock.relay.core.protocol.RelayError
import interlock.relay.core.protocol.RelayResponse
import interlock.relay.core.protocol.RetryPolicy
import interlock.relay.core.protocol.SurfaceKind
import interlock.relay.core.protocol.effectStateFor
import interlock.relay.core.protocol.retryPolicyOf
import interlock.relay.core.transport.EnvelopeCodec
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * PR-07 segment 3: the LEGACY Relay admission steps and response encoding (DSHBox interlock-relay-core) over the
 * shared cases in tests/TestVectors/relay-admission.json. The C# DshMobile.Runtime.Automation.Relay ports run the same
 * cases (RelayAdmissionParityTests). Upstream internals (selfTargetArg, consentKindFor, RateLimiter) are reached by
 * reflection; the admission order mirrors RelayCoordinator.handle and is also checked live on the device.
 * Encodings are compared as structures: Maven org.json (JVM tests) orders keys and escapes differently from the
 * Android libcore org.json Relay writes with — the exact text is locked on the C# side and on the device.
 */
class LegacyRelayAdmissionParityTest {
    private fun repo(): File {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "global.json").isFile) dir = dir.parentFile
        return checkNotNull(dir) { "repo root (global.json) not found" }
    }

    private val v by lazy { JSONObject(File(repo(), "tests/TestVectors/relay-admission.json").readText(Charsets.UTF_8)) }

    private val coordinatorKt by lazy { Class.forName("interlock.relay.core.runtime.RelayCoordinatorKt") }

    private fun selfTargetArg(value: Any?, self: String): String? =
        coordinatorKt.getDeclaredMethod("selfTargetArg", Any::class.java, String::class.java, String::class.java)
            .apply { isAccessible = true }.invoke(null, value, self, "") as String?

    private fun consentKindFor(error: RelayError?): String? =
        coordinatorKt.getDeclaredMethod("consentKindFor", RelayError::class.java)
            .apply { isAccessible = true }.invoke(null, error) as String?

    private fun canon(x: Any?): Any? = when (x) {
        is JSONObject -> x.keys().asSequence().sorted().associateWith { canon(x.opt(it)) }
        is JSONArray -> (0 until x.length()).map { canon(x.opt(it)) }
        is Number -> x.toString()
        JSONObject.NULL -> null
        else -> x
    }

    private fun strings(a: JSONArray): List<String> = (0 until a.length()).map { a.getString(it) }

    @Test
    fun legacyTablesMatchSharedVectors() {
        val category = v.getJSONObject("category")
        val limit = v.getJSONObject("executionLimit")
        val retry = v.getJSONObject("retryPolicy")
        assertEquals(CapabilityId.entries.size, category.length())
        val rateLimiter = Class.forName("interlock.relay.core.interlock.RateLimiter")
        val companion = rateLimiter.getDeclaredField("Companion").apply { isAccessible = true }.get(null)
        @Suppress("UNCHECKED_CAST")
        val execLimits = companion.javaClass.getMethod("getEXECUTION_LIMITS").invoke(companion) as (CapabilityId) -> Int
        for (id in CapabilityId.entries) {
            assertEquals(id.wire, category.getString(id.wire), id.category.name)
            assertEquals(id.wire, limit.getInt(id.wire), execLimits(id))
            assertEquals(id.wire, retry.getString(id.wire), retryPolicyOf(id).name.lowercase())
        }
        val errors = v.getJSONArray("errors")
        assertEquals(RelayError.entries.size, errors.length())
        for (i in 0 until errors.length()) {
            val e = errors.getJSONObject(i)
            val error = checkNotNull(RelayError.fromCode(e.getString("code")))
            assertEquals(e.getString("code"), e.getString("effectStateOnFailure"), effectStateFor(false, error).name.lowercase())
            assertEquals(e.getString("code"), if (e.isNull("consentKind")) null else e.getString("consentKind"), consentKindFor(error))
        }
    }

    @Test
    fun legacyArgsPreCheckMatchesSharedVectors() {
        val cases = v.getJSONArray("args")
        assertTrue(cases.length() >= 30)
        for (i in 0 until cases.length()) {
            val c = cases.getJSONObject(i)
            val id = checkNotNull(CapabilityId.fromWire(c.getString("capability")))
            val want = if (c.isNull("expect")) null else c.getString("expect")
            assertEquals(c.getString("name"), want, CapabilityArgsSpec.validate(id, JSONObject(c.getString("args"))))
        }
    }

    /** RelayCoordinator.handle up to the args pre-check (the entry limiter is covered by legacyEntryLimiter...). */
    @Test
    fun legacyAdmissionMatchesSharedVectors() {
        val self = v.getString("selfPackage")
        val cases = v.getJSONArray("admission")
        for (i in 0 until cases.length()) {
            val c = cases.getJSONObject(i)
            val name = c.getString("name")
            val registered = strings(c.optJSONArray("registered") ?: v.getJSONArray("registered")).toSet()
            val wire = c.getString("capability")
            val args = JSONObject(c.getString("args"))
            val id = CapabilityId.fromWire(wire)
            val got: Triple<RelayError, String, CapabilityId?>? = when {
                id == null || wire !in registered -> Triple(RelayError.CAPABILITY_NOT_IMPLEMENTED, wire, null)
                else -> {
                    val bringToFront = wire == CapabilityId.APP_LAUNCH.wire && args.length() == 1 && args.optString("package") == self
                    val selfArg = selfTargetArg(args, self)
                    if (selfArg != null && !bringToFront) {
                        Triple(
                            RelayError.GATE_HOST_DENIED,
                            "arg \"$selfArg\" names this app itself, and the assistant cannot read or act on the " +
                                "host app: drop that argument or name another app",
                            id,
                        )
                    } else {
                        CapabilityArgsSpec.validate(id, args)?.let { Triple(RelayError.TRANSPORT_MALFORMED, it, id) }
                    }
                }
            }
            if (c.isNull("expect")) {
                assertEquals("$name: admitted", null, got)
                continue
            }
            val e = c.getJSONObject("expect")
            checkNotNull(got) { "$name: admitted, expected a rejection" }
            assertEquals("$name code", e.getString("code"), got.first.code)
            assertEquals("$name reason", e.getString("reason"), got.second)
            assertEquals("$name retryPolicy", if (e.isNull("retryPolicy")) null else e.getString("retryPolicy"),
                got.third?.let { retryPolicyOf(it).name.lowercase() })
            assertEquals("$name effectState", e.getString("effectState"), effectStateFor(false, got.first).name.lowercase())
            assertEquals("$name consentKind", e.optString("consentKind").ifEmpty { null }, consentKindFor(got.first))
        }
    }

    @Test
    fun legacyEntryLimiterMatchesSharedVectors() {
        val rateLimiter = Class.forName("interlock.relay.core.interlock.RateLimiter")
        val companion = rateLimiter.getDeclaredField("Companion").apply { isAccessible = true }.get(null)
        val entryLimits = companion.javaClass.getMethod("getENTRY_LIMITS").invoke(companion)
        val cases = v.getJSONArray("rate")
        for (i in 0 until cases.length()) {
            val c = cases.getJSONObject(i)
            var clock = 0L
            val now: () -> Long = { clock }
            val limiter = rateLimiter.declaredConstructors.single().apply { isAccessible = true }.newInstance(now, entryLimits)
            val accepted = rateLimiter.getMethod("accepted", CapabilityId::class.java)
            val id = checkNotNull(CapabilityId.fromWire(c.getString("capability")))
            val events = c.getJSONArray("events")
            for (j in 0 until events.length()) {
                val e = events.getJSONObject(j)
                clock = e.getLong("at")
                repeat(e.getInt("repeat")) { k ->
                    assertEquals("${c.getString("name")} event $j #$k", e.getBoolean("accepted"), accepted.invoke(limiter, id) as Boolean)
                }
            }
        }
    }

    @Test
    fun legacyEncodingMatchesSharedVectorsStructurally() {
        val cases = v.getJSONArray("encode")
        for (i in 0 until cases.length()) {
            val c = cases.getJSONObject(i)
            val r = c.getJSONObject("response")
            val response = RelayResponse(
                id = r.getString("id"),
                ok = r.getBoolean("ok"),
                data = if (r.has("data")) JSONObject(r.getString("data")) else JSONObject(),
                artifacts = r.optJSONArray("artifacts")?.let { strings(it) } ?: emptyList(),
                surface = r.optString("surface").ifEmpty { null }?.let { checkNotNull(SurfaceKind.fromWire(it)) },
                degradedFrom = r.optString("degradedFrom").ifEmpty { null }?.let { checkNotNull(SurfaceKind.fromWire(it)) },
                reason = if (r.has("reason")) r.getString("reason") else null,
                degradeReason = if (r.has("degradeReason")) r.getString("degradeReason") else null,
                error = r.optString("error").ifEmpty { null }?.let { checkNotNull(RelayError.fromCode(it)) },
                elapsedMs = r.getLong("elapsedMs"),
                retryPolicy = r.optString("retryPolicy").ifEmpty { null }?.let { RetryPolicy.valueOf(it.uppercase()) },
                effectState = r.optString("effectState").ifEmpty { null }?.let { EffectState.valueOf(it.uppercase()) },
                consentKind = r.optString("consentKind").ifEmpty { null },
            )
            assertEquals(c.getString("name"), canon(JSONObject(c.getString("json"))), canon(JSONObject(EnvelopeCodec.encode(response))))
        }
        val control = v.getJSONArray("controlEncode")
        for (i in 0 until control.length()) {
            val c = control.getJSONObject(i)
            val text = EnvelopeCodec.encodeControl(c.getString("id"), c.getString("op"), c.getBoolean("ok"), JSONObject(c.getString("payload")))
            assertEquals(c.getString("name"), canon(JSONObject(c.getString("json"))), canon(JSONObject(text)))
        }
    }
}
