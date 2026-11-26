using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.Fragment.App;
using DshMobile.Agent.Presentation;
using DshMobile.App.Ui;
using Google.Android.Material.BottomSheet;
using FragmentManager = AndroidX.Fragment.App.FragmentManager;

namespace DshMobile.App.Chats;

/// <summary>
/// UI Slice 2 / UI-S6：会话卡片轻量操作底部抽屉（全 XML <c>sheet_session_actions.xml</c>），
/// 承接「打开会话」「重命名」「Fork 分支」「停止当前轮次」，替代原本卡片底部常驻的三按钮栏。
/// 已统一为 <see cref="BottomSheetDialogFragment"/>：标量参数经 <c>Arguments</c> 传递，
/// 动作经宿主 <see cref="IHost"/> 回调派发；tag 去重避免快速双击弹出双份弹窗，
/// 配置变更后若宿主回调不可用则安全关闭而不会抛空引用异常。
/// </summary>
public sealed class SessionActionsBottomSheet : BottomSheetDialogFragment
{
    public const string SheetTag = "dsh.session_actions.bottom_sheet";

    private const string ArgSessionId = "sheet.session.id";
    private const string ArgDisplayTitle = "sheet.session.title";
    private const string ArgCwd = "sheet.session.cwd";
    private const string ArgHostReady = "sheet.session.host_ready";
    private const string ArgCanCancel = "sheet.session.can_cancel";
    private const string ArgRunning = "sheet.session.running";

    /// <summary>会话卡片可派发的轻量动作。</summary>
    public enum Action
    {
        Open,
        Rename,
        Fork,
        Cancel,
    }

    /// <summary>宿主（会话列表页）回调契约；重建后宿主不再适用时由弹窗自行关闭。</summary>
    public interface IHost
    {
        void OnSessionAction(Action action, string sessionId);
    }

    private IHost? _host;
    private string _sessionId = string.Empty;

    /// <summary>以 tag 去重打开抽屉；已存在实例时直接复用，避免双击弹出双份。</summary>
    public static void Show(
        FragmentManager manager,
        string sessionId,
        string? displayTitle,
        string? cwd,
        bool hostReady,
        bool canCancel,
        bool running)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (manager.FindFragmentByTag(SheetTag) is not null)
        {
            return;
        }

        var args = new Bundle();
        args.PutString(ArgSessionId, sessionId);
        if (!string.IsNullOrWhiteSpace(displayTitle))
        {
            args.PutString(ArgDisplayTitle, displayTitle);
        }
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            args.PutString(ArgCwd, cwd);
        }
        args.PutBoolean(ArgHostReady, hostReady);
        args.PutBoolean(ArgCanCancel, canCancel);
        args.PutBoolean(ArgRunning, running);

        new SessionActionsBottomSheet { Arguments = args }.Show(manager, SheetTag);
    }

    public override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetStyle(AndroidX.Fragment.App.DialogFragment.StyleNoTitle, Resource.Style.ThemeOverlay_DshMobile_BottomSheetDialog);
    }

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        var view = inflater.Inflate(Resource.Layout.sheet_session_actions, container, false)!;
        Edges.Pad(view, top: false, bottom: true);

        _host = ParentFragment as IHost ?? Activity as IHost;
        _sessionId = Arguments?.GetString(ArgSessionId) ?? string.Empty;
        var hostReady = Arguments?.GetBoolean(ArgHostReady, false) ?? false;
        var canCancel = Arguments?.GetBoolean(ArgCanCancel, false) ?? false;
        var running = Arguments?.GetBoolean(ArgRunning, false) ?? false;

        var ctx = RequireContext();
        var title = view.FindViewById<TextView>(Resource.Id.sheet_session_title)!;
        var subtitle = view.FindViewById<TextView>(Resource.Id.sheet_session_subtitle)!;
        title.Text = ChatDisplayFormatter.FormatShortSessionTitle(
            _sessionId,
            Arguments?.GetString(ArgDisplayTitle),
            ctx.GetString(Resource.String.chats_untitled_prefix));
        var cwd = Arguments?.GetString(ArgCwd);
        subtitle.Text = string.IsNullOrWhiteSpace(cwd) ? _sessionId : $"{cwd} · {_sessionId}";

        BindActionRow(
            view.FindViewById(Resource.Id.sheet_session_open)!,
            Resource.Drawable.ic_chat,
            ctx.GetString(Resource.String.chats_action_open),
            ctx.GetString(Resource.String.chats_sheet_open_sub),
            visible: true,
            enabled: true,
            Action.Open);

        BindActionRow(
            view.FindViewById(Resource.Id.sheet_session_rename)!,
            Resource.Drawable.ic_article,
            ctx.GetString(Resource.String.chats_action_rename),
            ctx.GetString(Resource.String.chats_sheet_rename_sub),
            visible: hostReady,
            enabled: hostReady,
            Action.Rename);

        BindActionRow(
            view.FindViewById(Resource.Id.sheet_session_fork)!,
            Resource.Drawable.ic_code,
            ctx.GetString(Resource.String.chats_action_fork),
            ctx.GetString(Resource.String.chats_sheet_fork_sub),
            visible: hostReady,
            enabled: hostReady,
            Action.Fork);

        BindActionRow(
            view.FindViewById(Resource.Id.sheet_session_cancel)!,
            Resource.Drawable.ic_stop,
            ctx.GetString(Resource.String.chats_action_cancel),
            ctx.GetString(Resource.String.chats_sheet_cancel_sub),
            visible: hostReady && running,
            enabled: canCancel,
            Action.Cancel);

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

    private void BindActionRow(
        View row,
        int iconRes,
        string titleText,
        string subtitleText,
        bool visible,
        bool enabled,
        Action action)
    {
        row.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;
        if (!visible)
        {
            return;
        }
        row.FindViewById<ImageView>(Resource.Id.row_icon)!.SetImageResource(iconRes);
        row.FindViewById<TextView>(Resource.Id.row_title)!.Text = titleText;
        var sub = row.FindViewById<TextView>(Resource.Id.row_subtitle)!;
        sub.Text = subtitleText;
        sub.Visibility = ViewStates.Visible;
        row.Enabled = enabled;
        row.Alpha = enabled ? 1.0f : 0.48f;
        row.Click += (_, _) =>
        {
            if (enabled)
            {
                Dispatch(action);
            }
        };
    }

    private void Dispatch(Action action)
    {
        var host = _host;
        var sessionId = _sessionId;
        DismissAllowingStateLoss();
        host?.OnSessionAction(action, sessionId);
    }
}
