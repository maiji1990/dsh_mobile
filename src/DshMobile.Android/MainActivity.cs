using Android.Content;
using Android.Content.PM;
using Android.Views;
using Android.Widget;
using AndroidX.Activity;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;
using AndroidX.Fragment.App;
using DshMobile.Agent.Presentation;
using DshMobile.App.Chat;
using DshMobile.App.Chats;
using DshMobile.App.Files;
using DshMobile.App.Inbox;
using DshMobile.App.LocalNetwork;
using DshMobile.App.More;
using DshMobile.App.Ui;
using Google.Android.Material.BottomNavigation;
using Google.Android.Material.Button;
using Google.Android.Material.Navigation;
using Google.Android.Material.NavigationRail;

namespace DshMobile.App;

/// <summary>
/// App shell (ADR-0021 + N-06-02 / N-06-03 + PR-09 N-09-01):
/// 会话、工作区与更多三栏采用 Material 3 底栏或 600dp 侧边 NavigationRailView，默认进入原生会话列表；
/// 顶部展示跨会话待办收件箱横幅并在 <c>nav_chats</c> 实时显示待办角标，支持从聊天工具卡直达工作区预览/Diff。
/// </summary>
[Activity(
    Label = "@string/app_name",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    // Width changes update the navigation layout without recreating the current tab.
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden,
    WindowSoftInputMode = SoftInput.AdjustResize)]
public sealed class MainActivity : AppCompatActivity
{
    public const string ExtraNavTab = "main.extra.nav_tab";
    public const string ExtraWorkspacePath = "main.extra.workspace_path";
    public const string ExtraOpenGitDiff = "main.extra.open_git_diff";

    private const string TabKey = "navigation.tab.tag";
    private static readonly (int Id, string Tag)[] Tabs =
    [
        (Resource.Id.nav_chats, "chats"),
        (Resource.Id.nav_files, "files"),
        (Resource.Id.nav_more, "more"),
    ];

    /// <summary>Material 3 window size class "medium": from here on the rail replaces the bottom bar.</summary>
    private const int RailMinWidthDp = 600;

    /// <summary>UI-S6：宽屏双栏左侧会话列表的目标宽度。</summary>
    private const float ChatListPaneWidthDp = 340f;

    /// <summary>UI-S6：嵌入右栏的 <see cref="ChatFragment"/> 固定 tag，保证宽窄切换时无重复实例。</summary>
    private const string DetailChatTag = "main.chat.detail";

    private BottomNavigationView _bar = null!;
    private NavigationRailView _rail = null!;
    private View _root = null!;
    private View _inboxBanner = null!;
    private TextView _inboxBannerText = null!;
    private FrameLayout _tabContent = null!;
    private View _detailPane = null!;
    private View _detailDivider = null!;
    private View _detailEmpty = null!;
    private bool _wide;
    private int _tab = Resource.Id.nav_chats;
    private int _inboxRenderPosted;
    /// <summary>Polish-P3：程序化同步底部 / 侧轨选中态时抑制递归的 ItemSelected 回调。</summary>
    private bool _switchingTabs;

    /// <summary>构造从原生聊天等页面直达工作区文件预览或 Git Diff 的 Intent（N-06-02）。</summary>
    public static Intent CreateWorkspaceIntent(Context context, string relativePath, bool openGitDiff) =>
        new Intent(context, typeof(MainActivity))
            .AddFlags(global::Android.Content.ActivityFlags.ClearTop | global::Android.Content.ActivityFlags.SingleTop)
            .PutExtra(ExtraNavTab, Resource.Id.nav_files)
            .PutExtra(ExtraWorkspacePath, relativePath)
            .PutExtra(ExtraOpenGitDiff, openGitDiff);

    /// <summary>Polish-P3：窄屏隐藏底部 3-Tab，子页据此显示返回首页箭头；宽屏保留侧轨双栏。</summary>
    public bool IsWideLayout => _wide;

    /// <summary>
    /// Polish-P3：公开的轻量导航入口。首页设置图标切到 MoreFragment、底部工作区胶囊切到 FilesFragment，
    /// 以及返回键/深链都走这里，保持底部占位导航与可见侧轨的选中态一致。
    /// </summary>
    public void OpenTab(int navId)
    {
        var target = Tabs.Any(t => t.Id == navId) ? navId : Resource.Id.nav_chats;
        if (_tab != target)
        {
            Show(target);
        }
        SelectNav(target);
    }

    /// <summary>同步两个导航控件的选中态；底部栏在窄屏仅作为不可见状态载体，不参与布局高度。</summary>
    private void SelectNav(int id)
    {
        _switchingTabs = true;
        try
        {
            foreach (NavigationBarView nav in new NavigationBarView[] { _bar, _rail })
            {
                if (nav.SelectedItemId != id)
                {
                    nav.SelectedItemId = id;
                }
            }
        }
        finally
        {
            _switchingTabs = false;
        }
    }

    /// <summary>UI-S6：当前是否处于「宽屏 + 会话页签」的列表-详情双栏模式。</summary>
    public bool IsWideChatsDualPane => _wide && _tab == Resource.Id.nav_chats;

    /// <summary>
    /// UI-S6：在右栏加载或切换会话。已有同类（本地/远程）聊天实例时直接复用并跳转，
    /// 否则在固定的 <see cref="DetailChatTag"/> 上替换为新实例，避免重复 Fragment。
    /// </summary>
    public void OpenChatInDetailPane(string sessionId, bool isRemote, string? focusEventId)
    {
        if (!IsWideChatsDualPane)
        {
            return;
        }

        if (SupportFragmentManager.FindFragmentByTag(DetailChatTag) is ChatFragment existing
            && existing.IsRemote == isRemote)
        {
            existing.NavigateToSession(sessionId, focusEventId);
            _detailEmpty.Visibility = ViewStates.Gone;
            return;
        }

        var args = new Bundle();
        args.PutString(ChatActivity.ExtraSessionId, sessionId);
        if (!string.IsNullOrWhiteSpace(focusEventId))
        {
            args.PutString(ChatActivity.ExtraFocusEventId, focusEventId);
        }
        if (isRemote)
        {
            args.PutBoolean(ChatActivity.ExtraIsRemote, true);
        }
        args.PutBoolean(ChatActivity.ExtraEmbeddedInPane, true);

        var fragment = new ChatFragment { Arguments = args };
        SupportFragmentManager
            .BeginTransaction()
            .Replace(Resource.Id.main_detail_container, fragment, DetailChatTag)
            .SetReorderingAllowed(true)
            .Commit();
        _detailEmpty.Visibility = ViewStates.Gone;
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        var savedTab = SavedTab(savedInstanceState);
        // Previous versions saved a numeric tab ID and can contain the removed terminal tab in FragmentManager state.
        base.OnCreate(savedTab.HasValue ? savedInstanceState : null);
        Edges.Enable(Window!);
        SetContentView(Resource.Layout.activity_main);
        _bar = FindViewById<BottomNavigationView>(Resource.Id.bottom_nav)!;
        _rail = FindViewById<NavigationRailView>(Resource.Id.nav_rail)!;
        _inboxBanner = FindViewById(Resource.Id.main_inbox_banner)!;
        _inboxBannerText = FindViewById<TextView>(Resource.Id.main_inbox_banner_text)!;
        _tabContent = FindViewById<FrameLayout>(Resource.Id.tab_content)!;
        _detailPane = FindViewById(Resource.Id.main_detail_pane)!;
        _detailDivider = FindViewById(Resource.Id.main_detail_divider)!;
        _detailEmpty = FindViewById(Resource.Id.main_detail_empty)!;
        var inboxBtn = FindViewById<MaterialButton>(Resource.Id.main_inbox_banner_btn)!;
        _inboxBanner.Click += (_, _) => OpenInboxSheet();
        inboxBtn.Click += (_, _) => OpenInboxSheet();
        var content = FindViewById(Resource.Id.tab_content)!;
        _root = FindViewById(Resource.Id.screen_root)!;
        ViewCompat.SetOnApplyWindowInsetsListener(_root, new Edges.Listener((v, insets) =>
        {
            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout())!;
            var ime = Edges.ImeVisible(insets);
            // Polish-P3：窄屏不再有底部导航栏，根布局直接避让系统导航栏；宽屏仍由根布局避让。
            // 键盘弹起时交给 tab_content 避让 IME，避免底部双重留白。
            v.SetPadding(bars.Left, bars.Top, bars.Right, ime ? 0 : bars.Bottom);
            // 底部 3-Tab 在窄屏彻底退出布局（Gone 不占高度），宽屏仅显示左侧 NavigationRailView。
            _bar.Visibility = ViewStates.Gone;
            _rail.Visibility = _wide ? ViewStates.Visible : ViewStates.Gone;
            content.SetPadding(0, 0, 0, ime ? insets.GetInsets(WindowInsetsCompat.Type.Ime())!.Bottom : 0);
            return insets;
        }));

        _tab = savedTab ?? Resource.Id.nav_chats;
        foreach (NavigationBarView nav in new NavigationBarView[] { _bar, _rail })
        {
            nav.ItemSelected += (_, e) =>
            {
                e.Handled = true;
                if (_switchingTabs)
                {
                    return;
                }
                Show(e.Item.ItemId);
            };
            nav.ItemReselected += (_, e) => (Current() as ITabRoot)?.OnReselected();
        }
        ApplyWidth(Resources!.Configuration!);
        Show(_tab);
        SelectNav(_tab);
        if (savedInstanceState is null)
        {
            ApplyNavigationIntent(Intent);
        }

        OnBackPressedDispatcher.AddCallback(this, new BackCallback(this));

        // The foreground-service notification is only visible with this permission (Android 13+). The user answers the
        // system dialog; the service runs either way, so a denial is not an error.
        if (savedInstanceState is null && OperatingSystem.IsAndroidVersionAtLeast(33)
            && CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            RequestPermissions([Android.Manifest.Permission.PostNotifications], 1);
        }
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (intent is not null)
        {
            Intent = intent;
            ApplyNavigationIntent(intent);
        }
    }

    protected override void OnStart()
    {
        base.OnStart();
        var inbox = DshMobileApplication.Current.Bridge.Agent.Inbox;
        inbox.StateChanged += OnInboxStateChanged;
        RenderInbox(inbox.State);
        // ADR-0029: 回到前台时按节流间隔静默检查签名通道清单（仅只读拉取清单，不下载不安装）。
        if (UiPrefs.ShouldAutoCheckUpdates(this, DateTimeOffset.UtcNow))
        {
            var channel = UiPrefs.UpdateChannel(this);
            _ = DshMobileApplication.Current.Bridge.RunAutoUpdateCheckAsync(channel,
                () => UiPrefs.SetLastAutoCheckUtcMs(this, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }
        // ADR-0031: 到前台时按用户开关与电源约束静默预热 DSH，让点击「启动 DSH」落在已就绪或接近就绪的宿主上。
        _ = TryEagerWarmupAsync();
    }

    protected override void OnStop()
    {
        DshMobileApplication.Current.Bridge.Agent.Inbox.StateChanged -= OnInboxStateChanged;
        base.OnStop();
    }

    private void OnInboxStateChanged(InboxUiState state)
    {
        if (Interlocked.Exchange(ref _inboxRenderPosted, 1) == 0)
        {
            RunOnUiThread(() =>
            {
                Volatile.Write(ref _inboxRenderPosted, 0);
                RenderInbox(DshMobileApplication.Current.Bridge.Agent.Inbox.State);
            });
        }
    }

    private void RenderInbox(InboxUiState state)
    {
        if (state.TotalCount > 0)
        {
            _inboxBanner.Visibility = ViewStates.Visible;
            _inboxBannerText.Text = Rows.Fmt(this, Resource.String.inbox_banner_fmt, state.TotalCount, state.ApprovalCount, state.QuestionCount);
        }
        else
        {
            _inboxBanner.Visibility = ViewStates.Gone;
        }

        foreach (NavigationBarView nav in new NavigationBarView[] { _bar, _rail })
        {
            if (state.TotalCount > 0)
            {
                var badge = nav.GetOrCreateBadge(Resource.Id.nav_chats);
                badge.SetVisible(true);
                badge.Number = state.TotalCount;
            }
            else
            {
                nav.RemoveBadge(Resource.Id.nav_chats);
            }
        }
    }

    private void OpenInboxSheet()
    {
        if (SupportFragmentManager.FindFragmentByTag(InboxBottomSheet.SheetTag) is null)
        {
            new InboxBottomSheet().Show(SupportFragmentManager, InboxBottomSheet.SheetTag);
        }
    }

    private void ApplyNavigationIntent(Intent? intent)
    {
        if (intent is null)
        {
            return;
        }
        var targetTab = intent.GetIntExtra(ExtraNavTab, 0);
        if (Tabs.Any(t => t.Id == targetTab))
        {
            OpenTab(targetTab);
        }
        var workspacePath = intent.GetStringExtra(ExtraWorkspacePath);
        if (!string.IsNullOrWhiteSpace(workspacePath) &&
            SupportFragmentManager.FindFragmentByTag("files") is FilesFragment files)
        {
            var openDiff = intent.GetBooleanExtra(ExtraOpenGitDiff, false);
            files.OpenWorkspaceTarget(workspacePath, openDiff);
        }
    }

    /// <summary>
    /// 5A：预热只做一次决策（开关 / 用户已停止 / 忙 / 运行层未就绪 / 低电量 / 省电 / 温控），失败只写操作日志。
    /// 静默预热不经过局域网权限门禁（该门禁只在手动启动/远程页触发），因此不会弹出打断用户的系统对话框。
    /// </summary>
    private async Task TryEagerWarmupAsync()
    {
        var enabled = UiPrefs.EagerWarmupEnabled(this);
        if (!enabled)
        {
            return;
        }
        var (lowBattery, powerSave) = UiPrefs.WarmupPower(this);
        try
        {
            await DshMobileApplication.Current.Bridge.TryWarmupAsync(enabled, lowBattery, powerSave).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Android.Util.Log.Warn("DshMobile", "eager warmup failed: " + e.GetType().Name);
        }
    }

    public override void OnConfigurationChanged(Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ApplyWidth(newConfig);
    }

    /// <summary>ADR-0032: the LAN permission request is answered here; the gate lives in <see cref="LocalNetworkAccess"/>.</summary>
    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        LocalNetworkAccess.OnRequestResult(requestCode, grantResults);
    }

    /// <summary>Rail or hidden bottom bar for this width; both stay on the current tab, then insets are re-applied.</summary>
    private void ApplyWidth(Android.Content.Res.Configuration config)
    {
        _wide = config.ScreenWidthDp >= RailMinWidthDp;
        _rail.Visibility = _wide ? ViewStates.Visible : ViewStates.Gone;
        // Polish-P3：底部 3-Tab 在窄屏也保持 Gone，仅保留侧轨在宽屏出现。
        _bar.Visibility = ViewStates.Gone;
        SelectNav(_tab);
        ApplyChatPanes();
        NotifyShellWidth();
        ViewCompat.RequestApplyInsets(_root);
    }

    /// <summary>Polish-P3：把宽窄变化广播给已存在的页签，让子页顶栏返回箭头与会话页悬浮工作区条即时切换。</summary>
    private void NotifyShellWidth()
    {
        foreach (var (_, tag) in Tabs)
        {
            if (SupportFragmentManager.FindFragmentByTag(tag) is ITabRoot root)
            {
                root.OnShellWidthChanged(_wide);
            }
        }
    }

    /// <summary>
    /// UI-S6：只在宽屏 + 会话页签启用双栏；离开双栏（窄屏或切到文件/更多）即移除右栏聊天实例，
    /// 防止同一个会话同时驻留在嵌入式 ChatFragment 与单栏 ChatActivity 中。
    /// </summary>
    private void ApplyChatPanes()
    {
        var dual = IsWideChatsDualPane;
        if (!dual)
        {
            RemoveDetailChat();
        }

        var lp = (LinearLayout.LayoutParams)_tabContent.LayoutParameters!;
        var width = dual ? Rows.Dp(this, ChatListPaneWidthDp) : 0;
        var weight = dual ? 0f : 1f;
        if (lp.Width != width || Math.Abs(lp.Weight - weight) > 0.001f)
        {
            lp.Width = width;
            lp.Weight = weight;
            _tabContent.LayoutParameters = lp;
        }

        _detailDivider.Visibility = dual ? ViewStates.Visible : ViewStates.Gone;
        _detailPane.Visibility = dual ? ViewStates.Visible : ViewStates.Gone;
        UpdateDetailEmptyState();
    }

    private void UpdateDetailEmptyState() =>
        _detailEmpty.Visibility = SupportFragmentManager.FindFragmentByTag(DetailChatTag) is null
            ? ViewStates.Visible
            : ViewStates.Gone;

    private void RemoveDetailChat()
    {
        if (SupportFragmentManager.FindFragmentByTag(DetailChatTag) is not { } detail)
        {
            return;
        }
        SupportFragmentManager.BeginTransaction().Remove(detail).CommitNowAllowingStateLoss();
        _detailEmpty.Visibility = ViewStates.Visible;
    }

    /// <summary>返回键优先关闭右栏详情，再交给页签自身处理，最后才退出应用。</summary>
    private bool CloseDetailPane()
    {
        if (SupportFragmentManager.FindFragmentByTag(DetailChatTag) is null)
        {
            return false;
        }
        RemoveDetailChat();
        return true;
    }

    protected override void OnSaveInstanceState(Bundle outState)
    {
        base.OnSaveInstanceState(outState);
        outState.PutString(TabKey, Tabs.First(tab => tab.Id == _tab).Tag);
    }

    protected override void OnRestoreInstanceState(Bundle savedInstanceState)
    {
        if (SavedTab(savedInstanceState).HasValue)
        {
            base.OnRestoreInstanceState(savedInstanceState);
        }
    }

    private static int? SavedTab(Bundle? state)
    {
        var tag = state?.GetString(TabKey);
        return Tabs.Where(tab => tab.Tag == tag).Select(tab => (int?)tab.Id).FirstOrDefault();
    }

    private void Show(int id)
    {
        var fm = SupportFragmentManager;
        var tx = fm.BeginTransaction();
        if (fm.FindFragmentByTag("home") is { } removedHome)
        {
            // 升级覆盖安装保留状态时若残留旧 "home" 栈标签，统一安全清理避免异常。
            tx.Remove(removedHome);
        }
        foreach (var (tabId, tag) in Tabs)
        {
            var f = fm.FindFragmentByTag(tag);
            if (tabId == id)
            {
                if (f is null)
                {
                    tx.Add(Resource.Id.tab_content, Create(tabId), tag);
                }
                else
                {
                    tx.Show(f);
                }
            }
            else if (f is not null && !f.IsHidden)
            {
                tx.Hide(f);
            }
        }
        tx.SetReorderingAllowed(true);
        tx.CommitNow();
        _tab = id;
        ApplyChatPanes();
        // 保持不可见的底部状态载体与可见侧轨同步到同一页签。
        SelectNav(id);
        (fm.FindFragmentByTag(Tabs.First(t => t.Id == id).Tag) as ITabRoot)?.OnShellWidthChanged(_wide);
    }

    private static AndroidX.Fragment.App.Fragment Create(int id) => id switch
    {
        _ when id == Resource.Id.nav_files => new FilesFragment(),
        _ when id == Resource.Id.nav_more => new MoreFragment(),
        _ => new ChatsFragment(),
    };

    private AndroidX.Fragment.App.Fragment? Current() =>
        SupportFragmentManager.FindFragmentByTag(Tabs.First(t => t.Id == _tab).Tag);

    /// <summary>Back: the tab's own back first (a folder up), then back to Chats, then out of the app.</summary>
    private sealed class BackCallback(MainActivity owner) : OnBackPressedCallback(true)
    {
        public override void HandleOnBackPressed()
        {
            if (owner._wide && owner._tab == Resource.Id.nav_chats && owner.CloseDetailPane())
            {
                return;
            }
            if ((owner.Current() as ITabRoot)?.OnBack() == true)
            {
                return;
            }
            if (owner._tab != Resource.Id.nav_chats)
            {
                // Polish-P3：窄屏子页返回首页同样走 OpenTab，保证底部状态载体与侧轨同步且无残影。
                owner.OpenTab(Resource.Id.nav_chats);
                return;
            }
            Enabled = false;
            owner.OnBackPressedDispatcher.OnBackPressed();
            Enabled = true;
        }
    }
}

/// <summary>A tab fragment's hooks into the shell.</summary>
public interface ITabRoot
{
    /// <summary>Handles back inside the tab (e.g. a folder up); false lets the shell handle it.</summary>
    bool OnBack() => false;

    /// <summary>The tab's item was tapped again (scroll to top, refresh, …).</summary>
    void OnReselected()
    {
    }

    /// <summary>Polish-P3：窄屏 / 宽屏切换时同步子页顶栏返回箭头与首页悬浮工作区条。</summary>
    void OnShellWidthChanged(bool wide)
    {
    }
}
