using Android.Content;
using Android.Views;
using AndroidX.Core.Widget;
using DshMobile.App.Bridge;
using DshMobile.App.LocalNetwork;
using DshMobile.App.Ui;
using DshMobile.Application.LocalNetwork;
using DshMobile.Runtime.Automation;
using DshMobile.Runtime.Automation.Relay;
using DshMobile.Runtime.Automation.Relay.Interlock;
using DshMobile.Runtime.Automation.Relay.Shizuku;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.MaterialSwitch;
using Google.Android.Material.ProgressIndicator;

namespace DshMobile.App.Automation;

/// <summary>
/// 手机操控页（TASK-010, ADR-0004 / ADR-0010 / UI Slice 4）：
/// 全 XML 化布局（<c>activity_automation.xml</c>），严守页顶 <c>automation_approval_slot</c>、<c>FLAG_SECURE</c>、
/// <c>PageCardShown</c> 生命周期与权限只读引导边界；前置权限清单支持胶囊态直观自检。
/// </summary>
[Activity(Label = "@string/automation_title", Exported = false)]
public sealed class AutomationActivity : DshActivity
{
    private BridgeHost _bridge = null!;
    private LinearProgressIndicator _progress = null!;
    private NestedScrollView _page = null!;
    private FrameLayout _cardSlot = null!;
    private TextView _introText = null!;
    private MaterialButton _introToggle = null!;
    private bool _introExpanded;

    private View _relayRow = null!;
    private MaterialSwitch _relaySwitch = null!;
    private bool _settingSwitch;
    private View _a11yRow = null!;
    private View _overlayRow = null!;
    private View _lanRow = null!;
    private View _shizukuRow = null!;
    private View _toolsRow = null!;
    private View _detailsDivider = null!;
    private View _detailsRow = null!;
    private View _selftestRow = null!;
    private View _outputDivider = null!;
    private View _outputCard = null!;
    private TextView _output = null!;
    private View? _surfacePrefRow;
    private View[] _actions = [];

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetScreen(Resource.Layout.activity_automation, Resource.String.automation_title);
        _bridge = DshMobileApplication.Current.Bridge;
        _progress = FindViewById<LinearProgressIndicator>(Resource.Id.automation_progress)!;
        _page = FindViewById<NestedScrollView>(Resource.Id.automation_scroll)!;
        _cardSlot = FindViewById<FrameLayout>(Resource.Id.automation_approval_slot)!;

        _introText = FindViewById<TextView>(Resource.Id.automation_intro_text)!;
        _introToggle = FindViewById<MaterialButton>(Resource.Id.automation_intro_toggle)!;
        _introToggle.Click += (_, _) =>
        {
            _introExpanded = !_introExpanded;
            _introText.SetText(_introExpanded ? Resource.String.automation_explain : Resource.String.automation_intro);
            _introToggle.SetText(_introExpanded ? Resource.String.automation_less : Resource.String.automation_more);
        };

        _relayRow = FindViewById(Resource.Id.automation_row_relay)!;
        _relaySwitch = Rows.BindSwitchRow(_relayRow, Resource.Drawable.ic_phone, GetString(Resource.String.automation_relay),
            GetString(Resource.String.automation_relay_card_sub), false, checkedState =>
            {
                if (_settingSwitch)
                {
                    return;
                }
                _ = checkedState ? StartRelayAsync() : StopRelayAsync();
            });

        _a11yRow = FindViewById(Resource.Id.automation_row_a11y)!;
        Rows.BindNavRow(_a11yRow, Resource.Drawable.ic_lock, GetString(Resource.String.automation_a11y), null, OpenAccessibilitySettings);

        _overlayRow = FindViewById(Resource.Id.automation_row_overlay)!;
        Rows.BindNavRow(_overlayRow, Resource.Drawable.ic_open_in_new, GetString(Resource.String.automation_overlay), null, OpenOverlaySettings);

        _lanRow = FindViewById(Resource.Id.automation_row_lan)!;
        Rows.BindNavRow(_lanRow, Resource.Drawable.ic_lan, GetString(Resource.String.lan_title), null, () => _ = RequestLanAsync());

        _shizukuRow = FindViewById(Resource.Id.automation_row_shizuku)!;
        Rows.BindNavRow(_shizukuRow, Resource.Drawable.ic_key, GetString(Resource.String.automation_shizuku), null, ConnectShizuku);

        _toolsRow = FindViewById(Resource.Id.automation_row_tools)!;
        Rows.BindActionRow(_toolsRow, Resource.Drawable.ic_extension, GetString(Resource.String.automation_tools));

        _detailsDivider = FindViewById(Resource.Id.automation_details_divider)!;
        _detailsRow = FindViewById(Resource.Id.automation_row_details)!;
        Rows.BindActionRow(_detailsRow, Resource.Drawable.ic_info, GetString(Resource.String.automation_details));

        _selftestRow = FindViewById(Resource.Id.automation_row_selftest)!;
        Rows.BindActionRow(_selftestRow, Resource.Drawable.ic_check_circle, GetString(Resource.String.automation_selftest),
            GetString(Resource.String.automation_selftest_sub), () => _ = SelfTestAsync());
        _outputDivider = FindViewById(Resource.Id.automation_output_divider)!;
        _outputCard = FindViewById(Resource.Id.automation_output_card)!;
        _output = FindViewById<TextView>(Resource.Id.automation_output)!;

        if (UiPrefs.DeveloperEnabled(this))
        {
            FindViewById(Resource.Id.automation_dev_header)!.Visibility = ViewStates.Visible;
            FindViewById(Resource.Id.automation_dev_card)!.Visibility = ViewStates.Visible;
            _surfacePrefRow = FindViewById(Resource.Id.automation_row_surface_pref)!;
            Rows.BindActionRow(_surfacePrefRow, Resource.Drawable.ic_build, "", null, ToggleSurfacePreference);
            ShowSurfacePreference();
        }

        _actions = _surfacePrefRow is { } sp
            ? [_relayRow, _relaySwitch, _selftestRow, _shizukuRow, sp]
            : [_relayRow, _relaySwitch, _selftestRow, _shizukuRow];

        // 防止只读输出框在自检刷新时抢占焦点导致顶部被 AppBar 遮挡
        _page.Focusable = true;
        _page.FocusableInTouchMode = true;
        _page.DescendantFocusability = DescendantFocusability.BeforeDescendants;
        _page.RequestFocus();

        ShowCsRelay();
        ShowLanPermission();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _ = RefreshAsync();
        WatchCsRelay(true);
        ShowCsRelay();
        ShowOverlayPermission();
        ShowLanPermission();
        Relay.CsRelay.PageCardShown = true;
    }

    protected override void OnPause()
    {
        Relay.CsRelay.PageCardShown = false;
        WatchCsRelay(false);
        base.OnPause();
    }

    private Relay.ApprovalCardView? _card;
    private int _cardRequestId;
    private AndroidX.AppCompat.App.AlertDialog? _gapDialog;
    private RelayHost.AccessibilityNoticeInfo? _gapShown;
    private Relay.RelayHostWatch? _csWatch;
    private RelayHost? _watchedHost;
    private Action? _unwatchHost;

    private void ShowLanPermission()
    {
        var d = LocalNetworkAccess.Current(this);
        var (subRes, badgeText, tone) = d switch
        {
            LocalNetworkDecision.Granted => (Resource.String.lan_sub_granted, GetString(Resource.String.automation_badge_granted), Rows.BadgeTone.Success),
            LocalNetworkDecision.NotRequired => (Resource.String.lan_sub_notrequired, GetString(Resource.String.automation_badge_not_required), Rows.BadgeTone.Neutral),
            LocalNetworkDecision.Undeclared => (Resource.String.lan_sub_undeclared, GetString(Resource.String.automation_badge_conflict), Rows.BadgeTone.Error),
            _ => (Resource.String.lan_sub_denied, GetString(Resource.String.automation_badge_not_granted), Rows.BadgeTone.Warning),
        };
        Rows.SetRowSubtitle(_lanRow, GetString(subRes));
        Rows.SetBadge(_lanRow.FindViewById<TextView>(Resource.Id.row_trailing_badge)!, badgeText, tone);
    }

    private async Task RequestLanAsync()
    {
        var decision = await LocalNetworkAccess.EnsureAsync(this).ConfigureAwait(true);
        ShowLanPermission();
        if (decision == LocalNetworkDecision.NeedsPermission)
        {
            Toast.MakeText(this, GetString(Resource.String.lan_denied_toast), ToastLength.Long)?.Show();
        }
    }

    private async Task StartRelayAsync()
    {
        Enable(false);
        var (registered, stageError) = Relay.CsRelay.StartGuest(this, _bridge.Layout);
        _bridge.Note($"c# relay (guest) start: ui_* scripts {(stageError is null ? "staged" : "NOT staged")}, ui_* layer {(registered ? "registered" : stageError is null ? "conflict" : "not registered")}");
        ShowOutput(stageError is { } se
            ? GetString(Resource.String.automation_cs_guest_stage_failed, new Java.Lang.String(se))
            : registered ? "" : GetString(Resource.String.automation_cs_guest_conflict));
        WatchCsRelay(true);
        ShowCsRelay();
        Enable(true);
        await RefreshAsync();
    }

    private async Task StopRelayAsync()
    {
        var removed = Relay.CsRelay.StopGuest(_bridge.Layout.UserDataDir);
        _bridge.Note($"c# relay (guest) stop; ui_* layer {(removed ? "removed" : "left (not ours)")}");
        ShowCsRelay();
        await RefreshAsync();
    }

    private void WatchCsRelay(bool on)
    {
        _csWatch?.Dispose();
        _csWatch = null;
        _unwatchHost?.Invoke();
        _unwatchHost = null;
        _watchedHost = null;
        if (!on || Relay.CsRelay.Host is not { } host)
        {
            HideCard();
            CloseGapDialog();
            return;
        }
        _watchedHost = host;
        _csWatch = new Relay.RelayHostWatch(host, () => RunOnUiThread(ShowCsRelay));
        Action<InterlockQueue.Presentation> routing = p => RunOnUiThread(() => ShowRouting(host, p));
        Action<RelayHost.AccessibilityNoticeInfo?> notice = n => RunOnUiThread(() => ShowNotice(host, n));
        host.ApprovalRoutingChanged += routing;
        host.AccessibilityNoticeChanged += notice;
        _unwatchHost = () =>
        {
            host.ApprovalRoutingChanged -= routing;
            host.AccessibilityNoticeChanged -= notice;
        };
        ShowRouting(host, host.ApprovalRouting);
        ShowNotice(host, host.AccessibilityNotice);
    }

    private void ShowRouting(RelayHost host, InterlockQueue.Presentation? routing)
    {
        if (host != _watchedHost)
        {
            return;
        }
        if (routing is not { Via: InterlockChannel.Page, Request: { } request } || !Relay.CsRelay.PageCardShown)
        {
            HideCard();
            return;
        }
        if (_card is not null && _cardRequestId == request.Id)
        {
            _card.SetPendingCount(routing.WaitingCount);
            return;
        }
        HideCard();
        var id = request.Id;
        var capability = request.Prompt.Capability;
        _card = new Relay.ApprovalCardView(this, request.Prompt, routing.WaitingCount, choice =>
        {
            HideCard();
            var taken = host.ResolveApproval(choice, id);
            _bridge.Note($"c# relay page card: {capability} -> {choice.Wire()} (request {id}{(taken ? "" : ", already settled")})");
        }, onDeadline: () =>
        {
            if (_cardRequestId != id)
            {
                return;
            }
            HideCard();
            var expired = host.ExpireApproval(id);
            _bridge.Note($"c# relay page card: {capability} expired (request {id}{(expired ? "" : ", already settled")})");
        });
        _cardRequestId = id;
        Window!.AddFlags(WindowManagerFlags.Secure);
        _cardSlot.AddView(_card.View, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        _cardSlot.Visibility = ViewStates.Visible;
        _card.Start();
        _page.ScrollTo(0, 0);
    }

    private void HideCard()
    {
        if (_card is null)
        {
            return;
        }
        _card.Stop();
        _card = null;
        _cardRequestId = 0;
        _cardSlot.RemoveAllViews();
        _cardSlot.Visibility = ViewStates.Gone;
        Window?.ClearFlags(WindowManagerFlags.Secure);
    }

    private void ShowNotice(RelayHost host, RelayHost.AccessibilityNoticeInfo? notice)
    {
        if (host != _watchedHost)
        {
            return;
        }
        if (notice is null)
        {
            CloseGapDialog();
            return;
        }
        if (_gapDialog is { IsShowing: true } && _gapShown == notice)
        {
            return;
        }
        CloseGapDialog();
        _gapShown = notice;
        var message = GetString(notice.Gap == AccessibilityGap.NotBound ? Resource.String.relay_gap_not_bound : Resource.String.relay_gap_revoked,
            new Java.Lang.String(notice.Capability));
        _gapDialog = new MaterialAlertDialogBuilder(this)
            .SetIcon(Resource.Drawable.ic_lock)!
            .SetTitle(Resource.String.relay_gap_title)!
            .SetMessage(message)!
            .SetCancelable(false)!
            .SetPositiveButton(Resource.String.relay_gap_open, (_, _) =>
            {
                host.DismissAccessibilityNotice();
                OpenAccessibilitySettings();
            })!
            .SetNegativeButton(Resource.String.relay_gap_dismiss, (_, _) => host.DismissAccessibilityNotice())!
            .Show();
    }

    private void CloseGapDialog()
    {
        _gapDialog?.Dismiss();
        _gapDialog = null;
        _gapShown = null;
    }

    private void OpenAccessibilitySettings()
    {
        var component = new ComponentName(PackageName!, "dshmobile.relay.DshAccessibilityService").FlattenToString();
        try
        {
            var details = new Intent("android.settings.ACCESSIBILITY_DETAILS_SETTINGS");
            details.PutExtra(Intent.ExtraComponentName, component);
            StartActivity(details);
            return;
        }
        catch (ActivityNotFoundException)
        {
        }
        catch (Java.Lang.SecurityException)
        {
        }
        StartActivity(new Intent(Android.Provider.Settings.ActionAccessibilitySettings));
    }

    private void OpenOverlaySettings()
    {
        try
        {
            StartActivity(new Intent(Android.Provider.Settings.ActionManageOverlayPermission, Android.Net.Uri.Parse("package:" + PackageName)));
        }
        catch (ActivityNotFoundException)
        {
            StartActivity(new Intent(Android.Provider.Settings.ActionManageOverlayPermission));
        }
    }

    private void ShowOverlayPermission()
    {
        var granted = Android.Provider.Settings.CanDrawOverlays(this);
        Rows.SetRowSubtitle(_overlayRow, Rows.AfterLabel(GetString(granted
            ? Resource.String.automation_overlay_granted
            : Resource.String.automation_overlay_not_granted)));
        Rows.SetBadge(
            _overlayRow.FindViewById<TextView>(Resource.Id.row_trailing_badge)!,
            GetString(granted ? Resource.String.automation_badge_granted : Resource.String.automation_badge_not_granted),
            granted ? Rows.BadgeTone.Success : Rows.BadgeTone.Neutral);
    }

    private (string Line, string Badge, Rows.BadgeTone Tone) A11yStatus(AccessibilityGap? gap) => gap switch
    {
        null => (GetString(Resource.String.automation_a11y_on), GetString(Resource.String.automation_badge_enabled), Rows.BadgeTone.Success),
        AccessibilityGap.NotBound => (GetString(Resource.String.automation_a11y_not_bound), GetString(Resource.String.automation_badge_not_bound), Rows.BadgeTone.Warning),
        _ => (GetString(Resource.String.automation_a11y_off), GetString(Resource.String.automation_badge_missing), Rows.BadgeTone.Neutral),
    };

    private AccessibilityGap? LocalA11yGap()
    {
        if (Relay.DshAccessibilityService.Instance is not null)
        {
            return null;
        }
        var package = PackageName!;
        var listed = (Android.Provider.Settings.Secure.GetString(ContentResolver, Android.Provider.Settings.Secure.EnabledAccessibilityServices) ?? "")
            .Split(':').Any(c => c.StartsWith(package + "/", StringComparison.OrdinalIgnoreCase));
        return listed ? AccessibilityGap.NotBound : AccessibilityGap.Revoked;
    }

    private async Task ShowCsStatusAsync(RelayHost host)
    {
        var s = await Task.Run(host.Status);
        var shizuku = await Task.Run(ShizukuClient.Probe);
        var registered = UiToolsRegistration.IsRegistered(_bridge.Layout.UserDataDir);
        if (IsDestroyed)
        {
            return;
        }
        Rows.SetRowSubtitle(_relayRow, RelayLine(s.Phase, s.ChannelRunning, s.DegradedReason));
        BindA11yRow(s.AccessibilityGap);
        BindShizukuRow(shizuku);
        BindToolsRow(registered);
        Rows.SetRowSubtitle(_detailsRow, string.Join('\n', $"允许的能力：{string.Join(", ", s.Allowed)}", $"guest CLI：{s.GuestEntry}"));
        _detailsDivider.Visibility = ViewStates.Visible;
        _detailsRow.Visibility = ViewStates.Visible;
    }

    private void BindA11yRow(AccessibilityGap? gap)
    {
        var (line, badge, tone) = A11yStatus(gap);
        Rows.SetRowSubtitle(_a11yRow, Rows.AfterLabel(line));
        Rows.SetBadge(_a11yRow.FindViewById<TextView>(Resource.Id.row_trailing_badge)!, badge, tone);
    }

    private void BindShizukuRow(ShizukuProbe probe)
    {
        var line = ShizukuLine(probe);
        var (badge, tone) = ShizukuConnection.Decide(probe) switch
        {
            ShizukuConnectState.Granted => (GetString(Resource.String.automation_badge_granted), Rows.BadgeTone.Success),
            ShizukuConnectState.CanRequest => (GetString(Resource.String.automation_badge_not_granted), Rows.BadgeTone.Warning),
            ShizukuConnectState.PermanentlyDenied => (GetString(Resource.String.automation_badge_not_granted), Rows.BadgeTone.Error),
            _ => (GetString(Resource.String.automation_badge_missing), Rows.BadgeTone.Neutral),
        };
        Rows.SetRowSubtitle(_shizukuRow, Rows.AfterLabel(line));
        Rows.SetBadge(_shizukuRow.FindViewById<TextView>(Resource.Id.row_trailing_badge)!, badge, tone);
    }

    private void BindToolsRow(bool registered)
    {
        string line;
        string badge;
        Rows.BadgeTone tone;
        if (_bridge.Runtime.HostUiToolsAtStart is not null)
        {
            var view = WithHostView(new UiToolsView(registered, null, false, false));
            line = UiToolsLine(view);
            (badge, tone) = view switch
            {
                { Conflict: true } => (GetString(Resource.String.automation_badge_conflict), Rows.BadgeTone.Error),
                { DshRestartNeeded: true } => (GetString(Resource.String.automation_badge_restart), Rows.BadgeTone.Warning),
                { LoadedByDsh: true } => (GetString(Resource.String.automation_badge_live), Rows.BadgeTone.Success),
                { Registered: true } => (GetString(Resource.String.automation_badge_registered), Rows.BadgeTone.Primary),
                _ => (GetString(Resource.String.automation_badge_missing), Rows.BadgeTone.Neutral),
            };
        }
        else
        {
            line = GetString(registered ? Resource.String.automation_tools_cs_registered : Resource.String.automation_tools_off);
            badge = GetString(registered ? Resource.String.automation_badge_registered : Resource.String.automation_badge_missing);
            tone = registered ? Rows.BadgeTone.Primary : Rows.BadgeTone.Neutral;
        }
        Rows.SetRowSubtitle(_toolsRow, Rows.AfterLabel(line));
        Rows.SetBadge(_toolsRow.FindViewById<TextView>(Resource.Id.row_trailing_badge)!, badge, tone);
    }

    private string RelayLine(RelayRuntimePhase phase, bool channelRunning, string? degradedReason) =>
        GetString(phase switch
        {
            RelayRuntimePhase.Starting => Resource.String.automation_relay_starting,
            RelayRuntimePhase.Running => Resource.String.automation_relay_running,
            RelayRuntimePhase.Degraded => Resource.String.automation_relay_degraded,
            _ => Resource.String.automation_relay_off,
        })
        + (channelRunning ? " · " + GetString(Resource.String.automation_relay_mailbox) : "")
        + (degradedReason is { } r ? " · " + r : "");

    private void ShowCsRelay()
    {
        var host = Relay.CsRelay.Host;
        var phase = Relay.CsRelay.Phase;
        var on = phase is not RelayRuntimePhase.Stopped;
        _settingSwitch = true;
        _relaySwitch.Checked = on;
        _settingSwitch = false;
        Rows.SetRowSubtitle(_relayRow, on
            ? RelayLine(phase, host?.IsChannelRunning == true, host?.DegradedReason)
            : GetString(Resource.String.automation_relay_card_sub));
        if (!on)
        {
            _detailsDivider.Visibility = ViewStates.Gone;
            _detailsRow.Visibility = ViewStates.Gone;
        }
        if (Relay.CsRelay.GuestFacing && host is not null)
        {
            _ = ShowCsStatusAsync(host);
        }
    }

    private async Task RefreshAsync()
    {
        if (Relay.CsRelay.GuestFacing && Relay.CsRelay.Host is { } host)
        {
            await ShowCsStatusAsync(host);
        }
        else
        {
            await ShowCsStoppedStatusAsync();
        }
    }

    private async Task ShowCsStoppedStatusAsync()
    {
        var gap = LocalA11yGap();
        var shizuku = await Task.Run(ShizukuClient.Probe);
        var registered = UiToolsRegistration.IsRegistered(_bridge.Layout.UserDataDir);
        if (IsDestroyed)
        {
            return;
        }
        BindA11yRow(gap);
        BindShizukuRow(shizuku);
        BindToolsRow(registered);
        _detailsDivider.Visibility = ViewStates.Gone;
        _detailsRow.Visibility = ViewStates.Gone;
        _bridge.Note($"relay status (C#): stopped, ui_* layer {(registered ? "registered" : "absent")}");
    }

    private string? StoredSurfacePreference() =>
        GetSharedPreferences(RelaySurfaces.PrefsFile, FileCreationMode.Private)!
            .GetString(RelaySurfaces.PreferenceKey, null);

    private SurfacePreference CurrentSurfacePreference() =>
        RelaySurfaces.ParsePreference(StoredSurfacePreference());

    private string SurfacePreferenceLabel(SurfacePreference p) =>
        GetString(p == SurfacePreference.BackgroundPreferred
            ? Resource.String.automation_surface_pref_bg
            : Resource.String.automation_surface_pref_fg);

    private void ShowSurfacePreference()
    {
        if (_surfacePrefRow is null)
        {
            return;
        }
        var text = GetString(Resource.String.automation_surface_pref, new Java.Lang.String(SurfacePreferenceLabel(CurrentSurfacePreference())));
        var i = text.IndexOf('：', StringComparison.Ordinal);
        _surfacePrefRow.FindViewById<TextView>(Resource.Id.row_title)!.Text = i < 0 ? text : text[..i];
        Rows.SetRowSubtitle(_surfacePrefRow, Rows.AfterLabel(text));
    }

    private void ToggleSurfacePreference()
    {
        var next = CurrentSurfacePreference() == SurfacePreference.BackgroundPreferred
            ? SurfacePreference.Foreground
            : SurfacePreference.BackgroundPreferred;
        var editor = GetSharedPreferences(RelaySurfaces.PrefsFile, FileCreationMode.Private)!.Edit()!;
        editor.PutString(RelaySurfaces.PreferenceKey, next.Name());
        editor.Commit();
        Relay.CsRelay.Host?.OnPreferencesChanged();
        ShowSurfacePreference();
        ShowOutput(GetString(Resource.String.automation_surface_pref_changed, new Java.Lang.String(SurfacePreferenceLabel(next))));
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
        var line = ShizukuLine(probe);
        BindShizukuRow(probe);
        ShowOutput(state == ShizukuConnectState.CanRequest ? GetString(Resource.String.automation_shizuku_requested) : line);
        _bridge.Note($"shizuku connect: {state} (v{probe.Version}, uid {probe.Uid})");
    }

    private sealed record UiToolsView(bool Registered, bool? LoadedByDsh, bool DshRestartNeeded, bool Conflict);

    private UiToolsView WithHostView(UiToolsView t)
    {
        if (_bridge.Runtime.HostUiToolsAtStart is not { } loaded)
        {
            return t;
        }
        return t with { LoadedByDsh = loaded, DshRestartNeeded = loaded != t.Registered };
    }

    private string UiToolsLine(UiToolsView t)
    {
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
        ShowOutput(GetString(Resource.String.automation_selftest_running));
        var runtime = _bridge.Runtime;
        var r = await Task.Run(() => runtime.RunRelaySelfTestAsync(TimeSpan.FromSeconds(115), CancellationToken.None));
        Enable(true);
        var exit = r.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?";
        ShowOutput(r.ExitCode is null && r.Error is { } e
            ? $"selftest FAILED {e}"
            : $"exit={exit}{(r.Ok || r.Error is null ? "" : " · " + r.Error)}\n\n{r.Output}");
        _bridge.Note($"relay selftest exit={exit}");
    }

    private void ShowOutput(string text)
    {
        if (IsDestroyed)
        {
            return;
        }
        _output.Text = text.TrimEnd();
        var visible = text.Length == 0 ? ViewStates.Gone : ViewStates.Visible;
        _outputDivider.Visibility = visible;
        _outputCard.Visibility = visible;
    }

    private void Enable(bool on)
    {
        _progress.Visibility = on ? ViewStates.Gone : ViewStates.Visible;
        foreach (var v in _actions)
        {
            v.Enabled = on;
            v.Alpha = on ? 1f : 0.38f;
        }
    }
}
