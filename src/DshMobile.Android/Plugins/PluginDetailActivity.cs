using Android.Content;
using Android.Content.PM;
using Android.Views;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;
using DshMobile.App.Ui;

namespace DshMobile.App.Plugins;

/// <summary>
/// 原生插件详情、官方 <c>settings/*</c> 实时配置与页面扩展兼容引导 Activity（ADR-0034）：
/// 承载 <see cref="PluginDetailFragment"/>，遵循全 XML 布局与边到边 Insets 约定。
/// </summary>
[Activity(
    Label = "@string/plugin_detail_title",
    Exported = false,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden,
    WindowSoftInputMode = SoftInput.AdjustResize)]
public sealed class PluginDetailActivity : AppCompatActivity
{
    public const string ExtraMode = "extra_plugin_mode";
    public const string ExtraTarget = "extra_plugin_target";
    public const string ExtraTitle = "extra_plugin_title";

    public const string ModeInstalled = "installed";
    public const string ModeCatalog = "catalog";

    private const string FragmentTag = "native.plugin.detail.fragment";

    /// <summary>创建已安装插件详情页跳转 Intent。</summary>
    public static Intent CreateInstalledIntent(Context context, string entryIdOrPackageName, string? displayTitle)
    {
        ArgumentNullException.ThrowIfNull(context);
        var intent = new Intent(context, typeof(PluginDetailActivity));
        intent.PutExtra(ExtraMode, ModeInstalled);
        intent.PutExtra(ExtraTarget, entryIdOrPackageName ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(displayTitle))
        {
            intent.PutExtra(ExtraTitle, displayTitle);
        }
        return intent;
    }

    /// <summary>创建远端发现插件详情页跳转 Intent。</summary>
    public static Intent CreateCatalogIntent(Context context, string packageName, string? displayTitle)
    {
        ArgumentNullException.ThrowIfNull(context);
        var intent = new Intent(context, typeof(PluginDetailActivity));
        intent.PutExtra(ExtraMode, ModeCatalog);
        intent.PutExtra(ExtraTarget, packageName ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(displayTitle))
        {
            intent.PutExtra(ExtraTitle, displayTitle);
        }
        return intent;
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Edges.Enable(Window!);
        SetContentView(Resource.Layout.activity_plugin_detail);

        var root = FindViewById(Resource.Id.screen_root)!;
        ViewCompat.SetOnApplyWindowInsetsListener(root, new Edges.Listener((view, insets) =>
        {
            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout())!;
            var ime = insets.GetInsets(WindowInsetsCompat.Type.Ime())!;
            view.SetPadding(bars.Left, bars.Top, bars.Right, Math.Max(bars.Bottom, ime.Bottom));
            return insets;
        }));

        if (SupportFragmentManager.FindFragmentByTag(FragmentTag) is null)
        {
            var mode = Intent?.GetStringExtra(ExtraMode) ?? ModeInstalled;
            var target = Intent?.GetStringExtra(ExtraTarget) ?? string.Empty;
            var title = Intent?.GetStringExtra(ExtraTitle);

            var fragment = PluginDetailFragment.Create(mode, target, title);
            SupportFragmentManager
                .BeginTransaction()
                .Replace(Resource.Id.plugin_detail_fragment_container, fragment, FragmentTag)
                .Commit();
        }
    }
}
