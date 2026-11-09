using System.Collections.Concurrent;
using System.Text.Json;
using DshMobile.Core.Bridge;
using Microsoft.Extensions.Time.Testing;

namespace DshMobile.Core.Tests;

public class BridgeClientTests
{
    /// <summary>Records requests; the test decides when/how to answer.</summary>
    private sealed class ManualTransport : IBridgeTransport
    {
        public IBridgeSink? Sink;
        public readonly ConcurrentQueue<BridgeRequest> Sent = new();
        public Exception? ThrowOnSend;

        public void Attach(IBridgeSink sink) => Sink = sink;

        public void Send(string requestJson)
        {
            if (ThrowOnSend is not null)
            {
                throw ThrowOnSend;
            }
            Sent.Enqueue(JsonSerializer.Deserialize(requestJson, BridgeJsonContext.Default.BridgeRequest)!);
        }

        public void Reply(string requestId, bool ok = true, string code = "OK", string payload = "{}") =>
            Sink!.OnInbound($$$"""{"protocol":1,"requestId":"{{{requestId}}}","ok":{{{(ok ? "true" : "false")}}},"code":"{{{code}}}","message":null,"payload":{{{payload}}}}""");
    }

    /// <summary>Answers every ping asynchronously on the thread pool, like the native worker thread.</summary>
    private sealed class EchoTransport : IBridgeTransport
    {
        private IBridgeSink? _sink;
        public void Attach(IBridgeSink sink) => _sink = sink;

        public void Send(string requestJson)
        {
            var req = JsonSerializer.Deserialize(requestJson, BridgeJsonContext.Default.BridgeRequest)!;
            ThreadPool.QueueUserWorkItem(_ => _sink!.OnInbound(
                $$$"""{"protocol":1,"requestId":"{{{req.RequestId}}}","ok":true,"code":"OK","message":null,"payload":{"seq":1}}"""));
        }
    }

    private static async Task<BridgeRequest> WaitForSent(ManualTransport t)
    {
        for (var i = 0; i < 200; i++)
        {
            if (t.Sent.TryDequeue(out var r))
            {
                return r;
            }
            await Task.Delay(5);
        }
        throw new TimeoutException("request was not sent");
    }

    [Fact]
    public async Task Execute_CorrelatesOutOfOrderResponses()
    {
        var t = new ManualTransport();
        using var client = new BridgeClient(t);

        var a = client.ExecuteAsync(BridgeMethods.Ping);
        var reqA = await WaitForSent(t);
        var b = client.ExecuteAsync(BridgeMethods.Info);
        var reqB = await WaitForSent(t);
        Assert.Equal(2, client.InFlightCount);

        t.Reply(reqB.RequestId, payload: """{"which":"b"}""");
        t.Reply(reqA.RequestId, payload: """{"which":"a"}""");

        Assert.Equal("a", (await a).Payload!.Value.GetProperty("which").GetString());
        Assert.Equal("b", (await b).Payload!.Value.GetProperty("which").GetString());
        Assert.Equal(0, client.InFlightCount);
        Assert.Equal(BridgeMethods.Info, reqB.Method);
        Assert.Equal(30_000, reqA.DeadlineMs);
    }

    [Fact]
    public async Task Execute_NativeErrorIsReturnedNotThrown()
    {
        var t = new ManualTransport();
        using var client = new BridgeClient(t);
        var task = client.ExecuteAsync("runtime.start");
        t.Reply((await WaitForSent(t)).RequestId, ok: false, code: BridgeErrorCodes.MethodNotFound, payload: "null");
        var resp = await task;
        Assert.False(resp.Ok);
        Assert.Equal(BridgeErrorCodes.MethodNotFound, resp.Code);
    }

    [Fact]
    public async Task Execute_NoResponse_YieldsDeadlineExceeded_AndLateReplyIsOrphan()
    {
        var time = new FakeTimeProvider();
        var t = new ManualTransport();
        using var client = new BridgeClient(t, time);

        var task = client.ExecuteAsync(BridgeMethods.Ping, deadline: TimeSpan.FromSeconds(1));
        var req = await WaitForSent(t);
        time.Advance(TimeSpan.FromSeconds(1) + BridgeClient.LocalGrace + TimeSpan.FromMilliseconds(1));

        var resp = await task;
        Assert.Equal(BridgeErrorCodes.DeadlineExceeded, resp.Code);
        Assert.False(resp.Ok);
        Assert.Equal(0, client.InFlightCount);

        t.Reply(req.RequestId);
        Assert.Equal(1, client.OrphanResponses);
    }

    [Fact]
    public async Task Execute_Cancellation_ThrowsAndCleansUp()
    {
        var t = new ManualTransport();
        using var client = new BridgeClient(t);
        using var cts = new CancellationTokenSource();
        var task = client.ExecuteAsync(BridgeMethods.Ping, cancellationToken: cts.Token);
        await WaitForSent(t);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, client.InFlightCount);
    }

    [Fact]
    public async Task Execute_TransportFailure_IsInternal()
    {
        var t = new ManualTransport { ThrowOnSend = new InvalidOperationException("jni down") };
        using var client = new BridgeClient(t);
        var resp = await client.ExecuteAsync(BridgeMethods.Ping);
        Assert.Equal(BridgeErrorCodes.Internal, resp.Code);
        Assert.Contains("jni down", resp.Message);
        Assert.Equal(0, client.InFlightCount);
    }

    [Fact]
    public async Task Dispose_ResolvesPendingAsClosed_AndRejectsNewCalls()
    {
        var t = new ManualTransport();
        var client = new BridgeClient(t);
        var pending = client.ExecuteAsync(BridgeMethods.Ping);
        await WaitForSent(t);
        client.Dispose();
        Assert.Equal(BridgeErrorCodes.Closed, (await pending).Code);
        Assert.Equal(BridgeErrorCodes.Closed, (await client.ExecuteAsync(BridgeMethods.Ping)).Code);
    }

    [Fact]
    public void Inbound_MalformedAndEvents_AreClassified()
    {
        var t = new ManualTransport();
        using var client = new BridgeClient(t);
        BridgeEvent? seen = null;
        client.EventReceived += (_, e) => seen = e;
        client.EventReceived += (_, _) => throw new InvalidOperationException("subscriber bug must not escape");

        t.Sink!.OnInbound("garbage");
        t.Sink.OnInbound("""{"protocol":1,"type":"event","event":"bridge.lifecycle","payload":{"state":"CLOSED"}}""");

        Assert.Equal(1, client.MalformedMessages);
        Assert.Equal("bridge.lifecycle", seen?.Event);
    }

    [Fact]
    public async Task Burst_1000Concurrent_AllSucceed_AndNothingRemainsInFlight()
    {
        using var client = new BridgeClient(new EchoTransport());
        var results = await Task.WhenAll(Enumerable.Range(0, 1000).Select(_ => client.ExecuteAsync(BridgeMethods.Ping)));
        Assert.All(results, r => Assert.True(r.Ok));
        Assert.Equal(1000, results.Select(r => r.RequestId).Distinct().Count());
        Assert.Equal(0, client.InFlightCount);
        Assert.Equal(0, client.OrphanResponses);
    }
}
