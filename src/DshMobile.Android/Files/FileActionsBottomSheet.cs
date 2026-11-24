using Android.Content;
using Android.Content.Res;
using Android.Views;
using Android.Widget;
using DshMobile.App.Ui;
using DshMobile.Core.Files;
using Google.Android.Material.BottomSheet;

namespace DshMobile.App.Files;

/// <summary>
/// UI Slice 3：工作区文件/目录底部操作抽屉（全 XML <c>sheet_file_actions.xml</c>），
/// 严格按 <see cref="IFileProvider.CapabilitiesOf"/> 裁剪能力，并以错误语义色高亮「删除」操作。
/// </summary>
internal static class FileActionsBottomSheet
{
    public static void Show(
        Context ctx,
        WorkspaceEntry entry,
        FileCapabilities caps,
        Action onOpenOrEdit,
        Action onExport,
        Action onExtract,
        Action onRename,
        Action onCopy,
        Action onCut,
        Action onZip,
        Action onDelete)
    {
        var view = LayoutInflater.From(ctx)!.Inflate(Resource.Layout.sheet_file_actions, null)!;
        Edges.Pad(view, top: false, bottom: true);

        view.FindViewById<TextView>(Resource.Id.sheet_file_title)!.Text = entry.Name;
        var subtitle = view.FindViewById<TextView>(Resource.Id.sheet_file_subtitle)!;
        subtitle.Text = entry.Kind switch
        {
            WorkspaceEntryKind.Directory => ctx.GetString(Resource.String.files_sheet_dir_subtitle),
            WorkspaceEntryKind.Symlink => string.IsNullOrWhiteSpace(entry.LinkTarget)
                ? ctx.GetString(Resource.String.files_sheet_symlink_subtitle)
                : $"→ {entry.LinkTarget}",
            _ => $"{FilesFragment.FormatSize(entry.Size)} · {entry.Modified.ToLocalTime():yyyy-MM-dd HH:mm}",
        };

        var dialog = new BottomSheetDialog(ctx);
        dialog.SetContentView(view);

        var canRead = caps.HasFlag(FileCapabilities.Read);
        var canWrite = caps.HasFlag(FileCapabilities.Write);
        BindRow(
            view.FindViewById(Resource.Id.sheet_file_open)!,
            canWrite ? Resource.Drawable.ic_code : Resource.Drawable.ic_article,
            ctx.GetString(canWrite ? Resource.String.files_edit : Resource.String.files_open_text),
            ctx.GetString(canWrite ? Resource.String.files_sheet_open_edit_sub : Resource.String.files_sheet_open_read_sub),
            visible: canRead,
            dangerTint: false,
            () =>
            {
                dialog.Dismiss();
                onOpenOrEdit();
            });

        BindRow(
            view.FindViewById(Resource.Id.sheet_file_export)!,
            Resource.Drawable.ic_download,
            ctx.GetString(Resource.String.files_export),
            ctx.GetString(Resource.String.files_sheet_export_sub),
            visible: canRead,
            dangerTint: false,
            () =>
            {
                dialog.Dismiss();
                onExport();
            });

        var canExtract = caps.HasFlag(FileCapabilities.Archive)
            && entry.Kind == WorkspaceEntryKind.File
            && WorkspaceProvider.FormatOf(entry.Name) is not null;
        BindRow(
            view.FindViewById(Resource.Id.sheet_file_extract)!,
            Resource.Drawable.ic_storage,
            ctx.GetString(Resource.String.files_extract),
            ctx.GetString(Resource.String.files_sheet_extract_sub),
            visible: canExtract,
            dangerTint: false,
            () =>
            {
                dialog.Dismiss();
                onExtract();
            });

        BindRow(
            view.FindViewById(Resource.Id.sheet_file_rename)!,
            Resource.Drawable.ic_article,
            ctx.GetString(Resource.String.files_rename),
            ctx.GetString(Resource.String.files_sheet_rename_sub),
            visible: caps.HasFlag(FileCapabilities.Rename),
            dangerTint: false,
            () =>
            {
                dialog.Dismiss();
                onRename();
            });

        BindRow(
            view.FindViewById(Resource.Id.sheet_file_copy)!,
            Resource.Drawable.ic_copy,
            ctx.GetString(Resource.String.files_copy),
            ctx.GetString(Resource.String.files_sheet_copy_sub),
            visible: caps.HasFlag(FileCapabilities.Copy),
            dangerTint: false,
            () =>
            {
                dialog.Dismiss();
                onCopy();
            });

        BindRow(
            view.FindViewById(Resource.Id.sheet_file_cut)!,
            Resource.Drawable.ic_upload,
            ctx.GetString(Resource.String.files_cut),
            ctx.GetString(Resource.String.files_sheet_cut_sub),
            visible: caps.HasFlag(FileCapabilities.Move),
            dangerTint: false,
            () =>
            {
                dialog.Dismiss();
                onCut();
            });

        var canZip = caps.HasFlag(FileCapabilities.Archive)
            && entry.Kind is WorkspaceEntryKind.File or WorkspaceEntryKind.Directory;
        BindRow(
            view.FindViewById(Resource.Id.sheet_file_zip)!,
            Resource.Drawable.ic_storage,
            ctx.GetString(Resource.String.files_compress),
            ctx.GetString(Resource.String.files_sheet_compress_sub),
            visible: canZip,
            dangerTint: false,
            () =>
            {
                dialog.Dismiss();
                onZip();
            });

        BindRow(
            view.FindViewById(Resource.Id.sheet_file_delete)!,
            Resource.Drawable.ic_delete,
            ctx.GetString(Resource.String.files_delete),
            ctx.GetString(Resource.String.files_sheet_delete_sub),
            visible: caps.HasFlag(FileCapabilities.Delete),
            dangerTint: true,
            () =>
            {
                dialog.Dismiss();
                onDelete();
            });

        dialog.Show();
    }

    private static void BindRow(
        View row,
        int iconRes,
        string titleText,
        string subtitleText,
        bool visible,
        bool dangerTint,
        Action onClick)
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
        if (dangerTint)
        {
            var errColor = new global::Android.Graphics.Color(Rows.Color(row, Resource.Attribute.colorError));
            icon.ImageTintList = ColorStateList.ValueOf(errColor);
            title.SetTextColor(errColor);
        }
        row.Click += (_, _) => onClick();
    }
}
