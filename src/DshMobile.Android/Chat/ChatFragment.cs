using Android.Content;
using Android.Content.Res;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using AndroidX.Core.View;
using AndroidX.RecyclerView.Widget;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;
using DshMobile.Agent.Presentation;
using DshMobile.Agent.Rendering;
using DshMobile.App.Bridge;
using DshMobile.App.Credentials;
using DshMobile.App.Ui;
using DshMobile.Runtime.Hosting;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Chip;
using Google.Android.Material.Dialog;
using Google.Android.Material.FloatingActionButton;
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
    private View _sessionHeader = null!;
    private View _headerDivider = null!;
    private TextView _sessionTitle = null!;
    private TextView _sessionSubtitle = null!;
    private View _composerActions = null!;
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
    private FloatingActionButton _scrollToBottom = null!;
    private View _slashBar = null!;
    private ChipGroup _slashChips = null!;
    private View _attachmentsBar = null!;
    private LinearLayout _attachmentsList = null!;
    private EditText _input = null!;
    private MaterialButton _addBtn = null!;
    private MaterialButton _sendBtn = null!;
    private MaterialButton _attachImageBtn = null!;
    private MaterialButton _slashToggleBtn = null!;

    private bool _followBottom = true;
    private bool _slashExpanded;
    private bool _isRemote;
    private bool _connectInitiated;
    private bool _cacheLoadInitiated;
    private bool _onboardingPrompted;
    private bool _openedCredentials;
    private string? _requestedSessionId;
    private string? _pendingFocusEventId;
    private int _renderPosted;
    private PromptSendRequest? _lastSubmittedRequest;
    private IReadOnlyList<SlashCommandDescriptorView> _renderedSlash = [];

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        var view = inflater.Inflate(Resource.Layout.fragment_chat, container, false)!;
        _bridge = DshMobileApplication.Current.Bridge;
        _isRemote = Arguments?.GetBoolean(ChatActivity.ExtraIsRemote, false) ?? false;
        _vm = _isRemote ? (_bridge.Agent.RemoteChat ?? _bridge.Agent.Chat) : _bridge.Agent.Chat;
        _requestedSessionId = Arguments?.GetString(ChatActivity.ExtraSessionId);
        _pendingFocusEventId = Arguments?.GetString(ChatActivity.ExtraFocusEventId);

        _toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.chat_toolbar)!;
        _toolbar.NavigationClick += (_, _) => Activity?.Finish();
        _toolbar.MenuItemClick += (_, e) => OnMenuClicked(e.Item?.ItemId ?? 0);

        _sessionHeader = view.FindViewById(Resource.Id.chat_session_header)!;
        _headerDivider = view.FindViewById(Resource.Id.chat_header_divider)!;
        _sessionTitle = view.FindViewById<TextView>(Resource.Id.chat_session_title)!;
        _sessionSubtitle = view.FindViewById<TextView>(Resource.Id.chat_session_subtitle)!;
        _sessionHeader.Click += (_, _) => OnSessionHeaderClicked();

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
        _adapter = new ChatTimelineAdapter(
            _mdCache,
            action => Activity?.RunOnUiThread(action),
            (eventId, decision) => _ = _vm.AnswerApprovalAsync(eventId, decision),
            eventId => _ = _vm.UndoApprovalAsync(eventId),
            (eventId, batch) => _ = _vm.AnswerQuestionAsync(eventId, batch),
            eventId => _ = _vm.CancelQuestionAsync(eventId),
            OpenWorkspaceFile);
        _list.SetAdapter(_adapter);
        _list.AddOnScrollListener(new ChatScrollListener(this));

        _empty = view.FindViewById(Resource.Id.chat_empty)!;
        _empty.FindViewById<ImageView>(Resource.Id.empty_icon)!.SetImageResource(Resource.Drawable.ic_chat);
        _empty.FindViewById<TextView>(Resource.Id.empty_text)!.SetText(Resource.String.chat_empty_subtitle);

        _scrollToBottom = view.FindViewById<FloatingActionButton>(Resource.Id.chat_scroll_to_bottom)!;
        _scrollToBottom.Click += (_, _) => ScrollToBottomAndFollow();

        _slashBar = view.FindViewById(Resource.Id.chat_slash_bar)!;
        _slashChips = view.FindViewById<ChipGroup>(Resource.Id.chat_slash_chips)!;
        _attachmentsBar = view.FindViewById(Resource.Id.chat_attachments_bar)!;
        _attachmentsList = view.FindViewById<LinearLayout>(Resource.Id.chat_attachments_list)!;

        _input = view.FindViewById<EditText>(Resource.Id.chat_input)!;
        _composerActions = view.FindViewById(Resource.Id.chat_composer_actions)!;
        if (savedInstanceState?.GetString(DraftInputKey) is { } restoredDraft)
        {
            _input.Text = restoredDraft;
        }
        _slashExpanded = savedInstanceState?.GetBoolean(SlashExpandedKey, false) ?? false;
        _input.EditorAction += (_, e) =>
        {
            if (e.ActionId == ImeAction.Send)
            {
                e.Handled = true;
                _ = SendOrCancelAsync();
            }
        };

        _addBtn = view.FindViewById<MaterialButton>(Resource.Id.chat_add_btn)!;
        _sendBtn = view.FindViewById<MaterialButton>(Resource.Id.chat_send_btn)!;
        _attachImageBtn = view.FindViewById<MaterialButton>(Resource.Id.chat_attach_image_btn)!;
        _slashToggleBtn = view.FindViewById<MaterialButton>(Resource.Id.chat_slash_toggle_btn)!;

        _addBtn.Click += (_, _) => ToggleSlashBar();
        _slashToggleBtn.Click += (_, _) => ToggleSlashBar();
        _attachImageBtn.Click += (_, _) => OpenImagePicker();
        _sendBtn.Click += (_, _) => _ = SendOrCancelAsync();

        // 横屏弹起软键盘时收起非核心辅助条，避免挤压消息列表高度（F6）。
        ViewCompat.SetOnApplyWindowInsetsListener(view, new Edges.Listener((_, insets) =>
        {
            var isLandscape = Resources?.Configuration?.Orientation == global::Android.Content.Res.Orientation.Landscape;
            var imeVisible = insets.IsVisible(WindowInsetsCompat.Type.Ime());
            var compact = isLandscape && imeVisible;
            var visibility = compact ? ViewStates.Gone : ViewStates.Visible;
            _sessionHeader.Visibility = visibility;
            _headerDivider.Visibility = visibility;
            _composerActions.Visibility = visibility;
            return insets;
        }));

        RenderAttachments();
        return view;
    }

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

    private void OpenWorkspaceFile(string relativePath, bool openGitDiff)
    {
        StartActivity(MainActivity.CreateWorkspaceIntent(RequireContext(), relativePath, openGitDiff));
    }

    private void OnVmStateChanged(ChatUiState state) => ScheduleRender();
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
        _toolbar.Title = _isRemote && _bridge.Agent.RemoteHostLabel is { Length: > 0 } remoteLabel
            ? Rows.Fmt(ctx, Resource.String.remote_chat_title_fmt, remoteLabel)
            : GetString(Resource.String.chat_title);
        _sessionTitle.Text = string.IsNullOrWhiteSpace(s.SessionTitle)
            ? GetString(Resource.String.chat_session_default_title)
            : s.SessionTitle;
        _sessionSubtitle.Text = string.IsNullOrWhiteSpace(s.ModelLabel)
            ? GetString(Resource.String.chat_session_subtitle_default)
            : $"{s.ModelLabel} · {GetString(Resource.String.chat_session_subtitle_default)}";

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

        var items = new List<ChatTimelineItem>(s.Transcript.Rows.Count + s.PendingRequests.Count);
        foreach (var row in s.Transcript.Rows)
        {
            items.Add(new ChatRowTimelineItem(row, s.Transcript.Revision));
        }
        foreach (var req in s.PendingRequests)
        {
            items.Add(new PendingRequestTimelineItem(req));
        }

        _adapter.Submit(items);
        _empty.Visibility = items.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
        if (_pendingFocusEventId is { } focusId)
        {
            var focusIndex = _adapter.FindPendingRequestPosition(focusId);
            if (focusIndex >= 0)
            {
                _pendingFocusEventId = null;
                _followBottom = false;
                _list.ScrollToPosition(focusIndex);
                _scrollToBottom.Visibility = focusIndex < items.Count - 1 ? ViewStates.Visible : ViewStates.Gone;
            }
        }
        if (_followBottom && items.Count > 0)
        {
            _list.ScrollToPosition(items.Count - 1);
            _scrollToBottom.Visibility = ViewStates.Gone;
        }

        var isRunningTurn = s.Transcript.Phase is RunPhase.Starting or RunPhase.Streaming;
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

        _attachImageBtn.Visibility = s.Capabilities.PromptImages ? ViewStates.Visible : ViewStates.Gone;
        _slashToggleBtn.Visibility = s.Capabilities.SlashCommands ? ViewStates.Visible : ViewStates.Gone;
        _addBtn.Enabled = s.Capabilities.SlashCommands || s.Capabilities.PromptImages;
        RenderSlashBar(s);
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

    private void RenderSlashBar(ChatUiState s)
    {
        if (!s.Capabilities.SlashCommands || !_slashExpanded || s.SlashCommands.Count == 0)
        {
            _slashBar.Visibility = ViewStates.Gone;
            return;
        }
        _slashBar.Visibility = ViewStates.Visible;
        if (ReferenceEquals(_renderedSlash, s.SlashCommands))
        {
            return;
        }
        _renderedSlash = s.SlashCommands;
        _slashChips.RemoveAllViews();
        var inflater = LayoutInflater.From(RequireContext())!;
        foreach (var cmd in s.SlashCommands)
        {
            var chip = (Chip)inflater.Inflate(Resource.Layout.item_chat_slash_chip, _slashChips, false)!;
            chip.Text = EnsureLeadingSlash(cmd.Name);
            chip.Click += (_, _) => OnSlashChipClicked(cmd);
            _slashChips.AddView(chip);
        }
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
        var inflater = LayoutInflater.From(RequireContext())!;
        for (var i = 0; i < _attachments.Count; i++)
        {
            var index = i;
            var img = _attachments[i];
            var chip = (Chip)inflater.Inflate(Resource.Layout.item_chat_attachment, _attachmentsList, false)!;
            chip.Text = string.IsNullOrWhiteSpace(img.Name) ? $"image-{index + 1}" : img.Name;
            chip.SetOnCloseIconClickListener(new ActionClickListener(() =>
            {
                if (index < _attachments.Count)
                {
                    _attachments.RemoveAt(index);
                    RenderAttachments();
                }
            }));
            _attachmentsList.AddView(chip);
        }
    }

    private void ToggleSlashBar()
    {
        _slashExpanded = !_slashExpanded;
        RenderSlashBar(_vm.State);
    }

    private void OnSlashChipClicked(SlashCommandDescriptorView cmd)
    {
        var commandName = EnsureLeadingSlash(cmd.Name);
        if (string.IsNullOrWhiteSpace(cmd.InputHint))
        {
            _ = _vm.RunSlashCommandAsync(commandName);
            _slashExpanded = false;
            RenderSlashBar(_vm.State);
            return;
        }
        _input.Text = $"{commandName} ";
        _input.SetSelection(_input.Text?.Length ?? 0);
        _input.RequestFocus();
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
    }

    private void OnSessionHeaderClicked()
    {
        if (_vm.State.IsConnected && _vm.State.Capabilities.SwitchModelInSession)
        {
            OpenModelPicker();
            return;
        }
        _ = EnsureConnectedAsync();
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
            ModelPickerDialog.Show(
                RequireContext(),
                _vm.State.ModelGroups,
                _vm.State.CurrentModel,
                spec => _ = SelectModelAndNotifyAsync(spec),
                OpenCredentialsScreen);
        });
    }

    private async Task SelectModelAndNotifyAsync(ModelSelectionSpec spec)
    {
        await _vm.SelectModelAsync(spec).ConfigureAwait(false);
        Activity?.RunOnUiThread(() =>
        {
            if (View is { } root && _vm.State.ModelLabel is { } label && _vm.State.FailureCode is null)
            {
                Snackbar.Make(root, Rows.Fmt(RequireContext(), Resource.String.chat_model_switched, label), Snackbar.LengthShort).Show();
            }
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
        _scrollToBottom.Visibility = ViewStates.Gone;
        if (_adapter.ItemCount > 0)
        {
            _list.SmoothScrollToPosition(_adapter.ItemCount - 1);
        }
    }

    private sealed class ChatScrollListener(ChatFragment owner) : RecyclerView.OnScrollListener
    {
        public override void OnScrollStateChanged(RecyclerView recyclerView, int newState)
        {
            base.OnScrollStateChanged(recyclerView, newState);
            if (!recyclerView.CanScrollVertically(1))
            {
                owner._followBottom = true;
                owner._scrollToBottom.Visibility = ViewStates.Gone;
            }
            else if (newState == RecyclerView.ScrollStateDragging)
            {
                owner._followBottom = false;
                owner._scrollToBottom.Visibility = ViewStates.Visible;
            }
        }
    }

    private sealed class ActionClickListener(Action onClick) : Java.Lang.Object, View.IOnClickListener
    {
        public void OnClick(View? v) => onClick();
    }
}
