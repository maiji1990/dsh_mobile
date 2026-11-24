using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Presentation;
using DshMobile.App.Bridge;
using DshMobile.App.Chat;
using DshMobile.App.LocalNetwork;
using DshMobile.App.Remote;
using DshMobile.App.RuntimeUi;
using DshMobile.App.Ui;
using DshMobile.Application.LocalNetwork;
using DshMobile.Application.Ui;
using DshMobile.Runtime.Hosting;
using DshMobile.Runtime.Migration;
using DshMobile.Runtime.Updates;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Chip;
using Google.Android.Material.Dialog;
using Google.Android.Material.FloatingActionButton;
using Google.Android.Material.ProgressIndicator;
using Google.Android.Material.Snackbar;
using Google.Android.Material.TextField;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace DshMobile.App.Chats;

/// <summary>
/// PR-05 / PR-09 / UI Slice 2 默认主入口原生会话列表页签：
/// 承载顶栏宿主状态药丸与远程快捷入口、胶囊搜索框与轻量筛选芯片、紧凑会话卡片与底部操作抽屉、
/// 离线缓存会话回看、以及未安装/停止/启动/失败/更新的就地快速控制。
/// </summary>
public sealed class ChatsFragment : Fragment, ITabRoot
{
    private enum SessionFilterMode
    {
        All,
        Running,
        Pending,
    }

    private BridgeHost _bridge = null!;
    private SessionsViewModel _vm = null!;
    private ChatsAdapter _adapter = null!;
    private LinearProgressIndicator _progress = null!;
    private TextView _hostPill = null!;
    private TextView _remotePill = null!;
    private View _hostBanner = null!;
    private TextView _hostTitle = null!;
    private TextView _hostText = null!;
    private LinearProgressIndicator _hostProgress = null!;
    private MaterialButton _hostAction = null!;
    private MaterialButton _hostSecondary = null!;
    private View _failureCard = null!;
    private TextView _failureTitle = null!;
    private TextView _failureDetail = null!;
    private View _updateBanner = null!;
    private TextView _updateBannerTitle = null!;
    private TextView _updateBannerDetail = null!;
    private IMenuItem? _stopMenuItem;
    private View _webInvCard = null!;
    private TextView _webInvSummary = null!;
    private View _searchLayout = null!;
    private View _filterBar = null!;
    private Chip _filterAll = null!;
    private Chip _filterRunning = null!;
    private Chip _filterPending = null!;
    private View _cachedSubheader = null!;
    private RecyclerView _list = null!;
    private View _empty = null!;
    private TextView _emptyText = null!;
    private ExtendedFloatingActionButton _newFab = null!;
    private SessionFilterMode _filterMode = SessionFilterMode.All;
    private HomeStatus? _shownStatus;
    private UpdatePlan? _bannerPlan;
    private bool _secondaryCancelsDownload;
    private int _renderPosted;
    private WebProfileInventoryReport? _lastWebInventory;
    private int _inventoryChecked;

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        var view = inflater.Inflate(Resource.Layout.fragment_chats, container, false)!;
        _bridge = DshMobileApplication.Current.Bridge;
        _vm = _bridge.Agent.Sessions;

        var toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.chats_toolbar)!;
        _stopMenuItem = toolbar.Menu?.FindItem(Resource.Id.chats_menu_stop_dsh);
        _stopMenuItem?.SetVisible(false);
        toolbar.MenuItemClick += (_, e) =>
        {
            var id = e.Item?.ItemId ?? 0;
            if (id == Resource.Id.chats_menu_refresh)
            {
                _ = RefreshIfReadyAsync();
            }
            else if (id == Resource.Id.chats_menu_host_control)
            {
                OpenHostQuickControl();
            }
            else if (id == Resource.Id.chats_menu_remote_host)
            {
                StartActivity(new Intent(RequireContext(), typeof(RemoteActivity)));
            }
            else if (id == Resource.Id.chats_menu_credentials)
            {
                StartActivity(new Intent(RequireContext(), typeof(Credentials.CredentialsActivity)));
            }
            else if (id == Resource.Id.chats_menu_stop_dsh)
            {
                _ = _bridge.RunStopAsync();
            }
        };

        _hostPill = view.FindViewById<TextView>(Resource.Id.chats_host_pill)!;
        _hostPill.Click += (_, _) => OpenHostQuickControl();
        _remotePill = view.FindViewById<TextView>(Resource.Id.chats_remote_pill)!;
        _remotePill.Click += (_, _) => OpenRemoteChatOrSettings();

        _progress = view.FindViewById<LinearProgressIndicator>(Resource.Id.chats_progress)!;
        _hostBanner = view.FindViewById(Resource.Id.chats_host_banner)!;
        _hostTitle = view.FindViewById<TextView>(Resource.Id.chats_host_title)!;
        _hostText = view.FindViewById<TextView>(Resource.Id.chats_host_text)!;
        _hostProgress = view.FindViewById<LinearProgressIndicator>(Resource.Id.chats_host_progress)!;
        _hostAction = view.FindViewById<MaterialButton>(Resource.Id.chats_host_action)!;
        _hostSecondary = view.FindViewById<MaterialButton>(Resource.Id.chats_host_secondary)!;
        _hostAction.Click += (_, _) => HandleHostAction(_shownStatus?.Primary ?? HomeAction.None);
        _hostSecondary.Click += (_, _) =>
        {
            if (_secondaryCancelsDownload)
            {
                _bridge.CancelUpdate();
                return;
            }
            HandleHostAction(_shownStatus?.Secondary ?? HomeAction.None);
        };

        _failureCard = view.FindViewById(Resource.Id.chats_failure_card)!;
        _failureTitle = view.FindViewById<TextView>(Resource.Id.chats_failure_title)!;
        _failureDetail = view.FindViewById<TextView>(Resource.Id.chats_failure_detail)!;

        _updateBanner = view.FindViewById(Resource.Id.chats_update_banner_card)!;
        _updateBannerTitle = view.FindViewById<TextView>(Resource.Id.chats_update_banner_title)!;
        _updateBannerDetail = view.FindViewById<TextView>(Resource.Id.chats_update_banner_detail)!;
        view.FindViewById<MaterialButton>(Resource.Id.chats_update_banner_details)!.Click += (_, _) =>
            StartActivity(new Intent(RequireContext(), typeof(RuntimeActivity)));
        view.FindViewById<MaterialButton>(Resource.Id.chats_update_banner_install)!.Click += (_, _) =>
        {
            if (_bannerPlan is { HasWork: true } plan && !_bridge.IsBusy)
            {
                UpdateUi.ShowConfirmInstallDialog(RequireContext(), _bridge, plan);
            }
        };

        _webInvCard = view.FindViewById(Resource.Id.chats_web_inventory_card)!;
        _webInvSummary = view.FindViewById<TextView>(Resource.Id.chats_web_inventory_summary)!;
        view.FindViewById<MaterialButton>(Resource.Id.chats_web_inventory_dismiss)!.Click += (_, _) =>
        {
            UiPrefs.SetWebInventoryDismissed(RequireContext(), true);
            _webInvCard.Visibility = ViewStates.Gone;
        };
        view.FindViewById<MaterialButton>(Resource.Id.chats_web_inventory_details)!.Click += (_, _) => ShowWebInventoryDetails();

        _searchLayout = view.FindViewById(Resource.Id.chats_search_layout)!;
        if (_searchLayout is TextInputLayout til)
        {
            til.Hint = GetString(Resource.String.chats_search_hint);
        }
        var search = _searchLayout.FindViewById<TextInputEditText>(Resource.Id.search_input)!;
        search.TextChanged += (_, e) => _vm.SetSearchQuery(e.Text?.ToString());

        _filterBar = view.FindViewById(Resource.Id.chats_filter_bar)!;
        var filterChips = view.FindViewById<ChipGroup>(Resource.Id.chats_filter_chips)!;
        _filterAll = view.FindViewById<Chip>(Resource.Id.chats_filter_all)!;
        _filterRunning = view.FindViewById<Chip>(Resource.Id.chats_filter_running)!;
        _filterPending = view.FindViewById<Chip>(Resource.Id.chats_filter_pending)!;
        filterChips.CheckedStateChange += (_, e) =>
        {
            var checkedId = filterChips.CheckedChipId;
            _filterMode = checkedId == Resource.Id.chats_filter_running
                ? SessionFilterMode.Running
                : checkedId == Resource.Id.chats_filter_pending
                    ? SessionFilterMode.Pending
                    : SessionFilterMode.All;
            Render(_vm.State);
        };

        _cachedSubheader = view.FindViewById(Resource.Id.chats_cached_subheader)!;

        _list = view.FindViewById<RecyclerView>(Resource.Id.chats_list)!;
        _list.SetLayoutManager(new LinearLayoutManager(RequireContext()));
        _adapter = new ChatsAdapter(
            item => OpenChatSession(item.SessionId),
            ShowSessionActionsSheet,
            item => _ = CancelSessionAsync(item.SessionId));
        _list.SetAdapter(_adapter);

        _empty = view.FindViewById(Resource.Id.chats_empty)!;
        _empty.FindViewById<ImageView>(Resource.Id.empty_icon)!.SetImageResource(Resource.Drawable.ic_chat);
        _emptyText = _empty.FindViewById<TextView>(Resource.Id.empty_text)!;
        _emptyText.SetText(Resource.String.chats_empty);

        _newFab = view.FindViewById<ExtendedFloatingActionButton>(Resource.Id.chats_new_fab)!;
        _newFab.Click += (_, _) => _ = CreateAndOpenSessionAsync();
        _list.AddOnScrollListener(new FabScrollListener(_newFab));

        return view;
    }

    public override void OnStart()
    {
        base.OnStart();
        _vm.StateChanged += OnStateChanged;
        _bridge.Changed += OnBridgeChanged;
        Render(_vm.State);
        _ = RefreshIfReadyAsync();
    }

    public override void OnStop()
    {
        _vm.StateChanged -= OnStateChanged;
        _bridge.Changed -= OnBridgeChanged;
        base.OnStop();
    }

    public override void OnHiddenChanged(bool hidden)
    {
        base.OnHiddenChanged(hidden);
        if (!hidden)
        {
            Render(_vm.State);
            _ = RefreshIfReadyAsync();
        }
    }

    public void OnReselected()
    {
        _list.SmoothScrollToPosition(0);
        _ = RefreshIfReadyAsync();
    }

    private void OnStateChanged(SessionsUiState _) => ScheduleRender();

    private void OnBridgeChanged(object? sender, EventArgs e)
    {
        if (_bridge.Runtime.Host?.State == DshHostState.Ready && !_vm.State.IsConnected && !_vm.State.IsLoading)
        {
            _ = RefreshIfReadyAsync();
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

    private void Render(SessionsUiState state)
    {
        var ctx = RequireContext();
        var hostReady = _bridge.Runtime.Host?.State == DshHostState.Ready;
        var status = _bridge.Status;
        _shownStatus = status;

        HostQuickControlBottomSheet.ApplyHostPillAppearance(ctx, _hostPill, status.Phase, appendCaret: true);
        _remotePill.Visibility = _bridge.Agent.IsRemoteEndpointReady ? ViewStates.Visible : ViewStates.Gone;

        _progress.Visibility = state.IsLoading ? ViewStates.Visible : ViewStates.Gone;
        _stopMenuItem?.SetVisible(hostReady && !_bridge.IsBusy);
        _newFab.Enabled = hostReady;
        if (hostReady)
        {
            _newFab.Show();
        }
        else
        {
            _newFab.Hide();
        }

        RenderHostLifecycleCard(ctx, status, hostReady);

        var cachedItems = state.EffectiveCachedSessions;
        var liveSessions = state.Sessions;
        var runningCount = liveSessions.Count(s => s.Running);
        var pendingTotal = liveSessions.Count(s => s.PendingRequestCount > 0);
        var showFilters = hostReady && (runningCount > 0 || pendingTotal > 0);
        _filterBar.Visibility = showFilters ? ViewStates.Visible : ViewStates.Gone;
        if (showFilters)
        {
            _filterRunning.Visibility = runningCount > 0 ? ViewStates.Visible : ViewStates.Gone;
            _filterPending.Visibility = pendingTotal > 0 ? ViewStates.Visible : ViewStates.Gone;
            if (pendingTotal > 0)
            {
                _filterPending.Text = Rows.Fmt(ctx, Resource.String.chats_filter_pending_fmt, pendingTotal);
            }
            if ((_filterMode == SessionFilterMode.Running && runningCount == 0)
                || (_filterMode == SessionFilterMode.Pending && pendingTotal == 0))
            {
                _filterMode = SessionFilterMode.All;
                _filterAll.Checked = true;
            }
        }
        else if (_filterMode != SessionFilterMode.All)
        {
            _filterMode = SessionFilterMode.All;
            _filterAll.Checked = true;
        }

        IReadOnlyList<SessionListItemView> displayed;
        var isCachedMode = !hostReady && cachedItems.Count > 0;
        if (isCachedMode)
        {
            displayed = cachedItems;
        }
        else if (hostReady && _filterMode == SessionFilterMode.Running)
        {
            displayed = liveSessions.Where(s => s.Running).ToArray();
        }
        else if (hostReady && _filterMode == SessionFilterMode.Pending)
        {
            displayed = liveSessions.Where(s => s.PendingRequestCount > 0).ToArray();
        }
        else
        {
            displayed = liveSessions;
        }

        var hasDisplayed = displayed.Count > 0;
        var hasQuery = !string.IsNullOrWhiteSpace(state.SearchQuery);
        _searchLayout.Visibility = hostReady || hasDisplayed || hasQuery ? ViewStates.Visible : ViewStates.Gone;
        _cachedSubheader.Visibility = isCachedMode ? ViewStates.Visible : ViewStates.Gone;

        _adapter.Submit(displayed, state.ActiveSessionId, state.CanCancelSession, isCachedOffline: isCachedMode);
        if (!hasDisplayed && (hostReady || hasQuery))
        {
            _emptyText.SetText(hasQuery ? Resource.String.chats_empty_search : Resource.String.chats_empty);
            _empty.Visibility = ViewStates.Visible;
        }
        else
        {
            _empty.Visibility = ViewStates.Gone;
        }

        RenderWebInventoryCard();
    }

    private void RenderHostLifecycleCard(Context ctx, HomeStatus status, bool hostReady)
    {
        var busy = status.Phase is HomePhase.Starting or HomePhase.WaitingForDsh or HomePhase.Stopping or HomePhase.Busy;
        _hostBanner.Visibility = hostReady && status.Phase == HomePhase.Ready ? ViewStates.Gone : ViewStates.Visible;
        if (_hostBanner.Visibility == ViewStates.Visible)
        {
            var (titleText, detailText) = DescribeHostStatus(ctx, status);
            _hostTitle.Text = titleText;
            _hostText.Text = detailText;
            _hostProgress.Visibility = busy ? ViewStates.Visible : ViewStates.Gone;
            if (busy && _bridge.Progress is { Total: > 0 } p)
            {
                _hostProgress.Indeterminate = false;
                _hostProgress.SetProgressCompat((int)Math.Clamp(p.Done * 100 / p.Total, 0, 100), true);
            }
            else if (busy && !_hostProgress.Indeterminate)
            {
                _hostProgress.Indeterminate = true;
            }

            BindActionButton(_hostAction, status.Primary, status.Phase);
            _secondaryCancelsDownload = busy
                && _bridge.CurrentOperation == HomeStatus.UpdateInstallOperation
                && _bridge.Progress is { Step: UpdatePhases.Downloading };
            if (_secondaryCancelsDownload)
            {
                _hostSecondary.Visibility = ViewStates.Visible;
                _hostSecondary.SetText(Resource.String.update_cancel);
                _hostSecondary.SetIconResource(Resource.Drawable.ic_close);
            }
            else
            {
                BindActionButton(_hostSecondary, status.Secondary, status.Phase);
            }
        }
        else
        {
            _secondaryCancelsDownload = false;
        }

        if (status.FailedOperation is { } f)
        {
            _failureCard.Visibility = ViewStates.Visible;
            _failureTitle.Text = Rows.Fmt(ctx, Resource.String.home_failed_operation, OperationNames.Label(ctx, f.Operation));
            _failureDetail.Text = OperationNames.Error(f.Code, f.Message);
        }
        else
        {
            _failureCard.Visibility = ViewStates.Gone;
        }

        var channel = UiPrefs.UpdateChannel(ctx);
        if (!busy
            && status.Phase is not HomePhase.NotInstalled
            && status.DshVersion is not null
            && _bridge.LastCheck is { Ok: true } check
            && _bridge.LastCheckChannel == channel)
        {
            var plan = UpdatePlanner.Plan(check.Choices);
            if (plan.HasWork)
            {
                _bannerPlan = plan;
                _updateBanner.Visibility = ViewStates.Visible;
                _updateBannerTitle.Text = Rows.Fmt(ctx, Resource.String.home_update_banner_title, UpdateUi.DescribePlan(plan));
                _updateBannerDetail.Text = Rows.Fmt(ctx, Resource.String.update_install_plan_sub, UpdateUi.DigestHint(plan), UpdateUi.TotalMiB(plan));
                return;
            }
        }
        _bannerPlan = null;
        _updateBanner.Visibility = ViewStates.Gone;
    }

    private (string Title, string Detail) DescribeHostStatus(Context ctx, HomeStatus status)
    {
        var version = status.DshVersion;
        return status.Phase switch
        {
            HomePhase.NotInstalled => (GetString(Resource.String.home_title_not_installed), GetString(Resource.String.home_detail_not_installed)),
            HomePhase.Stopped => (
                GetString(Resource.String.home_title_stopped),
                version is null ? GetString(Resource.String.home_detail_stopped) : GetString(Resource.String.chats_host_stopped)),
            HomePhase.Starting or HomePhase.WaitingForDsh => (
                GetString(Resource.String.home_title_starting),
                StartingPhaseDetail()),
            HomePhase.Stopping => (GetString(Resource.String.home_title_stopping), GetString(Resource.String.home_detail_stopping)),
            HomePhase.Busy => (
                Rows.Fmt(ctx, Resource.String.home_title_busy, OperationNames.Label(ctx, status.Operation ?? "")),
                _bridge.Progress is { } p
                    ? (p.Total > 0 ? Rows.Fmt(ctx, Resource.String.home_detail_progress, p.Step, (int)Math.Clamp(p.Done * 100 / p.Total, 0, 100)) : p.Step)
                    : GetString(Resource.String.home_detail_busy)),
            HomePhase.Ready => (
                GetString(Resource.String.home_title_ready),
                Rows.Fmt(ctx, Resource.String.home_detail_ready, version ?? "?")),
            _ => (
                GetString(Resource.String.home_title_failed),
                OperationNames.Error(status.ErrorCode ?? "?", status.ErrorMessage)),
        };
    }

    private string StartingPhaseDetail() => _bridge.HostPhase switch
    {
        DshHostPhase.SandboxStarting => GetString(Resource.String.home_detail_phase_sandbox),
        DshHostPhase.SandboxReady => GetString(Resource.String.home_detail_phase_sandbox_ready),
        DshHostPhase.DshStarting => GetString(Resource.String.home_detail_phase_dsh_spawned),
        DshHostPhase.DshFirstOutput => GetString(Resource.String.home_detail_phase_first_output),
        DshHostPhase.DshTokenAvailable => GetString(Resource.String.home_detail_phase_token),
        DshHostPhase.ProtocolConnecting => GetString(Resource.String.home_detail_phase_protocol),
        DshHostPhase.Initializing => GetString(Resource.String.home_detail_phase_initializing),
        _ => GetString(Resource.String.home_detail_starting),
    };

    private static void BindActionButton(MaterialButton button, HomeAction action, HomePhase phase)
    {
        if (action == HomeAction.None)
        {
            button.Visibility = ViewStates.Gone;
            return;
        }
        button.Visibility = ViewStates.Visible;
        var (textRes, iconRes) = action switch
        {
            HomeAction.OnlineInstall => phase switch
            {
                HomePhase.NotInstalled => (Resource.String.home_action_online_setup, Resource.Drawable.ic_download),
                HomePhase.Failed => (Resource.String.home_action_online_retry, Resource.Drawable.ic_download),
                _ => (Resource.String.home_action_online_dsh, Resource.Drawable.ic_download),
            },
            HomeAction.ImportRuntime => (Resource.String.home_action_import, Resource.Drawable.ic_upload),
            HomeAction.ImportDsh => (Resource.String.home_action_import_dsh, Resource.Drawable.ic_upload),
            HomeAction.Start => (phase == HomePhase.Failed ? Resource.String.home_action_retry : Resource.String.home_action_start, Resource.Drawable.ic_play),
            HomeAction.OpenChat => (Resource.String.home_action_open_chat, Resource.Drawable.ic_chat),
            _ => (Resource.String.home_action_stop, Resource.Drawable.ic_stop),
        };
        button.SetText(textRes);
        button.SetIconResource(iconRes);
    }

    private void HandleHostAction(HomeAction action)
    {
        switch (action)
        {
            case HomeAction.OnlineInstall:
                _ = StartOnlineInstallFlowAsync();
                break;
            case HomeAction.ImportRuntime:
                StartActivityForResult(RuntimeImportFlow.PickIntent(), RuntimeImportFlow.RequestCode);
                break;
            case HomeAction.ImportDsh:
                StartActivityForResult(RuntimeImportFlow.PickDshIntent(), RuntimeImportFlow.DshRequestCode);
                break;
            case HomeAction.Start:
                _ = StartDshAsync();
                break;
            case HomeAction.OpenChat:
                StartActivity(new Intent(RequireContext(), typeof(ChatActivity)));
                break;
            case HomeAction.Stop:
                _ = _bridge.RunStopAsync();
                break;
        }
    }

    private async Task StartDshAsync()
    {
        var decision = LocalNetworkDecision.NotRequired;
        if (Activity is { } activity && !UiPrefs.LanPermissionAsked(activity))
        {
            UiPrefs.SetLanPermissionAsked(activity, true);
            try
            {
                decision = await LocalNetworkAccess.EnsureAsync(activity).ConfigureAwait(true);
            }
            catch (Exception e)
            {
                Android.Util.Log.Warn("DshMobile", "lan permission gate failed: " + e.GetType().Name);
            }
        }
        if (decision == LocalNetworkDecision.NeedsPermission && Context is { } ctx)
        {
            Android.Widget.Toast.MakeText(ctx, GetString(Resource.String.lan_denied_toast), ToastLength.Long)?.Show();
        }
        await _bridge.RunStartAsync().ConfigureAwait(false);
    }

    private async Task StartOnlineInstallFlowAsync()
    {
        var context = Context;
        if (context is null)
        {
            return;
        }
        var channel = UiPrefs.UpdateChannel(context);
        var check = await _bridge.RunUpdateCheckForResultAsync(channel).ConfigureAwait(false);
        Activity?.RunOnUiThread(() =>
        {
            if (View is null || Context is not { } ctx || check is not { Ok: true })
            {
                return;
            }
            var plan = UpdatePlanner.Plan(check.Choices);
            if (!plan.HasWork)
            {
                var detail = plan.Notes.Count > 0
                    ? string.Join('\n', plan.Notes)
                    : UpdateTransactionCodes.NothingToInstall;
                new MaterialAlertDialogBuilder(ctx)
                    .SetIcon(Resource.Drawable.ic_info)!
                    .SetTitle(Resource.String.update_nothing_title)!
                    .SetMessage(detail)!
                    .SetPositiveButton(Android.Resource.String.Ok, (_, _) => { })!
                    .Show();
                return;
            }
            UpdateUi.ShowConfirmInstallDialog(ctx, _bridge, plan);
        });
    }

    public override void OnActivityResult(int requestCode, int resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (resultCode != (int)Android.App.Result.Ok || data?.Data is not { } uri)
        {
            return;
        }
        if (requestCode == RuntimeImportFlow.RequestCode)
        {
            _ = RuntimeImportFlow.ImportAsync(uri);
        }
        else if (requestCode == RuntimeImportFlow.DshRequestCode)
        {
            _ = RuntimeImportFlow.ImportDshAsync(uri);
        }
    }

    private void RenderWebInventoryCard()
    {
        var report = Volatile.Read(ref _lastWebInventory);
        if (report is null ||
            !report.HasUserDshDirectory ||
            (report.DiscoveredSessions.Count == 0 && !report.HasCredentialsFile) ||
            UiPrefs.WebInventoryDismissed(RequireContext()))
        {
            _webInvCard.Visibility = ViewStates.Gone;
            return;
        }
        _webInvCard.Visibility = ViewStates.Visible;
        var resId = report.HasCredentialsFile ? Resource.String.web_inv_summary_creds : Resource.String.web_inv_summary;
        _webInvSummary.Text = Rows.Fmt(RequireContext(), resId, report.RecoverableCount, report.UnrecognizedOrLegacyCount);
    }

    private async Task RefreshIfReadyAsync()
    {
        if (_bridge.Runtime.Host?.State == DshHostState.Ready)
        {
            try
            {
                await _vm.RefreshAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        else
        {
            try
            {
                await _vm.LoadCachedSessionsAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        if (Interlocked.Exchange(ref _inventoryChecked, 1) == 0 || _bridge.Runtime.Host?.State == DshHostState.Ready)
        {
            var report = await Task.Run(() => _bridge.Agent.InspectWebProfileReadOnly()).ConfigureAwait(false);
            Volatile.Write(ref _lastWebInventory, report);
            ScheduleRender();
        }
    }

    private void OpenHostQuickControl()
    {
        var ctx = RequireContext();
        var status = _bridge.Status;
        var (titleText, detailText) = DescribeHostStatus(ctx, status);
        HostQuickControlBottomSheet.Show(
            ctx,
            _bridge,
            status,
            titleText,
            detailText,
            HandleHostAction,
            OpenRemoteChatOrSettings,
            () => StartActivity(new Intent(ctx, typeof(RuntimeActivity))),
            () => StartActivity(new Intent(ctx, typeof(Credentials.CredentialsActivity))));
    }

    private void OpenRemoteChatOrSettings()
    {
        var ctx = RequireContext();
        if (_bridge.Agent.IsRemoteEndpointReady)
        {
            var intent = new Intent(ctx, typeof(ChatActivity));
            intent.PutExtra(ChatActivity.ExtraIsRemote, true);
            StartActivity(intent);
            return;
        }
        StartActivity(new Intent(ctx, typeof(RemoteActivity)));
    }

    private void ShowSessionActionsSheet(SessionListItemView item)
    {
        var ctx = RequireContext();
        var hostReady = _bridge.Runtime.Host?.State == DshHostState.Ready;
        SessionActionsBottomSheet.Show(
            ctx,
            item,
            hostReady,
            _vm.State.CanCancelSession,
            () => OpenChatSession(item.SessionId),
            () => ShowRenameDialog(item),
            () => _ = ForkAndOpenAsync(item.SessionId),
            () => _ = CancelSessionAsync(item.SessionId));
    }

    private void OpenChatSession(string sessionId)
    {
        var intent = new Intent(RequireContext(), typeof(ChatActivity));
        intent.PutExtra(ChatActivity.ExtraSessionId, sessionId);
        StartActivity(intent);
    }

    private async Task CreateAndOpenSessionAsync()
    {
        var created = await _vm.CreateSessionAsync().ConfigureAwait(false);
        Activity?.RunOnUiThread(() =>
        {
            if (View is null)
            {
                return;
            }
            if (created.IsOk)
            {
                OpenChatSession(created.Value!);
            }
            else if (created.Error is { } err)
            {
                Snackbar.Make(View, Rows.Fmt(RequireContext(), Resource.String.chat_error_banner, err.DiagnosticKey ?? err.Code.ToString()), Snackbar.LengthShort).Show();
            }
        });
    }

    private void ShowRenameDialog(SessionListItemView item)
    {
        var ctx = RequireContext();
        var content = LayoutInflater.From(ctx)!.Inflate(Resource.Layout.dialog_text_input, null)!;
        var layout = content.FindViewById<TextInputLayout>(Resource.Id.dialog_text_input_layout)!;
        layout.Hint = GetString(Resource.String.chats_rename_hint);
        var input = content.FindViewById<TextInputEditText>(Resource.Id.dialog_text_input)!;
        input.Text = ChatDisplayFormatter.FormatShortSessionTitle(
            item.SessionId,
            item.DisplayTitle,
            ctx.GetString(Resource.String.chats_untitled_prefix));
        input.SetSelection(input.Text?.Length ?? 0);

        new MaterialAlertDialogBuilder(ctx)
            .SetTitle(Resource.String.chats_rename_title)!
            .SetView(content)!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(Resource.String.chats_rename_save, (_, _) =>
            {
                var nextTitle = input.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(nextTitle))
                {
                    _ = RenameSessionAndSyncAsync(item.SessionId, nextTitle);
                }
            })!
            .Show();
    }

    private async Task RenameSessionAndSyncAsync(string sessionId, string title)
    {
        var res = await _vm.RenameSessionAsync(sessionId, title).ConfigureAwait(false);
        if (res.IsOk && _bridge.Agent.Chat.State.ActiveSession.SessionId == sessionId)
        {
            await _bridge.Agent.Chat.RefreshHistoryAsync().ConfigureAwait(false);
        }
    }

    private async Task ForkAndOpenAsync(string sessionId)
    {
        var forked = await _vm.ForkSessionAsync(sessionId).ConfigureAwait(false);
        Activity?.RunOnUiThread(() =>
        {
            if (View is not null && forked.IsOk)
            {
                OpenChatSession(forked.Value!);
            }
        });
    }

    private async Task CancelSessionAsync(string sessionId)
    {
        var res = await _vm.CancelSessionAsync(sessionId).ConfigureAwait(false);
        Activity?.RunOnUiThread(() =>
        {
            if (View is { } v && !res.IsOk)
            {
                var msgRes = res.Error?.Code == DshFailureCode.Unsupported
                    ? Resource.String.chats_cancel_unsupported
                    : Resource.String.chat_error_banner;
                var msg = msgRes == Resource.String.chat_error_banner
                    ? Rows.Fmt(RequireContext(), msgRes, res.Error?.Code.ToString() ?? "ERROR")
                    : GetString(msgRes);
                Snackbar.Make(v, msg, Snackbar.LengthShort).Show();
            }
        });
    }

    private void ShowWebInventoryDetails()
    {
        var report = Volatile.Read(ref _lastWebInventory);
        if (report is null)
        {
            return;
        }
        var lines = new List<string> { GetString(Resource.String.web_inv_dialog_intro), string.Empty };
        foreach (var entry in report.DiscoveredSessions.Take(15))
        {
            var tag = entry.IsKnownToRemote ? "可直开" : "需迁移/只读保留";
            lines.Add($"• {entry.SessionId} [{tag}]");
        }
        if (report.DiscoveredSessions.Count > 15)
        {
            lines.Add($"… 其余 {report.DiscoveredSessions.Count - 15} 项");
        }

        new MaterialAlertDialogBuilder(RequireContext())
            .SetTitle(Resource.String.web_inv_dialog_title)!
            .SetMessage(string.Join('\n', lines))!
            .SetPositiveButton(Android.Resource.String.Ok, (_, _) => { })!
            .Show();
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
