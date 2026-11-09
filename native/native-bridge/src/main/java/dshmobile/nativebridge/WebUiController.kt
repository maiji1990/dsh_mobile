package dshmobile.nativebridge

import android.annotation.SuppressLint
import android.content.ActivityNotFoundException
import android.content.Intent
import android.net.Uri
import android.webkit.CookieManager
import android.webkit.ValueCallback
import android.webkit.WebChromeClient
import android.webkit.WebResourceError
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient
import com.dshbox.app.sandbox.SandboxManager
import com.dshbox.dsh.renderer.api.DshOriginPolicy
import com.dshbox.dsh.renderer.api.DshTokenRevisionTracker
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch

/**
 * Hosts the official DSH WebUI in a System WebView handed over from .NET (TASK-006).
 *
 * The DSH launch token stays inside this class: it is read from DSHBox's SandboxManager, placed only in
 * the URL handed to WebView.loadUrl (DSH exchanges it for a signed cookie), and never reported to the
 * listener, logged, or sent over the JSON control plane.
 *
 * Reuses DSHBox `dsh-renderer:api` (unmodified): [DshOriginPolicy] for the loopback-only navigation
 * allowlist and [DshTokenRevisionTracker] for reload-on-token-change. 401 recovery is bounded to one
 * retry per token revision (DSHBox's full DshNavigationAuthStateMachine needs per-document status
 * observation that System WebView does not expose; adopting it is a follow-up).
 *
 * All methods must run on the main thread (WebView requirement).
 */
internal class WebUiController(
    private val manager: SandboxManager,
    baseUrl: String,
    private val scope: CoroutineScope,
) {
    private val base = baseUrl.trimEnd('/') + "/"
    private val policy = DshOriginPolicy.create(base)
    private val tracker = DshTokenRevisionTracker()
    private var webView: WebView? = null
    private var listener: WebUiListener? = null
    private var tokenJob: Job? = null
    private var recoveredRevision: Long? = null

    @SuppressLint("SetJavaScriptEnabled")
    fun attach(view: WebView, l: WebUiListener?) {
        detach()
        webView = view
        listener = l
        view.settings.apply {
            javaScriptEnabled = true // the DSH WebUI is a JS app
            domStorageEnabled = true
            allowFileAccess = false
            allowContentAccess = false
            mixedContentMode = WebSettings.MIXED_CONTENT_NEVER_ALLOW
            setSupportMultipleWindows(false)
            javaScriptCanOpenWindowsAutomatically = false
            mediaPlaybackRequiresUserGesture = true
        }
        CookieManager.getInstance().apply {
            setAcceptCookie(true)
            setAcceptThirdPartyCookies(view, false)
        }
        // Deliberately no addJavascriptInterface: the page gets no native API surface.
        view.webViewClient = Client()
        view.webChromeClient = Chrome()
        view.setDownloadListener { _, _, _, _, _ -> emit("DOWNLOAD_UNSUPPORTED", "downloads arrive with TASK-007") }

        emit("WAITING_TOKEN", "")
        tokenJob = scope.launch(Dispatchers.Main) {
            manager.dshLaunchToken.collect { token ->
                if (tracker.update(token)) {
                    if (tracker.token != null) load() else emit("WAITING_TOKEN", "DSH not running or token not parsed yet")
                }
            }
        }
    }

    fun detach() {
        tokenJob?.cancel()
        tokenJob = null
        webView?.let {
            it.stopLoading()
            it.webViewClient = WebViewClient()
            it.webChromeClient = null
            it.setDownloadListener(null)
        }
        webView = null
        listener?.onWebUiState("DETACHED", "")
        listener = null
        tracker.clearForRelease()
        recoveredRevision = null
    }

    fun reload() {
        if (tracker.token != null) load() else emit("WAITING_TOKEN", "")
    }

    private fun load() {
        val view = webView ?: return
        emit("LOADING", "/")
        view.loadUrl(urlWithToken(base, tracker.token))
    }

    private fun emit(state: String, detail: String) {
        runCatching { listener?.onWebUiState(state, detail) }
    }

    /** Path only — query strings (which may carry the token) never leave this class. */
    private fun safePath(uri: Uri?): String = uri?.path?.ifEmpty { "/" } ?: "/"

    private inner class Client : WebViewClient() {
        override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean {
            val url = request.url
            if (policy.isTrusted(url.toString())) return false
            val scheme = url.scheme?.lowercase()
            if (request.isForMainFrame && (scheme == "http" || scheme == "https")) {
                // External site: hand it to the user's browser instead of this privileged origin's WebView.
                try {
                    view.context.startActivity(Intent(Intent.ACTION_VIEW, url).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                    emit("EXTERNAL_LINK", url.host ?: "")
                } catch (e: ActivityNotFoundException) {
                    emit("BLOCKED_LINK", "no browser for ${url.host}")
                }
            } else {
                emit("BLOCKED_LINK", scheme ?: "unknown scheme")
            }
            return true
        }

        override fun onPageFinished(view: WebView, url: String?) {
            if (url != null && policy.isTrusted(url)) emit("LOADED", safePath(Uri.parse(url)))
        }

        override fun onReceivedHttpError(view: WebView, request: WebResourceRequest, response: WebResourceResponse) {
            if (!request.isForMainFrame || response.statusCode != 401) return
            val revision = tracker.revision
            if (revision != null && revision != recoveredRevision) {
                // One re-exchange per token revision; a second 401 on the same token is reported, not looped.
                recoveredRevision = revision
                load()
            } else {
                emit("UNAUTHORIZED", safePath(request.url))
            }
        }

        override fun onReceivedError(view: WebView, request: WebResourceRequest, error: WebResourceError) {
            if (request.isForMainFrame) emit("UNREACHABLE", "${error.errorCode} ${error.description}")
        }
    }

    private inner class Chrome : WebChromeClient() {
        override fun onShowFileChooser(
            webView: WebView,
            filePathCallback: ValueCallback<Array<Uri>>,
            fileChooserParams: FileChooserParams,
        ): Boolean {
            filePathCallback.onReceiveValue(null) // explicit cancel; SAF picker arrives with TASK-007
            emit("FILE_CHOOSER_UNSUPPORTED", "attachments arrive with TASK-007")
            return true
        }
    }

    companion object {
        private const val UNRESERVED_EXTRA = "-_.~"

        /**
         * `base?token=<encoded>` replacing any existing token parameter. Ported from DSHBox
         * `app/.../webview/DshUrl.kt` (GPL-3.0, v1.5.1); same encoding rules.
         */
        fun urlWithToken(base: String, token: String?): String {
            if (token.isNullOrEmpty()) return base
            val encoded = buildString {
                for (c in token) {
                    when {
                        c.isLetterOrDigit() || c in UNRESERVED_EXTRA -> append(c)
                        c.code < 0x80 -> append('%').append("%02X".format(c.code))
                        else -> append(c)
                    }
                }
            }
            val queryStart = base.indexOf('?')
            if (queryStart < 0) return "$base?token=$encoded"
            val path = base.substring(0, queryStart)
            val kept = base.substring(queryStart + 1).split('&').filter { it.isNotEmpty() && !it.startsWith("token=") }
            return "$path?" + (kept + "token=$encoded").joinToString("&")
        }
    }
}
