package dshmobile.nativebridge

import org.junit.Assert.assertEquals
import org.junit.Test

class WebUiUrlTest {
    private fun url(base: String, token: String?) = WebUiController.urlWithToken(base, token)

    @Test
    fun noToken_keepsBase() {
        assertEquals("http://127.0.0.1:3080/", url("http://127.0.0.1:3080/", null))
        assertEquals("http://127.0.0.1:3080/", url("http://127.0.0.1:3080/", ""))
    }

    @Test
    fun appendsEncodedToken() {
        assertEquals("http://127.0.0.1:3080/?token=a%2Bb%2Fc%3D", url("http://127.0.0.1:3080/", "a+b/c="))
        assertEquals("http://127.0.0.1:3080/?token=Ab-_.~9", url("http://127.0.0.1:3080/", "Ab-_.~9"))
    }

    @Test
    fun replacesExistingTokenAndKeepsOtherParams() {
        assertEquals(
            "http://127.0.0.1:3080/s?x=1&token=new",
            url("http://127.0.0.1:3080/s?token=old&x=1", "new"),
        )
    }

    @Test
    fun ampersandInTokenCannotInjectParams() {
        assertEquals("http://127.0.0.1:3080/?token=x%26admin%3D1", url("http://127.0.0.1:3080/", "x&admin=1"))
    }
}
