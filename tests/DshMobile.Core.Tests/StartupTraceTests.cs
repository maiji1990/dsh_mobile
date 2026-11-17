using System.Text.Json;
using DshMobile.Core.Diagnostics;

namespace DshMobile.Core.Tests;

/// <summary>
/// The startup trace is observability only: one parseable line per span, a monotonic clock, nothing sensitive and no
/// way for a broken sink to change the outcome of a start (docs/STARTUP_PERF.md).
/// </summary>
public sealed class StartupTraceTests
{
    private sealed class Clock(double now = 0d)
    {
        public double Now { get; set; } = now;

        public double Read() => Now;

        public void Advance(double ms) => Now += ms;
    }

    private static JsonElement Parse(string line)
    {
        Assert.StartsWith(StartupTrace.LogPrefix + " {", line, StringComparison.Ordinal);
        Assert.EndsWith("}", line, StringComparison.Ordinal);
        return JsonElement.Parse(line[(StartupTrace.LogPrefix.Length + 1)..]);
    }

    [Fact]
    public void Span_RecordsStageDurationSinceStartAndResult()
    {
        var clock = new Clock();
        var lines = new List<string>();
        var trace = StartupTrace.Start(lines.Add, "run1", clock.Read);
        clock.Advance(5);
        var span = trace.Measure("guest_assets.total");
        clock.Advance(23);
        span.End();

        var record = Parse(Assert.Single(lines));
        Assert.Equal(1, record.GetProperty("schema").GetInt32());
        Assert.Equal("run1", record.GetProperty("runId").GetString());
        Assert.Equal("guest_assets.total", record.GetProperty("stage").GetString());
        Assert.Equal(23d, record.GetProperty("elapsedMs").GetDouble(), 3);
        Assert.Equal(28d, record.GetProperty("sinceStartMs").GetDouble(), 3);
        Assert.Equal("ok", record.GetProperty("result").GetString());
        Assert.Equal(JsonValueKind.Object, record.GetProperty("details").ValueKind);
        Assert.Empty(record.GetProperty("details").EnumerateObject());
    }

    [Fact]
    public void NestedSpans_KeepTheirOwnDurations_AndAnEndedSpanIgnoresASecondEnd()
    {
        var clock = new Clock();
        var lines = new List<string>();
        var trace = StartupTrace.Start(lines.Add, "run2", clock.Read);
        var outer = trace.Measure("host.ready");
        clock.Advance(5);
        var inner = trace.Measure("sandbox.spawn");
        clock.Advance(10);
        inner.End();
        clock.Advance(5);
        outer.End();
        inner.End("failed"); // ignored: one record per span

        var records = lines.Select(Parse).ToList();
        Assert.Equal(2, records.Count);
        Assert.Equal(10d, records[0].GetProperty("elapsedMs").GetDouble(), 3);
        Assert.Equal("sandbox.spawn", records[0].GetProperty("stage").GetString());
        Assert.Equal(20d, records[1].GetProperty("elapsedMs").GetDouble(), 3);
        Assert.All(records, r => Assert.Equal("ok", r.GetProperty("result").GetString()));
    }

    [Fact]
    public void DisposeWithoutEnd_RecordsIncomplete()
    {
        var clock = new Clock();
        var lines = new List<string>();
        var trace = StartupTrace.Start(lines.Add, "run3", clock.Read);
        using (trace.Measure("dsh.token_available"))
        {
            clock.Advance(1);
        }
        Assert.Equal("incomplete", Parse(Assert.Single(lines)).GetProperty("result").GetString());
    }

    [Fact]
    public async Task Extensions_RecordOkFailedAndCanceled()
    {
        var clock = new Clock();
        var lines = new List<string>();
        var trace = StartupTrace.Start(lines.Add, "run4", clock.Read);

        Assert.True(await trace.TraceAsync("coordinator.sync", () => Task.FromResult(true)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => trace.TraceAsync("guest_assets.total", () => throw new InvalidOperationException("x")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => trace.TraceAsync("host.ready", () => throw new OperationCanceledException()));

        var results = lines.Select(l => Parse(l).GetProperty("result").GetString()).ToList();
        Assert.Equal(["ok", "failed", "canceled"], results);
    }

    [Fact]
    public void Details_AreNumbersBoolsAndCodes_EverythingElseIsRedacted()
    {
        var clock = new Clock();
        var lines = new List<string>();
        var trace = StartupTrace.Start(lines.Add, "run5", clock.Read);
        trace.EmitSpan("guest_assets.corepack", 0, StartupTrace.Ok, new Dictionary<string, object?>
        {
            ["cacheHit"] = false,
            ["extracted"] = true,
            ["written"] = 3,
            ["stream"] = "stdout",
            ["code"] = "RUNTIME_NOT_INSTALLED",
            ["token"] = "eyJhbGciOiJIUzI1NiJ9.payload.signature", // must never appear
            ["url"] = "http://127.0.0.1:3080/?token=abc",
            ["prompt"] = "please summarise my notes",
            ["bad key"] = 1,
            ["ratio"] = 0.5,
        });

        var details = Parse(Assert.Single(lines)).GetProperty("details");
        Assert.False(details.GetProperty("cacheHit").GetBoolean());
        Assert.True(details.GetProperty("extracted").GetBoolean());
        Assert.Equal(3, details.GetProperty("written").GetInt32());
        Assert.Equal("stdout", details.GetProperty("stream").GetString());
        Assert.Equal("RUNTIME_NOT_INSTALLED", details.GetProperty("code").GetString());
        Assert.Equal(0.5d, details.GetProperty("ratio").GetDouble(), 3);
        Assert.Equal(StartupTrace.Redacted, details.GetProperty("token").GetString());
        Assert.Equal(StartupTrace.Redacted, details.GetProperty("url").GetString());
        Assert.Equal(StartupTrace.Redacted, details.GetProperty("prompt").GetString());
        Assert.False(details.TryGetProperty("bad key", out _));
    }

    [Fact]
    public void DisabledTrace_EmitsNothing_AndStillRunsTheBody()
    {
        var runs = 0;
        var result = StartupTrace.Disabled.TraceSync("host.preconditions", () =>
        {
            runs++;
            return 42;
        });
        Assert.Equal(42, result);
        Assert.Equal(1, runs);
        Assert.False(StartupTrace.Disabled.Enabled);
    }

    [Fact]
    public void SinkThatThrows_CannotBreakTheCaller()
    {
        var trace = StartupTrace.Start(_ => throw new IOException("log went away"));
        trace.Measure("sandbox.spawn").End();
        trace.EmitSpan("host.ready", 0);
        Assert.True(trace.Enabled);
    }

    [Fact]
    public void EmitElapsed_DerivesAStartFromTheDuration_ForPoolThreads()
    {
        var clock = new Clock();
        var lines = new List<string>();
        var trace = StartupTrace.Start(lines.Add, "run6", clock.Read);
        clock.Advance(100);
        trace.EmitElapsed("dsh.first_output", 40, StartupTrace.Ok, new Dictionary<string, object?> { ["stream"] = "stdout" });

        var record = Parse(Assert.Single(lines));
        Assert.Equal(40d, record.GetProperty("elapsedMs").GetDouble(), 3);
        Assert.Equal(100d, record.GetProperty("sinceStartMs").GetDouble(), 3);
    }

    [Fact]
    public void SinceStartMs_NeverGoesBackwards_AcrossSpans()
    {
        var clock = new Clock();
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var trace = StartupTrace.Start(l => lines.Enqueue(l), "run7", clock.Read);
        var marks = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var span = trace.Measure("sandbox.fixed_delay");
            clock.Advance(3);
            span.End();
            marks.Add(Parse(lines.Last()).GetProperty("sinceStartMs").GetDouble());
        }
        clock.Advance(10);
        trace.EmitSpan("bridge.complete", 0);
        marks.Add(Parse(lines.Last()).GetProperty("sinceStartMs").GetDouble());
        Assert.Equal(marks.Order(), marks);
        Assert.Equal(25d, marks[^1], 3); // 5 x 3 ms of spans + the 10 ms quiet gap, counted from the origin
    }

    [Fact]
    public void ConcurrentSpans_RecordCompleteLines()
    {
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var trace = StartupTrace.Start(l => lines.Enqueue(l), "run8");
        Parallel.For(0, 64, i =>
        {
            var span = trace.Measure(i % 2 == 0 ? "dsh.first_output" : "http.first_response");
            span.End(StartupTrace.Ok, new Dictionary<string, object?> { ["attempt"] = i });
        });

        Assert.Equal(64, lines.Count);
        Assert.All(lines, line => Assert.Equal(1, Parse(line).GetProperty("schema").GetInt32()));
        Assert.Equal(32, lines.Count(l => Parse(l).GetProperty("stage").GetString() == "dsh.first_output"));
    }

    [Fact]
    public void RunId_IsRandomHex_AndCarriesNoSecret()
    {
        var a = StartupTrace.NewRunId();
        var b = StartupTrace.NewRunId();
        Assert.Equal(12, a.Length);
        Assert.NotEqual(a, b);
        Assert.All(a, ch => Assert.True(char.IsAsciiHexDigitLower(ch)));
        Assert.Equal("fixed", StartupTrace.Start(_ => { }, "fixed").RunId);
    }
}
