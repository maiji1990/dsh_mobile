using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.Fragment.App;
using DshMobile.App.Ui;
using Google.Android.Material.BottomSheet;
using FragmentManager = AndroidX.Fragment.App.FragmentManager;

namespace DshMobile.App.Files;

/// <summary>
/// UI Slice 3 / UI-S6：工作区单手新建/导入底部抽屉（全 XML <c>sheet_workspace_create.xml</c>）。
/// 已统一为 <see cref="BottomSheetDialogFragment"/>：目录等标量经 <c>Arguments</c> 传递，
/// 动作经宿主 <see cref="IHost"/> 回调派发；配置变更后若宿主不可用则安全关闭。
/// </summary>
public sealed class WorkspaceCreateBottomSheet : BottomSheetDialogFragment
{
    public const string SheetTag = "dsh.workspace_create.bottom_sheet";

    private const string ArgDisplayDir = "sheet.create.display_dir";

    /// <summary>新建抽屉可派发的轻量动作。</summary>
    public enum Action
    {
        NewFile,
        NewFolder,
        Import,
    }

    /// <summary>宿主（文件页）回调契约；重建后宿主不再适用时由弹窗自行关闭。</summary>
    public interface IHost
    {
        void OnWorkspaceCreateAction(Action action);
    }

    private IHost? _host;

    /// <summary>以 tag 去重打开抽屉；已存在实例时直接复用，避免双击弹出双份。</summary>
    public static void ShowSheet(FragmentManager manager, string displayDir)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (manager.FindFragmentByTag(SheetTag) is not null)
        {
            return;
        }

        var args = new Bundle();
        args.PutString(ArgDisplayDir, displayDir);
        new WorkspaceCreateBottomSheet { Arguments = args }.Show(manager, SheetTag);
    }

    public override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetStyle(AndroidX.Fragment.App.DialogFragment.StyleNoTitle, Resource.Style.ThemeOverlay_DshMobile_BottomSheetDialog);
    }

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        var view = inflater.Inflate(Resource.Layout.sheet_workspace_create, container, false)!;
        Edges.Pad(view, top: false, bottom: true);

        _host = ParentFragment as IHost ?? Activity as IHost;
        var ctx = RequireContext();
        view.FindViewById<TextView>(Resource.Id.sheet_create_dir)!.Text =
            Arguments?.GetString(ArgDisplayDir) ?? string.Empty;

        BindRow(
            view.FindViewById(Resource.Id.sheet_create_file)!,
            Resource.Drawable.ic_file,
            ctx.GetString(Resource.String.files_new_file),
            ctx.GetString(Resource.String.files_create_file_sub),
            Action.NewFile);

        BindRow(
            view.FindViewById(Resource.Id.sheet_create_folder)!,
            Resource.Drawable.ic_folder,
            ctx.GetString(Resource.String.files_new_folder),
            ctx.GetString(Resource.String.files_create_folder_sub),
            Action.NewFolder);

        BindRow(
            view.FindViewById(Resource.Id.sheet_create_import)!,
            Resource.Drawable.ic_upload,
            ctx.GetString(Resource.String.files_import),
            ctx.GetString(Resource.String.files_import_sub),
            Action.Import);

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

    private void BindRow(View row, int iconRes, string titleText, string subtitleText, Action action)
    {
        row.FindViewById<ImageView>(Resource.Id.row_icon)!.SetImageResource(iconRes);
        row.FindViewById<TextView>(Resource.Id.row_title)!.Text = titleText;
        var sub = row.FindViewById<TextView>(Resource.Id.row_subtitle)!;
        sub.Text = subtitleText;
        sub.Visibility = ViewStates.Visible;
        row.Click += (_, _) => Dispatch(action);
    }

    private void Dispatch(Action action)
    {
        var host = _host;
        DismissAllowingStateLoss();
        host?.OnWorkspaceCreateAction(action);
    }
}
