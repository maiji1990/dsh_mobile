using Android.App;
using Android.Content;
using Android.Widget;
using AndroidX.Fragment.App;
using DshMobile.App.Bridge;
using DshMobile.App.Chat;
using DshMobile.App.LocalNetwork;
using DshMobile.App.RuntimeUi;
using DshMobile.App.Ui;
using DshMobile.Application.LocalNetwork;
using DshMobile.Application.Ui;
using DshMobile.Runtime.Hosting;
using DshMobile.Runtime.Migration;
using DshMobile.Runtime.Updates;
using Google.Android.Material.Dialog;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace DshMobile.App.Chats;

/// <summary>
/// UI-S6：宿主快速控制抽屉与会话首页/更多页共用的文案与动作执行逻辑。
/// 这样两个页签打开同一个 <see cref="HostQuickControlBottomSheet"/> 时不会各写一套启动、安装与导入分支，
/// 同时保持原有局域网权限门禁、更新安装确认与导入结果回传逻辑不变。
/// </summary>
public static class HostQuickControlSupport
{
    /// <summary>生成抽屉标题与详情（失败 &gt; 未安装/停止 &gt; 启动中 &gt; 就绪的既有优先级）。</summary>
    public static (string Title, string Detail) Describe(Context ctx, BridgeHost bridge, HomeStatus status)
    {
        var version = status.DshVersion;
        return status.Phase switch
        {
            HomePhase.NotInstalled => (
                ctx.GetString(Resource.String.home_title_not_installed),
                ctx.GetString(Resource.String.home_detail_not_installed)),
            HomePhase.Stopped => (
                ctx.GetString(Resource.String.home_title_stopped),
                version is null ? ctx.GetString(Resource.String.home_detail_stopped) : ctx.GetString(Resource.String.chats_host_stopped)),
            HomePhase.Starting or HomePhase.WaitingForDsh => (
                ctx.GetString(Resource.String.home_title_starting),
                StartingPhaseDetail(ctx, bridge)),
            HomePhase.Stopping => (
                ctx.GetString(Resource.String.home_title_stopping),
                ctx.GetString(Resource.String.home_detail_stopping)),
            HomePhase.Busy => (
                Rows.Fmt(ctx, Resource.String.home_title_busy, OperationNames.Label(ctx, status.Operation ?? "")),
                bridge.Progress is { } p
                    ? (p.Total > 0
                        ? Rows.Fmt(ctx, Resource.String.home_detail_progress, p.Step, (int)Math.Clamp(p.Done * 100 / p.Total, 0, 100))
                        : p.Step)
                    : ctx.GetString(Resource.String.home_detail_busy)),
            HomePhase.Ready => (
                ctx.GetString(Resource.String.home_title_ready),
                Rows.Fmt(ctx, Resource.String.home_detail_ready, version ?? "?")),
            _ => (
                ctx.GetString(Resource.String.home_title_failed),
                OperationNames.Error(status.ErrorCode ?? "?", status.ErrorMessage)),
        };
    }

    /// <summary>执行宿主快速控制动作（启动 / 停止 / 在线安装 / 导入 / 打开会话）。</summary>
    public static void Run(Fragment owner, BridgeHost bridge, HomeAction action)
    {
        ArgumentNullException.ThrowIfNull(owner);
        switch (action)
        {
            case HomeAction.OnlineInstall:
                _ = StartOnlineInstallFlowAsync(owner, bridge);
                break;
            case HomeAction.ImportRuntime:
#pragma warning disable CS0618
                owner.StartActivityForResult(RuntimeImportFlow.PickIntent(), RuntimeImportFlow.RequestCode);
#pragma warning restore CS0618
                break;
            case HomeAction.ImportDsh:
#pragma warning disable CS0618
                owner.StartActivityForResult(RuntimeImportFlow.PickDshIntent(), RuntimeImportFlow.DshRequestCode);
#pragma warning restore CS0618
                break;
            case HomeAction.Start:
                _ = StartDshAsync(owner, bridge);
                break;
            case HomeAction.OpenChat:
                owner.StartActivity(new Intent(owner.RequireContext(), typeof(ChatActivity)));
                break;
            case HomeAction.Stop:
                _ = bridge.RunStopAsync();
                break;
        }
    }

    /// <summary>处理运行环境包 / DSH 层导入结果；返回 true 表示该结果归本流程所有。</summary>
    public static bool HandleActivityResult(BridgeHost bridge, int requestCode, Result resultCode, Intent? data)
    {
        if (resultCode != Result.Ok || data?.Data is not { } uri)
        {
            return false;
        }
        if (requestCode == RuntimeImportFlow.RequestCode)
        {
            _ = RuntimeImportFlow.ImportAsync(uri);
            return true;
        }
        if (requestCode == RuntimeImportFlow.DshRequestCode)
        {
            _ = RuntimeImportFlow.ImportDshAsync(uri);
            return true;
        }
        return false;
    }

    private static string StartingPhaseDetail(Context ctx, BridgeHost bridge) => bridge.HostPhase switch
    {
        DshHostPhase.SandboxStarting => ctx.GetString(Resource.String.home_detail_phase_sandbox),
        DshHostPhase.SandboxReady => ctx.GetString(Resource.String.home_detail_phase_sandbox_ready),
        DshHostPhase.DshStarting => ctx.GetString(Resource.String.home_detail_phase_dsh_spawned),
        DshHostPhase.DshFirstOutput => ctx.GetString(Resource.String.home_detail_phase_first_output),
        DshHostPhase.DshTokenAvailable => ctx.GetString(Resource.String.home_detail_phase_token),
        DshHostPhase.ProtocolConnecting => ctx.GetString(Resource.String.home_detail_phase_protocol),
        DshHostPhase.Initializing => ctx.GetString(Resource.String.home_detail_phase_initializing),
        _ => ctx.GetString(Resource.String.home_detail_starting),
    };

    /// <summary>
    /// 5A：手动启动前走一次局域网权限门禁（与首页一致）；静默预热不经过这里。
    /// </summary>
    private static async Task StartDshAsync(Fragment owner, BridgeHost bridge)
    {
        var decision = LocalNetworkDecision.NotRequired;
        if (owner.Activity is { } activity && !UiPrefs.LanPermissionAsked(activity))
        {
            UiPrefs.SetLanPermissionAsked(activity, true);
            try
            {
                decision = await LocalNetworkAccess.EnsureAsync(activity).ConfigureAwait(true);
            }
            catch (Exception e)
            {
                Android.Util.Log.Warn("DshMobile", "lan permission gate failed: " + e.GetType().Name);
            }
        }
        if (decision == LocalNetworkDecision.NeedsPermission && owner.Context is { } ctx)
        {
            Toast.MakeText(ctx, ctx.GetString(Resource.String.lan_denied_toast), ToastLength.Long)?.Show();
        }
        await bridge.RunStartAsync().ConfigureAwait(false);
    }

    private static async Task StartOnlineInstallFlowAsync(Fragment owner, BridgeHost bridge)
    {
        var context = owner.Context;
        if (context is null)
        {
            return;
        }
        var channel = UiPrefs.UpdateChannel(context);
        var check = await bridge.RunUpdateCheckForResultAsync(channel).ConfigureAwait(false);
        owner.Activity?.RunOnUiThread(() =>
        {
            if (owner.View is null || owner.Context is not { } ctx || check is not { Ok: true })
            {
                return;
            }
            var plan = UpdatePlanner.Plan(check.Choices);
            if (!plan.HasWork)
            {
                var detail = plan.Notes.Count > 0
                    ? string.Join('\n', plan.Notes)
                    : UpdateTransactionCodes.NothingToInstall;
                new MaterialAlertDialogBuilder(ctx)
                    .SetIcon(Resource.Drawable.ic_info)!
                    .SetTitle(Resource.String.update_nothing_title)!
                    .SetMessage(detail)!
                    .SetPositiveButton(Android.Resource.String.Ok, (_, _) => { })!
                    .Show();
                return;
            }
            UpdateUi.ShowConfirmInstallDialog(ctx, bridge, plan);
        });
    }
}
