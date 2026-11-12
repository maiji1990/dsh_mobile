using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Android.Content;
using Android.Content.PM;
using Android.Views;
using Android.Webkit;
using AndroidX.AppCompat.App;
using DshMobile.App.Bridge;
using DshMobile.Application.Terminal;
using DshMobile.Runtime.Guest;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Tabs;
using Log = Android.Util.Log;

namespace DshMobile.App.Terminal;

/// <summary>
/// PR-06 C# terminal screen (developer opt-in: long-press 「终端」). Sessions are C#-owned PTYs
/// (<see cref="TerminalSessions"/>, guest login shell via libdshmobile_native); the emulator is xterm.js running from
/// local assets in a locked-down WebView. PTY bytes go over a dedicated WebMessagePort as base64 batches, never the JSON
/// bridge. Sessions outlive the screen: closing it only detaches the view.
/// </summary>
[Activity(
    Label = "@string/terminal_csharp_title",
    Theme = "@style/Theme.DshMobile.Material3",
    Exported = false,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.UiMode,
    WindowSoftInputMode = SoftInput.AdjustResize)]
public sealed class CSharpTerminalActivity : AppCompatActivity
{
    private const string Origin = "https://appassets.androidplatform.net";
    private const string PageUrl = Origin + "/terminal/index.html";
    private const int BatchBytes = 64 * 1024;
    private const int MenuNew = 1;
    private const int MenuClose = 2;

    private BridgeHost _bridge = null!;
    private TerminalSessions? _sessions;
    private TabLayout _tabs = null!;
    private WebView _web = null!;
    private TerminalInputView _keyboard = null!;
    private View _overlay = null!;
    private TextView _message = null!;
    private MaterialButton _ctrl = null!;
    private IMenuItem? _closeItem;
    private WebMessagePort? _port;
    private bool _ready;
    private bool _autoCreated;
    private bool _rendering;
    private bool _ctrlOn;
    private bool _appCursor;
    private int _cols;
    private int _rows;

    // Output: one generation per attachment; a stale pump never posts after a newer attachment's reset.
    private int _generation;
    private IDisposable? _attachment;
    private Channel<byte[]?>? _output;
    private string? _attachedId;

    // Input: one writer task so a full PTY never blocks the UI thread and keystrokes keep their order.
    private readonly Channel<(string Id, byte[] Data)> _input = Channel.CreateUnbounded<(string, byte[])>(new UnboundedChannelOptions { SingleReader = true });

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _bridge = DshMobileApplication.Current.Bridge;
        _sessions = _bridge.Runtime.Terminals;
        var dp = Resources!.DisplayMetrics!.Density;

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetFitsSystemWindows(true);

        var toolbar = new MaterialToolbar(this) { Title = GetString(Resource.String.terminal_csharp_title) };
        var menu = toolbar.Menu!;
        menu.Add(0, MenuNew, 0, GetString(Resource.String.terminal_new))!.SetShowAsAction(ShowAsAction.Always);
        _closeItem = menu.Add(0, MenuClose, 1, GetString(Resource.String.terminal_close))!;
        _closeItem.SetShowAsAction(ShowAsAction.Always);
        toolbar.MenuItemClick += (_, e) =>
        {
            if (e.Item?.ItemId == MenuNew)
            {
                _ = CreateSessionAsync();
            }
            else if (e.Item?.ItemId == MenuClose && _sessions?.ActiveId is { } id)
            {
                _ = _sessions.CloseAsync(id);
            }
        };
        root.AddView(toolbar);

        _tabs = new TabLayout(this) { TabMode = TabLayout.ModeScrollable };
        _tabs.TabSelected += (_, e) =>
        {
            if (!_rendering && e.Tab?.Tag?.ToString() is { } id)
            {
                _sessions?.Activate(id);
            }
        };
        root.AddView(_tabs);

        var body = new FrameLayout(this);
        // Keyboard input goes to a native view (see TerminalInputView); it only needs to exist and hold focus.
        _keyboard = new TerminalInputView(this);
        _keyboard.Typed += OnTyped;
        _keyboard.Key += (name, alt) => SendKey(name, alt);
        body.AddView(_keyboard, new FrameLayout.LayoutParams(1, 1, GravityFlags.Bottom | GravityFlags.Start));
        _web = CreateWebView();
        body.AddView(_web, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        var overlay = new LinearLayout(this) { Orientation = Orientation.Vertical };
        overlay.SetGravity(GravityFlags.Center);
        overlay.SetPadding((int)(24 * dp), (int)(24 * dp), (int)(24 * dp), (int)(24 * dp));
        _message = new TextView(this) { Text = GetString(Resource.String.terminal_no_session), Gravity = GravityFlags.Center };
        _message.SetTextIsSelectable(true);
        overlay.AddView(_message);
        var create = new MaterialButton(this) { Text = GetString(Resource.String.terminal_new_session) };
        create.Click += (_, _) => _ = CreateSessionAsync();
        overlay.AddView(create);
        _overlay = overlay;
        body.AddView(overlay, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        root.AddView(body, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        var keys = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        _ctrl = KeyButton("CTRL", () => SetCtrl(!_ctrlOn));
        _ctrl.Checkable = true;
        keys.AddView(_ctrl);
        foreach (var (label, key) in TerminalKeys.Bar)
        {
            keys.AddView(KeyButton(label, () => SendKey(key, alt: false)));
        }
        keys.AddView(KeyButton(GetString(Resource.String.terminal_paste), Paste));
        var keysScroll = new HorizontalScrollView(this);
        keysScroll.AddView(keys);
        root.AddView(keysScroll);

        SetContentView(root);
        _ = Task.Run(WriteInputAsync);

        if (_sessions is null)
        {
            ShowMessage(GetString(Resource.String.terminal_open_failed, new Java.Lang.String("RUNTIME_NOT_INSTALLED: " + GetString(Resource.String.terminal_no_runtime))));
            return;
        }
        _sessions.Changed += OnSessionsChanged;
        _web.LoadUrl(PageUrl);
        Render();
    }

    protected override void OnDestroy()
    {
        if (_sessions is not null)
        {
            _sessions.Changed -= OnSessionsChanged;
        }
        Detach(); // the shells keep running
        _input.Writer.TryComplete();
        _port?.Close();
        _port = null;
        _web.Destroy();
        base.OnDestroy();
    }

    // ── WebView ───────────────────────────────────────────────────────────

    private WebView CreateWebView()
    {
        var web = new WebView(this);
        var s = web.Settings!;
        s.JavaScriptEnabled = true;
        s.AllowFileAccess = false;
        s.AllowContentAccess = false;
        s.DomStorageEnabled = false;
        s.DatabaseEnabled = false;
        s.SetGeolocationEnabled(false);
        s.MixedContentMode = MixedContentHandling.NeverAllow;
        s.CacheMode = CacheModes.NoCache;
        web.SetBackgroundColor(Android.Graphics.Color.Black);
        web.SetWebViewClient(new LocalPageClient(this));
        // Display + touch only: the page never takes keyboard focus (xterm.js IME input is unreliable on Android).
        web.Focusable = false;
        web.FocusableInTouchMode = false;
        float downX = 0, downY = 0;
        var slop = Android.Views.ViewConfiguration.Get(this)!.ScaledTouchSlop;
        web.Touch += (_, e) =>
        {
            e.Handled = false; // the page still scrolls / selects
            var ev = e.Event!;
            if (ev.Action == MotionEventActions.Down)
            {
                (downX, downY) = (ev.GetX(), ev.GetY());
            }
            else if (ev.Action == MotionEventActions.Up && System.Math.Abs(ev.GetX() - downX) < slop && System.Math.Abs(ev.GetY() - downY) < slop)
            {
                _keyboard.ShowKeyboard();
            }
        };
        return web;
    }

    /// <summary>Hands the page its end of a fresh message channel (once per page load).</summary>
    private void ConnectPort()
    {
        _port?.Close();
        var ports = _web.CreateWebMessageChannel()!;
        _port = ports[0];
        _port.SetWebMessageCallback(new PortCallback(this));
        _web.PostWebMessage(new WebMessage("dshm-port", [ports[1]]), Android.Net.Uri.Parse(Origin)!);
    }

    private void OnPageMessage(string? json)
    {
        if (string.IsNullOrEmpty(json) || _sessions is null)
        {
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var m = doc.RootElement;
            switch (m.GetProperty("t").GetString())
            {
                case "ready":
                    _cols = m.GetProperty("cols").GetInt32();
                    _rows = m.GetProperty("rows").GetInt32();
                    _ready = true;
                    _bridge.Note($"{TerminalSessions.Slice}: view ready {_cols}x{_rows}");
                    if (!_autoCreated && _sessions.Snapshot().Count == 0)
                    {
                        _autoCreated = true;
                        _ = CreateSessionAsync();
                    }
                    Render();
                    break;
                case "in":
                    // The emulator's own replies (cursor position, device attributes …); keyboard input comes from _keyboard.
                    Send(Encoding.UTF8.GetBytes(m.GetProperty("d").GetString() ?? ""));
                    break;
                case "bin":
                    Send(Encoding.Latin1.GetBytes(m.GetProperty("d").GetString() ?? ""));
                    break;
                case "resize":
                    _cols = m.GetProperty("cols").GetInt32();
                    _rows = m.GetProperty("rows").GetInt32();
                    if (_attachedId is { } id)
                    {
                        _ = _sessions.ResizeAsync(id, _cols, _rows, CancellationToken.None);
                    }
                    break;
                case "mode":
                    _appCursor = m.GetProperty("appCursor").GetBoolean();
                    break;
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Warn(BridgeHost.LogTag, $"{TerminalSessions.Slice}: bad page message: {e.Message}");
        }
    }

    // ── sessions ──────────────────────────────────────────────────────────

    private async Task CreateSessionAsync()
    {
        if (_sessions is null)
        {
            return;
        }
        try
        {
            await _sessions.CreateAsync(_rows > 0 ? _rows : 24, _cols > 0 ? _cols : 80, CancellationToken.None).ConfigureAwait(false);
        }
        catch (GuestPtyException e)
        {
            _bridge.Note($"{TerminalSessions.Slice}: open FAILED {e.Message}");
            RunOnUiThread(() => ShowMessage(GetString(Resource.String.terminal_open_failed, new Java.Lang.String(e.Message))));
        }
    }

    private void OnSessionsChanged() => RunOnUiThread(() =>
    {
        if (!IsDestroyed)
        {
            Render();
        }
    });

    private void Render()
    {
        if (_sessions is null)
        {
            return;
        }
        var list = _sessions.Snapshot();
        _rendering = true;
        try
        {
            _tabs.RemoveAllTabs();
            foreach (var s in list)
            {
                var tab = _tabs.NewTab().SetText(Label(s))!;
                tab.SetTag(new Java.Lang.String(s.Id));
                tab.SetContentDescription((s.Active ? "当前会话 " : "会话 ") + Label(s));
                _tabs.AddTab(tab, s.Active);
            }
        }
        finally
        {
            _rendering = false;
        }
        var empty = list.Count == 0;
        _tabs.Visibility = empty ? ViewStates.Gone : ViewStates.Visible;
        _overlay.Visibility = empty ? ViewStates.Visible : ViewStates.Gone;
        // Invisible, not Gone: the page keeps its size so xterm.js can measure columns and rows.
        _web.Visibility = empty ? ViewStates.Invisible : ViewStates.Visible;
        _closeItem?.SetEnabled(!empty);
        if (empty)
        {
            Detach();
        }
        else if (_ready)
        {
            AttachActive();
        }
    }

    private static string Label(TerminalSessionInfo s) =>
        s.Title + (s.Exited ? $" ({(s.Signal is { } sig ? "signal " + sig.ToString(System.Globalization.CultureInfo.InvariantCulture) : s.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture))})" : "");

    /// <summary>UI thread. Shows the active session: reset the emulator, replay its kept output, then follow it live.</summary>
    private void AttachActive()
    {
        var id = _sessions?.ActiveId;
        if (id is null || id == _attachedId)
        {
            return;
        }
        Detach();
        var gen = ++_generation;
        var output = Channel.CreateUnbounded<byte[]?>(new UnboundedChannelOptions { SingleReader = true });
        output.Writer.TryWrite(null); // reset before the replay
        _attachment = _sessions!.Attach(id, b => output.Writer.TryWrite(b));
        _output = output;
        _attachedId = id;
        _appCursor = false;
        _ = Task.Run(() => PumpToPageAsync(output.Reader, gen));
        if (_cols > 0 && _rows > 0)
        {
            _ = _sessions.ResizeAsync(id, _cols, _rows, CancellationToken.None);
        }
        _keyboard.RequestFocus();
        _bridge.Note($"{TerminalSessions.Slice}: view attached to session {id}");
    }

    private void Detach()
    {
        _attachment?.Dispose();
        _attachment = null;
        _output?.Writer.TryComplete();
        _output = null;
        _attachedId = null;
        _generation++;
    }

    private async Task PumpToPageAsync(ChannelReader<byte[]?> reader, int gen)
    {
        var batch = new MemoryStream();
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var item))
            {
                if (item is null)
                {
                    Flush(batch, gen);
                    Post("{\"t\":\"reset\"}", gen);
                    continue;
                }
                batch.Write(item);
                if (batch.Length >= BatchBytes)
                {
                    Flush(batch, gen);
                }
            }
            Flush(batch, gen);
        }
    }

    private void Flush(MemoryStream batch, int gen)
    {
        if (batch.Length == 0)
        {
            return;
        }
        var b64 = Convert.ToBase64String(batch.GetBuffer(), 0, (int)batch.Length);
        batch.SetLength(0);
        Post("{\"t\":\"out\",\"b\":\"" + b64 + "\"}", gen);
    }

    private void Post(string json, int gen) => RunOnUiThread(() =>
    {
        if (gen == _generation && _port is not null && !IsDestroyed)
        {
            _port.PostMessage(new WebMessage(json));
        }
    });

    // ── input ─────────────────────────────────────────────────────────────

    private void Send(byte[] data)
    {
        if (_attachedId is { } id && data.Length > 0)
        {
            _input.Writer.TryWrite((id, data));
        }
    }

    private async Task WriteInputAsync()
    {
        await foreach (var (id, data) in _input.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            _sessions?.Write(id, data);
        }
    }

    private void OnTyped(string typed)
    {
        if (_ctrlOn)
        {
            var (output, released) = TerminalKeys.ApplyCtrl(typed);
            typed = output;
            if (released)
            {
                SetCtrl(false);
            }
        }
        Send(Encoding.UTF8.GetBytes(typed));
    }

    private void SendKey(string key, bool alt)
    {
        if (TerminalKeys.Sequence(key, _appCursor) is { } seq)
        {
            Send(Encoding.ASCII.GetBytes(alt ? "\u001b" + seq : seq));
        }
    }

    private void SetCtrl(bool on)
    {
        _ctrlOn = on;
        _ctrl.Checked = on;
        _ctrl.ContentDescription = on ? "CTRL 已锁定（下一个字母）" : "CTRL";
    }

    private void Paste()
    {
        var clipboard = (ClipboardManager?)GetSystemService(ClipboardService);
        var text = clipboard?.PrimaryClip is { ItemCount: > 0 } clip ? clip.GetItemAt(0)?.CoerceToText(this) : null;
        if (!string.IsNullOrEmpty(text) && _port is not null)
        {
            // xterm.js applies bracketed paste when the shell enabled it, then hands the text back as input.
            _port.PostMessage(new WebMessage("{\"t\":\"paste\",\"d\":\"" + JsonEncodedText.Encode(text) + "\"}"));
        }
    }

    // ── views ─────────────────────────────────────────────────────────────

    private MaterialButton KeyButton(string label, Action onClick)
    {
        var b = new MaterialButton(this, null, Resource.Attribute.materialButtonOutlinedStyle)
        {
            Text = label,
            Focusable = false, // keep keyboard focus in the terminal
        };
        b.SetAllCaps(false);
        b.SetMinWidth(0);
        b.SetMinimumWidth((int)(56 * Resources!.DisplayMetrics!.Density));
        b.SetPadding((int)(8 * Resources.DisplayMetrics.Density), 0, (int)(8 * Resources.DisplayMetrics.Density), 0);
        var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        lp.SetMargins((int)(2 * Resources.DisplayMetrics.Density), 0, (int)(2 * Resources.DisplayMetrics.Density), 0);
        b.LayoutParameters = lp;
        b.Click += (_, _) => onClick();
        return b;
    }

    private void ShowMessage(string text)
    {
        _message.Text = text;
        _overlay.Visibility = ViewStates.Visible;
    }

    /// <summary>Serves only <c>assets/terminal/**</c> at <see cref="Origin"/>; refuses every other request and navigation.</summary>
    private sealed class LocalPageClient(CSharpTerminalActivity owner) : WebViewClient
    {
        public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request)
        {
            var url = request?.Url;
            var path = url?.Path ?? "";
            if (url?.Scheme == "https" && url.Host == "appassets.androidplatform.net"
                && path.StartsWith("/terminal/", StringComparison.Ordinal) && !path.Contains("..", StringComparison.Ordinal))
            {
                try
                {
                    var stream = owner.Assets!.Open(path.TrimStart('/'));
                    return new WebResourceResponse(MimeOf(path), "utf-8", stream);
                }
                catch (Java.IO.IOException)
                {
                    // not shipped (e.g. *.js.map): fall through to 404
                }
                return Refuse(404, "Not Found");
            }
            return Refuse(403, "Forbidden");
        }

        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request) => true;

        public override void OnPageFinished(WebView? view, string? url)
        {
            base.OnPageFinished(view, url);
            if (url == PageUrl)
            {
                owner.ConnectPort();
            }
        }

        private static WebResourceResponse Refuse(int status, string reason) =>
            new("text/plain", "utf-8", status, reason, new Dictionary<string, string>(), new MemoryStream());

        private static string MimeOf(string path) => Path.GetExtension(path) switch
        {
            ".html" => "text/html",
            ".js" => "text/javascript",
            ".css" => "text/css",
            _ => "application/octet-stream",
        };
    }

    private sealed class PortCallback(CSharpTerminalActivity owner) : WebMessagePort.WebMessageCallback
    {
        public override void OnMessage(WebMessagePort? port, WebMessage? message)
        {
            if (!owner.IsDestroyed)
            {
                owner.OnPageMessage(message?.Data);
            }
        }
    }
}
