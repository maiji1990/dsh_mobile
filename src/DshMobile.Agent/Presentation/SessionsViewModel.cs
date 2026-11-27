using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;

namespace DshMobile.Agent.Presentation;

/// <summary>会话列表筛选模式：全部（默认不含已归档）、运行中、待处理、已归档。</summary>
public enum SessionFilterMode
{
    All,
    Running,
    Pending,
    Archived,
}

/// <summary>会话列表单条展示项，融合最近标题、运行状态、子 Agent、归档状态与待审批/问答角标（N-05-03）。</summary>
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
    string? FormatStatus,
    string? Origin = null,
    bool IsArchived = false);

/// <summary>原生会话列表页不可变状态快照（N-05-03）。</summary>
public sealed record SessionsUiState(
    bool IsLoading,
    bool IsConnected,
    string SearchQuery,
    IReadOnlyList<SessionListItemView> Sessions,
    string? ActiveSessionId,
    string? FailureCode,
    bool CanListSessions,
    bool CanCancelSession,
    IReadOnlyList<SessionListItemView>? CachedSessions = null,
    SessionFilterMode FilterMode = SessionFilterMode.All,
    int ArchivedCount = 0,
    bool CanArchiveSessions = false,
    string? LastRemovedSessionId = null)
{
    public IReadOnlyList<SessionListItemView> EffectiveCachedSessions => CachedSessions ?? [];
}

/// <summary>会话列表向业务层派发的动作契约（N-05-03）。</summary>
public interface ISessionsActions
{
    Task RefreshAsync(CancellationToken ct = default);
    Task LoadCachedSessionsAsync(CancellationToken ct = default);
    void SetSearchQuery(string? query);
    void SetFilterMode(SessionFilterMode mode);
    Task<Outcome<string>> CreateSessionAsync(CancellationToken ct = default);
    Task<Outcome<string>> RenameSessionAsync(string sessionId, string newTitle, CancellationToken ct = default);
    Task<Outcome<string>> ForkSessionAsync(string sessionId, CancellationToken ct = default);
    Task<Outcome<Unit>> CancelSessionAsync(string sessionId, CancellationToken ct = default);
    Task<Outcome<bool>> ArchiveSessionAsync(string sessionId, bool stopActivity = false, CancellationToken ct = default);
    Task<Outcome<bool>> UnarchiveSessionAsync(string sessionId, CancellationToken ct = default);
    Task<Outcome<bool>> DeleteSessionAsync(string sessionId, bool stopActivity = false, CancellationToken ct = default);
}

/// <summary>
/// 纯托管会话列表与生命周期 ViewModel（N-05-03）：
/// 聚合 <see cref="ISessionGateway"/>、<see cref="RequestsEngine"/> 与 <see cref="IEventGateway"/> 实时状态，
/// 支持服务端归档/取消归档、真实本地缓存清理与本机隐藏删除，并在未连接时提供 <see cref="KeptTranscriptStore"/> 离线只读快照摘要。
/// </summary>
public sealed class SessionsViewModel : ISessionsActions, IAsyncDisposable
{
    private readonly IDshGateway _gateway;
    private readonly RequestsEngine _requests;
    private readonly KeptTranscriptStore? _keptStore;
    private readonly HiddenSessionStore? _hiddenStore;
    private readonly TranscriptEngine? _transcriptEngine;
    private readonly Lock _stateLock = new();
    private readonly SemaphoreSlim _opGate = new(1, 1);
    private readonly HashSet<string> _localHiddenIds = new(StringComparer.Ordinal);

    private IReadOnlyList<SessionSummaryView> _rawSessions = [];
    private IReadOnlyList<LoadedRecentSession> _cachedSessions = [];
    private IReadOnlyList<PendingInteractionCard> _pendingCards = [];
    private HashSet<string> _archivedIds = new(StringComparer.Ordinal);
    private IAsyncDisposable? _workspaceFollowSub;
    private SessionsUiState _state;
    private int _disposed;

    public SessionsViewModel(
        IDshGateway gateway,
        RequestsEngine requests,
        KeptTranscriptStore? keptStore = null,
        HiddenSessionStore? hiddenStore = null,
        TranscriptEngine? transcriptEngine = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _requests = requests ?? throw new ArgumentNullException(nameof(requests));
        _keptStore = keptStore;
        _hiddenStore = hiddenStore;
        _transcriptEngine = transcriptEngine;
        _pendingCards = _requests.Snapshot();
        _state = new SessionsUiState(
            IsLoading: false,
            IsConnected: false,
            SearchQuery: string.Empty,
            Sessions: [],
            ActiveSessionId: null,
            FailureCode: null,
            CanListSessions: gateway.Capabilities.ListSessions,
            CanCancelSession: gateway.Capabilities.CancelSession,
            CachedSessions: [],
            FilterMode: SessionFilterMode.All,
            ArchivedCount: 0,
            CanArchiveSessions: gateway.Capabilities.ArchiveSessions,
            LastRemovedSessionId: null);

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

    /// <summary>当会话被归档、删除或服务端移除时触发，便于双栏/聊天页联动清理展示。</summary>
    public event Action<string>? SessionArchivedOrRemoved;

    public void SetActiveSessionId(string? sessionId)
    {
        PublishState(s => s with { ActiveSessionId = sessionId });
    }

    public void NotifyHostStopping()
    {
        IAsyncDisposable? subToDispose;
        lock (_stateLock)
        {
            subToDispose = _workspaceFollowSub;
            _workspaceFollowSub = null;
        }
        if (subToDispose is not null)
        {
            _ = DisposeSubscriptionSafelyAsync(subToDispose);
        }
        PublishState(s => s with { IsConnected = false, IsLoading = false });
    }

    public async Task LoadCachedSessionsAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_hiddenStore is not null)
        {
            await _hiddenStore.LoadAsync(ct).ConfigureAwait(false);
        }
        if (_keptStore is null)
        {
            return;
        }
        var recent = await _keptStore.ListRecentAsync(maxCount: 20, ct).ConfigureAwait(false);
        lock (_stateLock)
        {
            _cachedSessions = recent;
        }
        var current = State;
        RebuildFilteredState(
            isLoading: current.IsLoading,
            isConnected: current.IsConnected,
            canList: current.CanListSessions,
            canCancel: current.CanCancelSession,
            canArchive: current.CanArchiveSessions,
            failureCode: current.FailureCode);
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            PublishState(s => s with { IsLoading = true, FailureCode = null });

            if (_hiddenStore is not null)
            {
                await _hiddenStore.LoadAsync(ct).ConfigureAwait(false);
            }

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
                    CanArchiveSessions = caps.ArchiveSessions,
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
                    CanArchiveSessions = caps.ArchiveSessions,
                    FailureCode = listOutcome.Error!.DiagnosticKey ?? listOutcome.Error.Code.ToString(),
                });
                return;
            }

            if (caps.ArchiveSessions)
            {
                var archivedOutcome = await _gateway.GetArchivedSessionIdsAsync(ct).ConfigureAwait(false);
                if (archivedOutcome.IsOk && archivedOutcome.Value is not null)
                {
                    lock (_stateLock)
                    {
                        _archivedIds = new HashSet<string>(archivedOutcome.Value, StringComparer.Ordinal);
                    }
                }
                await EnsureWorkspaceFollowSubscribedAsync(ct).ConfigureAwait(false);
            }
            else
            {
                IAsyncDisposable? oldSub;
                lock (_stateLock)
                {
                    _archivedIds.Clear();
                    oldSub = _workspaceFollowSub;
                    _workspaceFollowSub = null;
                }
                if (oldSub is not null)
                {
                    await DisposeSubscriptionSafelyAsync(oldSub).ConfigureAwait(false);
                }
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
                canArchive: caps.ArchiveSessions,
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
        var current = State;
        RebuildFilteredState(
            isLoading: current.IsLoading,
            isConnected: current.IsConnected,
            canList: current.CanListSessions,
            canCancel: current.CanCancelSession,
            canArchive: current.CanArchiveSessions,
            failureCode: current.FailureCode);
    }

    public void SetFilterMode(SessionFilterMode mode)
    {
        lock (_stateLock)
        {
            if (_state.FilterMode == mode)
            {
                return;
            }
            _state = _state with { FilterMode = mode };
        }
        var current = State;
        RebuildFilteredState(
            isLoading: current.IsLoading,
            isConnected: current.IsConnected,
            canList: current.CanListSessions,
            canCancel: current.CanCancelSession,
            canArchive: current.CanArchiveSessions,
            failureCode: current.FailureCode);
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
        var current = State;
        RebuildFilteredState(current.IsLoading, current.IsConnected, current.CanListSessions, current.CanCancelSession, current.CanArchiveSessions, null);
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

    /// <summary>归档指定会话，并同步清理该会话的本地缓存与内存 Transcript 句柄。</summary>
    public async Task<Outcome<bool>> ArchiveSessionAsync(string sessionId, bool stopActivity = false, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Outcome<bool>.Fail(DshFailureCode.InvalidState, "session_archive.missing_id");
        }

        var current = State;
        if (!current.CanArchiveSessions && !_gateway.Capabilities.ArchiveSessions)
        {
            PublishState(s => s with { FailureCode = DshFailureCode.Unsupported.ToString() });
            return Outcome<bool>.Fail(DshFailureCode.Unsupported, "session_archive.unsupported");
        }

        var archiveOutcome = await _gateway.ArchiveSessionAsync(sessionId, stopActivity, ct).ConfigureAwait(false);
        if (!archiveOutcome.IsOk)
        {
            PublishState(s => s with { FailureCode = archiveOutcome.Error!.DiagnosticKey ?? archiveOutcome.Error.Code.ToString() });
            return Outcome<bool>.Fail(archiveOutcome.Error!);
        }

        if (_keptStore is not null)
        {
            await _keptStore.DeleteBySessionIdAsync(sessionId, ct).ConfigureAwait(false);
        }
        if (_transcriptEngine is not null)
        {
            await _transcriptEngine.RemoveSessionsByIdAsync(sessionId, ct).ConfigureAwait(false);
        }

        lock (_stateLock)
        {
            _archivedIds = new HashSet<string>(archiveOutcome.Value ?? [], StringComparer.Ordinal);
            _archivedIds.Add(sessionId);
            _cachedSessions = _cachedSessions
                .Where(c => !string.Equals(c.Scope.SessionId, sessionId, StringComparison.Ordinal))
                .ToArray();
        }

        var latest = State;
        RebuildFilteredState(
            latest.IsLoading,
            latest.IsConnected,
            latest.CanListSessions,
            latest.CanCancelSession,
            latest.CanArchiveSessions || _gateway.Capabilities.ArchiveSessions,
            failureCode: null,
            lastRemovedSessionId: sessionId);
        SessionArchivedOrRemoved?.Invoke(sessionId);
        return Outcome<bool>.Ok(true);
    }

    /// <summary>取消归档指定会话，并同步刷新列表状态。</summary>
    public async Task<Outcome<bool>> UnarchiveSessionAsync(string sessionId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Outcome<bool>.Fail(DshFailureCode.InvalidState, "session_unarchive.missing_id");
        }

        var current = State;
        if (!current.CanArchiveSessions && !_gateway.Capabilities.ArchiveSessions)
        {
            PublishState(s => s with { FailureCode = DshFailureCode.Unsupported.ToString() });
            return Outcome<bool>.Fail(DshFailureCode.Unsupported, "session_unarchive.unsupported");
        }

        var unarchiveOutcome = await _gateway.UnarchiveSessionAsync(sessionId, ct).ConfigureAwait(false);
        if (!unarchiveOutcome.IsOk)
        {
            PublishState(s => s with { FailureCode = unarchiveOutcome.Error!.DiagnosticKey ?? unarchiveOutcome.Error.Code.ToString() });
            return Outcome<bool>.Fail(unarchiveOutcome.Error!);
        }

        lock (_stateLock)
        {
            _archivedIds = new HashSet<string>(unarchiveOutcome.Value ?? [], StringComparer.Ordinal);
            _archivedIds.Remove(sessionId);
        }

        var latest = State;
        RebuildFilteredState(
            latest.IsLoading,
            latest.IsConnected,
            latest.CanListSessions,
            latest.CanCancelSession,
            latest.CanArchiveSessions || _gateway.Capabilities.ArchiveSessions,
            failureCode: null);
        return Outcome<bool>.Ok(true);
    }

    /// <summary>
    /// 删除/移出指定会话：
    /// - 在线会话：若支持归档先调用服务端归档（若返回 <see cref="DshFailureCode.SessionActive"/> 则透传失败供 UI 确认停活）；
    ///   随后真实删除本地快照缓存、卸载内存句柄、持久化本机永久隐藏并从列表移除；
    /// - 离线缓存会话：真实删除本地快照缓存文件并从离线列表移除，不写远端。
    /// </summary>
    public async Task<Outcome<bool>> DeleteSessionAsync(string sessionId, bool stopActivity = false, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Outcome<bool>.Fail(DshFailureCode.InvalidState, "session_delete.missing_id");
        }

        var current = State;
        if (current.IsConnected)
        {
            if (current.CanArchiveSessions || _gateway.Capabilities.ArchiveSessions)
            {
                var archiveOutcome = await _gateway.ArchiveSessionAsync(sessionId, stopActivity, ct).ConfigureAwait(false);
                if (!archiveOutcome.IsOk && archiveOutcome.Error?.Code != DshFailureCode.SessionNotFound)
                {
                    PublishState(s => s with { FailureCode = archiveOutcome.Error!.DiagnosticKey ?? archiveOutcome.Error.Code.ToString() });
                    return Outcome<bool>.Fail(archiveOutcome.Error!);
                }

                if (archiveOutcome.IsOk && archiveOutcome.Value is not null)
                {
                    lock (_stateLock)
                    {
                        _archivedIds = new HashSet<string>(archiveOutcome.Value, StringComparer.Ordinal);
                    }
                }
            }

            if (_keptStore is not null)
            {
                await _keptStore.DeleteBySessionIdAsync(sessionId, ct).ConfigureAwait(false);
            }
            if (_transcriptEngine is not null)
            {
                await _transcriptEngine.RemoveSessionsByIdAsync(sessionId, ct).ConfigureAwait(false);
            }
            if (_hiddenStore is not null)
            {
                await _hiddenStore.HideAsync(sessionId, ct).ConfigureAwait(false);
            }

            lock (_stateLock)
            {
                _localHiddenIds.Add(sessionId);
                _rawSessions = _rawSessions
                    .Where(item => !string.Equals(item.SessionId, sessionId, StringComparison.Ordinal))
                    .ToArray();
                _cachedSessions = _cachedSessions
                    .Where(item => !string.Equals(item.Scope.SessionId, sessionId, StringComparison.Ordinal))
                    .ToArray();
            }

            var latestOnline = State;
            RebuildFilteredState(
                latestOnline.IsLoading,
                latestOnline.IsConnected,
                latestOnline.CanListSessions,
                latestOnline.CanCancelSession,
                latestOnline.CanArchiveSessions,
                failureCode: null,
                lastRemovedSessionId: sessionId);
            SessionArchivedOrRemoved?.Invoke(sessionId);
            return Outcome<bool>.Ok(true);
        }

        // 离线缓存会话模式：只物理删除本地快照文件并清理内存缓存列表，不写远端。
        if (_keptStore is not null)
        {
            await _keptStore.DeleteBySessionIdAsync(sessionId, ct).ConfigureAwait(false);
        }
        if (_transcriptEngine is not null)
        {
            await _transcriptEngine.RemoveSessionsByIdAsync(sessionId, ct).ConfigureAwait(false);
        }

        lock (_stateLock)
        {
            _cachedSessions = _cachedSessions
                .Where(item => !string.Equals(item.Scope.SessionId, sessionId, StringComparison.Ordinal))
                .ToArray();
        }

        var latestOffline = State;
        RebuildFilteredState(
            latestOffline.IsLoading,
            latestOffline.IsConnected,
            latestOffline.CanListSessions,
            latestOffline.CanCancelSession,
            latestOffline.CanArchiveSessions,
            failureCode: null,
            lastRemovedSessionId: sessionId);
        SessionArchivedOrRemoved?.Invoke(sessionId);
        return Outcome<bool>.Ok(true);
    }

    private async Task EnsureWorkspaceFollowSubscribedAsync(CancellationToken ct)
    {
        lock (_stateLock)
        {
            if (_workspaceFollowSub is not null)
            {
                return;
            }
        }

        var followOutcome = await _gateway.FollowWorkspaceAsync(OnWorkspaceFollowFrameAsync, ct).ConfigureAwait(false);
        if (!followOutcome.IsOk || followOutcome.Value is null)
        {
            return;
        }

        IAsyncDisposable? redundant = null;
        lock (_stateLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || _workspaceFollowSub is not null)
            {
                redundant = followOutcome.Value;
            }
            else
            {
                _workspaceFollowSub = followOutcome.Value;
            }
        }

        if (redundant is not null)
        {
            await DisposeSubscriptionSafelyAsync(redundant).ConfigureAwait(false);
        }
    }

    private ValueTask OnWorkspaceFollowFrameAsync(WorkspaceFollowFrame frame)
    {
        IReadOnlyList<string>? updatedArchivedIds = frame switch
        {
            WorkspaceBaselineFrame baseline => baseline.ArchivedSessionIds,
            WorkspaceArchivedChangedFrame changed => changed.ArchivedSessionIds,
            WorkspaceReplacedFrame replaced => replaced.ArchivedSessionIds,
            _ => null,
        };

        if (updatedArchivedIds is null)
        {
            return ValueTask.CompletedTask;
        }

        lock (_stateLock)
        {
            _archivedIds = new HashSet<string>(updatedArchivedIds, StringComparer.Ordinal);
        }

        var current = State;
        RebuildFilteredState(
            current.IsLoading,
            current.IsConnected,
            current.CanListSessions,
            current.CanCancelSession,
            current.CanArchiveSessions,
            current.FailureCode);
        return ValueTask.CompletedTask;
    }

    private void OnRequestsChanged(IReadOnlyList<PendingInteractionCard> cards)
    {
        lock (_stateLock)
        {
            _pendingCards = cards;
        }
        var current = State;
        RebuildFilteredState(current.IsLoading, current.IsConnected, current.CanListSessions, current.CanCancelSession, current.CanArchiveSessions, current.FailureCode);
    }

    private void OnForwardedEvent(ForwardedGatewayEvent evt)
    {
        var current = State;
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
                RebuildFilteredState(current.IsLoading, current.IsConnected, current.CanListSessions, current.CanCancelSession, current.CanArchiveSessions, current.FailureCode);
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
                RebuildFilteredState(current.IsLoading, current.IsConnected, current.CanListSessions, current.CanCancelSession, current.CanArchiveSessions, current.FailureCode);
                break;

            case SessionRemovedGatewayEvent removed:
                lock (_stateLock)
                {
                    _rawSessions = _rawSessions
                        .Where(item => !string.Equals(item.SessionId, removed.SessionId, StringComparison.Ordinal))
                        .ToArray();
                }
                RebuildFilteredState(
                    current.IsLoading,
                    current.IsConnected,
                    current.CanListSessions,
                    current.CanCancelSession,
                    current.CanArchiveSessions,
                    current.FailureCode,
                    lastRemovedSessionId: removed.SessionId);
                SessionArchivedOrRemoved?.Invoke(removed.SessionId);
                break;
        }
    }

    private void RebuildFilteredState(
        bool isLoading,
        bool isConnected,
        bool canList,
        bool canCancel,
        bool canArchive,
        string? failureCode,
        string? lastRemovedSessionId = null)
    {
        var hiddenFromStore = _hiddenStore?.GetHiddenIdsSnapshot();
        SessionsUiState next;
        lock (_stateLock)
        {
            var query = _state.SearchQuery.Trim();
            var filterMode = _state.FilterMode;
            var pendingBySession = _pendingCards
                .GroupBy(c => c.Scope.SessionId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            bool IsHidden(string sessionId) =>
                _localHiddenIds.Contains(sessionId) ||
                (hiddenFromStore is not null && hiddenFromStore.Contains(sessionId));

            var visibleRaw = _rawSessions
                .Where(s => !IsHidden(s.SessionId))
                .OrderByDescending(s => s.UpdatedAtUnixMs)
                .Select(s =>
                {
                    var title = !string.IsNullOrWhiteSpace(s.Title) ? s.Title! : s.SessionId;
                    pendingBySession.TryGetValue(s.SessionId, out var pendingCount);
                    var isArchived = _archivedIds.Contains(s.SessionId);
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
                        FormatStatus: s.FormatStatus,
                        Origin: s.Origin,
                        IsArchived: isArchived);
                })
                .ToArray();

            var archivedCount = visibleRaw.Count(item => item.IsArchived);

            var filtered = visibleRaw
                .Where(item => filterMode switch
                {
                    SessionFilterMode.Archived => item.IsArchived,
                    SessionFilterMode.Running => !item.IsArchived && item.Running,
                    SessionFilterMode.Pending => !item.IsArchived && item.PendingRequestCount > 0,
                    _ => !item.IsArchived,
                })
                .Where(item => query.Length == 0 ||
                    item.DisplayTitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    item.SessionId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    (item.Cwd?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToArray();

            var filteredCached = _cachedSessions
                .Where(c => !IsHidden(c.Scope.SessionId))
                .Select(c => new SessionListItemView(
                    SessionId: c.Scope.SessionId,
                    DisplayTitle: !string.IsNullOrWhiteSpace(c.SessionTitle) ? c.SessionTitle! : c.Scope.SessionId,
                    UpdatedAtUnixMs: c.Record.SavedAtUnixMs,
                    Running: false,
                    Blank: c.Record.Rows.Count == 0,
                    AgentAvailable: false,
                    PendingRequestCount: 0,
                    ParentSessionId: null,
                    Cwd: null,
                    AgentPreset: null,
                    FormatStatus: null,
                    Origin: null,
                    IsArchived: _archivedIds.Contains(c.Scope.SessionId)))
                .Where(item => query.Length == 0 ||
                    item.DisplayTitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    item.SessionId.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            next = _state with
            {
                IsLoading = isLoading,
                IsConnected = isConnected,
                Sessions = filtered,
                FailureCode = failureCode,
                CanListSessions = canList,
                CanCancelSession = canCancel,
                CachedSessions = filteredCached,
                ArchivedCount = archivedCount,
                CanArchiveSessions = canArchive,
                LastRemovedSessionId = lastRemovedSessionId ?? _state.LastRemovedSessionId,
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

    private static async Task DisposeSubscriptionSafelyAsync(IAsyncDisposable subscription)
    {
        try
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _requests.RequestsChanged -= OnRequestsChanged;
        _gateway.ForwardedEvent -= OnForwardedEvent;

        IAsyncDisposable? workspaceSub;
        lock (_stateLock)
        {
            workspaceSub = _workspaceFollowSub;
            _workspaceFollowSub = null;
        }
        if (workspaceSub is not null)
        {
            await DisposeSubscriptionSafelyAsync(workspaceSub).ConfigureAwait(false);
        }
        _opGate.Dispose();
    }
}
