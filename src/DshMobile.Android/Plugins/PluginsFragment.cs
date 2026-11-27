using Android.Content;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Presentation;
using DshMobile.App.Bridge;
using DshMobile.App.Ui;
using DshMobile.Runtime.Hosting;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Chip;
using Google.Android.Material.Dialog;
using Google.Android.Material.ProgressIndicator;
using Google.Android.Material.Snackbar;
using Google.Android.Material.TextField;
using Fragment = AndroidX.Fragment.App.Fragment;
using Runnable = Java.Lang.Runnable;

namespace DshMobile.App.Plugins;

/// <summary>
/// PR-07 原生插件工作台 Fragment（N-07-02 / ADR-0034）：
/// 100% 绑定 XML 布局，提供「已安装筛选 + 远端发现搜索」双模式能力、详情页跳转与按需完整 WebUI 兼容入口。
/// </summary>
public sealed class PluginsFragment : Fragment
{
    private const int CatalogSearchDebounceMs = 350;

    private BridgeHost _bridge = null!;
    private PluginsViewModel _vm = null!;
    private PluginsAdapter _installedAdapter = null!;
    private PluginCatalogAdapter _catalogAdapter = null!;

    private MaterialToolbar _toolbar = null!;
    private LinearProgressIndicator _progress = null!;
    private View _hostBanner = null!;
    private View _restartBanner = null!;
    private MaterialButton _startDshButton = null!;
    private MaterialButton _restartNowButton = null!;
    private View _modeToggle = null!;
    private MaterialButton _installedTabButton = null!;
    private MaterialButton _discoverTabButton = null!;
    private View _searchLayout = null!;
    private TextInputEditText _searchInput = null!;
    private View _filterScroll = null!;
    private ChipGroup _filterChips = null!;
    private View _discoverChipsScroll = null!;
    private Chip _discoverOfficialChip = null!;
    private Chip _discoverDshChip = null!;
    private Chip _discoverCordisChip = null!;
    private Chip _discoverMcpChip = null!;
    private View _summaryBar = null!;
    private TextView _summaryText = null!;
    private MaterialButton _retrySearchButton = null!;
    private TextView _emptyView = null!;
    private RecyclerView _installedList = null!;
    private RecyclerView _catalogList = null!;

    private Handler? _searchDebounceHandler;
    private Runnable? _pendingCatalogSearchRunnable;
    private CancellationTokenSource? _viewLifecycleCts;

    private PluginTabMode? _lastRenderedTab;
    private bool _suppressSearchTextChanged;
    private bool _suppressFilterChipChange;
    private int _renderPosted;
    private string? _lastShownStatusKey;
    private string? _lastShownFailure;
    private bool _returnedFromDetail;

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        var view = inflater.Inflate(Resource.Layout.fragment_plugins, container, false)!;
        _bridge = DshMobileApplication.Current.Bridge;
        _vm = _bridge.Agent.Plugins;
        _searchDebounceHandler = new Handler(Looper.MainLooper!);
        _viewLifecycleCts = new CancellationTokenSource();
        _lastRenderedTab = null;

        _toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.plugins_toolbar)!;
        _toolbar.NavigationClick += (_, _) => Activity?.Finish();
        _toolbar.MenuItemClick += (_, e) => OnToolbarMenuItemClicked(e.Item?.ItemId ?? 0);

        _progress = view.FindViewById<LinearProgressIndicator>(Resource.Id.plugins_progress)!;
        _hostBanner = view.FindViewById(Resource.Id.plugins_host_banner)!;
        _restartBanner = view.FindViewById(Resource.Id.plugins_restart_banner)!;
        _startDshButton = view.FindViewById<MaterialButton>(Resource.Id.plugins_start_dsh_btn)!;
        _restartNowButton = view.FindViewById<MaterialButton>(Resource.Id.plugins_restart_now_btn)!;

        _modeToggle = view.FindViewById(Resource.Id.plugins_mode_toggle)!;
        _installedTabButton = view.FindViewById<MaterialButton>(Resource.Id.plugins_tab_installed)!;
        _discoverTabButton = view.FindViewById<MaterialButton>(Resource.Id.plugins_tab_discover)!;

        _searchLayout = view.FindViewById(Resource.Id.plugins_search_layout)!;
        _searchInput = _searchLayout.FindViewById<TextInputEditText>(Resource.Id.search_input)!;

        _filterScroll = view.FindViewById(Resource.Id.plugins_filter_scroll)!;
        _filterChips = view.FindViewById<ChipGroup>(Resource.Id.plugins_filter_chips)!;

        _discoverChipsScroll = view.FindViewById(Resource.Id.plugins_discover_chips_scroll)!;
        _discoverOfficialChip = view.FindViewById<Chip>(Resource.Id.plugins_discover_chip_official)!;
        _discoverDshChip = view.FindViewById<Chip>(Resource.Id.plugins_discover_chip_dsh)!;
        _discoverCordisChip = view.FindViewById<Chip>(Resource.Id.plugins_discover_chip_cordis)!;
        _discoverMcpChip = view.FindViewById<Chip>(Resource.Id.plugins_discover_chip_mcp)!;

        _summaryBar = view.FindViewById(Resource.Id.plugins_summary_bar)!;
        _summaryText = view.FindViewById<TextView>(Resource.Id.plugins_summary_text)!;
        _retrySearchButton = view.FindViewById<MaterialButton>(Resource.Id.plugins_retry_search_btn)!;
        _emptyView = view.FindViewById<TextView>(Resource.Id.plugins_empty)!;

        _installedList = view.FindViewById<RecyclerView>(Resource.Id.plugins_list)!;
        _installedList.SetLayoutManager(new LinearLayoutManager(RequireContext()));
        _installedAdapter = new PluginsAdapter(OpenInstalledPluginDetail, ConfirmTogglePlugin, ConfirmUninstallPlugin);
        _installedList.SetAdapter(_installedAdapter);

        _catalogList = view.FindViewById<RecyclerView>(Resource.Id.plugins_catalog_list)!;
        _catalogList.SetLayoutManager(new LinearLayoutManager(RequireContext()));
        _catalogAdapter = new PluginCatalogAdapter(OpenCatalogPluginDetail, ConfirmInstallCatalogPlugin);
        _catalogList.SetAdapter(_catalogAdapter);

        _startDshButton.Click += (_, _) => _ = _bridge.RunStartAsync();
        _restartNowButton.Click += (_, _) => _ = _vm.RestartDshAsync();
        _retrySearchButton.Click += (_, _) => TriggerCatalogSearchImmediate(_vm.State.CatalogQuery);

        _installedTabButton.Click += (_, _) => SwitchToInstalledTab();
        _discoverTabButton.Click += (_, _) => SwitchToDiscoverTab();

        _searchInput.TextChanged += OnSearchTextChanged;
        _searchInput.EditorAction += OnSearchEditorAction;
        _searchInput.KeyPress += OnSearchKeyPress;

        _filterChips.CheckedStateChange += OnFilterChipsChanged;

        BindDiscoverQuickChip(_discoverOfficialChip, Resource.String.plugins_discover_chip_official);
        BindDiscoverQuickChip(_discoverDshChip, Resource.String.plugins_discover_chip_dsh);
        BindDiscoverQuickChip(_discoverCordisChip, Resource.String.plugins_discover_chip_cordis);
        BindDiscoverQuickChip(_discoverMcpChip, Resource.String.plugins_discover_chip_mcp);

        Edges.ClampMaxWidth(_installedList, _catalogList, _filterScroll, _discoverChipsScroll, _summaryBar);
        Edges.ClampMaxWidthByMargin(_modeToggle, _searchLayout, _restartBanner);

        return view;
    }

    public override void OnStart()
    {
        base.OnStart();
        _vm.StateChanged += OnStateChanged;
        _bridge.Changed += OnBridgeChanged;
        var currentState = _vm.State;
        Render(currentState);
        _ = RefreshWhenReadyAsync();
        if (currentState.ActiveTab == PluginTabMode.Discover
            && currentState.CatalogEntries.Count == 0
            && !currentState.IsSearchingCatalog
            && currentState.CanSearchCatalog)
        {
            TriggerCatalogSearchImmediate(currentState.CatalogQuery);
        }
    }

    public override void OnResume()
    {
        base.OnResume();
        if (_returnedFromDetail)
        {
            _returnedFromDetail = false;
            ScheduleRender();
            _ = RefreshWhenReadyAsync();
        }
    }

    public override void OnStop()
    {
        _vm.StateChanged -= OnStateChanged;
        _bridge.Changed -= OnBridgeChanged;
        base.OnStop();
    }

    public override void OnDestroyView()
    {
        CancelPendingCatalogSearchDebounce();
        _searchDebounceHandler = null;
        if (_viewLifecycleCts is { } cts)
        {
            _viewLifecycleCts = null;
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                cts.Dispose();
            }
        }
        base.OnDestroyView();
    }

    private void OnToolbarMenuItemClicked(int itemId)
    {
        if (itemId == Resource.Id.plugins_action_install)
        {
            ShowInstallDialog();
            return;
        }

        if (itemId == Resource.Id.plugins_action_refresh)
        {
            if (_vm.State.ActiveTab == PluginTabMode.Discover)
            {
                TriggerCatalogSearchImmediate(_vm.State.CatalogQuery);
                if (_bridge.Runtime.Host?.State == DshHostState.Ready)
                {
                    _ = RefreshWhenReadyAsync();
                }
            }
            else
            {
                _ = RefreshWhenReadyAsync();
            }
            return;
        }

        if (itemId == Resource.Id.plugins_action_webui_compat)
        {
            var context = RequireContext();
            var desiredEnabled = !UiPrefs.FullWebUiCompatEnabled(context);
            PluginBrowserDialogs.ConfirmToggleFullWebUiCompat(
                context,
                View,
                _bridge,
                desiredEnabled,
                restartHostAsync: () => _vm.RestartDshAsync(),
                onPreferenceChanged: ScheduleRender);
            return;
        }

        if (itemId == Resource.Id.plugins_action_open_webui)
        {
            PluginBrowserDialogs.ConfirmAndOpenLocalWebUi(RequireContext(), View, _bridge);
        }
    }

    private void OpenInstalledPluginDetail(PluginEntryView entry)
    {
        var target = !string.IsNullOrWhiteSpace(entry.EntryId) ? entry.EntryId : entry.PackageName;
        var title = PluginDisplayFormatter.Format(entry).ShortTitle;
        _returnedFromDetail = true;
        StartActivity(PluginDetailActivity.CreateInstalledIntent(RequireContext(), target, title));
    }

    private void OpenCatalogPluginDetail(PluginCatalogEntryView entry)
    {
        var title = PluginDisplayFormatter.FormatCatalog(entry).ShortTitle;
        _returnedFromDetail = true;
        StartActivity(PluginDetailActivity.CreateCatalogIntent(RequireContext(), entry.PackageName, title));
    }

    private void SwitchToInstalledTab()
    {
        CancelPendingCatalogSearchDebounce();
        _vm.SetActiveTab(PluginTabMode.Installed);
    }

    private void SwitchToDiscoverTab()
    {
        _vm.SetActiveTab(PluginTabMode.Discover);
        var state = _vm.State;
        if (state.CatalogEntries.Count == 0 && !state.IsSearchingCatalog && state.CanSearchCatalog)
        {
            TriggerCatalogSearchImmediate(state.CatalogQuery);
        }
    }

    private void BindDiscoverQuickChip(Chip chip, int keywordStringRes)
    {
        chip.Click += (_, _) =>
        {
            var keyword = GetString(keywordStringRes) ?? string.Empty;
            ApplyDiscoverQuickQuery(keyword);
        };
    }

    private void ApplyDiscoverQuickQuery(string keyword)
    {
        if (_vm.State.ActiveTab != PluginTabMode.Discover)
        {
            _vm.SetActiveTab(PluginTabMode.Discover);
        }
        SyncSearchInputSilently(keyword, Resource.String.plugins_search_discover_hint);
        _vm.SetCatalogQuery(keyword);
        HideSoftKeyboard();
        TriggerCatalogSearchImmediate(keyword);
    }

    private void OnFilterChipsChanged(object? sender, ChipGroup.CheckedStateChangeEventArgs e)
    {
        if (_suppressFilterChipChange)
        {
            return;
        }
        var filterMode = _filterChips.CheckedChipId switch
        {
            Resource.Id.plugins_filter_manageable => PluginFilterMode.Manageable,
            Resource.Id.plugins_filter_enabled => PluginFilterMode.Enabled,
            Resource.Id.plugins_filter_protected => PluginFilterMode.Protected,
            Resource.Id.plugins_filter_issues => PluginFilterMode.Issues,
            _ => PluginFilterMode.All,
        };
        _vm.SetFilterMode(filterMode);
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressSearchTextChanged)
        {
            return;
        }
        var text = e.Text?.ToString() ?? string.Empty;
        if (_vm.State.ActiveTab == PluginTabMode.Discover)
        {
            _vm.SetCatalogQuery(text);
            ScheduleDebouncedCatalogSearch(text);
        }
        else
        {
            _vm.SetSearchQuery(text);
        }
    }

    private void OnSearchEditorAction(object? sender, TextView.EditorActionEventArgs e)
    {
        if (e.ActionId is ImeAction.Search or ImeAction.Done or ImeAction.Go or ImeAction.Unspecified
            || e.Event?.KeyCode == Keycode.Enter)
        {
            e.Handled = true;
            CommitImmediateSearchFromInput();
        }
    }

    private void OnSearchKeyPress(object? sender, View.KeyEventArgs e)
    {
        if (e.KeyCode == Keycode.Enter)
        {
            e.Handled = true;
            if (e.Event?.Action == KeyEventActions.Up)
            {
                CommitImmediateSearchFromInput();
            }
        }
        else
        {
            e.Handled = false;
        }
    }

    private void CommitImmediateSearchFromInput()
    {
        HideSoftKeyboard();
        if (_vm.State.ActiveTab == PluginTabMode.Discover)
        {
            var query = _searchInput.Text ?? string.Empty;
            _vm.SetCatalogQuery(query);
            TriggerCatalogSearchImmediate(query);
        }
    }

    private void HideSoftKeyboard()
    {
        if (Context?.GetSystemService(Context.InputMethodService) is InputMethodManager imm
            && _searchInput.WindowToken is { } token)
        {
            imm.HideSoftInputFromWindow(token, HideSoftInputFlags.None);
        }
        _searchInput.ClearFocus();
    }

    private void ScheduleDebouncedCatalogSearch(string? query)
    {
        CancelPendingCatalogSearchDebounce();
        if (_searchDebounceHandler is null || !_vm.State.CanSearchCatalog)
        {
            return;
        }
        var pending = new Runnable(() => _ = ExecuteCatalogSearchAsync(query));
        _pendingCatalogSearchRunnable = pending;
        _searchDebounceHandler.PostDelayed(pending, CatalogSearchDebounceMs);
    }

    private void TriggerCatalogSearchImmediate(string? query)
    {
        CancelPendingCatalogSearchDebounce();
        _ = ExecuteCatalogSearchAsync(query);
    }

    private void CancelPendingCatalogSearchDebounce()
    {
        if (_pendingCatalogSearchRunnable is { } runnable)
        {
            _searchDebounceHandler?.RemoveCallbacks(runnable);
            _pendingCatalogSearchRunnable = null;
        }
    }

    private async Task ExecuteCatalogSearchAsync(string? query)
    {
        var ct = _viewLifecycleCts?.Token ?? CancellationToken.None;
        if (ct.IsCancellationRequested)
        {
            return;
        }
        try
        {
            await _vm.SearchCatalogAsync(query, ct).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnStateChanged(PluginsUiState _) => ScheduleRender();

    private void OnBridgeChanged(object? sender, EventArgs e)
    {
        // 使用 TotalInstalledCount 判断是否已加载过本地清单，避免本地筛选无结果时重复触发刷新。
        if (_bridge.Runtime.Host?.State == DshHostState.Ready &&
            _vm.State.TotalInstalledCount == 0 &&
            !_vm.State.IsLoading &&
            !_vm.State.IsRestarting)
        {
            _ = RefreshWhenReadyAsync();
        }
        ScheduleRender();
    }

    private void ScheduleRender()
    {
        if (Interlocked.Exchange(ref _renderPosted, 1) == 0)
        {
            Activity?.RunOnUiThread(() =>
            {
                Volatile.Write(ref _renderPosted, 0);
                if (IsAdded && View is not null)
                {
                    Render(_vm.State);
                }
            });
        }
    }

    private void Render(PluginsUiState state)
    {
        var context = RequireContext();
        var hostReady = _bridge.Runtime.Host?.State == DshHostState.Ready;
        var isDiscover = state.ActiveTab == PluginTabMode.Discover;
        var busy = state.IsLoading || state.IsMutating || state.IsRestarting || _bridge.IsBusy;
        var showProgress = busy || (isDiscover && state.IsSearchingCatalog);

        _progress.Visibility = showProgress ? ViewStates.Visible : ViewStates.Gone;
        _hostBanner.Visibility = hostReady ? ViewStates.Gone : ViewStates.Visible;
        _restartBanner.Visibility = state.RequiresDshRestart ? ViewStates.Visible : ViewStates.Gone;

        _startDshButton.Enabled = !_bridge.IsBusy;
        _restartNowButton.Enabled = !busy;

        var installMenuItem = _toolbar.Menu?.FindItem(Resource.Id.plugins_action_install);
        installMenuItem?.SetEnabled(hostReady && !busy);
        var refreshMenuItem = _toolbar.Menu?.FindItem(Resource.Id.plugins_action_refresh);
        refreshMenuItem?.SetEnabled(isDiscover ? (!state.IsSearchingCatalog && state.CanSearchCatalog) : (hostReady && !busy));
        var webUiCompatMenuItem = _toolbar.Menu?.FindItem(Resource.Id.plugins_action_webui_compat);
        webUiCompatMenuItem?.SetTitle(UiPrefs.FullWebUiCompatEnabled(context)
            ? Resource.String.plugins_menu_webui_compat_on
            : Resource.String.plugins_menu_webui_compat_off);
        webUiCompatMenuItem?.SetEnabled(!busy);
        var openWebUiMenuItem = _toolbar.Menu?.FindItem(Resource.Id.plugins_action_open_webui);
        var webUiReady = _bridge.IsFullWebUiCompatActive && _bridge.TryGetLocalWebUiBrowserUri() is not null;
        openWebUiMenuItem?.SetVisible(webUiReady);
        openWebUiMenuItem?.SetEnabled(webUiReady && !busy);

        // 同步分段切换器选中态与已安装总数标签
        _installedTabButton.Checked = !isDiscover;
        _discoverTabButton.Checked = isDiscover;
        _installedTabButton.Text = state.TotalInstalledCount > 0
            ? Rows.Fmt(context, Resource.String.plugins_tab_installed_fmt, state.TotalInstalledCount)
            : GetString(Resource.String.plugins_tab_installed);

        // 切换 Tab 时静默同步搜索框提示词与内容，避免递归触发 TextChanged
        if (_lastRenderedTab != state.ActiveTab)
        {
            _lastRenderedTab = state.ActiveTab;
            SyncSearchInputSilently(
                isDiscover ? state.CatalogQuery : state.SearchQuery,
                isDiscover ? Resource.String.plugins_search_discover_hint : Resource.String.plugins_search_installed_hint);
        }

        // 切换上下文控件可见性
        _filterScroll.Visibility = isDiscover ? ViewStates.Gone : ViewStates.Visible;
        _discoverChipsScroll.Visibility = isDiscover ? ViewStates.Visible : ViewStates.Gone;
        _installedList.Visibility = isDiscover ? ViewStates.Gone : ViewStates.Visible;
        _catalogList.Visibility = isDiscover ? ViewStates.Visible : ViewStates.Gone;

        if (isDiscover)
        {
            RenderDiscoverSection(context, state, hostReady, busy);
        }
        else
        {
            RenderInstalledSection(context, state, hostReady, busy);
        }

        RenderFeedbackSnackbars(context, state);
    }

    private void RenderInstalledSection(Context context, PluginsUiState state, bool hostReady, bool busy)
    {
        SyncFilterChipSelection(state.FilterMode);
        _installedAdapter.Submit(state.Entries, busy, _bridge.IsFullWebUiCompatActive);

        _retrySearchButton.Visibility = ViewStates.Gone;
        if (state.TotalInstalledCount > 0)
        {
            _summaryBar.Visibility = ViewStates.Visible;
            var isFiltered = state.FilterMode != PluginFilterMode.All || !string.IsNullOrWhiteSpace(state.SearchQuery);
            _summaryText.Text = isFiltered
                ? Rows.Fmt(context, Resource.String.plugins_installed_count_filtered_fmt, state.Entries.Count, state.TotalInstalledCount)
                : Rows.Fmt(context, Resource.String.plugins_installed_count_all_fmt, state.TotalInstalledCount);
        }
        else
        {
            _summaryBar.Visibility = ViewStates.Gone;
        }

        if (state.Entries.Count == 0 && !state.IsLoading)
        {
            _emptyView.Visibility = ViewStates.Visible;
            if (hostReady && !state.CanListPlugins && state.FailureCode == nameof(DshFailureCode.Unsupported))
            {
                _emptyView.SetText(Resource.String.plugins_unsupported);
            }
            else if (state.TotalInstalledCount > 0)
            {
                _emptyView.SetText(Resource.String.plugins_installed_empty_filtered);
            }
            else
            {
                _emptyView.SetText(Resource.String.plugins_empty);
            }
        }
        else
        {
            _emptyView.Visibility = ViewStates.Gone;
        }
    }

    private void RenderDiscoverSection(Context context, PluginsUiState state, bool hostReady, bool busy)
    {
        SyncDiscoverQuickChips(state.CatalogQuery);
        _catalogAdapter.Submit(state.CatalogEntries, hostReady, busy);

        if (state.IsSearchingCatalog)
        {
            _summaryBar.Visibility = ViewStates.Visible;
            _summaryText.SetText(Resource.String.plugins_discover_searching);
            _retrySearchButton.Visibility = ViewStates.Gone;
        }
        else if (state.CatalogFailureCode is { } failureCode)
        {
            _summaryBar.Visibility = ViewStates.Visible;
            if (!state.CanSearchCatalog && failureCode == DshFailureCode.Unsupported)
            {
                _summaryText.SetText(Resource.String.plugins_discover_unavailable);
                _retrySearchButton.Visibility = ViewStates.Gone;
            }
            else
            {
                var detail = string.IsNullOrWhiteSpace(state.CatalogDiagnosticSummary)
                    ? failureCode.ToString()
                    : $"{failureCode}: {state.CatalogDiagnosticSummary}";
                _summaryText.Text = Rows.Fmt(context, Resource.String.plugins_discover_error_fmt, detail);
                _retrySearchButton.Visibility = ViewStates.Visible;
                _retrySearchButton.Enabled = !state.IsSearchingCatalog;
            }
        }
        else if (state.CatalogEntries.Count > 0)
        {
            _summaryBar.Visibility = ViewStates.Visible;
            _retrySearchButton.Visibility = ViewStates.Gone;
            _summaryText.Text = state.CatalogTotalCount > state.CatalogEntries.Count
                ? Rows.Fmt(context, Resource.String.plugins_discover_result_partial_fmt, state.CatalogEntries.Count, state.CatalogTotalCount)
                : Rows.Fmt(context, Resource.String.plugins_discover_result_fmt, state.CatalogEntries.Count);
        }
        else
        {
            _summaryBar.Visibility = ViewStates.Gone;
            _retrySearchButton.Visibility = ViewStates.Gone;
        }

        if (state.CatalogEntries.Count == 0 && !state.IsSearchingCatalog)
        {
            _emptyView.Visibility = ViewStates.Visible;
            _emptyView.SetText(!state.CanSearchCatalog
                ? Resource.String.plugins_discover_unavailable
                : Resource.String.plugins_discover_empty);
        }
        else
        {
            _emptyView.Visibility = ViewStates.Gone;
        }
    }

    private void RenderFeedbackSnackbars(Context context, PluginsUiState state)
    {
        if (View is not { } root)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(state.FailureCode))
        {
            var failureKey = $"{state.FailureCode}:{state.DiagnosticSummary}";
            if (!string.Equals(failureKey, _lastShownFailure, StringComparison.Ordinal))
            {
                _lastShownFailure = failureKey;
                var detail = string.IsNullOrWhiteSpace(state.DiagnosticSummary)
                    ? state.FailureCode
                    : $"{state.FailureCode}: {state.DiagnosticSummary}";
                Snackbar.Make(root, Rows.Fmt(context, Resource.String.chat_error_banner, detail), Snackbar.LengthLong).Show();
            }
        }
        else
        {
            _lastShownFailure = null;
        }

        if (state.StatusKey is not null && !string.Equals(state.StatusKey, _lastShownStatusKey, StringComparison.Ordinal))
        {
            _lastShownStatusKey = state.StatusKey;
            var msgRes = state.StatusKey switch
            {
                "plugins.status.installed" => Resource.String.plugins_status_installed,
                "plugins.status.uninstalled" => Resource.String.plugins_status_uninstalled,
                "plugins.status.enabled" => Resource.String.plugins_status_enabled,
                "plugins.status.disabled" => Resource.String.plugins_status_disabled_toast,
                _ => Resource.String.plugins_status_restarted,
            };
            Snackbar.Make(root, msgRes, Snackbar.LengthShort).Show();
        }
        else if (state.StatusKey is null)
        {
            _lastShownStatusKey = null;
        }
    }

    private void SyncSearchInputSilently(string targetText, int hintStringRes)
    {
        _suppressSearchTextChanged = true;
        try
        {
            _searchInput.Hint = GetString(hintStringRes);
            if (!string.Equals(_searchInput.Text ?? string.Empty, targetText, StringComparison.Ordinal))
            {
                _searchInput.Text = targetText;
                _searchInput.SetSelection(targetText.Length);
            }
        }
        finally
        {
            _suppressSearchTextChanged = false;
        }
    }

    private void SyncFilterChipSelection(PluginFilterMode filterMode)
    {
        var targetChipId = filterMode switch
        {
            PluginFilterMode.Manageable => Resource.Id.plugins_filter_manageable,
            PluginFilterMode.Enabled => Resource.Id.plugins_filter_enabled,
            PluginFilterMode.Protected => Resource.Id.plugins_filter_protected,
            PluginFilterMode.Issues => Resource.Id.plugins_filter_issues,
            _ => Resource.Id.plugins_filter_all,
        };
        if (_filterChips.CheckedChipId == targetChipId)
        {
            return;
        }
        _suppressFilterChipChange = true;
        try
        {
            _filterChips.Check(targetChipId);
        }
        finally
        {
            _suppressFilterChipChange = false;
        }
    }

    private void SyncDiscoverQuickChips(string catalogQuery)
    {
        var trimmed = catalogQuery.Trim();
        SyncSingleDiscoverChip(_discoverOfficialChip, Resource.String.plugins_discover_chip_official, trimmed);
        SyncSingleDiscoverChip(_discoverDshChip, Resource.String.plugins_discover_chip_dsh, trimmed);
        SyncSingleDiscoverChip(_discoverCordisChip, Resource.String.plugins_discover_chip_cordis, trimmed);
        SyncSingleDiscoverChip(_discoverMcpChip, Resource.String.plugins_discover_chip_mcp, trimmed);
    }

    private void SyncSingleDiscoverChip(Chip chip, int keywordRes, string currentQuery)
    {
        var keyword = GetString(keywordRes) ?? string.Empty;
        var shouldCheck = string.Equals(currentQuery, keyword, StringComparison.OrdinalIgnoreCase);
        if (chip.Checked != shouldCheck)
        {
            chip.Checked = shouldCheck;
        }
    }

    private async Task RefreshWhenReadyAsync()
    {
        if (_bridge.Runtime.Host?.State != DshHostState.Ready)
        {
            return;
        }
        try
        {
            await _vm.RefreshAsync(includeCliPackages: true).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException)
        {
        }
    }

    private void ShowInstallDialog()
    {
        var ctx = RequireContext();
        var content = LayoutInflater.From(ctx)!.Inflate(Resource.Layout.dialog_plugin_install, null)!;
        var input = content.FindViewById<TextInputEditText>(Resource.Id.plugin_dialog_spec_input)!;

        new MaterialAlertDialogBuilder(ctx)
            .SetTitle(Resource.String.plugins_install_dialog_title)!
            .SetView(content)!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(Resource.String.plugins_action_install, (_, _) =>
            {
                var spec = input.Text?.Trim() ?? string.Empty;
                if (!PluginPackageValidator.IsValidInstallSpec(spec))
                {
                    if (View is { } root)
                    {
                        Snackbar.Make(root, Resource.String.plugins_invalid_spec, Snackbar.LengthLong).Show();
                    }
                    return;
                }
                ConfirmInstallPlugin(spec);
            })!
            .Show();
    }

    private void ConfirmInstallPlugin(string spec)
    {
        var ctx = RequireContext();
        new MaterialAlertDialogBuilder(ctx)
            .SetTitle(Resource.String.plugins_install_confirm_title)!
            .SetMessage(Rows.Fmt(ctx, Resource.String.plugins_install_confirm_msg, spec))!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(Resource.String.plugins_install_confirm_action, (_, _) =>
            {
                _ = _vm.InstallPluginAsync(spec);
            })!
            .Show();
    }

    private void ConfirmInstallCatalogPlugin(PluginCatalogEntryView item)
    {
        var ctx = RequireContext();
        if (_bridge.Runtime.Host?.State != DshHostState.Ready)
        {
            if (View is { } root)
            {
                Snackbar.Make(root, Resource.String.plugins_host_required, Snackbar.LengthLong).Show();
            }
            return;
        }

        var installSpec = item.RecommendedInstallSpec?.Trim() ?? string.Empty;
        if (!PluginPackageValidator.IsValidInstallSpec(installSpec))
        {
            if (View is { } root)
            {
                Snackbar.Make(root, Resource.String.plugins_invalid_spec, Snackbar.LengthLong).Show();
            }
            return;
        }

        new MaterialAlertDialogBuilder(ctx)
            .SetTitle(Resource.String.plugins_catalog_install_confirm_title)!
            .SetMessage(Rows.Fmt(ctx, Resource.String.plugins_catalog_install_confirm_msg, installSpec))!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(Resource.String.plugins_install_confirm_action, (_, _) =>
            {
                _ = _vm.InstallPluginAsync(installSpec);
            })!
            .Show();
    }

    private void ConfirmUninstallPlugin(PluginEntryView entry)
    {
        var ctx = RequireContext();
        var packageName = entry.PackageName;
        new MaterialAlertDialogBuilder(ctx)
            .SetTitle(Rows.Fmt(ctx, Resource.String.plugins_uninstall_confirm_title, packageName))!
            .SetMessage(Rows.Fmt(ctx, Resource.String.plugins_uninstall_confirm_msg, packageName))!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(Resource.String.plugins_uninstall_confirm_action, (_, _) =>
            {
                _ = _vm.UninstallPluginAsync(packageName);
            })!
            .Show();
    }

    private void ConfirmTogglePlugin(PluginEntryView entry, bool desiredEnabled)
    {
        var ctx = RequireContext();
        var actionLabel = GetString(desiredEnabled ? Resource.String.plugins_toggle_enable : Resource.String.plugins_toggle_disable)!;
        var shortTitle = PluginDisplayFormatter.Format(entry).ShortTitle;
        new MaterialAlertDialogBuilder(ctx)
            .SetTitle(Rows.Fmt(ctx, Resource.String.plugins_toggle_confirm_title, actionLabel, shortTitle))!
            .SetMessage(Resource.String.plugins_toggle_confirm_msg)!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(actionLabel, (_, _) =>
            {
                _ = _vm.SetPluginEnabledAsync(entry.EntryId, entry.ModuleName, desiredEnabled);
            })!
            .Show();
    }
}
