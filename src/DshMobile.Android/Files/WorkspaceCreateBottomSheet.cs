using Android.Content;
using Android.Views;
using Android.Widget;
using DshMobile.App.Ui;
using Google.Android.Material.BottomSheet;

namespace DshMobile.App.Files;

/// <summary>
/// UI Slice 3：工作区单手新建/导入底部抽屉（全 XML <c>sheet_workspace_create.xml</c>）。
/// </summary>
internal static class WorkspaceCreateBottomSheet
{
    public static void Show(
        Context ctx,
        string displayDir,
        Action onNewFile,
        Action onNewFolder,
        Action onImport)
    {
        var view = LayoutInflater.From(ctx)!.Inflate(Resource.Layout.sheet_workspace_create, null)!;
        Edges.Pad(view, top: false, bottom: true);

        view.FindViewById<TextView>(Resource.Id.sheet_create_dir)!.Text = displayDir;

        var dialog = new BottomSheetDialog(ctx);
        dialog.SetContentView(view);

        BindRow(
            view.FindViewById(Resource.Id.sheet_create_file)!,
            Resource.Drawable.ic_file,
            ctx.GetString(Resource.String.files_new_file),
            ctx.GetString(Resource.String.files_create_file_sub),
            () =>
            {
                dialog.Dismiss();
                onNewFile();
            });

        BindRow(
            view.FindViewById(Resource.Id.sheet_create_folder)!,
            Resource.Drawable.ic_folder,
            ctx.GetString(Resource.String.files_new_folder),
            ctx.GetString(Resource.String.files_create_folder_sub),
            () =>
            {
                dialog.Dismiss();
                onNewFolder();
            });

        BindRow(
            view.FindViewById(Resource.Id.sheet_create_import)!,
            Resource.Drawable.ic_upload,
            ctx.GetString(Resource.String.files_import),
            ctx.GetString(Resource.String.files_import_sub),
            () =>
            {
                dialog.Dismiss();
                onImport();
            });

        dialog.Show();
    }

    private static void BindRow(View row, int iconRes, string titleText, string subtitleText, Action onClick)
    {
        row.FindViewById<ImageView>(Resource.Id.row_icon)!.SetImageResource(iconRes);
        row.FindViewById<TextView>(Resource.Id.row_title)!.Text = titleText;
        var sub = row.FindViewById<TextView>(Resource.Id.row_subtitle)!;
        sub.Text = subtitleText;
        sub.Visibility = ViewStates.Visible;
        row.Click += (_, _) => onClick();
    }
}
