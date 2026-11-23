using DshMobile.Agent.Engines;
using DshMobile.Agent.Presentation;
using DshMobile.Agent.Protocol;
using DshMobile.Agent.Transport;
using DshMobile.Application.Runtime;
using DshMobile.Core.Diagnostics;
using DshMobile.Runtime;
using DshMobile.Runtime.Hosting;

namespace DshMobile.App.Bridge;

/// <summary>
/// 应用级原生 Agent 连接与引擎作用域（N-04-01）：
/// 驻留 <see cref="DshMobileApplication"/> 生命周期，免疫 Activity/Fragment 重建；
/// 通过 <see cref="RuntimeCoordinator.AddHostStoppingHook"/> 在宿主停机前排空数据面并落盘脱敏缓存，绝不占用 <c>_op</c> 锁。
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
    private readonly Action<string> _log;
    private readonly IDisposable _stoppingHook;
    private IDshTokenSource? _activeTokenSource;
    private int _disposed;

    public AgentConnectionScope(RuntimeCoordinator runtime, RuntimeLayout layout, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(log);
        _log = log;

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
        _keptStore = new KeptTranscriptStore(Path.Combine(layout.HostStateDir, "agent-cache"));
        Chat = new ChatViewModel(_gateway, _transcripts, _requests, _sends, _reconciler, _keptStore);

        _connection.GenerationChanged += OnGenerationChanged;
        _stoppingHook = runtime.AddHostStoppingHook(NotifyHostStoppingAsync);
    }

    public ChatViewModel Chat { get; }

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
        _connection.PrepareForHostStart(trace);
        setPhase(DshHostPhase.ProtocolConnecting);

        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshakeCts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await _connection.ConnectAsync(handshakeCts.Token).ConfigureAwait(false);
            setPhase(DshHostPhase.Initializing);
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

    private void OnGenerationChanged(ConnectionGeneration? generation)
    {
        if (generation is not null)
        {
            Chat.UpdateConnectionIdentity(generation.ClientId);
        }
    }

    private async Task NotifyHostStoppingAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        await Chat.NotifyHostStoppingAsync(ct).ConfigureAwait(false);
        await _connection.BeginDrainAsync(ct).ConfigureAwait(false);
        Volatile.Write(ref _activeTokenSource, null);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _connection.GenerationChanged -= OnGenerationChanged;
        _stoppingHook.Dispose();
        _ = DisposeResourcesAsync();
    }

    private async Task DisposeResourcesAsync()
    {
        await Chat.DisposeAsync().ConfigureAwait(false);
        await _requests.DisposeAsync().ConfigureAwait(false);
        await _transcripts.DisposeAsync().ConfigureAwait(false);
        _gateway.Dispose();
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
