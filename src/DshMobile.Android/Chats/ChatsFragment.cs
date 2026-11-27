using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Presentation;
using DshMobile.App.Bridge;
using DshMobile.App.Chat;
using DshMobile.App.Inbox;
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
using Google.Android.Material.ProgressIndicator;
using Google.Android.Material.Snackbar;
using Google.Android.Material.TextField;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace DshMobile.App.Chats;

/// <summary>
/// 原生会话列表页签：
/// 承载贴顶工作台顶栏（常驻新建对话、搜索与收件箱）、宿主状态标签与四态筛选芯片（全部 / 运行中 / 待处理 / 已归档）、
/// 1dp 结构卡片与操作抽屉（含归档、取消归档与删除）、离线缓存会话管理与就地宿主快速控制。
/// </summary>
public sealed class ChatsFragment : Fragment, ITabRoot, SessionActionsBottomSheet.IHost, HostQuickControlBottomSheet.IHost
{
    private BridgeHost _bridge = null!;
    private SessionsViewModel _vm = null!;
    private ChatsAdapter _adapter = null!;
    private LinearProgressIndicator _progress = null!;
    private TextView _hostPill = null!;
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
    private IMenuItem? _newMenuItem;
    private IMenuItem? _stopMenuItem;
    private IMenuItem? _inboxMenuItem;
    private View _webInvCard = null!;
    private TextView _webInvSummary = null!;
    private View _searchLayout = null!;
    private TextInputEditText _searchInput = null!;
    private bool _searchExpanded;
    private View _filterBar = null!;
    private ChipGroup _filterChips = null!;
    private Chip _filterAll = null!;
    private Chip _filterRunning = null!;
    private Chip _filterPending = null!;
    private Chip _filterArchived = null!;
    private bool _syncingFilterChips;
    private View _cachedSubheader = null!;
    private RecyclerView _list = null!;
    private View _empty = null!;
    private TextView _emptyText = null!;
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
        _newMenuItem = toolbar.Menu?.FindItem(Resource.Id.chats_menu_new);
        _newMenuItem?.SetVisible(false);
        _stopMenuItem = toolbar.Menu?.FindItem(Resource.Id.chats_menu_stop_dsh);
        _stopMenuItem?.SetVisible(false);
        _inboxMenuItem = toolbar.Menu?.FindItem(Resource.Id.chats_menu_inbox);
        _inboxMenuItem?.SetVisible(false);
        toolbar.MenuItemClick += (_, e) =>
        {
            var id = e.Item?.ItemId ?? 0;
            if (id == Resource.Id.chats_menu_new)
            {
                _ = CreateAndOpenSessionAsync();
            }
            else if (id == Resource.Id.chats_menu_search)
            {
                ToggleSearch();
            }
            else if (id == Resource.Id.chats_menu_inbox)
            {
                OpenInboxSheet();
            }
            else if (id == Resource.Id.chats_menu_refresh)
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
            // 关闭后立即重算优先级状态槽，让低优先级卡片或列表恢复首屏空间。
            ScheduleRender();
        };
        view.FindViewById<MaterialButton>(Resource.Id.chats_web_inventory_details)!.Click += (_, _) => ShowWebInventoryDetails();

        _searchLayout = view.FindViewById(Resource.Id.chats_search_layout)!;
        _searchLayout.Visibility = ViewStates.Gone;
        _searchInput = _searchLayout.FindViewById<TextInputEditText>(Resource.Id.search_input)!;
        _searchInput.Hint = GetString(Resource.String.chats_search_hint);
        _searchInput.TextChanged += (_, e) => _vm.SetSearchQuery(e.Text?.ToString());
        _searchInput.EditorAction += (_, e) =>
        {
            if (e.ActionId == Android.Views.InputMethods.ImeAction.Search)
            {
                _searchInput.ClearFocus();
                HideKeyboard();
                e.Handled = true;
            }
        };

        _filterBar = view.FindViewById(Resource.Id.chats_filter_bar)!;
        _filterChips = view.FindViewById<ChipGroup>(Resource.Id.chats_filter_chips)!;
        _filterAll = view.FindViewById<Chip>(Resource.Id.chats_filter_all)!;
        _filterRunning = view.FindViewById<Chip>(Resource.Id.chats_filter_running)!;
        _filterPending = view.FindViewById<Chip>(Resource.Id.chats_filter_pending)!;
        _filterArchived = view.FindViewById<Chip>(Resource.Id.chats_filter_archived)!;
        _filterChips.CheckedStateChange += (_, _) =>
        {
            if (_syncingFilterChips)
            {
                return;
            }
            var checkedId = _filterChips.CheckedChipId;
            var nextMode = checkedId == Resource.Id.chats_filter_running
                ? SessionFilterMode.Running
                : checkedId == Resource.Id.chats_filter_pending
                    ? SessionFilterMode.Pending
                    : checkedId == Resource.Id.chats_filter_archived
                        ? SessionFilterMode.Archived
                        : SessionFilterMode.All;
            _vm.SetFilterMode(nextMode);
        };

        _cachedSubheader = view.FindViewById(Resource.Id.chats_cached_subheader)!;

        _list = view.FindViewById<RecyclerView>(Resource.Id.chats_list)!;
        _list.SetLayoutManager(new LinearLayoutManager(RequireContext()));
        _adapter = new ChatsAdapter(
            RequireContext(),
            item => OpenChatSession(item.SessionId),
            ShowSessionActionsSheet,
            item => _ = ApprovePendingAsync(item),
            item => _ = RejectPendingAsync(item),
            OpenPendingChat,
            OpenInboxSheet);
        _list.SetAdapter(_adapter);
        new ItemTouchHelper(new SwipeToActionsCallback(_adapter, ShowSessionActionsSheet)).AttachToRecyclerView(_list);

        _empty = view.FindViewById(Resource.Id.chats_empty)!;
        _empty.FindViewById<ImageView>(Resource.Id.empty_icon)!.SetImageResource(Resource.Drawable.ic_chat);
        _emptyText = _empty.FindViewById<TextView>(Resource.Id.empty_text)!;
        _emptyText.SetText(Resource.String.chats_empty);

        Edges.ClampMaxWidth(_list, _filterBar, _cachedSubheader);
        Edges.ClampMaxWidthByMargin(_hostBanner, _failureCard, _updateBanner, _webInvCard, _searchLayout);

        return view;
    }

    public override void OnStart()
    {
        base.OnStart();
        _vm.StateChanged += OnStateChanged;
        _bridge.Agent.Inbox.StateChanged += OnInboxStateChanged;
        _bridge.Changed += OnBridgeChanged;
        Render(_vm.State);
        _ = RefreshIfReadyAsync();
    }

    public override void OnStop()
    {
        _vm.StateChanged -= OnStateChanged;
        _bridge.Agent.Inbox.StateChanged -= OnInboxStateChanged;
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

    private void OnInboxStateChanged(InboxUiState _) => ScheduleRender();

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

        var remoteLabel = _bridge.Agent.IsRemoteEndpointReady ? _bridge.Agent.RemoteHostLabel : null;
        HostQuickControlBottomSheet.ApplyHostPillAppearance(
            ctx,
            _hostPill,
            status.Phase,
            appendCaret: true,
            remoteLabel: remoteLabel);

        _progress.Visibility = state.IsLoading ? ViewStates.Visible : ViewStates.Gone;
        _newMenuItem?.SetVisible(hostReady);
        _newMenuItem?.SetEnabled(hostReady);
        _stopMenuItem?.SetVisible(hostReady && !_bridge.IsBusy);
        var inboxItems = _bridge.Agent.Inbox.State.Items;
        var inboxTotal = _bridge.Agent.Inbox.State.TotalCount;
        _inboxMenuItem?.SetVisible(inboxTotal > 0);

        RenderStatusSlot(ctx, status, hostReady);

        var cachedItems = state.EffectiveCachedSessions;
        var liveSessions = state.Sessions;
        var pendingTotal = inboxItems
            .Select(i => i.SessionId)
            .Distinct(StringComparer.Ordinal)
            .Count();

        _filterBar.Visibility = ViewStates.Visible;
        _filterRunning.Visibility = ViewStates.Visible;
        _filterPending.Visibility = ViewStates.Visible;
        _filterPending.Text = pendingTotal > 0
            ? Rows.Fmt(ctx, Resource.String.chats_filter_pending_fmt, pendingTotal)
            : GetString(Resource.String.chats_filter_pending);

        var showArchivedChip = hostReady && state.CanArchiveSessions;
        _filterArchived.Visibility = showArchivedChip ? ViewStates.Visible : ViewStates.Gone;
        _filterArchived.Text = state.ArchivedCount > 0
            ? Rows.Fmt(ctx, Resource.String.chats_filter_archived_fmt, state.ArchivedCount)
            : GetString(Resource.String.chats_filter_archived);

        var activeFilter = state.FilterMode;
        if (!showArchivedChip && activeFilter == SessionFilterMode.Archived)
        {
            activeFilter = SessionFilterMode.All;
            _vm.SetFilterMode(SessionFilterMode.All);
        }
        SyncFilterChipSelection(activeFilter);

        var isCachedMode = !hostReady && cachedItems.Count > 0;
        var displayed = isCachedMode ? cachedItems : liveSessions;

        var hasDisplayed = displayed.Count > 0;
        var hasQuery = !string.IsNullOrWhiteSpace(state.SearchQuery);
        _searchLayout.Visibility = _searchExpanded || hasQuery ? ViewStates.Visible : ViewStates.Gone;
        _cachedSubheader.Visibility = isCachedMode ? ViewStates.Visible : ViewStates.Gone;

        IReadOnlyList<InboxItemView> pendingItems = hostReady && activeFilter != SessionFilterMode.Archived
            ? inboxItems
            : [];
        _adapter.Submit(displayed, pendingItems, state.ActiveSessionId, isCachedOffline: isCachedMode);
        var hasListContent = hasDisplayed || pendingItems.Count > 0;
        if (!hasListContent && (hostReady || hasQuery))
        {
            var emptyRes = hasQuery
                ? Resource.String.chats_empty_search
                : activeFilter switch
                {
                    SessionFilterMode.Archived => Resource.String.chats_empty_archived,
                    SessionFilterMode.Running => Resource.String.chats_empty_running,
                    SessionFilterMode.Pending => Resource.String.chats_empty_pending,
                    _ => Resource.String.chats_empty,
                };
            _emptyText.SetText(emptyRes);
            _empty.Visibility = ViewStates.Visible;
        }
        else
        {
            _empty.Visibility = ViewStates.Gone;
        }
    }

    private void SyncFilterChipSelection(SessionFilterMode mode)
    {
        var wantId = mode switch
        {
            SessionFilterMode.Running => Resource.Id.chats_filter_running,
            SessionFilterMode.Pending => Resource.Id.chats_filter_pending,
            SessionFilterMode.Archived => Resource.Id.chats_filter_archived,
            _ => Resource.Id.chats_filter_all,
        };
        if (_filterChips.CheckedChipId == wantId)
        {
            return;
        }
        _syncingFilterChips = true;
        try
        {
            _filterChips.Check(wantId);
        }
        finally
        {
            _syncingFilterChips = false;
        }
    }

    /// <summary>
    /// 唯一优先级状态槽（UI-S4）：失败 &gt; 宿主未就绪/启动中 &gt; 更新 &gt; Web 盘点。
    /// 四张候选卡同一时刻最多只显示一张，避免多横幅同时挤占首屏。
    /// </summary>
    private void RenderStatusSlot(Context ctx, HomeStatus status, bool hostReady)
    {
        var busy = status.Phase is HomePhase.Starting or HomePhase.WaitingForDsh or HomePhase.Stopping or HomePhase.Busy;
        var hostCandidate = !(hostReady && status.Phase == HomePhase.Ready);
        if (hostCandidate)
        {
            var (titleText, detailText) = HostQuickControlSupport.Describe(ctx, _bridge, status);
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

        var failureCandidate = false;
        if (status.FailedOperation is { } failedOperation)
        {
            failureCandidate = true;
            _failureTitle.Text = Rows.Fmt(ctx, Resource.String.home_failed_operation, OperationNames.Label(ctx, failedOperation.Operation));
            _failureDetail.Text = OperationNames.Error(failedOperation.Code, failedOperation.Message);
        }

        var updateCandidate = false;
        _bannerPlan = null;
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
                updateCandidate = true;
                _updateBannerTitle.Text = Rows.Fmt(ctx, Resource.String.home_update_banner_title, UpdateUi.DescribePlan(plan));
                _updateBannerDetail.Text = Rows.Fmt(ctx, Resource.String.update_install_plan_sub, UpdateUi.DigestHint(plan), UpdateUi.TotalMiB(plan));
            }
        }

        var inventoryCandidate = false;
        var report = Volatile.Read(ref _lastWebInventory);
        if (report is not null
            && report.HasUserDshDirectory
            && (report.DiscoveredSessions.Count > 0 || report.HasCredentialsFile)
            && !UiPrefs.WebInventoryDismissed(ctx))
        {
            inventoryCandidate = true;
            var resId = report.HasCredentialsFile ? Resource.String.web_inv_summary_creds : Resource.String.web_inv_summary;
            _webInvSummary.Text = Rows.Fmt(ctx, resId, report.RecoverableCount, report.UnrecognizedOrLegacyCount);
        }

        var active = failureCandidate
            ? _failureCard
            : hostCandidate
                ? _hostBanner
                : updateCandidate
                    ? _updateBanner
                    : inventoryCandidate
                        ? _webInvCard
                        : null;
        ApplyStatusSlotVisibility(active);
    }

    private void ApplyStatusSlotVisibility(View? active)
    {
        _failureCard.Visibility = ReferenceEquals(active, _failureCard) ? ViewStates.Visible : ViewStates.Gone;
        _hostBanner.Visibility = ReferenceEquals(active, _hostBanner) ? ViewStates.Visible : ViewStates.Gone;
        _updateBanner.Visibility = ReferenceEquals(active, _updateBanner) ? ViewStates.Visible : ViewStates.Gone;
        _webInvCard.Visibility = ReferenceEquals(active, _webInvCard) ? ViewStates.Visible : ViewStates.Gone;
    }

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

    private void HandleHostAction(HomeAction action) => HostQuickControlSupport.Run(this, _bridge, action);

    public void OnHostQuickAction(HomeAction action) => HandleHostAction(action);

    public void OnHostQuickOpenRemote() => OpenRemoteChatOrSettings();

    public void OnHostQuickOpenRuntime() =>
        StartActivity(new Intent(RequireContext(), typeof(RuntimeActivity)));

    public void OnHostQuickOpenCredentials() =>
        StartActivity(new Intent(RequireContext(), typeof(Credentials.CredentialsActivity)));

    public void OnHostQuickCancelUpdate() => _bridge.CancelUpdate();

    public void OnSessionAction(SessionActionsBottomSheet.Action action, string sessionId)
    {
        switch (action)
        {
            case SessionActionsBottomSheet.Action.Open:
                OpenChatSession(sessionId);
                break;
            case SessionActionsBottomSheet.Action.Rename:
                ShowRenameDialog(sessionId, FindSession(sessionId)?.DisplayTitle);
                break;
            case SessionActionsBottomSheet.Action.Fork:
                _ = ForkAndOpenAsync(sessionId);
                break;
            case SessionActionsBottomSheet.Action.Cancel:
                _ = CancelSessionAsync(sessionId);
                break;
            case SessionActionsBottomSheet.Action.Archive:
                StartArchiveFlow(sessionId);
                break;
            case SessionActionsBottomSheet.Action.Unarchive:
                _ = UnarchiveSessionAsync(sessionId);
                break;
            case SessionActionsBottomSheet.Action.Remove:
                ConfirmDeleteSession(sessionId);
                break;
        }
    }

    private SessionListItemView? FindSession(string sessionId) =>
        _vm.State.Sessions.FirstOrDefault(s => string.Equals(s.SessionId, sessionId, StringComparison.Ordinal))
        ?? _vm.State.EffectiveCachedSessions.FirstOrDefault(s => string.Equals(s.SessionId, sessionId, StringComparison.Ordinal));

    [Obsolete("Uses Fragment activity result for runtime import parity")]
    public override void OnActivityResult(int requestCode, int resultCode, Intent? data)
    {
#pragma warning disable CS0618
        base.OnActivityResult(requestCode, resultCode, data);
#pragma warning restore CS0618
        HostQuickControlSupport.HandleActivityResult(_bridge, requestCode, (Android.App.Result)resultCode, data);
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
        var (titleText, detailText) = HostQuickControlSupport.Describe(ctx, _bridge, status);
        HostQuickControlBottomSheet.Show(ChildFragmentManager, titleText, detailText);
    }

    private void ToggleSearch()
    {
        _searchExpanded = !_searchExpanded;
        if (_searchExpanded)
        {
            _searchLayout.Visibility = ViewStates.Visible;
            _searchInput.RequestFocus();
            _searchInput.Post(() => ShowKeyboard());
        }
        else
        {
            _searchInput.Text = string.Empty;
            _vm.SetSearchQuery(null);
            _searchInput.ClearFocus();
            HideKeyboard();
        }
        Render(_vm.State);
    }

    private void ShowKeyboard()
    {
        if (RequireContext().GetSystemService(Context.InputMethodService) is Android.Views.InputMethods.InputMethodManager manager)
        {
            manager.ShowSoftInput(_searchInput, Android.Views.InputMethods.ShowFlags.Implicit);
        }
    }

    private void HideKeyboard()
    {
        if (RequireContext().GetSystemService(Context.InputMethodService) is Android.Views.InputMethods.InputMethodManager manager)
        {
            manager.HideSoftInputFromWindow(_searchInput.WindowToken, Android.Views.InputMethods.HideSoftInputFlags.None);
        }
    }

    private void OpenInboxSheet()
    {
        var manager = ParentFragmentManager;
        if (manager.FindFragmentByTag(InboxBottomSheet.SheetTag) is null)
        {
            new InboxBottomSheet().Show(manager, InboxBottomSheet.SheetTag);
        }
    }

    private void OpenPendingChat(InboxItemView item) => OpenChatSession(item.SessionId, item.EventId);

    private async Task ApprovePendingAsync(InboxItemView item)
    {
        var outcome = await _bridge.Agent.Inbox.ApproveOnceAsync(item.EventId).ConfigureAwait(false);
        ShowPendingDecisionResult(outcome);
    }

    private async Task RejectPendingAsync(InboxItemView item)
    {
        var outcome = await _bridge.Agent.Inbox.RejectApprovalAsync(item.EventId).ConfigureAwait(false);
        ShowPendingDecisionResult(outcome);
    }

    private void ShowPendingDecisionResult(Outcome<Unit> outcome)
    {
        Activity?.RunOnUiThread(() =>
        {
            if (View is not { } root)
            {
                return;
            }
            var message = outcome.IsOk
                ? GetString(Resource.String.chats_inline_done)
                : Rows.Fmt(
                    RequireContext(),
                    Resource.String.chats_inline_action_failed,
                    outcome.Error?.DiagnosticKey ?? outcome.Error?.Code.ToString() ?? "ERROR");
            Snackbar.Make(root, message, outcome.IsOk ? Snackbar.LengthShort : Snackbar.LengthLong).Show();
        });
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
        var state = _vm.State;
        var hostReady = _bridge.Runtime.Host?.State == DshHostState.Ready && state.IsConnected;
        var isCachedOnly = !hostReady;
        SessionActionsBottomSheet.Show(
            ChildFragmentManager,
            item.SessionId,
            item.DisplayTitle,
            item.Cwd,
            hostReady,
            state.CanCancelSession,
            item.Running,
            item.IsArchived,
            state.CanArchiveSessions,
            isCachedOnly,
            canRemove: true);
    }

    private void OpenChatSession(string sessionId) => OpenChatSession(sessionId, focusEventId: null);

    private void OpenChatSession(string sessionId, string? focusEventId)
    {
        // UI-S6：宽屏且停留在会话页签时，右栏内联加载 ChatFragment；窄屏保持沉浸式单栏体验。
        if (Activity is MainActivity main && main.IsWideChatsDualPane)
        {
            main.OpenChatInDetailPane(sessionId, isRemote: false, focusEventId);
            return;
        }

        var intent = new Intent(RequireContext(), typeof(ChatActivity));
        intent.PutExtra(ChatActivity.ExtraSessionId, sessionId);
        if (!string.IsNullOrWhiteSpace(focusEventId))
        {
            intent.PutExtra(ChatActivity.ExtraFocusEventId, focusEventId);
        }
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

    private void ShowRenameDialog(SessionListItemView item) => ShowRenameDialog(item.SessionId, item.DisplayTitle);

    private void ShowRenameDialog(string sessionId, string? displayTitle)
    {
        var ctx = RequireContext();
        var content = LayoutInflater.From(ctx)!.Inflate(Resource.Layout.dialog_text_input, null)!;
        var layout = content.FindViewById<TextInputLayout>(Resource.Id.dialog_text_input_layout)!;
        layout.Hint = GetString(Resource.String.chats_rename_hint);
        var input = content.FindViewById<TextInputEditText>(Resource.Id.dialog_text_input)!;
        input.Text = ChatDisplayFormatter.FormatShortSessionTitle(
            sessionId,
            displayTitle,
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
                    _ = RenameSessionAndSyncAsync(sessionId, nextTitle);
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

    private void StartArchiveFlow(string sessionId)
    {
        var item = FindSession(sessionId);
        if (item?.Running == true)
        {
            ShowStopAndArchiveDialog(sessionId);
            return;
        }
        _ = ArchiveSessionAsync(sessionId, stopActivity: false);
    }

    private void ShowStopAndArchiveDialog(string sessionId)
    {
        new MaterialAlertDialogBuilder(RequireContext())
            .SetTitle(Resource.String.chats_archive_active_title)!
            .SetMessage(Resource.String.chats_archive_active_msg)!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(Resource.String.chats_archive_active_confirm, (_, _) =>
                _ = ArchiveSessionAsync(sessionId, stopActivity: true))!
            .Show();
    }

    private async Task ArchiveSessionAsync(string sessionId, bool stopActivity)
    {
        var res = await _vm.ArchiveSessionAsync(sessionId, stopActivity).ConfigureAwait(false);
        Activity?.RunOnUiThread(() =>
        {
            if (View is not { } root)
            {
                return;
            }
            if (res.IsOk)
            {
                Snackbar.Make(root, Resource.String.chats_archive_done, Snackbar.LengthShort).Show();
                return;
            }
            if (!stopActivity && res.Error?.Code == DshFailureCode.SessionActive)
            {
                ShowStopAndArchiveDialog(sessionId);
                return;
            }
            Snackbar.Make(
                root,
                Rows.Fmt(RequireContext(), Resource.String.chat_error_banner, res.Error?.DiagnosticKey ?? res.Error?.Code.ToString() ?? "ERROR"),
                Snackbar.LengthShort).Show();
        });
    }

    private async Task UnarchiveSessionAsync(string sessionId)
    {
        var res = await _vm.UnarchiveSessionAsync(sessionId).ConfigureAwait(false);
        Activity?.RunOnUiThread(() =>
        {
            if (View is not { } root)
            {
                return;
            }
            if (res.IsOk)
            {
                Snackbar.Make(root, Resource.String.chats_unarchive_done, Snackbar.LengthShort).Show();
            }
            else
            {
                Snackbar.Make(
                    root,
                    Rows.Fmt(RequireContext(), Resource.String.chat_error_banner, res.Error?.DiagnosticKey ?? res.Error?.Code.ToString() ?? "ERROR"),
                    Snackbar.LengthShort).Show();
            }
        });
    }

    private void ConfirmDeleteSession(string sessionId)
    {
        var isCachedOnly = _bridge.Runtime.Host?.State != DshHostState.Ready || !_vm.State.IsConnected;
        var titleRes = isCachedOnly ? Resource.String.chats_remove_cached_title : Resource.String.chats_remove_title;
        var msgRes = isCachedOnly ? Resource.String.chats_remove_cached_msg : Resource.String.chats_remove_msg;
        var positiveRes = isCachedOnly ? Resource.String.chats_remove_cached_confirm : Resource.String.chats_remove_confirm;

        new MaterialAlertDialogBuilder(RequireContext())
            .SetTitle(titleRes)!
            .SetMessage(msgRes)!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(positiveRes, (_, _) =>
                _ = DeleteSessionAsync(sessionId, stopActivity: false, isCachedOnly))!
            .Show();
    }

    private void ShowStopAndDeleteDialog(string sessionId)
    {
        new MaterialAlertDialogBuilder(RequireContext())
            .SetTitle(Resource.String.chats_remove_active_title)!
            .SetMessage(Resource.String.chats_remove_active_msg)!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(Resource.String.chats_remove_active_confirm, (_, _) =>
                _ = DeleteSessionAsync(sessionId, stopActivity: true, isCachedOnly: false))!
            .Show();
    }

    private async Task DeleteSessionAsync(string sessionId, bool stopActivity, bool isCachedOnly)
    {
        var res = await _vm.DeleteSessionAsync(sessionId, stopActivity).ConfigureAwait(false);
        Activity?.RunOnUiThread(() =>
        {
            if (View is not { } root)
            {
                return;
            }
            if (res.IsOk)
            {
                Snackbar.Make(
                    root,
                    isCachedOnly ? Resource.String.chats_remove_cached_done : Resource.String.chats_remove_done,
                    Snackbar.LengthShort).Show();
                return;
            }
            if (!isCachedOnly && !stopActivity && res.Error?.Code == DshFailureCode.SessionActive)
            {
                ShowStopAndDeleteDialog(sessionId);
                return;
            }
            Snackbar.Make(
                root,
                Rows.Fmt(RequireContext(), Resource.String.chat_error_banner, res.Error?.DiagnosticKey ?? res.Error?.Code.ToString() ?? "ERROR"),
                Snackbar.LengthShort).Show();
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

    /// <summary>左滑快捷手势：仅真实会话行可滑动，滑出后立即恢复行并呼出操作抽屉。</summary>
    private sealed class SwipeToActionsCallback(
        ChatsAdapter adapter,
        Action<SessionListItemView> onMore) : ItemTouchHelper.SimpleCallback(0, ItemTouchHelper.Left)
    {
        public override bool OnMove(RecyclerView recyclerView, RecyclerView.ViewHolder viewHolder, RecyclerView.ViewHolder target) =>
            false;

        public override int GetSwipeDirs(RecyclerView recyclerView, RecyclerView.ViewHolder viewHolder) =>
            adapter.GetSessionAt(viewHolder.BindingAdapterPosition) is null
                ? 0
                : base.GetSwipeDirs(recyclerView, viewHolder);

        public override void OnSwiped(RecyclerView.ViewHolder viewHolder, int direction)
        {
            var position = viewHolder.BindingAdapterPosition;
            var item = adapter.GetSessionAt(position);
            adapter.NotifyItemChanged(position);
            if (item is not null)
            {
                onMore(item);
            }
        }
    }
}
