using AndroidX.AppCompat.App;
using Google.Android.Material.AppBar;

namespace DshMobile.App.Ui;

/// <summary>
/// Base of the secondary screens (ADR-0021): Material 3 theme, edge-to-edge, a top app bar whose back arrow goes back,
/// and the root padded with the system-bar / keyboard insets. Screens set their layout with <see cref="SetScreen"/>;
/// every layout has a <c>toolbar</c> and a <c>screen_root</c>.
/// </summary>
public abstract class DshActivity : AppCompatActivity
{
    protected MaterialToolbar Toolbar { get; private set; } = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Edges.Enable(Window!);
    }

    protected void SetScreen(int layout, int title)
    {
        SetContentView(layout);
        Toolbar = FindViewById<MaterialToolbar>(Resource.Id.toolbar)!;
        Toolbar.SetTitle(title);
        Toolbar.SetNavigationIcon(Resource.Drawable.ic_arrow_back);
        Toolbar.SetNavigationContentDescription(Resource.String.abc_action_bar_up_description);
        Toolbar.NavigationClick += (_, _) => OnBackPressedDispatcher.OnBackPressed();
        Edges.Pad(FindViewById(Resource.Id.screen_root)!);
    }
}
