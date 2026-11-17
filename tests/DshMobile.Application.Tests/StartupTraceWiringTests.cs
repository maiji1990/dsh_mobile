using System.Text.Json;
using DshMobile.Application.Runtime;
using DshMobile.Application.Shadow;
using DshMobile.Core.Diagnostics;
using DshMobile.Runtime;
using DshMobile.Runtime.Hosting;
using DshMobile.Runtime.Journal;
using DshMobile.Runtime.Layers;

namespace DshMobile.Application.Tests;

/// <summary>
/// The startup trace is wired through the orchestration without changing it: a start still succeeds with a disabled
/// trace, and an enabled trace records the coordinator / guest-asset stages, one parseable line each
/// (docs/STARTUP_PERF.md).
/// </summary>
public sealed class StartupTraceWiringTests : IDisposable
{
    private sealed class Foreground : IForegroundServiceController
    {
        public void Acquire()
        {
        }

        public void Release()
        {
        }
    }

    private sealed class Log : IDiagnosticsLog, IShadowReportSink
    {
        public void Note(string line)
        {
        }

        public void Report(ShadowReport report)
        {
        }
    }

    private readonly string _dir = Directory.CreateTempSubdirectory("dshm-trace-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>An installed runtime plus the fake host; the caller owns the coordinator.</summary>
    private RuntimeCoordinator Coordinator()
    {
        var layout = new RuntimeLayout(Path.Combine(_dir, "files"), null, Path.Combine(_dir, "nolibs"));
        TestRuntime.Install(layout.FilesDir);
        var host = new FakeDshHost([]);
        return new RuntimeCoordinator(
            layout,
            new RuntimeStateMachine(new InMemoryRuntimeJournal(), RuntimeCoordinator.Implementation),
            new Foreground(),
            new Log(),
            new Log(),
            new RuntimeOptions { HostPathExists = LayerExtractor.AndroidLikeHost },
            hostFactory: (_, _, _) => host);
    }

    private static JsonElement Parse(string line) => JsonElement.Parse(line[(StartupTrace.LogPrefix.Length + 1)..]);

    private static StartupTrace Trace(List<string> lines) =>
        StartupTrace.Start(line => { lock (lines) { lines.Add(line); } }, "run-start");

    [Fact]
    public async Task Start_WithATrace_RecordsOneLinePerStage_AndKeepsTheOutcome()
    {
        var lines = new List<string>();
        using var coordinator = Coordinator();
        var result = await coordinator.StartAsync(DshProfile.Web, CancellationToken.None, Trace(lines));

        Assert.True(result.Ok);
        Assert.NotEmpty(lines);
        var stages = lines.Select(l => Parse(l).GetProperty("stage").GetString()).ToList();
        Assert.Contains("coordinator.lock_wait", stages);
        Assert.Contains("coordinator.sync", stages);
        Assert.Contains("coordinator.journal", stages);
        Assert.Contains("guest_assets.total", stages);
        Assert.Contains("guest_assets.shim", stages);
        Assert.Contains("guest_assets.wrappers", stages);
        Assert.All(lines, line =>
        {
            var record = Parse(line);
            Assert.Equal(1, record.GetProperty("schema").GetInt32());
            Assert.Equal("run-start", record.GetProperty("runId").GetString());
            Assert.StartsWith(StartupTrace.LogPrefix + " {", line, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task RepeatedStart_StillSucceeds_AndKeepsTracing()
    {
        var lines = new List<string>();
        using var coordinator = Coordinator();
        var trace = Trace(lines);
        Assert.True((await coordinator.StartAsync(DshProfile.Web, CancellationToken.None, trace)).Ok);
        // A second start while the fake host is Ready is the coordinator's "already running" path: still Ok, still traced.
        Assert.True((await coordinator.StartAsync(DshProfile.Web, CancellationToken.None, trace)).Ok);
        var records = lines.Select(Parse).ToList();
        Assert.Equal(2, records.Count(r => r.GetProperty("stage").GetString() == "coordinator.lock_wait"));
        Assert.Equal(1, records.Count(r => r.GetProperty("stage").GetString() == "guest_assets.total"));
    }

    [Fact]
    public async Task Start_WithoutATrace_RecordsNothing_AndStillSucceeds()
    {
        var lines = new List<string>();
        using var coordinator = Coordinator();
        var result = await coordinator.StartAsync(DshProfile.Web, CancellationToken.None, StartupTrace.Disabled);

        Assert.True(result.Ok);
        Assert.Empty(lines);
    }
}
