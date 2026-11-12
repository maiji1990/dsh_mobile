using Android.Content;
using Android.Views;
using DshMobile.App.Bridge;
using DshMobile.Application.Shadow;
using DshMobile.Core.Bridge;

namespace DshMobile.App.Automation;

/// <summary>
/// Mobile automation (TASK-010, ADR-0004), experimental and off by default. Starts/stops the Interlock Relay
/// execution layer, links to system accessibility settings (the user enables the service; the app never does),
/// runs a fixed read-only self-test (relay doctor + one ui.snapshot) through the real guest mailbox, and (developer)
/// the PR-07 envelope probe: always-rejected requests into the live mailbox, answers compared with the C# decoder.
/// </summary>
[Activity(Label = "@string/automation_title", Exported = false)]
public sealed class AutomationActivity : Activity
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
        root.AddView(new TextView(this) { Text = GetString(Resource.String.automation_explain) });
        _status = new TextView(this) { AccessibilityLiveRegion = AccessibilityLiveRegion.Polite };
        _status.SetPadding(0, 16, 0, 16);
        root.AddView(_status);

        var row1 = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var start = Make(Resource.String.automation_start, () => _ = Run(BridgeMethods.RelayStart));
        var stop = Make(Resource.String.automation_stop, () => _ = Run(BridgeMethods.RelayStop));
        row1.AddView(start, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        row1.AddView(stop, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        root.AddView(row1);
        var row2 = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var settings = Make(Resource.String.automation_a11y_settings,
            () => StartActivity(new Intent(Android.Provider.Settings.ActionAccessibilitySettings)));
        var selftest = Make(Resource.String.automation_selftest, () => _ = SelfTestAsync());
        row2.AddView(settings, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        row2.AddView(selftest, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        root.AddView(row2);
        // PR-07 segment 2 shadow, developer action: only always-rejected requests, never anything that reaches approval.
        var probe = Make(Resource.String.automation_envelope_probe, () => _ = EnvelopeProbeAsync());
        root.AddView(probe);
        _buttons = [start, stop, selftest, probe];

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
        _ = RefreshAsync(); // accessibility may have been toggled in system settings meanwhile
    }

    private Button Make(int text, Action onClick)
    {
        var b = new Button(this);
        b.SetText(text);
        b.SetMinHeight((int)(48 * Resources!.DisplayMetrics!.Density));
        b.Click += (_, _) => onClick();
        return b;
    }

    private Task RefreshAsync() => Run(BridgeMethods.RelayStatus);

    private async Task Run(string method)
    {
        Enable(false);
        var resp = await _bridge.Client.ExecuteAsync(method, deadline: TimeSpan.FromSeconds(30));
        Enable(true);
        if (!resp.Ok)
        {
            _status.Text = $"{method} FAILED {resp.Code}: {resp.Message}";
            return;
        }
        var s = BridgeCodec.ReadPayload(resp, BridgeJsonContext.Default.RelayStatus)!;
        _status.Text = string.Join('\n',
            $"Relay: {s.Phase}{(s.ChannelRunning ? " · 信箱运行中" : "")}{(s.DegradedReason is { } r ? " · " + r : "")}",
            GetString(s.AccessibilityEnabled ? Resource.String.automation_a11y_on : Resource.String.automation_a11y_off),
            $"允许的能力：{string.Join(", ", s.Allowed)}",
            $"guest CLI：{s.GuestCli}",
            UiToolsLine(s.UiTools));
        _bridge.Note($"{method}: phase={s.Phase} channel={s.ChannelRunning} a11y={s.AccessibilityEnabled}");
        if (s.UiTools is { } tools)
        {
            // PR-07 shadow (read-only): the C# port reads the same layer and setting, compared in the background.
            var legacy = new UiToolsLegacyView(method, tools.Registered, tools.Conflict, s.AccessibilityEnabled, s.Allowed, s.GuestCli, tools.ServerName);
            var enabled = Android.Provider.Settings.Secure.GetString(ContentResolver, Android.Provider.Settings.Secure.EnabledAccessibilityServices);
            var runtime = _bridge.Runtime;
            var package = PackageName!;
            _ = Task.Run(() => runtime.ShadowUiTools(legacy, enabled, package));
        }
    }

    /// <summary>Says plainly whether DSH has the ui_* tools now; never implies they are live when DSH has not reloaded.</summary>
    private string UiToolsLine(UiToolsStatus? t)
    {
        if (t is null)
        {
            return GetString(Resource.String.automation_tools_unknown);
        }
        if (t.Conflict)
        {
            return GetString(Resource.String.automation_tools_conflict);
        }
        if (t.DshRestartNeeded)
        {
            return GetString(t.Registered ? Resource.String.automation_tools_restart_on : Resource.String.automation_tools_restart_off);
        }
        if (t.LoadedByDsh == true)
        {
            return GetString(Resource.String.automation_tools_live);
        }
        return GetString(t.Registered ? Resource.String.automation_tools_registered : Resource.String.automation_tools_off);
    }

    private async Task SelfTestAsync()
    {
        Enable(false);
        _output.Text = GetString(Resource.String.automation_selftest_running);
        var resp = await _bridge.Client.ExecuteAsync(BridgeMethods.RelaySelftest, deadline: TimeSpan.FromMinutes(2));
        Enable(true);
        if (!resp.Ok)
        {
            _output.Text = $"selftest FAILED {resp.Code}: {resp.Message}";
            return;
        }
        var r = BridgeCodec.ReadPayload(resp, BridgeJsonContext.Default.PluginCommandResult)!;
        _output.Text = $"exit={r.ExitCode?.ToString() ?? "?"}\n\n{r.Output}";
        _bridge.Note($"relay.selftest exit={r.ExitCode}");
    }

    private async Task EnvelopeProbeAsync()
    {
        Enable(false);
        _output.Text = GetString(Resource.String.automation_envelope_probe_running);
        var runtime = _bridge.Runtime;
        var report = await Task.Run(() => runtime.ShadowRelayEnvelopeAsync(TimeSpan.FromSeconds(20), CancellationToken.None));
        Enable(true);
        _output.Text = report is null
            ? GetString(Resource.String.automation_envelope_probe_off)
            : $"{report.Slice}: {report.Verdict}\n\n{report.Detail}";
    }

    private void Enable(bool on)
    {
        foreach (var b in _buttons)
        {
            b.Enabled = on;
        }
    }
}
