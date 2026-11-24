using Android.Content;
using Android.Views;
using Android.Widget;
using DshMobile.Agent.Presentation;
using DshMobile.App.Ui;
using Google.Android.Material.BottomSheet;

namespace DshMobile.App.Chats;

/// <summary>
/// UI Slice 2：会话卡片轻量操作底部抽屉（全 XML <c>sheet_session_actions.xml</c>），
/// 承接「打开会话」「重命名」「Fork 分支」「停止当前轮次」，替代原本卡片底部常驻的三按钮栏。
/// </summary>
internal static class SessionActionsBottomSheet
{
    public static void Show(
        Context ctx,
        SessionListItemView item,
        bool hostReady,
        bool canCancelSession,
        Action onOpen,
        Action onRename,
        Action onFork,
        Action onCancel)
    {
        var view = LayoutInflater.From(ctx)!.Inflate(Resource.Layout.sheet_session_actions, null)!;
        Edges.Pad(view, top: false, bottom: true);

        var title = view.FindViewById<TextView>(Resource.Id.sheet_session_title)!;
        var subtitle = view.FindViewById<TextView>(Resource.Id.sheet_session_subtitle)!;
        title.Text = ChatDisplayFormatter.FormatShortSessionTitle(
            item.SessionId,
            item.DisplayTitle,
            ctx.GetString(Resource.String.chats_untitled_prefix));
        var path = string.IsNullOrWhiteSpace(item.Cwd) ? item.SessionId : $"{item.Cwd} · {item.SessionId}";
        subtitle.Text = path;

        var dialog = new BottomSheetDialog(ctx);
        dialog.SetContentView(view);

        BindActionRow(
            view.FindViewById(Resource.Id.sheet_session_open)!,
            Resource.Drawable.ic_chat,
            ctx.GetString(Resource.String.chats_action_open),
            ctx.GetString(Resource.String.chats_sheet_open_sub),
            visible: true,
            enabled: true,
            () =>
            {
                dialog.Dismiss();
                onOpen();
            });

        BindActionRow(
            view.FindViewById(Resource.Id.sheet_session_rename)!,
            Resource.Drawable.ic_article,
            ctx.GetString(Resource.String.chats_action_rename),
            ctx.GetString(Resource.String.chats_sheet_rename_sub),
            visible: hostReady,
            enabled: hostReady,
            () =>
            {
                dialog.Dismiss();
                onRename();
            });

        BindActionRow(
            view.FindViewById(Resource.Id.sheet_session_fork)!,
            Resource.Drawable.ic_code,
            ctx.GetString(Resource.String.chats_action_fork),
            ctx.GetString(Resource.String.chats_sheet_fork_sub),
            visible: hostReady,
            enabled: hostReady,
            () =>
            {
                dialog.Dismiss();
                onFork();
            });

        BindActionRow(
            view.FindViewById(Resource.Id.sheet_session_cancel)!,
            Resource.Drawable.ic_stop,
            ctx.GetString(Resource.String.chats_action_cancel),
            ctx.GetString(Resource.String.chats_sheet_cancel_sub),
            visible: hostReady && item.Running,
            enabled: canCancelSession,
            () =>
            {
                dialog.Dismiss();
                onCancel();
            });

        dialog.Show();
    }

    private static void BindActionRow(
        View row,
        int iconRes,
        string titleText,
        string subtitleText,
        bool visible,
        bool enabled,
        Action onClick)
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
                onClick();
            }
        };
    }
}
