using Android.Text;
using Android.Views;
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
using Google.Android.Material.FloatingActionButton;
using Google.Android.Material.ProgressIndicator;
using Google.Android.Material.Snackbar;
using Google.Android.Material.TextField;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace DshMobile.App.Plugins;

/// <summary>
/// PR-07 原生插件目录与 L0/L1 受控管理 Fragment（N-07-02 / ADR-0033 / UI Slice 4）：
/// 100% 绑定 XML 布局，不内嵌 WebView，不解析 L2 动态 Schema 表单；
/// 支持状态过滤芯片、胶囊搜索栏与右下角安装 ExtendedFAB。
/// </summary>
public sealed class PluginsFragment : Fragment
{
    private enum PluginFilter
    {
        All,
        Manageable,
        Enabled,
        Protected,
    }

    private BridgeHost _bridge = null!;
    private PluginsViewModel _vm = null!;
    private PluginsAdapter _adapter = null!;
    private MaterialToolbar _toolbar = null!;
    private LinearProgressIndicator _progress = null!;
    private View _hostBanner = null!;
    private View _restartBanner = null!;
    private MaterialButton _startDshBtn = null!;
    private MaterialButton _restartNowBtn = null!;
    private ExtendedFloatingActionButton _installFab = null!;
    private TextInputEditText _searchInput = null!;
    private ChipGroup _filterChips = null!;
    private PluginFilter _filter = PluginFilter.All;
    private TextView _emptyView = null!;
    private int _renderPosted;
    private string? _lastShownStatusKey;
    private string? _lastShownFailure;

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        var view = inflater.Inflate(Resource.Layout.fragment_plugins, container, false)!;
        _bridge = DshMobileApplication.Current.Bridge;
        _vm = _bridge.Agent.Plugins;

        _toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.plugins_toolbar)!;
        _toolbar.NavigationClick += (_, _) => Activity?.Finish();
        _toolbar.MenuItemClick += (_, e) =>
        {
            if (e.Item?.ItemId == Resource.Id.plugins_menu_refresh)
            {
                _ = RefreshWhenReadyAsync();
            }
        };

        _progress = view.FindViewById<LinearProgressIndicator>(Resource.Id.plugins_progress)!;
        _hostBanner = view.FindViewById(Resource.Id.plugins_host_banner)!;
        _restartBanner = view.FindViewById(Resource.Id.plugins_restart_banner)!;
        _startDshBtn = view.FindViewById<MaterialButton>(Resource.Id.plugins_start_dsh_btn)!;
        _restartNowBtn = view.FindViewById<MaterialButton>(Resource.Id.plugins_restart_now_btn)!;
        _installFab = view.FindViewById<ExtendedFloatingActionButton>(Resource.Id.plugins_install_fab)!;
        _searchInput = view.FindViewById(Resource.Id.plugins_search_layout)!.FindViewById<TextInputEditText>(Resource.Id.search_input)!;
        _searchInput.Hint = GetString(Resource.String.plugins_search_hint);
        _filterChips = view.FindViewById<ChipGroup>(Resource.Id.plugins_filter_chips)!;
        _emptyView = view.FindViewById<TextView>(Resource.Id.plugins_empty)!;

        _startDshBtn.Click += (_, _) => _ = _bridge.RunStartAsync();
        _restartNowBtn.Click += (_, _) => _ = _vm.RestartDshAsync();
        _installFab.Click += (_, _) => ShowInstallDialog();
        _searchInput.TextChanged += OnSearchTextChanged;
        _filterChips.CheckedStateChange += (_, _) =>
        {
            _filter = _filterChips.CheckedChipId switch
            {
                Resource.Id.plugins_filter_manageable => PluginFilter.Manageable,
                Resource.Id.plugins_filter_enabled => PluginFilter.Enabled,
                Resource.Id.plugins_filter_protected => PluginFilter.Protected,
                _ => PluginFilter.All,
            };
            Render(_vm.State);
        };

        var list = view.FindViewById<RecyclerView>(Resource.Id.plugins_list)!;
        list.SetLayoutManager(new LinearLayoutManager(RequireContext()));
        _adapter = new PluginsAdapter(ConfirmTogglePlugin, ConfirmUninstallPlugin);
        list.SetAdapter(_adapter);

        Edges.ClampMaxWidth(list, view.FindViewById(Resource.Id.plugins_filter_scroll));
        Edges.ClampMaxWidthByMargin(
            view.FindViewById(Resource.Id.plugins_search_layout),
            _restartBanner);

        return view;
    }

    public override void OnStart()
    {
        base.OnStart();
        _vm.StateChanged += OnStateChanged;
        _bridge.Changed += OnBridgeChanged;
        Render(_vm.State);
        _ = RefreshWhenReadyAsync();
    }

    public override void OnStop()
    {
        _vm.StateChanged -= OnStateChanged;
        _bridge.Changed -= OnBridgeChanged;
        base.OnStop();
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        _vm.SetSearchQuery(e.Text?.ToString());
    }

    private void OnStateChanged(PluginsUiState _) => ScheduleRender();

    private void OnBridgeChanged(object? sender, EventArgs e)
    {
        if (_bridge.Runtime.Host?.State == DshHostState.Ready &&
            _vm.State.Entries.Count == 0 &&
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
                if (View is not null)
                {
                    Render(_vm.State);
                }
            });
        }
    }

    private void Render(PluginsUiState state)
    {
        var hostReady = _bridge.Runtime.Host?.State == DshHostState.Ready;
        var busy = state.IsLoading || state.IsMutating || state.IsRestarting || _bridge.IsBusy;

        _progress.Visibility = busy ? ViewStates.Visible : ViewStates.Gone;
        _hostBanner.Visibility = hostReady ? ViewStates.Gone : ViewStates.Visible;
        _restartBanner.Visibility = state.RequiresDshRestart ? ViewStates.Visible : ViewStates.Gone;

        _startDshBtn.Enabled = !_bridge.IsBusy;
        _restartNowBtn.Enabled = !busy;
        _toolbar.Menu?.FindItem(Resource.Id.plugins_menu_refresh)?.SetEnabled(hostReady && !busy);
        _installFab.Visibility = hostReady ? ViewStates.Visible : ViewStates.Gone;
        _installFab.Enabled = hostReady && !busy;

        IReadOnlyList<PluginEntryView> visibleEntries = _filter switch
        {
            PluginFilter.Manageable => state.Entries.Where(e => !e.IsProtected && (e.CanToggle || e.CanUninstall)).ToArray(),
            PluginFilter.Enabled => state.Entries.Where(e => e.Enabled).ToArray(),
            PluginFilter.Protected => state.Entries.Where(e => e.IsProtected).ToArray(),
            _ => state.Entries,
        };
        _adapter.Submit(visibleEntries, busy);

        if (visibleEntries.Count == 0 && !state.IsLoading)
        {
            _emptyView.Visibility = ViewStates.Visible;
            _emptyView.SetText(hostReady && !state.CanListPlugins && state.FailureCode == nameof(DshFailureCode.Unsupported)
                ? Resource.String.plugins_unsupported
                : Resource.String.plugins_empty);
        }
        else
        {
            _emptyView.Visibility = ViewStates.Gone;
        }

        if (View is { } root)
        {
            if (!string.IsNullOrWhiteSpace(state.FailureCode))
            {
                var failureKey = $"{state.FailureCode}:{state.DiagnosticSummary}";
                if (!string.Equals(failureKey, _lastShownFailure, StringComparison.Ordinal))
                {
                    _lastShownFailure = failureKey;
                    var detail = string.IsNullOrWhiteSpace(state.DiagnosticSummary)
                        ? state.FailureCode
                        : $"{state.FailureCode}: {state.DiagnosticSummary}";
                    Snackbar.Make(root, Rows.Fmt(RequireContext(), Resource.String.chat_error_banner, detail), Snackbar.LengthLong).Show();
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
        catch (OperationCanceledException)
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
        new MaterialAlertDialogBuilder(ctx)
            .SetTitle(Rows.Fmt(ctx, Resource.String.plugins_toggle_confirm_title, actionLabel, entry.DisplayName))!
            .SetMessage(Resource.String.plugins_toggle_confirm_msg)!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(actionLabel, (_, _) =>
            {
                _ = _vm.SetPluginEnabledAsync(entry.EntryId, entry.ModuleName, desiredEnabled);
            })!
            .Show();
    }
}
