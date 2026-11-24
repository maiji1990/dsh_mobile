using Android.Views;
using AndroidX.AppCompat.App;
using AndroidX.Core.Widget;
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
        var root = FindViewById(Resource.Id.screen_root)!;
        Edges.Pad(root);
        // 所有标准二级页（NestedScrollView）在 w600dp+ 宽屏/横屏自动限制内容宽度至 720dp 并保持滚动条贴右侧边缘。
        if (FindFirstScrollContent(root) is { } scrollContent)
        {
            Edges.ClampMaxWidth(scrollContent);
        }
    }

    private static View? FindFirstScrollContent(View root)
    {
        if (root is NestedScrollView scroll)
        {
            return scroll.ChildCount > 0 ? scroll.GetChildAt(0) : null;
        }
        if (root is ViewGroup group)
        {
            for (var i = 0; i < group.ChildCount; i++)
            {
                if (group.GetChildAt(i) is { } child && FindFirstScrollContent(child) is { } found)
                {
                    return found;
                }
            }
        }
        return null;
    }
}
