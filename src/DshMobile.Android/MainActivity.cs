using Android.Content.PM;
using Android.Views;
using AndroidX.Activity;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;
using AndroidX.Fragment.App;
using DshMobile.App.Files;
using DshMobile.App.Home;
using DshMobile.App.More;
using DshMobile.App.Terminal;
using DshMobile.App.Ui;
using Google.Android.Material.BottomNavigation;

namespace DshMobile.App;

/// <summary>
/// App shell (ADR-0021): four tabs — Home, Terminal, Files, More — under a Material 3 bottom navigation bar. Tabs are
/// fragments that are shown / hidden rather than replaced, so a terminal session view or a folder position survives a
/// tab switch; the terminal's shells outlive the screen anyway (<c>TerminalSessions</c>). The bottom bar hides while the
/// soft keyboard is up so the terminal keeps its rows.
/// </summary>
[Activity(
    Label = "@string/app_name",
    MainLauncher = true,
    Exported = true,
    // Rotation / keyboard changes keep the terminal WebView; a light / dark switch recreates (the terminal replays).
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden,
    WindowSoftInputMode = SoftInput.AdjustResize)]
public sealed class MainActivity : AppCompatActivity
{
    private const string TabKey = "tab";
    private static readonly (int Id, string Tag)[] Tabs =
    [
        (Resource.Id.nav_home, "home"),
        (Resource.Id.nav_terminal, "terminal"),
        (Resource.Id.nav_files, "files"),
        (Resource.Id.nav_more, "more"),
    ];

    private BottomNavigationView _nav = null!;
    private int _tab = Resource.Id.nav_home;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Edges.Enable(Window!);
        SetContentView(Resource.Layout.activity_main);
        _nav = FindViewById<BottomNavigationView>(Resource.Id.bottom_nav)!;
        var content = FindViewById(Resource.Id.tab_content)!;
        var root = FindViewById(Resource.Id.screen_root)!;
        ViewCompat.SetOnApplyWindowInsetsListener(root, new Edges.Listener((v, insets) =>
        {
            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout())!;
            var ime = Edges.ImeVisible(insets);
            v.SetPadding(bars.Left, bars.Top, bars.Right, 0);
            // With the keyboard up the bar is hidden and the tab ends at the keyboard; otherwise the bar pads itself.
            _nav.Visibility = ime ? ViewStates.Gone : ViewStates.Visible;
            content.SetPadding(0, 0, 0, ime ? insets.GetInsets(WindowInsetsCompat.Type.Ime())!.Bottom : 0);
            return insets;
        }));

        _tab = savedInstanceState?.GetInt(TabKey, Resource.Id.nav_home) ?? Resource.Id.nav_home;
        _nav.ItemSelected += (_, e) =>
        {
            e.Handled = true;
            Show(e.Item.ItemId);
        };
        _nav.ItemReselected += (_, e) => (Current() as ITabRoot)?.OnReselected();
        _nav.SelectedItemId = _tab;
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

    protected override void OnSaveInstanceState(Bundle outState)
    {
        base.OnSaveInstanceState(outState);
        outState.PutInt(TabKey, _tab);
    }

    /// <summary>Switches to a tab (also from other screens' buttons, e.g. Home → Terminal).</summary>
    public void SelectTab(int id) => _nav.SelectedItemId = id;

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
    }

    private static AndroidX.Fragment.App.Fragment Create(int id) => id switch
    {
        _ when id == Resource.Id.nav_terminal => new TerminalFragment(),
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
                owner.SelectTab(Resource.Id.nav_home);
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
