using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DshMobile.Agent.Protocol;
using DshMobile.Agent.Transport;
using DshMobile.Core.Diagnostics;
using Microsoft.Extensions.Time.Testing;

namespace DshMobile.Agent.Tests.Transport;

public sealed class DshRemoteConnectionTests
{
    private const string SecretToken = "secret-launch-token-999";

    private static RemoteConnectionOptions FastOptions(Uri baseUri, int maxInboundBytes = 1024 * 1024) =>
        new(baseUri)
        {
            BackoffBase = TimeSpan.FromMilliseconds(25),
            BackoffFactor = 2.0,
            BackoffMax = TimeSpan.FromMilliseconds(100),
            ReadyTimeout = TimeSpan.FromSeconds(5),
            UnaryTimeout = TimeSpan.FromSeconds(5),
            MaxInboundMessageBytes = maxInboundBytes,
        };

    private static string ParseStreamId(string openFrameJson)
    {
        using var doc = JsonDocument.Parse(openFrameJson);
        return doc.RootElement.GetProperty("streamId").GetString()!;
    }

    private static string ParseRpcId(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("rpcId").GetString()!;
    }

    private static string ReadyFrameJson(string streamId, string clientId, string? home = null)
    {
        var payload = new RemoteEventsReadyPayload(
            "ready",
            clientId,
            home is null ? null : new RemoteHostInfo(home));
        var valueEl = JsonSerializer.SerializeToElement(payload, AgentProtocolJsonContext.Default.RemoteEventsReadyPayload);
        return RemoteWireCodec.EncodeMuxItem(streamId, valueEl);
    }

    private static string ServerResponseJson(string rpcId, string valueJson) =>
        $$$"""{"type":"server-response","rpcId":"{{{rpcId}}}","result":{"ok":true,"value":{{{valueJson}}}}}""";

    [Fact]
    public void Options_RejectsNonLoopbackAndMalformedBaseUris()
    {
        Assert.Throws<ArgumentException>(() => new RemoteConnectionOptions(new Uri("http://192.168.1.50:3000/")));
        Assert.Throws<ArgumentException>(() => new RemoteConnectionOptions(new Uri("http://example.com:3000/")));
        Assert.Throws<ArgumentException>(() => new RemoteConnectionOptions(new Uri("ws://127.0.0.1:3000/")));
        Assert.Throws<ArgumentException>(() => new RemoteConnectionOptions(new Uri("http://127.0.0.1:3000/?token=abc")));
        Assert.Throws<ArgumentException>(() => new RemoteConnectionOptions(new Uri("http://user:pass@127.0.0.1:3000/")));

        var valid = new RemoteConnectionOptions(new Uri("http://127.0.0.1:4321/"));
        Assert.Equal("http://127.0.0.1:4321/", valid.BaseUri.ToString());
        var ipv6 = new RemoteConnectionOptions(new Uri("http://[::1]:4321/"));
        Assert.Equal(4321, ipv6.BaseUri.Port);
    }

    [Fact]
    public async Task ConnectAsync_ExchangesTokenSetsCookieWaitsReady_AndEmitsSafeStartupTrace()
    {
        await using var server = new FakeRemoteServer();
        var seenConnectCookie = string.Empty;
        var seenWsCookie = string.Empty;
        var seenQuery = string.Empty;

        server.OnHttpRequest = req =>
        {
            if (req.Path == "/connect")
            {
                seenQuery = req.Query;
                seenConnectCookie = $"dsh-auth-{server.Port}=signed-cookie-gen1";
                return Task.FromResult(new FakeRemoteServer.HttpResponseData(
                    302,
                    "Found",
                    [
                        ("Location", "/"),
                        ("Set-Cookie", $"{seenConnectCookie}; Path=/; HttpOnly; SameSite=Strict"),
                    ],
                    []));
            }
            return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(404, "{}"));
        };

        server.OnWebSocketConnected = async (req, ws, ct) =>
        {
            req.Headers.TryGetValue("Cookie", out seenWsCookie!);
            var openRaw = await FakeRemoteServer.ReceiveTextAsync(ws, ct);
            Assert.NotNull(openRaw);
            var streamId = ParseStreamId(openRaw!);
            await FakeRemoteServer.SendTextAsync(ws, ReadyFrameJson(streamId, "client-1", "/home/dsh"), ct);
            await FakeRemoteServer.ReceiveTextAsync(ws, ct);
        };

        var logs = new List<string>();
        var traceLines = new List<string>();
        var trace = StartupTrace.Start(line => { lock (traceLines) { traceLines.Add(line); } }, "run-b");
        await using var conn = new DshRemoteConnection(
            FastOptions(server.BaseUri),
            () => SecretToken,
            trace: trace,
            log: msg => { lock (logs) { logs.Add(msg); } });

        var gen = await conn.ConnectAsync(CancellationToken.None);

        Assert.Equal(1, gen.GenerationId);
        Assert.Equal("client-1", gen.ClientId);
        Assert.Equal("/home/dsh", gen.HostHome);
        Assert.Equal(RemoteConnectionState.Connected, conn.State);
        Assert.Equal($"?token={SecretToken}&interactions=answer", seenQuery);
        Assert.Equal(seenConnectCookie, seenWsCookie);

        Assert.Contains(traceLines, l => l.Contains("\"protocol.connected\"", StringComparison.Ordinal));
        Assert.Contains(traceLines, l => l.Contains("\"protocol.initialized\"", StringComparison.Ordinal));
        Assert.DoesNotContain(traceLines, l => l.Contains(SecretToken, StringComparison.Ordinal));
        Assert.DoesNotContain(logs, l => l.Contains(SecretToken, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConnectAsync_RejectsNonReadyFirstEventsFrame_AndDoesNotPublishGeneration()
    {
        await using var server = new FakeRemoteServer();
        var connectAttempts = 0;
        var rejectedFirstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        server.OnHttpRequest = req =>
        {
            if (req.Path == "/connect")
            {
                return Task.FromResult(new FakeRemoteServer.HttpResponseData(
                    302, "Found",
                    [("Location", "/"), ("Set-Cookie", $"dsh-auth-{server.Port}=c1; HttpOnly")],
                    []));
            }
            return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(404, "{}"));
        };

        server.OnWebSocketConnected = async (_, ws, ct) =>
        {
            var attempt = Interlocked.Increment(ref connectAttempts);
            var openRaw = await FakeRemoteServer.ReceiveTextAsync(ws, ct);
            var streamId = ParseStreamId(openRaw!);

            if (attempt == 1)
            {
                await FakeRemoteServer.SendTextAsync(ws,
                    $$"""{"type":"item","streamId":"{{streamId}}","value":{"type":"emit","event":"bad"} }""",
                    ct);
                rejectedFirstAttempt.TrySetResult();
                return;
            }

            await FakeRemoteServer.SendTextAsync(ws, ReadyFrameJson(streamId, "client-2", "/h"), ct);
            await FakeRemoteServer.ReceiveTextAsync(ws, ct);
        };

        await using var conn = new DshRemoteConnection(FastOptions(server.BaseUri), () => SecretToken);
        var observedGenerations = new ConcurrentQueue<ConnectionGeneration?>();
        conn.GenerationChanged += g => observedGenerations.Enqueue(g);

        var gen = await conn.ConnectAsync(CancellationToken.None);
        await rejectedFirstAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("client-2", gen.ClientId);
        Assert.Equal(2, connectAttempts);
        Assert.DoesNotContain(observedGenerations, g => g is { ClientId: "" });
    }

    [Fact]
    public async Task CallAsync_RefreshesGenerationOn401_AndRetriesOnceSuccessfully()
    {
        await using var server = new FakeRemoteServer();
        var genCounter = 0;
        var apiCalls = 0;

        server.OnHttpRequest = req =>
        {
            if (req.Path == "/connect")
            {
                Interlocked.Increment(ref genCounter);
                return Task.FromResult(new FakeRemoteServer.HttpResponseData(
                    302, "Found",
                    [("Location", "/"), ("Set-Cookie", $"dsh-auth-{server.Port}=cookie-{genCounter}; HttpOnly")],
                    []));
            }

            if (req.Path == "/api/session.list")
            {
                var rpcId = ParseRpcId(req.Body);
                var callNum = Interlocked.Increment(ref apiCalls);
                req.Headers.TryGetValue("Cookie", out var cookie);
                if (callNum == 1)
                {
                    Assert.Equal($"dsh-auth-{server.Port}=cookie-1", cookie);
                    return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(401, """{"error":"unauthorized"}"""));
                }

                Assert.Equal($"dsh-auth-{server.Port}=cookie-2", cookie);
                return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(200,
                    ServerResponseJson(rpcId, """{"refreshed":true}""")));
            }

            return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(404, "{}"));
        };

        server.OnWebSocketConnected = async (_, ws, ct) =>
        {
            var openRaw = await FakeRemoteServer.ReceiveTextAsync(ws, ct);
            var streamId = ParseStreamId(openRaw!);
            await FakeRemoteServer.SendTextAsync(ws, ReadyFrameJson(streamId, $"client-{genCounter}", "/h"), ct);
            await FakeRemoteServer.ReceiveTextAsync(ws, ct);
        };

        await using var conn = new DshRemoteConnection(FastOptions(server.BaseUri), () => SecretToken);
        var gen1 = await conn.ConnectAsync(CancellationToken.None);
        Assert.Equal(1, gen1.GenerationId);

        var outcome = await conn.CallAsync("session.list", ct: CancellationToken.None);
        Assert.True(outcome.Ok);
        Assert.True(outcome.Value!.Value.GetProperty("refreshed").GetBoolean());
        Assert.Equal(2, apiCalls);
        Assert.Equal(2, conn.CurrentGeneration!.GenerationId);
    }

    [Fact]
    public async Task CallAsync_HandlesConcurrentCallsAndMultipartBinaryPayload()
    {
        await using var server = new FakeRemoteServer();
        server.OnHttpRequest = req =>
        {
            if (req.Path == "/connect")
            {
                return Task.FromResult(new FakeRemoteServer.HttpResponseData(
                    302, "Found",
                    [("Location", "/"), ("Set-Cookie", $"dsh-auth-{server.Port}=cookie-mp; HttpOnly")],
                    []));
            }

            if (req.Path == "/api/echo")
            {
                var rpcId = ParseRpcId(req.Body);
                return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(200,
                    ServerResponseJson(rpcId, $$"""{"pong":"{{rpcId}}"}""")));
            }

            if (req.Path == "/api/blob")
            {
                var rpcId = ParseRpcId(req.Body);
                const string boundary = "mp-bound-42";
                var meta = $$"""{"type":"server-response","rpcId":"{{rpcId}}","result":{"ok":true,"value":{"size":4} },"attachments":[{"path":["bytes"],"codec":"uint8array","part":"bytes-0"}]}""";
                var payload = new byte[] { 1, 2, 3, 4 };
                using var ms = new MemoryStream();
                ms.Write(Encoding.UTF8.GetBytes($"--{boundary}\r\nContent-Disposition: form-data; name=\"metadata\"\r\n\r\n{meta}\r\n"));
                ms.Write(Encoding.UTF8.GetBytes($"--{boundary}\r\nContent-Disposition: form-data; name=\"bytes-0\"\r\n\r\n"));
                ms.Write(payload);
                ms.Write(Encoding.UTF8.GetBytes($"\r\n--{boundary}--\r\n"));
                return Task.FromResult(FakeRemoteServer.HttpResponseData.Bytes(
                    200, $"multipart/form-data; boundary={boundary}", ms.ToArray()));
            }

            return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(404, "{}"));
        };

        server.OnWebSocketConnected = async (_, ws, ct) =>
        {
            var openRaw = await FakeRemoteServer.ReceiveTextAsync(ws, ct);
            var streamId = ParseStreamId(openRaw!);
            await FakeRemoteServer.SendTextAsync(ws, ReadyFrameJson(streamId, "c-mp"), ct);
            await FakeRemoteServer.ReceiveTextAsync(ws, ct);
        };

        await using var conn = new DshRemoteConnection(FastOptions(server.BaseUri), () => SecretToken);
        await conn.ConnectAsync(CancellationToken.None);

        var echoTasks = Enumerable.Range(0, 8)
            .Select(_ => conn.CallAsync("echo", ct: CancellationToken.None))
            .ToArray();
        var blobTask = conn.CallAsync("blob", ct: CancellationToken.None);

        var echoes = await Task.WhenAll(echoTasks);
        var blob = await blobTask;

        Assert.All(echoes, e => Assert.True(e.Ok));
        Assert.True(blob.Ok);
        Assert.Equal([1, 2, 3, 4], blob.Attachments["bytes-0"].ToArray());
    }

    private sealed class RecordingBackoffTimeProvider : TimeProvider
    {
        private readonly FakeTimeProvider _inner = new();
        private readonly Channel<TimeSpan> _delays = Channel.CreateUnbounded<TimeSpan>();

        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public override long GetTimestamp() => _inner.GetTimestamp();
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _inner.CreateTimer(callback, state, dueTime, period);
            if (dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
            {
                _delays.Writer.TryWrite(dueTime);
            }
            return timer;
        }

        public ValueTask<TimeSpan> ReadNextDelayAsync(CancellationToken ct = default) =>
            _delays.Reader.ReadAsync(ct);

        public void Advance(TimeSpan delta) => _inner.Advance(delta);
    }

    [Fact]
    public async Task Reconnect_UsesExponentialBackoffAndIncrementsGeneration()
    {
        await using var server = new FakeRemoteServer();
        var time = new RecordingBackoffTimeProvider();
        var wsAttempts = 0;
        var clientObservedGen1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        server.OnHttpRequest = req =>
        {
            if (req.Path == "/connect")
            {
                return Task.FromResult(new FakeRemoteServer.HttpResponseData(
                    302, "Found",
                    [("Location", "/"), ("Set-Cookie", $"dsh-auth-{server.Port}=c-{wsAttempts}; HttpOnly")],
                    []));
            }
            return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(404, "{}"));
        };

        server.OnWebSocketConnected = async (_, ws, ct) =>
        {
            var n = Interlocked.Increment(ref wsAttempts);
            if (n == 1)
            {
                var openRaw = await FakeRemoteServer.ReceiveTextAsync(ws, ct);
                var streamId = ParseStreamId(openRaw!);
                await FakeRemoteServer.SendTextAsync(ws, ReadyFrameJson(streamId, "c-1"), ct);
                await clientObservedGen1.Task.WaitAsync(ct);
                await ws.CloseOutputAsync(WebSocketCloseStatus.InternalServerError, "drop", ct);
                return;
            }
            if (n is 2 or 3)
            {
                await ws.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "transient", ct);
                return;
            }
            var open4 = await FakeRemoteServer.ReceiveTextAsync(ws, ct);
            var stream4 = ParseStreamId(open4!);
            await FakeRemoteServer.SendTextAsync(ws, ReadyFrameJson(stream4, "c-4"), ct);
            await FakeRemoteServer.ReceiveTextAsync(ws, ct);
        };

        var options = new RemoteConnectionOptions(server.BaseUri)
        {
            BackoffBase = TimeSpan.FromSeconds(5),
            BackoffFactor = 2.0,
            BackoffMax = TimeSpan.FromSeconds(20),
            ReadyTimeout = TimeSpan.FromSeconds(5),
        };
        await using var conn = new DshRemoteConnection(options, () => SecretToken, time: time);
        var genNextReady = new TaskCompletionSource<ConnectionGeneration>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.GenerationChanged += g =>
        {
            if (g is { GenerationId: >= 2 })
            {
                genNextReady.TrySetResult(g);
            }
        };

        var gen1 = await conn.ConnectAsync(CancellationToken.None);
        Assert.Equal(1, gen1.GenerationId);
        clientObservedGen1.TrySetResult();

        // Attempt 2 runs immediately (attempt reset to 0 on ready) and fails -> registers BackoffBase (5s) timer.
        var delay1 = await time.ReadNextDelayAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(5), delay1);
        Assert.False(genNextReady.Task.IsCompleted);
        time.Advance(delay1);

        // Attempt 3 runs and fails -> registers exponential BackoffBase * 2 (10s) timer.
        var delay2 = await time.ReadNextDelayAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(10), delay2);
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.False(genNextReady.Task.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(5));
        var genNext = await genNextReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(genNext.GenerationId > gen1.GenerationId);
        Assert.Equal(4, genNext.GenerationId);
        Assert.Equal("c-4", genNext.ClientId);
    }

    [Fact]
    public async Task RequestReconnect_SupersedesStaleGeneration_SoCancelledSourceNeverOverwritesNewerGeneration()
    {
        await using var server = new FakeRemoteServer();
        var attemptCount = 0;
        var gen1ReadyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gen1WsEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gen2Published = new TaskCompletionSource<ConnectionGeneration>(TaskCreationOptions.RunContinuationsAsynchronously);

        server.OnHttpRequest = req =>
        {
            if (req.Path == "/connect")
            {
                return Task.FromResult(new FakeRemoteServer.HttpResponseData(
                    302, "Found",
                    [("Location", "/"), ("Set-Cookie", $"dsh-auth-{server.Port}=c; HttpOnly")],
                    []));
            }
            return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(404, "{}"));
        };

        server.OnWebSocketConnected = async (_, ws, ct) =>
        {
            var n = Interlocked.Increment(ref attemptCount);
            var openRaw = await FakeRemoteServer.ReceiveTextAsync(ws, ct);
            var streamId = ParseStreamId(openRaw!);

            if (n == 1)
            {
                gen1WsEntered.TrySetResult();
                await gen1ReadyGate.Task.WaitAsync(ct);
                if (ws.State == WebSocketState.Open)
                {
                    await FakeRemoteServer.SendTextAsync(ws, ReadyFrameJson(streamId, "stale-1"), ct);
                }
                return;
            }

            await FakeRemoteServer.SendTextAsync(ws, ReadyFrameJson(streamId, "fresh-2"), ct);
            await FakeRemoteServer.ReceiveTextAsync(ws, ct);
        };

        await using var conn = new DshRemoteConnection(FastOptions(server.BaseUri), () => SecretToken);
        var seen = new ConcurrentQueue<ConnectionGeneration?>();
        conn.GenerationChanged += g =>
        {
            seen.Enqueue(g);
            if (g is { ClientId: "fresh-2" })
            {
                gen2Published.TrySetResult(g);
            }
        };

        var connectTask = conn.ConnectAsync(CancellationToken.None);
        await gen1WsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        conn.RequestReconnect();
        var fresh = await gen2Published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gen1ReadyGate.TrySetResult();

        var connected = await connectTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(fresh.GenerationId, connected.GenerationId);
        Assert.Equal("fresh-2", conn.CurrentGeneration!.ClientId);
        Assert.DoesNotContain(seen, g => g is { ClientId: "stale-1" });
    }

    [Fact]
    public async Task RequestReconnect_InterruptsBackoffImmediately_WhenTokenBecomesAvailable()
    {
        await using var server = new FakeRemoteServer();
        server.OnHttpRequest = req =>
        {
            if (req.Path == "/connect")
            {
                return Task.FromResult(new FakeRemoteServer.HttpResponseData(
                    302, "Found",
                    [("Location", "/"), ("Set-Cookie", $"dsh-auth-{server.Port}=c-token; HttpOnly")],
                    []));
            }
            return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(404, "{}"));
        };
        server.OnWebSocketConnected = async (_, ws, ct) =>
        {
            var openRaw = await FakeRemoteServer.ReceiveTextAsync(ws, ct);
            var streamId = ParseStreamId(openRaw!);
            await FakeRemoteServer.SendTextAsync(ws, ReadyFrameJson(streamId, "client-token"), ct);
            await FakeRemoteServer.ReceiveTextAsync(ws, ct);
        };

        string? token = null;
        var tokenQueried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new RecordingBackoffTimeProvider();
        var options = new RemoteConnectionOptions(server.BaseUri)
        {
            // 故意把退避基数设得远大于断言阈值：若 RequestReconnect 不能打断 Task.Delay，本测试会超时失败。
            BackoffBase = TimeSpan.FromSeconds(2),
            BackoffFactor = 2.0,
            BackoffMax = TimeSpan.FromSeconds(10),
            ReadyTimeout = TimeSpan.FromSeconds(5),
        };
        await using var conn = new DshRemoteConnection(
            options,
            () =>
            {
                tokenQueried.TrySetResult();
                return Volatile.Read(ref token);
            },
            time: time);

        var connectTask = conn.ConnectAsync(CancellationToken.None);
        await tokenQueried.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // 首次 null token 尝试失败后必须真正进入 BackoffBase 退避：假时钟记录到 2 s 的 Task.Delay。
        var backoff = await time.ReadNextDelayAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(2), backoff);
        Assert.False(connectTask.IsCompleted);

        var tokenAvailableAt = System.Diagnostics.Stopwatch.StartNew();
        Volatile.Write(ref token, SecretToken);
        conn.RequestReconnect();
        // 不推进假时钟：只有 RequestReconnect 打断了退避，连接才可能在这种状态下完成。
        var generation = await connectTask.WaitAsync(TimeSpan.FromSeconds(5));
        tokenAvailableAt.Stop();

        Assert.Equal("client-token", generation.ClientId);
        Assert.Equal(RemoteConnectionState.Connected, conn.State);
        Assert.True(
            tokenAvailableAt.Elapsed < TimeSpan.FromMilliseconds(500),
            $"token 更新后 RequestReconnect 应立即打断 2s 退避，实际耗时 {tokenAvailableAt.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task InboundMessage_ExceedingMaxInboundMessageBytes_ClosesGenerationAndReconnects()
    {
        await using var server = new FakeRemoteServer();
        var attempt = 0;
        var reconnected = new TaskCompletionSource<ConnectionGeneration>(TaskCreationOptions.RunContinuationsAsynchronously);

        server.OnHttpRequest = req =>
        {
            if (req.Path == "/connect")
            {
                return Task.FromResult(new FakeRemoteServer.HttpResponseData(
                    302, "Found",
                    [("Location", "/"), ("Set-Cookie", $"dsh-auth-{server.Port}=c; HttpOnly")],
                    []));
            }
            return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(404, "{}"));
        };

        server.OnWebSocketConnected = async (_, ws, ct) =>
        {
            var n = Interlocked.Increment(ref attempt);
            var openRaw = await FakeRemoteServer.ReceiveTextAsync(ws, ct);
            var streamId = ParseStreamId(openRaw!);

            if (n == 1)
            {
                await FakeRemoteServer.SendTextAsync(ws, ReadyFrameJson(streamId, "small-ok"), ct);
                var bigText = new string('x', 512);
                await FakeRemoteServer.SendTextAsync(ws,
                    $$"""{"type":"item","streamId":"{{streamId}}","value":{"type":"emit","event":"big","args":{"blob":"{{bigText}}"} } }""",
                    ct);
                await FakeRemoteServer.ReceiveTextAsync(ws, ct);
                return;
            }

            await FakeRemoteServer.SendTextAsync(ws, ReadyFrameJson(streamId, "recovered-2"), ct);
            await FakeRemoteServer.ReceiveTextAsync(ws, ct);
        };

        await using var conn = new DshRemoteConnection(FastOptions(server.BaseUri, maxInboundBytes: 256), () => SecretToken);
        conn.GenerationChanged += g =>
        {
            if (g is { ClientId: "recovered-2" })
            {
                reconnected.TrySetResult(g);
            }
        };

        await conn.ConnectAsync(CancellationToken.None);
        var next = await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(next.GenerationId >= 2);
    }

    [Fact]
    public async Task MuxStreams_AndForwardedWaterfallEvents_ExchangeFramesAndCancelOnDispose()
    {
        await using var server = new FakeRemoteServer();
        var serverReceived = new ConcurrentQueue<string>();
        string? serverRecordedEventResult = null;
        var cancelObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        server.OnHttpRequest = req =>
        {
            if (req.Path == "/connect")
            {
                return Task.FromResult(new FakeRemoteServer.HttpResponseData(
                    302, "Found",
                    [("Location", "/"), ("Set-Cookie", $"dsh-auth-{server.Port}=c-mux; HttpOnly")],
                    []));
            }
            if (req.Path == "/api/$events/result")
            {
                serverRecordedEventResult = Encoding.UTF8.GetString(req.Body);
                var rpcId = ParseRpcId(req.Body);
                return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(200,
                    $$"""{"type":"server-response","rpcId":"{{rpcId}}","result":{"ok":true} }"""));
            }
            return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(404, "{}"));
        };

        server.OnWebSocketConnected = async (_, ws, ct) =>
        {
            var openEvents = await FakeRemoteServer.ReceiveTextAsync(ws, ct);
            var evStreamId = ParseStreamId(openEvents!);
            await FakeRemoteServer.SendTextAsync(ws, ReadyFrameJson(evStreamId, "client-mux"), ct);

            await FakeRemoteServer.SendTextAsync(ws,
                $$"""{"type":"item","streamId":"{{evStreamId}}","value":{"type":"waterfall","event":"approval/request","eventId":"ev-7","agentId":"agent-1","args":{"tool":"bash"} } }""",
                ct);

            string? stream1 = null;
            string? stream2 = null;
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var msg = await FakeRemoteServer.ReceiveTextAsync(ws, ct);
                if (msg is null)
                {
                    break;
                }
                serverReceived.Enqueue(msg);
                using var doc = JsonDocument.Parse(msg);
                var type = doc.RootElement.GetProperty("type").GetString();
                var sid = doc.RootElement.GetProperty("streamId").GetString()!;

                if (type == "open" && stream1 is null)
                {
                    stream1 = sid;
                    await FakeRemoteServer.SendTextAsync(ws,
                        $$"""{"type":"item","streamId":"{{sid}}","value":{"delta":"hello"} }""", ct);
                }
                else if (type == "end" && sid == stream1)
                {
                    await FakeRemoteServer.SendTextAsync(ws, $$"""{"type":"end","streamId":"{{sid}}"}""", ct);
                }
                else if (type == "open" && stream1 is not null)
                {
                    stream2 = sid;
                }
                else if (type == "cancel" && sid == stream2)
                {
                    cancelObserved.TrySetResult();
                }
            }
        };

        await using var conn = new DshRemoteConnection(FastOptions(server.BaseUri), () => SecretToken);
        var forwarded = new TaskCompletionSource<RemoteForwardedEventPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.ForwardedEventReceived += evt =>
        {
            forwarded.TrySetResult(evt);
            return ValueTask.CompletedTask;
        };

        var gen = await conn.ConnectAsync(CancellationToken.None);
        var evt = await forwarded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("approval/request", evt.Event);
        Assert.Equal("ev-7", evt.EventId);

        using var outcomeDoc = JsonDocument.Parse("""{"approved":true}""");
        var evOutcome = await conn.SendEventResultAsync(gen.ClientId, evt.EventId!, new RemoteEventOutcome("result", outcomeDoc.RootElement));
        Assert.True(evOutcome.Ok);
        Assert.NotNull(serverRecordedEventResult);
        Assert.Contains("\"eventId\":\"ev-7\"", serverRecordedEventResult!, StringComparison.Ordinal);

        var receivedFrames = new List<MuxInboundFrame>();
        await using (var s1 = await conn.OpenStreamAsync("session.stream", null, frame => { receivedFrames.Add(frame); return ValueTask.CompletedTask; }))
        {
            using var itemDoc = JsonDocument.Parse("""{"text":"ping"}""");
            await s1.SendItemAsync(itemDoc.RootElement);
            await s1.EndUplinkAsync();
            await s1.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(2, receivedFrames.Count);
        Assert.Equal("hello", receivedFrames[0].Value!.Value.GetProperty("delta").GetString());
        Assert.Equal("end", receivedFrames[1].Type);

        var s2 = await conn.OpenStreamAsync("session.watch", null, _ => ValueTask.CompletedTask);
        await s2.DisposeAsync();
        await cancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task BeginDrainAsync_RejectsNewRequestsAndCompletesCleanly()
    {
        await using var server = new FakeRemoteServer();
        server.OnHttpRequest = req => Task.FromResult(new FakeRemoteServer.HttpResponseData(
            302, "Found", [("Location", "/"), ("Set-Cookie", $"dsh-auth-{server.Port}=c-drain; HttpOnly")], []));
        server.OnWebSocketConnected = async (_, ws, ct) =>
        {
            var openRaw = await FakeRemoteServer.ReceiveTextAsync(ws, ct);
            var streamId = ParseStreamId(openRaw!);
            await FakeRemoteServer.SendTextAsync(ws, ReadyFrameJson(streamId, "client-drain"), ct);
            await FakeRemoteServer.ReceiveTextAsync(ws, ct);
        };

        await using var conn = new DshRemoteConnection(FastOptions(server.BaseUri), () => SecretToken);
        await conn.ConnectAsync(CancellationToken.None);

        await conn.BeginDrainAsync(CancellationToken.None);

        Assert.Equal(RemoteConnectionState.Draining, conn.State);
        Assert.Null(conn.CurrentGeneration);
        await Assert.ThrowsAsync<RemoteDrainingException>(() => conn.CallAsync("session.list"));
        await Assert.ThrowsAsync<RemoteDrainingException>(() => conn.OpenStreamAsync("session.watch", null, _ => ValueTask.CompletedTask));
    }

    [Fact]
    public async Task CallAsync_AndOpenStreamAsync_WithoutPriorConnect_AutoStartLoopAndSucceed()
    {
        await using var server = new FakeRemoteServer();
        server.OnHttpRequest = req =>
        {
            if (req.Path == "/connect")
            {
                return Task.FromResult(new FakeRemoteServer.HttpResponseData(
                    302, "Found", [("Location", "/"), ("Set-Cookie", $"dsh-auth-{server.Port}=c-auto; HttpOnly")], []));
            }
            if (req.Path == "/api/echo")
            {
                var rpcId = ParseRpcId(req.Body);
                return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(200,
                    ServerResponseJson(rpcId, """{"ok":true}""")));
            }
            return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(404, "{}"));
        };
        server.OnWebSocketConnected = async (_, ws, ct) =>
        {
            var openRaw = await FakeRemoteServer.ReceiveTextAsync(ws, ct);
            var streamId = ParseStreamId(openRaw!);
            await FakeRemoteServer.SendTextAsync(ws, ReadyFrameJson(streamId, "client-auto"), ct);
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var next = await FakeRemoteServer.ReceiveTextAsync(ws, ct);
                if (next is null)
                {
                    break;
                }
                using var doc = JsonDocument.Parse(next);
                if (doc.RootElement.GetProperty("type").GetString() == "open")
                {
                    var sid = doc.RootElement.GetProperty("streamId").GetString()!;
                    await FakeRemoteServer.SendTextAsync(ws, "{\"type\":\"end\",\"streamId\":\"" + sid + "\"}", ct);
                }
            }
        };

        // CallAsync on a fresh DshRemoteConnection without calling ConnectAsync/Start first
        await using (var conn1 = new DshRemoteConnection(FastOptions(server.BaseUri), () => SecretToken))
        {
            var rpc = await conn1.CallAsync("echo").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(rpc.Ok);
        }

        // OpenStreamAsync on a fresh DshRemoteConnection without calling ConnectAsync/Start first
        await using (var conn2 = new DshRemoteConnection(FastOptions(server.BaseUri), () => SecretToken))
        {
            await using var stream = await conn2.OpenStreamAsync("watch", null, _ => ValueTask.CompletedTask)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await stream.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task CallAsync_WhenHostNeverBecomesReady_TimesOutCleanlyWithoutHanging()
    {
        await using var server = new FakeRemoteServer();
        server.OnHttpRequest = _ => Task.FromResult(FakeRemoteServer.HttpResponseData.Json(503, "{}"));

        var options = new RemoteConnectionOptions(server.BaseUri)
        {
            BackoffBase = TimeSpan.FromMilliseconds(20),
            BackoffMax = TimeSpan.FromMilliseconds(40),
            UnaryTimeout = TimeSpan.FromMilliseconds(150),
        };
        await using var conn = new DshRemoteConnection(options, () => SecretToken);
        await Assert.ThrowsAsync<TimeoutException>(() => conn.CallAsync("echo").WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task PrepareForHostStart_AfterBeginDrain_AllowsReconnectAndEmitsTraceSpans()
    {
        await using var server = new FakeRemoteServer();
        server.OnHttpRequest = req =>
        {
            if (req.Path == "/connect")
            {
                return Task.FromResult(new FakeRemoteServer.HttpResponseData(
                    302, "Found", [("Location", "/"), ("Set-Cookie", $"dsh-auth-{server.Port}=c-restart; HttpOnly")], []));
            }
            if (req.Path == "/api/echo")
            {
                var rpcId = ParseRpcId(req.Body);
                return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(200,
                    ServerResponseJson(rpcId, """{"restarted":true}""")));
            }
            return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(404, "{}"));
        };
        var clientSeq = 0;
        server.OnWebSocketConnected = async (_, ws, ct) =>
        {
            var openRaw = await FakeRemoteServer.ReceiveTextAsync(ws, ct);
            var streamId = ParseStreamId(openRaw!);
            var seq = Interlocked.Increment(ref clientSeq);
            await FakeRemoteServer.SendTextAsync(ws, ReadyFrameJson(streamId, $"client-r-{seq}"), ct);
            await FakeRemoteServer.ReceiveTextAsync(ws, ct);
        };

        var traceLines = new List<string>();
        var trace = DshMobile.Core.Diagnostics.StartupTrace.Start(line => traceLines.Add(line));
        await using var conn = new DshRemoteConnection(FastOptions(server.BaseUri), () => SecretToken, trace: trace);

        var gen1 = await conn.ConnectAsync(CancellationToken.None);
        Assert.Equal("client-r-1", gen1.ClientId);
        Assert.Contains(traceLines, l => l.Contains("protocol.connected", StringComparison.Ordinal));
        Assert.Contains(traceLines, l => l.Contains("protocol.initialized", StringComparison.Ordinal));

        // 模拟宿主停机排空（F14）
        await conn.BeginDrainAsync(CancellationToken.None);
        Assert.Equal(RemoteConnectionState.Draining, conn.State);

        // 模拟宿主重启周期重新激活连接（F4 / F14）
        var trace2Lines = new List<string>();
        var trace2 = DshMobile.Core.Diagnostics.StartupTrace.Start(line => trace2Lines.Add(line));
        conn.PrepareForHostStart(trace2);
        var gen2 = await conn.ConnectAsync(CancellationToken.None);
        Assert.Equal("client-r-2", gen2.ClientId);
        Assert.Contains(trace2Lines, l => l.Contains("protocol.connected", StringComparison.Ordinal));
        Assert.Contains(trace2Lines, l => l.Contains("protocol.initialized", StringComparison.Ordinal));

        var rpc = await conn.CallAsync("echo");
        Assert.True(rpc.Ok);
        Assert.True(rpc.Value!.Value.GetProperty("restarted").GetBoolean());
    }
}
