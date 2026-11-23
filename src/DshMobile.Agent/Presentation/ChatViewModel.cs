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
    private readonly string _runtimeId;
    private readonly string _profileId;
    private readonly Lock _stateLock = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);

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
        string profileId = "web")
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _transcripts = transcripts ?? throw new ArgumentNullException(nameof(transcripts));
        _requests = requests ?? throw new ArgumentNullException(nameof(requests));
        _sends = sends ?? throw new ArgumentNullException(nameof(sends));
        _reconciler = reconciler ?? throw new ArgumentNullException(nameof(reconciler));
        _keptStore = keptStore;
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
        if (handleToSubscribe is not null)
        {
            var newSub = handleToSubscribe.Subscribe(OnTranscriptPublished);
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
    }

    public async Task LoadCachedSessionAsync(string sessionId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_keptStore is null || string.IsNullOrWhiteSpace(sessionId))
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
        AttachHandle(handle, sessionTitle: sessionId);
        PublishState(s => s with
        {
            ActiveSession = scope,
            StatusKey = "chat.status.cache_only",
            FailureCode = null,
            Transcript = handle.CurrentSnapshot,
        });
    }

    public async Task ConnectAndAttachAsync(string? preferredSessionId = null, CancellationToken ct = default)
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
            var sessionId = preferredSessionId;
            if (string.IsNullOrWhiteSpace(sessionId) && caps.ListSessions)
            {
                var listOutcome = await _gateway.ListSessionsAsync(ct).ConfigureAwait(false);
                if (listOutcome.IsOk && listOutcome.Value!.Count > 0)
                {
                    sessionId = listOutcome.Value
                        .OrderByDescending(x => x.UpdatedAtUnixMs)
                        .Select(x => x.SessionId)
                        .FirstOrDefault();
                }
            }

            var createOutcome = await _gateway.CreateOrResumeSessionAsync(
                new CreateOrResumeSessionRequest(SessionId: sessionId),
                ct).ConfigureAwait(false);
            if (!createOutcome.IsOk)
            {
                PublishState(s => s with
                {
                    IsConnected = false,
                    IsReconciling = false,
                    Capabilities = caps,
                    StatusKey = "chat.status.session_failed",
                    FailureCode = createOutcome.Error!.Code.ToString(),
                });
                return;
            }

            var resolvedSessionId = createOutcome.Value!;
            var scope = new SessionKey(_runtimeId, _profileId, resolvedSessionId);
            var handle = _transcripts.GetOrCreateSession(scope);
            AttachHandle(handle, sessionTitle: resolvedSessionId);

            // Best-effort 读取模型标签与可用斜杠命令，不阻断聊天主流程。
            string? modelLabel = null;
            if (caps.SwitchModelInSession)
            {
                var catalog = await _gateway.GetModelCatalogAsync(ct).ConfigureAwait(false);
                if (catalog.IsOk)
                {
                    modelLabel = $"{catalog.Value!.Default.Provider}/{catalog.Value.Default.Model}";
                }
            }

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

            PublishState(s => s with
            {
                ActiveSession = scope,
                SessionTitle = resolvedSessionId,
                ModelLabel = modelLabel ?? s.ModelLabel,
                IsConnected = true,
                IsReconciling = false,
                Capabilities = caps,
                SlashCommands = caps.SlashCommands ? slashCommands : [],
                StatusKey = reconcileOutcome.IsOk ? "chat.status.ready" : "chat.status.partial_history",
                FailureCode = reconcileOutcome.IsOk ? null : reconcileOutcome.Error!.Code.ToString(),
                Transcript = handle.CurrentSnapshot,
                PendingRequests = FilterPendingForSession(scope, _requests.Snapshot()),
            });

            await SaveCacheSnapshotAsync(handle.CurrentSnapshot, ct).ConfigureAwait(false);
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
        ConnectAndAttachAsync(State.ActiveSession.SessionId is "pending" ? null : State.ActiveSession.SessionId, ct);

    public Task SwitchOrCreateSessionAsync(string? sessionId = null, CancellationToken ct = default) =>
        ConnectAndAttachAsync(sessionId, ct);

    public async Task FlushPendingHoldsAsync(CancellationToken ct = default)
    {
        await _requests.FlushHeldAsync(ct).ConfigureAwait(false);
        await SaveCacheSnapshotAsync(State.Transcript, ct).ConfigureAwait(false);
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

        if (visible)
        {
            newSub = handle.Subscribe(OnTranscriptPublished);
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
            case ApprovalGatewayEvent or UserQuestionGatewayEvent:
                _requests.IngestForwardedEvent(active, clientId, evt);
                break;
            case ForwardedEventCancelledGatewayEvent cancelled:
                _requests.CancelRequest(cancelled.EventId);
                break;
            case SessionErrorGatewayEvent sessionEvent when string.Equals(sessionEvent.SessionId, active.SessionId, StringComparison.Ordinal):
                var mapped = DshFailureMapper.FromSessionError(sessionEvent);
                PublishState(s => s with { FailureCode = mapped.DiagnosticKey ?? mapped.Code.ToString() });
                break;
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _gateway.ForwardedEvent -= OnGatewayForwardedEvent;
        _requests.RequestsChanged -= OnRequestsChanged;
        _sends.EntryChanged -= OnSendEntryChanged;

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
