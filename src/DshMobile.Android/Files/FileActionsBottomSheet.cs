using Android.Content;
using Android.Content.Res;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.Fragment.App;
using DshMobile.App.Ui;
using DshMobile.Core.Files;
using Google.Android.Material.BottomSheet;
using FragmentManager = AndroidX.Fragment.App.FragmentManager;

namespace DshMobile.App.Files;

/// <summary>
/// UI Slice 3 / UI-S6：工作区文件/目录底部操作抽屉（全 XML <c>sheet_file_actions.xml</c>），
/// 严格按 <see cref="IFileProvider.CapabilitiesOf"/> 裁剪能力，并以错误语义色高亮「删除」操作。
/// 已统一为 <see cref="BottomSheetDialogFragment"/>：展示所需标量经 <c>Arguments</c> 传递，
/// 动作经宿主 <see cref="IHost"/> 回调派发；配置变更后若宿主不可用则安全关闭。
/// </summary>
public sealed class FileActionsBottomSheet : BottomSheetDialogFragment
{
    public const string SheetTag = "dsh.file_actions.bottom_sheet";

    private const string ArgName = "sheet.file.name";
    private const string ArgRelativePath = "sheet.file.relative_path";
    private const string ArgKind = "sheet.file.kind";
    private const string ArgSize = "sheet.file.size";
    private const string ArgModifiedMs = "sheet.file.modified_ms";
    private const string ArgProtected = "sheet.file.protected";
    private const string ArgLinkTarget = "sheet.file.link_target";
    private const string ArgCaps = "sheet.file.caps";

    /// <summary>文件操作抽屉可派发的轻量动作。</summary>
    public enum Action
    {
        OpenOrEdit,
        Export,
        Extract,
        Rename,
        Copy,
        Cut,
        Zip,
        Delete,
    }

    /// <summary>宿主（文件页）回调契约；重建后宿主不再适用时由弹窗自行关闭。</summary>
    public interface IHost
    {
        void OnFileAction(Action action, WorkspaceEntry entry);
    }

    private IHost? _host;
    private WorkspaceEntry? _entry;

    /// <summary>以 tag 去重打开抽屉；已存在实例时直接复用，避免双击弹出双份。</summary>
    public static void Show(FragmentManager manager, WorkspaceEntry entry, FileCapabilities caps)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(entry);
        if (manager.FindFragmentByTag(SheetTag) is not null)
        {
            return;
        }

        var args = new Bundle();
        args.PutString(ArgName, entry.Name);
        args.PutString(ArgRelativePath, entry.RelativePath);
        args.PutInt(ArgKind, (int)entry.Kind);
        args.PutLong(ArgSize, entry.Size);
        args.PutLong(ArgModifiedMs, entry.Modified.ToUnixTimeMilliseconds());
        args.PutBoolean(ArgProtected, entry.Protected);
        if (!string.IsNullOrEmpty(entry.LinkTarget))
        {
            args.PutString(ArgLinkTarget, entry.LinkTarget);
        }
        args.PutInt(ArgCaps, (int)caps);

        new FileActionsBottomSheet { Arguments = args }.Show(manager, SheetTag);
    }

    public override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetStyle(AndroidX.Fragment.App.DialogFragment.StyleNoTitle, Resource.Style.ThemeOverlay_DshMobile_BottomSheetDialog);
    }

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        var view = inflater.Inflate(Resource.Layout.sheet_file_actions, container, false)!;
        Edges.Pad(view, top: false, bottom: true);

        _host = ParentFragment as IHost ?? Activity as IHost;
        _entry = ReadEntry();
        if (_entry is null)
        {
            return view;
        }

        var ctx = RequireContext();
        var caps = (FileCapabilities)(Arguments?.GetInt(ArgCaps, 0) ?? 0);
        var entry = _entry!;

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

        var canRead = caps.HasFlag(FileCapabilities.Read);
        var canWrite = caps.HasFlag(FileCapabilities.Write);
        BindRow(
            view.FindViewById(Resource.Id.sheet_file_open)!,
            canWrite ? Resource.Drawable.ic_code : Resource.Drawable.ic_article,
            ctx.GetString(canWrite ? Resource.String.files_edit : Resource.String.files_open_text),
            ctx.GetString(canWrite ? Resource.String.files_sheet_open_edit_sub : Resource.String.files_sheet_open_read_sub),
            visible: canRead,
            dangerTint: false,
            Action.OpenOrEdit);

        BindRow(
            view.FindViewById(Resource.Id.sheet_file_export)!,
            Resource.Drawable.ic_download,
            ctx.GetString(Resource.String.files_export),
            ctx.GetString(Resource.String.files_sheet_export_sub),
            visible: canRead,
            dangerTint: false,
            Action.Export);

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
            Action.Extract);

        BindRow(
            view.FindViewById(Resource.Id.sheet_file_rename)!,
            Resource.Drawable.ic_article,
            ctx.GetString(Resource.String.files_rename),
            ctx.GetString(Resource.String.files_sheet_rename_sub),
            visible: caps.HasFlag(FileCapabilities.Rename),
            dangerTint: false,
            Action.Rename);

        BindRow(
            view.FindViewById(Resource.Id.sheet_file_copy)!,
            Resource.Drawable.ic_copy,
            ctx.GetString(Resource.String.files_copy),
            ctx.GetString(Resource.String.files_sheet_copy_sub),
            visible: caps.HasFlag(FileCapabilities.Copy),
            dangerTint: false,
            Action.Copy);

        BindRow(
            view.FindViewById(Resource.Id.sheet_file_cut)!,
            Resource.Drawable.ic_upload,
            ctx.GetString(Resource.String.files_cut),
            ctx.GetString(Resource.String.files_sheet_cut_sub),
            visible: caps.HasFlag(FileCapabilities.Move),
            dangerTint: false,
            Action.Cut);

        var canZip = caps.HasFlag(FileCapabilities.Archive)
            && entry.Kind is WorkspaceEntryKind.File or WorkspaceEntryKind.Directory;
        BindRow(
            view.FindViewById(Resource.Id.sheet_file_zip)!,
            Resource.Drawable.ic_storage,
            ctx.GetString(Resource.String.files_compress),
            ctx.GetString(Resource.String.files_sheet_compress_sub),
            visible: canZip,
            dangerTint: false,
            Action.Zip);

        BindRow(
            view.FindViewById(Resource.Id.sheet_file_delete)!,
            Resource.Drawable.ic_delete,
            ctx.GetString(Resource.String.files_delete),
            ctx.GetString(Resource.String.files_sheet_delete_sub),
            visible: caps.HasFlag(FileCapabilities.Delete),
            dangerTint: true,
            Action.Delete);

        return view;
    }

    public override void OnStart()
    {
        base.OnStart();
        if (_host is null || _entry is null)
        {
            // 重建后宿主回调 / 条目标量失效：安全关闭，绝不把空引用抛给用户。
            DismissAllowingStateLoss();
            return;
        }

        if (Dialog is BottomSheetDialog sheet)
        {
            sheet.Behavior.SkipCollapsed = true;
            sheet.Behavior.State = BottomSheetBehavior.StateExpanded;
        }
    }

    private WorkspaceEntry? ReadEntry()
    {
        var args = Arguments;
        var relativePath = args?.GetString(ArgRelativePath);
        if (string.IsNullOrEmpty(relativePath))
        {
            return null;
        }
        return new WorkspaceEntry(
            args?.GetString(ArgName) ?? relativePath,
            relativePath,
            (WorkspaceEntryKind)(args?.GetInt(ArgKind, 0) ?? 0),
            args?.GetLong(ArgSize, 0) ?? 0,
            DateTimeOffset.FromUnixTimeMilliseconds(args?.GetLong(ArgModifiedMs, 0) ?? 0),
            args?.GetBoolean(ArgProtected, false) ?? false,
            args?.GetString(ArgLinkTarget));
    }

    private void BindRow(
        View row,
        int iconRes,
        string titleText,
        string subtitleText,
        bool visible,
        bool dangerTint,
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
        if (dangerTint)
        {
            var errColor = new global::Android.Graphics.Color(Rows.Color(row, Resource.Attribute.colorError));
            icon.ImageTintList = ColorStateList.ValueOf(errColor);
            title.SetTextColor(errColor);
        }
        row.Click += (_, _) => Dispatch(action);
    }

    private void Dispatch(Action action)
    {
        var host = _host;
        var entry = _entry;
        DismissAllowingStateLoss();
        if (host is not null && entry is not null)
        {
            host.OnFileAction(action, entry);
        }
    }
}
