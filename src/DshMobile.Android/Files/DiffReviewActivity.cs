using Android.Content;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using DshMobile.App.Ui;
using DshMobile.Core.Files;
using Google.Android.Material.Card;
using Google.Android.Material.ProgressIndicator;

namespace DshMobile.App.Files;

/// <summary>
/// UI-S5：独立全屏 Diff 审查页（全 XML <c>activity_diff_review.xml</c>，遵循 <see cref="DshActivity"/> 与
/// <see cref="Edges"/> edge-to-edge 规范）。单一极简顶栏（返回箭头 + 文件名 + 真实 <c>+N −M</c> 副标题 +
/// 自动换行 / 打开文件两个动作），不再复用工作区页面内的内嵌 Diff 视图，因此不存在第二个返回箭头。
/// 所有 Diff 清洗、Hunk 头规范化与增删统计均委托 <see cref="UnifiedDiffReview"/>（纯 C#，可单测）。
/// </summary>
[Activity(
    Label = "@string/git_diff_review_title",
    Exported = false,
    WindowSoftInputMode = SoftInput.AdjustResize)]
public sealed class DiffReviewActivity : DshActivity
{
    public const string ExtraRepoRoot = "diff.review.repo_root";
    public const string ExtraFile = "diff.review.file";
    public const string ExtraWorkspacePath = "diff.review.workspace_path";
    public const string ExtraBranch = "diff.review.branch";

    private const string StateWrap = "diff.review.wrap";
    private const string GuestRoot = "/root/projects";
    // 不超过该行数的短 Diff 让圆角纸面卡自然包裹内容，长 Diff 仍占满视口并内部滚动。
    private const int ShortDiffLineLimit = 16;

    private WorkspaceProvider? _provider;
    private DiffReviewAdapter _adapter = null!;
    private HorizontalScrollView _hScroll = null!;
    private RecyclerView _list = null!;
    private MaterialCardView _card = null!;
    private View _container = null!;
    private LinearProgressIndicator _progress = null!;
    private TextView _notice = null!;
    private TextView _empty = null!;
    private IMenuItem? _wrapItem;
    private IMenuItem? _openItem;
    private string _workspaceRelative = "";
    private string _branchName = "";
    private bool _wrap;
    private bool _opening;
    private bool _openedEditor;

    /// <summary>从工作区 Git 变更列表进入：仓库相对根 + 相对仓库的文件路径（可选分支名用于副标题）。</summary>
    public static Intent CreateIntent(Context context, string repoRelativeRoot, string fileRelativeToRepo, string? branchName = null) =>
        new Intent(context, typeof(DiffReviewActivity))
            .PutExtra(ExtraRepoRoot, repoRelativeRoot)
            .PutExtra(ExtraFile, fileRelativeToRepo)
            .PutExtra(ExtraBranch, branchName ?? string.Empty);

    /// <summary>从聊天工具卡等入口进入：仅知工作区相对路径，由本页在后台解析所属仓库与分支。</summary>
    public static Intent CreateWorkspaceIntent(Context context, string workspaceRelativePath) =>
        new Intent(context, typeof(DiffReviewActivity))
            .PutExtra(ExtraWorkspacePath, workspaceRelativePath);

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetScreen(Resource.Layout.activity_diff_review, Resource.String.git_diff_review_title);
        _wrap = savedInstanceState?.GetBoolean(StateWrap, false) ?? false;

        _progress = FindViewById<LinearProgressIndicator>(Resource.Id.diff_progress)!;
        _notice = FindViewById<TextView>(Resource.Id.diff_notice)!;
        _empty = FindViewById<TextView>(Resource.Id.diff_empty)!;
        _hScroll = FindViewById<HorizontalScrollView>(Resource.Id.diff_hscroll)!;
        _list = FindViewById<RecyclerView>(Resource.Id.diff_list)!;
        _card = FindViewById<MaterialCardView>(Resource.Id.diff_card)!;
        _container = FindViewById(Resource.Id.diff_container)!;

        // 折行模式下把列表宽度锁到横向滚动视口的实测宽度：HorizontalScrollView 默认不限制子视图宽度，
        // 不锁宽会让 RecyclerView 按内容无限展开，自动换行因此失效。
        // 非换行模式则把视口宽度交给适配器，并给列表本身设最小宽度；短行用 minWidth 铺满视口，长行继续横向滚动。
        _hScroll.LayoutChange += (_, _) =>
        {
            if (_hScroll.Width <= 0)
            {
                return;
            }
            if (_wrap)
            {
                if (_list.LayoutParameters is { } listParams && listParams.Width != _hScroll.Width)
                {
                    listParams.Width = _hScroll.Width;
                    _list.LayoutParameters = listParams;
                    _list.RequestLayout();
                }
                // 折行模式下由视口宽度约束列表，不能再保留非换行时的最小宽度。
                _list.SetMinimumWidth(0);
            }
            else
            {
                // 非换行模式：列表本身也至少铺满横向视口，短内容无需依赖 fillViewport 兜底；
                // 长行仍按内容宽度撑开 RecyclerView 并继续横向滚动。
                _list.SetMinimumWidth(_hScroll.Width);
                _adapter.SetViewportWidth(_hScroll.Width);
            }
        };
        _list.SetLayoutManager(new LinearLayoutManager(this));
        _list.SetItemAnimator(null);
        _adapter = new DiffReviewAdapter { Wrap = _wrap };
        _list.SetAdapter(_adapter);

        _wrapItem = Toolbar.Menu?.FindItem(Resource.Id.diff_menu_wrap);
        _openItem = Toolbar.Menu?.FindItem(Resource.Id.diff_menu_open);
        _wrapItem?.SetChecked(_wrap);
        Toolbar.MenuItemClick += (_, e) => OnMenu(e.Item?.ItemId ?? 0);

        ApplyWrap();
        // 加载期间维持限高滚动形态；拿到真实 Diff 后再按行数决定是否收缩为 wrap_content。
        ApplyCardHeight(compact: false);
        _ = LoadAsync();
    }

    protected override void OnSaveInstanceState(Bundle outState)
    {
        base.OnSaveInstanceState(outState);
        outState.PutBoolean(StateWrap, _wrap);
    }

    protected override void OnResume()
    {
        base.OnResume();
        // 从编辑器返回：可能刚保存了修改，重新拉取一次真实 Diff（不伪造结果）。
        if (_openedEditor)
        {
            _openedEditor = false;
            _ = LoadAsync();
        }
    }

    private void OnMenu(int itemId)
    {
        if (itemId == Resource.Id.diff_menu_wrap)
        {
            _wrap = !_wrap;
            _wrapItem?.SetChecked(_wrap);
            ApplyWrap();
        }
        else if (itemId == Resource.Id.diff_menu_open)
        {
            OpenTargetFile();
        }
    }

    private void ApplyWrap()
    {
        _adapter.Wrap = _wrap;
        // 两种模式都启用 fillViewport：短内容时 RecyclerView 至少铺满视口，消除右侧白边。
        _hScroll.FillViewport = true;
        if (_list.LayoutParameters is { } lp)
        {
            lp.Width = _wrap
                ? (_hScroll.Width > 0 ? _hScroll.Width : ViewGroup.LayoutParams.MatchParent)
                : ViewGroup.LayoutParams.WrapContent;
            _list.LayoutParameters = lp;
        }
        // 与 LayoutChange 保持一致：非换行时列表最少占满视口宽度，折行时清除该下限。
        _list.SetMinimumWidth(_wrap ? 0 : Math.Max(0, _hScroll.Width));
        _adapter.SetViewportWidth(_hScroll.Width > 0 ? _hScroll.Width : 0);
        _hScroll.HorizontalScrollBarEnabled = !_wrap;
        _list.RequestLayout();
        _adapter.NotifyDataSetChanged();
    }

    /// <summary>
    /// 短 Diff 让外层圆角纸面卡自然包裹内容，避免拉出 90% 死白空洞；
    /// 长 Diff 则恢复 0dp + weight 的限高滚动，保证数千行仍可正常纵向浏览。
    /// </summary>
    private void ApplyCardHeight(bool compact)
    {
        if (_card.LayoutParameters is LinearLayout.LayoutParams cardParams)
        {
            var targetHeight = compact ? ViewGroup.LayoutParams.WrapContent : 0;
            var targetWeight = compact ? 0f : 1f;
            if (cardParams.Height != targetHeight || Math.Abs(cardParams.Weight - targetWeight) > 0.001f)
            {
                cardParams.Height = targetHeight;
                cardParams.Weight = targetWeight;
                _card.LayoutParameters = cardParams;
            }
        }

        SetChildHeight(_container, compact);
        SetChildHeight(_hScroll, compact);
        SetChildHeight(_list, compact);
        _card.RequestLayout();
    }

    private static void SetChildHeight(View view, bool compact)
    {
        if (view.LayoutParameters is not { } lp)
        {
            return;
        }
        var target = compact ? ViewGroup.LayoutParams.WrapContent : ViewGroup.LayoutParams.MatchParent;
        if (lp.Height != target)
        {
            lp.Height = target;
            view.LayoutParameters = lp;
        }
    }

    private async Task LoadAsync()
    {
        var git = DshMobileApplication.Current.Bridge.CreateWorkspaceGitService();
        if (git is null)
        {
            ShowMessage(GetString(Resource.String.files_no_workspace));
            return;
        }

        var repoRoot = Intent?.GetStringExtra(ExtraRepoRoot);
        var file = Intent?.GetStringExtra(ExtraFile);
        var workspacePath = Intent?.GetStringExtra(ExtraWorkspacePath);
        var branch = Intent?.GetStringExtra(ExtraBranch);

        _progress.Visibility = ViewStates.Visible;
        try
        {
            if (!string.IsNullOrWhiteSpace(workspacePath))
            {
                // 聊天入口：只拿到工作区相对路径，先安全规范化 + .dsh/ 保护，再解析所属仓库
                var wsNorm = WorkspaceFileSystem.NormalizeRelative(workspacePath);
                EnsureSafeWorkspacePath(wsNorm);
                _workspaceRelative = wsNorm;
                var parentDir = ParentDirectory(wsNorm);
                var status = await Task.Run(() => git.GetStatusAsync(parentDir)).ConfigureAwait(true);
                if (!status.IsRepository)
                {
                    _progress.Visibility = ViewStates.Gone;
                    ShowMessage(status.ErrorCode == GitErrors.GitUnavailable
                        ? GetString(Resource.String.git_unavailable)
                        : GetString(Resource.String.git_not_repo));
                    return;
                }
                _branchName = status.BranchName ?? "";
                repoRoot = status.RepoRelativeRoot;
                file = RelativeToRepo(wsNorm, status.RepoRelativeRoot);
            }
            else
            {
                var fileNorm = WorkspaceFileSystem.NormalizeRelative(file);
                var rootNorm = WorkspaceFileSystem.NormalizeRelative(repoRoot);
                if (WorkspaceFileSystem.IsProtected(rootNorm))
                {
                    throw new WorkspaceException(WorkspaceErrors.Protected, $"'{rootNorm}' is protected");
                }
                EnsureSafeWorkspacePath(fileNorm);
                _workspaceRelative = rootNorm.Length == 0 ? fileNorm : $"{rootNorm}/{fileNorm}";
                if (WorkspaceFileSystem.IsProtected(_workspaceRelative))
                {
                    throw new WorkspaceException(WorkspaceErrors.Protected, $"'{_workspaceRelative}' is protected");
                }
                repoRoot = rootNorm;
                file = fileNorm;
                _branchName = branch ?? "";
            }

            var doc = await Task.Run(() => git.GetFileDiffAsync(repoRoot, file!)).ConfigureAwait(true);
            _progress.Visibility = ViewStates.Gone;
            Render(doc);
        }
        catch (WorkspaceException ex)
        {
            _progress.Visibility = ViewStates.Gone;
            ShowMessage($"{ex.Code}: {ex.Message}");
            Log.Info("DshMobile", $"diff review {ex.Code}: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or Java.Lang.Exception)
        {
            _progress.Visibility = ViewStates.Gone;
            ShowMessage($"{ex.GetType().Name}: {ex.Message}");
            Log.Warn("DshMobile", $"diff review failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>渲染后台已解析 + 核心侧已清洗的 Diff；无 diff / 二进制 / 截断均如实提示，不伪造内容。</summary>
    private void Render(GitDiffDocument doc)
    {
        var review = UnifiedDiffReview.Build(doc.Lines);
        var fileName = FileNameOf(_workspaceRelative);
        Toolbar.Title = fileName.Length > 0 ? fileName : GetString(Resource.String.git_diff_review_title);
        var where = string.IsNullOrEmpty(_branchName)
            ? _workspaceRelative
            : Rows.Fmt(this, Resource.String.git_diff_review_branch_fmt, _workspaceRelative, _branchName);
        Toolbar.Subtitle = Rows.Fmt(
            this,
            Resource.String.git_diff_review_subtitle_fmt,
            review.Stats.AddedLines,
            review.Stats.RemovedLines,
            where);

        if (doc.IsBinary)
        {
            ShowMessage(GetString(Resource.String.git_diff_binary));
            return;
        }
        if (review.IsEmpty)
        {
            ShowMessage(GetString(Resource.String.git_diff_empty));
            return;
        }

        _empty.Visibility = ViewStates.Gone;
        _hScroll.Visibility = ViewStates.Visible;
        _adapter.Submit(review.Lines);
        ApplyCardHeight(review.Lines.Count <= ShortDiffLineLimit);

        if (doc.IsTruncated && !string.IsNullOrWhiteSpace(doc.TruncationReason))
        {
            _notice.Text = Rows.Fmt(this, Resource.String.git_diff_truncated_fmt, doc.TruncationReason!);
            _notice.Visibility = ViewStates.Visible;
        }
        else
        {
            _notice.Visibility = ViewStates.Gone;
        }
        _openItem?.SetEnabled(true);
    }

    private void ShowMessage(string message)
    {
        _adapter.Submit([]);
        _notice.Visibility = ViewStates.Gone;
        _hScroll.Visibility = ViewStates.Gone;
        _empty.Text = message;
        _empty.Visibility = ViewStates.Visible;
        // 无 Diff / 二进制 / 空内容时也让纸面卡包裹提示文本，不保留整屏空白。
        ApplyCardHeight(compact: true);
    }

    /// <summary>「打开/编辑文件」：严格经工作区安全路径校验（含 .dsh/ 保护、符号链接拒绝）后跳转编辑器。</summary>
    private void OpenTargetFile()
    {
        if (_opening || string.IsNullOrEmpty(_workspaceRelative))
        {
            return;
        }
        _opening = true;
        _ = OpenTargetFileAsync();
    }

    private async Task OpenTargetFileAsync()
    {
        try
        {
            string norm;
            try
            {
                norm = WorkspaceFileSystem.NormalizeRelative(_workspaceRelative);
            }
            catch (WorkspaceException ex)
            {
                ShowNotice($"{ex.Code}: {ex.Message}");
                return;
            }
            if (norm.Length == 0 || WorkspaceFileSystem.IsProtected(norm))
            {
                ShowNotice(GetString(Resource.String.files_protected));
                return;
            }
            if (!EnsureProvider())
            {
                ShowNotice(GetString(Resource.String.files_no_workspace));
                return;
            }

            // StatAsync 复用 WorkspaceFileSystem 的路径逃逸 / 符号链接 / .dsh 保护门禁；
            // WorkspaceEditorActivity 打开时还会再次 Stat + OpenRead，绝不绕过安全校验。
            var entry = await _provider!.StatAsync(norm, CancellationToken.None).ConfigureAwait(true);
            if (entry.Kind != WorkspaceEntryKind.File || !_provider.CapabilitiesOf(entry).HasFlag(FileCapabilities.Read))
            {
                ShowNotice(GetString(Resource.String.git_diff_open_not_file));
                return;
            }
            _openedEditor = true;
            StartActivity(WorkspaceEditorActivity.CreateIntent(this, entry.RelativePath, startEditing: false));
        }
        catch (WorkspaceException ex)
        {
            ShowNotice($"{ex.Code}: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or Java.Lang.Exception)
        {
            ShowNotice($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _opening = false;
        }
    }

    private bool EnsureProvider()
    {
        if (_provider is not null)
        {
            return true;
        }
        var files = FilesDir!.AbsolutePath;
        var workspace = Path.Combine(files, "user-data");
        if (!Directory.Exists(workspace))
        {
            return false;
        }
        _provider = new WorkspaceProvider(
            new WorkspaceFileSystem(workspace),
            Path.Combine(files, "dshmobile-files-trash"),
            GuestRoot);
        return true;
    }

    private void ShowNotice(string message)
    {
        _notice.Text = message;
        _notice.Visibility = ViewStates.Visible;
        Log.Info("DshMobile", "diff review open: " + message);
    }

    /// <summary>统一的工作区安全前置校验：空路径、越界与 <c>.dsh/</c> 保护一律拒绝。</summary>
    private static void EnsureSafeWorkspacePath(string normalized)
    {
        if (normalized.Length == 0)
        {
            throw new WorkspaceException(WorkspaceErrors.InvalidName, "file path is required");
        }
        if (WorkspaceFileSystem.IsProtected(normalized))
        {
            throw new WorkspaceException(WorkspaceErrors.Protected, $"'{normalized}' is protected");
        }
    }

    private static string ParentDirectory(string normalized)
    {
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? "" : normalized[..slash];
    }

    private static string RelativeToRepo(string workspaceRelative, string repoRoot)
    {
        if (repoRoot.Length == 0)
        {
            return workspaceRelative;
        }
        return workspaceRelative.StartsWith(repoRoot + "/", StringComparison.Ordinal)
            ? workspaceRelative[(repoRoot.Length + 1)..]
            : workspaceRelative;
    }

    private static string FileNameOf(string relative)
    {
        var slash = relative.LastIndexOf('/');
        return slash < 0 ? relative : relative[(slash + 1)..];
    }
}
