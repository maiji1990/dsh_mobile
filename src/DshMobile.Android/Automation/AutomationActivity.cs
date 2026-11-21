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

namespace DshMobile.App.Automation;

/// <summary>
/// Mobile automation (TASK-010, ADR-0004 / ADR-0010), experimental and off by default. Starts/stops the C# Relay on the
/// guest mailbox, links to system accessibility settings (the user enables the service; the app never does) and runs a
/// fixed read-only self-test (relay doctor + the ui_* facade over MCP) through the real guest mailbox. ADR-0021: the
/// same actions as Material 3 rows; the relay is a switch, each permission row opens the system page that grants it.
/// </summary>
[Activity(Label = "@string/automation_title", Exported = false)]
public sealed class AutomationActivity : DshActivity
{
    private BridgeHost _bridge = null!;
    private NestedScrollView _page = null!;
    private FrameLayout _cardSlot = null!;
    private Rows.Row _relay = null!;
    private MaterialSwitch _relaySwitch = null!;
    private bool _settingSwitch;
    private Rows.Row _a11y = null!;
    private Rows.Row _overlay = null!;
    private Rows.Row _lan = null!;
    private Rows.Row _shizuku = null!;
    private Rows.Row _tools = null!;
    private Rows.Row _details = null!;
    private Rows.Row _selftest = null!;
    private Rows.Row? _surfacePref;
    private TextView _output = null!;
    private View _outputCard = null!;
    private View[] _actions = [];

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetScreen(Resource.Layout.fragment_list, Resource.String.automation_title);
        _bridge = DshMobileApplication.Current.Bridge;
        _page = FindViewById<NestedScrollView>(Resource.Id.list_scroll)!;
        var content = FindViewById<LinearLayout>(Resource.Id.list_content)!;

        // ADR-0011: the C# Relay's approval card sits at the top of this page while the page is in front.
        _cardSlot = new FrameLayout(this) { Visibility = ViewStates.Gone };
        var m = Rows.Dp(this, 16);
        _cardSlot.SetPadding(m, m, m, 0);
        content.AddView(_cardSlot);

        IntroCard(content);

        Rows.Header(content, GetString(Resource.String.automation_section_status));
        // ADR-0010: the C# Relay serves the guest mailbox.
        _relay = Rows.Add(content, Resource.Drawable.ic_phone, GetString(Resource.String.automation_relay), null, () => _relaySwitch.Toggle());
        _relaySwitch = new MaterialSwitch(this);
        _relaySwitch.ContentDescription = GetString(Resource.String.automation_relay);
        _relaySwitch.CheckedChange += (_, e) =>
        {
            if (_settingSwitch)
            {
                return;
            }
            _ = e.IsChecked ? StartRelayAsync() : StopRelayAsync();
        };
        _relay.SetTrailing(_relaySwitch);
        _a11y = Rows.Add(content, Resource.Drawable.ic_lock, GetString(Resource.String.automation_a11y), null, OpenAccessibilitySettings);
        _a11y.SetTrailing(OpenIcon());
        // ADR-0011: opens the system "display over other apps" page for this app; the user grants it there (or not: the
        // floating card is then skipped and the notification card asks instead).
        _overlay = Rows.Add(content, Resource.Drawable.ic_open_in_new, GetString(Resource.String.automation_overlay), null, OpenOverlaySettings);
        _overlay.SetTrailing(OpenIcon());
        // ADR-0032: Android 17 gates LAN traffic (the DSH sandbox shares this UID) behind a runtime permission. The row
        // is the status and the way back in after a denial - the guest shell cannot show a system prompt by itself.
        _lan = Rows.Add(content, Resource.Drawable.ic_lan, GetString(Resource.String.lan_title), null, () => _ = RequestLanAsync());
        _lan.SetTrailing(OpenIcon());
        // ADR-0008: asks Shizuku to show its own grant dialog (the user answers it in Shizuku); never starts Shizuku.
        _shizuku = Rows.Add(content, Resource.Drawable.ic_key, GetString(Resource.String.automation_shizuku), null, ConnectShizuku);
        _tools = Rows.Add(content, Resource.Drawable.ic_extension, GetString(Resource.String.automation_tools));
        _details = Rows.Add(content, Resource.Drawable.ic_info, GetString(Resource.String.automation_details));
        _details.View.Visibility = ViewStates.Gone;

        Rows.Header(content, GetString(Resource.String.automation_section_check));
        _selftest = Rows.Add(content, Resource.Drawable.ic_check_circle, GetString(Resource.String.automation_selftest),
            GetString(Resource.String.automation_selftest_sub), () => _ = SelfTestAsync());
        _output = Rows.MonoCard(content, out _outputCard);
        _output.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
        _outputCard.Visibility = ViewStates.Gone;

        if (UiPrefs.DeveloperEnabled(this))
        {
            // PR-07 segment 4, developer action: Relay's own stored surface preference (the setting upstream's UI writes).
            // It never grants anything: without Shizuku and a trusted display, "background preferred" falls back to the
            // foreground and Relay says so.
            Rows.Header(content, GetString(Resource.String.automation_section_developer));
            _surfacePref = Rows.Add(content, Resource.Drawable.ic_build, "", null, ToggleSurfacePreference);
            ShowSurfacePreference();
        }
        _actions = _surfacePref is { } sp
            ? [_relay.View, _relaySwitch, _selftest.View, _shizuku.View, sp.View]
            : [_relay.View, _relaySwitch, _selftest.View, _shizuku.View];

        // The selectable output is focusable in touch mode, so it used to take the window's initial focus; the scroll view
        // then kept that focused view on screen as the output grew and scrolled the page by itself while a self-test ran,
        // which hid the top of the page under the app bar (the guest's ui_get_element then correctly saw that node as not
        // visible). The page itself takes focus first; tapping the output still focuses it for selection.
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
        _ = RefreshAsync(); // accessibility may have been toggled in system settings meanwhile
        WatchCsRelay(true);
        ShowCsRelay();
        ShowOverlayPermission();
        ShowLanPermission();
        // Upstream uiInForeground(): from now on a pending C# question is asked on this page.
        Relay.CsRelay.PageCardShown = true;
    }

    protected override void OnPause()
    {
        // The question moves to the floating card or the notification before this page goes away.
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

    /// <summary>The intro text, with the full approval / permission explanation behind a text button.</summary>
    private void IntroCard(LinearLayout content)
    {
        var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var text = new TextView(this) { Text = GetString(Resource.String.automation_intro) };
        text.SetTextAppearance(Resource.Style.TextAppearance_Material3_BodyMedium);
        text.SetTextColor(Android.Content.Res.ColorStateList.ValueOf(new Android.Graphics.Color(Rows.Color(_page, Resource.Attribute.colorOnSurfaceVariant))));
        box.AddView(text);
        var more = new MaterialButton(this, null, Resource.Attribute.borderlessButtonStyle) { Text = GetString(Resource.String.automation_more) };
        var expanded = false;
        more.Click += (_, _) =>
        {
            expanded = !expanded;
            text.Text = GetString(expanded ? Resource.String.automation_explain : Resource.String.automation_intro);
            more.Text = GetString(expanded ? Resource.String.automation_less : Resource.String.automation_more);
        };
        var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { Gravity = GravityFlags.Start };
        box.AddView(more, lp);
        var card = Rows.Card(content, box);
        ((ViewGroup.MarginLayoutParams)card.LayoutParameters!).TopMargin = Rows.Dp(this, 16);
    }

    /// <summary>ADR-0032: the LAN row's status line (granted / still needed / unnecessary / broken build).</summary>
    private void ShowLanPermission()
    {
        _lan.SetSubtitle(LocalNetworkAccess.Current(this) switch
        {
            LocalNetworkDecision.Granted => GetString(Resource.String.lan_sub_granted),
            LocalNetworkDecision.NotRequired => GetString(Resource.String.lan_sub_notrequired),
            LocalNetworkDecision.Undeclared => GetString(Resource.String.lan_sub_undeclared),
            _ => GetString(Resource.String.lan_sub_denied),
        });
    }

    /// <summary>Ask for ACCESS_LOCAL_NETWORK, then show what the answer means; a denial is not an error here.</summary>
    private async Task RequestLanAsync()
    {
        var decision = await LocalNetworkAccess.EnsureAsync(this).ConfigureAwait(true);
        ShowLanPermission();
        if (decision == LocalNetworkDecision.NeedsPermission)
        {
            Android.Widget.Toast.MakeText(this, GetString(Resource.String.lan_denied_toast), Android.Widget.ToastLength.Long)?.Show();
        }
    }

    private ImageView OpenIcon()
    {
        var icon = new ImageView(this);
        icon.SetImageResource(Resource.Drawable.ic_chevron_right);
        icon.ImportantForAccessibility = ImportantForAccessibility.No;
        return icon;
    }

    /// <summary>
    /// ADR-0010: the C# host serves files/pilot, stages the ui_* guest scripts and registers the ui_* layer.
    /// </summary>
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
        // Both arrive on pool threads; every handler re-checks that this host is still the one watched.
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

    /// <summary>
    /// ADR-0011 in-page card: shown only while the queue routes the displayed question to this page. Answers go to the
    /// queue by request id, so a card left over from an earlier question can never answer a newer one. The window is
    /// FLAG_SECURE while it shows; the agent cannot press it (writes on the host display are suspended while any of this
    /// app's windows is up).
    /// </summary>
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
            // Time is up: take the card (and FLAG_SECURE) down and close this question in the queue, which routes the next
            // one if any (upstream expireApproval). Card first: the routing callback runs inline on this thread.
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

    /// <summary>
    /// Upstream <c>AccessibilityNotice</c> dialog: a call was held back because the accessibility step is missing. It only
    /// says what to do; the user turns the service on in system settings (the app never does).
    /// </summary>
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
        // Dismissing here is not the user's "知道了": the notice stays until they answer it.
        _gapDialog?.Dismiss();
        _gapDialog = null;
        _gapShown = null;
    }

    /// <summary>This app's own service (ADR-0011: the only one), straight to its page where the ROM supports that.</summary>
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

    private void ShowOverlayPermission() =>
        _overlay.SetSubtitle(Rows.AfterLabel(GetString(Android.Provider.Settings.CanDrawOverlays(this)
            ? Resource.String.automation_overlay_granted
            : Resource.String.automation_overlay_not_granted)));

    /// <summary>The accessibility line, from this app's own service (ADR-0011).</summary>
    private string A11yLine(AccessibilityGap? gap) => GetString(gap switch
    {
        null => Resource.String.automation_a11y_on,
        AccessibilityGap.NotBound => Resource.String.automation_a11y_not_bound,
        _ => Resource.String.automation_a11y_off,
    });

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

    /// <summary>The status rows while the C# host runs: the host's own.</summary>
    private async Task ShowCsStatusAsync(RelayHost host)
    {
        // Status() reads the system facts (Shizuku binder included): off the main thread.
        var s = await Task.Run(host.Status);
        var registered = UiToolsRegistration.IsRegistered(_bridge.Layout.UserDataDir);
        if (IsDestroyed)
        {
            return;
        }
        _relay.SetSubtitle(RelayLine(s.Phase, s.ChannelRunning, s.DegradedReason));
        _a11y.SetSubtitle(Rows.AfterLabel(A11yLine(s.AccessibilityGap)));
        _shizuku.SetSubtitle(Rows.AfterLabel(ShizukuLine(ShizukuClient.Probe())));
        _tools.SetSubtitle(Rows.AfterLabel(CsToolsLine(registered)));
        _details.SetSubtitle(string.Join('\n', $"允许的能力：{string.Join(", ", s.Allowed)}", $"guest CLI：{s.GuestEntry}"));
        _details.View.Visibility = ViewStates.Visible;
    }

    /// <summary>The relay row: the phase in words, the mailbox, and a degraded reason as reported.</summary>
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
        _relay.SetSubtitle(on
            ? RelayLine(phase, host?.IsChannelRunning == true, host?.DegradedReason)
            : GetString(Resource.String.automation_relay_off));
        if (!on)
        {
            _details.View.Visibility = ViewStates.Gone;
        }
        if (Relay.CsRelay.GuestFacing && host is not null)
        {
            _ = ShowCsStatusAsync(host);
        }
    }

    /// <summary>The status rows: the C# host's while it serves the guest mailbox, else the stopped state read by C#.</summary>
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
        _a11y.SetSubtitle(Rows.AfterLabel(A11yLine(gap)));
        _shizuku.SetSubtitle(Rows.AfterLabel(ShizukuLine(shizuku)));
        _tools.SetSubtitle(Rows.AfterLabel(CsToolsLine(registered)));
        _details.View.Visibility = ViewStates.Gone;
        _bridge.Note($"relay status (C#): stopped, ui_* layer {(registered ? "registered" : "absent")}");
    }

    private string CsToolsLine(bool registered) =>
        _bridge.Runtime.HostUiToolsAtStart is not null
            ? UiToolsLine(WithHostView(new UiToolsView(registered, null, false, false)))
            : GetString(registered ? Resource.String.automation_tools_cs_registered : Resource.String.automation_tools_off);

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

    private void ShowSurfacePreference()
    {
        if (_surfacePref is null)
        {
            return;
        }
        var text = GetString(Resource.String.automation_surface_pref, new Java.Lang.String(SurfacePreferenceLabel(CurrentSurfacePreference())));
        var i = text.IndexOf('：', StringComparison.Ordinal);
        _surfacePref.Title.Text = i < 0 ? text : text[..i];
        _surfacePref.SetSubtitle(Rows.AfterLabel(text));
    }

    private void ToggleSurfacePreference()
    {
        var next = CurrentSurfacePreference() == SurfacePreference.BackgroundPreferred
            ? SurfacePreference.Foreground
            : SurfacePreference.BackgroundPreferred;
        var editor = GetSharedPreferences(RelaySurfaces.PrefsFile, FileCreationMode.Private)!.Edit()!;
        editor.PutString(RelaySurfaces.PreferenceKey, next.Name());
        editor.Commit();
        // The C# host re-reads its preferences now instead of at the next heartbeat.
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
        _shizuku.SetSubtitle(Rows.AfterLabel(line));
        ShowOutput(state == ShizukuConnectState.CanRequest ? GetString(Resource.String.automation_shizuku_requested) : line);
        _bridge.Note($"shizuku connect: {state} (v{probe.Version}, uid {probe.Uid})");
    }

    /// <summary>
    /// Registration of the guest ui_* facade with DSH. <c>Registered</c> is the patch layer on disk; <c>LoadedByDsh</c> is
    /// what the running DSH read at its start (null when DSH is not running); <c>DshRestartNeeded</c> means the two differ;
    /// <c>Conflict</c> means the layer file holds content this app did not write (left untouched).
    /// </summary>
    private sealed record UiToolsView(bool Registered, bool? LoadedByDsh, bool DshRestartNeeded, bool Conflict);

    /// <summary>ADR-0015: the host records whether the ui_* layer was registered each time it spawned DSH.</summary>
    private UiToolsView WithHostView(UiToolsView t)
    {
        if (_bridge.Runtime.HostUiToolsAtStart is not { } loaded)
        {
            return t;
        }
        return t with { LoadedByDsh = loaded, DshRestartNeeded = loaded != t.Registered };
    }

    /// <summary>Says plainly whether DSH has the ui_* tools now; never implies they are live when DSH has not reloaded.</summary>
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

    /// <summary>
    /// The fixed guest check of DSHBox <c>relay.selftest</c> (relay doctor + the ui_* facade over MCP), run by the C# PRoot
    /// launcher.
    /// </summary>
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
        _outputCard.Visibility = text.Length == 0 ? ViewStates.Gone : ViewStates.Visible;
    }

    private void Enable(bool on)
    {
        FindViewById(Resource.Id.list_progress)!.Visibility = on ? ViewStates.Gone : ViewStates.Visible;
        foreach (var v in _actions)
        {
            v.Enabled = on;
            v.Alpha = on ? 1f : 0.38f;
        }
    }
}
