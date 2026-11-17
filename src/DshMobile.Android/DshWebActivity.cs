using Android.Content;
using Android.Content.PM;
using DshMobile.App.Files;
using Android.Views;
using Android.Webkit;
using AndroidX.Activity;
using AndroidX.AppCompat.App;
using DshMobile.App.Bridge;
using DshMobile.App.Ui;
using DshMobile.Core.Diagnostics;
using DshMobile.App.WebUi;
using Google.Android.Material.ProgressIndicator;
using Log = Android.Util.Log;

namespace DshMobile.App;

/// <summary>
/// Full-screen host for the official DSH WebUI (TASK-006, MIGRATION row 25). Loading, the launch-token exchange,
/// navigation policy and 401 recovery live in <see cref="CSharpWebUiController"/>: this class never sees the token.
/// ADR-0021: edge-to-edge with a Material status banner that disappears once the page has loaded.
/// </summary>
[Activity(
    Label = "@string/dsh_title",
    Exported = false,
    // Keep the WebView (and DSH's in-page state) across rotation / keyboard / dark-mode changes.
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.UiMode,
    WindowSoftInputMode = SoftInput.AdjustResize)]
public sealed class DshWebActivity : AppCompatActivity
{
    private WebView? _webView;
    private TextView? _status;
    private View? _banner;
    private LinearProgressIndicator? _progress;
    private CSharpWebUiController? _csharp;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Observability only (docs/STARTUP_PERF.md): DSH being READY does not mean the page is loaded or usable, so the
        // WebView load gets its own run id and its own records.
        var trace = UiPrefs.StartupTraceEnabled(this) ? StartupTrace.Start(line => Log.Info(BridgeHost.LogTag, line)) : StartupTrace.Disabled;
        var activitySpan = trace.Measure("web.activity_create");

        Edges.Enable(Window!);
        SetContentView(Resource.Layout.activity_web);
        Edges.Pad(FindViewById(Resource.Id.screen_root)!);
        _banner = FindViewById(Resource.Id.web_banner);
        _progress = FindViewById<LinearProgressIndicator>(Resource.Id.web_progress);
        _status = FindViewById<TextView>(Resource.Id.web_status);
        _webView = FindViewById<WebView>(Resource.Id.web_view)!;
        ShowState("LOADING", "");

        var runtime = DshMobileApplication.Current.Bridge.Runtime;
        DshMobile.Runtime.Hosting.IDshTokenSource tokens = runtime.Host is { } host ? host : NoDshToken.Instance;
        Log.Info(BridgeHost.LogTag, runtime.HostOwnsProcesses ? "webui: C# WebView on the C#-hosted DSH" : "webui: C# WebView (DSH not running)");
        _csharp = new CSharpWebUiController(tokens,
            (state, detail) => RunOnUiThread(() =>
            {
                // detail is token-free by construction (paths, hosts, error codes only).
                Log.Info(BridgeHost.LogTag, $"webui {state} {detail}");
                if (!IsDestroyed)
                {
                    ShowState(state, detail);
                }
            }),
            (types, multiple) => RunOnUiThread(() =>
            {
                if (IsDestroyed)
                {
                    _csharp?.CompleteFileChooser(null);
                }
                else
                {
                    OpenFileChooser(types, multiple);
                }
            }),
            trace);
        _csharp.Attach(_webView);
        activitySpan.End();

        // Predictive back (default for targetSdk 36+): navigate WebView history first, then leave.
        OnBackPressedDispatcher.AddCallback(this, new BackCallback(this));
    }

    protected override void OnDestroy()
    {
        _csharp?.Detach();
        _csharp = null;
        if (_webView is not null)
        {
            ((ViewGroup?)_webView.Parent)?.RemoveView(_webView);
            _webView.Destroy();
            _webView = null;
        }
        base.OnDestroy();
    }

    private const int RequestFileChooser = 51;

    private void OpenFileChooser(string[] acceptTypes, bool multiple)
    {
        try
        {
            // The user's own pick is the consent for this upload; the page never sees anything else.
            StartActivityForResult(DocumentPicker.OpenIntent(acceptTypes, multiple), RequestFileChooser);
        }
        catch (ActivityNotFoundException)
        {
            CompleteChooser(null);
        }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == RequestFileChooser)
        {
            var uris = resultCode == Result.Ok ? DocumentPicker.ResultUris(data) : [];
            Log.Info(BridgeHost.LogTag, $"webui file chooser: {uris.Length} file(s)");
            CompleteChooser(uris.Length == 0 ? null : uris);
        }
    }

    private void CompleteChooser(Android.Net.Uri[]? uris) => _csharp?.CompleteFileChooser(uris);

    private void GoBack()
    {
        if (_webView?.CanGoBack() == true)
        {
            _webView.GoBack();
        }
        else
        {
            Finish();
        }
    }

    private void ShowState(string state, string detail)
    {
        // LOADED hides the bar so the WebUI gets the whole screen; anything else stays visible.
        _banner!.Visibility = state == "LOADED" ? ViewStates.Gone : ViewStates.Visible;
        var waiting = state is "WAITING_TOKEN" or "LOADING";
        _progress!.Visibility = waiting ? ViewStates.Visible : ViewStates.Gone;
        _banner.SetBackgroundColor(new Android.Graphics.Color(Rows.Color(_banner,
            waiting ? Resource.Attribute.colorSurfaceContainerHigh : Resource.Attribute.colorErrorContainer)));
        _status!.SetTextColor(new Android.Graphics.Color(Rows.Color(_status,
            waiting ? Resource.Attribute.colorOnSurface : Resource.Attribute.colorOnErrorContainer)));
        _status.Text = state switch
        {
            "WAITING_TOKEN" => GetString(Resource.String.web_waiting),
            "LOADING" => GetString(Resource.String.web_loading),
            "UNAUTHORIZED" => GetString(Resource.String.web_unauthorized),
            "UNREACHABLE" => GetString(Resource.String.web_unreachable) + " " + detail,
            _ => $"{state} {detail}",
        };
    }

    /// <summary>No C# host yet in this process: no token, so the page says DSH is not running.</summary>
    private sealed class NoDshToken : DshMobile.Runtime.Hosting.IDshTokenSource
    {
        public static NoDshToken Instance { get; } = new();

        public string? Token => null;

        public long? TokenRevision => null;

        public event EventHandler? TokenChanged
        {
            add { }
            remove { }
        }
    }

    private sealed class BackCallback(DshWebActivity owner) : OnBackPressedCallback(true)
    {
        public override void HandleOnBackPressed() => owner.GoBack();
    }
}
