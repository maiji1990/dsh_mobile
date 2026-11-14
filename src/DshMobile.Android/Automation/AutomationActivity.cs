using Android.Content;
using Android.Views;
using DshMobile.App.Bridge;
using DshMobile.Application.Shadow;
using DshMobile.Core.Bridge;
using DshMobile.Runtime.Automation.Relay;
using DshMobile.Runtime.Automation.Relay.Shizuku;

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
    private Button _surfacePref = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _bridge = DshMobileApplication.Current.Bridge;

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(24, 16, 24, 16);
        root.AddView(new TextView(this) { Text = GetString(Resource.String.automation_explain) });
        _status = new TextView(this) { AccessibilityLiveRegion = AccessibilityLiveRegion.Polite };
        _status.SetPadding(0, 16, 0, 16);
        root.AddView(_status);

        var row1 = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        // ADR-0010: the user picks who serves the guest mailbox; never both at once.
        var start = Make(Resource.String.automation_start, ChooseRelayStart);
        var stop = Make(Resource.String.automation_stop, () => _ = StopRelayAsync());
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
        // ADR-0008: asks Shizuku to show its own grant dialog (the user answers it in Shizuku); never starts Shizuku.
        var shizuku = Make(Resource.String.automation_shizuku_connect, ConnectShizuku);
        root.AddView(shizuku);
        // PR-07 segment 2 shadow, developer action: only always-rejected requests, never anything that reaches approval.
        var probe = Make(Resource.String.automation_envelope_probe, () => _ = EnvelopeProbeAsync());
        root.AddView(probe);
        // PR-07 segment 7 shadow, developer action: only ui.snapshot / ui.node, each through the normal approval.
        var a11yRead = Make(Resource.String.automation_a11y_read_probe, () => _ = A11yReadProbeAsync());
        root.AddView(a11yRead);
        // PR-07 segment 4, developer action: Relay's own stored surface preference (the setting upstream's UI writes),
        // so the fallback path of the surface shadow can be checked. It never grants anything: without Shizuku and a
        // trusted display, "background preferred" falls back to the foreground and Relay says so.
        _surfacePref = new Button(this);
        _surfacePref.SetMinHeight((int)(48 * Resources!.DisplayMetrics!.Density));
        _surfacePref.Click += (_, _) => ToggleSurfacePreference();
        ShowSurfacePreference();
        root.AddView(_surfacePref);
        // PR-07 step 4, opt-in: the C# Relay host on its own mailbox (files/pilot-cs/run). The legacy Relay keeps
        // deciding for the guest; every C# write still asks the user on the C# approval card.
        _csRelay = Make(Resource.String.automation_cs_relay_start, ToggleCsRelay);
        root.AddView(_csRelay);
        _csPhase = new TextView(this) { AccessibilityLiveRegion = AccessibilityLiveRegion.Polite };
        root.AddView(_csPhase);
        ShowCsRelay();
        // PR-07 steps 1–4 shadow, developer action: the same calls to both relays, each through its own approval.
        var hostProbe = Make(Resource.String.automation_host_probe, () => _ = HostProbeAsync());
        root.AddView(hostProbe);
        _buttons = [start, stop, selftest, shizuku, probe, a11yRead, _surfacePref, _csRelay, hostProbe];

        // The page scrolls as a whole: the controls alone are taller than a phone screen, and a fixed layout pushed
        // the last buttons off it.
        _output = new TextView(this) { TextSize = 11 };
        _output.SetTextIsSelectable(true);
        _output.SetPadding(0, 16, 0, 0);
        root.AddView(_output);
        var page = new ScrollView(this) { FillViewport = true };
        page.SetFitsSystemWindows(true);
        // The selectable output is focusable in touch mode, so it used to take the window's initial focus; the
        // ScrollView then kept that focused view on screen as the output grew and scrolled the page by itself
        // while a self-test ran, which hid the top of the page under the app bar (the guest's ui_get_element
        // then correctly saw that node as not visible). The page itself takes focus first; tapping the output
        // still focuses it for selection.
        page.Focusable = true;
        page.FocusableInTouchMode = true;
        page.DescendantFocusability = DescendantFocusability.BeforeDescendants;
        page.AddView(root);
        SetContentView(page);
        page.RequestFocus();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _ = RefreshAsync(); // accessibility may have been toggled in system settings meanwhile
        WatchCsRelay(true);
        ShowCsRelay();
    }

    protected override void OnPause()
    {
        WatchCsRelay(false);
        base.OnPause();
    }

    private Button _csRelay = null!;
    private TextView _csPhase = null!;
    private Relay.RelayHostWatch? _csWatch;

    private void ChooseRelayStart() =>
        new AlertDialog.Builder(this)
            .SetTitle(Resource.String.automation_start_choice_title)!
            .SetMessage(Resource.String.automation_start_choice_message)!
            .SetPositiveButton(Resource.String.automation_start_legacy, (_, _) => _ = StartLegacyRelayAsync())!
            .SetNeutralButton(Resource.String.automation_start_cs, (_, _) => _ = StartCsGuestRelayAsync())!
            .Show();

    /// <summary>The legacy Relay takes the guest mailbox: a C# host on it (or on its own mailbox) is stopped first.</summary>
    private async Task StartLegacyRelayAsync()
    {
        if (Relay.CsRelay.GuestFacing)
        {
            Relay.CsRelay.StopGuest(_bridge.Layout.UserDataDir);
            _bridge.Note("c# relay (guest) stopped: legacy relay takes the mailbox");
        }
        ShowCsRelay();
        await Run(BridgeMethods.RelayStart);
    }

    /// <summary>
    /// ADR-0010: stop the legacy Relay (fail closed: if it does not stop, the C# host does not take the mailbox), then the
    /// C# host serves files/pilot and registers the ui_* layer.
    /// </summary>
    private async Task StartCsGuestRelayAsync()
    {
        Enable(false);
        var stopped = await _bridge.Client.ExecuteAsync(BridgeMethods.RelayStop, deadline: TimeSpan.FromSeconds(30));
        if (!stopped.Ok)
        {
            Enable(true);
            _output.Text = GetString(Resource.String.automation_cs_guest_legacy_stop_failed, new Java.Lang.String($"{stopped.Code}: {stopped.Message}"));
            return;
        }
        var registered = Relay.CsRelay.StartGuest(this, _bridge.Layout.UserDataDir);
        _bridge.Note($"c# relay (guest) start: legacy stopped, ui_* layer {(registered ? "registered" : "conflict")}");
        _output.Text = registered ? "" : GetString(Resource.String.automation_cs_guest_conflict);
        WatchCsRelay(true);
        ShowCsRelay();
        Enable(true);
        await RefreshAsync();
    }

    private async Task StopRelayAsync()
    {
        if (Relay.CsRelay.GuestFacing)
        {
            var removed = Relay.CsRelay.StopGuest(_bridge.Layout.UserDataDir);
            _bridge.Note($"c# relay (guest) stop; ui_* layer {(removed ? "removed" : "left (not ours)")}");
            ShowCsRelay();
            await RefreshAsync();
            return;
        }
        await Run(BridgeMethods.RelayStop);
    }

    private void ToggleCsRelay()
    {
        if (Relay.CsRelay.GuestFacing)
        {
            _output.Text = GetString(Resource.String.automation_cs_shadow_blocked);
            return;
        }
        if (Relay.CsRelay.Phase is RelayRuntimePhase.Stopped)
        {
            Relay.CsRelay.Start(this);
            _bridge.Note("c# relay start (opt-in)");
        }
        else
        {
            Relay.CsRelay.Stop();
            _bridge.Note("c# relay stop");
        }
        WatchCsRelay(true);
        ShowCsRelay();
    }

    private void WatchCsRelay(bool on)
    {
        _csWatch?.Dispose();
        _csWatch = on && Relay.CsRelay.Host is { } host ? new Relay.RelayHostWatch(host, () => RunOnUiThread(ShowCsRelay)) : null;
    }

    private void ShowCsRelay()
    {
        var host = Relay.CsRelay.Host;
        var phase = Relay.CsRelay.Phase;
        // This button is the shadow host's (own mailbox); the guest-facing host is started from "开启中继".
        var shadowUp = Relay.CsRelay.Mode == Relay.CsRelayMode.Shadow && phase is not RelayRuntimePhase.Stopped;
        _csRelay.SetText(shadowUp ? Resource.String.automation_cs_relay_stop : Resource.String.automation_cs_relay_start);
        var detail = phase.ToString()
            + (host?.IsChannelRunning == true ? " · 信箱运行中" : "")
            + (host?.DegradedReason is { } r ? " · " + r : "")
            + (host is not null && phase is not RelayRuntimePhase.Stopped ? " · " + host.Paths.Policy.GuestEntry : "");
        _csPhase.Text = GetString(Relay.CsRelay.GuestFacing ? Resource.String.automation_cs_guest_line : Resource.String.automation_cs_relay_phase,
            new Java.Lang.String(detail));
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
            ShizukuLine(ShizukuClient.Probe()),
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
        if (s.ChannelRunning)
        {
            // PR-07 segment 4 shadow (read-only): Relay's published surface decision against the C# policy.
            var runtime = _bridge.Runtime;
            var preference = StoredSurfacePreference();
            _ = Task.Run(() => runtime.ShadowRelaySurface(preference, (int)Android.OS.Build.VERSION.SdkInt));
            // PR-07 segment 5 shadow (read-only): Relay's published system state / tier / ceiling against the C# gate.
            var device = RelayDevice();
            var readPref = RelayPrefReader();
            _ = Task.Run(() => runtime.ShadowRelayGate(device, readPref));
        }
    }

    /// <summary>
    /// What Android tells this app directly about the facts Relay's system-state probe reads (upstream
    /// <c>SystemStateProbe</c>): the Settings lists, the runtime permissions the capability table names, the
    /// microphone app-op. Whether a service is bound right now is the legacy process's and is not read here.
    /// </summary>
    private RelayDeviceFacts RelayDevice()
    {
        var package = PackageName!;
        bool Listed(string key) =>
            (Android.Provider.Settings.Secure.GetString(ContentResolver, key) ?? "")
                .Split(':').Any(c => c.Contains(package, StringComparison.OrdinalIgnoreCase));
        var granted = RelayProtocol.Capabilities.SelectMany(RelayGatePolicy.RuntimePermissionsOf).Distinct()
            .Where(p => CheckSelfPermission(p) == Android.Content.PM.Permission.Granted)
            .ToHashSet(StringComparer.Ordinal);
        var micBlocked = false;
        try
        {
            var ops = (Android.App.AppOpsManager?)GetSystemService(AppOpsService);
#pragma warning disable CA1422 // same deprecated call as upstream (its replacement needs Android 14+)
            micBlocked = ops?.UnsafeCheckOpNoThrow(Android.App.AppOpsManager.OpstrRecordAudio!, Android.OS.Process.MyUid(), package)
                == Android.App.AppOpsManagerMode.Ignored;
#pragma warning restore CA1422
        }
        catch (Java.Lang.Exception)
        {
            // Upstream: getOrDefault(MODE_ALLOWED).
        }
        return new RelayDeviceFacts((int)Android.OS.Build.VERSION.SdkInt, Listed("enabled_accessibility_services"),
            Listed("enabled_notification_listeners"), granted, micBlocked);
    }

    /// <summary>Reads Relay's private "relay" preferences (tiers, surface preference); never writes.</summary>
    private Func<string, string?> RelayPrefReader()
    {
        var prefs = GetSharedPreferences(RelaySurfaces.PrefsFile, FileCreationMode.Private)!;
        return key => prefs.GetString(key, null);
    }

    /// <summary>Relay's stored preference (upstream <c>SurfacePreferences</c>: "relay" prefs, null = never set).</summary>
    private string? StoredSurfacePreference() =>
        GetSharedPreferences(RelaySurfaces.PrefsFile, FileCreationMode.Private)!
            .GetString(RelaySurfaces.PreferenceKey, null);

    private SurfacePreference CurrentSurfacePreference() =>
        RelaySurfaces.ParsePreference(StoredSurfacePreference());

    private string SurfacePreferenceLabel(SurfacePreference p) =>
        GetString(p == SurfacePreference.BackgroundPreferred
            ? Resource.String.automation_surface_pref_bg
            : Resource.String.automation_surface_pref_fg);

    private void ShowSurfacePreference() =>
        _surfacePref.Text = GetString(Resource.String.automation_surface_pref, new Java.Lang.String(SurfacePreferenceLabel(CurrentSurfacePreference())));

    private void ToggleSurfacePreference()
    {
        var next = CurrentSurfacePreference() == SurfacePreference.BackgroundPreferred
            ? SurfacePreference.Foreground
            : SurfacePreference.BackgroundPreferred;
        var editor = GetSharedPreferences(RelaySurfaces.PrefsFile, FileCreationMode.Private)!.Edit()!;
        editor.PutString(RelaySurfaces.PreferenceKey, next.Name());
        editor.Commit();
        ShowSurfacePreference();
        _output.Text = GetString(Resource.String.automation_surface_pref_changed, new Java.Lang.String(SurfacePreferenceLabel(next)));
        _bridge.Note($"relay surface preference -> {next.Name()} (developer)");
    }

    private string ShizukuLine(ShizukuProbe p) => ShizukuConnection.Decide(p) switch
    {
        ShizukuConnectState.Granted => GetString(Resource.String.automation_shizuku_granted, new Java.Lang.String(
            p.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)), new Java.Lang.String(p.Uid == 0 ? "root" : p.Uid == 2000 ? "shell" : p.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture))),
        ShizukuConnectState.CanRequest => GetString(Resource.String.automation_shizuku_not_granted),
        ShizukuConnectState.PermanentlyDenied => GetString(Resource.String.automation_shizuku_denied),
        ShizukuConnectState.Unsupported => GetString(Resource.String.automation_shizuku_unsupported),
        _ => GetString(Resource.String.automation_shizuku_not_running),
    };

    private void ConnectShizuku()
    {
        var state = ShizukuClient.RequestIfNeeded(out var probe);
        _output.Text = state == ShizukuConnectState.CanRequest ? GetString(Resource.String.automation_shizuku_requested) : ShizukuLine(probe);
        _bridge.Note($"shizuku connect: {state} (v{probe.Version}, uid {probe.Uid})");
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
        var package = PackageName!;
        var preference = StoredSurfacePreference();
        var sdk = (int)Android.OS.Build.VERSION.SdkInt;
        var device = RelayDevice();
        var readPref = RelayPrefReader();
        var reports = await Task.Run(async () => new[]
        {
            await runtime.ShadowRelayEnvelopeAsync(TimeSpan.FromSeconds(20), CancellationToken.None),
            await runtime.ShadowRelayAdmissionAsync(package, TimeSpan.FromSeconds(20), CancellationToken.None),
            runtime.ShadowRelaySurface(preference, sdk),
            runtime.ShadowRelayGate(device, readPref),
            await runtime.ShadowRelayGateProbeAsync(device, readPref, package, TimeSpan.FromSeconds(20), CancellationToken.None),
        });
        Enable(true);
        _output.Text = reports.All(r => r is null)
            ? GetString(Resource.String.automation_envelope_probe_off)
            : string.Join("\n\n", reports.OfType<ShadowReport>().Select(r => $"{r.Slice}: {r.Verdict}\n{r.Detail}"));
    }

    private async Task A11yReadProbeAsync()
    {
        // The user leaves this page to answer the cards; coming back refreshes the status, which must not re-enable
        // the buttons (a second tap would start an overlapping check) until this one has finished.
        _longProbe = true;
        Enable(false);
        _output.Text = GetString(Resource.String.automation_a11y_read_probe_running);
        var runtime = _bridge.Runtime;
        var context = ApplicationContext!;
        try
        {
            // Segment 7 (ui.snapshot / ui.node) then segment 8 (ui.waitFor): one card per capability, then none.
            var read = await Task.Run(() => runtime.ShadowRelayA11yReadAsync(() => RelayA11yTreeSource.HostTree(context),
                TimeSpan.FromMinutes(3), CancellationToken.None));
            _output.Text = read is null ? GetString(Resource.String.automation_envelope_probe_off) : $"{read.Slice}: {read.Verdict}\n{read.Detail}";
            var wait = await Task.Run(() => runtime.ShadowRelayA11yWaitForAsync(() => RelayA11yTreeSource.HostTree(context),
                TimeSpan.FromMinutes(2), CancellationToken.None));
            var reports = new[] { read, wait }.OfType<ShadowReport>().ToList();
            _output.Text = reports.Count == 0
                ? GetString(Resource.String.automation_envelope_probe_off)
                : string.Join("\n\n", reports.Select(r => $"{r.Slice}: {r.Verdict}\n{r.Detail}"));
        }
        catch (Exception ex)
        {
            // Never leave the page stuck with every button disabled: say what failed.
            Android.Util.Log.Error("DshMobile", $"a11y read probe failed: {ex}");
            _output.Text = $"automation.relay-a11y-read: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            _longProbe = false;
            Enable(true);
        }
    }

    private async Task HostProbeAsync()
    {
        if (Relay.CsRelay.GuestFacing)
        {
            _output.Text = GetString(Resource.String.automation_host_probe_guest_mode);
            return;
        }
        if (Relay.CsRelay.Host is not { Phase: RelayRuntimePhase.Running } host)
        {
            _output.Text = GetString(Resource.String.automation_host_probe_needs_cs);
            return;
        }
        _longProbe = true;
        Enable(false);
        _output.Text = GetString(Resource.String.automation_host_probe_running);
        var runtime = _bridge.Runtime;
        var entry = host.Paths.EntryDir;
        var guestEntry = host.Paths.GuestEntry;
        try
        {
            // ADR-0010, read-only and first (no approvals): the guest files each relay published.
            var files = runtime.ShadowRelayGuestFiles(entry, guestEntry);
            var head = files is null ? "" : $"{files.Slice}: {files.Verdict}\n{files.Detail}\n\n";
            _output.Text = head + GetString(Resource.String.automation_host_probe_running);
            var report = await Task.Run(() => runtime.ShadowRelayHostAsync(entry, TimeSpan.FromMinutes(15), CancellationToken.None));
            _output.Text = head + (report is null ? GetString(Resource.String.automation_envelope_probe_off) : $"{report.Slice}: {report.Verdict}\n{report.Detail}");
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("DshMobile", $"relay host probe failed: {ex}");
            _output.Text = $"automation.relay-host: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            _longProbe = false;
            Enable(true);
        }
    }

    private bool _longProbe;

    private void Enable(bool on)
    {
        foreach (var b in _buttons)
        {
            b.Enabled = on && !_longProbe;
        }
    }
}
