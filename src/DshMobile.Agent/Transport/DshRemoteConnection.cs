using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DshMobile.Agent.Protocol;
using DshMobile.Core.Diagnostics;

namespace DshMobile.Agent.Transport;

/// <summary>
/// Native C# Route B transport over loopback `/connect`, `/api/*`, and `/api/remote.mux` (ADR-0033).
/// Holds no RuntimeCoordinator lock; keeps `/api/remote.mux` WebSocket alive for the lifetime of the `v2` Cookie
/// Peer, enforces the `$events` `{ type: "ready" }` barrier before publishing a generation, and supports
/// 401 refresh, exponential backoff reconnect, and orderly draining.
/// </summary>
public sealed class DshRemoteConnection : IAsyncDisposable
{
    private readonly RemoteConnectionOptions _options;
    private readonly Func<string?> _tokenProvider;
    private readonly TimeProvider _time;
    private StartupTrace? _trace;
    private readonly Action<string> _log;
    private readonly SocketsHttpHandler _httpHandler;
    private readonly HttpClient _http;
    private readonly Lock _stateLock = new();

    private CancellationTokenSource _lifetimeCts = new();
    private CancellationTokenSource? _activeGenCts;
    private ActiveGeneration? _activeGen;
    private TaskCompletionSource<ActiveGeneration> _readyTcs = NewPendingReadyTcs();
    private Task? _loopTask;
    private RemoteConnectionState _state = RemoteConnectionState.Disconnected;
    private long _nextGenerationId;
    private long _nextStreamSeq;
    private long _nextRpcSeq;
    private int _drainRequested;
    private int _disposed;
    private int _reconnectRequested;

    public DshRemoteConnection(
        RemoteConnectionOptions options,
        Func<string?> tokenProvider,
        TimeProvider? time = null,
        StartupTrace? trace = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        if (options.MaxInboundMessageBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxInboundMessageBytes must be positive.");
        }
        if (options.InteractionsMode is not ("answer" or "none"))
        {
            throw new ArgumentException("InteractionsMode must be 'answer' or 'none'.", nameof(options));
        }

        _options = options;
        _tokenProvider = tokenProvider;
        _time = time ?? TimeProvider.System;
        _trace = trace;
        _log = log ?? (_ => { });

        _httpHandler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectCallback = LoopbackConnectAsync,
        };
        _http = new HttpClient(_httpHandler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public RemoteConnectionState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public ConnectionGeneration? CurrentGeneration
    {
        get
        {
            lock (_stateLock)
            {
                return _activeGen?.Public;
            }
        }
    }

    /// <summary>Raised when a new `$events` `ready` generation is published or cleared.</summary>
    public event Action<ConnectionGeneration?>? GenerationChanged;

    /// <summary>Raised when <see cref="State"/> transitions.</summary>
    public event Action<RemoteConnectionState>? StateChanged;

    /// <summary>Raised for forwarded `$events` (`emit` or `waterfall`) received after `ready`.</summary>
    public event Func<RemoteForwardedEventPayload, ValueTask>? ForwardedEventReceived;

    /// <summary>Starts the connection/reconnect loop idempotently and waits for the first ready generation.</summary>
    public async Task<ConnectionGeneration> ConnectAsync(CancellationToken ct = default)
    {
        StartLoopIfNeeded();
        var gen = await WaitForActiveGenerationAsync(ct).ConfigureAwait(false);
        return gen.Public;
    }

    /// <summary>Starts the background connection/reconnect loop without blocking on readiness.</summary>
    public void Start() => StartLoopIfNeeded();

    /// <summary>更新本次启动周期的性能跟踪器；传 null 可在启动握手完成后解绑。</summary>
    public void SetTrace(StartupTrace? trace) => Volatile.Write(ref _trace, trace);

    /// <summary>在宿主新周期启动前清除已排空（Draining）状态并挂载跟踪器，支持停机后无损重启重连（F4 / F14）。</summary>
    public void PrepareForHostStart(StartupTrace? trace = null)
    {
        CancellationTokenSource? oldLifetime = null;
        var canDisposeOld = false;
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_state == RemoteConnectionState.Disposed, this);
            Volatile.Write(ref _trace, trace);
            if (_state == RemoteConnectionState.Draining || Volatile.Read(ref _drainRequested) != 0)
            {
                Volatile.Write(ref _drainRequested, 0);
                Interlocked.Exchange(ref _reconnectRequested, 0);
                canDisposeOld = _loopTask is null || _loopTask.IsCompleted;
                oldLifetime = _lifetimeCts;
                _lifetimeCts = new CancellationTokenSource();
                _loopTask = null;
                _readyTcs = NewPendingReadyTcs();
                SetStateLocked(RemoteConnectionState.Disconnected);
            }
        }
        if (canDisposeOld)
        {
            oldLifetime?.Dispose();
        }
    }

    /// <summary>
    /// Cancels the current generation immediately and starts a fresh connection attempt without waiting for backoff.
    /// </summary>
    public void RequestReconnect()
    {
        ActiveGeneration? prevGen = null;
        CancellationTokenSource? prevCts = null;
        lock (_stateLock)
        {
            if (_state is RemoteConnectionState.Draining or RemoteConnectionState.Disposed)
            {
                return;
            }
            Interlocked.Exchange(ref _reconnectRequested, 1);
            prevGen = _activeGen;
            prevCts = _activeGenCts;
            _activeGen = null;
            _activeGenCts = null;
            ResetReadyTcsLocked();
            SetStateLocked(RemoteConnectionState.Connecting);
        }
        prevCts?.Cancel();
        if (prevGen is not null)
        {
            GenerationChanged?.Invoke(null);
        }
    }

    /// <summary>
    /// Executes a unary HTTP RPC (`POST /api/&lt;endpoint&gt;`) with automatic single-retry 401 generation refresh.
    /// </summary>
    public async Task<RemoteUnaryOutcome> CallAsync(
        string endpoint,
        JsonElement? args = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        ThrowIfDrainingOrDisposed();
        StartLoopIfNeeded();

        var gen = await WaitForActiveGenerationAsync(ct, _options.UnaryTimeout).ConfigureAwait(false);
        try
        {
            return await SendUnaryOnceAsync(gen, endpoint, args, ct).ConfigureAwait(false);
        }
        catch (RemoteAuthenticationException) when (!ct.IsCancellationRequested && Volatile.Read(ref _drainRequested) == 0)
        {
            // 401 refresh: invalidate stale generation if it is still current, wait for the refreshed generation, retry once.
            InvalidateIfCurrent(gen);
            var refreshed = await WaitForActiveGenerationAsync(ct, _options.UnaryTimeout).ConfigureAwait(false);
            return await SendUnaryOnceAsync(refreshed, endpoint, args, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Answers a forwarded `$events` waterfall item via `POST /api/$events/result`.</summary>
    public Task<RemoteUnaryOutcome> SendEventResultAsync(
        string clientId,
        string eventId,
        RemoteEventOutcome outcome,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientId);
        ArgumentException.ThrowIfNullOrEmpty(eventId);
        ArgumentNullException.ThrowIfNull(outcome);
        var args = RemoteWireCodec.EncodeEventResultArgs(new RemoteEventResultArgs(clientId, eventId, outcome));
        return CallAsync("$events/result", args, ct);
    }

    /// <summary>Opens a multiplexed logical stream on `/api/remote.mux`.</summary>
    public async Task<IRemoteStreamHandle> OpenStreamAsync(
        string endpoint,
        JsonElement? args,
        Func<MuxInboundFrame, ValueTask> onFrame,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        ArgumentNullException.ThrowIfNull(onFrame);
        ThrowIfDrainingOrDisposed();
        StartLoopIfNeeded();

        var gen = await WaitForActiveGenerationAsync(ct, _options.UnaryTimeout).ConfigureAwait(false);
        var streamId = $"s-{Interlocked.Increment(ref _nextStreamSeq)}";
        var handle = new MuxStreamHandle(this, gen, streamId, onFrame);
        if (!gen.TryAddStream(streamId, handle))
        {
            throw new RemoteConnectionException("Active connection generation closed before stream opened.");
        }

        try
        {
            var openJson = RemoteWireCodec.EncodeMuxOpen(streamId, endpoint, args);
            await gen.EnqueueOutboundAsync(openJson, ct).ConfigureAwait(false);
            return handle;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            gen.RemoveStream(streamId);
            handle.TrySetFailed(ex);
            throw;
        }
    }

    /// <summary>
    /// Orderly drain hook (N-01-03): rejects new calls/streams, cancels active waits, and closes the WebSocket
    /// cleanly before <see cref="DshMobile.Runtime.Hosting.DshHost"/> terminates the DSH process.
    /// </summary>
    public async Task BeginDrainAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _drainRequested, 1) != 0)
        {
            return;
        }

        Task? loopTask;
        ActiveGeneration? active;
        CancellationTokenSource? genCts;
        lock (_stateLock)
        {
            if (_state == RemoteConnectionState.Disposed)
            {
                return;
            }
            SetStateLocked(RemoteConnectionState.Draining);
            active = _activeGen;
            genCts = _activeGenCts;
            _activeGen = null;
            _activeGenCts = null;
            _readyTcs.TrySetException(new RemoteDrainingException());
            loopTask = _loopTask;
        }

        GenerationChanged?.Invoke(null);
        try
        {
            await _lifetimeCts.CancelAsync().ConfigureAwait(false);
            if (genCts is not null)
            {
                await genCts.CancelAsync().ConfigureAwait(false);
            }
            if (active is not null)
            {
                await active.CloseAsync(ct).ConfigureAwait(false);
            }
            if (loopTask is not null)
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waitCts.CancelAfter(TimeSpan.FromSeconds(2));
                await loopTask.WaitAsync(waitCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"remote-conn: drain completed with {ex.GetType().Name}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await BeginDrainAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Dispose must finish tearing down local resources.
        }

        lock (_stateLock)
        {
            SetStateLocked(RemoteConnectionState.Disposed);
        }
        _lifetimeCts.Dispose();
        _http.Dispose();
    }

    // ------------------------------------------------------------------------------------------------

    private void StartLoopIfNeeded()
    {
        lock (_stateLock)
        {
            if (_state is RemoteConnectionState.Draining or RemoteConnectionState.Disposed)
            {
                throw new RemoteDrainingException();
            }
            if (_loopTask is { IsCompleted: false })
            {
                return;
            }
            SetStateLocked(RemoteConnectionState.Connecting);
            var lifetimeCt = _lifetimeCts.Token;
            _loopTask = Task.Run(() => ConnectionLoopAsync(lifetimeCt));
        }
    }

    private async Task ConnectionLoopAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested && Volatile.Read(ref _drainRequested) == 0)
        {
            if (Interlocked.Exchange(ref _reconnectRequested, 0) != 0)
            {
                attempt = 0;
            }
            else if (attempt > 0)
            {
                var delay = ComputeBackoff(attempt);
                try
                {
                    await Task.Delay(delay, _time, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            attempt++;
            using var genCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_stateLock)
            {
                if (_state is RemoteConnectionState.Draining or RemoteConnectionState.Disposed)
                {
                    break;
                }
                _activeGenCts = genCts;
                SetStateLocked(RemoteConnectionState.Connecting);
            }

            ActiveGeneration? established = null;
            try
            {
                established = await EstablishGenerationAsync(genCts, ct).ConfigureAwait(false);
                attempt = 0;
                await established.RunReceiveUntilClosedAsync(genCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || Volatile.Read(ref _drainRequested) != 0)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // Generation was cancelled by RequestReconnect / InvalidateIfCurrent; loop immediately if reconnect requested.
                _log("remote-conn: generation cancelled");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log($"remote-conn: generation ended ({ex.GetType().Name})");
            }
            finally
            {
                var cleared = false;
                lock (_stateLock)
                {
                    if (ReferenceEquals(_activeGenCts, genCts))
                    {
                        _activeGenCts = null;
                    }
                    if (established is not null && ReferenceEquals(_activeGen, established))
                    {
                        _activeGen = null;
                        ResetReadyTcsLocked();
                        cleared = true;
                        if (_state is not (RemoteConnectionState.Draining or RemoteConnectionState.Disposed))
                        {
                            SetStateLocked(RemoteConnectionState.Connecting);
                        }
                    }
                }
                if (cleared)
                {
                    GenerationChanged?.Invoke(null);
                }
                if (established is not null)
                {
                    await established.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        lock (_stateLock)
        {
            if (_state is not (RemoteConnectionState.Draining or RemoteConnectionState.Disposed))
            {
                SetStateLocked(RemoteConnectionState.Disconnected);
            }
        }
    }

    private async Task<ActiveGeneration> EstablishGenerationAsync(CancellationTokenSource genCts, CancellationToken lifetimeCt)
    {
        var token = _tokenProvider();
        if (string.IsNullOrEmpty(token))
        {
            throw new RemoteAuthenticationException("DSH launch token is not available.");
        }

        var connectUri = new Uri(_options.BaseUri, $"connect?token={Uri.EscapeDataString(token)}&interactions={_options.InteractionsMode}");
        using var connectReq = new HttpRequestMessage(HttpMethod.Get, connectUri);
        using var connectRes = await _http.SendAsync(connectReq, HttpCompletionOption.ResponseHeadersRead, genCts.Token).ConfigureAwait(false);
        if (connectRes.StatusCode != HttpStatusCode.Found && !connectRes.IsSuccessStatusCode)
        {
            throw new RemoteAuthenticationException($"Token exchange rejected (HTTP {(int)connectRes.StatusCode}).", (int)connectRes.StatusCode);
        }

        var cookie = ExtractCookiePair(connectRes.Headers);
        if (string.IsNullOrEmpty(cookie))
        {
            throw new RemoteAuthenticationException("Token exchange succeeded without a session cookie.");
        }

        var ws = new ClientWebSocket();
        ws.Options.Proxy = null;
        ws.Options.SetRequestHeader("Cookie", cookie);
        var wsUri = BuildMuxWebSocketUri(_options.BaseUri);

        var trace = Volatile.Read(ref _trace);
        var connectSpan = trace?.Measure("protocol.connected");
        try
        {
            await ws.ConnectAsync(wsUri, genCts.Token).ConfigureAwait(false);
            connectSpan?.End();
        }
        catch (OperationCanceledException)
        {
            connectSpan?.End(StartupTrace.Canceled);
            ws.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            connectSpan?.End(StartupTrace.Failed);
            ws.Dispose();
            throw new RemoteConnectionException($"WebSocket connect failed ({ex.GetType().Name}).", ex);
        }

        var genId = Interlocked.Increment(ref _nextGenerationId);
        var eventsStreamId = $"ev-{genId}";
        var gen = new ActiveGeneration(
            genId,
            cookie,
            ws,
            _options.MaxInboundMessageBytes,
            OnForwardedEventAsync,
            _log);

        var initSpan = trace?.Measure("protocol.initialized");
        try
        {
            gen.StartSendPump(genCts.Token);
            var openEvents = RemoteWireCodec.EncodeMuxOpen(eventsStreamId, "$events", RemoteWireCodec.EmptyArgs);
            await gen.EnqueueOutboundAsync(openEvents, genCts.Token).ConfigureAwait(false);

            using var readyTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(genCts.Token);
            readyTimeoutCts.CancelAfter(_options.ReadyTimeout);
            var readyPayload = await gen.ReadEventsReadyAsync(eventsStreamId, readyTimeoutCts.Token).ConfigureAwait(false);
            gen.SetReady(readyPayload);

            bool published;
            lock (_stateLock)
            {
                if (genCts.IsCancellationRequested ||
                    lifetimeCt.IsCancellationRequested ||
                    !ReferenceEquals(_activeGenCts, genCts) ||
                    _state is RemoteConnectionState.Draining or RemoteConnectionState.Disposed)
                {
                    published = false;
                }
                else
                {
                    _activeGen = gen;
                    SetStateLocked(RemoteConnectionState.Connected);
                    _readyTcs.TrySetResult(gen);
                    published = true;
                }
            }

            if (!published)
            {
                initSpan?.End(StartupTrace.Canceled);
                await gen.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                throw new OperationCanceledException("Generation was superseded or cancelled before ready.");
            }

            initSpan?.End();
            GenerationChanged?.Invoke(gen.Public);
            return gen;
        }
        catch (OperationCanceledException)
        {
            initSpan?.End(StartupTrace.Canceled);
            await gen.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            initSpan?.End(StartupTrace.Failed);
            await gen.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<RemoteUnaryOutcome> SendUnaryOnceAsync(
        ActiveGeneration gen,
        string endpoint,
        JsonElement? args,
        CancellationToken ct)
    {
        var rpcId = $"rpc-{Interlocked.Increment(ref _nextRpcSeq)}";
        var jsonBody = RemoteWireCodec.EncodeClientRequest(rpcId, endpoint, args);

        var targetUri = new Uri(_options.BaseUri, $"api/{endpoint.TrimStart('/')}");
        using var req = new HttpRequestMessage(HttpMethod.Post, targetUri)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Cookie", gen.Cookie);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
        timeoutCts.CancelAfter(_options.UnaryTimeout);

        HttpResponseMessage res;
        try
        {
            res = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && _lifetimeCts.IsCancellationRequested)
        {
            throw new RemoteDrainingException();
        }

        using (res)
        {
            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new RemoteAuthenticationException("Remote API returned 401 unauthorized.", 401);
            }
            if (!res.IsSuccessStatusCode)
            {
                throw new RemoteConnectionException($"Remote API '{endpoint}' failed with HTTP {(int)res.StatusCode}.");
            }

            var bytes = await res.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);
            var contentType = res.Content.Headers.ContentType;
            if (contentType is not null && string.Equals(contentType.MediaType, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
            {
                var (envelope, parts) = RemoteWireCodec.DecodeMultipartResponse(contentType, bytes);
                if (envelope.RpcId != rpcId)
                {
                    throw new RemoteProtocolException($"rpcId mismatch for '{endpoint}'.");
                }
                return new RemoteUnaryOutcome(envelope.Result.Ok, envelope.Result.Value, envelope.Result.Error, parts);
            }

            var parsed = RemoteWireCodec.DecodeServerResponse(bytes);
            if (parsed.RpcId != rpcId)
            {
                throw new RemoteProtocolException($"rpcId mismatch for '{endpoint}'.");
            }
            return new RemoteUnaryOutcome(parsed.Result.Ok, parsed.Result.Value, parsed.Result.Error, EmptyAttachments);
        }
    }

    private async Task<ActiveGeneration> WaitForActiveGenerationAsync(CancellationToken ct, TimeSpan? timeout = null)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task<ActiveGeneration> readyTask;
            lock (_stateLock)
            {
                if (_state is RemoteConnectionState.Draining or RemoteConnectionState.Disposed)
                {
                    throw new RemoteDrainingException();
                }
                if (_activeGen is { } live)
                {
                    return live;
                }
                readyTask = _readyTcs.Task;
            }

            if (timeout is not { } limit || limit <= TimeSpan.Zero || limit == Timeout.InfiniteTimeSpan)
            {
                return await readyTask.WaitAsync(ct).ConfigureAwait(false);
            }

            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(limit);
            try
            {
                return await readyTask.WaitAsync(waitCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && waitCts.IsCancellationRequested)
            {
                throw new TimeoutException("Timed out waiting for an active remote connection generation.");
            }
        }
    }

    private void InvalidateIfCurrent(ActiveGeneration stale)
    {
        CancellationTokenSource? toCancel = null;
        var cleared = false;
        lock (_stateLock)
        {
            if (!ReferenceEquals(_activeGen, stale))
            {
                return;
            }
            Interlocked.Exchange(ref _reconnectRequested, 1);
            toCancel = _activeGenCts;
            _activeGen = null;
            _activeGenCts = null;
            ResetReadyTcsLocked();
            cleared = true;
            if (_state is not (RemoteConnectionState.Draining or RemoteConnectionState.Disposed))
            {
                SetStateLocked(RemoteConnectionState.Connecting);
            }
        }
        toCancel?.Cancel();
        if (cleared)
        {
            GenerationChanged?.Invoke(null);
        }
    }

    private void ThrowIfDrainingOrDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _drainRequested) != 0)
        {
            throw new RemoteDrainingException();
        }
    }

    private TimeSpan ComputeBackoff(int attempt)
    {
        var factor = Math.Pow(_options.BackoffFactor, Math.Max(0, attempt - 1));
        var ms = Math.Min(_options.BackoffMax.TotalMilliseconds, _options.BackoffBase.TotalMilliseconds * factor);
        if (!double.IsFinite(ms) || ms < 0)
        {
            return _options.BackoffMax;
        }
        return TimeSpan.FromMilliseconds(ms);
    }

    private void SetStateLocked(RemoteConnectionState next)
    {
        if (_state == next)
        {
            return;
        }
        _state = next;
        StateChanged?.Invoke(next);
    }

    private void ResetReadyTcsLocked()
    {
        if (!_readyTcs.Task.IsCompleted)
        {
            return;
        }
        _readyTcs = NewPendingReadyTcs();
    }

    private static TaskCompletionSource<ActiveGeneration> NewPendingReadyTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task OnForwardedEventAsync(RemoteForwardedEventPayload evt)
    {
        return ForwardedEventReceived is { } handler ? handler(evt).AsTask() : Task.CompletedTask;
    }

    private static string? ExtractCookiePair(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Set-Cookie", out var values))
        {
            return null;
        }
        foreach (var raw in values)
        {
            var semi = raw.IndexOf(';', StringComparison.Ordinal);
            var pair = (semi >= 0 ? raw[..semi] : raw).Trim();
            if (pair.StartsWith("dsh-auth-", StringComparison.Ordinal))
            {
                return pair;
            }
        }
        return null;
    }

    private static Uri BuildMuxWebSocketUri(Uri baseUri)
    {
        var scheme = baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        var builder = new UriBuilder(scheme, baseUri.Host, baseUri.Port, "/api/remote.mux");
        return builder.Uri;
    }

    private static async ValueTask<Stream> LoopbackConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;
        if (!RemoteConnectionOptions.IsLoopbackHost(host))
        {
            throw new RemoteConnectionException($"Refused non-loopback connection to '{host}'.");
        }
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        var loopback = addresses.FirstOrDefault(IPAddress.IsLoopback)
            ?? throw new RemoteConnectionException($"Host '{host}' resolved to no loopback address.");

        var socket = new Socket(loopback.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(loopback, port), ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            socket.Dispose();
            throw;
        }
    }

    private static readonly IReadOnlyDictionary<string, ReadOnlyMemory<byte>> EmptyAttachments =
        new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);

    // ------------------------------------------------------------------------------------------------

    private sealed class ActiveGeneration
    {
        private readonly long _generationId;
        private readonly ClientWebSocket _ws;
        private readonly int _maxInboundMessageBytes;
        private readonly Func<RemoteForwardedEventPayload, Task> _onEvent;
        private readonly Action<string> _log;
        private readonly Channel<string> _outbound = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        private readonly ConcurrentDictionary<string, MuxStreamHandle> _streams = new(StringComparer.Ordinal);
        private Task? _sendLoop;
        private string? _eventsStreamId;
        private int _closed;

        public ActiveGeneration(
            long generationId,
            string cookie,
            ClientWebSocket ws,
            int maxInboundMessageBytes,
            Func<RemoteForwardedEventPayload, Task> onEvent,
            Action<string> log)
        {
            _generationId = generationId;
            Cookie = cookie;
            _ws = ws;
            _maxInboundMessageBytes = maxInboundMessageBytes;
            _onEvent = onEvent;
            _log = log;
        }

        public string Cookie { get; }
        public ConnectionGeneration Public { get; private set; } = new(0, "", null);

        public void StartSendPump(CancellationToken ct)
        {
            _sendLoop = Task.Run(() => SendLoopAsync(ct), CancellationToken.None);
        }

        public ValueTask EnqueueOutboundAsync(string text, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _closed) != 0 || !_outbound.Writer.TryWrite(text))
            {
                throw new RemoteConnectionException("WebSocket send queue is closed.");
            }
            return ValueTask.CompletedTask;
        }

        public async Task<RemoteEventsReadyPayload> ReadEventsReadyAsync(string eventsStreamId, CancellationToken ct)
        {
            _eventsStreamId = eventsStreamId;
            while (true)
            {
                var raw = await ReceiveTextMessageAsync(ct).ConfigureAwait(false);
                if (raw is null)
                {
                    throw new RemoteProtocolException("WebSocket closed before $events ready frame.");
                }
                var frame = RemoteWireCodec.DecodeMuxInbound(raw.Value.Span);
                if (frame.StreamId != eventsStreamId)
                {
                    throw new RemoteProtocolException($"Unexpected stream '{frame.StreamId}' before $events ready.");
                }
                if (frame.Type != "item" || frame.Value is not { } val)
                {
                    throw new RemoteProtocolException($"First $events frame must be ready item, got '{frame.Type}'.");
                }
                var ready = RemoteWireCodec.TryDecodeEventsReady(val);
                if (ready is null || ready.Type != "ready" || string.IsNullOrEmpty(ready.ClientId))
                {
                    throw new RemoteProtocolException($"First $events item was not ready (type='{ready?.Type ?? "null"}').");
                }
                return ready;
            }
        }

        public void SetReady(RemoteEventsReadyPayload ready)
        {
            Public = new ConnectionGeneration(_generationId, ready.ClientId!, ready.Host?.Home);
        }

        public bool TryAddStream(string streamId, MuxStreamHandle handle)
        {
            if (Volatile.Read(ref _closed) != 0)
            {
                return false;
            }
            return _streams.TryAdd(streamId, handle);
        }

        public void RemoveStream(string streamId) => _streams.TryRemove(streamId, out _);

        public async Task RunReceiveUntilClosedAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                var raw = await ReceiveTextMessageAsync(ct).ConfigureAwait(false);
                if (raw is null)
                {
                    break;
                }
                var frame = RemoteWireCodec.DecodeMuxInbound(raw.Value.Span);
                if (frame.StreamId == _eventsStreamId)
                {
                    if (frame.Type == "item" && frame.Value is { } v)
                    {
                        var evt = RemoteWireCodec.TryDecodeForwardedEvent(v);
                        if (evt is not null)
                        {
                            await _onEvent(evt).ConfigureAwait(false);
                        }
                    }
                    else if (frame.Type is "end" or "error")
                    {
                        throw new RemoteProtocolException($"$events stream terminated ({frame.Type}).");
                    }
                    continue;
                }

                if (_streams.TryGetValue(frame.StreamId, out var handle))
                {
                    await handle.DeliverAsync(frame).ConfigureAwait(false);
                    if (frame.Type is "end" or "error")
                    {
                        _streams.TryRemove(frame.StreamId, out _);
                    }
                }
            }
        }

        public async Task CloseAsync(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }

            _outbound.Writer.TryComplete();
            foreach (var kv in _streams)
            {
                if (_streams.TryRemove(kv.Key, out var h))
                {
                    h.TrySetFailed(new RemoteConnectionException("Connection generation closed."));
                }
            }

            try
            {
                if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    closeCts.CancelAfter(TimeSpan.FromMilliseconds(500));
                    await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closing", closeCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log($"remote-conn: ws close {ex.GetType().Name}");
            }
            finally
            {
                _ws.Dispose();
            }

            if (_sendLoop is not null)
            {
                await _sendLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        private async Task SendLoopAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var text in _outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    if (_ws.State != WebSocketState.Open)
                    {
                        break;
                    }
                    var bytes = Encoding.UTF8.GetBytes(text);
                    await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on generation teardown.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log($"remote-conn: send loop stopped ({ex.GetType().Name})");
            }
        }

        private async Task<ReadOnlyMemory<byte>?> ReceiveTextMessageAsync(CancellationToken ct)
        {
            var chunk = new byte[Math.Min(16 * 1024, _maxInboundMessageBytes)];
            using var ms = new MemoryStream();
            while (true)
            {
                var result = await _ws.ReceiveAsync(chunk, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new RemoteProtocolException("Unexpected binary WebSocket frame on /api/remote.mux.");
                }
                if (ms.Length + result.Count > _maxInboundMessageBytes)
                {
                    throw new RemoteProtocolException($"Inbound WebSocket message exceeded {_maxInboundMessageBytes} bytes.");
                }
                if (result.Count > 0)
                {
                    ms.Write(chunk, 0, result.Count);
                }
                if (result.EndOfMessage)
                {
                    return ms.GetBuffer().AsMemory(0, (int)ms.Length).ToArray();
                }
            }
        }
    }

    private sealed class MuxStreamHandle(
        DshRemoteConnection owner,
        ActiveGeneration gen,
        string streamId,
        Func<MuxInboundFrame, ValueTask> onFrame) : IRemoteStreamHandle
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        public string StreamId => streamId;
        public long GenerationId => gen.Public.GenerationId;
        public Task Completion => _completion.Task;

        public async Task DeliverAsync(MuxInboundFrame frame)
        {
            try
            {
                await onFrame(frame).ConfigureAwait(false);
                if (frame.Type == "end")
                {
                    _completion.TrySetResult();
                }
                else if (frame.Type == "error")
                {
                    _completion.TrySetException(new RemoteConnectionException(frame.Error?.Message ?? "Remote stream error."));
                }
            }
            catch (OperationCanceledException ex)
            {
                _completion.TrySetCanceled(ex.CancellationToken);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _completion.TrySetException(ex);
                throw;
            }
        }

        public void TrySetFailed(Exception ex)
        {
            if (ex is OperationCanceledException oce)
            {
                _completion.TrySetCanceled(oce.CancellationToken);
            }
            else
            {
                _completion.TrySetException(ex);
            }
        }

        public ValueTask SendItemAsync(JsonElement value, CancellationToken ct = default)
        {
            owner.ThrowIfDrainingOrDisposed();
            return gen.EnqueueOutboundAsync(RemoteWireCodec.EncodeMuxItem(streamId, value), ct);
        }

        public ValueTask EndUplinkAsync(CancellationToken ct = default)
        {
            owner.ThrowIfDrainingOrDisposed();
            return gen.EnqueueOutboundAsync(RemoteWireCodec.EncodeMuxEnd(streamId), ct);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            gen.RemoveStream(streamId);
            if (!_completion.Task.IsCompleted)
            {
                try
                {
                    await gen.EnqueueOutboundAsync(RemoteWireCodec.EncodeMuxCancel(streamId), CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Generation already closing; safe to ignore cancel send failure.
                    _ = ex;
                }
                _completion.TrySetCanceled();
            }
        }
    }
}
