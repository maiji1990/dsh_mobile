using Android.Views;
using DshMobile.App.Bridge;
using DshMobile.Core.Bridge;

namespace DshMobile.App.Browser;

/// <summary>
/// Browser automation (TASK-011, ADR-0005). Opens DSHBox's browser runtime (library BrowserActivity in the
/// <c>:browser</c> process, separate cookies/storage), turns the <c>browser_*</c> tools for DSH on/off (DSHBox's own
/// browser-use installer, off by default), and runs a fixed read-only self-test through the adapter's client.
/// AI control of the browser is always granted by the user inside the browser itself; nothing here grants it.
/// </summary>
[Activity(Label = "@string/dm_browser_title", Exported = false)]
public sealed class BrowserToolsActivity : Activity
{
    private BridgeHost _bridge = null!;
    private TextView _status = null!;
    private TextView _output = null!;
    private Button[] _buttons = [];

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _bridge = DshMobileApplication.Current.Bridge;

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetFitsSystemWindows(true);
        root.SetPadding(24, 16, 24, 16);
        root.AddView(new TextView(this) { Text = GetString(Resource.String.dm_browser_explain) });
        _status = new TextView(this) { AccessibilityLiveRegion = AccessibilityLiveRegion.Polite };
        _status.SetPadding(0, 16, 0, 16);
        root.AddView(_status);

        var open = Make(Resource.String.dm_browser_open, () => _ = RunStatus(BridgeMethods.BrowserOpen));
        var selftest = Make(Resource.String.dm_browser_selftest, () => _ = RunCommand(BridgeMethods.BrowserSelftest, Resource.String.dm_browser_selftest_running));
        var enable = Make(Resource.String.dm_browser_tools_enable, ConfirmEnable);
        var disable = Make(Resource.String.dm_browser_tools_disable, () => _ = RunCommand(BridgeMethods.BrowserToolsDisable, Resource.String.dm_browser_working));
        root.AddView(Row(open, selftest));
        root.AddView(Row(enable, disable));
        _buttons = [open, selftest, enable, disable];

        _output = new TextView(this) { TextSize = 11 };
        _output.SetTextIsSelectable(true);
        var scroll = new ScrollView(this);
        scroll.AddView(_output);
        root.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
        SetContentView(root);
    }

    protected override void OnResume()
    {
        base.OnResume();
        _ = RunStatus(BridgeMethods.BrowserStatus);
    }

    private LinearLayout Row(Button a, Button b)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.AddView(a, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        row.AddView(b, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        return row;
    }

    private Button Make(int text, Action onClick)
    {
        var b = new Button(this);
        b.SetText(text);
        b.SetMinHeight((int)(48 * Resources!.DisplayMetrics!.Density));
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>Third-party code in the DSH profile: say so before installing (same rule as the plugin page).</summary>
    private void ConfirmEnable()
    {
        new AlertDialog.Builder(this)
            .SetTitle(Resource.String.dm_browser_tools_enable)!
            .SetMessage(Resource.String.dm_browser_tools_enable_warning)!
            .SetPositiveButton(Resource.String.dm_browser_tools_enable, (_, _) => _ = RunCommand(BridgeMethods.BrowserToolsEnable, Resource.String.dm_browser_working))!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .Show();
    }

    private async Task RunStatus(string method)
    {
        Enable(false);
        var resp = await _bridge.Client.ExecuteAsync(method, deadline: TimeSpan.FromSeconds(30));
        Enable(true);
        if (!resp.Ok)
        {
            _status.Text = $"{method} FAILED {resp.Code}: {resp.Message}";
            return;
        }
        Show(BridgeCodec.ReadPayload(resp, BridgeJsonContext.Default.BrowserStatusInfo)!);
        _bridge.Note($"{method}: ok");
    }

    private void Show(BrowserStatusInfo s)
    {
        var broker = s.BrokerRunning ? GetString(Resource.String.dm_browser_broker_on)
            : s.SandboxState == "RUNNING" ? GetString(Resource.String.dm_browser_broker_off_error)
            : GetString(Resource.String.dm_browser_broker_off);
        var lines = new List<string>
        {
            broker + (s.LastError is { } e ? " · " + e : ""),
            $"guest CLI：{s.GuestCli}{(s.CliReady ? "" : GetString(Resource.String.dm_browser_cli_missing))}",
            ToolsLine(s),
        };
        // The adapter only logs failures; show them as they are instead of implying the tools are fine.
        if (s.AdapterLogLines is { Length: > 0 } logged)
        {
            lines.Add(GetString(Resource.String.dm_browser_adapter_log));
            lines.AddRange(logged);
        }
        _status.Text = string.Join('\n', lines);
    }

    private string ToolsLine(BrowserStatusInfo s)
    {
        if (s.DshRestartNeeded)
        {
            return GetString(s.AdapterInstalled ? Resource.String.dm_browser_tools_restart_on : Resource.String.dm_browser_tools_restart_off);
        }
        if (s.ToolsLoadedByDsh == true)
        {
            return GetString(Resource.String.dm_browser_tools_live);
        }
        return GetString(s.AdapterInstalled ? Resource.String.dm_browser_tools_installed : Resource.String.dm_browser_tools_off);
    }

    private async Task RunCommand(string method, int runningText)
    {
        Enable(false);
        _output.Text = GetString(runningText);
        var resp = await _bridge.Client.ExecuteAsync(method, deadline: TimeSpan.FromMinutes(3));
        Enable(true);
        if (!resp.Ok)
        {
            _output.Text = $"{method} FAILED {resp.Code}: {resp.Message}";
            return;
        }
        var r = BridgeCodec.ReadPayload(resp, BridgeJsonContext.Default.PluginCommandResult)!;
        _output.Text = $"{method}: exit={r.ExitCode?.ToString() ?? "?"}{(r.Error is { } err ? " · " + err : "")}\n\n{r.Output}";
        _bridge.Note($"{method} exit={r.ExitCode}");
        await RunStatus(BridgeMethods.BrowserStatus);
    }

    private void Enable(bool on)
    {
        foreach (var b in _buttons)
        {
            b.Enabled = on;
        }
    }
}
