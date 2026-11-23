using Android.Content.PM;
using Android.Views;
using AndroidX.Activity;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;
using AndroidX.Fragment.App;
using DshMobile.App.Chats;
using DshMobile.App.Files;
using DshMobile.App.Home;
using DshMobile.App.LocalNetwork;
using DshMobile.App.More;
using DshMobile.App.Ui;
using Google.Android.Material.BottomNavigation;
using Google.Android.Material.Navigation;
using Google.Android.Material.NavigationRail;

namespace DshMobile.App;

/// <summary>
/// App shell (ADR-0021): Home, Files, and More use a Material 3 bottom bar or a navigation rail at 600dp.
/// Tab fragments remain attached to preserve their state across navigation and window size changes.
/// </summary>
[Activity(
    Label = "@string/app_name",
    MainLauncher = true,
    Exported = true,
    // Width changes update the navigation layout without recreating the current tab.
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden,
    WindowSoftInputMode = SoftInput.AdjustResize)]
public sealed class MainActivity : AppCompatActivity
{
    private const string TabKey = "navigation.tab.tag";
    private static readonly (int Id, string Tag)[] Tabs =
    [
        (Resource.Id.nav_home, "home"),
        (Resource.Id.nav_chats, "chats"),
        (Resource.Id.nav_files, "files"),
        (Resource.Id.nav_more, "more"),
    ];

    /// <summary>Material 3 window size class "medium": from here on the rail replaces the bottom bar.</summary>
    private const int RailMinWidthDp = 600;

    private BottomNavigationView _bar = null!;
    private NavigationRailView _rail = null!;
    private View _root = null!;
    private bool _wide;
    private int _tab = Resource.Id.nav_home;

    /// <summary>The navigation that is shown now.</summary>
    private NavigationBarView Nav => _wide ? _rail : _bar;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        var savedTab = SavedTab(savedInstanceState);
        // Previous versions saved a numeric tab ID and can contain the removed terminal tab in FragmentManager state.
        base.OnCreate(savedTab.HasValue ? savedInstanceState : null);
        Edges.Enable(Window!);
        SetContentView(Resource.Layout.activity_main);
        _bar = FindViewById<BottomNavigationView>(Resource.Id.bottom_nav)!;
        _rail = FindViewById<NavigationRailView>(Resource.Id.nav_rail)!;
        var content = FindViewById(Resource.Id.tab_content)!;
        _root = FindViewById(Resource.Id.screen_root)!;
        ViewCompat.SetOnApplyWindowInsetsListener(_root, new Edges.Listener((v, insets) =>
        {
            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout())!;
            var ime = Edges.ImeVisible(insets);
            // The bottom bar pads itself for the navigation bar; with the rail the root does. With the keyboard up the
            // bottom bar is hidden and the tab ends at the keyboard (the rail stays: it takes no rows).
            v.SetPadding(bars.Left, bars.Top, bars.Right, _wide && !ime ? bars.Bottom : 0);
            _bar.Visibility = _wide || ime ? ViewStates.Gone : ViewStates.Visible;
            _rail.Visibility = _wide ? ViewStates.Visible : ViewStates.Gone;
            content.SetPadding(0, 0, 0, ime ? insets.GetInsets(WindowInsetsCompat.Type.Ime())!.Bottom : 0);
            return insets;
        }));

        _tab = savedTab ?? Resource.Id.nav_home;
        foreach (NavigationBarView nav in new NavigationBarView[] { _bar, _rail })
        {
            nav.ItemSelected += (_, e) =>
            {
                e.Handled = true;
                Show(e.Item.ItemId);
            };
            nav.ItemReselected += (_, e) => (Current() as ITabRoot)?.OnReselected();
        }
        ApplyWidth(Resources!.Configuration!);
        Show(_tab);

        OnBackPressedDispatcher.AddCallback(this, new BackCallback(this));

        // The foreground-service notification is only visible with this permission (Android 13+). The user answers the
        // system dialog; the service runs either way, so a denial is not an error.
        if (savedInstanceState is null && OperatingSystem.IsAndroidVersionAtLeast(33)
            && CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            RequestPermissions([Android.Manifest.Permission.PostNotifications], 1);
        }
    }

    protected override void OnStart()
    {
        base.OnStart();
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

    /// <summary>ADR-0031：预热只做一次决策（开关 / 用户已停止 / 忙 / 运行层未就绪 / 低电量 / 省电），失败只写操作日志。</summary>
    private async Task TryEagerWarmupAsync()
    {
        if (!UiPrefs.EagerWarmupEnabled(this))
        {
            return;
        }
        var (lowBattery, powerSave) = UiPrefs.WarmupPower(this);
        try
        {
            await DshMobileApplication.Current.Bridge.TryWarmupAsync(true, lowBattery, powerSave).ConfigureAwait(false);
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

    /// <summary>Rail or bottom bar for this width; the shown one gets the current tab, then the insets are re-applied.</summary>
    private void ApplyWidth(Android.Content.Res.Configuration config)
    {
        _wide = config.ScreenWidthDp >= RailMinWidthDp;
        _rail.Visibility = _wide ? ViewStates.Visible : ViewStates.Gone;
        _bar.Visibility = _wide ? ViewStates.Gone : ViewStates.Visible;
        if (Nav.SelectedItemId != _tab)
        {
            Nav.SelectedItemId = _tab;
        }
        ViewCompat.RequestApplyInsets(_root);
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
        // Keep the hidden navigation on the same tab for the next width change (no listener runs for an equal id).
        foreach (NavigationBarView nav in new NavigationBarView[] { _bar, _rail })
        {
            if (nav != Nav && nav.SelectedItemId != id)
            {
                nav.Menu.FindItem(id)?.SetChecked(true);
            }
        }
    }

    private static AndroidX.Fragment.App.Fragment Create(int id) => id switch
    {
        _ when id == Resource.Id.nav_chats => new ChatsFragment(),
        _ when id == Resource.Id.nav_files => new FilesFragment(),
        _ when id == Resource.Id.nav_more => new MoreFragment(),
        _ => new HomeFragment(),
    };

    private AndroidX.Fragment.App.Fragment? Current() =>
        SupportFragmentManager.FindFragmentByTag(Tabs.First(t => t.Id == _tab).Tag);

    /// <summary>Back: the tab's own back first (a folder up), then to Home, then out of the app.</summary>
    private sealed class BackCallback(MainActivity owner) : OnBackPressedCallback(true)
    {
        public override void HandleOnBackPressed()
        {
            if ((owner.Current() as ITabRoot)?.OnBack() == true)
            {
                return;
            }
            if (owner._tab != Resource.Id.nav_home)
            {
                owner.Nav.SelectedItemId = Resource.Id.nav_home;
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
}
