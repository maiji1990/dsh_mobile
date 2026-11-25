using Android.Content;
using Android.Views;
using DshMobile.App.Automation;
using DshMobile.App.Bridge;
using DshMobile.App.Credentials;
using DshMobile.App.Developer;
using DshMobile.App.Plugins;
using DshMobile.App.Remote;
using DshMobile.App.RuntimeUi;
using DshMobile.App.Ui;
using DshMobile.Application.Ui;
using DshMobile.Runtime.Automation;
using DshMobile.Runtime.Automation.Relay;
using Google.Android.Material.Button;
using Google.Android.Material.MaterialSwitch;
using Google.Android.Material.ProgressIndicator;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace DshMobile.App.More;

/// <summary>
/// More 标签页（ADR-0021 / UI Slice 4）：
/// 全 XML 化声明（<c>fragment_more.xml</c> + <c>view_setting_*_row</c>），顶部集成运行与远程概览卡与快捷启停，
/// 彻底移除动态 Add/RemoveAllViews 构建方式。
/// </summary>
public sealed class MoreFragment : Fragment, ITabRoot
{
    private BridgeHost _bridge = null!;
    private LinearProgressIndicator _progress = null!;
    private TextView _dshBadge = null!;
    private TextView _dshVersion = null!;
    private TextView _remoteOverview = null!;
    private MaterialButton _dshToggleBtn = null!;

    private View _rowCredentials = null!;
    private View _rowAutomation = null!;
    private View _rowRemote = null!;
    private View _rowRuntime = null!;
    private MaterialSwitch _warmupSwitch = null!;
    private View _developerDivider = null!;
    private View _rowDeveloper = null!;

    private int _versionTaps;
    private Toast? _toast;
    private int _renderPosted;

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        var view = inflater.Inflate(Resource.Layout.fragment_more, container, false)!;
        _bridge = DshMobileApplication.Current.Bridge;

        _progress = view.FindViewById<LinearProgressIndicator>(Resource.Id.more_progress)!;
        _dshBadge = view.FindViewById<TextView>(Resource.Id.more_dsh_badge)!;
        _dshVersion = view.FindViewById<TextView>(Resource.Id.more_dsh_version)!;
        _remoteOverview = view.FindViewById<TextView>(Resource.Id.more_overview_remote)!;
        _dshToggleBtn = view.FindViewById<MaterialButton>(Resource.Id.more_dsh_toggle_btn)!;
        _dshToggleBtn.Click += (_, _) =>
        {
            var status = _bridge.Status;
            if (status.Primary == HomeAction.Start)
            {
                _ = _bridge.RunStartAsync();
            }
            else if (status.Secondary == HomeAction.Stop)
            {
                _ = _bridge.RunStopAsync();
            }
        };

        var ctx = RequireContext();
        _rowCredentials = view.FindViewById(Resource.Id.more_row_credentials)!;
        Rows.BindNavRow(_rowCredentials, Resource.Drawable.ic_key, GetString(Resource.String.more_credentials),
            GetString(Resource.String.more_credentials_sub), () => Open(typeof(CredentialsActivity)));

        var rowPlugins = view.FindViewById(Resource.Id.more_row_plugins)!;
        Rows.BindNavRow(rowPlugins, Resource.Drawable.ic_extension, GetString(Resource.String.more_plugins),
            GetString(Resource.String.more_plugins_sub), () => Open(typeof(PluginsActivity)));

        _rowAutomation = view.FindViewById(Resource.Id.more_row_automation)!;
        Rows.BindNavRow(_rowAutomation, Resource.Drawable.ic_phone, GetString(Resource.String.more_automation),
            GetString(Resource.String.more_automation_sub), () => Open(typeof(AutomationActivity)));

        _rowRemote = view.FindViewById(Resource.Id.more_row_remote)!;
        Rows.BindNavRow(_rowRemote, Resource.Drawable.ic_cloud, GetString(Resource.String.more_remote),
            GetString(Resource.String.more_remote_sub), () => Open(typeof(RemoteActivity)));

        _rowRuntime = view.FindViewById(Resource.Id.more_row_runtime)!;
        Rows.BindNavRow(_rowRuntime, Resource.Drawable.ic_storage, GetString(Resource.String.more_runtime),
            GetString(Resource.String.more_runtime_sub), () => Open(typeof(RuntimeActivity)));

        var rowWarmup = view.FindViewById(Resource.Id.more_row_warmup)!;
        // 5A：默认开启的后台预热；行文案说明电量/省电/温控保护与可关闭，开关仍由用户控制。
        _warmupSwitch = Rows.BindSwitchRow(rowWarmup, Resource.Drawable.ic_refresh, GetString(Resource.String.more_warmup),
            GetString(Resource.String.more_warmup_short_sub), UiPrefs.EagerWarmupEnabled(ctx),
            enabled => UiPrefs.SetEagerWarmupEnabled(ctx, enabled));

        var rowAbout = view.FindViewById(Resource.Id.more_row_about)!;
        Rows.BindActionRow(rowAbout, Resource.Drawable.ic_info, GetString(Resource.String.more_about),
            Rows.Fmt(ctx, Resource.String.more_about_sub, AppVersion(ctx)), OnVersionTapped);

        var rowLicenses = view.FindViewById(Resource.Id.more_row_licenses)!;
        Rows.BindNavRow(rowLicenses, Resource.Drawable.ic_article, GetString(Resource.String.more_licenses),
            GetString(Resource.String.more_licenses_sub), ShowLicenses);

        _developerDivider = view.FindViewById(Resource.Id.more_developer_divider)!;
        _rowDeveloper = view.FindViewById(Resource.Id.more_row_developer)!;
        Rows.BindNavRow(_rowDeveloper, Resource.Drawable.ic_code, GetString(Resource.String.developer_title),
            GetString(Resource.String.more_developer_sub), () => Open(typeof(DeveloperActivity)));

        if (view.FindViewById<ViewGroup>(Resource.Id.more_scroll) is { ChildCount: > 0 } scroll)
        {
            Edges.ClampMaxWidth(scroll.GetChildAt(0));
        }

        return view;
    }

    public override void OnStart()
    {
        base.OnStart();
        _bridge.Changed += OnStateChanged;
        _bridge.Agent.Credentials.StateChanged += OnCredentialsChanged;
        Render();
    }

    public override void OnStop()
    {
        _bridge.Changed -= OnStateChanged;
        _bridge.Agent.Credentials.StateChanged -= OnCredentialsChanged;
        base.OnStop();
    }

    public override void OnResume()
    {
        base.OnResume();
        if (IsAdded && View is not null)
        {
            _warmupSwitch.Checked = UiPrefs.EagerWarmupEnabled(RequireContext());
            Render();
        }
    }

    private void OnStateChanged(object? sender, EventArgs e) => ScheduleRender();

    private void OnCredentialsChanged(Agent.Presentation.CredentialsUiState _) => ScheduleRender();

    private void ScheduleRender()
    {
        if (Interlocked.Exchange(ref _renderPosted, 1) == 0)
        {
            Activity?.RunOnUiThread(() =>
            {
                Volatile.Write(ref _renderPosted, 0);
                if (IsAdded && View is not null)
                {
                    Render();
                }
            });
        }
    }

    private void Render()
    {
        var ctx = RequireContext();
        var status = _bridge.Status;
        var busy = _bridge.IsBusy;
        _progress.Visibility = busy ? ViewStates.Visible : ViewStates.Gone;

        var (phaseRes, tone) = status.Phase switch
        {
            HomePhase.NotInstalled => (Resource.String.home_label_not_installed, Rows.BadgeTone.Neutral),
            HomePhase.Starting or HomePhase.WaitingForDsh => (Resource.String.home_label_starting, Rows.BadgeTone.Warning),
            HomePhase.Ready => (Resource.String.home_label_ready, Rows.BadgeTone.Success),
            HomePhase.Stopping => (Resource.String.home_label_stopping, Rows.BadgeTone.Warning),
            HomePhase.Busy => (Resource.String.home_label_busy, Rows.BadgeTone.Warning),
            HomePhase.Failed => (Resource.String.home_label_failed, Rows.BadgeTone.Error),
            _ => (Resource.String.home_label_stopped, Rows.BadgeTone.Neutral),
        };
        Rows.SetBadge(_dshBadge, GetString(phaseRes), tone);

        var version = status.DshVersion;
        _dshVersion.Text = string.IsNullOrWhiteSpace(version) ? "" : $"DSH {version}";
        _dshVersion.Visibility = string.IsNullOrWhiteSpace(version) ? ViewStates.Gone : ViewStates.Visible;

        var remoteSession = CsRemoteConnection.Session;
        if (remoteSession is not null)
        {
            var label = string.IsNullOrWhiteSpace(remoteSession.Profile.Label)
                ? remoteSession.Profile.Host
                : remoteSession.Profile.Label;
            _remoteOverview.Text = Rows.Fmt(ctx, Resource.String.more_overview_remote_connected, label);
        }
        else
        {
            _remoteOverview.Text = GetString(Resource.String.more_overview_remote_disconnected);
        }

        if (status.Primary == HomeAction.Start)
        {
            _dshToggleBtn.Visibility = ViewStates.Visible;
            _dshToggleBtn.SetText(Resource.String.home_action_start);
            _dshToggleBtn.Enabled = !busy;
        }
        else if (status.Secondary == HomeAction.Stop)
        {
            _dshToggleBtn.Visibility = ViewStates.Visible;
            _dshToggleBtn.SetText(Resource.String.home_action_stop);
            _dshToggleBtn.Enabled = !busy;
        }
        else
        {
            _dshToggleBtn.Visibility = ViewStates.Gone;
        }

        // 各分组右侧状态胶囊
        var creds = _bridge.Agent.Credentials.State.Entries;
        if (creds.Count > 0)
        {
            var configuredCount = creds.Count(e => e.Configured);
            Rows.SetBadge(
                _rowCredentials.FindViewById<TextView>(Resource.Id.row_trailing_badge)!,
                configuredCount > 0 ? Rows.Fmt(ctx, Resource.String.more_badge_configured_count, configuredCount) : GetString(Resource.String.more_badge_unconfigured),
                configuredCount > 0 ? Rows.BadgeTone.Success : Rows.BadgeTone.Neutral);
        }
        else
        {
            Rows.SetBadge(_rowCredentials.FindViewById<TextView>(Resource.Id.row_trailing_badge)!, null);
        }

        var relayOn = Automation.Relay.CsRelay.Phase is not RelayRuntimePhase.Stopped;
        Rows.SetBadge(
            _rowAutomation.FindViewById<TextView>(Resource.Id.row_trailing_badge)!,
            GetString(relayOn ? Resource.String.automation_badge_enabled : Resource.String.automation_relay_off),
            relayOn ? Rows.BadgeTone.Success : Rows.BadgeTone.Neutral);

        var remoteConnected = remoteSession is not null;
        Rows.SetBadge(
            _rowRemote.FindViewById<TextView>(Resource.Id.row_trailing_badge)!,
            GetString(remoteConnected ? Resource.String.more_badge_connected : Resource.String.more_badge_disconnected),
            remoteConnected ? Rows.BadgeTone.Primary : Rows.BadgeTone.Neutral);

        Rows.SetBadge(
            _rowRuntime.FindViewById<TextView>(Resource.Id.row_trailing_badge)!,
            string.IsNullOrWhiteSpace(version) ? null : version,
            Rows.BadgeTone.Neutral);

        var devVisible = UiPrefs.DeveloperEnabled(ctx) ? ViewStates.Visible : ViewStates.Gone;
        _developerDivider.Visibility = devVisible;
        _rowDeveloper.Visibility = devVisible;
    }

    private void Open(Type activity) => StartActivity(new Intent(RequireContext(), activity));

    internal static string AppVersion(Context ctx)
    {
        var info = ctx.PackageManager!.GetPackageInfo(ctx.PackageName!, 0);
        return info?.VersionName ?? "?";
    }

    /// <summary>Like Android's own developer options: seven taps on the version.</summary>
    private void OnVersionTapped()
    {
        var ctx = RequireContext();
        if (UiPrefs.DeveloperEnabled(ctx))
        {
            Toast(GetString(Resource.String.developer_already));
            return;
        }
        _versionTaps++;
        var left = UiPrefs.DeveloperTaps - _versionTaps;
        if (left <= 0)
        {
            UiPrefs.SetDeveloperEnabled(ctx, true);
            _versionTaps = 0;
            Toast(GetString(Resource.String.developer_enabled));
            Render();
        }
        else if (left <= 3)
        {
            Toast(Rows.Fmt(ctx, Resource.String.developer_steps, left));
        }
    }

    private void Toast(string text)
    {
        _toast?.Cancel();
        _toast = Android.Widget.Toast.MakeText(RequireContext(), text, ToastLength.Short);
        _toast!.Show();
    }

    private void ShowLicenses()
    {
        var ctx = RequireContext();
        Rows.ShowScrollableTextDialog(ctx, Resource.String.more_licenses, GetString(Resource.String.licenses_body));
    }
}
