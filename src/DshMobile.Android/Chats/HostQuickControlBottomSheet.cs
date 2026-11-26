using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.Fragment.App;
using DshMobile.App.Bridge;
using DshMobile.App.Ui;
using DshMobile.Application.Ui;
using DshMobile.Runtime.Updates;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Button;
using Google.Android.Material.ProgressIndicator;
using FragmentManager = AndroidX.Fragment.App.FragmentManager;

namespace DshMobile.App.Chats;

/// <summary>
/// UI Slice 2 / UI-S6：顶栏宿主状态药丸点击后弹出的快速控制底部抽屉（全 XML <c>sheet_host_quick_control.xml</c>），
/// 支持一步启停 DSH、直达远程主机、查看运行环境与凭据配置。
/// 已统一为 <see cref="BottomSheetDialogFragment"/>：标题/详情等标量经 <c>Arguments</c> 传递，
/// 动作经宿主 <see cref="IHost"/> 回调派发；配置变更后若宿主不可用则安全关闭。
/// </summary>
public sealed class HostQuickControlBottomSheet : BottomSheetDialogFragment
{
    public const string SheetTag = "dsh.host_quick_control.bottom_sheet";

    private const string ArgTitle = "sheet.host.title";
    private const string ArgDetail = "sheet.host.detail";

    /// <summary>宿主（会话页或更多页）回调契约。</summary>
    public interface IHost
    {
        void OnHostQuickAction(HomeAction action);

        void OnHostQuickOpenRemote();

        void OnHostQuickOpenRuntime();

        void OnHostQuickOpenCredentials();

        void OnHostQuickCancelUpdate();
    }

    private IHost? _host;
    private BridgeHost _bridge = null!;

    /// <summary>以 tag 去重打开抽屉；已存在实例时直接复用，避免双击弹出双份。</summary>
    public static void Show(FragmentManager manager, string titleText, string detailText)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (manager.FindFragmentByTag(SheetTag) is not null)
        {
            return;
        }

        var args = new Bundle();
        args.PutString(ArgTitle, titleText);
        args.PutString(ArgDetail, detailText);
        new HostQuickControlBottomSheet { Arguments = args }.Show(manager, SheetTag);
    }

    public override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetStyle(AndroidX.Fragment.App.DialogFragment.StyleNoTitle, Resource.Style.ThemeOverlay_DshMobile_BottomSheetDialog);
    }

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        var view = inflater.Inflate(Resource.Layout.sheet_host_quick_control, container, false)!;
        Edges.Pad(view, top: false, bottom: true);

        _host = ParentFragment as IHost ?? Activity as IHost;
        _bridge = DshMobileApplication.Current.Bridge;
        var ctx = RequireContext();
        var status = _bridge.Status;

        view.FindViewById<TextView>(Resource.Id.sheet_host_title)!.Text =
            Arguments?.GetString(ArgTitle) ?? ctx.GetString(Resource.String.home_title_stopped);
        view.FindViewById<TextView>(Resource.Id.sheet_host_detail)!.Text =
            Arguments?.GetString(ArgDetail) ?? ctx.GetString(Resource.String.chats_host_stopped);

        var pill = view.FindViewById<TextView>(Resource.Id.sheet_host_pill)!;
        var remoteLabel = _bridge.Agent.IsRemoteEndpointReady ? _bridge.Agent.RemoteHostLabel : null;
        ApplyHostPillAppearance(ctx, pill, status.Phase, appendCaret: false, remoteLabel);

        var progress = view.FindViewById<LinearProgressIndicator>(Resource.Id.sheet_host_progress)!;
        var busy = status.Phase is HomePhase.Starting or HomePhase.WaitingForDsh or HomePhase.Stopping or HomePhase.Busy;
        progress.Visibility = busy ? ViewStates.Visible : ViewStates.Gone;
        if (busy && _bridge.Progress is { Total: > 0 } p)
        {
            progress.Indeterminate = false;
            progress.SetProgressCompat((int)Math.Clamp(p.Done * 100 / p.Total, 0, 100), true);
        }
        else if (busy)
        {
            progress.Indeterminate = true;
        }

        var primaryBtn = view.FindViewById<MaterialButton>(Resource.Id.sheet_host_primary)!;
        var secondaryBtn = view.FindViewById<MaterialButton>(Resource.Id.sheet_host_secondary)!;

        // 就绪时主操作直接呈现「停止 DSH」，避免再钻入次级菜单。
        var primaryAction = status.Phase == HomePhase.Ready ? HomeAction.Stop : status.Primary;
        BindActionButton(primaryBtn, primaryAction, status.Phase, () => DispatchHome(primaryAction));

        var secondaryCancelsDownload = busy
            && _bridge.CurrentOperation == HomeStatus.UpdateInstallOperation
            && _bridge.Progress is { Step: UpdatePhases.Downloading };
        if (secondaryCancelsDownload)
        {
            secondaryBtn.Visibility = ViewStates.Visible;
            secondaryBtn.SetText(Resource.String.update_cancel);
            secondaryBtn.SetIconResource(Resource.Drawable.ic_close);
            secondaryBtn.Click += (_, _) => DispatchCancelUpdate();
        }
        else
        {
            var secondaryAction = status.Phase == HomePhase.Ready ? HomeAction.OpenChat : status.Secondary;
            BindActionButton(secondaryBtn, secondaryAction, status.Phase, () => DispatchHome(secondaryAction));
        }

        var remoteBadge = _bridge.Agent.IsRemoteEndpointReady
            ? _bridgeLabelOrDefault(ctx, _bridge.Agent.RemoteHostLabel)
            : null;
        BindNavRow(
            view.FindViewById(Resource.Id.sheet_host_nav_remote)!,
            Resource.Drawable.ic_lan,
            ctx.GetString(Resource.String.more_remote),
            ctx.GetString(Resource.String.host_sheet_nav_remote_sub),
            remoteBadge,
            DispatchOpenRemote);

        BindNavRow(
            view.FindViewById(Resource.Id.sheet_host_nav_runtime)!,
            Resource.Drawable.ic_build,
            ctx.GetString(Resource.String.more_runtime),
            ctx.GetString(Resource.String.host_sheet_nav_runtime_sub),
            status.DshVersion,
            DispatchOpenRuntime);

        BindNavRow(
            view.FindViewById(Resource.Id.sheet_host_nav_credentials)!,
            Resource.Drawable.ic_key,
            ctx.GetString(Resource.String.more_credentials),
            ctx.GetString(Resource.String.host_sheet_nav_creds_sub),
            trailingBadge: null,
            DispatchOpenCredentials);

        return view;
    }

    public override void OnStart()
    {
        base.OnStart();
        if (_host is null)
        {
            // 重建后宿主回调可能已失效：安全关闭，绝不把空引用抛给用户。
            DismissAllowingStateLoss();
            return;
        }

        if (Dialog is BottomSheetDialog sheet)
        {
            sheet.Behavior.SkipCollapsed = true;
            sheet.Behavior.State = BottomSheetBehavior.StateExpanded;
        }
    }

    /// <summary>
    /// 统一设置宿主状态药丸的文案与 Material 3 语义色彩（顶栏与底部抽屉共用）。
    /// UI-S4：就绪健康态只显示绿点 + 简洁名称（本地 DSH / 远程: label），仅启动中、停止中与异常态显示状态词。
    /// </summary>
    public static void ApplyHostPillAppearance(
        Context ctx,
        TextView pill,
        HomePhase phase,
        bool appendCaret,
        string? remoteLabel = null)
    {
        if (phase == HomePhase.Ready)
        {
            var name = string.IsNullOrWhiteSpace(remoteLabel)
                ? ctx.GetString(Resource.String.chats_pill_local_host)
                : ctx.GetString(Resource.String.chats_pill_remote_host_fmt, remoteLabel!);
            pill.Text = appendCaret ? $"{name} ▾" : name;
            pill.SetCompoundDrawablesRelativeWithIntrinsicBounds(Resource.Drawable.bg_live_status_dot, 0, 0, 0);
            pill.CompoundDrawablePadding = Rows.Dp(ctx, 6);
            pill.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(
                new Android.Graphics.Color(Rows.Color(pill, Resource.Attribute.colorSurfaceContainerHigh)));
            pill.SetTextColor(new Android.Graphics.Color(Rows.Color(pill, Resource.Attribute.colorOnSurface)));
            return;
        }

        var (labelRes, bgAttr, fgAttr) = phase switch
        {
            HomePhase.Starting or HomePhase.WaitingForDsh or HomePhase.Busy or HomePhase.Stopping =>
                (phase == HomePhase.Stopping ? Resource.String.chats_pill_stopping : Resource.String.chats_pill_starting,
                 Resource.Attribute.colorTertiaryContainer, Resource.Attribute.colorOnTertiaryContainer),
            HomePhase.Failed => (Resource.String.chats_pill_failed, Resource.Attribute.colorErrorContainer, Resource.Attribute.colorOnErrorContainer),
            HomePhase.NotInstalled => (Resource.String.chats_pill_not_installed, Resource.Attribute.colorSurfaceContainerHigh, Resource.Attribute.colorOnSurfaceVariant),
            _ => (Resource.String.chats_pill_stopped, Resource.Attribute.colorSurfaceContainerHigh, Resource.Attribute.colorOnSurfaceVariant),
        };

        var statusLabel = ctx.GetString(labelRes);
        pill.Text = appendCaret ? $"{statusLabel} ▾" : statusLabel;
        pill.SetCompoundDrawablesRelativeWithIntrinsicBounds(0, 0, 0, 0);
        pill.CompoundDrawablePadding = 0;
        pill.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(
            new Android.Graphics.Color(Rows.Color(pill, bgAttr)));
        pill.SetTextColor(new Android.Graphics.Color(Rows.Color(pill, fgAttr)));
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

    private void DispatchHome(HomeAction action)
    {
        var host = _host;
        DismissAllowingStateLoss();
        host?.OnHostQuickAction(action);
    }

    private void DispatchCancelUpdate()
    {
        var host = _host;
        DismissAllowingStateLoss();
        host?.OnHostQuickCancelUpdate();
    }

    private void DispatchOpenRemote()
    {
        var host = _host;
        DismissAllowingStateLoss();
        host?.OnHostQuickOpenRemote();
    }

    private void DispatchOpenRuntime()
    {
        var host = _host;
        DismissAllowingStateLoss();
        host?.OnHostQuickOpenRuntime();
    }

    private void DispatchOpenCredentials()
    {
        var host = _host;
        DismissAllowingStateLoss();
        host?.OnHostQuickOpenCredentials();
    }
}
