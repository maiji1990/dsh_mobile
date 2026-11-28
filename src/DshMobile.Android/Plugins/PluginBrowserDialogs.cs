using Android.Content;
using Android.Views;
using DshMobile.App.Bridge;
using DshMobile.App.Ui;
using DshMobile.Runtime.Hosting;
using Google.Android.Material.Dialog;
using Google.Android.Material.Snackbar;

namespace DshMobile.App.Plugins;

/// <summary>
/// 插件页与插件详情页共用的安全二次确认弹窗（ADR-0034）：
/// 负责「完整 WebUI 兼容模式」开关确认、本机回环 WebUI 系统浏览器安全打开及合法 HTTPS 外链确认。
/// 绝不将携带启动会话凭证的本机 URI 写入日志、剪贴板或持久化存储。
/// </summary>
internal static class PluginBrowserDialogs
{
    /// <summary>校验 URL 是否为合法的 <c>https://</c> 外部链接。</summary>
    public static bool TryParseSafeHttpsUrl(string? rawUrl, out Uri? parsedUri)
    {
        parsedUri = null;
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return false;
        }
        if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var candidate))
        {
            return false;
        }
        if (!string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        parsedUri = candidate;
        return true;
    }

    /// <summary>弹出安全确认框后通过系统浏览器打开合法 <c>https://</c> 外部链接。</summary>
    public static void ConfirmAndOpenExternalHttpsUrl(Context context, View? snackbarAnchor, string? rawUrl)
    {
        if (!TryParseSafeHttpsUrl(rawUrl, out var parsedUri) || parsedUri is null)
        {
            return;
        }

        new MaterialAlertDialogBuilder(context)
            .SetTitle(Resource.String.chat_external_link_title)!
            .SetMessage(parsedUri.AbsoluteUri)!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(Resource.String.chat_external_link_open, (_, _) =>
            {
                LaunchBrowsableUri(context, snackbarAnchor, parsedUri.AbsoluteUri);
            })!
            .Show();
    }

    /// <summary>
    /// 弹出明确安全二次确认弹窗后，经一次性回环票据代理在应用内浏览器（Custom Tabs）打开本机完整 WebUI。
    /// 启动令牌与 Cookie 只驻留进程内存，绝不进入浏览器 URL、日志或剪贴板。
    /// </summary>
    public static void ConfirmAndOpenLocalWebUi(Context context, View? snackbarAnchor, BridgeHost bridge)
    {
        if (!bridge.CanOpenLocalWebUiPortal)
        {
            ShowSnackbar(snackbarAnchor, Resource.String.plugins_webui_browser_unavailable);
            return;
        }

        new MaterialAlertDialogBuilder(context)
            .SetTitle(Resource.String.plugins_webui_browser_confirm_title)!
            .SetMessage(Resource.String.plugins_webui_browser_confirm_msg)!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(Resource.String.plugins_webui_browser_confirm_open, (_, _) =>
            {
                var portalUri = bridge.TryIssueLocalWebUiPortalUri();
                if (portalUri is null || !CustomTabsPortalLauncher.Launch(context, snackbarAnchor, portalUri))
                {
                    ShowSnackbar(snackbarAnchor, Resource.String.plugins_webui_browser_unavailable);
                }
            })!
            .Show();
    }

    /// <summary>
    /// 远程宿主共用的一次性票据确认流程：<paramref name="canOpen"/> 决定可用性，
    /// <paramref name="issueLaunchUri"/> 在点击「安全打开」后即时签发，交给应用内浏览器打开。
    /// </summary>
    public static void ConfirmAndOpenRemoteWebUi(
        Context context,
        View? snackbarAnchor,
        Func<bool> canOpen,
        Func<Uri?> issueLaunchUri,
        Action onUnavailable)
    {
        ArgumentNullException.ThrowIfNull(canOpen);
        ArgumentNullException.ThrowIfNull(issueLaunchUri);
        ArgumentNullException.ThrowIfNull(onUnavailable);

        if (!canOpen())
        {
            onUnavailable();
            return;
        }

        new MaterialAlertDialogBuilder(context)
            .SetTitle(Resource.String.remote_webui_confirm_title)!
            .SetMessage(Resource.String.remote_webui_confirm_msg)!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(Resource.String.remote_webui_confirm_open, (_, _) =>
            {
                var portalUri = issueLaunchUri();
                if (portalUri is null || !CustomTabsPortalLauncher.Launch(context, snackbarAnchor, portalUri))
                {
                    onUnavailable();
                }
            })!
            .Show();
    }

    /// <summary>弹出二次确认弹窗切换「完整 WebUI 兼容模式」偏好，并按宿主当前状态触发重启或启动。</summary>
    public static void ConfirmToggleFullWebUiCompat(
        Context context,
        View? snackbarAnchor,
        BridgeHost bridge,
        bool targetEnabled,
        Func<Task> restartHostAsync,
        Action? onPreferenceChanged = null)
    {
        var hostRunning = bridge.Runtime.Host?.State == DshHostState.Ready || bridge.Runtime.HostOwnsProcesses;

        if (targetEnabled)
        {
            var confirmTextRes = hostRunning
                ? Resource.String.plugins_webui_compat_enable_confirm
                : Resource.String.plugins_webui_compat_enable_start_confirm;

            new MaterialAlertDialogBuilder(context)
                .SetTitle(Resource.String.plugins_webui_compat_enable_title)!
                .SetMessage(Resource.String.plugins_webui_compat_enable_msg)!
                .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
                .SetPositiveButton(confirmTextRes, (_, _) =>
                {
                    UiPrefs.SetFullWebUiCompatEnabled(context, true);
                    onPreferenceChanged?.Invoke();
                    _ = hostRunning ? restartHostAsync() : bridge.RunStartAsync();
                })!
                .Show();
            return;
        }

        new MaterialAlertDialogBuilder(context)
            .SetTitle(Resource.String.plugins_webui_compat_disable_title)!
            .SetMessage(Resource.String.plugins_webui_compat_disable_msg)!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(Resource.String.plugins_webui_compat_disable_confirm, (_, _) =>
            {
                UiPrefs.SetFullWebUiCompatEnabled(context, false);
                // 关闭兼容模式即失效：立即吊销一次性票据与全部代理会话（不再等宿主重启）。
                bridge.RevokeLocalWebUiPortal();
                onPreferenceChanged?.Invoke();
                if (hostRunning)
                {
                    _ = restartHostAsync();
                }
                else
                {
                    ShowSnackbar(snackbarAnchor, Resource.String.plugins_webui_compat_disable_saved);
                }
            })!
            .Show();
    }

    private static void LaunchBrowsableUri(Context context, View? snackbarAnchor, string absoluteUri)
    {
        try
        {
            var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(absoluteUri));
            intent.AddCategory(Intent.CategoryBrowsable);
            context.StartActivity(intent);
        }
        catch (ActivityNotFoundException)
        {
            ShowSnackbar(snackbarAnchor, Resource.String.plugins_browser_not_found);
        }
    }

    private static void ShowSnackbar(View? anchor, int messageRes)
    {
        if (anchor is not null)
        {
            Snackbar.Make(anchor, messageRes, Snackbar.LengthLong).Show();
        }
    }
}
