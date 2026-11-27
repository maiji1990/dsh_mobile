using Android.Content;
using Android.Content.Res;
using Android.Views;
using Android.Widget;
using AndroidX.Core.Widget;
using DshMobile.App.Automation;
using DshMobile.App.Bridge;
using DshMobile.App.Chats;
using DshMobile.App.Credentials;
using DshMobile.App.Developer;
using DshMobile.App.Inbox;
using DshMobile.App.Plugins;
using DshMobile.App.Remote;
using DshMobile.App.RuntimeUi;
using DshMobile.App.Ui;
using DshMobile.Agent.Presentation;
using DshMobile.Application.Ui;
using DshMobile.Runtime.Automation;
using DshMobile.Runtime.Automation.Relay;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.MaterialSwitch;
using Google.Android.Material.ProgressIndicator;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace DshMobile.App.More;

/// <summary>
/// More 标签页（ADR-0021 / UI Slice 4 / UI-S6）：
/// 全 XML 化声明（<c>fragment_more.xml</c> + <c>view_setting_*_row</c>），顶部由大块概览卡压缩为
/// 单行运行摘要条（状态圆点 + 本机/远程摘要 + 紧凑控制按钮），点击直接拉起宿主快速控制抽屉；
/// 顶栏按待办数量显示跨会话收件箱入口，并将其余设置行副标题收敛到 1~2 行。
/// </summary>
public sealed class MoreFragment : Fragment, ITabRoot, HostQuickControlBottomSheet.IHost
{
    private BridgeHost _bridge = null!;
    private MaterialToolbar _toolbar = null!;
    private IMenuItem? _inboxMenuItem;
    private LinearProgressIndicator _progress = null!;
    private View _summaryStrip = null!;
    private ImageView _statusDot = null!;
    private TextView _statusTitle = null!;
    private TextView _statusDetail = null!;
    private NestedScrollView _scroll = null!;

    private View _rowCredentials = null!;
    private View _rowAutomation = null!;
    private View _rowRemote = null!;
    private View _rowRuntime = null!;
    private View _rowAppearance = null!;
    private MaterialSwitch _warmupSwitch = null!;
    private View _developerDivider = null!;
    private View _rowDeveloper = null!;
    private bool _isWide;

    private int _versionTaps;
    private Toast? _toast;
    private int _renderPosted;

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        var view = inflater.Inflate(Resource.Layout.fragment_more, container, false)!;
        _bridge = DshMobileApplication.Current.Bridge;
        _isWide = (Activity as MainActivity)?.IsWideLayout ?? false;

        _toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.more_toolbar)!;
        _inboxMenuItem = _toolbar.Menu?.FindItem(Resource.Id.more_menu_inbox);
        _inboxMenuItem?.SetVisible(false);
        _toolbar.NavigationClick += (_, _) => Activity?.OnBackPressedDispatcher?.OnBackPressed();
        ApplyToolbarNavigation();
        _toolbar.MenuItemClick += (_, e) =>
        {
            if (e.Item?.ItemId == Resource.Id.more_menu_inbox)
            {
                OpenInboxSheet();
            }
        };

        _progress = view.FindViewById<LinearProgressIndicator>(Resource.Id.more_progress)!;
        _summaryStrip = view.FindViewById(Resource.Id.more_summary_strip)!;
        _summaryStrip.Click += (_, _) => OpenHostQuickControl();
        _statusDot = view.FindViewById<ImageView>(Resource.Id.more_status_dot)!;
        _statusTitle = view.FindViewById<TextView>(Resource.Id.more_status_title)!;
        _statusDetail = view.FindViewById<TextView>(Resource.Id.more_status_detail)!;
        view.FindViewById<MaterialButton>(Resource.Id.more_summary_action)!.Click += (_, _) => OpenHostQuickControl();

        var ctx = RequireContext();
        _rowCredentials = view.FindViewById(Resource.Id.more_row_credentials)!;
        Rows.BindNavRow(_rowCredentials, Resource.Drawable.ic_key, GetString(Resource.String.more_credentials),
            subtitle: null, onClick: () => Open(typeof(CredentialsActivity)));

        var rowPlugins = view.FindViewById(Resource.Id.more_row_plugins)!;
        Rows.BindNavRow(rowPlugins, Resource.Drawable.ic_extension, GetString(Resource.String.more_plugins),
            subtitle: null, onClick: () => Open(typeof(PluginsActivity)));

        _rowAutomation = view.FindViewById(Resource.Id.more_row_automation)!;
        Rows.BindNavRow(_rowAutomation, Resource.Drawable.ic_phone, GetString(Resource.String.more_automation),
            subtitle: null, onClick: () => Open(typeof(AutomationActivity)));

        _rowRemote = view.FindViewById(Resource.Id.more_row_remote)!;
        Rows.BindNavRow(_rowRemote, Resource.Drawable.ic_cloud, GetString(Resource.String.more_remote),
            subtitle: null, onClick: () => Open(typeof(RemoteActivity)));

        _rowRuntime = view.FindViewById(Resource.Id.more_row_runtime)!;
        Rows.BindNavRow(_rowRuntime, Resource.Drawable.ic_storage, GetString(Resource.String.more_runtime),
            subtitle: null, onClick: () => Open(typeof(RuntimeActivity)));

        var rowWarmup = view.FindViewById(Resource.Id.more_row_warmup)!;
        // 5A：默认开启的后台预热；保留一句 ≤18 字短副标题，说明可关闭与自动保护。
        _warmupSwitch = Rows.BindSwitchRow(rowWarmup, Resource.Drawable.ic_refresh, GetString(Resource.String.more_warmup),
            GetString(Resource.String.more_warmup_short_sub), UiPrefs.EagerWarmupEnabled(ctx),
            enabled => UiPrefs.SetEagerWarmupEnabled(ctx, enabled));

        var rowAbout = view.FindViewById(Resource.Id.more_row_about)!;
        Rows.BindActionRow(rowAbout, Resource.Drawable.ic_info, GetString(Resource.String.more_about),
            Rows.Fmt(ctx, Resource.String.more_about_sub, AppVersion(ctx)), OnVersionTapped);

        // Polish-P3：外观入口实时显示当前模式，点击弹出清爽对话框选择主题与动态取色。
        _rowAppearance = view.FindViewById(Resource.Id.more_row_appearance)!;
        Rows.BindNavRow(_rowAppearance, Resource.Drawable.ic_palette, GetString(Resource.String.more_appearance),
            subtitle: null, ShowAppearanceDialog);

        var rowLicenses = view.FindViewById(Resource.Id.more_row_licenses)!;
        Rows.BindNavRow(rowLicenses, Resource.Drawable.ic_article, GetString(Resource.String.more_licenses),
            subtitle: null, ShowLicenses);

        _developerDivider = view.FindViewById(Resource.Id.more_developer_divider)!;
        _rowDeveloper = view.FindViewById(Resource.Id.more_row_developer)!;
        Rows.BindNavRow(_rowDeveloper, Resource.Drawable.ic_code, GetString(Resource.String.developer_title),
            subtitle: null, onClick: () => Open(typeof(DeveloperActivity)));

        _scroll = view.FindViewById<NestedScrollView>(Resource.Id.more_scroll)!;
        if (_scroll.ChildCount > 0)
        {
            Edges.ClampMaxWidth(_scroll.GetChildAt(0));
        }

        return view;
    }

    public override void OnStart()
    {
        base.OnStart();
        _bridge.Changed += OnStateChanged;
        _bridge.Agent.Credentials.StateChanged += OnCredentialsChanged;
        _bridge.Agent.Inbox.StateChanged += OnInboxStateChanged;
        Render();
    }

    public override void OnStop()
    {
        _bridge.Changed -= OnStateChanged;
        _bridge.Agent.Credentials.StateChanged -= OnCredentialsChanged;
        _bridge.Agent.Inbox.StateChanged -= OnInboxStateChanged;
        base.OnStop();
    }

    public override void OnResume()
    {
        base.OnResume();
        if (IsAdded && View is not null)
        {
            _isWide = (Activity as MainActivity)?.IsWideLayout ?? false;
            ApplyToolbarNavigation();
            _warmupSwitch.Checked = UiPrefs.EagerWarmupEnabled(RequireContext());
            Render();
        }
    }

    /// <summary>Polish-P3：窄屏设置页顶栏显示 ← 返回首页；宽屏由左侧 NavigationRail 承担导航，不显示箭头。</summary>
    public void OnShellWidthChanged(bool wide)
    {
        _isWide = wide;
        if (IsAdded && View is not null)
        {
            ApplyToolbarNavigation();
        }
    }

    private void ApplyToolbarNavigation()
    {
        if (_isWide)
        {
            _toolbar.NavigationIcon = null;
            return;
        }
        _toolbar.SetNavigationIcon(Resource.Drawable.ic_arrow_back);
        _toolbar.SetNavigationContentDescription(Resource.String.shell_back_home);
    }

    /// <summary>UI-S6：再次点击「更多」页签时平滑回到顶部。</summary>
    public void OnReselected()
    {
        if (View is not null)
        {
            _scroll.SmoothScrollTo(0, 0);
        }
    }

    private void OnStateChanged(object? sender, EventArgs e) => ScheduleRender();

    private void OnCredentialsChanged(Agent.Presentation.CredentialsUiState _) => ScheduleRender();

    private void OnInboxStateChanged(InboxUiState _) => ScheduleRender();

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

        var phaseRes = status.Phase switch
        {
            HomePhase.NotInstalled => Resource.String.home_label_not_installed,
            HomePhase.Starting or HomePhase.WaitingForDsh => Resource.String.home_label_starting,
            HomePhase.Ready => Resource.String.home_label_ready,
            HomePhase.Stopping => Resource.String.home_label_stopping,
            HomePhase.Busy => Resource.String.home_label_busy,
            HomePhase.Failed => Resource.String.home_label_failed,
            _ => Resource.String.home_label_stopped,
        };
        _statusTitle.Text = Rows.Fmt(ctx, Resource.String.more_summary_local_fmt, GetString(phaseRes));

        var version = status.DshVersion;
        var remoteSession = CsRemoteConnection.Session;
        var remoteText = remoteSession is null
            ? GetString(Resource.String.more_overview_remote_disconnected)
            : Rows.Fmt(
                ctx,
                Resource.String.more_overview_remote_connected,
                string.IsNullOrWhiteSpace(remoteSession.Profile.Label) ? remoteSession.Profile.Host : remoteSession.Profile.Label);
        _statusDetail.Text = string.IsNullOrWhiteSpace(version)
            ? remoteText
            : Rows.Fmt(ctx, Resource.String.more_summary_detail_fmt, remoteText, version);

        _statusDot.ImageTintList = ColorStateList.ValueOf(status.Phase == HomePhase.Ready
            ? new Android.Graphics.Color(AndroidX.Core.Content.ContextCompat.GetColor(ctx, Resource.Color.dshm_running))
            : new Android.Graphics.Color(Rows.Color(_statusDot, Resource.Attribute.colorOnSurfaceVariant)));

        RenderInboxEntry(_bridge.Agent.Inbox.State);

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

        RenderAppearance(ctx);

        var devVisible = UiPrefs.DeveloperEnabled(ctx) ? ViewStates.Visible : ViewStates.Gone;
        _developerDivider.Visibility = devVisible;
        _rowDeveloper.Visibility = devVisible;
    }

    /// <summary>Polish-P3：外观行右侧实时显示当前模式，纯淡灰文字、无胶囊底色。</summary>
    private void RenderAppearance(Context ctx)
    {
        var modeRes = UiPrefs.ThemeMode(ctx) switch
        {
            UiPrefs.ThemeModeLight => Resource.String.appearance_light,
            UiPrefs.ThemeModeDark => Resource.String.appearance_dark,
            _ => Resource.String.appearance_system,
        };
        Rows.SetBadge(
            _rowAppearance.FindViewById<TextView>(Resource.Id.row_trailing_badge)!,
            GetString(modeRes),
            Rows.BadgeTone.Neutral);
    }

    private void RenderInboxEntry(InboxUiState inbox)
    {
        if (_inboxMenuItem is null)
        {
            return;
        }

        var total = inbox.TotalCount;
        _inboxMenuItem.SetVisible(total > 0);
        _inboxMenuItem.SetTitle(total > 0
            ? Rows.Fmt(RequireContext(), Resource.String.chat_menu_inbox_fmt, total)
            : GetString(Resource.String.chat_menu_inbox));
    }

    private void OpenInboxSheet()
    {
        var manager = ParentFragmentManager;
        if (manager.FindFragmentByTag(InboxBottomSheet.SheetTag) is null)
        {
            new InboxBottomSheet().Show(manager, InboxBottomSheet.SheetTag);
        }
    }

    private void OpenHostQuickControl()
    {
        var ctx = RequireContext();
        var status = _bridge.Status;
        var (titleText, detailText) = HostQuickControlSupport.Describe(ctx, _bridge, status);
        HostQuickControlBottomSheet.Show(ChildFragmentManager, titleText, detailText);
    }

    public void OnHostQuickAction(HomeAction action) => HostQuickControlSupport.Run(this, _bridge, action);

    public void OnHostQuickOpenRemote() => Open(typeof(RemoteActivity));

    public void OnHostQuickOpenRuntime() => Open(typeof(RuntimeActivity));

    public void OnHostQuickOpenCredentials() => Open(typeof(CredentialsActivity));

    public void OnHostQuickCancelUpdate() => _bridge.CancelUpdate();

    [Obsolete("Uses Fragment activity result for runtime import parity")]
    public override void OnActivityResult(int requestCode, int resultCode, Intent? data)
    {
#pragma warning disable CS0618
        base.OnActivityResult(requestCode, resultCode, data);
#pragma warning restore CS0618
        HostQuickControlSupport.HandleActivityResult(_bridge, requestCode, (Android.App.Result)resultCode, data);
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

    /// <summary>
    /// Polish-P3 外观设置：清爽对话框内选择跟随系统 / 浅色 / 深色，并可开关壁纸动态取色；
    /// 选择后立即写入 <see cref="UiPrefs"/> 并调用 <see cref="UiPrefs.ApplyThemeMode"/>。
    /// 动态取色需要 Application 启动时应用，因此变化时明确提示重启 App 生效。
    /// </summary>
    private void ShowAppearanceDialog()
    {
        var ctx = RequireContext();
        var content = LayoutInflater.From(ctx)!.Inflate(Resource.Layout.dialog_appearance, null, false)!;
        var group = content.FindViewById<RadioGroup>(Resource.Id.appearance_theme_group)!;
        group.Check(UiPrefs.ThemeMode(ctx) switch
        {
            UiPrefs.ThemeModeLight => Resource.Id.appearance_theme_light,
            UiPrefs.ThemeModeDark => Resource.Id.appearance_theme_dark,
            _ => Resource.Id.appearance_theme_system,
        });
        var dynamicSwitch = content.FindViewById<MaterialSwitch>(Resource.Id.appearance_dynamic_switch)!;
        dynamicSwitch.Checked = UiPrefs.DynamicColorEnabled(ctx);

        new MaterialAlertDialogBuilder(ctx)
            .SetTitle(Resource.String.more_appearance)!
            .SetView(content)!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(Resource.String.appearance_apply, (_, _) =>
                ApplyAppearance(group.CheckedRadioButtonId, dynamicSwitch.Checked))!
            .Show();
    }

    private void ApplyAppearance(int checkedId, bool dynamicColor)
    {
        var ctx = RequireContext();
        var mode = checkedId switch
        {
            Resource.Id.appearance_theme_light => UiPrefs.ThemeModeLight,
            Resource.Id.appearance_theme_dark => UiPrefs.ThemeModeDark,
            _ => UiPrefs.ThemeModeSystem,
        };
        var dynamicChanged = UiPrefs.DynamicColorEnabled(ctx) != dynamicColor;
        UiPrefs.SetThemeMode(ctx, mode);
        UiPrefs.SetDynamicColorEnabled(ctx, dynamicColor);
        // 立即持久化并让 AppCompat 应用默认夜间模式（已启动页面会按系统标准自行重建）。
        UiPrefs.ApplyThemeMode(ctx);
        RenderAppearance(ctx);
        if (dynamicChanged)
        {
            Toast(GetString(dynamicColor
                ? Resource.String.appearance_dynamic_on_hint
                : Resource.String.appearance_dynamic_off_hint));
        }
    }

    private void ShowLicenses()
    {
        var ctx = RequireContext();
        Rows.ShowScrollableTextDialog(ctx, Resource.String.more_licenses, GetString(Resource.String.licenses_body));
    }
}
