using Android.Content;
using Android.Content.PM;
using Android.Views;
using Google.Android.Material.Snackbar;

namespace DshMobile.App.Plugins;

/// <summary>
/// 应用内浏览器（Custom Tabs）启动器（ADR-0036）：只接受本机回环一次性票据代理签发的地址
/// （<c>http://127.0.0.1:&lt;port&gt;/__dshmobile/launch?nonce=…</c>），绝不接受携带 <c>token=</c>
/// 的启动地址，因而启动令牌不会出现在浏览器 URL、日志或历史记录中。
/// </summary>
internal static class CustomTabsPortalLauncher
{
    private const string LaunchPath = "/__dshmobile/launch";
    private const string CustomTabsServiceAction = "android.support.customtabs.action.CustomTabsService";
    private const string SessionExtra = "android.support.customtabs.extra.SESSION";
    private const string ShareStateExtra = "androidx.browser.customtabs.extra.SHARE_STATE";
    private const string TitleVisibilityExtra = "android.support.customtabs.extra.TITLE_VISIBILITY";
    private const string UrlBarHidingExtra = "android.support.customtabs.extra.ENABLE_URLBAR_HIDING";
    private const string ToolbarColorExtra = "android.support.customtabs.extra.TOOLBAR_COLOR";
    private const int ShareStateOn = 2;
    private const int TitleVisibilityShow = 1;
    private const int HttpProbePort = 80;
    /// <summary>PackageManager.MATCH_DEFAULT_ONLY：只解析声明了 CATEGORY_DEFAULT 的浏览器。</summary>
    private const int MatchDefaultOnly = 0x00010000;
    private const int DefaultToolbarColor = unchecked((int)0xFF1B1B1F);

    /// <summary>
    /// 通过支持 Custom Tabs 的浏览器打开一次性票据地址；找不到浏览器时提示并返回 <c>false</c>。
    /// </summary>
    /// <param name="context">启动上下文；非 <see cref="Android.App.Activity"/> 时自动补 <see cref="ActivityFlags.NewTask"/>。</param>
    /// <param name="snackbarAnchor">失败提示的锚点视图；为空时只返回失败。</param>
    /// <param name="portalLaunchUri">回环代理签发的一次性票据地址。</param>
    public static bool Launch(Context context, View? snackbarAnchor, Uri portalLaunchUri)
    {
        if (!IsTrustedPortalUri(portalLaunchUri))
        {
            ShowSnackbar(snackbarAnchor);
            return false;
        }

        var target = global::Android.Net.Uri.Parse(portalLaunchUri.AbsoluteUri)!;
        var supportingPackages = ResolveCustomTabsPackages(context);
        var defaultBrowser = ResolveDefaultBrowserPackage(context);
        var preferred = supportingPackages.FirstOrDefault(p => string.Equals(p, defaultBrowser, StringComparison.Ordinal))
            ?? supportingPackages.FirstOrDefault();

        if (preferred is not null)
        {
            if (TryStart(context, BuildIntent(context, target, preferred)))
            {
                return true;
            }
            // 指定包名不可用：清空包名后按系统默认浏览器重试一次。
            if (TryStart(context, BuildIntent(context, target, null)))
            {
                return true;
            }
        }
        else
        {
            // 未查询到声明 Custom Tabs 服务的浏览器：不设包名，交给系统解析默认浏览器。
            if (TryStart(context, BuildIntent(context, target, null)))
            {
                return true;
            }
        }

        ShowSnackbar(snackbarAnchor);
        return false;
    }

    /// <summary>防御性校验：仅接受本机回环、固定路径、合法端口且不含启动令牌的一次性票据地址。</summary>
    private static bool IsTrustedPortalUri(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return false;
        }
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal))
        {
            return false;
        }
        if (!string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal))
        {
            return false;
        }
        if (uri.Port is < 1 or > 65535)
        {
            return false;
        }
        if (!string.Equals(uri.AbsolutePath, LaunchPath, StringComparison.Ordinal))
        {
            return false;
        }
        // 启动令牌只允许留在进程内，URL 中出现 token= 一律拒绝。
        return !uri.AbsoluteUri.Contains("token=", StringComparison.OrdinalIgnoreCase) &&
               !uri.Query.Contains("token=", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>构造 Custom Tabs 打开意图，写入标准 Extras 与当前主题的工具栏颜色。</summary>
    private static Intent BuildIntent(Context context, global::Android.Net.Uri target, string? packageName)
    {
        var intent = new Intent(Intent.ActionView, target);
        intent.AddCategory(Intent.CategoryBrowsable);
        if (!(context is Android.App.Activity))
        {
            intent.AddFlags(ActivityFlags.NewTask);
        }
        if (packageName is not null)
        {
            intent.SetPackage(packageName);
        }

        var extras = new Android.OS.Bundle();
        extras.PutBinder(SessionExtra, null);
        extras.PutInt(ShareStateExtra, ShareStateOn);
        extras.PutInt(TitleVisibilityExtra, TitleVisibilityShow);
        extras.PutBoolean(UrlBarHidingExtra, true);
        extras.PutInt(ToolbarColorExtra, ResolveToolbarColor(context));
        intent.PutExtras(extras);
        return intent;
    }

    /// <summary>启动意图；只把「没有可处理的应用」当作可回退的失败。</summary>
    private static bool TryStart(Context context, Intent intent)
    {
        try
        {
            context.StartActivity(intent);
            return true;
        }
        catch (ActivityNotFoundException)
        {
            return false;
        }
    }

    /// <summary>查询声明 <c>CustomTabsService</c> 的浏览器包名（去重，保持系统返回顺序）。</summary>
    private static List<string> ResolveCustomTabsPackages(Context context)
    {
        var manager = context.PackageManager;
        if (manager is null)
        {
            return [];
        }
        var serviceIntent = new Intent(CustomTabsServiceAction);
#pragma warning disable CA1422 // 本绑定未提供可用的 ResolveInfoFlags 枚举重载，统一使用 int flags（与仓库既有查询一致）。
        var resolved = manager.QueryIntentServices(serviceIntent, 0);
#pragma warning restore CA1422

        var packages = new List<string>();
        if (resolved is null)
        {
            return packages;
        }
        foreach (var info in resolved)
        {
            var package = info.ServiceInfo?.PackageName;
            if (!string.IsNullOrWhiteSpace(package) && !packages.Contains(package, StringComparer.Ordinal))
            {
                packages.Add(package!);
            }
        }
        return packages;
    }

    /// <summary>解析默认 HTTP 浏览器的包名；解析不到时返回 <c>null</c>。</summary>
    private static string? ResolveDefaultBrowserPackage(Context context)
    {
        var manager = context.PackageManager;
        if (manager is null)
        {
            return null;
        }
        var probe = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse($"http://127.0.0.1:{HttpProbePort}/"));
        probe.AddCategory(Intent.CategoryBrowsable);
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            // 本绑定把 ResolveInfoFlags 暴露为包装类，用 Of(...) 构造 MATCH_DEFAULT_ONLY 标志。
            var flags = PackageManager.ResolveInfoFlags.Of(MatchDefaultOnly);
            return manager.ResolveActivity(probe, flags)?.ActivityInfo?.PackageName;
        }
#pragma warning disable CA1422 // API 33 以下只有 int flags 的查询重载，语义相同。
        var matches = manager.QueryIntentActivities(probe, 0);
#pragma warning restore CA1422
        return matches?.FirstOrDefault()?.ActivityInfo?.PackageName;
    }

    /// <summary>从当前主题解析 <c>colorSurface</c> 作为 Custom Tabs 工具栏颜色。</summary>
    private static int ResolveToolbarColor(Context context)
    {
        var value = new Android.Util.TypedValue();
        if (context.Theme?.ResolveAttribute(Resource.Attribute.colorSurface, value, true) == true)
        {
            // 主题属性可能直接给出颜色值，也可能给出颜色资源引用。
            return value.ResourceId != 0 ? context.GetColor(value.ResourceId) : value.Data;
        }
        return DefaultToolbarColor;
    }

    private static void ShowSnackbar(View? anchor)
    {
        if (anchor is not null)
        {
            Snackbar.Make(anchor, Resource.String.plugins_browser_not_found, Snackbar.LengthLong).Show();
        }
    }
}
