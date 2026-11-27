using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;
using DshMobile.Agent.Presentation;
using DshMobile.Agent.Rendering;
using DshMobile.App.Bridge;
using DshMobile.App.Chat;
using DshMobile.App.Ui;
using DshMobile.Runtime.Hosting;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.ProgressIndicator;
using Google.Android.Material.Snackbar;
using Google.Android.Material.TextField;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace DshMobile.App.Plugins;

/// <summary>
/// 原生插件详情、官方 <c>settings/*</c> 实时配置与页面扩展兼容引导 Fragment（ADR-0034）：
/// 100% 绑定 XML 布局，复用纯托管 Markdown 渲染器展示详情文档，提供受控官方配置表单与按需完整 WebUI 兼容入口。
/// </summary>
public sealed class PluginDetailFragment : Fragment
{
    private const string ArgMode = "extra_plugin_mode";
    private const string ArgTarget = "extra_plugin_target";
    private const string ArgTitle = "extra_plugin_title";

    private BridgeHost _bridge = null!;
    private PluginDetailViewModel _viewModel = null!;
    private PluginConfigAdapter _configAdapter = null!;

    private MaterialToolbar _toolbar = null!;
    private LinearProgressIndicator _progress = null!;
    private View _scrollContent = null!;
    private View _restartBanner = null!;
    private MaterialButton _restartNowButton = null!;

    private TextView _nameView = null!;
    private TextView _packageView = null!;
    private TextView _versionView = null!;
    private TextView _descView = null!;

    private TextView _runtimeBadge = null!;
    private TextView _protectedBadge = null!;
    private TextView _officialBadge = null!;
    private TextView _installedBadge = null!;
    private TextView _configurableBadge = null!;
    private TextView _webUiBadge = null!;
    private TextView _notBundleBadge = null!;
    private TextView _trimmedBadge = null!;
    private TextView _updateBadge = null!;

    private TextView _metaInfoView = null!;
    private View _linksRow = null!;
    private MaterialButton _homepageLinkButton = null!;
    private MaterialButton _repoLinkButton = null!;
    private MaterialButton _npmLinkButton = null!;

    private View _actionsRow = null!;
    private MaterialButton _installButton = null!;
    private MaterialButton _toggleButton = null!;
    private MaterialButton _uninstallButton = null!;
    private MaterialButton _upgradeButton = null!;
    private TextView _installHintView = null!;
    private TextView _updateHintView = null!;
    private TextView _feedbackView = null!;

    private View _webUiCard = null!;
    private MaterialButton _webUiActionButton = null!;

    private View _configCard = null!;
    private View _configLiveBadge = null!;
    private MaterialButton _configResetAllButton = null!;
    private View _configReadOnlyBanner = null!;
    private View _configEmptyView = null!;
    private RecyclerView _configList = null!;

    private TextView _docOriginView = null!;
    private TextView _docEmptyView = null!;
    private LinearLayout _docMarkdownContainer = null!;

    private CancellationTokenSource? _viewLifecycleCts;
    private string _mode = PluginDetailActivity.ModeInstalled;
    private string _target = string.Empty;
    private string? _initialTitle;
    private int _renderPosted;
    private long _markdownSequence;
    private string? _lastRenderedMarkdown;
    private string? _lastShownStatusKey;
    private string? _lastShownFailure;

    public static PluginDetailFragment Create(string mode, string target, string? displayTitle)
    {
        var fragment = new PluginDetailFragment();
        var args = new Bundle();
        args.PutString(ArgMode, mode);
        args.PutString(ArgTarget, target);
        if (!string.IsNullOrWhiteSpace(displayTitle))
        {
            args.PutString(ArgTitle, displayTitle);
        }
        fragment.Arguments = args;
        return fragment;
    }

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        var view = inflater.Inflate(Resource.Layout.fragment_plugin_detail, container, false)!;
        _bridge = DshMobileApplication.Current.Bridge;
        _viewModel = _bridge.Agent.CreatePluginDetailViewModel();
        _viewLifecycleCts = new CancellationTokenSource();

        _mode = Arguments?.GetString(ArgMode) ?? PluginDetailActivity.ModeInstalled;
        _target = Arguments?.GetString(ArgTarget) ?? string.Empty;
        _initialTitle = Arguments?.GetString(ArgTitle);

        _toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.plugin_detail_toolbar)!;
        _toolbar.NavigationClick += (_, _) => Activity?.Finish();
        _toolbar.MenuItemClick += (_, e) =>
        {
            if (e.Item?.ItemId == Resource.Id.plugin_detail_action_refresh)
            {
                _ = LoadDetailAsync();
            }
        };
        if (!string.IsNullOrWhiteSpace(_initialTitle))
        {
            _toolbar.Title = _initialTitle;
        }

        _progress = view.FindViewById<LinearProgressIndicator>(Resource.Id.plugin_detail_progress)!;
        _scrollContent = view.FindViewById(Resource.Id.plugin_detail_content)!;
        _restartBanner = view.FindViewById(Resource.Id.plugin_detail_restart_banner)!;
        _restartNowButton = view.FindViewById<MaterialButton>(Resource.Id.plugin_detail_restart_now_btn)!;

        _nameView = view.FindViewById<TextView>(Resource.Id.plugin_detail_name)!;
        _packageView = view.FindViewById<TextView>(Resource.Id.plugin_detail_package)!;
        _versionView = view.FindViewById<TextView>(Resource.Id.plugin_detail_version)!;
        _descView = view.FindViewById<TextView>(Resource.Id.plugin_detail_desc)!;

        _runtimeBadge = view.FindViewById<TextView>(Resource.Id.plugin_detail_badge_runtime)!;
        _protectedBadge = view.FindViewById<TextView>(Resource.Id.plugin_detail_badge_protected)!;
        _officialBadge = view.FindViewById<TextView>(Resource.Id.plugin_detail_badge_official)!;
        _installedBadge = view.FindViewById<TextView>(Resource.Id.plugin_detail_badge_installed)!;
        _configurableBadge = view.FindViewById<TextView>(Resource.Id.plugin_detail_badge_configurable)!;
        _webUiBadge = view.FindViewById<TextView>(Resource.Id.plugin_detail_badge_webui)!;
        _notBundleBadge = view.FindViewById<TextView>(Resource.Id.plugin_detail_badge_not_bundle)!;
        _trimmedBadge = view.FindViewById<TextView>(Resource.Id.plugin_detail_badge_trimmed)!;
        _updateBadge = view.FindViewById<TextView>(Resource.Id.plugin_detail_badge_update)!;

        _metaInfoView = view.FindViewById<TextView>(Resource.Id.plugin_detail_meta_info)!;
        _linksRow = view.FindViewById(Resource.Id.plugin_detail_links_row)!;
        _homepageLinkButton = view.FindViewById<MaterialButton>(Resource.Id.plugin_detail_link_homepage_btn)!;
        _repoLinkButton = view.FindViewById<MaterialButton>(Resource.Id.plugin_detail_link_repo_btn)!;
        _npmLinkButton = view.FindViewById<MaterialButton>(Resource.Id.plugin_detail_link_npm_btn)!;

        _actionsRow = view.FindViewById(Resource.Id.plugin_detail_actions_row)!;
        _installButton = view.FindViewById<MaterialButton>(Resource.Id.plugin_detail_install_btn)!;
        _toggleButton = view.FindViewById<MaterialButton>(Resource.Id.plugin_detail_toggle_btn)!;
        _uninstallButton = view.FindViewById<MaterialButton>(Resource.Id.plugin_detail_uninstall_btn)!;
        _upgradeButton = view.FindViewById<MaterialButton>(Resource.Id.plugin_detail_upgrade_btn)!;
        _installHintView = view.FindViewById<TextView>(Resource.Id.plugin_detail_install_hint)!;
        _updateHintView = view.FindViewById<TextView>(Resource.Id.plugin_detail_update_hint)!;
        _feedbackView = view.FindViewById<TextView>(Resource.Id.plugin_detail_feedback)!;

        _webUiCard = view.FindViewById(Resource.Id.plugin_detail_webui_card)!;
        _webUiActionButton = view.FindViewById<MaterialButton>(Resource.Id.plugin_detail_webui_action_btn)!;

        _configCard = view.FindViewById(Resource.Id.plugin_detail_config_card)!;
        _configLiveBadge = view.FindViewById(Resource.Id.plugin_detail_config_live_badge)!;
        _configResetAllButton = view.FindViewById<MaterialButton>(Resource.Id.plugin_detail_config_reset_all_btn)!;
        _configReadOnlyBanner = view.FindViewById(Resource.Id.plugin_detail_config_readonly_banner)!;
        _configEmptyView = view.FindViewById(Resource.Id.plugin_detail_config_empty)!;
        _configList = view.FindViewById<RecyclerView>(Resource.Id.plugin_detail_config_list)!;
        _configList.NestedScrollingEnabled = false;
        _configList.SetLayoutManager(new LinearLayoutManager(RequireContext()));
        _configAdapter = new PluginConfigAdapter(
            OnConfigBooleanToggled,
            OnConfigEditFieldRequested,
            OnConfigSelectOptionRequested,
            OnConfigResetFieldRequested);
        _configList.SetAdapter(_configAdapter);

        _docOriginView = view.FindViewById<TextView>(Resource.Id.plugin_detail_doc_origin)!;
        _docEmptyView = view.FindViewById<TextView>(Resource.Id.plugin_detail_doc_empty)!;
        _docMarkdownContainer = view.FindViewById<LinearLayout>(Resource.Id.plugin_detail_doc_markdown_container)!;

        _restartNowButton.Click += (_, _) => _ = _viewModel.RestartDshAsync();
        _homepageLinkButton.Click += (_, _) => PluginBrowserDialogs.ConfirmAndOpenExternalHttpsUrl(RequireContext(), View, _viewModel.State.HomepageUrl);
        _repoLinkButton.Click += (_, _) => PluginBrowserDialogs.ConfirmAndOpenExternalHttpsUrl(RequireContext(), View, _viewModel.State.RepositoryUrl);
        _npmLinkButton.Click += (_, _) => PluginBrowserDialogs.ConfirmAndOpenExternalHttpsUrl(RequireContext(), View, _viewModel.State.NpmUrl);
        _installButton.Click += (_, _) => ConfirmInstallCurrentPlugin();
        _toggleButton.Click += (_, _) => ConfirmToggleCurrentPlugin();
        _uninstallButton.Click += (_, _) => ConfirmUninstallCurrentPlugin();
        _upgradeButton.Click += (_, _) => ConfirmUpgradeCurrentPlugin();
        _webUiActionButton.Click += (_, _) => HandleWebUiGuideAction();
        _configResetAllButton.Click += (_, _) => ConfirmResetAllSettings();

        Edges.ClampMaxWidth(_scrollContent);
        return view;
    }

    public override void OnStart()
    {
        base.OnStart();
        _viewModel.StateChanged += OnStateChanged;
        _bridge.Changed += OnBridgeChanged;
        Render(_viewModel.State);
        _ = LoadDetailAsync();
    }

    public override void OnStop()
    {
        _viewModel.StateChanged -= OnStateChanged;
        _bridge.Changed -= OnBridgeChanged;
        base.OnStop();
    }

    public override void OnDestroyView()
    {
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
        _ = _viewModel.DisposeAsync().AsTask();
        base.OnDestroyView();
    }

    private async Task LoadDetailAsync()
    {
        var ct = _viewLifecycleCts?.Token ?? CancellationToken.None;
        if (ct.IsCancellationRequested)
        {
            return;
        }
        var language = ResolvePreferredLanguage();
        try
        {
            if (string.Equals(_mode, PluginDetailActivity.ModeCatalog, StringComparison.Ordinal))
            {
                await _viewModel.LoadCatalogAsync(_target, language, ct).ConfigureAwait(false);
            }
            else
            {
                await _viewModel.LoadInstalledAsync(_target, language, ct).ConfigureAwait(false);
            }
        }
        catch (System.OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static string ResolvePreferredLanguage()
    {
        var tag = Java.Util.Locale.Default?.ToLanguageTag();
        return string.IsNullOrWhiteSpace(tag) ? "zh-CN" : tag;
    }

    private void OnStateChanged(PluginDetailUiState _) => ScheduleRender();

    private void OnBridgeChanged(object? sender, EventArgs e) => ScheduleRender();

    private void ScheduleRender()
    {
        if (Interlocked.Exchange(ref _renderPosted, 1) == 0)
        {
            Activity?.RunOnUiThread(() =>
            {
                Volatile.Write(ref _renderPosted, 0);
                if (IsAdded && View is not null)
                {
                    Render(_viewModel.State);
                }
            });
        }
    }

    private void Render(PluginDetailUiState state)
    {
        var context = RequireContext();
        var hostReady = _bridge.Runtime.Host?.State == DshHostState.Ready;
        var busy = state.IsLoading || state.IsSavingConfig || state.IsMutating || state.IsRestarting || _bridge.IsBusy;

        _progress.Visibility = busy ? ViewStates.Visible : ViewStates.Gone;
        var refreshMenuItem = _toolbar.Menu?.FindItem(Resource.Id.plugin_detail_action_refresh);
        refreshMenuItem?.SetEnabled(!busy);

        // 1. 待重启横幅
        _restartBanner.Visibility = state.RequiresDshRestart ? ViewStates.Visible : ViewStates.Gone;
        _restartNowButton.Enabled = !busy;

        // 2. 顶部摘要与操作卡
        var resolvedTitle = !string.IsNullOrWhiteSpace(state.Title)
            ? state.Title
            : (!string.IsNullOrWhiteSpace(_initialTitle)
                ? _initialTitle
                : (!string.IsNullOrWhiteSpace(state.PackageName) ? state.PackageName : _target));
        _toolbar.Title = string.IsNullOrWhiteSpace(resolvedTitle)
            ? GetString(Resource.String.plugin_detail_title)
            : resolvedTitle;
        _nameView.Text = resolvedTitle;
        _packageView.Text = !string.IsNullOrWhiteSpace(state.PackageName) ? state.PackageName : _target;

        RenderVersion(context, state);
        if (!string.IsNullOrWhiteSpace(state.Description))
        {
            _descView.Visibility = ViewStates.Visible;
            _descView.Text = state.Description;
        }
        else
        {
            _descView.Visibility = ViewStates.Gone;
        }

        RenderBadges(context, state);
        RenderMetadataAndLinks(context, state);
        RenderActionButtons(context, state, hostReady, busy);
        RenderFeedbackBanner(context, state);

        // 3. 页面修改插件生效引导卡
        RenderWebUiGuideCard(context, state, hostReady, busy);

        // 4. 官方 settings/* 实时配置卡
        RenderSettingsSection(state, busy);

        // 5. 详情文档卡
        RenderMarkdownSection(state);

        // 瞬态操作结果 Snackbar
        RenderFeedbackSnackbars(context, state);
    }

    private void RenderVersion(Context context, PluginDetailUiState state)
    {
        var currentVer = state.Version?.Trim();
        var latestVer = state.LatestCatalogVersion?.Trim();
        if (!string.IsNullOrEmpty(currentVer))
        {
            _versionView.Visibility = ViewStates.Visible;
            if (!string.IsNullOrEmpty(latestVer) &&
                !string.Equals(currentVer, latestVer, StringComparison.OrdinalIgnoreCase))
            {
                _versionView.Text = Rows.Fmt(context, Resource.String.plugin_detail_version_update_fmt, currentVer, latestVer);
            }
            else
            {
                _versionView.Text = Rows.Fmt(context, Resource.String.plugin_detail_version_fmt, currentVer);
            }
        }
        else if (!string.IsNullOrEmpty(latestVer))
        {
            _versionView.Visibility = ViewStates.Visible;
            _versionView.Text = Rows.Fmt(context, Resource.String.plugin_detail_version_fmt, latestVer);
        }
        else
        {
            _versionView.Visibility = ViewStates.Gone;
        }
    }

    private void RenderBadges(Context context, PluginDetailUiState state)
    {
        if (state.IsInstalled)
        {
            var (textRes, tone) = state.FiberPhase switch
            {
                "failed" => (Resource.String.plugins_status_failed, Rows.BadgeTone.Error),
                "not-bundle" => (Resource.String.plugins_status_not_bundle, Rows.BadgeTone.Warning),
                "pending-restart" => (Resource.String.plugins_status_pending_restart, Rows.BadgeTone.Primary),
                "loading" or "pending" when state.Enabled => (Resource.String.plugins_status_loading, Rows.BadgeTone.Primary),
                "active" when state.Enabled => (Resource.String.plugins_status_active, Rows.BadgeTone.Success),
                _ when !state.Enabled => (Resource.String.plugins_status_disabled, Rows.BadgeTone.Neutral),
                _ => (Resource.String.plugins_status_inactive, Rows.BadgeTone.Neutral),
            };
            Rows.SetBadge(_runtimeBadge, context.GetString(textRes), tone);
        }
        else
        {
            _runtimeBadge.Visibility = ViewStates.Gone;
        }

        _protectedBadge.Visibility = state.IsProtected ? ViewStates.Visible : ViewStates.Gone;
        var isOfficial = !string.IsNullOrWhiteSpace(state.PackageName) &&
                         state.PackageName.StartsWith("@deepseek-ai/", StringComparison.Ordinal);
        _officialBadge.Visibility = isOfficial ? ViewStates.Visible : ViewStates.Gone;
        _installedBadge.Visibility = state.IsInstalled ? ViewStates.Visible : ViewStates.Gone;
        _configurableBadge.Visibility = state.HasSettings ? ViewStates.Visible : ViewStates.Gone;
        _webUiBadge.Visibility = state.HasCustomWebUi ? ViewStates.Visible : ViewStates.Gone;
        _notBundleBadge.Visibility = state.IsInstalled && !state.HasBundleManifest ? ViewStates.Visible : ViewStates.Gone;
        _trimmedBadge.Visibility = state.IsTrimmedByNativeMode ? ViewStates.Visible : ViewStates.Gone;

        // 可更新胶囊：只有拿到明确的最新版本时才展示，避免出现没有目标版本的“可更新”。
        if (state.HasUpdate && !string.IsNullOrWhiteSpace(state.LatestCatalogVersion))
        {
            _updateBadge.Visibility = ViewStates.Visible;
            _updateBadge.Text = Rows.Fmt(
                context,
                Resource.String.plugins_update_badge_fmt,
                state.LatestCatalogVersion!.Trim());
        }
        else
        {
            _updateBadge.Visibility = ViewStates.Gone;
        }
    }

    private void RenderMetadataAndLinks(Context context, PluginDetailUiState state)
    {
        var metaSegments = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(state.Author))
        {
            metaSegments.Add(Rows.Fmt(context, Resource.String.plugin_detail_meta_author_fmt, state.Author.Trim()));
        }
        if (!string.IsNullOrWhiteSpace(state.License))
        {
            metaSegments.Add(Rows.Fmt(context, Resource.String.plugin_detail_meta_license_fmt, state.License.Trim()));
        }
        if (metaSegments.Count > 0)
        {
            _metaInfoView.Visibility = ViewStates.Visible;
            _metaInfoView.Text = string.Join(" · ", metaSegments);
        }
        else
        {
            _metaInfoView.Visibility = ViewStates.Gone;
        }

        var hasHomepage = PluginBrowserDialogs.TryParseSafeHttpsUrl(state.HomepageUrl, out _);
        var hasRepo = PluginBrowserDialogs.TryParseSafeHttpsUrl(state.RepositoryUrl, out _);
        var hasNpm = PluginBrowserDialogs.TryParseSafeHttpsUrl(state.NpmUrl, out _);

        _homepageLinkButton.Visibility = hasHomepage ? ViewStates.Visible : ViewStates.Gone;
        _repoLinkButton.Visibility = hasRepo ? ViewStates.Visible : ViewStates.Gone;
        _npmLinkButton.Visibility = hasNpm ? ViewStates.Visible : ViewStates.Gone;
        _linksRow.Visibility = hasHomepage || hasRepo || hasNpm ? ViewStates.Visible : ViewStates.Gone;
    }

    private void RenderActionButtons(Context context, PluginDetailUiState state, bool hostReady, bool busy)
    {
        if (!state.IsInstalled)
        {
            var validSpec = PluginPackageValidator.IsValidInstallSpec(state.RecommendedInstallSpec ?? state.PackageName);
            var showInstall = !state.IsLoading && validSpec;
            _installButton.Visibility = showInstall ? ViewStates.Visible : ViewStates.Gone;
            _installButton.Enabled = showInstall && state.CanInstall && hostReady && !busy;
            _toggleButton.Visibility = ViewStates.Gone;
            _uninstallButton.Visibility = ViewStates.Gone;
            _upgradeButton.Visibility = ViewStates.Gone;
            _updateHintView.Visibility = ViewStates.Gone;
            _actionsRow.Visibility = showInstall ? ViewStates.Visible : ViewStates.Gone;
            _installHintView.Visibility = showInstall && !hostReady ? ViewStates.Visible : ViewStates.Gone;
            return;
        }

        _installButton.Visibility = ViewStates.Gone;
        _installHintView.Visibility = ViewStates.Gone;

        var latestVersion = state.LatestCatalogVersion?.Trim();
        var showUpgrade = state.CanUpgrade && !string.IsNullOrWhiteSpace(latestVersion);
        _upgradeButton.Visibility = showUpgrade ? ViewStates.Visible : ViewStates.Gone;
        if (showUpgrade)
        {
            _upgradeButton.Text = Rows.Fmt(context, Resource.String.plugins_upgrade_to_fmt, latestVersion!);
            _upgradeButton.Enabled = hostReady && !busy;
        }

        var showToggle = !state.IsProtected && !state.IsTrimmedByNativeMode && state.CanToggle;
        _toggleButton.Visibility = showToggle ? ViewStates.Visible : ViewStates.Gone;
        if (showToggle)
        {
            _toggleButton.SetText(state.Enabled ? Resource.String.plugins_toggle_disable : Resource.String.plugins_toggle_enable);
            _toggleButton.Enabled = hostReady && !busy;
        }

        var showUninstall = state.CanUninstall;
        _uninstallButton.Visibility = showUninstall ? ViewStates.Visible : ViewStates.Gone;
        _uninstallButton.Enabled = hostReady && !busy;

        _actionsRow.Visibility = showUpgrade || showToggle || showUninstall ? ViewStates.Visible : ViewStates.Gone;

        // 内置、核心保护或官方按需插件不能通过 npm 单独更新时，给出明确的 DSH 层更新引导。
        var isInstallationShipped = string.Equals(state.Availability, "installation", StringComparison.OrdinalIgnoreCase);
        var isOfficialScope = !string.IsNullOrWhiteSpace(state.PackageName) &&
                              state.PackageName.StartsWith("@deepseek-ai/", StringComparison.Ordinal);
        if (isInstallationShipped || state.IsProtected)
        {
            _updateHintView.Visibility = ViewStates.Visible;
            _updateHintView.SetText(Resource.String.plugins_update_requires_dsh_layer);
        }
        else if (isOfficialScope && !showUpgrade)
        {
            _updateHintView.Visibility = ViewStates.Visible;
            _updateHintView.SetText(Resource.String.plugins_update_official_requires_dsh_layer);
        }
        else
        {
            _updateHintView.Visibility = ViewStates.Gone;
        }
    }

    private void RenderFeedbackBanner(Context context, PluginDetailUiState state)
    {
        if (!string.IsNullOrWhiteSpace(state.FailureCode))
        {
            _feedbackView.Visibility = ViewStates.Visible;
            _feedbackView.SetTextColor(new Color(Rows.Color(_feedbackView, Android.Resource.Attribute.ColorError)));
            var detail = MapDiagnosticMessage(context, state.FeedbackDiagnostic) ?? state.FailureCode!;
            _feedbackView.Text = string.Equals(detail, state.FailureCode, StringComparison.Ordinal)
                ? detail
                : $"{state.FailureCode}: {detail}";
            return;
        }

        if (!string.IsNullOrWhiteSpace(state.DiagnosticSummary))
        {
            _feedbackView.Visibility = ViewStates.Visible;
            _feedbackView.SetTextColor(new Color(Rows.Color(_feedbackView, Android.Resource.Attribute.ColorError)));
            _feedbackView.Text = state.DiagnosticSummary;
            return;
        }

        if (state.StatusKey is { } statusKey && TryMapStatusMessageRes(statusKey, out var msgRes))
        {
            _feedbackView.Visibility = ViewStates.Visible;
            _feedbackView.SetTextColor(new Color(Rows.Color(_feedbackView, Resource.Attribute.colorPrimary)));
            _feedbackView.SetText(msgRes);
            return;
        }

        _feedbackView.Visibility = ViewStates.Gone;
    }

    private void RenderWebUiGuideCard(Context context, PluginDetailUiState state, bool hostReady, bool busy)
    {
        if (!state.HasCustomWebUi)
        {
            _webUiCard.Visibility = ViewStates.Gone;
            return;
        }

        _webUiCard.Visibility = ViewStates.Visible;
        var prefEnabled = UiPrefs.FullWebUiCompatEnabled(context);
        var activeAndReady = _bridge.CanOpenLocalWebUiPortal;

        if (!prefEnabled)
        {
            _webUiActionButton.SetText(Resource.String.plugin_detail_webui_action_enable);
            _webUiActionButton.Enabled = !busy;
        }
        else if (activeAndReady)
        {
            _webUiActionButton.SetText(Resource.String.plugin_detail_webui_action_open_browser);
            _webUiActionButton.Enabled = !busy;
        }
        else if (hostReady || _bridge.Runtime.HostOwnsProcesses)
        {
            _webUiActionButton.SetText(Resource.String.plugin_detail_webui_action_restart);
            _webUiActionButton.Enabled = !busy;
        }
        else
        {
            _webUiActionButton.SetText(Resource.String.plugin_detail_webui_action_start);
            _webUiActionButton.Enabled = !busy;
        }
    }

    private void RenderSettingsSection(PluginDetailUiState state, bool busy)
    {
        if (!state.IsInstalled)
        {
            _configCard.Visibility = ViewStates.Gone;
            return;
        }

        _configCard.Visibility = ViewStates.Visible;
        var hasFields = state.HasSettings && state.ConfigFields.Count > 0;
        if (!hasFields)
        {
            _configLiveBadge.Visibility = ViewStates.Gone;
            _configResetAllButton.Visibility = ViewStates.Gone;
            _configReadOnlyBanner.Visibility = ViewStates.Gone;
            _configList.Visibility = ViewStates.Gone;
            _configEmptyView.Visibility = state.IsLoading ? ViewStates.Gone : ViewStates.Visible;
            return;
        }

        _configEmptyView.Visibility = ViewStates.Gone;
        _configLiveBadge.Visibility = state.SettingsWritable ? ViewStates.Visible : ViewStates.Gone;
        _configReadOnlyBanner.Visibility = state.SettingsWritable ? ViewStates.Gone : ViewStates.Visible;
        _configResetAllButton.Visibility = state.SettingsWritable ? ViewStates.Visible : ViewStates.Gone;
        _configResetAllButton.Enabled = state.SettingsWritable && !busy;

        _configList.Visibility = ViewStates.Visible;
        _configAdapter.Submit(state.ConfigFields, state.SettingsWritable, busy);
    }

    private void RenderMarkdownSection(PluginDetailUiState state)
    {
        if (!string.IsNullOrWhiteSpace(state.DocumentOrigin))
        {
            _docOriginView.Visibility = ViewStates.Visible;
            _docOriginView.Text = string.IsNullOrWhiteSpace(state.DocumentPath)
                ? state.DocumentOrigin
                : $"{state.DocumentOrigin} · {state.DocumentPath}";
        }
        else
        {
            _docOriginView.Visibility = ViewStates.Gone;
        }

        var markdown = state.MarkdownDocument;
        if (string.IsNullOrWhiteSpace(markdown))
        {
            _lastRenderedMarkdown = null;
            _docMarkdownContainer.RemoveAllViews();
            _docMarkdownContainer.Visibility = ViewStates.Gone;
            _docEmptyView.Visibility = state.IsLoading ? ViewStates.Gone : ViewStates.Visible;
            return;
        }

        _docEmptyView.Visibility = ViewStates.Gone;
        _docMarkdownContainer.Visibility = ViewStates.Visible;
        if (string.Equals(_lastRenderedMarkdown, markdown, StringComparison.Ordinal))
        {
            return;
        }
        _lastRenderedMarkdown = markdown;

        var seq = Interlocked.Increment(ref _markdownSequence);
        var ct = _viewLifecycleCts?.Token ?? CancellationToken.None;
        _ = Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var segments = MarkdownRenderCache.BuildSegments(markdown);
            Activity?.RunOnUiThread(() =>
            {
                if (!IsAdded || View is null || seq != Volatile.Read(ref _markdownSequence))
                {
                    return;
                }
                ChatMarkdownBinder.BindSegments(
                    _docMarkdownContainer,
                    segments,
                    LayoutInflater.From(RequireContext())!);
            });
        }, ct);
    }

    private void RenderFeedbackSnackbars(Context context, PluginDetailUiState state)
    {
        if (View is not { } root)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(state.FailureCode))
        {
            var failureKey = $"{state.FailureCode}:{state.FeedbackDiagnostic}";
            if (!string.Equals(failureKey, _lastShownFailure, StringComparison.Ordinal))
            {
                _lastShownFailure = failureKey;
                var detail = MapDiagnosticMessage(context, state.FeedbackDiagnostic) ?? state.FailureCode!;
                Snackbar.Make(root, detail, Snackbar.LengthLong).Show();
            }
        }
        else
        {
            _lastShownFailure = null;
        }

        if (state.StatusKey is { } statusKey && !string.Equals(statusKey, _lastShownStatusKey, StringComparison.Ordinal))
        {
            _lastShownStatusKey = statusKey;
            if (TryMapStatusMessageRes(statusKey, out var msgRes))
            {
                Snackbar.Make(root, msgRes, Snackbar.LengthShort).Show();
            }
        }
        else if (state.StatusKey is null)
        {
            _lastShownStatusKey = null;
        }
    }

    private void HandleWebUiGuideAction()
    {
        var context = RequireContext();
        var prefEnabled = UiPrefs.FullWebUiCompatEnabled(context);
        var activeAndReady = _bridge.CanOpenLocalWebUiPortal;

        if (!prefEnabled)
        {
            PluginBrowserDialogs.ConfirmToggleFullWebUiCompat(
                context,
                View,
                _bridge,
                targetEnabled: true,
                restartHostAsync: () => _viewModel.RestartDshAsync(),
                onPreferenceChanged: ScheduleRender);
            return;
        }

        if (activeAndReady)
        {
            PluginBrowserDialogs.ConfirmAndOpenLocalWebUi(Activity ?? RequireContext(), View, _bridge);
            return;
        }

        if (_bridge.Runtime.Host?.State == DshHostState.Ready || _bridge.Runtime.HostOwnsProcesses)
        {
            _ = _viewModel.RestartDshAsync();
        }
        else
        {
            _ = _bridge.RunStartAsync();
        }
    }

    private void ConfirmInstallCurrentPlugin()
    {
        var context = RequireContext();
        if (_bridge.Runtime.Host?.State != DshHostState.Ready)
        {
            if (View is { } root)
            {
                Snackbar.Make(root, Resource.String.plugins_host_required, Snackbar.LengthLong).Show();
            }
            return;
        }

        var state = _viewModel.State;
        var installSpec = (state.RecommendedInstallSpec ?? state.PackageName)?.Trim() ?? string.Empty;
        if (!PluginPackageValidator.IsValidInstallSpec(installSpec))
        {
            if (View is { } root)
            {
                Snackbar.Make(root, Resource.String.plugins_invalid_spec, Snackbar.LengthLong).Show();
            }
            return;
        }

        new MaterialAlertDialogBuilder(context)
            .SetTitle(Resource.String.plugins_catalog_install_confirm_title)!
            .SetMessage(Rows.Fmt(context, Resource.String.plugins_catalog_install_confirm_msg, installSpec))!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(Resource.String.plugins_install_confirm_action, (_, _) =>
            {
                _ = _viewModel.InstallAsync(installSpec);
            })!
            .Show();
    }

    /// <summary>详情页升级确认：展示当前版本与目标版本，确认后走受控升级并保留重启提示。</summary>
    private void ConfirmUpgradeCurrentPlugin()
    {
        var context = RequireContext();
        var state = _viewModel.State;
        if (_bridge.Runtime.Host?.State != DshHostState.Ready)
        {
            if (View is { } root)
            {
                Snackbar.Make(root, Resource.String.plugins_host_required, Snackbar.LengthLong).Show();
            }
            return;
        }

        var targetVersion = state.LatestCatalogVersion?.Trim();
        if (!state.CanUpgrade || string.IsNullOrWhiteSpace(targetVersion))
        {
            return;
        }

        var displayTitle = string.IsNullOrWhiteSpace(state.Title) ? state.PackageName : state.Title;
        new MaterialAlertDialogBuilder(context)
            .SetTitle(Resource.String.plugins_upgrade_confirm_title)!
            .SetMessage(Rows.Fmt(
                context,
                Resource.String.plugins_upgrade_confirm_msg,
                displayTitle,
                state.Version?.Trim() ?? string.Empty,
                targetVersion!))!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(Resource.String.plugins_upgrade_confirm_action, (_, _) =>
            {
                _ = _viewModel.UpgradeAsync();
            })!
            .Show();
    }

    private void ConfirmToggleCurrentPlugin()
    {
        var context = RequireContext();
        var state = _viewModel.State;
        if (!state.CanToggle || state.IsProtected || state.IsTrimmedByNativeMode)
        {
            return;
        }

        var desiredEnabled = !state.Enabled;
        var actionLabel = GetString(desiredEnabled ? Resource.String.plugins_toggle_enable : Resource.String.plugins_toggle_disable)!;
        var displayTitle = string.IsNullOrWhiteSpace(state.Title) ? state.PackageName : state.Title;

        new MaterialAlertDialogBuilder(context)
            .SetTitle(Rows.Fmt(context, Resource.String.plugins_toggle_confirm_title, actionLabel, displayTitle))!
            .SetMessage(Resource.String.plugins_toggle_confirm_msg)!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(actionLabel, (_, _) =>
            {
                _ = _viewModel.SetEnabledAsync(desiredEnabled);
            })!
            .Show();
    }

    private void ConfirmUninstallCurrentPlugin()
    {
        var context = RequireContext();
        var state = _viewModel.State;
        if (!state.CanUninstall || string.IsNullOrWhiteSpace(state.PackageName))
        {
            return;
        }

        new MaterialAlertDialogBuilder(context)
            .SetTitle(Rows.Fmt(context, Resource.String.plugins_uninstall_confirm_title, state.PackageName))!
            .SetMessage(Rows.Fmt(context, Resource.String.plugins_uninstall_confirm_msg, state.PackageName))!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(Resource.String.plugins_uninstall_confirm_action, (_, _) =>
            {
                _ = _viewModel.UninstallAsync();
            })!
            .Show();
    }

    private void OnConfigBooleanToggled(PluginConfigFieldView field, bool newValue)
    {
        var encoded = PluginSettingsSchemaParser.EncodeBoolean(newValue);
        _ = _viewModel.SaveFieldAsync(field.Path, encoded);
    }

    private void OnConfigEditFieldRequested(PluginConfigFieldView field)
    {
        var context = RequireContext();
        var content = LayoutInflater.From(context)!.Inflate(Resource.Layout.dialog_text_input, null)!;
        var inputLayout = content.FindViewById<TextInputLayout>(Resource.Id.dialog_text_input_layout)!;
        var input = content.FindViewById<TextInputEditText>(Resource.Id.dialog_text_input)!;

        inputLayout.ErrorEnabled = true;

        switch (field.Kind)
        {
            case PluginConfigFieldKind.Secret:
                input.InputType = InputTypes.ClassText | InputTypes.TextVariationPassword;
                input.SetSingleLine(true);
                inputLayout.Hint = GetString(Resource.String.plugin_detail_config_secret_input_hint);
                input.Text = string.Empty;
                break;

            case PluginConfigFieldKind.Number:
                input.InputType = InputTypes.ClassNumber | InputTypes.NumberFlagDecimal | InputTypes.NumberFlagSigned;
                input.SetSingleLine(true);
                inputLayout.Hint = field.KeyDisplay;
                input.Text = (field.CurrentValueJson ?? field.BaseValueJson)?.Trim() ?? string.Empty;
                break;

            case PluginConfigFieldKind.JsonFallback:
                input.InputType = InputTypes.ClassText | InputTypes.TextFlagMultiLine;
                input.SetSingleLine(false);
                input.SetMinLines(4);
                input.SetMaxLines(10);
                input.Typeface = Typeface.Monospace;
                inputLayout.Hint = field.KeyDisplay;
                input.Text = field.CurrentValueJson ?? field.BaseValueJson ?? "{}";
                break;

            default:
                input.InputType = InputTypes.ClassText;
                input.SetSingleLine(true);
                inputLayout.Hint = field.KeyDisplay;
                input.Text = PluginConfigAdapter.UnquoteJsonDisplay(field.CurrentValueJson ?? field.BaseValueJson);
                break;
        }

        if (!string.IsNullOrEmpty(input.Text))
        {
            input.SetSelection(input.Text.Length);
        }

        var dialog = new MaterialAlertDialogBuilder(context)
            .SetTitle(field.Label)!
            .SetView(content)!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(Resource.String.plugin_detail_config_save, (_, _) => { })!
            .Create()!;

        dialog.Show();

        var saveButton = dialog.GetButton((int)DialogButtonType.Positive);
        if (saveButton is not null)
        {
            saveButton.Click += (_, _) =>
            {
                var rawInput = input.Text ?? string.Empty;
                if (!PluginSettingsSchemaParser.TryEncodeFieldValue(field, rawInput, out var encoded, out var errorKey))
                {
                    inputLayout.Error = MapDiagnosticMessage(context, errorKey)
                        ?? GetString(Resource.String.plugin_detail_config_err_invalid_value);
                    return;
                }
                inputLayout.Error = null;
                dialog.Dismiss();
                _ = _viewModel.SaveFieldAsync(field.Path, encoded);
            };
        }
    }

    private void OnConfigSelectOptionRequested(PluginConfigFieldView field)
    {
        if (field.Options.Count == 0)
        {
            return;
        }

        var context = RequireContext();
        var effectiveJson = (field.CurrentValueJson ?? field.BaseValueJson)?.Trim();
        var labels = new string[field.Options.Count];
        var checkedIndex = -1;

        for (var i = 0; i < field.Options.Count; i++)
        {
            var option = field.Options[i];
            labels[i] = string.IsNullOrWhiteSpace(option.Description)
                ? option.DisplayLabel
                : $"{option.DisplayLabel} ({option.Description})";
            if (checkedIndex < 0 &&
                !string.IsNullOrEmpty(effectiveJson) &&
                string.Equals(option.RawValueJson.Trim(), effectiveJson, StringComparison.Ordinal))
            {
                checkedIndex = i;
            }
        }

        new MaterialAlertDialogBuilder(context)
            .SetTitle(field.Label)!
            .SetSingleChoiceItems(labels, checkedIndex, (sender, args) =>
            {
                var selectedIndex = args.Which;
                if (sender is IDialogInterface dialogInterface)
                {
                    dialogInterface.Dismiss();
                }
                if (selectedIndex >= 0 && selectedIndex < field.Options.Count)
                {
                    var chosen = field.Options[selectedIndex];
                    _ = _viewModel.SaveFieldInputAsync(field, chosen.RawValueJson);
                }
            })!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .Show();
    }

    private void OnConfigResetFieldRequested(PluginConfigFieldView field)
    {
        if (field.Kind == PluginConfigFieldKind.Secret)
        {
            var context = RequireContext();
            new MaterialAlertDialogBuilder(context)
                .SetTitle(Resource.String.plugin_detail_config_secret_clear_title)!
                .SetMessage(Rows.Fmt(context, Resource.String.plugin_detail_config_secret_clear_msg, field.Label))!
                .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
                .SetPositiveButton(Resource.String.plugin_detail_config_secret_clear_confirm, (_, _) =>
                {
                    _ = _viewModel.ResetFieldAsync(field.Path);
                })!
                .Show();
            return;
        }

        _ = _viewModel.ResetFieldAsync(field.Path);
    }

    private void ConfirmResetAllSettings()
    {
        var context = RequireContext();
        var state = _viewModel.State;
        var hasSecrets = state.SecretSlots.Count > 0 ||
                         state.ConfigFields.Any(static f => f.Kind == PluginConfigFieldKind.Secret);

        var messageRes = hasSecrets
            ? Resource.String.plugin_detail_config_reset_all_secret_msg
            : Resource.String.plugin_detail_config_reset_all_msg;

        new MaterialAlertDialogBuilder(context)
            .SetTitle(Resource.String.plugin_detail_config_reset_all_title)!
            .SetMessage(messageRes)!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(Resource.String.plugin_detail_config_reset_all_confirm, (_, _) =>
            {
                _ = _viewModel.ResetAllFieldsAsync();
            })!
            .Show();
    }

    private static bool TryMapStatusMessageRes(string statusKey, out int messageRes)
    {
        messageRes = statusKey switch
        {
            "plugin_detail.status.config_saved_live" => Resource.String.plugin_detail_config_saved_live,
            "plugin_detail.status.config_field_reset" => Resource.String.plugin_detail_config_field_reset,
            "plugin_detail.status.config_reset_all" => Resource.String.plugin_detail_config_reset_all_done,
            "plugin_detail.status.config_reset_preserving_secrets" => Resource.String.plugin_detail_config_reset_preserving_secrets,
            "plugin_detail.status.config_no_non_secret_overrides" => Resource.String.plugin_detail_config_no_non_secret_overrides,
            "plugin_detail.status.config_conflict_refreshed" => Resource.String.plugin_detail_config_conflict_refreshed,
            "plugins.status.installed" => Resource.String.plugins_status_installed,
            "plugins.status.uninstalled" => Resource.String.plugins_status_uninstalled,
            "plugins.status.enabled" => Resource.String.plugins_status_enabled,
            "plugins.status.disabled" => Resource.String.plugins_status_disabled_toast,
            "plugins.status.restarted" => Resource.String.plugins_status_restarted,
            "plugins.status.upgraded" => Resource.String.plugins_status_upgraded,
            _ => 0,
        };
        return messageRes != 0;
    }

    private static string? MapDiagnosticMessage(Context context, string? diagnosticKey) => diagnosticKey switch
    {
        null or "" => null,
        "settings.invalid_number" => context.GetString(Resource.String.plugin_detail_config_err_invalid_number),
        "settings.number_below_min" => context.GetString(Resource.String.plugin_detail_config_err_number_below_min),
        "settings.number_above_max" => context.GetString(Resource.String.plugin_detail_config_err_number_above_max),
        "settings.invalid_json" => context.GetString(Resource.String.plugin_detail_config_err_invalid_json),
        "settings.secret_empty" => context.GetString(Resource.String.plugin_detail_config_err_secret_empty),
        "settings.invalid_value" or "settings.invalid_boolean" or "settings.invalid_option"
            => context.GetString(Resource.String.plugin_detail_config_err_invalid_value),
        "settings.not_writable" or "settings.field_readonly"
            => context.GetString(Resource.String.plugin_detail_config_readonly_banner),
        "plugin.trimmed_by_native_mode" => context.GetString(Resource.String.plugins_trimmed_notice),
        _ => diagnosticKey,
    };
}
