using System.Text.Json;
using Android.Content.PM;
using Android.Graphics;
using Android.Views;
using DshMobile.App.Bridge;
using DshMobile.NativeBridge;
using Log = Android.Util.Log;

namespace DshMobile.App.Terminal;

/// <summary>
/// Terminal screen (TASK-008). Hosts DSHBox's native TerminalView (Termux emulator + PRoot shell session);
/// PTY data never passes through managed code. This class only lays out session tabs and an extra-keys bar.
/// Sessions outlive the Activity: closing the screen detaches the View, it does not kill shells.
/// </summary>
[Activity(
    Label = "@string/terminal_title",
    Exported = false,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.UiMode,
    WindowSoftInputMode = SoftInput.AdjustResize)]
public sealed class TerminalActivity : Activity
{
    private BridgeHost _bridge = null!;
    private LinearLayout _tabs = null!;
    private Button _ctrl = null!;
    private View? _terminal;
    private TextView? _emptyHint;
    private SessionsListener? _listener;
    private bool _ctrlOn;

    private static readonly (string Label, string Key)[] ExtraKeys =
    [
        ("ESC", "ESC"), ("TAB", "TAB"), ("↑", "UP"), ("↓", "DOWN"), ("←", "LEFT"), ("→", "RIGHT"),
        ("HOME", "HOME"), ("END", "END"), ("PGUP", "PGUP"), ("PGDN", "PGDN"), ("^C", "CTRL_C"),
    ];

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _bridge = DshMobileApplication.Current.Bridge;

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetFitsSystemWindows(true);
        root.SetBackgroundColor(Color.Black);

        _tabs = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var tabsScroll = new HorizontalScrollView(this);
        tabsScroll.AddView(_tabs);
        root.AddView(tabsScroll);

        _listener = new SessionsListener(this);
        _terminal = _bridge.CreateTerminalView(this, _listener);
        if (_terminal is null)
        {
            var missing = new TextView(this) { Text = GetString(Resource.String.terminal_no_runtime) };
            missing.SetTextColor(Color.White);
            missing.SetPadding(32, 32, 32, 32);
            root.AddView(missing, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
            SetContentView(root);
            return;
        }
        root.AddView(_terminal, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
        // Shown instead of the View when every session has been closed (the View would show a dead session).
        _emptyHint = new TextView(this) { Text = GetString(Resource.String.terminal_no_session), Visibility = ViewStates.Gone };
        _emptyHint.SetTextColor(Color.White);
        _emptyHint.SetPadding(32, 32, 32, 32);
        root.AddView(_emptyHint, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        var keys = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        _ctrl = KeyButton("CTRL", () =>
        {
            _ctrlOn = !_ctrlOn;
            _bridge.SetTerminalCtrl(_ctrlOn);
            RenderCtrl();
        });
        keys.AddView(_ctrl);
        foreach (var (label, key) in ExtraKeys)
        {
            keys.AddView(KeyButton(label, () => _bridge.TerminalSendKey(key)));
        }
        keys.AddView(KeyButton(GetString(Resource.String.terminal_paste), _bridge.TerminalPaste));
        var keysScroll = new HorizontalScrollView(this);
        keysScroll.AddView(keys);
        root.AddView(keysScroll);

        SetContentView(root);
        _terminal.RequestFocus();
        RenderTabs(_bridge.TerminalSessionsJson());
    }

    protected override void OnDestroy()
    {
        _bridge.ReleaseTerminalView(); // detach only; shells keep running
        _terminal = null;
        base.OnDestroy();
    }

    private Button KeyButton(string label, Action onClick)
    {
        var b = new Button(this) { Text = label, Focusable = false }; // keep keyboard focus on the terminal
        b.SetAllCaps(false);
        b.SetMinWidth(0);
        b.SetMinimumWidth((int)(56 * Resources!.DisplayMetrics!.Density));
        b.SetMinHeight((int)(48 * Resources.DisplayMetrics.Density));
        b.Click += (_, _) => onClick();
        return b;
    }

    private void RenderCtrl() => _ctrl.Alpha = _ctrlOn ? 1f : 0.6f;

    private void RenderTabs(string? json)
    {
        _tabs.RemoveAllViews();
        try
        {
            using var doc = JsonDocument.Parse(json ?? "[]");
            var empty = doc.RootElement.GetArrayLength() == 0;
            if (_terminal is not null && _emptyHint is not null)
            {
                _terminal.Visibility = empty ? ViewStates.Gone : ViewStates.Visible;
                _emptyHint.Visibility = empty ? ViewStates.Visible : ViewStates.Gone;
            }
            foreach (var s in doc.RootElement.EnumerateArray())
            {
                var id = s.GetProperty("id").GetString()!;
                var title = s.GetProperty("title").GetString();
                var active = s.GetProperty("active").GetBoolean();
                var exited = s.GetProperty("exited").GetBoolean();
                var label = (string.IsNullOrEmpty(title) ? id : title) + (exited ? $" ({s.GetProperty("exitCode").GetInt32()})" : "");
                var tab = KeyButton(label, () => _bridge.ActivateTerminalSession(id));
                tab.Alpha = active ? 1f : 0.6f;
                tab.ContentDescription = (active ? "当前会话 " : "会话 ") + label;
                _tabs.AddView(tab);
            }
        }
        catch (JsonException e)
        {
            Log.Warn(BridgeHost.LogTag, "terminal sessions json: " + e.Message);
        }
        _tabs.AddView(KeyButton("+", () => _bridge.NewTerminalSession()));
        _tabs.AddView(KeyButton("×", CloseActive));
    }

    private void CloseActive()
    {
        try
        {
            using var doc = JsonDocument.Parse(_bridge.TerminalSessionsJson() ?? "[]");
            foreach (var s in doc.RootElement.EnumerateArray())
            {
                if (s.GetProperty("active").GetBoolean())
                {
                    _bridge.CloseTerminalSession(s.GetProperty("id").GetString()!);
                    return;
                }
            }
        }
        catch (JsonException)
        {
        }
    }

    private sealed class SessionsListener(TerminalActivity owner) : Java.Lang.Object, ITerminalListener
    {
        public void OnTerminalSessionsChanged(string? json)
        {
            Log.Info(BridgeHost.LogTag, "terminal sessions " + json);
            if (!owner.IsDestroyed)
            {
                owner.RenderTabs(json);
            }
        }
    }
}
