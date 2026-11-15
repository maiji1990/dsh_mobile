using Android.Content;
using Android.Content.PM;
using DshMobile.App.Files;
using Android.Views;
using Android.Webkit;
using DshMobile.App.Bridge;
using DshMobile.App.WebUi;
using DshMobile.NativeBridge;
using Log = Android.Util.Log;

namespace DshMobile.App;

/// <summary>
/// Full-screen host for the official DSH WebUI (TASK-006). The WebView is created here, but loading,
/// the launch-token exchange, navigation policy and 401 recovery all live in the native WebUiController:
/// this class never sees the token.
/// </summary>
[Activity(
    Label = "@string/dsh_title",
    Exported = false,
    // Keep the WebView (and DSH's in-page state) across rotation / keyboard / dark-mode changes.
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.UiMode,
    WindowSoftInputMode = SoftInput.AdjustResize)]
public sealed class DshWebActivity : Activity
{
    private WebView? _webView;
    private TextView? _status;
    private StateListener? _listener;
    private BackCallback? _back;
    /// <summary>Set when DSH is hosted by the C# DshHost (ADR-0007): the C# WebView path replaces the legacy one.</summary>
    private CSharpWebUiController? _csharp;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetFitsSystemWindows(true);
        _status = new TextView(this);
        _status.SetPadding(24, 12, 24, 12);
        _status.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
        _webView = new WebView(this);
        root.AddView(_status, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        root.AddView(_webView, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
        SetContentView(root);

        var runtime = DshMobileApplication.Current.Bridge.Runtime;
        // ADR-0015: whenever the C# host owns the processes (DSH given up included), the legacy WebView path knows nothing.
        // MIGRATION row 25: the C# WebView is also the default when nothing runs; only a DSH started by the legacy runtime
        // (fallback, in this process) is shown through the legacy WebUiController.
        if (runtime.HostOwnsProcesses || !runtime.LegacyRuntimeActive)
        {
            DshMobile.Runtime.Hosting.IDshTokenSource tokens = runtime.Host is { } host ? host : NoDshToken.Instance;
            Log.Info(BridgeHost.LogTag, runtime.HostOwnsProcesses ? "webui: C# WebView on the C#-hosted DSH" : "webui: C# WebView (DSH not running)");
            _csharp = new CSharpWebUiController(tokens,
                (state, detail) => RunOnUiThread(() =>
                {
                    // detail is token-free by construction (paths, hosts, error codes only).
                    Log.Info(BridgeHost.LogTag, $"webui(csharp) {state} {detail}");
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
                }));
            _csharp.Attach(_webView);
        }
        else
        {
            _listener = new StateListener(this);
            DshMobileApplication.Current.Bridge.AttachWebUi(_webView, _listener);
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            // Predictive back (default for targetSdk 36+): navigate WebView history first, then leave.
            _back = new BackCallback(this);
            OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(0, _back);
        }
    }

    protected override void OnDestroy()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && _back is not null)
        {
            OnBackInvokedDispatcher.UnregisterOnBackInvokedCallback(_back);
        }
        if (_csharp is not null)
        {
            _csharp.Detach();
            _csharp = null;
        }
        else
        {
            DshMobileApplication.Current.Bridge.DetachWebUi();
        }
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

    private void CompleteChooser(Android.Net.Uri[]? uris)
    {
        if (_csharp is not null)
        {
            _csharp.CompleteFileChooser(uris);
        }
        else
        {
            DshMobileApplication.Current.Bridge.CompleteWebFileChooser(uris);
        }
    }

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
        _status!.Visibility = state == "LOADED" ? ViewStates.Gone : ViewStates.Visible;
        _status.Text = state switch
        {
            "WAITING_TOKEN" => GetString(Resource.String.web_waiting),
            "LOADING" => GetString(Resource.String.web_loading),
            "UNAUTHORIZED" => GetString(Resource.String.web_unauthorized),
            "UNREACHABLE" => GetString(Resource.String.web_unreachable) + " " + detail,
            _ => $"{state} {detail}",
        };
    }

    private sealed class StateListener(DshWebActivity owner) : Java.Lang.Object, IWebUiListener
    {
        public void OnWebUiState(string? state, string? detail)
        {
            // detail is token-free by contract (paths, hosts, error codes only).
            Log.Info(BridgeHost.LogTag, $"webui {state} {detail}");
            owner.RunOnUiThread(() =>
            {
                if (!owner.IsDestroyed)
                {
                    owner.ShowState(state ?? "", detail ?? "");
                }
            });
        }

        public void OnFileChooserRequested(string[]? acceptTypes, bool allowMultiple) =>
            owner.RunOnUiThread(() =>
            {
                if (owner.IsDestroyed)
                {
                    DshMobileApplication.Current.Bridge.CompleteWebFileChooser(null);
                }
                else
                {
                    owner.OpenFileChooser(acceptTypes ?? [], allowMultiple);
                }
            });
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

    private sealed class BackCallback(DshWebActivity owner) : Java.Lang.Object, Android.Window.IOnBackInvokedCallback
    {
        public void OnBackInvoked() => owner.GoBack();
    }
}
