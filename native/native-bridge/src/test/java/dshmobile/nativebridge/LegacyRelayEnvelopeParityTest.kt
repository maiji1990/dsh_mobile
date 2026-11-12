package dshmobile.nativebridge

import interlock.relay.core.protocol.CapabilityId
import interlock.relay.core.protocol.RelayError
import interlock.relay.core.transport.ControlDecodeResult
import interlock.relay.core.transport.ControlRequest
import interlock.relay.core.transport.DecodeResult
import interlock.relay.core.transport.EnvelopeCodec
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test
import java.io.File

/**
 * PR-07 segment 2: the LEGACY Relay envelope decoder (DSHBox interlock-relay-core EnvelopeCodec) over the shared
 * "request" / "control" cases in tests/TestVectors/relay-envelope.json. The C# DshMobile.Runtime.Automation.Relay
 * RelayEnvelope runs the same cases (RelayEnvelopeParityTests). "csharpOnly" cases are skipped here: they follow
 * Android libcore org.json, which the Maven org.json on the JVM test classpath does differently.
 */
class LegacyRelayEnvelopeParityTest {
    private fun repo(): File {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "global.json").isFile) dir = dir.parentFile
        return checkNotNull(dir) { "repo root (global.json) not found" }
    }

    private val vectors by lazy {
        JSONObject(File(repo(), "tests/TestVectors/relay-envelope.json").readText(Charsets.UTF_8))
    }

    private fun text(c: JSONObject): String {
        val text = c.getString("text")
        if (!c.has("padTo")) return text
        val n = c.getInt("padTo")
        assertTrue("padTo shorter than the text", n >= text.length)
        return text.padEnd(n, ' ')
    }

    private fun assertRejected(name: String, r: JSONObject, id: String, error: RelayError, cause: String) {
        assertEquals("$name id", r.getString("id"), id)
        assertEquals("$name code", r.getString("code"), error.code)
        assertEquals("$name cause", r.getString("cause"), cause)
    }

    /** Structural value (the compile classpath is android.jar's org.json, which has no similar()). */
    private fun canon(v: Any?): Any? = when (v) {
        is JSONObject -> v.keys().asSequence().sorted().associateWith { canon(v.opt(it)) }
        is JSONArray -> (0 until v.length()).map { canon(v.opt(it)) }
        is Number -> v.toString()
        else -> v
    }

    private fun assertArgs(name: String, expected: JSONObject, actual: JSONObject) =
        assertEquals("$name args", canon(expected), canon(actual))

    @Test
    fun legacyRequestDecodingMatchesSharedVectors() {
        val cases = vectors.getJSONArray("request")
        assertTrue(cases.length() >= 30)
        for (i in 0 until cases.length()) {
            val c = cases.getJSONObject(i)
            val name = c.getString("name")
            val expect = c.getJSONObject("expect")
            when (val result = EnvelopeCodec.decodeRequest(text(c), c.getString("file"))) {
                is DecodeResult.Rejected -> {
                    val r = expect.optJSONObject("rejected") ?: fail("$name: rejected (${result.cause}), expected valid")
                    assertRejected(name, r as JSONObject, result.id, result.error, result.cause)
                }
                is DecodeResult.Valid -> {
                    val v = expect.optJSONObject("valid") ?: fail("$name: valid, expected rejected")
                    v as JSONObject
                    val req = result.request
                    assertEquals("$name id", v.getString("id"), req.id)
                    assertEquals("$name sentAtMs", v.getLong("sentAtMs"), req.sentAtMs)
                    assertEquals("$name capability", v.getString("capability"), req.capability)
                    assertArgs(name, v.getJSONObject("args"), req.args)
                    assertEquals("$name timeoutMs", v.getLong("timeoutMs"), req.timeoutMs)
                }
            }
        }
    }

    @Test
    fun legacyControlDecodingMatchesSharedVectors() {
        val cases = vectors.getJSONArray("control")
        assertTrue(cases.length() >= 30)
        for (i in 0 until cases.length()) {
            val c = cases.getJSONObject(i)
            val name = c.getString("name")
            val expect = c.getJSONObject("expect")
            when (val result = EnvelopeCodec.decodeControl(text(c), c.getString("file"))) {
                is ControlDecodeResult.Rejected -> {
                    val r = expect.optJSONObject("rejected") ?: fail("$name: rejected (${result.cause}), expected valid")
                    assertRejected(name, r as JSONObject, result.id, result.error, result.cause)
                }
                is ControlDecodeResult.Valid -> {
                    val v = expect.optJSONObject("valid") ?: fail("$name: valid, expected rejected")
                    v as JSONObject
                    val control = result.control
                    assertEquals("$name id", v.getString("id"), control.id)
                    when (val op = v.getString("op")) {
                        "health" -> assertTrue(name, control is ControlRequest.Health)
                        "status" -> assertEquals(name, v.getString("targetId"), (control as ControlRequest.Status).targetId)
                        "cancel" -> assertEquals(name, v.getString("targetId"), (control as ControlRequest.Cancel).targetId)
                        "submit" -> {
                            val s = control as ControlRequest.Submit
                            assertEquals(name, v.getString("capability"), s.capability.wire)
                            assertArgs(name, v.getJSONObject("args"), s.args)
                            assertEquals(name, v.getLong("hostTtlMs"), s.hostTtlMs)
                            assertEquals(name, v.getString("payloadDigest"), s.payloadDigest)
                        }
                        "ask" -> {
                            val a = control as ControlRequest.Ask
                            assertEquals(name, v.getString("question"), a.question)
                            val options = v.getJSONArray("options")
                            assertEquals(name, (0 until options.length()).map { options.getString(it) }, a.options)
                            assertEquals(name, v.getLong("timeoutMs"), a.timeoutMs)
                        }
                        else -> fail("$name: unknown op $op in the vector")
                    }
                }
            }
        }
    }

    @Test
    fun legacyTablesMatchSharedVectors() {
        val caps = vectors.getJSONArray("capabilities")
        assertEquals(CapabilityId.entries.map { it.wire }, (0 until caps.length()).map { caps.getString(it) })
        val errors = vectors.getJSONArray("errors")
        assertEquals(RelayError.entries.size, errors.length())
        RelayError.entries.forEachIndexed { i, e ->
            val v = errors.getJSONObject(i)
            assertEquals(v.getString("name"), e.name)
            assertEquals(v.getString("code"), e.code)
            assertEquals(v.getBoolean("retryable"), e.retryable)
            assertEquals(v.getInt("exitCode"), e.exitCode)
        }
    }
}
