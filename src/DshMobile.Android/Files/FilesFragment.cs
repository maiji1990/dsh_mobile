using Android.Content;
using Android.Content.Res;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using DshMobile.App.Ui;
using DshMobile.Core.Files;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Chip;
using Google.Android.Material.Dialog;
using Google.Android.Material.FloatingActionButton;
using Google.Android.Material.ProgressIndicator;
using Google.Android.Material.Snackbar;
using Google.Android.Material.TextField;
using Fragment = AndroidX.Fragment.App.Fragment;
using Log = Android.Util.Log;

namespace DshMobile.App.Files;

/// <summary>
/// 工作区工作台页签（TASK-007 / N-06-02 / UI Slice 3）：
/// 全 XML 化布局（<c>fragment_files.xml</c> / <c>item_workspace_entry.xml</c> / <c>item_file_crumb.xml</c>），
/// 单击文本/代码文件直达全屏 <see cref="WorkspaceEditorActivity"/>，行尾 <c>⋮</c> 拉起底部操作抽屉，
/// 单手 FAB 承接新建/导入，Git 非仓库时自动扫描子仓库提供一键跳转；
/// 所有路径校验、符号链接拦截、<c>.dsh</c> 保护与冲突检测严格委托 <see cref="WorkspaceProvider"/>。
/// </summary>
public sealed class FilesFragment : Fragment, ITabRoot, FileActionsBottomSheet.IHost, WorkspaceCreateBottomSheet.IHost
{
    private const int RequestImport = 71;
    private const int RequestExport = 72;
    private const int RequestEditor = 73;
    private const string GuestRoot = "/root/projects";
    private const int DiffLimit = 5000;
    private static readonly TimeSpan TrashAge = TimeSpan.FromDays(7);

    private WorkspaceProvider? _provider;
    private string _dir = "";
    private string? _searchQuery;
    private SearchResult? _searchResult;
    private string? _pendingExport;
    private FileSort _sort = FileSort.Name;
    private bool _descending;
    private string _filter = "";
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private List<string> _clip = [];
    private bool _clipCut;
    private bool _busy;
    private int _loadGeneration;
    private bool _gitMode;
    private int _gitGeneration;
    private string _gitRepoRoot = "";
    private string _gitBranch = "";
    private string? _pendingTargetPath;
    private bool _pendingTargetDiff;
    private bool _menuIsSelection;
    private bool _searchExpanded;
    private string? _lastCrumbsKey;
    private bool _isWide;

    private MaterialToolbar _toolbar = null!;
    private LinearProgressIndicator _filesProgress = null!;
    private MaterialButton _filesModeButton = null!;
    private MaterialButton _gitModeButton = null!;
    private View _browserPane = null!;
    private View _gitPane = null!;
    private LinearProgressIndicator _gitProgress = null!;
    private View _gitHeaderBar = null!;
    private TextView _gitBranchText = null!;
    private TextView _gitRepoRootText = null!;
    private TextView _gitNotice = null!;
    private RecyclerView _gitChangesList = null!;
    private View _gitEmptyWrapper = null!;
    private TextView _gitEmptyText = null!;
    private View _gitSubreposBar = null!;
    private ChipGroup _gitSubreposChips = null!;
    private GitChangesAdapter _gitChangesAdapter = null!;

    private LinearLayout _crumbs = null!;
    private HorizontalScrollView _crumbsScroll = null!;
    private View _searchRow = null!;
    private TextInputEditText _filterText = null!;
    private MaterialButton _sortBtn = null!;
    private TextView _status = null!;
    private RecyclerView _list = null!;
    private View _empty = null!;
    private View _pasteBar = null!;
    private TextView _pasteText = null!;
    private ExtendedFloatingActionButton _createFab = null!;
    private EntryAdapter _adapter = null!;
    private IReadOnlyList<WorkspaceEntry> _entries = [];

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        var view = inflater.Inflate(Resource.Layout.fragment_files, container, false)!;
        _lastCrumbsKey = null;
        _dir = savedInstanceState?.GetString("dir") ?? _dir;
        _gitMode = savedInstanceState?.GetBoolean("gitMode", false) ?? _gitMode;
        _isWide = (Activity as MainActivity)?.IsWideLayout ?? false;

        _toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.toolbar)!;
        _toolbar.SetTitle(Resource.String.files_title);
        _toolbar.MenuItemClick += (_, e) => OnMenu(e.Item?.ItemId ?? 0);
        // Polish-P3：统一交给 Activity 返回栈，窄屏由外壳从工作区平顺回到 ChatsFragment 首页。
        _toolbar.NavigationClick += (_, _) => Activity?.OnBackPressedDispatcher?.OnBackPressed();
        _filesProgress = view.FindViewById<LinearProgressIndicator>(Resource.Id.files_progress)!;

        _filesModeButton = view.FindViewById<MaterialButton>(Resource.Id.files_mode_files)!;
        _gitModeButton = view.FindViewById<MaterialButton>(Resource.Id.files_mode_git)!;
        _browserPane = view.FindViewById(Resource.Id.files_browser_pane)!;
        _gitPane = view.FindViewById(Resource.Id.files_git_pane)!;
        _gitProgress = view.FindViewById<LinearProgressIndicator>(Resource.Id.git_progress)!;
        _gitHeaderBar = view.FindViewById(Resource.Id.git_header_bar)!;
        _gitBranchText = view.FindViewById<TextView>(Resource.Id.git_branch_text)!;
        _gitRepoRootText = view.FindViewById<TextView>(Resource.Id.git_repo_root_text)!;
        _gitNotice = view.FindViewById<TextView>(Resource.Id.git_notice)!;
        _gitChangesList = view.FindViewById<RecyclerView>(Resource.Id.git_changes_list)!;
        _gitEmptyWrapper = view.FindViewById(Resource.Id.git_empty_wrapper)!;
        _gitEmptyText = view.FindViewById<TextView>(Resource.Id.git_empty_text)!;
        _gitSubreposBar = view.FindViewById(Resource.Id.git_subrepos_bar)!;
        _gitSubreposChips = view.FindViewById<ChipGroup>(Resource.Id.git_subrepos_chips)!;

        _gitChangesList.SetLayoutManager(new LinearLayoutManager(RequireContext()));
        // UI-S5：Git 变更点击直接拉起独立全屏 DiffReviewActivity，按返回无缝回到变更列表
        _gitChangesAdapter = new GitChangesAdapter(OpenGitDiff);
        _gitChangesList.SetAdapter(_gitChangesAdapter);

        // Polish-P4：两颗独立 MaterialButton 自行维护选中态，避免 ToggleGroup 切平内侧圆角。
        UpdateModeSelection();
        _filesModeButton.Click += (_, _) => SetGitMode(false);
        _gitModeButton.Click += (_, _) => SetGitMode(true);

        _crumbs = view.FindViewById<LinearLayout>(Resource.Id.files_crumbs)!;
        _crumbsScroll = view.FindViewById<HorizontalScrollView>(Resource.Id.files_crumbs_scroll)!;

        _searchRow = view.FindViewById(Resource.Id.files_search_row)!;
        _searchRow.Visibility = ViewStates.Gone;
        _searchExpanded = false;
        var filterLayout = view.FindViewById(Resource.Id.files_filter_layout)!;
        _filterText = filterLayout.FindViewById<TextInputEditText>(Resource.Id.search_input)!;
        _filterText.Hint = GetString(Resource.String.files_filter_or_search_hint);
        _filterText.TextChanged += (_, _) =>
        {
            _filter = _filterText.Text ?? "";
            Show();
        };
        _filterText.EditorAction += (_, e) =>
        {
            if (e.ActionId is ImeAction.Search or ImeAction.Done or ImeAction.Go or ImeAction.Unspecified
                || e.Event?.KeyCode == Keycode.Enter)
            {
                e.Handled = true;
                TriggerInlineSearch();
            }
        };
        _filterText.KeyPress += (_, e) =>
        {
            if (e.KeyCode == Keycode.Enter)
            {
                e.Handled = true;
                if (e.Event?.Action == KeyEventActions.Up)
                {
                    TriggerInlineSearch();
                }
            }
            else
            {
                e.Handled = false;
            }
        };

        _sortBtn = view.FindViewById<MaterialButton>(Resource.Id.files_sort_btn)!;
        _sortBtn.Click += (_, _) => ChooseSort();

        _status = view.FindViewById<TextView>(Resource.Id.files_status)!;
        _list = view.FindViewById<RecyclerView>(Resource.Id.files_list)!;
        _list.SetLayoutManager(new LinearLayoutManager(RequireContext()));
        _list.SetItemAnimator(null);
        _adapter = new EntryAdapter(this);
        _list.SetAdapter(_adapter);

        _empty = view.FindViewById(Resource.Id.files_empty)!;
        _empty.FindViewById<ImageView>(Resource.Id.empty_icon)!.SetImageResource(Resource.Drawable.ic_folder);
        _pasteBar = view.FindViewById(Resource.Id.files_paste_bar)!;
        _pasteText = view.FindViewById<TextView>(Resource.Id.files_paste_text)!;
        view.FindViewById<MaterialButton>(Resource.Id.files_paste)!.Click += (_, _) => _ = PasteAsync();
        view.FindViewById<MaterialButton>(Resource.Id.files_paste_clear)!.Click += (_, _) =>
        {
            _clip = [];
            UpdateChrome();
        };

        _createFab = view.FindViewById<ExtendedFloatingActionButton>(Resource.Id.files_create_fab)!;
        _createFab.Click += (_, _) => ShowCreateSheet();
        _list.AddOnScrollListener(new FabScrollListener(_createFab));

        // Polish-P4：切换器自带 18dp 圆角底槽背景，必须用外边距限宽（而不是加内边距限宽），
        // 否则 ≥600dp 时底槽会被拉满整个内容宽、按钮被内边距居中，四周 3dp 留白不再均匀。
        Edges.ClampMaxWidthByMargin(view.FindViewById(Resource.Id.files_mode_toggle));
        Edges.ClampMaxWidth(
            view.FindViewById(Resource.Id.files_search_row),
            view.FindViewById(Resource.Id.files_crumbs_scroll),
            _list,
            view.FindViewById(Resource.Id.git_changes_list));

        EnsureProvider();
        ApplyModeVisibility();
        if (_pendingTargetPath is { } pendingPath)
        {
            var pendingDiff = _pendingTargetDiff;
            _pendingTargetPath = null;
            OpenWorkspaceTarget(pendingPath, pendingDiff);
        }
        else if (_gitMode)
        {
            _ = LoadGitStatusAsync(_dir);
        }
        else
        {
            _ = LoadAsync();
        }
        return view;
    }

    public override void OnSaveInstanceState(Bundle outState)
    {
        base.OnSaveInstanceState(outState);
        outState.PutString("dir", _dir);
        outState.PutBoolean("gitMode", _gitMode);
    }

    public override void OnHiddenChanged(bool hidden)
    {
        base.OnHiddenChanged(hidden);
        if (!hidden)
        {
            _isWide = (Activity as MainActivity)?.IsWideLayout ?? false;
            if (_gitMode)
            {
                _ = LoadGitStatusAsync(_dir);
            }
            else if (_searchQuery is null)
            {
                _ = LoadAsync();
            }
        }
    }

    /// <summary>Polish-P3：窄屏始终显示返回首页箭头，宽屏仅在进入子目录 / 搜索 / 多选时显示向上箭头。</summary>
    public void OnShellWidthChanged(bool wide)
    {
        _isWide = wide;
        if (IsAdded && View is not null)
        {
            UpdateChrome();
        }
    }

    public bool OnBack()
    {
        if (_gitMode)
        {
            SetGitMode(false);
            return true;
        }
        // 展开中的搜索栏优先收起，并同时清空筛选词与已执行的递归搜索结果，避免隐藏后仍停留在旧结果。
        if (_searchExpanded)
        {
            CollapseSearch();
            return true;
        }
        if (_searchQuery is not null)
        {
            _searchQuery = null;
            _searchResult = null;
            _filter = "";
            _filterText.Text = "";
            _ = LoadAsync();
            return true;
        }
        if (_selected.Count > 0)
        {
            _selected.Clear();
            Show();
            return true;
        }
        if (_dir.Length == 0)
        {
            return false;
        }
        var i = _dir.LastIndexOf('/');
        Navigate(i < 0 ? "" : _dir[..i]);
        return true;
    }

    public void OnReselected()
    {
        if (_gitMode)
        {
            _ = LoadGitStatusAsync(_dir);
            return;
        }
        Navigate("");
    }

    /// <summary>从聊天工具卡等入口直达工作区文件预览或 Git Diff（N-06-02），严格校验工作区边界与 <c>.dsh/</c> 保护。</summary>
    public void OpenWorkspaceTarget(string relativePath, bool preferDiff)
    {
        string norm;
        try
        {
            norm = WorkspaceFileSystem.NormalizeRelative(relativePath);
            if (norm.Length == 0 || WorkspaceFileSystem.IsProtected(norm))
            {
                if (IsAdded)
                {
                    ShowStatus(GetString(Resource.String.files_protected));
                }
                return;
            }
        }
        catch (WorkspaceException ex)
        {
            if (IsAdded)
            {
                ShowError(ex);
            }
            return;
        }

        if (View is null || !IsAdded)
        {
            _pendingTargetPath = norm;
            _pendingTargetDiff = preferDiff;
            return;
        }

        if (!EnsureProvider())
        {
            ShowStatus(GetString(Resource.String.files_no_workspace));
            return;
        }

        var parentSlash = norm.LastIndexOf('/');
        var parentDir = parentSlash < 0 ? "" : norm[..parentSlash];
        if (preferDiff)
        {
            // UI-S5：统一拉起独立全屏 DiffReviewActivity；其内部再次执行工作区安全路径校验
            // （规范化、越界拒绝、.dsh/ 保护与符号链接拦截），绝不绕过安全门禁。
            StartActivity(DiffReviewActivity.CreateWorkspaceIntent(RequireContext(), norm));
        }
        else
        {
            SetGitMode(false, triggerLoad: false);
            _ = OpenTargetFilePreviewAsync(parentDir, norm);
        }
    }

    /// <summary>同步两颗独立滑块按钮的选中态；选中背景由 dshm_segment_bg 的 checked 状态驱动。</summary>
    private void UpdateModeSelection()
    {
        _filesModeButton.Checked = !_gitMode;
        _gitModeButton.Checked = _gitMode;
    }

    private void SetGitMode(bool gitMode, bool triggerLoad = true)
    {
        _gitMode = gitMode;
        UpdateModeSelection();
        ApplyModeVisibility();
        UpdateChrome();
        if (triggerLoad)
        {
            if (gitMode)
            {
                _ = LoadGitStatusAsync(_dir);
            }
            else
            {
                _ = LoadAsync();
            }
        }
    }

    private void ApplyModeVisibility()
    {
        _browserPane.Visibility = _gitMode ? ViewStates.Gone : ViewStates.Visible;
        _gitPane.Visibility = _gitMode ? ViewStates.Visible : ViewStates.Gone;
    }

    private async Task LoadGitStatusAsync(string repoSearchDir)
    {
        var git = DshMobileApplication.Current.Bridge.CreateWorkspaceGitService();
        if (git is null)
        {
            _gitHeaderBar.Visibility = ViewStates.Gone;
            ShowGitEmpty(GetString(Resource.String.files_no_workspace), subRepos: []);
            return;
        }
        var gen = ++_gitGeneration;
        _gitProgress.Visibility = ViewStates.Visible;
        _gitNotice.Visibility = ViewStates.Gone;
        try
        {
            var fs = _provider?.FileSystem;
            var (status, subRepos) = await Task.Run(async () =>
            {
                var st = await git.GetStatusAsync(repoSearchDir).ConfigureAwait(false);
                IReadOnlyList<Breadcrumb> subs = !st.IsRepository && fs is not null && st.ErrorCode != GitErrors.GitUnavailable
                    ? FileListing.DiscoverGitRepositories(fs, repoSearchDir)
                    : [];
                return (st, subs);
            }).ConfigureAwait(false);

            Activity?.RunOnUiThread(() =>
            {
                if (gen != _gitGeneration || !IsAdded)
                {
                    return;
                }
                _gitProgress.Visibility = ViewStates.Gone;
                ApplyGitStatus(status, subRepos);
            });
        }
        catch (WorkspaceException ex)
        {
            Activity?.RunOnUiThread(() =>
            {
                if (gen == _gitGeneration && IsAdded)
                {
                    _gitProgress.Visibility = ViewStates.Gone;
                    _gitHeaderBar.Visibility = ViewStates.Gone;
                    ShowGitEmpty($"{ex.Code}: {ex.Message}", subRepos: []);
                }
            });
        }
    }

    private void ApplyGitStatus(GitStatusResult status, IReadOnlyList<Breadcrumb> subRepos)
    {
        var ctx = RequireContext();
        if (!status.IsRepository)
        {
            _gitHeaderBar.Visibility = ViewStates.Gone;
            _gitRepoRoot = "";
            _gitBranch = "";
            _gitBranchText.Text = string.Empty;
            _gitRepoRootText.Text = string.Empty;
            _gitChangesAdapter.Submit([]);
            var msg = status.ErrorCode == GitErrors.GitUnavailable
                ? GetString(Resource.String.git_unavailable)
                : GetString(Resource.String.git_not_repo);
            ShowGitEmpty(msg, subRepos);
            UpdateChrome();
            return;
        }

        _gitHeaderBar.Visibility = ViewStates.Visible;
        _gitSubreposBar.Visibility = ViewStates.Gone;
        _gitRepoRoot = status.RepoRelativeRoot;
        _gitBranch = status.BranchName ?? "";
        _gitBranchText.Text = status.IsDetachedHead
            ? Rows.Fmt(ctx, Resource.String.git_branch_detached, status.BranchName ?? "HEAD")
            : Rows.Fmt(ctx, Resource.String.git_branch_fmt, status.BranchName ?? "HEAD");
        _gitRepoRootText.Text = Rows.Fmt(
            ctx,
            Resource.String.git_repo_root_fmt,
            string.IsNullOrEmpty(status.RepoRelativeRoot) ? GuestRoot : GuestRoot + "/" + status.RepoRelativeRoot);

        _gitChangesAdapter.Submit(status.Changes);
        if (status.Truncated)
        {
            _gitNotice.Visibility = ViewStates.Visible;
            _gitNotice.Text = Rows.Fmt(ctx, Resource.String.git_changes_truncated, status.Changes.Count, status.TotalChangeCount);
        }
        else
        {
            _gitNotice.Visibility = ViewStates.Gone;
        }

        if (status.Changes.Count == 0)
        {
            ShowGitEmpty(GetString(Resource.String.git_clean), subRepos: []);
        }
        else
        {
            _gitEmptyWrapper.Visibility = ViewStates.Gone;
        }
        UpdateChrome();
    }

    private void ShowGitEmpty(string message, IReadOnlyList<Breadcrumb> subRepos)
    {
        _gitChangesAdapter.Submit([]);
        _gitNotice.Visibility = ViewStates.Gone;
        _gitEmptyText.Text = message;
        _gitEmptyWrapper.Visibility = ViewStates.Visible;

        _gitSubreposChips.RemoveAllViews();
        if (subRepos.Count == 0)
        {
            _gitSubreposBar.Visibility = ViewStates.Gone;
            return;
        }

        _gitSubreposBar.Visibility = ViewStates.Visible;
        var inflater = LayoutInflater.From(RequireContext())!;
        foreach (var repo in subRepos)
        {
            var chip = (Chip)inflater.Inflate(Resource.Layout.item_git_subrepo_chip, _gitSubreposChips, false)!;
            chip.Text = repo.Label;
            chip.Click += (_, _) =>
            {
                _dir = repo.Path;
                _ = LoadGitStatusAsync(_dir);
            };
            _gitSubreposChips.AddView(chip);
        }
    }

    /// <summary>
    /// UI-S5：Git 变更行点击后直接拉起独立全屏 <see cref="DiffReviewActivity"/>。
    /// 路径由 <see cref="WorkspaceGitService"/> 在审查页内再次校验（.dsh/ 保护、越界、符号链接），
    /// 这里只负责携带仓库相对根与文件相对路径，且不再维护任何内嵌 Diff 视图状态。
    /// </summary>
    private void OpenGitDiff(GitChangeEntry entry)
    {
        if (!EnsureProvider())
        {
            ShowStatus(GetString(Resource.String.files_no_workspace));
            return;
        }
        StartActivity(DiffReviewActivity.CreateIntent(RequireContext(), _gitRepoRoot, entry.RelativePath, _gitBranch));
    }

    private async Task OpenTargetFilePreviewAsync(string parentDir, string workspaceRelativePath)
    {
        Navigate(parentDir);
        await LoadAsync();
        if (!IsAdded || _provider is null)
        {
            return;
        }
        try
        {
            var entry = await _provider.StatAsync(workspaceRelativePath, CancellationToken.None);
            if (entry.Kind == WorkspaceEntryKind.File && _provider.CapabilitiesOf(entry).HasFlag(FileCapabilities.Read))
            {
                OpenEditorActivity(entry, startEditing: false);
            }
            else if (entry.Kind == WorkspaceEntryKind.Symlink)
            {
                ShowStatus(GetString(Resource.String.files_symlink_blocked));
            }
        }
        catch (WorkspaceException ex)
        {
            ShowError(ex);
        }
    }

    private bool EnsureProvider()
    {
        if (_provider is not null)
        {
            return true;
        }
        var files = RequireContext().FilesDir!.AbsolutePath;
        var workspace = Path.Combine(files, "user-data");
        if (!Directory.Exists(workspace))
        {
            return false;
        }
        var provider = new WorkspaceProvider(new WorkspaceFileSystem(workspace), Path.Combine(files, "dshmobile-files-trash"), GuestRoot);
        _provider = provider;
        _ = Task.Run(() =>
        {
            try
            {
                provider.PurgeTrash(TrashAge);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn("DshMobile", "files: trash purge failed: " + e.Message);
            }
        });
        return true;
    }

    private void Navigate(string dir)
    {
        _dir = dir;
        _searchQuery = null;
        _searchResult = null;
        _selected.Clear();
        _filter = "";
        _filterText.Text = "";
        ShowStatus(null);
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (!EnsureProvider())
        {
            _entries = [];
            Show(GetString(Resource.String.files_no_workspace));
            return;
        }
        var gen = ++_loadGeneration;
        try
        {
            var entries = await _provider!.ListAsync(_dir, CancellationToken.None);
            if (gen == _loadGeneration && IsAdded)
            {
                _entries = entries;
                _selected.IntersectWith(entries.Select(e => e.RelativePath));
                Show();
            }
        }
        catch (WorkspaceException e)
        {
            if (gen == _loadGeneration && IsAdded)
            {
                _entries = [];
                Show();
                ShowError(e);
            }
        }
    }

    private void Show(string? emptyText = null)
    {
        if (!IsAdded)
        {
            return;
        }
        var source = _searchResult?.Matches ?? _entries;
        var shown = FileListing.Filter(FileListing.Sort(source, _sort, _descending), _filter);
        _adapter.Update(shown, _selected, inSearch: _searchQuery is not null);
        _empty.FindViewById<TextView>(Resource.Id.empty_text)!.Text = emptyText ?? GetString(Resource.String.files_empty);
        _empty.Visibility = shown.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
        UpdateChrome();
    }

    private void UpdateChrome()
    {
        var ctx = RequireContext();
        _filesProgress.Visibility = _busy ? ViewStates.Visible : ViewStates.Gone;

        var showFab = !_gitMode && _selected.Count == 0 && _provider is not null;
        if (showFab)
        {
            _createFab.Show();
        }
        else
        {
            _createFab.Hide();
        }

        if (_gitMode)
        {
            _toolbar.Subtitle = null;
            // Git 页签只展示变更列表；Diff 在独立全屏审查页中查看。窄屏仍保留返回首页箭头。
            ApplyToolbarNavigation();
            SyncMenu(isSelection: false, isGitMode: true);
            return;
        }

        _toolbar.Subtitle = _selected.Count > 0
            ? Rows.Fmt(ctx, Resource.String.files_selected, _selected.Count)
            : _searchQuery is { } q ? Rows.Fmt(ctx, Resource.String.files_search_title, q) : null;
        ApplyToolbarNavigation();

        var sortName = GetString(_sort switch
        {
            FileSort.Size => Resource.String.files_sort_size,
            FileSort.Modified => Resource.String.files_sort_modified,
            FileSort.Type => Resource.String.files_sort_type,
            _ => Resource.String.files_sort_name,
        });
        _sortBtn.Text = $"{sortName} {(_descending ? "↓" : "↑")}";

        SyncMenu(isSelection: _selected.Count > 0, isGitMode: false);
        BuildCrumbs(ctx);

        _pasteBar.Visibility = _clip.Count > 0 && _searchQuery is null ? ViewStates.Visible : ViewStates.Gone;
        if (_clip.Count > 0)
        {
            _pasteText.Text = Rows.Fmt(ctx, _clipCut ? Resource.String.files_clip_cut : Resource.String.files_clip_copy, _clip.Count);
        }
    }

    /// <summary>
    /// Polish-P3：窄屏顶栏固定显示 ←（返回首页）；宽屏仅在工作区子目录 / 搜索 / 多选时显示向上箭头，
    /// 顶栏不出现第二个返回箭头，点击统一交给 OnBackPressedDispatcher。
    /// </summary>
    private void ApplyToolbarNavigation()
    {
        if (_selected.Count > 0)
        {
            _toolbar.SetNavigationIcon(Resource.Drawable.ic_close);
            _toolbar.SetNavigationContentDescription(Resource.String.files_clear_selection);
            return;
        }
        if (!_isWide)
        {
            _toolbar.SetNavigationIcon(Resource.Drawable.ic_arrow_back);
            // 根目录回首页，子目录先回上一级；无障碍描述跟随真实语义，不再一律播报「返回首页」。
            _toolbar.SetNavigationContentDescription(_dir.Length == 0
                ? Resource.String.shell_back_home
                : Resource.String.files_up);
            return;
        }
        if (_dir.Length == 0 && _searchQuery is null)
        {
            _toolbar.NavigationIcon = null;
            return;
        }
        _toolbar.SetNavigationIcon(Resource.Drawable.ic_arrow_back);
        _toolbar.SetNavigationContentDescription(Resource.String.files_up);
    }

    private void SyncMenu(bool isSelection, bool isGitMode)
    {
        var menu = _toolbar.Menu!;
        if (menu.Size() == 0 || isSelection != _menuIsSelection)
        {
            menu.Clear();
            _toolbar.InflateMenu(isSelection ? Resource.Menu.files_selection_toolbar : Resource.Menu.files_toolbar);
            _menuIsSelection = isSelection;
        }

        for (var i = 0; i < menu.Size(); i++)
        {
            var item = menu.GetItem(i)!;
            var id = item.ItemId;
            if (!isSelection)
            {
                var visible = !isGitMode
                    || id == Resource.Id.files_menu_refresh
                    || id == Resource.Id.files_menu_open_root;
                item.SetVisible(visible);
                item.SetEnabled(!_busy && (id != Resource.Id.files_menu_open_root || _dir.Length > 0));
            }
            else
            {
                item.SetEnabled(!_busy);
            }
        }
    }

    private void BuildCrumbs(Context ctx)
    {
        var key = $"{_dir}|{_searchQuery}";
        if (string.Equals(_lastCrumbsKey, key, StringComparison.Ordinal) && _crumbs.ChildCount > 0)
        {
            return;
        }
        _lastCrumbsKey = key;
        _crumbs.RemoveAllViews();
        var inflater = LayoutInflater.From(ctx)!;
        var crumbs = FileListing.Breadcrumbs(GuestRoot, _dir);
        for (var i = 0; i < crumbs.Count; i++)
        {
            var crumb = crumbs[i];
            var crumbView = inflater.Inflate(Resource.Layout.item_file_crumb, _crumbs, false)!;
            var sep = crumbView.FindViewById(Resource.Id.crumb_sep)!;
            var label = crumbView.FindViewById<TextView>(Resource.Id.crumb_label)!;

            sep.Visibility = i == 0 ? ViewStates.Gone : ViewStates.Visible;
            label.Text = crumb.Label;

            var isCurrent = i == crumbs.Count - 1 && _searchQuery is null;
            if (isCurrent)
            {
                label.Clickable = false;
                label.Focusable = false;
                label.SetTypeface(null, global::Android.Graphics.TypefaceStyle.Bold);
                label.SetTextColor(new global::Android.Graphics.Color(Rows.Color(label, Resource.Attribute.colorOnSurface)));
            }
            else
            {
                label.Clickable = true;
                label.Focusable = true;
                label.SetTypeface(null, global::Android.Graphics.TypefaceStyle.Normal);
                label.SetTextColor(new global::Android.Graphics.Color(Rows.Color(label, Resource.Attribute.colorOnSurfaceVariant)));
                label.Click += (_, _) => Navigate(crumb.Path);
            }
            _crumbs.AddView(crumbView);
        }
        _crumbsScroll.Post(() => _crumbsScroll.ScrollTo(Math.Max(0, _crumbs.Width - _crumbsScroll.Width), 0));
    }

    private void OnMenu(int id)
    {
        if (id == Resource.Id.files_menu_refresh)
        {
            if (_gitMode)
            {
                _ = LoadGitStatusAsync(_dir);
            }
            else
            {
                ShowStatus(null);
                _ = LoadAsync();
            }
        }
        else if (id == Resource.Id.files_menu_open_root)
        {
            if (_gitMode)
            {
                _dir = "";
                _ = LoadGitStatusAsync("");
            }
            else
            {
                Navigate("");
            }
        }
        else if (id == Resource.Id.files_menu_import)
        {
            StartImport();
        }
        else if (id == Resource.Id.files_menu_toggle_search)
        {
            ToggleSearch();
        }
        else if (id == Resource.Id.files_menu_search)
        {
            AskName(Resource.String.files_search, Resource.String.files_search_hint, "", q => _ = SearchAsync(q));
        }
        else if (id == Resource.Id.files_menu_sort)
        {
            ChooseSort();
        }
        else if (id == Resource.Id.files_menu_new_folder)
        {
            AskName(Resource.String.files_new_folder, Resource.String.files_name_hint, "", n => Run(() => _provider!.CreateDirectory(_dir, n)));
        }
        else if (id == Resource.Id.files_menu_new_file)
        {
            AskName(Resource.String.files_new_file, Resource.String.files_name_hint, "", n => Run(() => _provider!.CreateFile(_dir, n)));
        }
        else if (id == Resource.Id.files_menu_copy || id == Resource.Id.files_menu_cut)
        {
            _clip = [.. _selected];
            _clipCut = id == Resource.Id.files_menu_cut;
            _selected.Clear();
            Show();
        }
        else if (id == Resource.Id.files_menu_delete)
        {
            ConfirmDelete([.. _selected]);
        }
        else if (id == Resource.Id.files_menu_zip)
        {
            _ = ZipAsync([.. _selected]);
        }
        else if (id == Resource.Id.files_menu_select_all)
        {
            foreach (var e in _adapter.Items)
            {
                if (_provider!.CapabilitiesOf(e).HasFlag(FileCapabilities.Delete))
                {
                    _selected.Add(e.RelativePath);
                }
            }
            Show();
        }
        else if (id == Resource.Id.files_menu_clear_selection)
        {
            _selected.Clear();
            Show();
        }
    }

    private void ShowCreateSheet()
    {
        var displayDir = string.IsNullOrEmpty(_dir) ? GuestRoot : $"{GuestRoot}/{_dir}";
        WorkspaceCreateBottomSheet.ShowSheet(ChildFragmentManager, displayDir);
    }

    private void ChooseSort()
    {
        var keys = new[] { FileSort.Name, FileSort.Size, FileSort.Modified, FileSort.Type };
        var labels = new[] { Resource.String.files_sort_name, Resource.String.files_sort_size, Resource.String.files_sort_modified, Resource.String.files_sort_type }
            .Select((r, i) => GetString(r) + (keys[i] == _sort ? (_descending ? " ↓" : " ↑") : "")).ToArray();
        new MaterialAlertDialogBuilder(RequireContext())
            .SetTitle(GetString(Resource.String.files_sort) + " · " + GetString(Resource.String.files_sort_hint))!
            .SetSingleChoiceItems(labels, Array.IndexOf(keys, _sort), (d, e) =>
            {
                var key = keys[e.Which];
                _descending = key == _sort && !_descending;
                _sort = key;
                (d as IDialogInterface)?.Dismiss();
                Show();
            })!
            .Show();
    }

    private void OnClick(WorkspaceEntry e)
    {
        if (_selected.Count > 0)
        {
            Toggle(e);
            return;
        }
        if (e.Kind == WorkspaceEntryKind.Directory)
        {
            Navigate(e.RelativePath);
            return;
        }
        if (e.Kind == WorkspaceEntryKind.File
            && WorkspaceProvider.FormatOf(e.Name) is null
            && _provider!.CapabilitiesOf(e).HasFlag(FileCapabilities.Read))
        {
            OpenEditorActivity(e, startEditing: false);
            return;
        }
        ShowActions(e);
    }

    private void OnLongClick(WorkspaceEntry e) => Toggle(e);

    private void Toggle(WorkspaceEntry e)
    {
        if (!_selected.Remove(e.RelativePath))
        {
            if (!_provider!.CapabilitiesOf(e).HasFlag(FileCapabilities.Delete))
            {
                ShowStatus(GetString(Resource.String.files_protected));
                return;
            }
            _selected.Add(e.RelativePath);
        }
        Show();
    }

    private void ShowActions(WorkspaceEntry e)
    {
        var provider = _provider!;
        var caps = provider.CapabilitiesOf(e);
        if (e.Kind == WorkspaceEntryKind.Symlink)
        {
            ShowStatus(GetString(Resource.String.files_symlink_blocked) + " → " + (e.LinkTarget ?? "?"));
        }
        if (caps == FileCapabilities.None)
        {
            ShowStatus(GetString(Resource.String.files_protected));
            return;
        }
        FileActionsBottomSheet.Show(ChildFragmentManager, e, caps);
    }

    public void OnFileAction(FileActionsBottomSheet.Action action, WorkspaceEntry entry)
    {
        var provider = _provider;
        if (provider is null)
        {
            return;
        }

        var caps = provider.CapabilitiesOf(entry);
        switch (action)
        {
            case FileActionsBottomSheet.Action.OpenOrEdit:
                OpenEditorActivity(entry, startEditing: caps.HasFlag(FileCapabilities.Write));
                break;
            case FileActionsBottomSheet.Action.Export:
                _pendingExport = entry.RelativePath;
#pragma warning disable CS0618
                StartActivityForResult(DocumentPicker.CreateIntent(entry.Name), RequestExport);
#pragma warning restore CS0618
                break;
            case FileActionsBottomSheet.Action.Extract:
                _ = ExtractAsync(entry);
                break;
            case FileActionsBottomSheet.Action.Rename:
                AskName(Resource.String.files_rename, Resource.String.files_name_hint, entry.Name,
                    n => Run(() => provider.Rename(entry.RelativePath, n)));
                break;
            case FileActionsBottomSheet.Action.Copy:
                SetClip([entry.RelativePath], cut: false);
                break;
            case FileActionsBottomSheet.Action.Cut:
                SetClip([entry.RelativePath], cut: true);
                break;
            case FileActionsBottomSheet.Action.Zip:
                _ = ZipAsync([entry.RelativePath]);
                break;
            case FileActionsBottomSheet.Action.Delete:
                ConfirmDelete([entry.RelativePath]);
                break;
        }
    }

    public void OnWorkspaceCreateAction(WorkspaceCreateBottomSheet.Action action)
    {
        switch (action)
        {
            case WorkspaceCreateBottomSheet.Action.NewFile:
                AskName(Resource.String.files_new_file, Resource.String.files_name_hint, "",
                    n => Run(() => _provider!.CreateFile(_dir, n)));
                break;
            case WorkspaceCreateBottomSheet.Action.NewFolder:
                AskName(Resource.String.files_new_folder, Resource.String.files_name_hint, "",
                    n => Run(() => _provider!.CreateDirectory(_dir, n)));
                break;
            case WorkspaceCreateBottomSheet.Action.Import:
                StartImport();
                break;
        }
    }

    private void OpenEditorActivity(WorkspaceEntry e, bool startEditing)
    {
#pragma warning disable CS0618
        StartActivityForResult(WorkspaceEditorActivity.CreateIntent(RequireContext(), e.RelativePath, startEditing), RequestEditor);
#pragma warning restore CS0618
    }

    private void SetClip(List<string> paths, bool cut)
    {
        _clip = paths;
        _clipCut = cut;
        UpdateChrome();
    }

    private void Run(Func<string> op)
    {
        try
        {
            var done = op();
            ShowStatus(Rows.Fmt(RequireContext(), Resource.String.files_done, GuestRoot + "/" + done));
        }
        catch (WorkspaceException e)
        {
            ShowError(e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowStatus($"{WorkspaceErrors.IoError}: {e.Message}");
        }
        _ = LoadAsync();
    }

    private async Task Busy(Func<Task<string?>> op)
    {
        if (_busy)
        {
            return;
        }
        _busy = true;
        ShowStatus(GetString(Resource.String.files_busy));
        UpdateChrome();
        try
        {
            ShowStatus(await op());
        }
        catch (WorkspaceException e)
        {
            ShowError(e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowStatus($"{WorkspaceErrors.IoError}: {e.Message}");
        }
        finally
        {
            _busy = false;
        }
        if (IsAdded)
        {
            await LoadAsync();
        }
    }

    private string Describe(TransferResult r)
    {
        var ctx = RequireContext();
        var text = r.Failed.Count == 0
            ? Rows.Fmt(ctx, Resource.String.files_done, string.Join(", ", r.Created))
            : Rows.Fmt(ctx, Resource.String.files_partial, r.Created.Count, r.Failed.Count, string.Join("; ", r.Failed.Select(f => $"{f.Path} {f.Code}")));
        return r.SkippedLinks > 0 ? text + Rows.Fmt(ctx, Resource.String.files_skipped_links, r.SkippedLinks) : text;
    }

    private Task PasteAsync()
    {
        var paths = _clip;
        var cut = _clipCut;
        return Busy(async () =>
        {
            var r = cut
                ? await _provider!.MoveAsync(paths, _dir, CancellationToken.None)
                : await _provider!.CopyAsync(paths, _dir, CancellationToken.None);
            if (cut)
            {
                _clip = [];
            }
            return Describe(r);
        });
    }

    private void ConfirmDelete(List<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }
        var trash = _provider!.Capabilities.HasFlag(FileCapabilities.Trash);
        new MaterialAlertDialogBuilder(RequireContext())
            .SetIcon(Resource.Drawable.ic_delete)!
            .SetTitle(Rows.Fmt(RequireContext(), Resource.String.files_delete_title, paths.Count))!
            .SetMessage(trash ? Resource.String.files_delete_trash : Resource.String.files_delete_permanent)!
            .SetPositiveButton(Resource.String.files_delete, (_, _) => _ = DeleteAsync(paths))!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .Show();
    }

    private async Task DeleteAsync(List<string> paths)
    {
        DeleteReceipt? receipt = null;
        await Busy(async () =>
        {
            var got = await _provider!.DeleteAsync(paths, CancellationToken.None);
            receipt = got;
            _selected.Clear();
            _clip.RemoveAll(c => got.Deleted.Contains(c));
            return got.Failed.Count == 0 ? null
                : Rows.Fmt(RequireContext(), Resource.String.files_partial, got.Deleted.Count, got.Failed.Count, string.Join("; ", got.Failed.Select(f => $"{f.Path} {f.Code}")));
        });
        if (receipt is { Deleted.Count: > 0 } r && IsAdded && View is { } root)
        {
            var bar = Snackbar.Make(root, Rows.Fmt(RequireContext(), Resource.String.files_deleted, r.Deleted.Count), Snackbar.LengthLong);
            if (r.Restorable)
            {
                bar.SetAction(GetString(Resource.String.files_undo), view => _ = Busy(async () =>
                {
                    var restored = await _provider!.RestoreAsync(r, CancellationToken.None);
                    return Rows.Fmt(RequireContext(), Resource.String.files_restored, string.Join(", ", restored));
                }));
            }
            bar.Show();
        }
    }

    private Task ExtractAsync(WorkspaceEntry e) => Busy(async () =>
    {
        var r = await _provider!.ExtractAsync(e.RelativePath, _dir, null, CancellationToken.None);
        var ctx = RequireContext();
        var text = Rows.Fmt(ctx, Resource.String.files_extracted, GuestRoot + "/" + r.Directory, r.Files);
        return r.Skipped.Count == 0 ? text : text + Rows.Fmt(ctx, Resource.String.files_extract_skipped, r.Skipped.Count, string.Join(", ", r.Skipped.Take(5)));
    });

    private Task ZipAsync(List<string> paths)
    {
        if (paths.Count == 0)
        {
            return Task.CompletedTask;
        }
        var name = paths.Count == 1 ? paths[0][(paths[0].LastIndexOf('/') + 1)..] : "archive";
        return Busy(async () =>
        {
            var r = await _provider!.CreateZipAsync(paths, _dir, name, CancellationToken.None);
            _selected.Clear();
            var text = Rows.Fmt(RequireContext(), Resource.String.files_zipped, GuestRoot + "/" + r.Path, r.Files);
            return r.SkippedLinks > 0 ? text + Rows.Fmt(RequireContext(), Resource.String.files_skipped_links, r.SkippedLinks) : text;
        });
    }

    private void TriggerInlineSearch()
    {
        var query = _filterText.Text?.Trim();
        if (Context?.GetSystemService(Context.InputMethodService) is InputMethodManager imm && _filterText.WindowToken is { } token)
        {
            imm.HideSoftInputFromWindow(token, HideSoftInputFlags.None);
        }
        _filterText.ClearFocus();
        if (!string.IsNullOrEmpty(query))
        {
            _ = SearchAsync(query);
        }
    }

    /// <summary>UI-S5：顶栏 🔍 图标平滑展开/收起工作区搜索框（默认隐藏，常态释放约 64dp 顶部空间）。</summary>
    private void ToggleSearch()
    {
        if (_searchExpanded)
        {
            CollapseSearch();
        }
        else
        {
            ExpandSearch();
        }
    }

    private void ExpandSearch()
    {
        if (!IsAdded || View is null)
        {
            return;
        }
        _searchExpanded = true;
        _searchRow.Visibility = ViewStates.Visible;
        _searchRow.Alpha = 0f;
        _searchRow.TranslationY = -Rows.Dp(RequireContext(), 8f);
        _searchRow.Animate()?.Alpha(1f).TranslationY(0f).SetDuration(180).Start();
        _filterText.RequestFocus();
        if (Context?.GetSystemService(Context.InputMethodService) is InputMethodManager imm)
        {
            imm.ShowSoftInput(_filterText, ShowFlags.Implicit);
        }
    }

    private void CollapseSearch()
    {
        if (!IsAdded || View is null)
        {
            return;
        }
        // 收起即清空：普通筛选词与已执行的递归搜索都归零，避免搜索栏隐藏后列表仍停在旧结果。
        var hadSearch = _searchQuery is not null;
        _searchExpanded = false;
        _searchQuery = null;
        _searchResult = null;
        _filter = "";
        _filterText.Text = "";
        if (Context?.GetSystemService(Context.InputMethodService) is InputMethodManager imm && _filterText.WindowToken is { } token)
        {
            imm.HideSoftInputFromWindow(token, HideSoftInputFlags.None);
        }
        _filterText.ClearFocus();
        _searchRow.Animate()?.Alpha(0f).TranslationY(-Rows.Dp(RequireContext(), 8f)).SetDuration(140).Start();
        _searchRow.PostDelayed(() =>
        {
            // 动画结束后再收起；期间若用户重新展开则保持可见，避免闪烁。
            if (!_searchExpanded)
            {
                _searchRow.Visibility = ViewStates.Gone;
                _searchRow.Alpha = 1f;
                _searchRow.TranslationY = 0f;
            }
        }, 160);
        if (hadSearch)
        {
            _ = LoadAsync();
        }
        else
        {
            Show();
        }
    }

    private async Task SearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || _provider is null)
        {
            return;
        }
        var fs = _provider.FileSystem;
        var start = _dir;
        await Busy(async () =>
        {
            var r = await Task.Run(() => FileListing.Search(fs, start, query));
            _searchQuery = query.Trim();
            _searchResult = r;
            _selected.Clear();
            return Rows.Fmt(RequireContext(), r.Truncated ? Resource.String.files_search_truncated : Resource.String.files_search_result, r.Matches.Count, r.Visited);
        });
        Show();
    }

    private void AskName(int title, int hint, string initial, Action<string> onOk)
    {
        var ctx = RequireContext();
        var content = LayoutInflater.From(ctx)!.Inflate(Resource.Layout.dialog_text_input, null)!;
        var layout = content.FindViewById<TextInputLayout>(Resource.Id.dialog_text_input_layout)!;
        layout.Hint = GetString(hint);
        var input = content.FindViewById<TextInputEditText>(Resource.Id.dialog_text_input)!;
        input.Text = initial;
        input.SetSelection(initial.Length);

        new MaterialAlertDialogBuilder(ctx)
            .SetTitle(title)!
            .SetView(content)!
            .SetPositiveButton(Android.Resource.String.Ok, (_, _) =>
            {
                var text = input.Text ?? "";
                if (text.Length > 0)
                {
                    onOk(text);
                }
            })!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .Show();
    }

    private void StartImport()
    {
        if (!EnsureProvider())
        {
            ShowStatus(GetString(Resource.String.files_no_workspace));
            return;
        }
#pragma warning disable CS0618
        StartActivityForResult(DocumentPicker.OpenIntent(null, multiple: true), RequestImport);
#pragma warning restore CS0618
    }

    [Obsolete("Uses Fragment activity result for SAF import/export parity")]
    public override async void OnActivityResult(int requestCode, int resultCode, Intent? data)
    {
#pragma warning disable CS0618
        base.OnActivityResult(requestCode, resultCode, data);
#pragma warning restore CS0618
        if (resultCode != (int)Android.App.Result.Ok || !EnsureProvider())
        {
            return;
        }

        if (requestCode == RequestEditor)
        {
            if (data?.GetStringExtra(WorkspaceEditorActivity.ExtraSavedPath) is { Length: > 0 } savedRel)
            {
                ShowStatus(Rows.Fmt(RequireContext(), Resource.String.files_saved, $"{GuestRoot}/{savedRel}"));
            }
            await LoadAsync();
            return;
        }

        var fs = _provider!.FileSystem;
        var resolver = RequireContext().ContentResolver!;
        try
        {
            if (requestCode == RequestImport)
            {
                var done = new List<string>();
                foreach (var uri in DocumentPicker.ResultUris(data))
                {
                    var name = DocumentPicker.DisplayName(resolver, uri);
                    await using var input = resolver.OpenInputStream(uri) ?? throw new IOException("cannot open " + name);
                    done.Add(await fs.ImportAsync(_dir, name, input));
                }
                ShowStatus($"{GetString(Resource.String.files_imported)} {done.Count}: {string.Join(", ", done)}");
                await LoadAsync();
            }
            else if (requestCode == RequestExport && _pendingExport is { } rel && data?.Data is { } target)
            {
                _pendingExport = null;
                await using var input = fs.OpenRead(rel);
                await using var output = resolver.OpenOutputStream(target, "wt") ?? throw new IOException("cannot open export target");
                await input.CopyToAsync(output);
                ShowStatus($"{GetString(Resource.String.files_exported)} {rel}");
            }
        }
        catch (WorkspaceException e)
        {
            ShowError(e);
        }
        catch (Exception e) when (e is IOException or Java.Lang.Exception)
        {
            Log.Warn("DshMobile", $"files: {e.GetType().Name}: {e.Message}");
            ShowStatus($"{e.GetType().Name}: {e.Message}");
        }
    }

    private void ShowStatus(string? text)
    {
        if (!IsAdded)
        {
            return;
        }
        _status.Text = text;
        _status.Visibility = string.IsNullOrEmpty(text) ? ViewStates.Gone : ViewStates.Visible;
    }

    private void ShowError(WorkspaceException e)
    {
        ShowStatus($"{e.Code}: {e.Message}");
        Log.Info("DshMobile", $"files {e.Code}: {e.Message}");
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F1} GB",
    };

    private sealed class RowHolder(View view) : RecyclerView.ViewHolder(view)
    {
        public View Root { get; } = view;
        public ImageView Icon { get; } = view.FindViewById<ImageView>(Resource.Id.workspace_entry_icon)!;
        public TextView Title { get; } = view.FindViewById<TextView>(Resource.Id.workspace_entry_title)!;
        public TextView Subtitle { get; } = view.FindViewById<TextView>(Resource.Id.workspace_entry_subtitle)!;
        public View Check { get; } = view.FindViewById(Resource.Id.workspace_entry_check)!;
        public View Lock { get; } = view.FindViewById(Resource.Id.workspace_entry_lock)!;
        public MaterialButton MoreBtn { get; } = view.FindViewById<MaterialButton>(Resource.Id.workspace_entry_more)!;
        public WorkspaceEntry? Entry { get; set; }
    }

    /// <summary>全 XML 工作区列表适配器（<c>item_workspace_entry.xml</c>），零运行期动态创建视图。</summary>
    private sealed class EntryAdapter(FilesFragment owner) : RecyclerView.Adapter
    {
        private List<(WorkspaceEntry Entry, bool Selected, bool InSelectionMode)> _rows = [];
        private bool _inSearch;

        public IEnumerable<WorkspaceEntry> Items => _rows.Select(r => r.Entry);

        public override int ItemCount => _rows.Count;

        public void Update(IReadOnlyList<WorkspaceEntry> entries, HashSet<string> selected, bool inSearch)
        {
            var inSelectionMode = selected.Count > 0;
            var next = entries.Select(e => (e, selected.Contains(e.RelativePath), inSelectionMode)).ToList();
            var old = _rows;
            var searchChanged = inSearch != _inSearch;
            _rows = next;
            _inSearch = inSearch;
            if (searchChanged || old.Count > DiffLimit || next.Count > DiffLimit)
            {
                NotifyDataSetChanged();
                return;
            }
            DiffUtil.CalculateDiff(new Diff(old, next), false).DispatchUpdatesTo(this);
        }

        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
        {
            var view = LayoutInflater.From(parent.Context)!.Inflate(Resource.Layout.item_workspace_entry, parent, false)!;
            var holder = new RowHolder(view);
            view.Click += (_, _) =>
            {
                if (holder.Entry is { } e)
                {
                    owner.OnClick(e);
                }
            };
            view.LongClick += (_, a) =>
            {
                a.Handled = holder.Entry is not null;
                if (holder.Entry is { } e)
                {
                    owner.OnLongClick(e);
                }
            };
            holder.MoreBtn.Click += (_, _) =>
            {
                if (holder.Entry is { } e)
                {
                    owner.ShowActions(e);
                }
            };
            return holder;
        }

        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
        {
            var h = (RowHolder)holder;
            var (e, selected, inSelectionMode) = _rows[position];
            h.Entry = e;
            h.Title.Text = e.Name;
            var where = _inSearch ? WhereOf(e.RelativePath) : null;

            string? subtitle = null;
            switch (e.Kind)
            {
                case WorkspaceEntryKind.Directory:
                    h.Icon.SetImageResource(Resource.Drawable.ic_folder);
                    subtitle = where;
                    break;
                case WorkspaceEntryKind.Symlink:
                    h.Icon.SetImageResource(Resource.Drawable.ic_link);
                    subtitle = "→ " + (e.LinkTarget ?? "?") + (where is null ? "" : " · " + where);
                    break;
                default:
                    h.Icon.SetImageResource(WorkspaceProvider.FormatOf(e.Name) is not null
                        ? Resource.Drawable.ic_storage
                        : TextDocument.KindOf(e.Name) == TextKind.Plain
                            ? Resource.Drawable.ic_file
                            : Resource.Drawable.ic_code);
                    subtitle = FormatSize(e.Size) + " · " + e.Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)
                        + (where is null ? "" : " · " + where);
                    break;
            }

            if (string.IsNullOrEmpty(subtitle))
            {
                h.Subtitle.Visibility = ViewStates.Gone;
                h.Subtitle.Text = string.Empty;
            }
            else
            {
                h.Subtitle.Visibility = ViewStates.Visible;
                h.Subtitle.Text = subtitle;
            }

            h.Check.Visibility = selected ? ViewStates.Visible : ViewStates.Gone;
            h.Lock.Visibility = !selected && e.Protected ? ViewStates.Visible : ViewStates.Gone;
            h.MoreBtn.Visibility = !selected && !e.Protected && !inSelectionMode ? ViewStates.Visible : ViewStates.Gone;

            // UI-S5 无框通栏行：选中态用柔和 colorSecondaryContainer 铺底，未选中保持透明，
            // 不再为每行绘制独立描边卡片，首屏可多显示约 30% 条目。
            h.Root.SetBackgroundColor(selected
                ? new global::Android.Graphics.Color(Rows.Color(h.Root, Resource.Attribute.colorSecondaryContainer))
                : global::Android.Graphics.Color.Transparent);
        }

        private static string WhereOf(string path)
        {
            var i = path.LastIndexOf('/');
            return GuestRoot + (i < 0 ? "" : "/" + path[..i]);
        }

        private sealed class Diff(
            List<(WorkspaceEntry Entry, bool Selected, bool InSelectionMode)> old,
            List<(WorkspaceEntry Entry, bool Selected, bool InSelectionMode)> next) : DiffUtil.Callback
        {
            public override int OldListSize => old.Count;
            public override int NewListSize => next.Count;

            public override bool AreItemsTheSame(int oldItemPosition, int newItemPosition) =>
                old[oldItemPosition].Entry.RelativePath == next[newItemPosition].Entry.RelativePath;

            public override bool AreContentsTheSame(int oldItemPosition, int newItemPosition) =>
                old[oldItemPosition] == next[newItemPosition];
        }
    }

    private sealed class FabScrollListener(ExtendedFloatingActionButton fab) : RecyclerView.OnScrollListener
    {
        public override void OnScrolled(RecyclerView recyclerView, int dx, int dy)
        {
            base.OnScrolled(recyclerView, dx, dy);
            if (dy > 6 && fab.Extended)
            {
                fab.Shrink();
            }
            else if (dy < -6 && !fab.Extended)
            {
                fab.Extend();
            }
            else if (!recyclerView.CanScrollVertically(-1) && !fab.Extended)
            {
                fab.Extend();
            }
        }
    }
}
