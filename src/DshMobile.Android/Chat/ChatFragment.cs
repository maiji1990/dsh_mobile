using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using AndroidX.Activity;
using AndroidX.Core.View;
using AndroidX.RecyclerView.Widget;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;
using DshMobile.Agent.Presentation;
using DshMobile.Agent.Rendering;
using DshMobile.App.Bridge;
using DshMobile.App.Credentials;
using DshMobile.App.Files;
using DshMobile.App.Inbox;
using DshMobile.App.Ui;
using DshMobile.Runtime.Hosting;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.ProgressIndicator;
using Google.Android.Material.Snackbar;
using Fragment = AndroidX.Fragment.App.Fragment;
using Log = Android.Util.Log;

namespace DshMobile.App.Chat;

/// <summary>
/// 纯原生 Material 3 聊天页（N-04-03 / N-04-04）：全部视图由 XML 布局加载，
/// 通过 <see cref="ChatViewModel"/> 驻留应用级 ConnectionScope，免疫旋转与深色切换。
/// </summary>
public sealed class ChatFragment : Fragment
{
    private const int RequestImagePick = 81;
    private const string DraftInputKey = "chat.composer.draft";
    private const string SlashExpandedKey = "chat.slash.expanded";

    private BridgeHost _bridge = null!;
    private ChatViewModel _vm = null!;
    private ChatTimelineAdapter _adapter = null!;
    private LinearLayoutManager _layoutManager = null!;
    private readonly MarkdownRenderCache _mdCache = new();
    private readonly List<PromptAttachmentImage> _attachments = [];

    private MaterialToolbar _toolbar = null!;
    private IMenuItem? _cancelMenuItem;
    private IMenuItem? _inboxMenuItem;
    private IMenuItem? _archiveMenuItem;
    private IMenuItem? _unarchiveMenuItem;
    private IMenuItem? _removeMenuItem;
    private View _archivedBanner = null!;
    private MaterialButton _archivedUnarchiveBtn = null!;
    private View _statusBanner = null!;
    private LinearProgressIndicator _statusProgress = null!;
    private TextView _statusText = null!;
    private MaterialButton _statusAction = null!;
    private View _unknownBanner = null!;
    private MaterialButton _unknownCheckBtn = null!;
    private MaterialButton _unknownResendBtn = null!;
    private MaterialButton _loadOlderBtn = null!;
    private RecyclerView _list = null!;
    private View _empty = null!;
    private View _jumpBottomBar = null!;
    private View _slashPanel = null!;
    private RecyclerView _slashList = null!;
    private View _slashEmpty = null!;
    private SlashCommandAdapter _slashAdapter = null!;
    private View _attachmentsBar = null!;
    private LinearLayout _attachmentsList = null!;
    private EditText _input = null!;
    private MaterialButton _modelPill = null!;
    private MaterialButton _sendBtn = null!;
    private MaterialButton _plusBtn = null!;
    private MaterialButton _trayImagesBtn = null!;
    private MaterialButton _traySlashBtn = null!;
    private View _composerControls = null!;
    private View _composerTray = null!;
    private ChatRequestSlotController _requestSlot = null!;
    private OnBackPressedCallback? _panelBackCallback;

    private bool _followBottom = true;
    private bool _slashExpanded;
    private bool _slashAutoFromInput;
    private bool _trayExpanded;
    private string _slashFilterQuery = string.Empty;
    private bool _isRemote;
    private bool _embeddedInPane;
    private bool _connectInitiated;
    private bool _cacheLoadInitiated;
    private bool _onboardingPrompted;
    private bool _openedCredentials;
    private string? _requestedSessionId;
    private long _sendGuardUntilMs;
    private bool _lastImeVisible;
    private string? _pendingFocusEventId;
    private int _renderPosted;
    private PromptSendRequest? _lastSubmittedRequest;

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        var view = inflater.Inflate(Resource.Layout.fragment_chat, container, false)!;
        _bridge = DshMobileApplication.Current.Bridge;
        _isRemote = Arguments?.GetBoolean(ChatActivity.ExtraIsRemote, false) ?? false;
        _embeddedInPane = Arguments?.GetBoolean(ChatActivity.ExtraEmbeddedInPane, false) ?? false;
        _vm = _isRemote ? (_bridge.Agent.RemoteChat ?? _bridge.Agent.Chat) : _bridge.Agent.Chat;
        _requestedSessionId = Arguments?.GetString(ChatActivity.ExtraSessionId);
        _pendingFocusEventId = Arguments?.GetString(ChatActivity.ExtraFocusEventId);

        _toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.chat_toolbar)!;
        _toolbar.NavigationClick += (_, _) =>
        {
            // 嵌入宽屏右栏时绝不调用 Activity.Finish()，否则会把整个 MainActivity 关掉。
            if (!_embeddedInPane)
            {
                Activity?.Finish();
            }
        };
        if (_embeddedInPane)
        {
            _toolbar.NavigationIcon = null;
        }
        _cancelMenuItem = _toolbar.Menu?.FindItem(Resource.Id.chat_menu_cancel);
        _cancelMenuItem?.SetVisible(false);
        _inboxMenuItem = _toolbar.Menu?.FindItem(Resource.Id.chat_menu_inbox);
        _inboxMenuItem?.SetVisible(false);
        _archiveMenuItem = _toolbar.Menu?.FindItem(Resource.Id.chat_menu_archive);
        _archiveMenuItem?.SetVisible(false);
        _unarchiveMenuItem = _toolbar.Menu?.FindItem(Resource.Id.chat_menu_unarchive);
        _unarchiveMenuItem?.SetVisible(false);
        _removeMenuItem = _toolbar.Menu?.FindItem(Resource.Id.chat_menu_remove);
        _removeMenuItem?.SetVisible(false);
        _toolbar.MenuItemClick += (_, e) => OnMenuClicked(e.Item?.ItemId ?? 0);
        _toolbar.Click += (_, _) => OnSessionHeaderClicked();

        _archivedBanner = view.FindViewById(Resource.Id.chat_archived_banner)!;
        _archivedUnarchiveBtn = view.FindViewById<MaterialButton>(Resource.Id.chat_archived_unarchive_btn)!;
        _archivedUnarchiveBtn.Click += (_, _) => _ = UnarchiveCurrentSessionFlowAsync();

        _statusBanner = view.FindViewById(Resource.Id.chat_status_banner)!;
        _statusProgress = view.FindViewById<LinearProgressIndicator>(Resource.Id.chat_status_progress)!;
        _statusText = view.FindViewById<TextView>(Resource.Id.chat_status_text)!;
        _statusAction = view.FindViewById<MaterialButton>(Resource.Id.chat_status_action)!;
        _statusAction.Click += (_, _) => OnStatusActionClicked();

        _unknownBanner = view.FindViewById(Resource.Id.chat_unknown_send_banner)!;
        _unknownCheckBtn = view.FindViewById<MaterialButton>(Resource.Id.chat_unknown_check_btn)!;
        _unknownResendBtn = view.FindViewById<MaterialButton>(Resource.Id.chat_unknown_resend_btn)!;
        _unknownCheckBtn.Click += (_, _) => _ = RecoverLastSendAsync(userConfirmedResend: false);
        _unknownResendBtn.Click += (_, _) => _ = RecoverLastSendAsync(userConfirmedResend: true);

        _loadOlderBtn = view.FindViewById<MaterialButton>(Resource.Id.chat_load_older_btn)!;
        _loadOlderBtn.Click += (_, _) => _ = LoadOlderHistoryAsync();

        _list = view.FindViewById<RecyclerView>(Resource.Id.chat_list)!;
        _layoutManager = new LinearLayoutManager(RequireContext()) { StackFromEnd = false };
        _list.SetLayoutManager(_layoutManager);
        _list.SetItemAnimator(null);
        // 审批 / 问答卡已迁入固定 RequestSlot，时间线适配器只负责会话行。
        _adapter = new ChatTimelineAdapter(
            _mdCache,
            action => Activity?.RunOnUiThread(action),
            OpenWorkspaceFile);
        _list.SetAdapter(_adapter);
        _list.AddOnScrollListener(new ChatScrollListener(this));

        _requestSlot = new ChatRequestSlotController(
            view.FindViewById(Resource.Id.chat_request_slot)!,
            (eventId, decision) => _ = _vm.AnswerApprovalAsync(eventId, decision),
            eventId => _ = _vm.UndoApprovalAsync(eventId),
            (eventId, batch) => _ = _vm.AnswerQuestionAsync(eventId, batch),
            eventId => _ = _vm.CancelQuestionAsync(eventId));

        _empty = view.FindViewById(Resource.Id.chat_empty)!;
        _empty.FindViewById<ImageView>(Resource.Id.empty_icon)!.SetImageResource(Resource.Drawable.ic_chat);
        _empty.FindViewById<TextView>(Resource.Id.empty_text)!.SetText(
            _embeddedInPane ? Resource.String.chat_pane_empty_hint : Resource.String.chat_empty_subtitle);

        _jumpBottomBar = view.FindViewById(Resource.Id.chat_jump_bottom_bar)!;
        _jumpBottomBar.Click += (_, _) => ScrollToBottomAndFollow();

        _slashPanel = view.FindViewById(Resource.Id.chat_slash_panel)!;
        _slashList = _slashPanel.FindViewById<RecyclerView>(Resource.Id.chat_slash_list)!;
        _slashEmpty = _slashPanel.FindViewById(Resource.Id.chat_slash_empty)!;
        _slashList.SetLayoutManager(new LinearLayoutManager(RequireContext()));
        _slashAdapter = new SlashCommandAdapter(OnSlashChipClicked);
        _slashList.SetAdapter(_slashAdapter);
        _slashPanel.FindViewById(Resource.Id.chat_slash_close_btn)!.Click += (_, _) =>
        {
            _slashExpanded = false;
            _slashAutoFromInput = false;
            RenderSlashBar(_vm.State);
        };

        _attachmentsBar = view.FindViewById(Resource.Id.chat_attachments_bar)!;
        _attachmentsList = view.FindViewById<LinearLayout>(Resource.Id.chat_attachments_list)!;

        _input = view.FindViewById<EditText>(Resource.Id.chat_input)!;
        if (savedInstanceState?.GetString(DraftInputKey) is { } restoredDraft)
        {
            _input.Text = restoredDraft;
        }
        _slashExpanded = savedInstanceState?.GetBoolean(SlashExpandedKey, false) ?? false;
        _input.TextChanged += (_, e) => OnInputTextChanged(e.Text?.ToString());
        // 软键盘 Enter 由 multiLine + flagNoEnterAction 保持换行；物理键盘 Enter 在 KeyPress 中发送。
        _input.KeyPress += OnInputKeyPress;

        _modelPill = view.FindViewById<MaterialButton>(Resource.Id.chat_model_pill)!;
        _sendBtn = view.FindViewById<MaterialButton>(Resource.Id.chat_send_btn)!;
        _plusBtn = view.FindViewById<MaterialButton>(Resource.Id.chat_plus_btn)!;
        _trayImagesBtn = view.FindViewById<MaterialButton>(Resource.Id.chat_composer_tray_images)!;
        _traySlashBtn = view.FindViewById<MaterialButton>(Resource.Id.chat_composer_tray_slash)!;
        _composerControls = view.FindViewById(Resource.Id.chat_composer_controls)!;
        _composerTray = view.FindViewById(Resource.Id.chat_composer_tray)!;

        _modelPill.Click += (_, _) => OpenModelPicker();
        _plusBtn.Click += (_, _) => ToggleComposerTray();
        _trayImagesBtn.Click += (_, _) =>
        {
            CollapseComposerTray();
            OpenImagePicker();
        };
        _traySlashBtn.Click += (_, _) =>
        {
            CollapseComposerTray();
            ShowSlashBar();
        };
        _sendBtn.Click += (_, _) => _ = SendOrCancelAsync();

        // 横屏弹出软键盘时收起内嵌控制行与 Tray、压缩输入区为单行，配合 flagNoExtractUi 保住输入框完整可见。
        var composerInner = view.FindViewById(Resource.Id.chat_composer_inner)!;
        var dp = Resources.DisplayMetrics!.Density;
        var normalComposerPadH = composerInner.PaddingLeft;
        var normalComposerPadT = composerInner.PaddingTop;
        var normalComposerPadB = composerInner.PaddingBottom;
        var normalInputPadH = _input.PaddingLeft;
        var normalInputPadT = _input.PaddingTop;
        var normalInputPadB = _input.PaddingBottom;
        var normalInputMinH = _input.MinimumHeight;
        ViewCompat.SetOnApplyWindowInsetsListener(view, new Edges.Listener((_, insets) =>
        {
            var isLandscape = Resources?.Configuration?.Orientation == global::Android.Content.Res.Orientation.Landscape;
            var imeVisible = insets.IsVisible(WindowInsetsCompat.Type.Ime());
            var compact = isLandscape && imeVisible;
            _composerControls.Visibility = compact ? ViewStates.Gone : ViewStates.Visible;
            _composerTray.Visibility = compact || !_trayExpanded ? ViewStates.Gone : ViewStates.Visible;
            _input.SetMaxLines(compact ? 1 : 5);
            _input.SetMinHeight(compact ? 0 : normalInputMinH);
            if (compact)
            {
                var p2 = (int)Math.Round(2f * dp);
                var p4 = (int)Math.Round(4f * dp);
                composerInner.SetPadding(normalComposerPadH, p2, normalComposerPadH, p2);
                _input.SetPadding(normalInputPadH, p4, normalInputPadH, p4);
            }
            else
            {
                composerInner.SetPadding(normalComposerPadH, normalComposerPadT, normalComposerPadH, normalComposerPadB);
                _input.SetPadding(normalInputPadH, normalInputPadT, normalInputPadH, normalInputPadB);
            }

            // IME 收起后部分 ROM 会残留底部白边：400ms 后再兜底刷新一次 inset。
            if (_lastImeVisible != imeVisible)
            {
                _lastImeVisible = imeVisible;
                view.PostDelayed(() => ViewCompat.RequestApplyInsets(view), 400);
            }
            return insets;
        }));

        var requestSlotRoot = view.FindViewById(Resource.Id.chat_request_slot)!;
        // 宽屏保持阅读流与停靠输入区居中限宽，同时保留全宽贴顶顶栏与全宽底栏顶线。
        Edges.ClampMaxWidthByMarginTo(
            Resource.Dimension.dshm_chat_max_width,
            _list,
            _attachmentsBar,
            requestSlotRoot,
            composerInner,
            _unknownBanner,
            _slashPanel);

        RenderAttachments();
        return view;
    }

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);
        // 系统返回键优先收起斜杠面板 / 快捷 Tray，而不是直接退出对话页；
        // 注册在 ViewLifecycleOwner 上，视图销毁时自动移除，避免持有已释放的视图。
        _panelBackCallback = new PanelBackCallback(this);
        RequireActivity().OnBackPressedDispatcher.AddCallback(ViewLifecycleOwner, _panelBackCallback);
        SyncPanelBackCallback();
    }

    /// <summary>
    /// 仅在斜杠面板实际可见或快捷 Tray 展开时启用返回键拦截；收起后立即禁用，
    /// 同时排除已隐藏的 Fragment，避免吞掉其他标签页的系统返回与预测性返回手势。
    /// </summary>
    private void SyncPanelBackCallback()
    {
        if (_panelBackCallback is not null)
        {
            var slashVisible = _slashExpanded && _slashPanel is { Visibility: ViewStates.Visible };
            _panelBackCallback.Enabled = !IsHidden && (slashVisible || _trayExpanded);
        }
    }

    public override void OnHiddenChanged(bool hidden)
    {
        base.OnHiddenChanged(hidden);
        SyncPanelBackCallback();
    }

    /// <summary>UI-S6：是否以远程主机会话运行（MainActivity 右栏据此判断能否复用现有实例）。</summary>
    public bool IsRemote => _isRemote;

    public override void OnStart()
    {
        base.OnStart();
        if (_openedCredentials)
        {
            _openedCredentials = false;
            _onboardingPrompted = false;
            _ = _vm.RefreshCredentialReadinessAsync();
        }
        _vm.StateChanged += OnVmStateChanged;
        _bridge.Changed += OnBridgeChanged;
        _bridge.Agent.Inbox.StateChanged += OnInboxStateChanged;
        _vm.SetUiVisible(true);
        RenderState(_vm.State);

        var sessionMismatch = !string.IsNullOrWhiteSpace(_requestedSessionId) &&
            !string.Equals(_vm.State.ActiveSession.SessionId, _requestedSessionId, StringComparison.Ordinal);
        var shouldLoadCache = !_isRemote && !_cacheLoadInitiated;
        var shouldConnect = !_connectInitiated || sessionMismatch || (!_vm.State.IsConnected && !_vm.State.IsReconciling && IsEndpointReady());
        if (shouldLoadCache)
        {
            _cacheLoadInitiated = true;
        }
        if (shouldConnect)
        {
            _connectInitiated = true;
        }
        if (shouldLoadCache || shouldConnect)
        {
            _ = StartupAttachSequenceAsync(shouldLoadCache, shouldConnect);
        }
    }

    private bool IsEndpointReady() =>
        _isRemote ? _bridge.Agent.IsRemoteEndpointReady : _bridge.Runtime.Host?.State == DshHostState.Ready;

    public override void OnStop()
    {
        _vm.SetUiVisible(false);
        _vm.StateChanged -= OnVmStateChanged;
        _bridge.Changed -= OnBridgeChanged;
        _bridge.Agent.Inbox.StateChanged -= OnInboxStateChanged;
        _ = _vm.FlushPendingHoldsAsync();
        base.OnStop();
    }

    public override void OnSaveInstanceState(Bundle outState)
    {
        base.OnSaveInstanceState(outState);
        if (_input is not null && !string.IsNullOrEmpty(_input.Text))
        {
            outState.PutString(DraftInputKey, _input.Text);
        }
        outState.PutBoolean(SlashExpandedKey, _slashExpanded);
    }

    [Obsolete("Uses Fragment activity result for image picker parity")]
    public override void OnActivityResult(int requestCode, int resultCode, Intent? data)
    {
#pragma warning disable CS0618
        base.OnActivityResult(requestCode, resultCode, data);
#pragma warning restore CS0618
        if (requestCode != RequestImagePick || resultCode != (int)Android.App.Result.Ok || data is null)
        {
            return;
        }
        var uris = new List<global::Android.Net.Uri>();
        if (data.ClipData is { ItemCount: > 0 } clip)
        {
            for (var i = 0; i < clip.ItemCount; i++)
            {
                if (clip.GetItemAt(i)?.Uri is { } u)
                {
                    uris.Add(u);
                }
            }
        }
        else if (data.Data is { } single)
        {
            uris.Add(single);
        }
        if (uris.Count > 0)
        {
            _ = LoadPickedImagesAsync(uris);
        }
    }

    /// <summary>从跨会话收件箱或系统通知切入指定会话并定位交互卡片（N-06-03 / N-06-04）。</summary>
    public void NavigateToSession(string? sessionId, string? focusEventId)
    {
        _pendingFocusEventId = string.IsNullOrWhiteSpace(focusEventId) ? null : focusEventId;
        if (!string.IsNullOrWhiteSpace(sessionId) &&
            !string.Equals(sessionId, _vm.State.ActiveSession.SessionId, StringComparison.Ordinal))
        {
            _requestedSessionId = sessionId;
            _connectInitiated = true;
            _ = StartupAttachSequenceAsync(loadCache: !_isRemote, connect: IsEndpointReady());
            return;
        }
        if (View is not null)
        {
            RenderState(_vm.State);
        }
    }

    /// <summary>
    /// UI-S5：聊天工具卡的文件胶囊不再通过 <c>ClearTop</c> 跳回 <see cref="MainActivity"/>（那会摧毁
    /// <see cref="ChatActivity"/> 返回栈与对话滚动位置）。Diff 直接打开独立全屏审查页，普通文件直接打开
    /// 全屏编辑器；两者都在目标页内复用工作区安全路径校验（含 .dsh/ 保护）。系统返回即回到对话原位。
    /// </summary>
    private void OpenWorkspaceFile(string relativePath, bool openGitDiff)
    {
        StartActivity(openGitDiff
            ? DiffReviewActivity.CreateWorkspaceIntent(RequireContext(), relativePath)
            : WorkspaceEditorActivity.CreateIntent(RequireContext(), relativePath, startEditing: false));
    }

    private void OnVmStateChanged(ChatUiState state) => ScheduleRender();

    private void OnInboxStateChanged(InboxUiState state)
    {
        // 跨会话收件箱状态可能来自后台线程，统一切回 UI 线程更新顶栏入口。
        Activity?.RunOnUiThread(() =>
        {
            if (View is not null)
            {
                RenderInboxEntry(state);
            }
        });
    }

    private void RenderInboxEntry() => RenderInboxEntry(_bridge.Agent.Inbox.State);

    private void RenderInboxEntry(InboxUiState inbox)
    {
        if (_inboxMenuItem is null)
        {
            return;
        }

        var total = inbox.TotalCount;
        _inboxMenuItem.SetVisible(total > 0);
        _inboxMenuItem.SetTitle(total > 0
            ? Rows.Fmt(RequireContext(), Resource.String.chat_menu_inbox_fmt, total)
            : GetString(Resource.String.chat_menu_inbox));
    }

    private void ShowInbox()
    {
        var manager = ParentFragmentManager;
        if (manager.FindFragmentByTag(InboxBottomSheet.SheetTag) is null)
        {
            new InboxBottomSheet().Show(manager, InboxBottomSheet.SheetTag);
        }
    }

    private void OnBridgeChanged(object? sender, EventArgs e)
    {
        if (IsEndpointReady() && !_vm.State.IsConnected && !_vm.State.IsReconciling)
        {
            _ = EnsureConnectedAsync();
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
                    RenderState(_vm.State);
                }
            });
        }
    }

    private void RenderState(ChatUiState s)
    {
        var ctx = RequireContext();
        var untitledPrefix = GetString(Resource.String.chats_untitled_prefix);
        _toolbar.Title = string.IsNullOrWhiteSpace(s.SessionTitle)
            ? GetString(Resource.String.chat_session_default_title)
            : ChatDisplayFormatter.FormatShortSessionTitle(s.ActiveSession.SessionId, s.SessionTitle, untitledPrefix);

        var workspaceName = ResolveWorkspaceName(ctx);
        _toolbar.Subtitle = _isRemote && _bridge.Agent.RemoteHostLabel is { Length: > 0 } remoteLabel
            ? Rows.Fmt(ctx, Resource.String.remote_chat_title_fmt, remoteLabel)
            : Rows.Fmt(ctx, Resource.String.chat_session_subtitle_local_fmt, workspaceName);

        _modelPill.Text = ChatDisplayFormatter.FormatCompactModelBadge(
            s.ModelLabel,
            s.CurrentModel,
            GetString(Resource.String.chat_model_pill_fallback));
        _modelPill.Enabled = s.IsConnected;

        RenderStatusBanner(ctx, s);
        RenderUnknownSendBanner(s);

        var canLoadOlder = s.IsConnected && s.Capabilities.HistoryReplay && s.Transcript.IsPartialHistory && s.Transcript.EarliestSeq is > 0;
        _loadOlderBtn.Visibility = canLoadOlder ? ViewStates.Visible : ViewStates.Gone;
        _loadOlderBtn.Enabled = !s.IsLoadingOlderHistory;
        _loadOlderBtn.SetText(s.IsLoadingOlderHistory ? Resource.String.chat_loading_older : Resource.String.chat_load_older);

        if (s.IsConnected && s.NeedsCredentialOnboarding && s.Capabilities.ManageCredentials && !_onboardingPrompted)
        {
            _onboardingPrompted = true;
            ShowCredentialOnboardingDialog();
        }

        // 工具分组、Turn Footer 与流式 Live 行由投影器生成；审批 / 问答卡固定在 RequestSlot，不进入列表。
        var items = ChatTimelineProjector.Project(s.Transcript);
        _adapter.Submit(items);
        // 内容高度变化后重新评估回底按钮，避免用户停留在上方时新消息到达却看不到入口。
        _list.Post(() => UpdateScrollToBottomVisibility(_list));
        _empty.Visibility = items.Count == 0 && s.PendingRequests.Count == 0 ? ViewStates.Visible : ViewStates.Gone;

        _requestSlot.Bind(s.PendingRequests, _pendingFocusEventId);
        if (_pendingFocusEventId is { } focusId && s.PendingRequests.Any(c => c.EventId == focusId))
        {
            _pendingFocusEventId = null;
        }
        RenderInboxEntry();

        if (_followBottom && items.Count > 0)
        {
            ScrollToLatestItem();
            _jumpBottomBar.Visibility = ViewStates.Gone;
        }

        var hasRealSession = !string.IsNullOrWhiteSpace(s.ActiveSession.SessionId)
            && !string.Equals(s.ActiveSession.SessionId, "pending", StringComparison.Ordinal);
        var canArchive = !_isRemote && hasRealSession && s.IsConnected && s.Capabilities.ArchiveSessions;
        _archivedBanner.Visibility = hasRealSession && s.IsArchived ? ViewStates.Visible : ViewStates.Gone;
        _archivedUnarchiveBtn.Enabled = canArchive;
        _archiveMenuItem?.SetVisible(canArchive && !s.IsArchived);
        _unarchiveMenuItem?.SetVisible(canArchive && s.IsArchived);
        _removeMenuItem?.SetVisible(!_isRemote && hasRealSession);

        var isRunningTurn = s.Transcript.Phase is RunPhase.Starting or RunPhase.Streaming;
        _cancelMenuItem?.SetVisible(isRunningTurn && s.Capabilities.CancelSession);
        if (isRunningTurn && s.Capabilities.CancelSession)
        {
            _sendBtn.SetIconResource(Resource.Drawable.ic_stop);
            _sendBtn.ContentDescription = GetString(Resource.String.chat_stop_desc);
            _sendBtn.Enabled = true;
        }
        else
        {
            _sendBtn.SetIconResource(Resource.Drawable.ic_arrow_upward);
            _sendBtn.ContentDescription = GetString(Resource.String.chat_send_desc);
            _sendBtn.Enabled = s.CanSend;
        }

        _trayImagesBtn.Visibility = s.Capabilities.PromptImages ? ViewStates.Visible : ViewStates.Gone;
        _traySlashBtn.Visibility = s.Capabilities.SlashCommands ? ViewStates.Visible : ViewStates.Gone;
        _plusBtn.Visibility = s.Capabilities.PromptImages || s.Capabilities.SlashCommands
            ? ViewStates.Visible
            : ViewStates.Gone;
        if (!s.Capabilities.PromptImages && !s.Capabilities.SlashCommands && _trayExpanded)
        {
            CollapseComposerTray();
        }
        RenderSlashBar(s);
    }

    /// <summary>本地副标题使用真实工作区名（/root/projects → projects），不再固定写死“工作区”。</summary>
    private static string ResolveWorkspaceName(Context ctx)
    {
        var raw = ctx.GetString(Resource.String.chat_session_subtitle_default)?.Trim() ?? string.Empty;
        var normalized = raw.TrimEnd('/');
        var slash = normalized.LastIndexOf('/');
        var name = slash >= 0 && slash + 1 < normalized.Length ? normalized[(slash + 1)..] : normalized;
        return string.IsNullOrWhiteSpace(name)
            ? ctx.GetString(Resource.String.chat_session_subtitle_workspace_fallback) ?? "工作区"
            : name;
    }

    private void RenderStatusBanner(Context ctx, ChatUiState s)
    {
        var hostPhase = _isRemote ? null : _bridge.HostPhase;
        var hostReady = IsEndpointReady();
        if (s.IsConnected && !s.IsReconciling && !s.Transcript.IsDetachedCache && !s.Transcript.IsPartialHistory && s.FailureCode is null)
        {
            _statusBanner.Visibility = ViewStates.Gone;
            return;
        }

        _statusBanner.Visibility = ViewStates.Visible;
        _statusProgress.Visibility = s.IsReconciling || (hostPhase is not null and not DshHostPhase.Idle and not DshHostPhase.Ready and not DshHostPhase.Failed)
            ? ViewStates.Visible
            : ViewStates.Gone;

        if (!hostReady && !s.IsConnected)
        {
            if (_isRemote)
            {
                _statusText.SetText(Resource.String.remote_chat_disconnected);
                _statusAction.Visibility = ViewStates.Gone;
                return;
            }
            _statusText.Text = hostPhase switch
            {
                DshHostPhase.SandboxStarting => GetString(Resource.String.home_detail_phase_sandbox),
                DshHostPhase.DshStarting => GetString(Resource.String.home_detail_phase_dsh_spawned),
                DshHostPhase.DshTokenAvailable => GetString(Resource.String.home_detail_phase_token),
                DshHostPhase.ProtocolConnecting => GetString(Resource.String.home_detail_phase_protocol),
                DshHostPhase.Initializing => GetString(Resource.String.home_detail_phase_initializing),
                _ => s.Transcript.IsDetachedCache
                    ? GetString(Resource.String.chat_status_cache_only)
                    : Rows.Fmt(ctx, Resource.String.chat_status_disconnected, s.FailureCode ?? "STOPPED"),
            };
            _statusAction.Visibility = _bridge.IsBusy ? ViewStates.Gone : ViewStates.Visible;
            _statusAction.SetText(Resource.String.chat_status_start_dsh);
            return;
        }

        if (s.IsReconciling)
        {
            _statusText.SetText(Resource.String.chat_status_reconciling);
            _statusAction.Visibility = ViewStates.Gone;
            return;
        }

        if (s.Transcript.IsPartialHistory || s.FailureCode is not null)
        {
            if (IsCredentialFailure(s.FailureCode))
            {
                _statusText.SetText(Resource.String.credentials_onboarding_title);
                _statusAction.Visibility = ViewStates.Visible;
                _statusAction.SetText(Resource.String.credentials_onboarding_action);
                return;
            }
            _statusText.Text = s.IsConnected
                ? Rows.Fmt(ctx, Resource.String.chat_status_partial_history, s.FailureCode ?? s.Transcript.FailureCode ?? "PARTIAL")
                : Rows.Fmt(ctx, Resource.String.chat_status_disconnected, s.FailureCode ?? "DISCONNECTED");
            _statusAction.Visibility = ViewStates.Visible;
            _statusAction.SetText(Resource.String.chat_status_retry_connect);
            return;
        }

        _statusBanner.Visibility = ViewStates.Gone;
    }

    private void RenderUnknownSendBanner(ChatUiState s)
    {
        if (s.LastSend is { Phase: OutboundSendPhase.UnknownOutcome or OutboundSendPhase.RequiresUserConfirmation } last)
        {
            _unknownBanner.Visibility = ViewStates.Visible;
            _unknownResendBtn.Visibility = last.Phase == OutboundSendPhase.RequiresUserConfirmation
                ? ViewStates.Visible
                : ViewStates.Gone;
        }
        else
        {
            _unknownBanner.Visibility = ViewStates.Gone;
        }
    }

    private void OnInputTextChanged(string? text)
    {
        var raw = text ?? string.Empty;
        var state = _vm.State;
        if (state.Capabilities.SlashCommands
            && state.SlashCommands.Count > 0
            && raw.StartsWith('/')
            && !raw.Contains('\n')
            && raw.Length <= 32
            && !raw.Contains(' '))
        {
            _slashFilterQuery = raw;
            _slashExpanded = true;
            _slashAutoFromInput = true;
            RenderSlashBar(state);
            return;
        }

        if (_slashAutoFromInput)
        {
            _slashAutoFromInput = false;
            _slashExpanded = false;
            _slashFilterQuery = string.Empty;
            RenderSlashBar(state);
        }
    }

    private void RenderSlashBar(ChatUiState s)
    {
        if (!s.Capabilities.SlashCommands || !_slashExpanded || s.SlashCommands.Count == 0)
        {
            _slashPanel.Visibility = ViewStates.Gone;
            SyncPanelBackCallback();
            return;
        }
        _slashPanel.Visibility = ViewStates.Visible;
        _slashAdapter.Submit(s.SlashCommands, _slashAutoFromInput ? _slashFilterQuery : string.Empty);

        var count = _slashAdapter.ItemCount;
        _slashList.Visibility = count > 0 ? ViewStates.Visible : ViewStates.Gone;
        _slashEmpty.Visibility = count == 0 ? ViewStates.Visible : ViewStates.Gone;

        if (_slashList.LayoutParameters is { } lp)
        {
            var desiredHeight = count > 4 ? Rows.Dp(RequireContext(), 210) : ViewGroup.LayoutParams.WrapContent;
            if (lp.Height != desiredHeight)
            {
                lp.Height = desiredHeight;
                _slashList.LayoutParameters = lp;
            }
        }
        SyncPanelBackCallback();
    }

    private void RenderAttachments()
    {
        if (_attachments.Count == 0)
        {
            _attachmentsBar.Visibility = ViewStates.Gone;
            return;
        }
        _attachmentsBar.Visibility = ViewStates.Visible;
        _attachmentsList.RemoveAllViews();
        var ctx = RequireContext();
        var inflater = LayoutInflater.From(ctx)!;
        var targetPx = Rows.Dp(ctx, 56f);
        for (var i = 0; i < _attachments.Count; i++)
        {
            var index = i;
            var img = _attachments[i];
            var name = string.IsNullOrWhiteSpace(img.Name)
                ? Rows.Fmt(ctx, Resource.String.chat_attachment_default_name_fmt, index + 1)
                : img.Name!;

            var item = inflater.Inflate(Resource.Layout.item_chat_attachment, _attachmentsList, false)!;
            var thumb = item.FindViewById<ImageView>(Resource.Id.chat_attachment_thumb)!;
            var fallback = item.FindViewById<LinearLayout>(Resource.Id.chat_attachment_fallback)!;
            var fallbackName = item.FindViewById<TextView>(Resource.Id.chat_attachment_fallback_name)!;
            var remove = item.FindViewById<MaterialButton>(Resource.Id.chat_attachment_remove)!;

            var bitmap = TryDecodeAttachmentThumbnail(img.Base64Data, targetPx);
            if (bitmap is not null)
            {
                thumb.SetImageBitmap(bitmap);
                thumb.Visibility = ViewStates.Visible;
                fallback.Visibility = ViewStates.Gone;
            }
            else
            {
                thumb.Visibility = ViewStates.Gone;
                fallback.Visibility = ViewStates.Visible;
                fallbackName.Text = name;
            }

            remove.ContentDescription = Rows.Fmt(ctx, Resource.String.chat_attachment_remove_fmt, name);
            remove.Click += (_, _) =>
            {
                if (index < _attachments.Count)
                {
                    _attachments.RemoveAt(index);
                    RenderAttachments();
                }
            };
            _attachmentsList.AddView(item);
        }
    }

    /// <summary>按目标缩略图尺寸做 2 的幂采样解码，避免 5 MiB 大图以原图位图占用内存。</summary>
    private static Bitmap? TryDecodeAttachmentThumbnail(string? base64, int targetPx)
    {
        if (string.IsNullOrWhiteSpace(base64) || targetPx <= 0)
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }

        var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
        BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length, bounds);
        if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0)
        {
            return null;
        }

        var sampleSize = 1;
        var maxEdge = targetPx * 2;
        while (bounds.OutWidth / sampleSize > maxEdge || bounds.OutHeight / sampleSize > maxEdge)
        {
            sampleSize *= 2;
        }

        var options = new BitmapFactory.Options
        {
            InSampleSize = sampleSize,
            InPreferredConfig = Bitmap.Config.Argb8888,
        };
        return BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length, options);
    }

    private void ShowSlashBar()
    {
        _slashAutoFromInput = false;
        _slashFilterQuery = string.Empty;
        _slashExpanded = true;
        RenderSlashBar(_vm.State);
    }

    /// <summary>展开 / 收起输入胶囊上方的快捷 Tray，并让 + 图标旋转 45° 变为 ×。</summary>
    private void ToggleComposerTray() => SetComposerTrayExpanded(!_trayExpanded);

    private void CollapseComposerTray()
    {
        if (_trayExpanded)
        {
            SetComposerTrayExpanded(false);
        }
    }

    private void SetComposerTrayExpanded(bool expanded)
    {
        _trayExpanded = expanded;
        if (expanded)
        {
            // Tray 与斜杠面板互斥：同一张 Composer 卡片上方只允许一个浮层，避免两层叠压。
            if (_slashExpanded)
            {
                _slashExpanded = false;
                _slashAutoFromInput = false;
                _slashFilterQuery = string.Empty;
                RenderSlashBar(_vm.State);
            }
            _composerTray.Alpha = 0f;
            _composerTray.Visibility = ViewStates.Visible;
            _composerTray.Animate()?.Alpha(1f).SetDuration(160).Start();
        }
        else
        {
            _composerTray.Animate()?.Alpha(0f).SetDuration(120).Start();
            _composerTray.PostDelayed(() =>
            {
                if (!_trayExpanded)
                {
                    _composerTray.Visibility = ViewStates.Gone;
                    _composerTray.Alpha = 1f;
                }
            }, 130);
        }

        var animator = _plusBtn.Animate();
        if (animator is not null)
        {
            animator.Rotation(expanded ? 45f : 0f).SetDuration(180).Start();
        }
        else
        {
            _plusBtn.Rotation = expanded ? 45f : 0f;
        }
        SyncPanelBackCallback();
    }

    /// <summary>
    /// 区分软硬键盘：软键盘 Enter 保持换行；物理键盘 Enter 发送，Shift+Enter 继续换行。
    /// </summary>
    private void OnInputKeyPress(object? sender, View.KeyEventArgs e)
    {
        if (e.Event is not KeyEvent key
            || key.Action != KeyEventActions.Down
            || key.KeyCode != Keycode.Enter)
        {
            return;
        }

        // FLAG_SOFT_KEYBOARD = 0x2：来自软键盘的按键继续交给 EditText 插入换行。
        if ((((int)key.Flags) & 0x2) != 0 || key.IsShiftPressed)
        {
            return;
        }

        e.Handled = true;
        if (_vm.State.Transcript.Phase is RunPhase.Starting or RunPhase.Streaming)
        {
            return;
        }
        _ = SendOrCancelAsync();
    }

    private bool IsStopGuardActive()
    {
        if (Android.OS.SystemClock.ElapsedRealtime() >= _sendGuardUntilMs)
        {
            return false;
        }
        if (View is { } root)
        {
            Snackbar.Make(root, Resource.String.chat_stop_guard_hint, Snackbar.LengthShort).Show();
        }
        return true;
    }

    private void OnSlashChipClicked(SlashCommandDescriptorView cmd)
    {
        var commandName = EnsureLeadingSlash(cmd.Name);
        _slashExpanded = false;
        _slashAutoFromInput = false;
        _slashFilterQuery = string.Empty;
        if (string.IsNullOrWhiteSpace(cmd.InputHint))
        {
            _input.Text = string.Empty;
            RenderSlashBar(_vm.State);
            _ = _vm.RunSlashCommandAsync(commandName);
            return;
        }
        _input.Text = $"{commandName} ";
        _input.SetSelection(_input.Text?.Length ?? 0);
        _input.RequestFocus();
        RenderSlashBar(_vm.State);
    }

    private static string EnsureLeadingSlash(string name)
    {
        var trimmed = name.Trim();
        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }

    private void OnMenuClicked(int itemId)
    {
        if (itemId == Resource.Id.chat_menu_refresh)
        {
            _ = _vm.RefreshHistoryAsync();
        }
        else if (itemId == Resource.Id.chat_menu_switch_model)
        {
            OpenModelPicker();
        }
        else if (itemId == Resource.Id.chat_menu_credentials)
        {
            OpenCredentialsScreen();
        }
        else if (itemId == Resource.Id.chat_menu_new_session)
        {
            _requestedSessionId = null;
            _ = _vm.SwitchOrCreateSessionAsync(sessionId: null);
        }
        else if (itemId == Resource.Id.chat_menu_cancel)
        {
            _ = _vm.CancelSessionAsync();
        }
        else if (itemId == Resource.Id.chat_menu_inbox)
        {
            ShowInbox();
        }
        else if (itemId == Resource.Id.chat_menu_archive)
        {
            StartArchiveCurrentSessionFlow();
        }
        else if (itemId == Resource.Id.chat_menu_unarchive)
        {
            _ = UnarchiveCurrentSessionFlowAsync();
        }
        else if (itemId == Resource.Id.chat_menu_remove)
        {
            ConfirmRemoveCurrentSession();
        }
    }

    private void StartArchiveCurrentSessionFlow()
    {
        var phase = _vm.State.Transcript.Phase;
        if (phase is RunPhase.Starting or RunPhase.Streaming)
        {
            ShowStopAndArchiveCurrentDialog();
            return;
        }
        _ = ArchiveCurrentSessionFlowAsync(stopActivity: false);
    }

    private void ShowStopAndArchiveCurrentDialog()
    {
        new MaterialAlertDialogBuilder(RequireContext())
            .SetTitle(Resource.String.chats_archive_active_title)!
            .SetMessage(Resource.String.chats_archive_active_msg)!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(Resource.String.chats_archive_active_confirm, (_, _) =>
                _ = ArchiveCurrentSessionFlowAsync(stopActivity: true))!
            .Show();
    }

    private async Task ArchiveCurrentSessionFlowAsync(bool stopActivity)
    {
        var res = await _vm.ArchiveCurrentSessionAsync(stopActivity).ConfigureAwait(false);
        if (res.IsOk && !_isRemote && _bridge.Runtime.Host?.State == DshHostState.Ready)
        {
            _ = _bridge.Agent.Sessions.RefreshAsync();
        }
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
                ShowStopAndArchiveCurrentDialog();
                return;
            }
            Snackbar.Make(
                root,
                Rows.Fmt(RequireContext(), Resource.String.chat_error_banner, res.Error?.DiagnosticKey ?? res.Error?.Code.ToString() ?? "ERROR"),
                Snackbar.LengthShort).Show();
        });
    }

    private async Task UnarchiveCurrentSessionFlowAsync()
    {
        var res = await _vm.UnarchiveCurrentSessionAsync().ConfigureAwait(false);
        if (res.IsOk && !_isRemote && _bridge.Runtime.Host?.State == DshHostState.Ready)
        {
            _ = _bridge.Agent.Sessions.RefreshAsync();
        }
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

    private void ConfirmRemoveCurrentSession()
    {
        var state = _vm.State;
        var isCachedOnly = !state.IsConnected;
        var titleRes = isCachedOnly ? Resource.String.chats_remove_cached_title : Resource.String.chats_remove_title;
        var msgRes = isCachedOnly ? Resource.String.chats_remove_cached_msg : Resource.String.chats_remove_msg;
        var positiveRes = isCachedOnly ? Resource.String.chats_remove_cached_confirm : Resource.String.chats_remove_confirm;

        new MaterialAlertDialogBuilder(RequireContext())
            .SetTitle(titleRes)!
            .SetMessage(msgRes)!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(positiveRes, (_, _) =>
                _ = RemoveCurrentSessionFlowAsync(stopActivity: false, isCachedOnly))!
            .Show();
    }

    private void ShowStopAndRemoveCurrentDialog()
    {
        new MaterialAlertDialogBuilder(RequireContext())
            .SetTitle(Resource.String.chats_remove_active_title)!
            .SetMessage(Resource.String.chats_remove_active_msg)!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(Resource.String.chats_remove_active_confirm, (_, _) =>
                _ = RemoveCurrentSessionFlowAsync(stopActivity: true, isCachedOnly: false))!
            .Show();
    }

    private async Task RemoveCurrentSessionFlowAsync(bool stopActivity, bool isCachedOnly)
    {
        var sessionId = _vm.State.ActiveSession.SessionId;
        var res = await _vm.DeleteCurrentSessionAsync(stopActivity).ConfigureAwait(false);
        if (res.IsOk && !_isRemote)
        {
            if (_bridge.Runtime.Host?.State == DshHostState.Ready)
            {
                _ = _bridge.Agent.Sessions.RefreshAsync();
            }
            else
            {
                _ = _bridge.Agent.Sessions.LoadCachedSessionsAsync();
            }
        }
        Activity?.RunOnUiThread(() =>
        {
            if (res.IsOk)
            {
                if (_embeddedInPane)
                {
                    (Activity as MainActivity)?.CloseDetailChatIfMatches(sessionId);
                }
                else
                {
                    Activity?.Finish();
                }
                return;
            }
            if (!isCachedOnly && !stopActivity && res.Error?.Code == DshFailureCode.SessionActive)
            {
                ShowStopAndRemoveCurrentDialog();
                return;
            }
            if (View is { } root)
            {
                Snackbar.Make(
                    root,
                    Rows.Fmt(RequireContext(), Resource.String.chat_error_banner, res.Error?.DiagnosticKey ?? res.Error?.Code.ToString() ?? "ERROR"),
                    Snackbar.LengthShort).Show();
            }
        });
    }

    /// <summary>
    /// Polish-P2：点击顶栏标题 / 副标题直达当前工作区浏览，不再误弹模型选择器。
    /// 模型切换统一由底部内嵌胶囊承担；宽屏嵌入时同样复用 MainActivity 的工作区页签。
    /// </summary>
    private void OnSessionHeaderClicked()
    {
        StartActivity(MainActivity.CreateWorkspaceIntent(RequireContext(), string.Empty, openGitDiff: false));
    }

    private void OnStatusActionClicked()
    {
        if (IsCredentialFailure(_vm.State.FailureCode))
        {
            OpenCredentialsScreen();
            return;
        }
        if (!IsEndpointReady())
        {
            if (!_isRemote)
            {
                _ = _bridge.RunStartAsync();
            }
        }
        else
        {
            _ = EnsureConnectedAsync();
        }
    }

    private async Task StartupAttachSequenceAsync(bool loadCache, bool connect)
    {
        if (loadCache)
        {
            await LoadCacheFirstAsync().ConfigureAwait(false);
        }
        if (connect)
        {
            await EnsureConnectedAsync().ConfigureAwait(false);
        }
    }

    private async Task LoadCacheFirstAsync()
    {
        try
        {
            var hasRows = false;
            if (!string.IsNullOrWhiteSpace(_requestedSessionId))
            {
                await _vm.LoadCachedSessionAsync(_requestedSessionId).ConfigureAwait(false);
                hasRows = _vm.State.Transcript.Rows.Count > 0;
            }
            else
            {
                hasRows = await _vm.LoadMostRecentCachedSessionAsync().ConfigureAwait(false);
            }

            if (hasRows)
            {
                Activity?.RunOnUiThread(() => (Activity as ChatActivity)?.MarkCacheFirstRendered());
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task EnsureConnectedAsync()
    {
        try
        {
            if (IsEndpointReady())
            {
                await _vm.ConnectAndAttachAsync(_requestedSessionId).ConfigureAwait(false);
                _requestedSessionId = null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn(BridgeHost.LogTag, "chat connect failed: " + ex.GetType().Name);
        }
    }

    private async Task LoadOlderHistoryAsync()
    {
        _followBottom = false;
        try
        {
            await _vm.LoadOlderHistoryAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OpenModelPicker()
    {
        var state = _vm.State;
        if (!state.IsConnected || !state.Capabilities.SwitchModelInSession)
        {
            if (View is { } root)
            {
                Snackbar.Make(root, Resource.String.chat_model_switch_unsupported, Snackbar.LengthShort).Show();
            }
            return;
        }

        _ = RefreshAndShowModelPickerAsync();
    }

    private async Task RefreshAndShowModelPickerAsync()
    {
        if (_vm.State.ModelGroups is null or { Count: 0 })
        {
            await _vm.RefreshModelCatalogAsync().ConfigureAwait(false);
        }
        Activity?.RunOnUiThread(() =>
        {
            if (View is null || !IsAdded)
            {
                return;
            }
            ModelPickerDialog.Show(ParentFragmentManager);
        });
    }

    private void ShowCredentialOnboardingDialog()
    {
        var ctx = RequireContext();
        var content = LayoutInflater.From(ctx)!.Inflate(Resource.Layout.dialog_credential_onboarding, null)!;
        var dialog = new MaterialAlertDialogBuilder(ctx)
            .SetTitle(Resource.String.credentials_onboarding_title)!
            .SetView(content)!
            .SetNegativeButton(Resource.String.credentials_onboarding_later, (_, _) => _vm.DismissCredentialOnboarding())!
            .SetPositiveButton(Resource.String.credentials_onboarding_action, (_, _) => OpenCredentialsScreen())!
            .Create()!;
        dialog.CancelEvent += (_, _) => _vm.DismissCredentialOnboarding();
        dialog.Show();
    }

    private void OpenCredentialsScreen()
    {
        _openedCredentials = true;
        StartActivity(new Intent(RequireContext(), typeof(CredentialsActivity)));
    }

    private static bool IsCredentialFailure(string? code) =>
        code is "CredentialsMissing" or "session_error.credentials_missing" or "session/provider-credentials-unavailable";

    private async Task SendOrCancelAsync()
    {
        var state = _vm.State;
        if (state.Transcript.Phase is RunPhase.Starting or RunPhase.Streaming && state.Capabilities.CancelSession)
        {
            if (IsStopGuardActive())
            {
                return;
            }
            _sendGuardUntilMs = Android.OS.SystemClock.ElapsedRealtime() + 800;
            await _vm.CancelSessionAsync().ConfigureAwait(false);
            return;
        }

        var text = _input.Text?.Trim() ?? string.Empty;
        if (text.Length == 0 && _attachments.Count == 0)
        {
            return;
        }

        if (text.StartsWith('/') && _attachments.Count == 0 && state.Capabilities.SlashCommands)
        {
            _sendGuardUntilMs = Android.OS.SystemClock.ElapsedRealtime() + 800;
            _input.Text = string.Empty;
            _followBottom = true;
            await _vm.RunSlashCommandAsync(text).ConfigureAwait(false);
            return;
        }

        var images = _attachments.ToArray();
        _lastSubmittedRequest = new PromptSendRequest(
            RequestId: string.Empty,
            SessionId: state.ActiveSession.SessionId,
            Text: text,
            Images: images.Length > 0 ? images : null);

        // 发送后 800ms 内忽略停止 / 发送按钮的二次误触，给流式界面一个稳定窗口。
        _sendGuardUntilMs = Android.OS.SystemClock.ElapsedRealtime() + 800;
        _input.Text = string.Empty;
        _attachments.Clear();
        RenderAttachments();
        _followBottom = true;

        await _vm.SendWithImagesAsync(text, images).ConfigureAwait(false);
    }

    private async Task RecoverLastSendAsync(bool userConfirmedResend)
    {
        if (_vm.State.LastSend is not { } last || _lastSubmittedRequest is null)
        {
            return;
        }
        await _vm.RetryOrConfirmSendAsync(last.ClientOpId, _lastSubmittedRequest, userConfirmedResend).ConfigureAwait(false);
    }

    private void OpenImagePicker()
    {
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("image/*");
        intent.PutExtra(Intent.ExtraAllowMultiple, true);
#pragma warning disable CS0618
        StartActivityForResult(intent, RequestImagePick);
#pragma warning restore CS0618
    }

    private async Task LoadPickedImagesAsync(IReadOnlyList<global::Android.Net.Uri> uris)
    {
        var ctx = Context;
        if (ctx is null)
        {
            return;
        }
        var resolver = ctx.ContentResolver!;
        var limits = ImageUploadLimitsSpec.Default;
        var added = new List<PromptAttachmentImage>();

        await Task.Run(() =>
        {
            foreach (var uri in uris)
            {
                if (_attachments.Count + added.Count >= limits.MaxImagesPerMessage)
                {
                    break;
                }
                var mime = resolver.GetType(uri) ?? "image/png";
                if (!limits.AllowedMediaTypes.Contains(mime))
                {
                    continue;
                }
                using var stream = resolver.OpenInputStream(uri);
                if (stream is null)
                {
                    continue;
                }
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                if (ms.Length == 0 || ms.Length > limits.MaxImageBytes)
                {
                    continue;
                }
                var b64 = Convert.ToBase64String(ms.GetBuffer(), 0, (int)ms.Length);
                added.Add(new PromptAttachmentImage(mime, b64, $"img-{_attachments.Count + added.Count + 1}"));
            }
        }).ConfigureAwait(false);

        Activity?.RunOnUiThread(() =>
        {
            if (View is null)
            {
                return;
            }
            if (added.Count == 0)
            {
                Snackbar.Make(View, Rows.Fmt(ctx, Resource.String.chat_image_rejected, "FORMAT_OR_SIZE"), Snackbar.LengthShort).Show();
                return;
            }
            var candidate = _attachments.Concat(added).ToArray();
            if (ImageAttachmentValidator.Validate(candidate, limits) is { } err)
            {
                Snackbar.Make(View, Rows.Fmt(ctx, Resource.String.chat_image_rejected, err.Code.ToString()), Snackbar.LengthShort).Show();
                return;
            }
            _attachments.AddRange(added);
            RenderAttachments();
        });
    }

    private void ScrollToBottomAndFollow()
    {
        _followBottom = true;
        _jumpBottomBar.Visibility = ViewStates.Gone;
        ScrollToLatestItem();
    }

    /// <summary>
    /// 回到列表最后一项并强制贴到底部：最后一项（例如超长 Turn Footer / 工具组）可能高于视口，
    /// 仅调用 ScrollToPosition 会停在项顶部；布局完成后用超大负偏移把该项底部对齐到列表底部。
    /// </summary>
    private void ScrollToLatestItem()
    {
        if (_adapter.ItemCount <= 0)
        {
            return;
        }

        _list.ScrollToPosition(_adapter.ItemCount - 1);
        _list.Post(() =>
        {
            if (_adapter.ItemCount <= 0 || _list.LayoutParameters is null)
            {
                return;
            }
            var last = _adapter.ItemCount - 1;
            _layoutManager.ScrollToPositionWithOffset(last, -100000);
        });
    }

    /// <summary>距底部超过 480dp 且用户已脱离贴底状态时显示停靠式回到底部提示条，绝不悬浮遮挡消息文字。</summary>
    private void UpdateScrollToBottomVisibility(RecyclerView recyclerView)
    {
        if (_jumpBottomBar is null)
        {
            return;
        }
        if (_followBottom)
        {
            // 贴底跟随时无需计算距离，直接隐藏提示条，避免流式输出期间每帧做滚动范围测量。
            _jumpBottomBar.Visibility = ViewStates.Gone;
            return;
        }
        var show = DistanceFromBottom(recyclerView) >= DpToPx(recyclerView, 480f);
        _jumpBottomBar.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
    }

    private static int DistanceFromBottom(RecyclerView recyclerView) =>
        Math.Max(
            0,
            recyclerView.ComputeVerticalScrollRange()
            - recyclerView.ComputeVerticalScrollExtent()
            - recyclerView.ComputeVerticalScrollOffset());

    private static int DpToPx(RecyclerView recyclerView, float dp) =>
        (int)Math.Round(dp * recyclerView.Context!.Resources!.DisplayMetrics!.Density);

    /// <summary>系统返回键优先收起斜杠面板 / 快捷 Tray，而不是直接离开对话页。</summary>
    private sealed class PanelBackCallback(ChatFragment owner) : OnBackPressedCallback(false)
    {
        public override void HandleOnBackPressed()
        {
            if (owner._slashExpanded)
            {
                owner._slashExpanded = false;
                owner._slashAutoFromInput = false;
                owner._slashFilterQuery = string.Empty;
                owner.RenderSlashBar(owner._vm.State);
                return;
            }
            if (owner._trayExpanded)
            {
                owner.SetComposerTrayExpanded(false);
            }
        }
    }

    private sealed class ChatScrollListener(ChatFragment owner) : RecyclerView.OnScrollListener
    {
        public override void OnScrollStateChanged(RecyclerView recyclerView, int newState)
        {
            base.OnScrollStateChanged(recyclerView, newState);
            if (newState == RecyclerView.ScrollStateDragging)
            {
                // 用户一开始拖拽就立刻暂停自动贴底，把滚动控制权交还给手指。
                owner._followBottom = false;
            }
            else if (newState == RecyclerView.ScrollStateIdle
                && DistanceFromBottom(recyclerView) <= DpToPx(recyclerView, 48f))
            {
                // 停止滚动且已回到距底部 48dp 以内时恢复贴底。
                owner._followBottom = true;
            }
            owner.UpdateScrollToBottomVisibility(recyclerView);
        }

        public override void OnScrolled(RecyclerView recyclerView, int dx, int dy)
        {
            base.OnScrolled(recyclerView, dx, dy);
            owner.UpdateScrollToBottomVisibility(recyclerView);
        }
    }
}
