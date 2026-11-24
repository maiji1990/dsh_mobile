using DshMobile.Application.Runtime;
using DshMobile.Application.Shadow;
using DshMobile.Core.Diagnostics;
using DshMobile.Runtime;
using DshMobile.Runtime.Hosting;
using DshMobile.Runtime.Journal;
using DshMobile.Runtime.Layers;

namespace DshMobile.Application.Tests;

/// <summary>
/// PR-01 (N-01-03): verifies control-plane (<c>_op</c>) and data-plane isolation, plus ordered
/// <see cref="RuntimeCoordinator.AddHostStoppingHook"/> draining before sandbox / host teardown.
/// </summary>
public sealed class RuntimeCoordinatorIsolationTests : IDisposable
{
    private sealed class DummyForeground : IForegroundServiceController
    {
        public void Acquire()
        {
        }

        public void Release()
        {
        }
    }

    private sealed class RecordingLog : IDiagnosticsLog, IShadowReportSink
    {
        public readonly List<string> Lines = [];

        public void Note(string line)
        {
            lock (Lines)
            {
                Lines.Add(line);
            }
        }

        public void Report(ShadowReport report)
        {
        }
    }

    private sealed class BlockingStartHost(List<string> calls, TaskCompletionSource startEntered, TaskCompletionSource releaseStart) : IDshHost
    {
        public DshHostState State { get; private set; } = DshHostState.Stopped;
        public bool? UiToolsAtStart => null;
        public bool OwnsProcesses => State is DshHostState.Starting or DshHostState.Ready or DshHostState.Stopping;
        public event EventHandler? StateChanged;

        public async Task<DshHostResult> StartAsync(CancellationToken ct, StartupTrace? trace = null)
        {
            calls.Add("blocking.start.enter");
            State = DshHostState.Starting;
            StateChanged?.Invoke(this, EventArgs.Empty);
            startEntered.TrySetResult();
            await releaseStart.Task.WaitAsync(ct);
            State = DshHostState.Ready;
            StateChanged?.Invoke(this, EventArgs.Empty);
            calls.Add("blocking.start.exit");
            return new DshHostResult(true, "OK", null);
        }

        public Task<DshHostResult> StopAsync(CancellationToken ct)
        {
            calls.Add("blocking.stop");
            State = DshHostState.Stopped;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(new DshHostResult(true, "OK", null));
        }

        public Task<DshHealthProbeResult> ProbeHealthAsync(CancellationToken ct) =>
            Task.FromResult(new DshHealthProbeResult(true, "OK", "ready"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private readonly string _dir = Directory.CreateTempSubdirectory("dshm-coord-iso-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private RuntimeCoordinator CreateCoordinator(IDshHost host, RecordingLog? log = null)
    {
        var layout = new RuntimeLayout(Path.Combine(_dir, "files"), null, Path.Combine(_dir, "nolibs"));
        TestRuntime.Install(layout.FilesDir);
        var logger = log ?? new RecordingLog();
        return new RuntimeCoordinator(
            layout,
            new RuntimeStateMachine(new InMemoryRuntimeJournal(), RuntimeCoordinator.Implementation),
            new DummyForeground(),
            logger,
            logger,
            new RuntimeOptions { HostPathExists = LayerExtractor.AndroidLikeHost },
            hostFactory: (_, _, _) => host);
    }

    [Fact]
    public async Task Stop_RegisteredDataPlaneHooks_RunInLifoOrderAndHonorDispose()
    {
        var calls = new List<string>();
        var host = new FakeDshHost(calls);
        using var coordinator = CreateCoordinator(host);

        coordinator.HostStopping = _ => { calls.Add("legacy.hook"); return Task.CompletedTask; };
        using var h1 = coordinator.AddHostStoppingHook(_ => { calls.Add("dp.hook.1"); return Task.CompletedTask; });
        var h2 = coordinator.AddHostStoppingHook(_ => { calls.Add("dp.hook.2"); return Task.CompletedTask; });
        using var h3 = coordinator.AddHostStoppingHook(_ => { calls.Add("dp.hook.3"); return Task.CompletedTask; });
        h2.Dispose();
        h2.Dispose(); // idempotent disposal
        coordinator.SandboxStopping += () => calls.Add("sandbox.stopping");

        Assert.True((await coordinator.StartAsync(DshProfile.Web, CancellationToken.None)).Ok);
        var stop = await coordinator.StopAsync(CancellationToken.None);

        Assert.True(stop.Ok);
        Assert.Equal(["host.start", "legacy.hook", "dp.hook.3", "dp.hook.1", "sandbox.stopping", "host.stop"], calls);
    }

    [Fact]
    public async Task Stop_WithBlockedDataPlaneOperation_DrainsWithoutDeadlockingControlPlane()
    {
        var calls = new List<string>();
        var host = new FakeDshHost(calls);
        using var coordinator = CreateCoordinator(host);

        using var inflightCts = new CancellationTokenSource();
        var dataPlaneWaiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiterStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drainCompleted = false;

        using var hook = coordinator.AddHostStoppingHook(async stopCt =>
        {
            calls.Add("dp.drain.start");
            inflightCts.Cancel();
            await dataPlaneWaiter.Task.WaitAsync(TimeSpan.FromSeconds(5), stopCt);
            drainCompleted = true;
            calls.Add("dp.drain.done");
        });

        Assert.True((await coordinator.StartAsync(DshProfile.Web, CancellationToken.None)).Ok);

        // Simulate an active data-plane call suspended independently of RuntimeCoordinator._op.
        var inflightTask = Task.Run(async () =>
        {
            waiterStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, inflightCts.Token);
            }
            catch (OperationCanceledException)
            {
                dataPlaneWaiter.TrySetResult();
            }
        });

        await waiterStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopResult = await coordinator.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await inflightTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(stopResult.Ok);
        Assert.True(drainCompleted);
        Assert.Equal(["host.start", "dp.drain.start", "dp.drain.done", "host.stop"], calls);
    }

    [Fact]
    public async Task ReadFactsAndState_DoNotBlockWhileControlPlaneLockIsHeldByStart()
    {
        var calls = new List<string>();
        var startEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new BlockingStartHost(calls, startEntered, releaseStart);
        using var coordinator = CreateCoordinator(host);

        var startTask = coordinator.StartAsync(DshProfile.Web, CancellationToken.None);
        await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Data-plane / telemetry observers must stay lock-free relative to _op.
        var facts = await coordinator.ReadFactsAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(facts.RuntimeInstalled);
        Assert.Equal(DshHostState.Starting, coordinator.HostState);

        releaseStart.TrySetResult();
        var startResult = await startTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(startResult.Ok);
        Assert.Equal(DshHostState.Ready, coordinator.HostState);
    }

    [Fact]
    public async Task Stop_WhenRegisteredDataPlaneHookThrows_LogsAndStillStopsHost()
    {
        var calls = new List<string>();
        var log = new RecordingLog();
        var host = new FakeDshHost(calls);
        using var coordinator = CreateCoordinator(host, log);

        using var throwing = coordinator.AddHostStoppingHook(_ => throw new InvalidOperationException("drain boom"));
        using var healthy = coordinator.AddHostStoppingHook(_ => { calls.Add("dp.healthy"); return Task.CompletedTask; });

        Assert.True((await coordinator.StartAsync(DshProfile.Web, CancellationToken.None)).Ok);
        var result = await coordinator.StopAsync(CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(["host.start", "dp.healthy", "host.stop"], calls);
        Assert.Contains(log.Lines, l => l.Contains("InvalidOperationException: drain boom", StringComparison.Ordinal));
    }
}
