using Android.Content;
using Android.Webkit;
using DshMobile.Core.Diagnostics;
using DshMobile.Core.WebUi;
using DshMobile.Runtime;
using DshMobile.Runtime.Hosting;

namespace DshMobile.App.WebUi;

/// <summary>
/// Hosts the official DSH WebUI for a C#-hosted DSH (<see cref="DshHost"/>), replacing the legacy Kotlin
/// WebUiController on that path. Same security posture as legacy:
/// <list type="bullet">
/// <item>the launch token is read from the host in memory and placed ONLY in the URL given to <c>LoadUrl</c>
/// (DSH exchanges it for a signed cookie); it is never logged, shown, or passed to the page;</item>
/// <item>navigation is limited to the exact loopback DSH origin (<see cref="DshOriginPolicy"/>); top-level http(s)
/// elsewhere goes to the user's browser; everything else is blocked;</item>
/// <item>no <c>addJavascriptInterface</c>, no file/content access, no mixed content, no third-party cookies;</item>
/// <item>a main-frame 401 re-exchanges the token once per token revision, never in a loop;</item>
/// <item>status callbacks carry paths / hosts / codes only - never query strings.</item>
/// </list>
/// Main thread only (WebView requirement).
/// </summary>
internal sealed class CSharpWebUiController(
    IDshTokenSource tokens,
    Action<string, string> onState,
    Action<string[], bool> onFileChooser,
    StartupTrace? trace = null)
{
    private readonly IDshTokenSource _tokens = tokens;
    private readonly Action<string, string> _onState = onState;
    private readonly Action<string[], bool> _onFileChooser = onFileChooser;
    private readonly StartupTrace? _trace = trace;
    private double? _loadMark;
    private readonly string _base = RuntimeConstants.DshBaseUrl + "/";
    private readonly DshOriginPolicy _policy = DshOriginPolicy.Create(RuntimeConstants.DshBaseUrl);
    private WebView? _view;
    private long? _loadedRevision;
    private long? _recoveredRevision;
    private IValueCallback? _pendingChooser;

    public void Attach(WebView view)
    {
        Detach();
        _view = view;
        var s = view.Settings;
        s.JavaScriptEnabled = true; // the DSH WebUI is a JS app
        s.DomStorageEnabled = true;
        s.AllowFileAccess = false;
        s.AllowContentAccess = false;
        s.MixedContentMode = MixedContentHandling.NeverAllow;
        s.SetSupportMultipleWindows(false);
        s.JavaScriptCanOpenWindowsAutomatically = false;
        s.MediaPlaybackRequiresUserGesture = true;
        var cookies = CookieManager.Instance!;
        cookies.SetAcceptCookie(true);
        cookies.SetAcceptThirdPartyCookies(view, false);
        // Deliberately no AddJavascriptInterface: the page gets no native API surface.
        view.SetWebViewClient(new Client(this));
        view.SetWebChromeClient(new Chrome(this));
        view.SetDownloadListener(new NoDownloads(this));
        _tokens.TokenChanged += OnTokenChanged;
        LoadIfToken();
    }

    public void Detach()
    {
        _tokens.TokenChanged -= OnTokenChanged;
        CancelChooser();
        if (_view is { } v)
        {
            v.StopLoading();
            v.SetWebViewClient(new WebViewClient());
            v.SetWebChromeClient(null);
            v.SetDownloadListener(null);
        }
        _view = null;
        _loadedRevision = null;
        _recoveredRevision = null;
    }

    public void Reload() => LoadIfToken(force: true);

    /// <summary>Answer to <see cref="onFileChooser"/>; extra or late answers are ignored.</summary>
    public void CompleteFileChooser(Android.Net.Uri[]? uris)
    {
        var cb = _pendingChooser;
        if (cb is null)
        {
            return;
        }
        _pendingChooser = null;
        var picked = uris is { Length: > 0 } ? uris : null;
        cb.OnReceiveValue(picked is null ? null : new Java.Lang.Object(Android.Runtime.JNIEnv.NewArray(picked), Android.Runtime.JniHandleOwnership.TransferLocalRef));
        if (picked is null)
        {
            _onState("FILE_CHOOSER_CANCELLED", "");
        }
    }

    private void OnTokenChanged(object? sender, EventArgs e) => _view?.Post(() => LoadIfToken());

    private void LoadIfToken(bool force = false)
    {
        if (_view is not { } view)
        {
            return;
        }
        var token = _tokens.Token;
        if (token is null)
        {
            _onState("WAITING_TOKEN", "DSH not running or token not parsed yet");
            return;
        }
        var revision = _tokens.TokenRevision;
        if (!force && revision == _loadedRevision)
        {
            return;
        }
        _loadedRevision = revision;
        _onState("LOADING", "/");
        _loadMark = _trace?.Mark();
        _trace?.EmitSpan("web.load_url", _loadMark!.Value, details: new Dictionary<string, object?> { ["revision"] = revision });
        view.LoadUrl(WebUiPolicy.UrlWithToken(_base, token));
    }

    private void CancelChooser()
    {
        _pendingChooser?.OnReceiveValue(null);
        _pendingChooser = null;
    }

    /// <summary>Path only: query strings (which may carry the token) never leave this class.</summary>
    private static string SafePath(Android.Net.Uri? uri) => string.IsNullOrEmpty(uri?.Path) ? "/" : uri.Path!;

    /// <summary>Timing for one main-frame document load; a navigation that starts a new load refreshes the mark.</summary>
    private void Mark(string stage, string? result = null, IReadOnlyDictionary<string, object?>? details = null)
    {
        if (_trace is { Enabled: true } && _loadMark is { } started)
        {
            _trace.EmitSpan(stage, started, result ?? StartupTrace.Ok, details);
        }
    }

    private sealed class Client(CSharpWebUiController owner) : WebViewClient
    {
        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            if (view is null || request?.Url is not { } url)
            {
                return true;
            }
            switch (WebUiPolicy.Navigate(owner._policy, url.ToString() ?? "", request.IsForMainFrame))
            {
                case WebUiNavigation.Allow:
                    return false;
                case WebUiNavigation.OpenExternally:
                    try
                    {
                        view.Context!.StartActivity(new Intent(Intent.ActionView, url).AddFlags(ActivityFlags.NewTask));
                        owner._onState("EXTERNAL_LINK", url.Host ?? "");
                    }
                    catch (ActivityNotFoundException)
                    {
                        owner._onState("BLOCKED_LINK", $"no browser for {url.Host}");
                    }
                    return true;
                default:
                    owner._onState("BLOCKED_LINK", url.Scheme ?? "unknown scheme");
                    return true;
            }
        }

        public override void OnPageStarted(WebView? view, string? url, Android.Graphics.Bitmap? favicon)
        {
            owner.Mark("web.page_start");
        }

        public override void OnPageCommitVisible(WebView? view, string? url) => owner.Mark("web.commit_visible");

        public override void OnPageFinished(WebView? view, string? url)
        {
            if (url is not null && owner._policy.IsTrusted(url))
            {
                owner.Mark("web.page_finish");
                owner._onState("LOADED", SafePath(Android.Net.Uri.Parse(url)));
            }
            else
            {
                owner.Mark("web.page_finish", StartupTrace.Failed);
            }
        }

        public override void OnReceivedHttpError(WebView? view, IWebResourceRequest? request, WebResourceResponse? errorResponse)
        {
            if (request is null || !request.IsForMainFrame || errorResponse?.StatusCode != 401)
            {
                return;
            }
            if (WebUiPolicy.ShouldRetryAfter401(owner._tokens.TokenRevision, ref owner._recoveredRevision))
            {
                owner.LoadIfToken(force: true);
            }
            else
            {
                owner._onState("UNAUTHORIZED", SafePath(request.Url));
            }
        }

        public override void OnReceivedError(WebView? view, IWebResourceRequest? request, WebResourceError? error)
        {
            if (request?.IsForMainFrame == true)
            {
                owner._onState("UNREACHABLE", $"{error?.ErrorCode} {error?.Description}");
            }
        }
    }

    private sealed class Chrome(CSharpWebUiController owner) : WebChromeClient
    {
        public override bool OnShowFileChooser(WebView? webView, IValueCallback? filePathCallback, FileChooserParams? fileChooserParams)
        {
            if (filePathCallback is null)
            {
                return false;
            }
            // One picker at a time: a new request supersedes (and cancels) an unanswered one.
            owner.CancelChooser();
            owner._pendingChooser = filePathCallback;
            var multiple = fileChooserParams?.Mode == ChromeFileChooserMode.OpenMultiple;
            owner._onFileChooser(fileChooserParams?.GetAcceptTypes() ?? [], multiple);
            return true;
        }
    }

    private sealed class NoDownloads(CSharpWebUiController owner) : Java.Lang.Object, IDownloadListener
    {
        public void OnDownloadStart(string? url, string? userAgent, string? contentDisposition, string? mimetype, long contentLength) =>
            owner._onState("DOWNLOAD_UNSUPPORTED", "downloads are not supported yet");
    }
}
