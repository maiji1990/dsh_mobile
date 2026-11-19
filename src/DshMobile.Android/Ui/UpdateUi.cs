using Android.Content;
using DshMobile.App.Bridge;
using DshMobile.Runtime.Updates;
using Google.Android.Material.Dialog;

namespace DshMobile.App.Ui;

/// <summary>
/// ADR-0029: 首页与运行环境页共用的在线更新摘要与确认弹窗辅助方法。
/// 所有操作均在用户确认后才触发下载与换层，绝不在后台静默停止正在运行的 DSH。
/// </summary>
public static class UpdateUi
{
    public static string DescribePlan(UpdatePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var parts = new List<string>(2);
        if (plan.Runtime is { } runtime)
        {
            parts.Add($"Runtime {runtime.Version}");
        }
        if (plan.Dsh is { } dsh)
        {
            parts.Add($"DSH {dsh.Version}");
        }
        return string.Join(" + ", parts);
    }

    public static long TotalMiB(UpdatePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var totalBytes = (plan.Runtime?.Size ?? 0L) + (plan.Dsh?.Size ?? 0L);
        return Math.Max(1L, totalBytes / (1024L * 1024L));
    }

    public static string DigestHint(UpdatePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var parts = new List<string>(2);
        if (plan.Runtime is { } runtime)
        {
            parts.Add($"Runtime {runtime.Version} ({runtime.Sha256[..12]}…)");
        }
        if (plan.Dsh is { } dsh)
        {
            parts.Add($"DSH {dsh.Version} ({dsh.Sha256[..12]}…)");
        }
        return string.Join(" · ", parts);
    }

    public static void ShowConfirmInstallDialog(Context context, BridgeHost bridge, UpdatePlan plan)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(plan);
        var summary = DescribePlan(plan);
        var mib = TotalMiB(plan);
        var running = bridge.Runtime.HostOwnsProcesses;
        new MaterialAlertDialogBuilder(context)
            .SetIcon(Resource.Drawable.ic_download)!
            .SetTitle(Rows.Fmt(context, Resource.String.update_confirm_title, summary))!
            .SetMessage(Rows.Fmt(context, running ? Resource.String.update_confirm_running : Resource.String.update_confirm_stopped, summary, mib))!
            .SetPositiveButton(Resource.String.update_confirm_action, (_, _) => _ = bridge.RunUpdateInstallAsync(stopConfirmed: true))!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .Show();
    }
}
