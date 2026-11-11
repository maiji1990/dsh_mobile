package dshmobile.nativebridge

import com.dshbox.app.common.LogRedactor
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/** tests/TestVectors/log-redactor.json against the LEGACY DSHBox LogRedactor; the C# port runs the same file. */
class LegacyLogRedactorParityTest {
    @Test
    fun legacyRedactionMatchesSharedVectors() {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "global.json").isFile) dir = dir.parentFile
        val cases = JSONObject(File(checkNotNull(dir), "tests/TestVectors/log-redactor.json").readText()).getJSONArray("redact")
        assertTrue(cases.length() >= 8)
        for (i in 0 until cases.length()) {
            val c = cases.getJSONObject(i)
            assertEquals(c.getString("in"), c.getString("out"), LogRedactor.redact(c.getString("in")))
        }
    }
}
