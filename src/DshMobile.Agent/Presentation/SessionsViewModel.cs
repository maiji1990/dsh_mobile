using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;

namespace DshMobile.Agent.Presentation;

/// <summary>会话列表单条展示项，融合最近标题、运行状态、子 Agent 与待审批/问答角标（N-05-03）。</summary>
public sealed record SessionListItemView(
    string SessionId,
    string DisplayTitle,
    long UpdatedAtUnixMs,
    bool Running,
    bool Blank,
    bool AgentAvailable,
    int PendingRequestCount,
    string? ParentSessionId,
    string? Cwd,
    string? AgentPreset,
    string? FormatStatus);

/// <summary>原生会话列表页不可变状态快照（N-05-03）。</summary>
public sealed record SessionsUiState(
    bool IsLoading,
    bool IsConnected,
    string SearchQuery,
    IReadOnlyList<SessionListItemView> Sessions,
    string? ActiveSessionId,
    string? FailureCode,
    bool CanListSessions,
    bool CanCancelSession);

/// <summary>会话列表向业务层派发的动作契约（N-05-03）。</summary>
public interface ISessionsActions
{
    Task RefreshAsync(CancellationToken ct = default);
    void SetSearchQuery(string? query);
    Task<Outcome<string>> CreateSessionAsync(CancellationToken ct = default);
    Task<Outcome<string>> RenameSessionAsync(string sessionId, string newTitle, CancellationToken ct = default);
    Task<Outcome<string>> ForkSessionAsync(string sessionId, CancellationToken ct = default);
    Task<Outcome<Unit>> CancelSessionAsync(string sessionId, CancellationToken ct = default);
}

/// <summary>
/// 纯托管会话列表与生命周期 ViewModel（N-05-03）：
/// 聚合 <see cref="ISessionGateway"/>、<see cref="RequestsEngine"/> 与 <see cref="IEventGateway"/> 实时状态。
/// </summary>
public sealed class SessionsViewModel : ISessionsActions, IAsyncDisposable
{
    private readonly IDshGateway _gateway;
    private readonly RequestsEngine _requests;
    private readonly Lock _stateLock = new();
    private readonly SemaphoreSlim _opGate = new(1, 1);

    private IReadOnlyList<SessionSummaryView> _rawSessions = [];
    private IReadOnlyList<PendingInteractionCard> _pendingCards = [];
    private SessionsUiState _state;
    private int _disposed;

    public SessionsViewModel(IDshGateway gateway, RequestsEngine requests)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _requests = requests ?? throw new ArgumentNullException(nameof(requests));
        _pendingCards = _requests.Snapshot();
        _state = new SessionsUiState(
            IsLoading: false,
            IsConnected: false,
            SearchQuery: string.Empty,
            Sessions: [],
            ActiveSessionId: null,
            FailureCode: null,
            CanListSessions: gateway.Capabilities.ListSessions,
            CanCancelSession: gateway.Capabilities.CancelSession);

        _requests.RequestsChanged += OnRequestsChanged;
        _gateway.ForwardedEvent += OnForwardedEvent;
    }

    public SessionsUiState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public event Action<SessionsUiState>? StateChanged;

    public void SetActiveSessionId(string? sessionId)
    {
        PublishState(s => s with { ActiveSessionId = sessionId });
    }

    public void NotifyHostStopping()
    {
        PublishState(s => s with { IsConnected = false, IsLoading = false });
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            PublishState(s => s with { IsLoading = true, FailureCode = null });

            var capsOutcome = await _gateway.ProbeCapabilitiesAsync(ct).ConfigureAwait(false);
            if (!capsOutcome.IsOk)
            {
                PublishState(s => s with
                {
                    IsLoading = false,
                    IsConnected = false,
                    FailureCode = capsOutcome.Error!.DiagnosticKey ?? capsOutcome.Error.Code.ToString(),
                });
                return;
            }

            var caps = capsOutcome.Value!;
            if (!caps.ListSessions)
            {
                PublishState(s => s with
                {
                    IsLoading = false,
                    IsConnected = true,
                    CanListSessions = false,
                    CanCancelSession = caps.CancelSession,
                    FailureCode = DshFailureCode.Unsupported.ToString(),
                });
                return;
            }

            var listOutcome = await _gateway.ListSessionsAsync(ct).ConfigureAwait(false);
            if (!listOutcome.IsOk)
            {
                PublishState(s => s with
                {
                    IsLoading = false,
                    IsConnected = true,
                    CanListSessions = caps.ListSessions,
                    CanCancelSession = caps.CancelSession,
                    FailureCode = listOutcome.Error!.DiagnosticKey ?? listOutcome.Error.Code.ToString(),
                });
                return;
            }

            lock (_stateLock)
            {
                _rawSessions = listOutcome.Value!;
            }
            RebuildFilteredState(
                isLoading: false,
                isConnected: true,
                canList: caps.ListSessions,
                canCancel: caps.CancelSession,
                failureCode: null);
        }
        finally
        {
            _opGate.Release();
        }
    }

    public void SetSearchQuery(string? query)
    {
        var normalized = query ?? string.Empty;
        lock (_stateLock)
        {
            if (string.Equals(_state.SearchQuery, normalized, StringComparison.Ordinal))
            {
                return;
            }
            _state = _state with { SearchQuery = normalized };
        }
        RebuildFilteredState(
            isLoading: State.IsLoading,
            isConnected: State.IsConnected,
            canList: State.CanListSessions,
            canCancel: State.CanCancelSession,
            failureCode: State.FailureCode);
    }

    public async Task<Outcome<string>> CreateSessionAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var outcome = await _gateway.CreateOrResumeSessionAsync(new CreateOrResumeSessionRequest(), ct).ConfigureAwait(false);
        if (!outcome.IsOk)
        {
            PublishState(s => s with { FailureCode = outcome.Error!.DiagnosticKey ?? outcome.Error.Code.ToString() });
            return outcome;
        }
        await RefreshAsync(ct).ConfigureAwait(false);
        return outcome;
    }

    public async Task<Outcome<string>> RenameSessionAsync(string sessionId, string newTitle, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(newTitle))
        {
            return Outcome<string>.Fail(DshFailureCode.InvalidState, "session_rename.empty");
        }

        var outcome = await _gateway.RenameSessionAsync(sessionId, newTitle.Trim(), ct).ConfigureAwait(false);
        if (!outcome.IsOk)
        {
            PublishState(s => s with { FailureCode = outcome.Error!.DiagnosticKey ?? outcome.Error.Code.ToString() });
            return outcome;
        }

        lock (_stateLock)
        {
            _rawSessions = _rawSessions
                .Select(item => string.Equals(item.SessionId, sessionId, StringComparison.Ordinal)
                    ? item with { Title = outcome.Value }
                    : item)
                .ToArray();
        }
        RebuildFilteredState(State.IsLoading, State.IsConnected, State.CanListSessions, State.CanCancelSession, null);
        return outcome;
    }

    public async Task<Outcome<string>> ForkSessionAsync(string sessionId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Outcome<string>.Fail(DshFailureCode.InvalidState, "session_fork.missing_id");
        }

        var outcome = await _gateway.ForkSessionAsync(sessionId, null, ct).ConfigureAwait(false);
        if (!outcome.IsOk)
        {
            PublishState(s => s with { FailureCode = outcome.Error!.DiagnosticKey ?? outcome.Error.Code.ToString() });
            return outcome;
        }

        await RefreshAsync(ct).ConfigureAwait(false);
        return outcome;
    }

    public async Task<Outcome<Unit>> CancelSessionAsync(string sessionId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Outcome<Unit>.Fail(DshFailureCode.InvalidState, "session_cancel.missing_id");
        }
        if (!_gateway.Capabilities.CancelSession)
        {
            PublishState(s => s with { FailureCode = DshFailureCode.Unsupported.ToString() });
            return Outcome<Unit>.Fail(DshFailureCode.Unsupported, "session_cancel.unsupported");
        }

        var outcome = await _gateway.CancelTurnAsync(sessionId, ct).ConfigureAwait(false);
        if (!outcome.IsOk)
        {
            PublishState(s => s with { FailureCode = outcome.Error!.DiagnosticKey ?? outcome.Error.Code.ToString() });
            return outcome;
        }

        await RefreshAsync(ct).ConfigureAwait(false);
        return outcome;
    }

    private void OnRequestsChanged(IReadOnlyList<PendingInteractionCard> cards)
    {
        lock (_stateLock)
        {
            _pendingCards = cards;
        }
        RebuildFilteredState(State.IsLoading, State.IsConnected, State.CanListSessions, State.CanCancelSession, State.FailureCode);
    }

    private void OnForwardedEvent(ForwardedGatewayEvent evt)
    {
        switch (evt)
        {
            case SessionStatusGatewayEvent status:
                lock (_stateLock)
                {
                    _rawSessions = _rawSessions
                        .Select(item => string.Equals(item.SessionId, status.SessionId, StringComparison.Ordinal)
                            ? item with { Running = status.Running }
                            : item)
                        .ToArray();
                }
                RebuildFilteredState(State.IsLoading, State.IsConnected, State.CanListSessions, State.CanCancelSession, State.FailureCode);
                break;

            case SessionActivityGatewayEvent activity:
                lock (_stateLock)
                {
                    _rawSessions = _rawSessions
                        .Select(item => string.Equals(item.SessionId, activity.SessionId, StringComparison.Ordinal)
                            ? item with { UpdatedAtUnixMs = activity.UpdatedAtUnixMs }
                            : item)
                        .ToArray();
                }
                RebuildFilteredState(State.IsLoading, State.IsConnected, State.CanListSessions, State.CanCancelSession, State.FailureCode);
                break;

            case SessionRemovedGatewayEvent removed:
                lock (_stateLock)
                {
                    _rawSessions = _rawSessions
                        .Where(item => !string.Equals(item.SessionId, removed.SessionId, StringComparison.Ordinal))
                        .ToArray();
                }
                RebuildFilteredState(State.IsLoading, State.IsConnected, State.CanListSessions, State.CanCancelSession, State.FailureCode);
                break;
        }
    }

    private void RebuildFilteredState(
        bool isLoading,
        bool isConnected,
        bool canList,
        bool canCancel,
        string? failureCode)
    {
        SessionsUiState next;
        lock (_stateLock)
        {
            var query = _state.SearchQuery.Trim();
            var pendingBySession = _pendingCards
                .GroupBy(c => c.Scope.SessionId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            var filtered = _rawSessions
                .OrderByDescending(s => s.UpdatedAtUnixMs)
                .Select(s =>
                {
                    var title = !string.IsNullOrWhiteSpace(s.Title) ? s.Title! : s.SessionId;
                    pendingBySession.TryGetValue(s.SessionId, out var pendingCount);
                    return new SessionListItemView(
                        SessionId: s.SessionId,
                        DisplayTitle: title,
                        UpdatedAtUnixMs: s.UpdatedAtUnixMs,
                        Running: s.Running,
                        Blank: s.Blank,
                        AgentAvailable: s.AgentAvailable,
                        PendingRequestCount: pendingCount,
                        ParentSessionId: s.ParentSessionId,
                        Cwd: s.Cwd,
                        AgentPreset: s.AgentPreset,
                        FormatStatus: s.FormatStatus);
                })
                .Where(item => query.Length == 0 ||
                    item.DisplayTitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    item.SessionId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    (item.Cwd?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToArray();

            next = _state with
            {
                IsLoading = isLoading,
                IsConnected = isConnected,
                Sessions = filtered,
                FailureCode = failureCode,
                CanListSessions = canList,
                CanCancelSession = canCancel,
            };
            if (next == _state)
            {
                return;
            }
            _state = next;
        }
        StateChanged?.Invoke(next);
    }

    private void PublishState(Func<SessionsUiState, SessionsUiState> transform)
    {
        SessionsUiState next;
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

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }
        _requests.RequestsChanged -= OnRequestsChanged;
        _gateway.ForwardedEvent -= OnForwardedEvent;
        _opGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
