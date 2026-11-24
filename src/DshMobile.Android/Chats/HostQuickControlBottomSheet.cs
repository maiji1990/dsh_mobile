using Android.Content;
using Android.Views;
using Android.Widget;
using DshMobile.App.Bridge;
using DshMobile.App.Ui;
using DshMobile.Application.Ui;
using DshMobile.Runtime.Updates;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Button;
using Google.Android.Material.ProgressIndicator;

namespace DshMobile.App.Chats;

/// <summary>
/// UI Slice 2：顶栏宿主状态药丸点击后弹出的快速控制底部抽屉（全 XML <c>sheet_host_quick_control.xml</c>），
/// 支持一步启停 DSH、直达远程主机、查看运行环境与凭据配置。
/// </summary>
internal static class HostQuickControlBottomSheet
{
    public static void Show(
        Context ctx,
        BridgeHost bridge,
        HomeStatus status,
        string titleText,
        string detailText,
        Action<HomeAction> onAction,
        Action onOpenRemote,
        Action onOpenRuntime,
        Action onOpenCredentials)
    {
        var view = LayoutInflater.From(ctx)!.Inflate(Resource.Layout.sheet_host_quick_control, null)!;
        Edges.Pad(view, top: false, bottom: true);

        view.FindViewById<TextView>(Resource.Id.sheet_host_title)!.Text = titleText;
        view.FindViewById<TextView>(Resource.Id.sheet_host_detail)!.Text = detailText;

        var pill = view.FindViewById<TextView>(Resource.Id.sheet_host_pill)!;
        ApplyHostPillAppearance(ctx, pill, status.Phase, appendCaret: false);

        var progress = view.FindViewById<LinearProgressIndicator>(Resource.Id.sheet_host_progress)!;
        var busy = status.Phase is HomePhase.Starting or HomePhase.WaitingForDsh or HomePhase.Stopping or HomePhase.Busy;
        progress.Visibility = busy ? ViewStates.Visible : ViewStates.Gone;
        if (busy && bridge.Progress is { Total: > 0 } p)
        {
            progress.Indeterminate = false;
            progress.SetProgressCompat((int)Math.Clamp(p.Done * 100 / p.Total, 0, 100), true);
        }
        else if (busy)
        {
            progress.Indeterminate = true;
        }

        var dialog = new BottomSheetDialog(ctx);
        dialog.SetContentView(view);

        var primaryBtn = view.FindViewById<MaterialButton>(Resource.Id.sheet_host_primary)!;
        var secondaryBtn = view.FindViewById<MaterialButton>(Resource.Id.sheet_host_secondary)!;

        // 就绪时主操作直接呈现「停止 DSH」，避免再钻入次级菜单。
        var primaryAction = status.Phase == HomePhase.Ready ? HomeAction.Stop : status.Primary;
        BindActionButton(primaryBtn, primaryAction, status.Phase, () =>
        {
            dialog.Dismiss();
            onAction(primaryAction);
        });

        var secondaryCancelsDownload = busy
            && bridge.CurrentOperation == HomeStatus.UpdateInstallOperation
            && bridge.Progress is { Step: UpdatePhases.Downloading };
        if (secondaryCancelsDownload)
        {
            secondaryBtn.Visibility = ViewStates.Visible;
            secondaryBtn.SetText(Resource.String.update_cancel);
            secondaryBtn.SetIconResource(Resource.Drawable.ic_close);
            secondaryBtn.Click += (_, _) =>
            {
                dialog.Dismiss();
                bridge.CancelUpdate();
            };
        }
        else
        {
            var secondaryAction = status.Phase == HomePhase.Ready ? HomeAction.OpenChat : status.Secondary;
            BindActionButton(secondaryBtn, secondaryAction, status.Phase, () =>
            {
                dialog.Dismiss();
                onAction(secondaryAction);
            });
        }

        var remoteBadge = bridge.Agent.IsRemoteEndpointReady
            ? (_bridgeLabelOrDefault(ctx, bridge.Agent.RemoteHostLabel))
            : null;
        BindNavRow(
            view.FindViewById(Resource.Id.sheet_host_nav_remote)!,
            Resource.Drawable.ic_lan,
            ctx.GetString(Resource.String.more_remote),
            ctx.GetString(Resource.String.host_sheet_nav_remote_sub),
            remoteBadge,
            () =>
            {
                dialog.Dismiss();
                onOpenRemote();
            });

        BindNavRow(
            view.FindViewById(Resource.Id.sheet_host_nav_runtime)!,
            Resource.Drawable.ic_build,
            ctx.GetString(Resource.String.more_runtime),
            ctx.GetString(Resource.String.host_sheet_nav_runtime_sub),
            status.DshVersion,
            () =>
            {
                dialog.Dismiss();
                onOpenRuntime();
            });

        BindNavRow(
            view.FindViewById(Resource.Id.sheet_host_nav_credentials)!,
            Resource.Drawable.ic_key,
            ctx.GetString(Resource.String.more_credentials),
            ctx.GetString(Resource.String.host_sheet_nav_creds_sub),
            trailingBadge: null,
            () =>
            {
                dialog.Dismiss();
                onOpenCredentials();
            });

        dialog.Show();
    }

    /// <summary>统一设置宿主状态药丸的文案与 Material 3 语义色彩（顶栏与底部抽屉共用）。</summary>
    public static void ApplyHostPillAppearance(Context ctx, TextView pill, HomePhase phase, bool appendCaret)
    {
        var (labelRes, bgAttr, fgAttr, useRunningGreen) = phase switch
        {
            HomePhase.Ready => (Resource.String.chats_pill_running, Resource.Attribute.colorSecondaryContainer, 0, true),
            HomePhase.Starting or HomePhase.WaitingForDsh or HomePhase.Busy or HomePhase.Stopping =>
                (phase == HomePhase.Stopping ? Resource.String.chats_pill_stopping : Resource.String.chats_pill_starting,
                 Resource.Attribute.colorTertiaryContainer, Resource.Attribute.colorOnTertiaryContainer, false),
            HomePhase.Failed => (Resource.String.chats_pill_failed, Resource.Attribute.colorErrorContainer, Resource.Attribute.colorOnErrorContainer, false),
            HomePhase.NotInstalled => (Resource.String.chats_pill_not_installed, Resource.Attribute.colorSurfaceContainerHigh, Resource.Attribute.colorOnSurfaceVariant, false),
            _ => (Resource.String.chats_pill_stopped, Resource.Attribute.colorSurfaceContainerHigh, Resource.Attribute.colorOnSurfaceVariant, false),
        };

        var label = ctx.GetString(labelRes);
        pill.Text = appendCaret ? $"{label} ▾" : label;
        pill.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(
            new Android.Graphics.Color(Rows.Color(pill, bgAttr)));
        var fgColor = useRunningGreen
            ? new Android.Graphics.Color(ctx.GetColor(Resource.Color.dshm_running))
            : new Android.Graphics.Color(Rows.Color(pill, fgAttr));
        pill.SetTextColor(fgColor);
    }

    private static string _bridgeLabelOrDefault(Context ctx, string? label) =>
        string.IsNullOrWhiteSpace(label) ? ctx.GetString(Resource.String.chats_pill_remote) : label!;

    private static void BindActionButton(MaterialButton button, HomeAction action, HomePhase phase, Action onClick)
    {
        if (action == HomeAction.None)
        {
            button.Visibility = ViewStates.Gone;
            return;
        }
        button.Visibility = ViewStates.Visible;
        var (textRes, iconRes) = action switch
        {
            HomeAction.OnlineInstall => phase switch
            {
                HomePhase.NotInstalled => (Resource.String.home_action_online_setup, Resource.Drawable.ic_download),
                HomePhase.Failed => (Resource.String.home_action_online_retry, Resource.Drawable.ic_download),
                _ => (Resource.String.home_action_online_dsh, Resource.Drawable.ic_download),
            },
            HomeAction.ImportRuntime => (Resource.String.home_action_import, Resource.Drawable.ic_upload),
            HomeAction.ImportDsh => (Resource.String.home_action_import_dsh, Resource.Drawable.ic_upload),
            HomeAction.Start => (phase == HomePhase.Failed ? Resource.String.home_action_retry : Resource.String.home_action_start, Resource.Drawable.ic_play),
            HomeAction.OpenChat => (Resource.String.home_action_open_chat, Resource.Drawable.ic_chat),
            _ => (Resource.String.home_action_stop, Resource.Drawable.ic_stop),
        };
        button.SetText(textRes);
        button.SetIconResource(iconRes);
        button.Click += (_, _) => onClick();
    }

    private static void BindNavRow(
        View row,
        int iconRes,
        string titleText,
        string subtitleText,
        string? trailingBadge,
        Action onClick)
    {
        row.FindViewById<ImageView>(Resource.Id.row_icon)!.SetImageResource(iconRes);
        row.FindViewById<TextView>(Resource.Id.row_title)!.Text = titleText;
        var sub = row.FindViewById<TextView>(Resource.Id.row_subtitle)!;
        sub.Text = subtitleText;
        sub.Visibility = ViewStates.Visible;
        var badge = row.FindViewById<TextView>(Resource.Id.row_trailing_badge)!;
        if (!string.IsNullOrWhiteSpace(trailingBadge))
        {
            badge.Text = trailingBadge;
            badge.Visibility = ViewStates.Visible;
        }
        else
        {
            badge.Visibility = ViewStates.Gone;
        }
        row.Click += (_, _) => onClick();
    }
}
