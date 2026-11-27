using Android.Content;
using DshMobile.Agent.Engines;
using DshMobile.Agent.Presentation;
using DshMobile.Agent.Protocol;
using DshMobile.Agent.Transport;
using DshMobile.App.Notifications;
using DshMobile.Application.Runtime;
using DshMobile.Core.Diagnostics;
using DshMobile.Runtime;
using DshMobile.Runtime.Hosting;
using DshMobile.Runtime.Migration;
using DshMobile.Runtime.Remote;

namespace DshMobile.App.Bridge;

/// <summary>
/// 应用级原生 Agent 连接与引擎作用域（N-04-01 / N-06-03 / N-06-04 / N-08-02 / N-08-06）：
/// 驻留 <see cref="DshMobileApplication"/> 生命周期，免疫 Activity/Fragment 重建；
/// 管理本地 DSH 与远程 SSH 隧道（<see cref="RemoteSshSession"/>）双隔离原生会话栈，并实时同步工作租约（<see cref="WorkLeaseManager"/>）。
/// </summary>
public sealed class AgentConnectionScope : IDisposable
{
    private readonly DshRemoteConnection _connection;
    private readonly RemoteDshGatewayClient _gateway;
    private readonly TranscriptEngine _transcripts;
    private readonly RequestsEngine _requests;
    private readonly SendStateMachine _sends;
    private readonly HistoryReconciler _reconciler;
    private readonly KeptTranscriptStore _keptStore;
    private readonly HiddenSessionStore _hiddenStore;
    private readonly NpmRegistryPluginSearchService _pluginCatalogSearch;
    private readonly RuntimeLayout _layout;
    private readonly Action<string> _log;
    private readonly WorkLeaseManager? _leases;
    private readonly IDisposable _stoppingHook;
    private readonly Lock _leaseSyncGate = new();
    private readonly Lock _remoteGate = new();
    private readonly Lock _tokenGate = new();
    private IDshTokenSource? _activeTokenSource;
    private IDshTokenSource? _tokenSubscription;
    private IDisposable? _chatLease;
    private IDisposable? _reconcileLease;
    private IDisposable? _approvalLease;
    private RemoteTargetBundle? _remoteBundle;
    private int _disposed;

    public AgentConnectionScope(
        Context context,
        RuntimeCoordinator runtime,
        RuntimeLayout layout,
        Action<string> log,
        Func<CancellationToken, Task<RuntimeStartResult>>? restartHostAsync = null,
        WorkLeaseManager? leases = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(log);
        _layout = layout;
        _log = log;
        _leases = leases;

        var connOptions = new RemoteConnectionOptions(new Uri(RuntimeConstants.DshBaseUrl));
        _connection = new DshRemoteConnection(
            connOptions,
            () => Volatile.Read(ref _activeTokenSource)?.Token ?? runtime.Host?.Token,
            log: log);
        _gateway = new RemoteDshGatewayClient(_connection);
        _transcripts = new TranscriptEngine();
        _requests = new RequestsEngine(_gateway, _gateway, _transcripts);
        _sends = new SendStateMachine(_gateway, _gateway);
        _reconciler = new HistoryReconciler(_gateway, _gateway);
        var cacheDir = Path.Combine(layout.HostStateDir, "agent-cache");
        _keptStore = new KeptTranscriptStore(cacheDir);
        _hiddenStore = new HiddenSessionStore(cacheDir);
        // 启动阶段即预加载本机隐藏会话表，确保首屏与离线缓存过滤一致。
        _ = _hiddenStore.LoadAsync();
        Chat = new ChatViewModel(_gateway, _transcripts, _requests, _sends, _reconciler, _keptStore, hiddenStore: _hiddenStore);
        Credentials = new CredentialsViewModel(_gateway, Path.Combine(layout.HostStateDir, "custom-credential-refs.txt"));
        Sessions = new SessionsViewModel(_gateway, _requests, _keptStore, _hiddenStore, _transcripts);
        var pluginOps = new RuntimePluginOperations(
            runtime,
            restartHostAsync ?? (ct => runtime.RestartWebHostAsync(ct)));
        var pluginService = new PluginInventoryService(_gateway, pluginOps);
        _pluginCatalogSearch = new NpmRegistryPluginSearchService();
        Plugins = new PluginsViewModel(_gateway, pluginService, _pluginCatalogSearch);
        Inbox = new InboxViewModel(_requests, ResolveSessionTitle);
        RequestNotifications = new BackgroundRequestNotificationCoordinator(
            _requests,
            new AgentRequestNotificationSurface(context),
            ResolveSessionTitle);

        Chat.StateChanged += OnChatStateChanged;
        Sessions.StateChanged += OnSessionsStateChanged;
        _requests.RequestsChanged += OnRequestsChanged;
        _connection.GenerationChanged += OnGenerationChanged;
        _stoppingHook = runtime.AddHostStoppingHook(NotifyHostStoppingAsync);
    }

    public ChatViewModel Chat { get; }

    public CredentialsViewModel Credentials { get; }

    public SessionsViewModel Sessions { get; }

    public PluginsViewModel Plugins { get; }

    public InboxViewModel Inbox { get; }

    public BackgroundRequestNotificationCoordinator RequestNotifications { get; }

    /// <summary>供 <see cref="IdleStopPolicy"/> 判定本地与远程 Agent 是否全部处于 Idle 且无挂起请求。</summary>
    public bool IsAgentIdleForIdleStop
    {
        get
        {
            var chatState = Chat.State;
            if (chatState.IsReconciling || chatState.IsSending || chatState.Transcript.Phase != RunPhase.Idle)
            {
                return false;
            }
            if (_requests.Snapshot().Count > 0)
            {
                return false;
            }
            if (Sessions.State.Sessions.Any(s => s.Running))
            {
                return false;
            }
            lock (_remoteGate)
            {
                if (_remoteBundle is { } remote)
                {
                    var rs = remote.Chat.State;
                    if (rs.IsReconciling || rs.IsSending || rs.Transcript.Phase != RunPhase.Idle || remote.Requests.Snapshot().Count > 0)
                    {
                        return false;
                    }
                }
            }
            return true;
        }
    }

    /// <summary>当前绑定的远程 SSH 主机原生会话 ViewModel（未连接时为 null，N-08-06）。</summary>
    public ChatViewModel? RemoteChat
    {
        get
        {
            lock (_remoteGate)
            {
                return _remoteBundle?.Chat;
            }
        }
    }

    public string? RemoteHostLabel
    {
        get
        {
            lock (_remoteGate)
            {
                return _remoteBundle?.Label;
            }
        }
    }

    public bool IsRemoteEndpointReady
    {
        get
        {
            lock (_remoteGate)
            {
                return _remoteBundle is { Session: { IsConnected: true, LoopbackBaseUri: not null } s } &&
                       !string.IsNullOrEmpty(((IDshTokenSource)s).Token);
            }
        }
    }

    /// <summary>
    /// 按 ADR-0033 将已建立并启动远程 <c>dsh web</c> 的 <see cref="RemoteSshSession"/> 接入独立 <see cref="IDshGateway"/> 栈（N-08-06）。
    /// 不修改本地 <see cref="KeptTranscriptStore"/>，并保持 Keystore 不可导出私钥与主机指纹固定策略。
    /// </summary>
    public ChatViewModel AttachRemoteHost(string label, RemoteSshSession session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(session);

        RemoteTargetBundle? oldToDispose = null;
        RemoteTargetBundle? targetToReturn;
        lock (_remoteGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_remoteBundle is { } existing && string.Equals(existing.ProfileId, session.Profile.Id, StringComparison.Ordinal))
            {
                existing.RebindSession(label, session);
                targetToReturn = existing;
            }
            else
            {
                oldToDispose = _remoteBundle;
                targetToReturn = new RemoteTargetBundle(label, session, _log);
                _remoteBundle = targetToReturn;
            }
        }
        if (oldToDispose is not null)
        {
            _ = oldToDispose.DisposeAsync().AsTask();
        }
        targetToReturn.PrepareForConnect();
        return targetToReturn.Chat;
    }

    /// <summary>断开并清理远程 SSH 原生会话栈（SSH 主动断开或删除主机时调用）。</summary>
    public void DetachRemoteHost()
    {
        RemoteTargetBundle? old;
        lock (_remoteGate)
        {
            old = _remoteBundle;
            _remoteBundle = null;
        }
        if (old is not null)
        {
            _ = old.DisposeAsync().AsTask();
        }
    }

    /// <summary>只读盘点升级前留存的 <c>user-data/.dsh</c> 会话与凭据存在态（绝不读取密钥或改动原目录，N-05-05）。</summary>
    public WebProfileInventoryReport InspectWebProfileReadOnly()
    {
        var remoteIds = Sessions.State.Sessions
            .Select(s => s.SessionId)
            .ToHashSet(StringComparer.Ordinal);
        return WebProfileInventory.InspectReadOnly(_layout.UserDataDir, remoteIds);
    }

    /// <summary>
    /// 在 <see cref="DshHost.StartAsync"/> 就绪后执行 Route B 协议握手，挂载 <see cref="StartupTrace"/>
    /// 记录 <c>protocol.connected</c>/<c>protocol.initialized</c> 并在宿主重启后恢复重连状态（F4 / F14）。
    /// </summary>
    public async Task<DshHostResult> PerformStartupHandshakeAsync(
        IDshTokenSource tokenSource,
        Action<DshHostPhase> setPhase,
        StartupTrace? trace,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tokenSource);
        ArgumentNullException.ThrowIfNull(setPhase);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return new DshHostResult(false, "PROTOCOL_DISPOSED", "agent connection scope is disposed");
        }

        Volatile.Write(ref _activeTokenSource, tokenSource);
        // 客户端双保险：订阅启动令牌更新。若握手时令牌尚未就绪（HTTP 先于 stdout 就绪），
        // token 到达即主动 RequestReconnect，立即打断连接退避，而不是睡满 BackoffBase。
        BindTokenSource(tokenSource);
        _connection.PrepareForHostStart(trace);
        setPhase(DshHostPhase.ProtocolConnecting);

        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshakeCts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await _connection.ConnectAsync(handshakeCts.Token).ConfigureAwait(false);
            setPhase(DshHostPhase.Initializing);
            Plugins.NotifyHostStarted();
            return new DshHostResult(true, RuntimeErrorCodes.Ok, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"agent: startup protocol handshake failed ({ex.GetType().Name})");
            return new DshHostResult(false, "PROTOCOL_HANDSHAKE_FAILED", $"route b handshake failed ({ex.GetType().Name})");
        }
        finally
        {
            _connection.SetTrace(null);
        }
    }

    /// <summary>绑定当前启动令牌源；切换宿主时先解绑旧源，避免同一作用域重复订阅或泄漏事件处理器。</summary>
    private void BindTokenSource(IDshTokenSource tokenSource)
    {
        lock (_tokenGate)
        {
            if (ReferenceEquals(_tokenSubscription, tokenSource))
            {
                return;
            }
            if (_tokenSubscription is not null)
            {
                _tokenSubscription.TokenChanged -= OnTokenSourceChanged;
            }
            _tokenSubscription = tokenSource;
            tokenSource.TokenChanged += OnTokenSourceChanged;
        }
    }

    /// <summary>解绑启动令牌源（停机 / 释放时调用）。可重复调用。</summary>
    private void UnbindTokenSource()
    {
        lock (_tokenGate)
        {
            if (_tokenSubscription is not null)
            {
                _tokenSubscription.TokenChanged -= OnTokenSourceChanged;
                _tokenSubscription = null;
            }
        }
    }

    private void OnTokenSourceChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        if (string.IsNullOrEmpty((sender as IDshTokenSource)?.Token))
        {
            // 令牌被清空属于停机 / 排空路径，由 NotifyHostStoppingAsync 处理，不触发重连。
            return;
        }
        if (_connection.State is RemoteConnectionState.Connected or RemoteConnectionState.Draining or RemoteConnectionState.Disposed)
        {
            return;
        }
        _connection.RequestReconnect();
    }

    private string? ResolveSessionTitle(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }
        var chatState = Chat.State;
        if (string.Equals(chatState.ActiveSession.SessionId, sessionId, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(chatState.SessionTitle))
        {
            return chatState.SessionTitle;
        }
        foreach (var item in Sessions.State.Sessions)
        {
            if (string.Equals(item.SessionId, sessionId, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(item.DisplayTitle))
            {
                return item.DisplayTitle;
            }
        }
        return null;
    }

    private void OnChatStateChanged(ChatUiState state)
    {
        if (state.ActiveSession.SessionId is { } sid and not "pending")
        {
            Sessions.SetActiveSessionId(sid);
        }
        Inbox.RefreshTitles();
        SyncLeases(state, _requests.Snapshot().Count);
    }

    private void OnRequestsChanged(IReadOnlyList<PendingInteractionCard> cards) =>
        SyncLeases(Chat.State, cards.Count);

    private void OnSessionsStateChanged(SessionsUiState _) => Inbox.RefreshTitles();

    private void OnGenerationChanged(ConnectionGeneration? generation)
    {
        if (generation is not null)
        {
            Chat.UpdateConnectionIdentity(generation.ClientId);
        }
    }

    private void SyncLeases(ChatUiState chatState, int pendingRequestCount)
    {
        if (_leases is null)
        {
            return;
        }
        var chatActive = chatState.IsSending || chatState.Transcript.Phase != RunPhase.Idle;
        var reconcileActive = chatState.IsReconciling;
        var approvalActive = pendingRequestCount > 0;
        lock (_leaseSyncGate)
        {
            UpdateLeaseSlot(ref _chatLease, chatActive, WorkLeaseKind.Chat, "chat.turn");
            UpdateLeaseSlot(ref _reconcileLease, reconcileActive, WorkLeaseKind.Reconcile, "chat.reconcile");
            UpdateLeaseSlot(ref _approvalLease, approvalActive, WorkLeaseKind.Approval, "agent.approval");
        }
    }

    private void UpdateLeaseSlot(ref IDisposable? slot, bool shouldHold, WorkLeaseKind kind, string label)
    {
        if (shouldHold)
        {
            slot ??= _leases!.Acquire(kind, label);
        }
        else if (slot is not null)
        {
            slot.Dispose();
            slot = null;
        }
    }

    private async Task NotifyHostStoppingAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        UnbindTokenSource();
        Sessions.NotifyHostStopping();
        Plugins.NotifyHostStopping();
        await Chat.NotifyHostStoppingAsync(ct).ConfigureAwait(false);
        await _connection.BeginDrainAsync(ct).ConfigureAwait(false);
        Volatile.Write(ref _activeTokenSource, null);
        lock (_leaseSyncGate)
        {
            UpdateLeaseSlot(ref _chatLease, false, WorkLeaseKind.Chat, "chat.turn");
            UpdateLeaseSlot(ref _reconcileLease, false, WorkLeaseKind.Reconcile, "chat.reconcile");
            UpdateLeaseSlot(ref _approvalLease, false, WorkLeaseKind.Approval, "agent.approval");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        UnbindTokenSource();
        Chat.StateChanged -= OnChatStateChanged;
        Sessions.StateChanged -= OnSessionsStateChanged;
        _requests.RequestsChanged -= OnRequestsChanged;
        _connection.GenerationChanged -= OnGenerationChanged;
        _stoppingHook.Dispose();
        lock (_leaseSyncGate)
        {
            UpdateLeaseSlot(ref _chatLease, false, WorkLeaseKind.Chat, "chat.turn");
            UpdateLeaseSlot(ref _reconcileLease, false, WorkLeaseKind.Reconcile, "chat.reconcile");
            UpdateLeaseSlot(ref _approvalLease, false, WorkLeaseKind.Approval, "agent.approval");
        }
        _ = DisposeResourcesAsync();
    }

    private async Task DisposeResourcesAsync()
    {
        RemoteTargetBundle? remote;
        lock (_remoteGate)
        {
            remote = _remoteBundle;
            _remoteBundle = null;
        }
        if (remote is not null)
        {
            await remote.DisposeAsync().ConfigureAwait(false);
        }
        await Plugins.DisposeAsync().ConfigureAwait(false);
        _pluginCatalogSearch.Dispose();
        await RequestNotifications.DisposeAsync().ConfigureAwait(false);
        await Inbox.DisposeAsync().ConfigureAwait(false);
        await Sessions.DisposeAsync().ConfigureAwait(false);
        await Credentials.DisposeAsync().ConfigureAwait(false);
        await Chat.DisposeAsync().ConfigureAwait(false);
        await _requests.DisposeAsync().ConfigureAwait(false);
        await _transcripts.DisposeAsync().ConfigureAwait(false);
        _gateway.Dispose();
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class RemoteTargetBundle : IAsyncDisposable
    {
        private readonly Lock _sessionGate = new();
        private readonly Action<string> _log;
        private readonly DshRemoteConnection _connection;
        private readonly RemoteDshGatewayClient _gateway;
        private readonly TranscriptEngine _transcripts;
        private readonly SendStateMachine _sends;
        private readonly HistoryReconciler _reconciler;
        private RemoteSshSession _session;
        private string _label;

        public RemoteTargetBundle(string label, RemoteSshSession session, Action<string> log)
        {
            _label = label;
            _session = session;
            _log = log;
            ProfileId = session.Profile.Id;

            var options = new RemoteConnectionOptions(new Uri(RuntimeConstants.DshBaseUrl));
            _connection = new DshRemoteConnection(
                options,
                ReadToken,
                log: msg => _log($"remote-agent: {msg}"),
                baseUriProvider: ReadLoopbackUri);
            _gateway = new RemoteDshGatewayClient(_connection);
            _transcripts = new TranscriptEngine();
            Requests = new RequestsEngine(_gateway, _gateway, _transcripts);
            _sends = new SendStateMachine(_gateway, _gateway);
            _reconciler = new HistoryReconciler(_gateway, _gateway);
            Chat = new ChatViewModel(_gateway, _transcripts, Requests, _sends, _reconciler, keptStore: null);

            _connection.GenerationChanged += OnGenerationChanged;
            _session.TokenChanged += OnSessionTokenChanged;
        }

        public string ProfileId { get; }
        public RequestsEngine Requests { get; }
        public ChatViewModel Chat { get; }

        public string Label
        {
            get
            {
                lock (_sessionGate)
                {
                    return _label;
                }
            }
        }

        public RemoteSshSession Session
        {
            get
            {
                lock (_sessionGate)
                {
                    return _session;
                }
            }
        }

        public void RebindSession(string label, RemoteSshSession nextSession)
        {
            lock (_sessionGate)
            {
                if (!ReferenceEquals(_session, nextSession))
                {
                    _session.TokenChanged -= OnSessionTokenChanged;
                    _session = nextSession;
                    _session.TokenChanged += OnSessionTokenChanged;
                }
                _label = label;
            }
        }

        public void PrepareForConnect()
        {
            _connection.PrepareForHostStart();
            _connection.RequestReconnect();
        }

        private string? ReadToken()
        {
            lock (_sessionGate)
            {
                return ((IDshTokenSource)_session).Token;
            }
        }

        private Uri? ReadLoopbackUri()
        {
            lock (_sessionGate)
            {
                return _session.LoopbackBaseUri;
            }
        }

        private void OnSessionTokenChanged(object? sender, EventArgs e)
        {
            if (ReadToken() is null)
            {
                _ = _connection.BeginDrainAsync(CancellationToken.None);
            }
            else
            {
                _connection.PrepareForHostStart();
                _connection.RequestReconnect();
            }
        }

        private void OnGenerationChanged(ConnectionGeneration? generation)
        {
            if (generation is not null)
            {
                Chat.UpdateConnectionIdentity(generation.ClientId);
            }
        }

        public async ValueTask DisposeAsync()
        {
            lock (_sessionGate)
            {
                _session.TokenChanged -= OnSessionTokenChanged;
            }
            _connection.GenerationChanged -= OnGenerationChanged;
            await Chat.DisposeAsync().ConfigureAwait(false);
            await Requests.DisposeAsync().ConfigureAwait(false);
            await _transcripts.DisposeAsync().ConfigureAwait(false);
            _gateway.Dispose();
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
