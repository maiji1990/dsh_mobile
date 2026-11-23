using Android.Content.PM;
using Android.Views;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;
using DshMobile.App.Ui;

namespace DshMobile.App.Plugins;

/// <summary>
/// PR-07 原生插件目录与 L0/L1 受控管理入口 Activity（N-07-02 / ADR-0033）：
/// 承载 <see cref="PluginsFragment"/>，遵循全 XML 布局与边到边 Insets 约定。
/// </summary>
[Activity(
    Label = "@string/plugins_title",
    Exported = false,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden,
    WindowSoftInputMode = SoftInput.AdjustResize)]
public sealed class PluginsActivity : AppCompatActivity
{
    private const string FragmentTag = "native.plugins.fragment";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Edges.Enable(Window!);
        SetContentView(Resource.Layout.activity_plugins);

        var root = FindViewById(Resource.Id.screen_root)!;
        ViewCompat.SetOnApplyWindowInsetsListener(root, new Edges.Listener((v, insets) =>
        {
            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout())!;
            var ime = insets.GetInsets(WindowInsetsCompat.Type.Ime())!;
            v.SetPadding(bars.Left, bars.Top, bars.Right, Math.Max(bars.Bottom, ime.Bottom));
            return insets;
        }));

        if (SupportFragmentManager.FindFragmentByTag(FragmentTag) is null)
        {
            SupportFragmentManager
                .BeginTransaction()
                .Replace(Resource.Id.plugins_fragment_container, new PluginsFragment(), FragmentTag)
                .Commit();
        }
    }
}
