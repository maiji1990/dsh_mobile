using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;

namespace DshMobile.Agent.Presentation;

/// <summary>
/// 纯托管聊天会话 ViewModel（N-04-01）：驻留应用级 ConnectionScope，免疫 Activity 重建与深色切换；
/// 串联 <see cref="IDshGateway"/>、<see cref="TranscriptEngine"/>、<see cref="RequestsEngine"/>、
/// <see cref="SendStateMachine"/> 与 <see cref="HistoryReconciler"/>，对外只暴露不可变 <see cref="ChatUiState"/>。
/// </summary>
public sealed class ChatViewModel : IChatActions, IAsyncDisposable
{
    public static readonly IReadOnlyList<SlashCommandDescriptorView> BuiltinSlashCommands =
    [
        new SlashCommandDescriptorView("/compact", "压缩当前会话上下文", null, false),
        new SlashCommandDescriptorView("/permission", "切换或查看会话权限模式", "<mode>", false),
        new SlashCommandDescriptorView("/plan", "进入分步规划模式", "<goal>", false),
        new SlashCommandDescriptorView("/goal", "设置长程持续执行目标", "<goal>", false),
    ];

    private readonly IDshGateway _gateway;
    private readonly TranscriptEngine _transcripts;
    private readonly RequestsEngine _requests;
    private readonly SendStateMachine _sends;
    private readonly HistoryReconciler _reconciler;
    private readonly KeptTranscriptStore? _keptStore;
    private readonly HiddenSessionStore? _hiddenStore;
    private readonly string _runtimeId;
    private readonly string _profileId;
    private readonly Lock _stateLock = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly HashSet<string> _dismissedOnboardingSessions = new(StringComparer.Ordinal);

    private string _clientId = "local-client";
    private TranscriptHandle? _activeHandle;
    private IDisposable? _transcriptSub;
    private IAsyncDisposable? _followSub;
    private bool _uiVisible;
    private ChatUiState _state;
    private int _disposed;

    public ChatViewModel(
        IDshGateway gateway,
        TranscriptEngine transcripts,
        RequestsEngine requests,
        SendStateMachine sends,
        HistoryReconciler reconciler,
        KeptTranscriptStore? keptStore = null,
        string runtimeId = "local",
        string profileId = "web",
        HiddenSessionStore? hiddenStore = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _transcripts = transcripts ?? throw new ArgumentNullException(nameof(transcripts));
        _requests = requests ?? throw new ArgumentNullException(nameof(requests));
        _sends = sends ?? throw new ArgumentNullException(nameof(sends));
        _reconciler = reconciler ?? throw new ArgumentNullException(nameof(reconciler));
        _keptStore = keptStore;
        _hiddenStore = hiddenStore;
        _runtimeId = string.IsNullOrWhiteSpace(runtimeId) ? "local" : runtimeId;
        _profileId = string.IsNullOrWhiteSpace(profileId) ? "web" : profileId;

        var initialKey = new SessionKey(_runtimeId, _profileId, "pending");
        _state = new ChatUiState(
            ActiveSession: initialKey,
            SessionTitle: string.Empty,
            ModelLabel: null,
            IsConnected: false,
            IsReconciling: false,
            IsSending: false,
            StatusKey: "chat.status.idle",
            FailureCode: null,
            Capabilities: _gateway.Capabilities,
            SlashCommands: [],
            Transcript: new ChatSnapshot(0, initialKey, -1, RunPhase.Idle, false, false, null, [], TranscriptCounters.Zero),
            PendingRequests: [],
            LastSend: null);

        _gateway.ForwardedEvent += OnGatewayForwardedEvent;
        _requests.RequestsChanged += OnRequestsChanged;
        _sends.EntryChanged += OnSendEntryChanged;
        _transcripts.SessionRemoved += OnTranscriptSessionRemoved;
    }

    public ChatUiState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public event Action<ChatUiState>? StateChanged;

    public void UpdateConnectionIdentity(string clientId)
    {
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            Volatile.Write(ref _clientId, clientId);
        }
    }

    /// <summary>绑定/解绑前端页面可见状态；隐藏期间取消订阅以触发 `SkippedWhileHidden`，恢复后立即补发最新快照。</summary>
    public void SetUiVisible(bool visible)
    {
        IDisposable? oldSub = null;
        TranscriptHandle? handleToSubscribe = null;
        lock (_stateLock)
        {
            if (_uiVisible == visible)
            {
                return;
            }
            _uiVisible = visible;
            if (!visible)
            {
                oldSub = _transcriptSub;
                _transcriptSub = null;
            }
            else
            {
                handleToSubscribe = _activeHandle;
            }
        }

        oldSub?.Dispose();
        if (handleToSubscribe is not null && !handleToSubscribe.IsDisposed)
        {
            IDisposable? newSub = null;
            try
            {
                newSub = handleToSubscribe.Subscribe(OnTranscriptPublished);
            }
            catch (ObjectDisposedException)
            {
                // 并发场景下若句柄恰好被归档/删除释放，清空悬空引用等待下一次 ConnectAndAttach 重建。
                lock (_stateLock)
                {
                    if (ReferenceEquals(_activeHandle, handleToSubscribe))
                    {
                        _activeHandle = null;
                    }
                }
                return;
            }

            lock (_stateLock)
            {
                if (_uiVisible && ReferenceEquals(_activeHandle, handleToSubscribe))
                {
                    _transcriptSub?.Dispose();
                    _transcriptSub = newSub;
                    newSub = null;
                }
            }
            newSub?.Dispose();
        }
        else if (handleToSubscribe is { IsDisposed: true })
        {
            lock (_stateLock)
            {
                if (ReferenceEquals(_activeHandle, handleToSubscribe))
                {
                    _activeHandle = null;
                }
            }
        }
    }

    public async Task LoadCachedSessionAsync(string sessionId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_keptStore is null || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }
        var current = State;
        if (current.IsConnected && string.Equals(current.ActiveSession.SessionId, sessionId, StringComparison.Ordinal))
        {
            return;
        }

        var scope = new SessionKey(_runtimeId, _profileId, sessionId);
        var loaded = await _keptStore.LoadDetailedAsync(scope, ct).ConfigureAwait(false);
        if (loaded.Status != KeptTranscriptLoadStatus.Loaded || loaded.Record is null)
        {
            return;
        }

        var handle = _transcripts.CreateDetached(scope, loaded.Record);
        var snap = await handle.WaitForIdleDrainAsync(ct).ConfigureAwait(false);
        var title = !string.IsNullOrWhiteSpace(loaded.Record.SessionTitle)
            ? loaded.Record.SessionTitle!
            : sessionId;
        AttachHandle(handle, sessionTitle: title);
        PublishState(s => s.IsConnected && string.Equals(s.ActiveSession.SessionId, sessionId, StringComparison.Ordinal)
            ? s
            : s with
            {
                ActiveSession = scope,
                SessionTitle = title,
                StatusKey = "chat.status.cache_only",
                FailureCode = null,
                Transcript = snap,
            });
    }

    /// <summary>冷启 0.2s Cache-First 路径：从本地索引恢复最近未被隐藏的会话快照，无缓存时安静返回 false（N-05-04）。</summary>
    public async Task<bool> LoadMostRecentCachedSessionAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_keptStore is null)
        {
            return false;
        }
        var current = State;
        if (current.IsConnected || current.Transcript.Rows.Count > 0)
        {
            return current.Transcript.Rows.Count > 0;
        }

        if (_hiddenStore is not null)
        {
            await _hiddenStore.LoadAsync(ct).ConfigureAwait(false);
        }

        var recentList = await _keptStore.ListRecentAsync(maxCount: 20, ct).ConfigureAwait(false);
        LoadedRecentSession? loaded = null;
        foreach (var candidate in recentList)
        {
            if (_hiddenStore is null || !_hiddenStore.IsHidden(candidate.Scope.SessionId))
            {
                loaded = candidate;
                break;
            }
        }
        if (loaded is null)
        {
            return false;
        }

        var handle = _transcripts.CreateDetached(loaded.Scope, loaded.Record);
        var snap = await handle.WaitForIdleDrainAsync(ct).ConfigureAwait(false);
        var title = !string.IsNullOrWhiteSpace(loaded.SessionTitle)
            ? loaded.SessionTitle!
            : loaded.Scope.SessionId;
        AttachHandle(handle, sessionTitle: title);
        PublishState(s => s.IsConnected
            ? s
            : s with
            {
                ActiveSession = loaded.Scope,
                SessionTitle = title,
                StatusKey = "chat.status.cache_only",
                FailureCode = null,
                Transcript = snap,
            });
        return snap.Rows.Count > 0;
    }

    public Task ConnectAndAttachAsync(string? preferredSessionId = null, CancellationToken ct = default) =>
        ConnectAndAttachCoreAsync(preferredSessionId, forceCreateNew: false, ct);

    private async Task ConnectAndAttachCoreAsync(string? preferredSessionId, bool forceCreateNew, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            PublishState(s => s with { IsReconciling = true, StatusKey = "chat.status.connecting", FailureCode = null });

            var capsOutcome = await _gateway.ProbeCapabilitiesAsync(ct).ConfigureAwait(false);
            if (!capsOutcome.IsOk)
            {
                PublishState(s => s with
                {
                    IsConnected = false,
                    IsReconciling = false,
                    StatusKey = "chat.status.disconnected",
                    FailureCode = capsOutcome.Error!.Code.ToString(),
                });
                return;
            }

            var caps = capsOutcome.Value!;
            if (_hiddenStore is not null)
            {
                await _hiddenStore.LoadAsync(ct).ConfigureAwait(false);
            }

            var archivedSet = new HashSet<string>(StringComparer.Ordinal);
            if (caps.ArchiveSessions)
            {
                var archivedOutcome = await _gateway.GetArchivedSessionIdsAsync(ct).ConfigureAwait(false);
                if (archivedOutcome.IsOk && archivedOutcome.Value is not null)
                {
                    foreach (var archivedId in archivedOutcome.Value)
                    {
                        archivedSet.Add(archivedId);
                    }
                }
            }

            var sessionId = preferredSessionId;
            string? listedTitle = null;
            IReadOnlyList<SessionSummaryView>? listedSessions = null;
            if (!forceCreateNew && caps.ListSessions)
            {
                var listOutcome = await _gateway.ListSessionsAsync(ct).ConfigureAwait(false);
                if (listOutcome.IsOk && listOutcome.Value!.Count > 0)
                {
                    listedSessions = listOutcome.Value;
                    if (string.IsNullOrWhiteSpace(sessionId))
                    {
                        var selectable = listedSessions
                            .Where(x => !archivedSet.Contains(x.SessionId) && (_hiddenStore is null || !_hiddenStore.IsHidden(x.SessionId)))
                            .ToArray();
                        if (selectable.Length > 0)
                        {
                            var cachedSid = State.ActiveSession.SessionId;
                            var matchedCached = State.Transcript.IsDetachedCache && cachedSid is not "pending"
                                ? selectable.FirstOrDefault(x => string.Equals(x.SessionId, cachedSid, StringComparison.Ordinal))
                                : null;
                            var chosen = matchedCached ?? selectable.OrderByDescending(x => x.UpdatedAtUnixMs).First();
                            sessionId = chosen.SessionId;
                            listedTitle = chosen.Title;
                        }
                    }
                    else
                    {
                        listedTitle = listedSessions
                            .FirstOrDefault(x => string.Equals(x.SessionId, sessionId, StringComparison.Ordinal))
                            ?.Title;
                    }
                }
            }

            // DSH `session/create` 仅复用空白会话；传入已有非空会话 ID 会新建空白 UUID 从而覆盖历史（F17）。
            // 仅当显式新建（forceCreateNew）或服务端尚无目标已有会话时才调用 `session/create`。
            string resolvedSessionId;
            if (forceCreateNew || string.IsNullOrWhiteSpace(sessionId))
            {
                var createOutcome = await _gateway.CreateOrResumeSessionAsync(
                    new CreateOrResumeSessionRequest(SessionId: forceCreateNew ? null : sessionId),
                    ct).ConfigureAwait(false);
                if (!createOutcome.IsOk)
                {
                    var errCode = createOutcome.Error!.Code;
                    PublishState(s => s with
                    {
                        IsConnected = false,
                        IsReconciling = false,
                        Capabilities = caps,
                        NeedsCredentialOnboarding = errCode == DshFailureCode.CredentialsMissing || s.NeedsCredentialOnboarding,
                        StatusKey = "chat.status.session_failed",
                        FailureCode = errCode.ToString(),
                    });
                    return;
                }
                resolvedSessionId = createOutcome.Value!;
            }
            else
            {
                resolvedSessionId = sessionId!;
            }

            listedTitle ??= listedSessions?
                .FirstOrDefault(x => string.Equals(x.SessionId, resolvedSessionId, StringComparison.Ordinal))
                ?.Title;
            var scope = new SessionKey(_runtimeId, _profileId, resolvedSessionId);
            var handle = _transcripts.GetOrCreateSession(scope);
            AttachHandle(handle, sessionTitle: !string.IsNullOrWhiteSpace(listedTitle) ? listedTitle! : resolvedSessionId);

            // Best-effort 读取模型目录、凭据就绪状态与可用斜杠命令，不阻断聊天主流程。
            string? modelLabel = null;
            ModelSelectionSpec? currentModel = null;
            IReadOnlyList<ModelProviderGroupView>? modelGroups = null;
            if (caps.SwitchModelInSession)
            {
                var catalog = await _gateway.GetModelCatalogAsync(ct).ConfigureAwait(false);
                if (catalog.IsOk)
                {
                    currentModel = catalog.Value!.Default;
                    modelGroups = catalog.Value.Groups;
                    modelLabel = FormatModelLabel(currentModel);
                }
            }

            var needsOnboarding = await CheckNeedsCredentialOnboardingAsync(caps, modelGroups, resolvedSessionId, ct).ConfigureAwait(false);

            var slashCommands = BuiltinSlashCommands;
            if (caps.SlashCommands)
            {
                var listedCmds = await _gateway.ListCommandsAsync(resolvedSessionId, ct).ConfigureAwait(false);
                if (listedCmds.IsOk && listedCmds.Value!.Count > 0)
                {
                    slashCommands = listedCmds.Value;
                }
            }

            if (_followSub is not null)
            {
                await _followSub.DisposeAsync().ConfigureAwait(false);
                _followSub = null;
            }

            var reconcileOutcome = await _reconciler.AttachAndReconcileAsync(handle, ct: ct).ConfigureAwait(false);
            if (reconcileOutcome.IsOk)
            {
                _followSub = reconcileOutcome.Value;
            }

            var snap = await handle.WaitForIdleDrainAsync(ct).ConfigureAwait(false);
            var finalTitle = !string.IsNullOrWhiteSpace(snap.SessionTitle)
                ? snap.SessionTitle!
                : (!string.IsNullOrWhiteSpace(listedTitle) ? listedTitle! : resolvedSessionId);

            PublishState(s => s with
            {
                ActiveSession = scope,
                SessionTitle = finalTitle,
                ModelLabel = modelLabel ?? s.ModelLabel,
                CurrentModel = currentModel ?? s.CurrentModel,
                ModelGroups = modelGroups ?? s.ModelGroups,
                NeedsCredentialOnboarding = needsOnboarding,
                IsConnected = true,
                IsReconciling = false,
                Capabilities = caps,
                SlashCommands = caps.SlashCommands ? slashCommands : [],
                StatusKey = reconcileOutcome.IsOk ? "chat.status.ready" : "chat.status.partial_history",
                FailureCode = reconcileOutcome.IsOk ? null : reconcileOutcome.Error!.Code.ToString(),
                Transcript = snap,
                PendingRequests = FilterPendingForSession(scope, _requests.Snapshot()),
                IsArchived = archivedSet.Contains(resolvedSessionId),
            });

            await SaveCacheSnapshotAsync(snap, ct).ConfigureAwait(false);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public Task SendTextAsync(string text, CancellationToken ct = default) =>
        SendWithImagesAsync(text, [], ct);

    public async Task SendWithImagesAsync(
        string text,
        IReadOnlyList<PromptAttachmentImage> images,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(images);

        var current = State;
        if (!current.IsConnected || !current.Capabilities.Prompt)
        {
            PublishState(s => s with { FailureCode = DshFailureCode.Unavailable.ToString() });
            return;
        }
        if (string.IsNullOrWhiteSpace(text) && images.Count == 0)
        {
            return;
        }
        if (images.Count > 0 && ImageAttachmentValidator.Validate(images, ImageUploadLimitsSpec.Default) is { } imgErr)
        {
            PublishState(s => s with { FailureCode = imgErr.DiagnosticKey ?? imgErr.Code.ToString() });
            return;
        }

        var sessionId = current.ActiveSession.SessionId;
        var draft = _sends.CreateDraft(sessionId, text);
        var req = new PromptSendRequest(
            RequestId: draft.RequestId,
            SessionId: sessionId,
            Text: text,
            Mode: PromptDeliveryMode.Queue,
            Images: images.Count > 0 ? images : null);

        PublishState(s => s with { IsSending = true, FailureCode = null, LastSend = draft });
        var result = await _sends.DispatchAsync(draft.ClientOpId, req, ct).ConfigureAwait(false);
        PublishState(s => s with
        {
            IsSending = false,
            LastSend = result,
            FailureCode = result.Error?.DiagnosticKey ?? result.Error?.Code.ToString(),
        });
    }

    public async Task RetryOrConfirmSendAsync(
        string clientOpId,
        PromptSendRequest originalRequest,
        bool userConfirmedResend,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrEmpty(clientOpId);
        ArgumentNullException.ThrowIfNull(originalRequest);

        var current = State;
        OutboundSendEntry result;
        if (userConfirmedResend)
        {
            result = await _sends.ConfirmResendAsync(clientOpId, originalRequest, ct).ConfigureAwait(false);
        }
        else
        {
            result = await _sends.RecoverUnknownOutcomeAsync(clientOpId, originalRequest, current.Capabilities, ct).ConfigureAwait(false);
        }

        PublishState(s => s with
        {
            LastSend = result,
            FailureCode = result.Error?.DiagnosticKey ?? result.Error?.Code.ToString(),
        });
    }

    public async Task RunSlashCommandAsync(
        string commandLine,
        IReadOnlyList<PromptAttachmentImage>? images = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return;
        }
        var current = State;
        if (!current.IsConnected || !current.Capabilities.SlashCommands)
        {
            PublishState(s => s with { FailureCode = DshFailureCode.Unsupported.ToString() });
            return;
        }

        var normalizedCommand = commandLine.Trim();
        if (!normalizedCommand.StartsWith('/'))
        {
            normalizedCommand = "/" + normalizedCommand;
        }

        PublishState(s => s with { IsSending = true, FailureCode = null });
        var outcome = await _gateway.ExecuteCommandAsync(
            current.ActiveSession.SessionId,
            normalizedCommand,
            images,
            ct).ConfigureAwait(false);
        PublishState(s => s with
        {
            IsSending = false,
            FailureCode = outcome.IsOk ? null : outcome.Error!.DiagnosticKey ?? outcome.Error.Code.ToString(),
        });
    }

    public Task AnswerApprovalAsync(string eventId, ApprovalDecision decision, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _ = ct;
        // 通过 RequestsEngine 启动 1.5s Hold-to-undo 缓冲窗，避免小屏误触直接放行。
        _requests.HoldApprovalDecision(eventId, decision);
        return Task.CompletedTask;
    }

    public Task UndoApprovalAsync(string eventId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _ = ct;
        _requests.UndoHold(eventId);
        return Task.CompletedTask;
    }

    public async Task AnswerQuestionAsync(string eventId, UserQuestionAnswerBatch answer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var outcome = await _requests.AnswerQuestionAsync(eventId, answer, ct).ConfigureAwait(false);
        if (!outcome.IsOk)
        {
            PublishState(s => s with { FailureCode = outcome.Error!.DiagnosticKey ?? outcome.Error.Code.ToString() });
        }
    }

    public async Task CancelQuestionAsync(string eventId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var clientId = Volatile.Read(ref _clientId);
        _requests.CancelRequest(eventId);
        var outcome = await _gateway.RejectOpenQuestionAsync(clientId, eventId, "question.cancelled", ct).ConfigureAwait(false);
        if (!outcome.IsOk)
        {
            PublishState(s => s with { FailureCode = outcome.Error!.DiagnosticKey ?? outcome.Error.Code.ToString() });
        }
    }

    public async Task CancelSessionAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var current = State;
        if (!current.IsConnected || !current.Capabilities.CancelSession)
        {
            PublishState(s => s with { FailureCode = DshFailureCode.Unsupported.ToString() });
            return;
        }
        var outcome = await _gateway.CancelTurnAsync(current.ActiveSession.SessionId, ct).ConfigureAwait(false);
        if (!outcome.IsOk)
        {
            PublishState(s => s with { FailureCode = outcome.Error!.DiagnosticKey ?? outcome.Error.Code.ToString() });
        }
    }

    public Task RefreshHistoryAsync(CancellationToken ct = default) =>
        ConnectAndAttachCoreAsync(State.ActiveSession.SessionId is "pending" ? null : State.ActiveSession.SessionId, forceCreateNew: false, ct);

    /// <summary>冷读分页加载当前窗口之前的历史消息并无闪烁前置（N-05-03）。</summary>
    public async Task LoadOlderHistoryAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var current = State;
        if (!current.IsConnected ||
            !current.Capabilities.HistoryReplay ||
            current.IsLoadingOlderHistory ||
            !current.Transcript.IsPartialHistory ||
            current.Transcript.EarliestSeq is not { } earliest ||
            earliest <= 0)
        {
            return;
        }

        TranscriptHandle? handle;
        lock (_stateLock)
        {
            handle = _activeHandle;
        }
        if (handle is null || handle.Scope != current.ActiveSession)
        {
            return;
        }

        PublishState(s => s with { IsLoadingOlderHistory = true });
        try
        {
            var pageOutcome = await _gateway.LoadSessionEventsAsync(
                current.ActiveSession.SessionId,
                beforeSeq: earliest,
                maxMessages: 30,
                ct: ct).ConfigureAwait(false);
            if (!pageOutcome.IsOk)
            {
                PublishState(s => s with
                {
                    IsLoadingOlderHistory = false,
                    FailureCode = pageOutcome.Error!.DiagnosticKey ?? pageOutcome.Error.Code.ToString(),
                });
                return;
            }

            var page = pageOutcome.Value!;
            handle.PostPrependedPage(current.ActiveSession, page.Events, page.HasMore);
            var drained = await handle.WaitForIdleDrainAsync(ct).ConfigureAwait(false);
            PublishState(s => s.ActiveSession == current.ActiveSession
                ? s with { IsLoadingOlderHistory = false, Transcript = drained }
                : s with { IsLoadingOlderHistory = false });
        }
        catch (OperationCanceledException)
        {
            PublishState(s => s with { IsLoadingOlderHistory = false });
            throw;
        }
    }

    public Task SwitchOrCreateSessionAsync(string? sessionId = null, CancellationToken ct = default) =>
        ConnectAndAttachCoreAsync(sessionId, forceCreateNew: string.IsNullOrWhiteSpace(sessionId), ct);

    /// <summary>拉取最新模型目录（N-05-02）。</summary>
    public async Task RefreshModelCatalogAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var current = State;
        if (!current.IsConnected || !current.Capabilities.SwitchModelInSession)
        {
            PublishState(s => s with { FailureCode = DshFailureCode.Unsupported.ToString() });
            return;
        }

        var catalog = await _gateway.GetModelCatalogAsync(ct).ConfigureAwait(false);
        if (!catalog.IsOk)
        {
            PublishState(s => s with
            {
                FailureCode = catalog.Error!.DiagnosticKey ?? catalog.Error.Code.ToString(),
                NeedsCredentialOnboarding = catalog.Error.Code == DshFailureCode.CredentialsMissing || s.NeedsCredentialOnboarding,
            });
            return;
        }

        PublishState(s => s with
        {
            CurrentModel = catalog.Value!.Default,
            ModelGroups = catalog.Value.Groups,
            ModelLabel = FormatModelLabel(s.CurrentModel ?? catalog.Value.Default),
            FailureCode = null,
        });
    }

    /// <summary>运行中切换当前会话的模型与 Reasoning Effort（N-05-02）。</summary>
    public async Task SelectModelAsync(ModelSelectionSpec selection, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(selection);
        var current = State;
        if (!current.IsConnected || !current.Capabilities.SwitchModelInSession)
        {
            PublishState(s => s with { FailureCode = DshFailureCode.Unsupported.ToString() });
            return;
        }

        var outcome = await _gateway.SelectSessionModelAsync(current.ActiveSession.SessionId, selection, ct).ConfigureAwait(false);
        if (!outcome.IsOk)
        {
            var err = outcome.Error!;
            PublishState(s => s with
            {
                FailureCode = err.DiagnosticKey ?? err.Code.ToString(),
                NeedsCredentialOnboarding = err.Code == DshFailureCode.CredentialsMissing || s.NeedsCredentialOnboarding,
            });
            return;
        }

        var applied = outcome.Value!;
        PublishState(s => s with
        {
            CurrentModel = applied,
            ModelLabel = FormatModelLabel(applied),
            NeedsCredentialOnboarding = false,
            FailureCode = null,
        });
    }

    /// <summary>重新检查凭据配置状态（用户完成 N-05-01 配置返回聊天页后调用）。</summary>
    public async Task RefreshCredentialReadinessAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var current = State;
        if (!current.IsConnected)
        {
            return;
        }

        IReadOnlyList<ModelProviderGroupView>? groups = current.ModelGroups;
        ModelSelectionSpec? currentModel = current.CurrentModel;
        if (current.Capabilities.SwitchModelInSession)
        {
            var catalog = await _gateway.GetModelCatalogAsync(ct).ConfigureAwait(false);
            if (catalog.IsOk)
            {
                groups = catalog.Value!.Groups;
                currentModel ??= catalog.Value.Default;
            }
        }

        var needsOnboarding = await CheckNeedsCredentialOnboardingAsync(current.Capabilities, groups, current.ActiveSession.SessionId, ct).ConfigureAwait(false);
        PublishState(s => s with
        {
            NeedsCredentialOnboarding = needsOnboarding,
            ModelGroups = groups ?? s.ModelGroups,
            CurrentModel = currentModel ?? s.CurrentModel,
            ModelLabel = currentModel is not null ? FormatModelLabel(currentModel) : s.ModelLabel,
            FailureCode = needsOnboarding ? s.FailureCode : (s.FailureCode is "CredentialsMissing" or "session_error.credentials_missing" or "session/provider-credentials-unavailable" ? null : s.FailureCode),
        });
    }

    /// <summary>针对当前会话记录「本次已忽略」首次凭据引导（F19）。</summary>
    public void DismissCredentialOnboarding()
    {
        var sid = State.ActiveSession.SessionId;
        lock (_stateLock)
        {
            _dismissedOnboardingSessions.Add(sid);
        }
        PublishState(s => s with { NeedsCredentialOnboarding = false });
    }

    private async Task<bool> CheckNeedsCredentialOnboardingAsync(
        DshCapabilities caps,
        IReadOnlyList<ModelProviderGroupView>? groups,
        string sessionId,
        CancellationToken ct)
    {
        lock (_stateLock)
        {
            if (_dismissedOnboardingSessions.Contains(sessionId))
            {
                return false;
            }
        }

        // F15：若模型目录已有可用 Provider 模型（含 OAuth 账户模型、内置或自定义 Key），说明聊天就绪，不弹引导。
        if (groups is { Count: > 0 } && groups.Any(g => g.Models.Count > 0))
        {
            return false;
        }

        if (!caps.ManageCredentials)
        {
            return false;
        }
        var discover = await _gateway.DiscoverCredentialRefsAsync(ct).ConfigureAwait(false);
        var refs = discover.IsOk && discover.Value is { Count: > 0 }
            ? discover.Value
            : ["DEEPSEEK_API_KEY", "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "GEMINI_API_KEY", "OPENROUTER_API_KEY"];
        var desc = await _gateway.DescribeCredentialsAsync(refs, ct).ConfigureAwait(false);
        if (desc.IsOk)
        {
            return !desc.Value!.Values.Any(v => v.Configured);
        }
        return groups is { Count: 0 };
    }

    private static string FormatModelLabel(ModelSelectionSpec selection) =>
        string.IsNullOrWhiteSpace(selection.ReasoningEffort)
            ? $"{selection.Provider}/{selection.Model}"
            : $"{selection.Provider}/{selection.Model} · {selection.ReasoningEffort}";

    public async Task FlushPendingHoldsAsync(CancellationToken ct = default)
    {
        await _requests.FlushHeldAsync(ct).ConfigureAwait(false);
        await SaveCacheSnapshotAsync(State.Transcript, ct).ConfigureAwait(false);
    }

    /// <summary>归档当前会话，并清理其本地缓存快照。</summary>
    public async Task<Outcome<bool>> ArchiveCurrentSessionAsync(bool stopActivity = false, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var current = State;
        var sessionId = current.ActiveSession.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId) || string.Equals(sessionId, "pending", StringComparison.Ordinal))
        {
            return Outcome<bool>.Fail(DshFailureCode.InvalidState, "chat_archive.no_active_session");
        }
        if (!current.IsConnected || (!current.Capabilities.ArchiveSessions && !_gateway.Capabilities.ArchiveSessions))
        {
            PublishState(s => s with { FailureCode = DshFailureCode.Unsupported.ToString() });
            return Outcome<bool>.Fail(DshFailureCode.Unsupported, "chat_archive.unsupported");
        }

        var outcome = await _gateway.ArchiveSessionAsync(sessionId, stopActivity, ct).ConfigureAwait(false);
        if (!outcome.IsOk)
        {
            PublishState(s => s with { FailureCode = outcome.Error!.DiagnosticKey ?? outcome.Error.Code.ToString() });
            return Outcome<bool>.Fail(outcome.Error!);
        }

        if (_keptStore is not null)
        {
            await _keptStore.DeleteBySessionIdAsync(sessionId, ct).ConfigureAwait(false);
        }

        PublishState(s => string.Equals(s.ActiveSession.SessionId, sessionId, StringComparison.Ordinal)
            ? s with { IsArchived = true, FailureCode = null }
            : s);
        return Outcome<bool>.Ok(true);
    }

    /// <summary>取消归档当前会话。</summary>
    public async Task<Outcome<bool>> UnarchiveCurrentSessionAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var current = State;
        var sessionId = current.ActiveSession.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId) || string.Equals(sessionId, "pending", StringComparison.Ordinal))
        {
            return Outcome<bool>.Fail(DshFailureCode.InvalidState, "chat_unarchive.no_active_session");
        }
        if (!current.IsConnected || (!current.Capabilities.ArchiveSessions && !_gateway.Capabilities.ArchiveSessions))
        {
            PublishState(s => s with { FailureCode = DshFailureCode.Unsupported.ToString() });
            return Outcome<bool>.Fail(DshFailureCode.Unsupported, "chat_unarchive.unsupported");
        }

        var outcome = await _gateway.UnarchiveSessionAsync(sessionId, ct).ConfigureAwait(false);
        if (!outcome.IsOk)
        {
            PublishState(s => s with { FailureCode = outcome.Error!.DiagnosticKey ?? outcome.Error.Code.ToString() });
            return Outcome<bool>.Fail(outcome.Error!);
        }

        PublishState(s => string.Equals(s.ActiveSession.SessionId, sessionId, StringComparison.Ordinal)
            ? s with { IsArchived = false, FailureCode = null }
            : s);
        return Outcome<bool>.Ok(true);
    }

    /// <summary>删除/移出当前会话：在线时先归档（透传 SessionActive），随后停流、删除本地缓存、记录本机隐藏并重置聊天页状态。</summary>
    public async Task<Outcome<bool>> DeleteCurrentSessionAsync(bool stopActivity = false, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var current = State;
        var activeScope = current.ActiveSession;
        var sessionId = activeScope.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId) || string.Equals(sessionId, "pending", StringComparison.Ordinal))
        {
            return Outcome<bool>.Fail(DshFailureCode.InvalidState, "chat_delete.no_active_session");
        }

        if (current.IsConnected && (current.Capabilities.ArchiveSessions || _gateway.Capabilities.ArchiveSessions))
        {
            var archiveOutcome = await _gateway.ArchiveSessionAsync(sessionId, stopActivity, ct).ConfigureAwait(false);
            if (!archiveOutcome.IsOk && archiveOutcome.Error?.Code != DshFailureCode.SessionNotFound)
            {
                PublishState(s => s with { FailureCode = archiveOutcome.Error!.DiagnosticKey ?? archiveOutcome.Error.Code.ToString() });
                return Outcome<bool>.Fail(archiveOutcome.Error!);
            }
        }

        await DetachCurrentSessionStreamAndHandleAsync(ct).ConfigureAwait(false);
        await _transcripts.RemoveSessionAsync(activeScope, ct).ConfigureAwait(false);
        if (_keptStore is not null)
        {
            await _keptStore.DeleteBySessionIdAsync(sessionId, ct).ConfigureAwait(false);
        }
        if (current.IsConnected && _hiddenStore is not null)
        {
            await _hiddenStore.HideAsync(sessionId, ct).ConfigureAwait(false);
        }

        var emptyKey = new SessionKey(_runtimeId, _profileId, "pending");
        PublishState(s => s with
        {
            ActiveSession = emptyKey,
            SessionTitle = string.Empty,
            IsArchived = false,
            IsReconciling = false,
            IsSending = false,
            StatusKey = "chat.status.session_removed",
            FailureCode = null,
            Transcript = new ChatSnapshot(0, emptyKey, -1, RunPhase.Idle, false, false, null, [], TranscriptCounters.Zero),
            PendingRequests = [],
        });
        return Outcome<bool>.Ok(true);
    }

    private async Task DetachCurrentSessionStreamAndHandleAsync(CancellationToken ct)
    {
        _ = ct;
        IDisposable? oldSub;
        IAsyncDisposable? oldFollow;
        lock (_stateLock)
        {
            oldSub = _transcriptSub;
            _transcriptSub = null;
            _activeHandle = null;
            oldFollow = _followSub;
            _followSub = null;
        }
        oldSub?.Dispose();
        if (oldFollow is not null)
        {
            await DisposeFollowSafelyAsync(oldFollow).ConfigureAwait(false);
        }
    }

    private static async Task DisposeFollowSafelyAsync(IAsyncDisposable followSub)
    {
        try
        {
            await followSub.DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
        }
    }

    public async Task NotifyHostStoppingAsync(CancellationToken ct = default)
    {
        try
        {
            await _requests.FlushHeldAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
        }

        try
        {
            await SaveCacheSnapshotAsync(State.Transcript, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
        }

        if (_followSub is not null)
        {
            await _followSub.DisposeAsync().ConfigureAwait(false);
            _followSub = null;
        }
        _requests.MarkTransportLost();
        PublishState(s => s with { IsConnected = false, IsReconciling = false, StatusKey = "chat.status.disconnected" });
    }

    private void AttachHandle(TranscriptHandle handle, string sessionTitle)
    {
        IDisposable? oldSub;
        IDisposable? newSub = null;
        var visible = false;
        lock (_stateLock)
        {
            oldSub = _transcriptSub;
            _transcriptSub = null;
            _activeHandle = handle;
            visible = _uiVisible;
        }
        oldSub?.Dispose();

        if (visible && !handle.IsDisposed)
        {
            try
            {
                newSub = handle.Subscribe(OnTranscriptPublished);
            }
            catch (ObjectDisposedException)
            {
                lock (_stateLock)
                {
                    if (ReferenceEquals(_activeHandle, handle))
                    {
                        _activeHandle = null;
                    }
                }
                return;
            }
            lock (_stateLock)
            {
                if (_uiVisible && ReferenceEquals(_activeHandle, handle))
                {
                    _transcriptSub = newSub;
                    newSub = null;
                }
            }
            newSub?.Dispose();
        }

        PublishState(s => s with
        {
            ActiveSession = handle.Scope,
            SessionTitle = !string.IsNullOrWhiteSpace(handle.CurrentSnapshot.SessionTitle)
                ? handle.CurrentSnapshot.SessionTitle
                : sessionTitle,
            Transcript = handle.CurrentSnapshot,
        });
    }

    private void OnTranscriptPublished(ChatSnapshot snapshot)
    {
        PublishState(s => snapshot.Session == s.ActiveSession
            ? s with
            {
                Transcript = snapshot,
                SessionTitle = !string.IsNullOrWhiteSpace(snapshot.SessionTitle)
                    ? snapshot.SessionTitle
                    : s.SessionTitle,
            }
            : s);
        if (snapshot.Phase is RunPhase.Completed or RunPhase.Failed or RunPhase.Cancelled && !snapshot.IsDetachedCache)
        {
            _ = SaveCacheSnapshotAsync(snapshot, CancellationToken.None);
        }
    }

    private void OnRequestsChanged(IReadOnlyList<PendingInteractionCard> allCards)
    {
        PublishState(s => s with { PendingRequests = FilterPendingForSession(s.ActiveSession, allCards) });
    }

    private void OnSendEntryChanged(OutboundSendEntry entry)
    {
        PublishState(s => s with
        {
            LastSend = entry,
            IsSending = entry.Phase is OutboundSendPhase.Dispatching,
        });
    }

    private void OnGatewayForwardedEvent(ForwardedGatewayEvent evt)
    {
        var active = State.ActiveSession;
        var clientId = Volatile.Read(ref _clientId);
        switch (evt)
        {
            case ApprovalGatewayEvent ap:
            {
                var sid = string.IsNullOrWhiteSpace(ap.Request.AgentId) ? active.SessionId : ap.Request.AgentId;
                var scope = string.Equals(sid, active.SessionId, StringComparison.Ordinal)
                    ? active
                    : new SessionKey(active.RuntimeId, active.ProfileId, sid);
                _requests.IngestForwardedEvent(scope, clientId, evt);
                break;
            }
            case UserQuestionGatewayEvent uq:
            {
                var sid = string.IsNullOrWhiteSpace(uq.Request.AgentId) ? active.SessionId : uq.Request.AgentId;
                var scope = string.Equals(sid, active.SessionId, StringComparison.Ordinal)
                    ? active
                    : new SessionKey(active.RuntimeId, active.ProfileId, sid);
                _requests.IngestForwardedEvent(scope, clientId, evt);
                break;
            }
            case ForwardedEventCancelledGatewayEvent cancelled:
                _requests.CancelRequest(cancelled.EventId);
                break;
            case SessionErrorGatewayEvent sessionEvent when string.Equals(sessionEvent.SessionId, active.SessionId, StringComparison.Ordinal):
                var mapped = DshFailureMapper.FromSessionError(sessionEvent);
                PublishState(s => s with
                {
                    FailureCode = mapped.DiagnosticKey ?? mapped.Code.ToString(),
                    NeedsCredentialOnboarding = mapped.Code == DshFailureCode.CredentialsMissing || s.NeedsCredentialOnboarding,
                });
                break;
            case SessionRemovedGatewayEvent removed when string.Equals(removed.SessionId, active.SessionId, StringComparison.Ordinal):
            {
                IAsyncDisposable? followToStop;
                lock (_stateLock)
                {
                    followToStop = _followSub;
                    _followSub = null;
                }
                if (followToStop is not null)
                {
                    _ = DisposeFollowSafelyAsync(followToStop);
                }
                PublishState(s => string.Equals(s.ActiveSession.SessionId, removed.SessionId, StringComparison.Ordinal)
                    ? s with
                    {
                        IsReconciling = false,
                        IsSending = false,
                        StatusKey = "chat.status.session_removed",
                        FailureCode = DshFailureCode.SessionNotFound.ToString(),
                        PendingRequests = [],
                    }
                    : s);
                break;
            }
        }
    }

    private static PendingInteractionCard[] FilterPendingForSession(
        SessionKey scope,
        IReadOnlyList<PendingInteractionCard> cards)
    {
        if (cards.Count == 0)
        {
            return [];
        }
        return cards.Where(c => c.Scope == scope).ToArray();
    }

    private async Task SaveCacheSnapshotAsync(ChatSnapshot snapshot, CancellationToken ct)
    {
        if (_keptStore is null || snapshot.IsDetachedCache || snapshot.Rows.Count == 0 || snapshot.Session.SessionId == "pending")
        {
            return;
        }
        try
        {
            await _keptStore.SaveAsync(snapshot, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
        }
    }

    private void PublishState(Func<ChatUiState, ChatUiState> transform)
    {
        ChatUiState next;
        lock (_stateLock)
        {
            next = transform(_state);
            if (ReferenceEquals(next, _state) || next == _state)
            {
                return;
            }
            _state = next;
        }
        StateChanged?.Invoke(next);
    }

    private void OnTranscriptSessionRemoved(SessionKey removedScope)
    {
        IDisposable? oldSub = null;
        IAsyncDisposable? oldFollow = null;
        lock (_stateLock)
        {
            if (_activeHandle is null || _activeHandle.Scope != removedScope)
            {
                return;
            }
            oldSub = _transcriptSub;
            _transcriptSub = null;
            _activeHandle = null;
            oldFollow = _followSub;
            _followSub = null;
        }

        oldSub?.Dispose();
        if (oldFollow is not null)
        {
            _ = DisposeFollowSafelyAsync(oldFollow);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _gateway.ForwardedEvent -= OnGatewayForwardedEvent;
        _requests.RequestsChanged -= OnRequestsChanged;
        _sends.EntryChanged -= OnSendEntryChanged;
        _transcripts.SessionRemoved -= OnTranscriptSessionRemoved;

        IDisposable? sub;
        IAsyncDisposable? follow;
        lock (_stateLock)
        {
            sub = _transcriptSub;
            _transcriptSub = null;
            follow = _followSub;
            _followSub = null;
        }
        sub?.Dispose();
        if (follow is not null)
        {
            await follow.DisposeAsync().ConfigureAwait(false);
        }
        _connectGate.Dispose();
    }
}
