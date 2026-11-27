using Android.Content.Res;
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
/// 会话卡片操作底部抽屉（全 XML <c>sheet_session_actions.xml</c>），
/// 承接「打开」「重命名」「Fork 分支」「停止当前轮次」「归档 / 取消归档」与「删除对话 / 删除本地缓存」。
/// 标量参数均通过安全的 <c>Arguments</c> 键传递，动作经宿主 <see cref="IHost"/> 回调派发。
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
    private const string ArgIsArchived = "arg_is_archived";
    private const string ArgCanArchive = "arg_can_archive";
    private const string ArgIsCachedOnly = "arg_is_cached_only";
    private const string ArgCanRemove = "arg_can_remove";

    /// <summary>会话卡片可派发的操作类型。</summary>
    public enum Action
    {
        Open,
        Rename,
        Fork,
        Cancel,
        Archive,
        Unarchive,
        Remove,
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
        bool running,
        bool isArchived,
        bool canArchive,
        bool isCachedOnly,
        bool canRemove)
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
        args.PutBoolean(ArgIsArchived, isArchived);
        args.PutBoolean(ArgCanArchive, canArchive);
        args.PutBoolean(ArgIsCachedOnly, isCachedOnly);
        args.PutBoolean(ArgCanRemove, canRemove);

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
        var isArchived = Arguments?.GetBoolean(ArgIsArchived, false) ?? false;
        var canArchive = Arguments?.GetBoolean(ArgCanArchive, false) ?? false;
        var isCachedOnly = Arguments?.GetBoolean(ArgIsCachedOnly, false) ?? false;
        var canRemove = Arguments?.GetBoolean(ArgCanRemove, true) ?? true;

        var online = hostReady && !isCachedOnly;
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
            destructive: false,
            Action.Open);

        BindActionRow(
            view.FindViewById(Resource.Id.sheet_session_rename)!,
            Resource.Drawable.ic_article,
            ctx.GetString(Resource.String.chats_action_rename),
            ctx.GetString(Resource.String.chats_sheet_rename_sub),
            visible: online,
            enabled: online,
            destructive: false,
            Action.Rename);

        BindActionRow(
            view.FindViewById(Resource.Id.sheet_session_fork)!,
            Resource.Drawable.ic_code,
            ctx.GetString(Resource.String.chats_action_fork),
            ctx.GetString(Resource.String.chats_sheet_fork_sub),
            visible: online,
            enabled: online,
            destructive: false,
            Action.Fork);

        BindActionRow(
            view.FindViewById(Resource.Id.sheet_session_cancel)!,
            Resource.Drawable.ic_stop,
            ctx.GetString(Resource.String.chats_action_cancel),
            ctx.GetString(Resource.String.chats_sheet_cancel_sub),
            visible: online && running,
            enabled: canCancel,
            destructive: false,
            Action.Cancel);

        BindActionRow(
            view.FindViewById(Resource.Id.sheet_session_archive)!,
            Resource.Drawable.ic_archive,
            ctx.GetString(Resource.String.chats_action_archive),
            ctx.GetString(Resource.String.chats_sheet_archive_sub),
            visible: online && canArchive && !isArchived,
            enabled: online && canArchive,
            destructive: false,
            Action.Archive);

        BindActionRow(
            view.FindViewById(Resource.Id.sheet_session_unarchive)!,
            Resource.Drawable.ic_unarchive,
            ctx.GetString(Resource.String.chats_action_unarchive),
            ctx.GetString(Resource.String.chats_sheet_unarchive_sub),
            visible: online && canArchive && isArchived,
            enabled: online && canArchive,
            destructive: false,
            Action.Unarchive);

        var showRemove = canRemove && (online || isCachedOnly);
        view.FindViewById(Resource.Id.sheet_session_danger_divider)!.Visibility =
            showRemove ? ViewStates.Visible : ViewStates.Gone;

        BindActionRow(
            view.FindViewById(Resource.Id.sheet_session_remove)!,
            Resource.Drawable.ic_delete,
            ctx.GetString(isCachedOnly ? Resource.String.chats_action_remove_cached : Resource.String.chats_action_remove),
            ctx.GetString(isCachedOnly ? Resource.String.chats_sheet_remove_cached_sub : Resource.String.chats_sheet_remove_sub),
            visible: showRemove,
            enabled: showRemove,
            destructive: true,
            Action.Remove);

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
        bool destructive,
        Action action)
    {
        row.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;
        if (!visible)
        {
            return;
        }

        var icon = row.FindViewById<ImageView>(Resource.Id.row_icon)!;
        var title = row.FindViewById<TextView>(Resource.Id.row_title)!;
        var sub = row.FindViewById<TextView>(Resource.Id.row_subtitle)!;
        icon.SetImageResource(iconRes);
        title.Text = titleText;
        sub.Text = subtitleText;
        sub.Visibility = ViewStates.Visible;

        if (destructive)
        {
            var errorColor = new Android.Graphics.Color(Rows.Color(row, Resource.Attribute.colorError));
            icon.ImageTintList = ColorStateList.ValueOf(errorColor);
            title.SetTextColor(errorColor);
        }

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
