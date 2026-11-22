using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
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

    [Fact]
    public async Task Reconnect_UsesExponentialBackoffAndIncrementsGeneration()
    {
        await using var server = new FakeRemoteServer();
        var fakeTime = new FakeTimeProvider();
        var wsAttempts = 0;
        var firstClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

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
                await Task.Delay(20, ct);
                await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, "drop", ct);
                firstClosed.TrySetResult();
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
        await using var conn = new DshRemoteConnection(options, () => SecretToken, time: fakeTime);
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

        await firstClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        SpinWait.SpinUntil(() => wsAttempts >= 2, TimeSpan.FromSeconds(5));
        await Task.Delay(30);
        Assert.False(genNextReady.Task.IsCompleted);

        // Attempt 2 failed -> waits 5s (5 * 2^0) before Attempt 3
        fakeTime.Advance(TimeSpan.FromSeconds(5));
        SpinWait.SpinUntil(() => wsAttempts >= 3, TimeSpan.FromSeconds(5));
        await Task.Delay(30);
        Assert.False(genNextReady.Task.IsCompleted);

        // Attempt 3 failed -> waits 10s (5 * 2^1) before Attempt 4
        fakeTime.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(30);
        Assert.Equal(3, wsAttempts);
        Assert.False(genNextReady.Task.IsCompleted);

        fakeTime.Advance(TimeSpan.FromSeconds(5));
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
}
