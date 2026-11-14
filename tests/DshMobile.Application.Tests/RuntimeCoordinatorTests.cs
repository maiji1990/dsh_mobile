using DshMobile.Application.Runtime;
using DshMobile.Application.Shadow;
using DshMobile.Runtime;
using DshMobile.Runtime.Bundle;
using DshMobile.Runtime.Journal;
using DshMobile.Runtime.Layers;

namespace DshMobile.Application.Tests;

public sealed class RuntimeCoordinatorTests : IDisposable
{
    private sealed class FakeRuntime : IRuntimeManager
    {
        public RuntimeState State = RuntimeState.Ready;
        public HarnessState Harness = HarnessState.Stopped;
        public Func<RuntimeStartResult>? OnStartSandbox;
        public Func<RuntimeStartResult>? OnStartHarness;
        public Func<RuntimeStartResult>? OnStopSandbox;
        public Func<RuntimeInstallResult>? OnInstall;
        public readonly List<string> Calls = [];

        public string Implementation => "fake";

        private RuntimeSnapshot Snap() => new(State, Harness, 0, "0.2.1-rc.5", null, null, DateTimeOffset.UnixEpoch);

        private RuntimeStartResult Ok() => new(true, "OK", null, Snap());

        public Task<RuntimeSnapshot> GetStateAsync(CancellationToken ct = default) => Task.FromResult(Snap());

        public Task<RuntimeInstallResult> InstallAsync(RuntimePackage package, IProgress<RuntimeProgress>? progress, CancellationToken ct)
        {
            Calls.Add("install");
            return Task.FromResult(OnInstall?.Invoke() ?? new RuntimeInstallResult(true, "OK", null));
        }

        public Func<RuntimeInstallResult>? OnPrepare;

        public Task<RuntimeInstallResult> PrepareAsync(CancellationToken ct)
        {
            Calls.Add("prepare");
            return Task.FromResult(OnPrepare?.Invoke() ?? new RuntimeInstallResult(true, "OK", "DSH kept at 0.2.1-rc.5"));
        }

        public Task<RuntimeStartResult> StartSandboxAsync(CancellationToken ct)
        {
            Calls.Add("sandbox.start");
            if (OnStartSandbox is not null)
            {
                return Task.FromResult(OnStartSandbox());
            }
            State = RuntimeState.Running;
            return Task.FromResult(Ok());
        }

        public Task<RuntimeStartResult> StartHarnessAsync(DshProfile profile, CancellationToken ct)
        {
            Calls.Add("dsh.start");
            if (OnStartHarness is not null)
            {
                return Task.FromResult(OnStartHarness());
            }
            Harness = HarnessState.Ready;
            return Task.FromResult(Ok());
        }

        public Task<RuntimeStartResult> StopHarnessAsync(CancellationToken ct)
        {
            Calls.Add("dsh.stop");
            Harness = HarnessState.Stopped;
            return Task.FromResult(Ok());
        }

        public Task<RuntimeStartResult> StopSandboxAsync(CancellationToken ct)
        {
            Calls.Add("sandbox.stop");
            if (OnStopSandbox is not null)
            {
                return Task.FromResult(OnStopSandbox());
            }
            State = RuntimeState.Ready;
            return Task.FromResult(Ok());
        }
    }

    private sealed class FakeForeground : IForegroundServiceController
    {
        public bool Held;
        public int Acquired;

        public void Acquire()
        {
            Held = true;
            Acquired++;
        }

        public void Release() => Held = false;
    }

    private sealed class ListLog : IDiagnosticsLog, IShadowReportSink
    {
        public readonly List<string> Lines = [];
        public readonly List<ShadowReport> Reports = [];

        public void Note(string line) => Lines.Add(line);

        public void Report(ShadowReport report) => Reports.Add(report);
    }

    private readonly string _dir = Directory.CreateTempSubdirectory("dshm-coord-").FullName;
    private readonly FakeRuntime _runtime = new();
    private readonly FakeForeground _fg = new();
    private readonly ListLog _log = new();
    private readonly InMemoryRuntimeJournal _journal = new();
    private RuntimeCoordinator? _coordinator;

    public void Dispose()
    {
        _coordinator?.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private RuntimeCoordinator Coordinator(SliceMode mode = SliceMode.LegacyWithShadow) =>
        _coordinator ??= new RuntimeCoordinator(_runtime, new RuntimeStateMachine(_journal, "fake"), _fg, _log, _log, new MigrationOptions { BundleImport = mode, RuntimeHost = SliceMode.Legacy });

    private RuntimeStartResult SandboxEndedInError()
    {
        // Legacy behavior: sandbox.start answers ok=true while the sandbox itself went to ERROR.
        _runtime.State = RuntimeState.Failed;
        return new RuntimeStartResult(true, "OK", null, new RuntimeSnapshot(RuntimeState.Failed, HarnessState.Stopped, 0, null, null, null, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public async Task Start_HappyPath_HoldsServiceAndReachesReadyHarness()
    {
        var r = await Coordinator().StartAsync(DshProfile.Web, CancellationToken.None);
        Assert.True(r.Ok);
        Assert.True(_fg.Held);
        Assert.Equal(RuntimeState.Running, r.Snapshot.State);
        Assert.Equal(HarnessState.Ready, r.Snapshot.Harness);
        Assert.Equal(1, r.Snapshot.Generation);
        Assert.Equal(["sandbox.start", "dsh.start"], _runtime.Calls);
    }

    [Fact]
    public async Task Start_SandboxEndsInError_CleansUpAndReleasesService()
    {
        _runtime.OnStartSandbox = SandboxEndedInError;
        var r = await Coordinator().StartAsync(DshProfile.Web, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.False(_fg.Held);
        Assert.Equal(RuntimeState.Failed, r.Snapshot.State);
        Assert.Equal(["sandbox.start", "sandbox.stop"], _runtime.Calls);
        Assert.DoesNotContain("dsh.start", _runtime.Calls);
    }

    [Fact]
    public async Task Start_SandboxFailsAndCleanupFails_KeepsService()
    {
        _runtime.OnStartSandbox = () => new RuntimeStartResult(false, "RUNTIME_ERROR", "proot died",
            new RuntimeSnapshot(RuntimeState.Failed, HarnessState.Stopped, 0, null, "PROOT_EXIT", "proot died", DateTimeOffset.UnixEpoch), "PROOT_EXIT");
        _runtime.OnStopSandbox = () => new RuntimeStartResult(false, "DEADLINE_EXCEEDED", "timeout",
            new RuntimeSnapshot(RuntimeState.Failed, HarnessState.Stopped, 0, null, null, null, DateTimeOffset.UnixEpoch));
        var r = await Coordinator().StartAsync(DshProfile.Web, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.True(_fg.Held);
        Assert.Equal("PROOT_EXIT", r.Snapshot.ErrorCode);
    }

    [Fact]
    public async Task Start_HarnessFails_KeepsSandboxAndService()
    {
        _runtime.OnStartHarness = () => new RuntimeStartResult(false, "RUNTIME_ERROR", "DSH_NOT_READY",
            new RuntimeSnapshot(RuntimeState.Failed, HarnessState.Stopped, 0, null, null, null, DateTimeOffset.UnixEpoch), "DSH_NOT_READY");
        var r = await Coordinator().StartAsync(DshProfile.Web, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.True(_fg.Held);
        Assert.Equal(RuntimeState.Running, r.Snapshot.State);
        Assert.Equal(HarnessState.Failed, r.Snapshot.Harness);
    }

    [Fact]
    public async Task Start_WhenSandboxAlreadyRunning_OnlyStartsHarness()
    {
        _runtime.State = RuntimeState.Running;
        var r = await Coordinator().StartAsync(DshProfile.Web, CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal(["dsh.start"], _runtime.Calls);
        Assert.True(_fg.Held);
    }

    [Fact]
    public async Task Start_WhenAlreadyReady_DoesNothing()
    {
        _runtime.State = RuntimeState.Running;
        _runtime.Harness = HarnessState.Ready;
        var r = await Coordinator().StartAsync(DshProfile.Web, CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Empty(_runtime.Calls);
    }

    [Fact]
    public async Task Start_WithoutRuntime_IsRefused()
    {
        _runtime.State = RuntimeState.NotInstalled;
        var r = await Coordinator().StartAsync(DshProfile.Web, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(RuntimeErrorCodes.InvalidTransition, r.Code);
        Assert.Empty(_runtime.Calls);
        Assert.False(_fg.Held);
    }

    [Fact]
    public async Task Stop_HappyPath_ReleasesService()
    {
        await Coordinator().StartAsync(DshProfile.Web, CancellationToken.None);
        var r = await Coordinator().StopAsync(CancellationToken.None);
        Assert.True(r.Ok);
        Assert.False(_fg.Held);
        Assert.Equal(RuntimeState.Ready, r.Snapshot.State);
        Assert.Equal(["sandbox.start", "dsh.start", "dsh.stop", "sandbox.stop"], _runtime.Calls);
    }

    [Fact]
    public async Task Stop_And_FailedStartCleanup_RaiseSandboxStoppingBeforeTheSandboxStops()
    {
        var coordinator = Coordinator();
        var seen = new List<string>();
        coordinator.SandboxStopping += () => seen.Add("stopping after " + string.Join(",", _runtime.Calls));
        await coordinator.StartAsync(DshProfile.Web, CancellationToken.None);
        await coordinator.StopAsync(CancellationToken.None);
        Assert.Equal(["stopping after sandbox.start,dsh.start,dsh.stop"], seen);

        // A failing listener never keeps the sandbox up.
        _runtime.Calls.Clear();
        seen.Clear();
        coordinator.SandboxStopping += () => throw new InvalidOperationException("listener");
        _runtime.OnStartSandbox = SandboxEndedInError;
        await coordinator.StartAsync(DshProfile.Web, CancellationToken.None);
        Assert.Equal(["stopping after sandbox.start"], seen);
        Assert.Equal(["sandbox.start", "sandbox.stop"], _runtime.Calls);

        // Nothing running: nothing is stopped, nothing raised.
        _runtime.Calls.Clear();
        seen.Clear();
        await coordinator.StopAsync(CancellationToken.None);
        Assert.Empty(seen);
    }

    [Fact]
    public async Task Stop_Fails_KeepsServiceSoProcessesAreNotOrphaned()
    {
        await Coordinator().StartAsync(DshProfile.Web, CancellationToken.None);
        _runtime.OnStopSandbox = () => new RuntimeStartResult(false, "DEADLINE_EXCEEDED", "sandbox.stop exceeded",
            new RuntimeSnapshot(RuntimeState.Failed, HarnessState.Stopped, 0, null, null, null, DateTimeOffset.UnixEpoch));
        var r = await Coordinator().StopAsync(CancellationToken.None);
        Assert.False(r.Ok);
        Assert.True(_fg.Held);
        Assert.Equal(RuntimeState.Failed, r.Snapshot.State);
        Assert.Equal("DEADLINE_EXCEEDED", r.Snapshot.ErrorCode);
    }

    [Fact]
    public async Task EveryCommandedTransition_IsJournaled()
    {
        await Coordinator().StartAsync(DshProfile.Web, CancellationToken.None);
        await Coordinator().StopAsync(CancellationToken.None);
        var reasons = _journal.ReadTail(100).Select(e => e.Reason).ToList();
        Assert.Contains("start requested", reasons);
        Assert.Contains("harness started", reasons);
        Assert.Contains("stopped", reasons);
    }

    [Fact]
    public async Task Install_WithShadow_ReportsDivergenceButLegacyResultStands()
    {
        // Legacy accepts a file the C# inspector rejects (not a zip): must be reported as Diverge, result unchanged.
        var path = Path.Combine(_dir, "bundle.zip");
        await File.WriteAllTextAsync(path, "not a zip");
        var r = await Coordinator().InstallAsync(new RuntimePackage(path), null, CancellationToken.None);
        Assert.True(r.Ok);
        var report = Assert.Single(_log.Reports);
        Assert.Equal(ShadowVerdict.Diverge, report.Verdict);
        Assert.Equal(BundleErrorCodes.NotAZip, report.CSharpCode);
        Assert.Equal(RuntimeState.Ready, Coordinator().State.Current.State);
    }

    [Fact]
    public async Task Install_LegacyOnly_RunsNoShadow()
    {
        var path = Path.Combine(_dir, "bundle.zip");
        await File.WriteAllTextAsync(path, "not a zip");
        await Coordinator(SliceMode.Legacy).InstallAsync(new RuntimePackage(path), null, CancellationToken.None);
        Assert.Empty(_log.Reports);
    }

    [Fact]
    public async Task Install_LegacyFailure_IsFailedWithUpstreamCode()
    {
        _runtime.OnInstall = () => new RuntimeInstallResult(false, "RUNTIME_ERROR", "sha", "BUNDLE_SHA256_MISMATCH");
        var path = Path.Combine(_dir, "bundle.zip");
        await File.WriteAllTextAsync(path, "not a zip");
        var r = await Coordinator().InstallAsync(new RuntimePackage(path), null, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(RuntimeState.Failed, Coordinator().State.Current.State);
        Assert.Equal("BUNDLE_SHA256_MISMATCH", Coordinator().State.Current.ErrorCode);
    }

    private static string FixtureBundle(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }
        return Path.Combine(dir!.FullName, "tests", "TestVectors", "bundles", name);
    }

    private RuntimeCoordinator CSharpCoordinator(DshMobile.Runtime.RuntimeLayout layout) =>
        _coordinator ??= new RuntimeCoordinator(_runtime, new RuntimeStateMachine(_journal, "fake"), _fg, _log, _log,
            new MigrationOptions { HostPathExists = LayerExtractor.AndroidLikeHost }, null, layout);

    [Fact]
    public async Task Install_CSharpMode_InstallsVerifiesAndPreparesWithoutLegacyImport()
    {
        var layout = new DshMobile.Runtime.RuntimeLayout(Path.Combine(_dir, "files"), null, "/lib");
        var r = await CSharpCoordinator(layout).InstallAsync(new RuntimePackage(FixtureBundle("good.zip")), null, CancellationToken.None, SliceMode.CSharp);
        Assert.True(r.Ok, r.Message);
        Assert.DoesNotContain("install", _runtime.Calls); // the legacy importer never ran
        Assert.Contains("prepare", _runtime.Calls);
        Assert.True(File.Exists(Path.Combine(layout.LayerDir("base"), "etc", "os-release")));
        var verify = Assert.Single(_log.Reports);
        Assert.Equal("runtime.layer-install", verify.Slice);
        Assert.Equal(ShadowVerdict.Agree, verify.Verdict);
        Assert.Equal(RuntimeState.Ready, _coordinator!.State.Current.State);
    }

    [Fact]
    public async Task Install_CSharpMode_CorruptBundle_IsFailedAndLegacyNeverCalled()
    {
        var layout = new DshMobile.Runtime.RuntimeLayout(Path.Combine(_dir, "files"), null, "/lib");
        var r = await CSharpCoordinator(layout).InstallAsync(new RuntimePackage(FixtureBundle("corrupt-node.zip")), null, CancellationToken.None, SliceMode.CSharp);
        Assert.False(r.Ok);
        Assert.Equal(BundleErrorCodes.Sha256Mismatch, r.UpstreamCode);
        Assert.Empty(_runtime.Calls);
        Assert.Equal(RuntimeState.Failed, _coordinator!.State.Current.State);
    }

    [Fact]
    public async Task Install_CSharpMode_PrepareFailure_IsNotReportedAsSuccess()
    {
        _runtime.OnPrepare = () => new RuntimeInstallResult(false, "RUNTIME_ERROR", "DSH provision failed");
        var layout = new DshMobile.Runtime.RuntimeLayout(Path.Combine(_dir, "files"), null, "/lib");
        var r = await CSharpCoordinator(layout).InstallAsync(new RuntimePackage(FixtureBundle("good.zip")), null, CancellationToken.None, SliceMode.CSharp);
        Assert.False(r.Ok);
        Assert.Equal("RUNTIME_IMPORT_UNVERIFIED", r.Code);
        Assert.Equal(RuntimeState.Failed, _coordinator!.State.Current.State);
    }

    [Fact]
    public async Task Start_CSharpHost_FailingPreconditions_IsFailedAndReleasesService()
    {
        var layout = new DshMobile.Runtime.RuntimeLayout(Path.Combine(_dir, "files"), null, Path.Combine(_dir, "nolibs"));
        var r = await CSharpCoordinator(layout).StartAsync(DshProfile.Web, CancellationToken.None, SliceMode.CSharp);
        Assert.False(r.Ok);
        Assert.Equal("EXECUTABLE_NOT_ALLOWED", r.Code);
        Assert.Empty(_runtime.Calls); // the legacy runtime is never asked to start anything
        Assert.False(_fg.Held);
        Assert.Equal(RuntimeState.Failed, r.Snapshot.State);
        Assert.Contains(_journal.ReadTail(20), e => e.Reason == "start requested (csharp host)");
    }

    [Fact]
    public async Task Start_CSharpHost_WhileLegacyRunning_IsRefused()
    {
        _runtime.State = RuntimeState.Running;
        var layout = new DshMobile.Runtime.RuntimeLayout(Path.Combine(_dir, "files"), null, Path.Combine(_dir, "nolibs"));
        var r = await CSharpCoordinator(layout).StartAsync(DshProfile.Web, CancellationToken.None, SliceMode.CSharp);
        Assert.False(r.Ok);
        Assert.Equal(RuntimeErrorCodes.InvalidTransition, r.Code);
        Assert.Empty(_runtime.Calls);
    }

    // ------------------------------------------------------------------ ADR-0015: C# host as the default

    private sealed class FakeHost : DshMobile.Runtime.Hosting.IDshHost
    {
        public DshMobile.Runtime.Hosting.DshHostState State { get; private set; }
        public bool? UiToolsAtStart { get; private set; }
        private bool _kept;
        private bool _orphans;

        // Same rule as DshHost: alive between start and stop, after the health loop gave up, or after a stop with orphans.
        public bool OwnsProcesses => State is DshMobile.Runtime.Hosting.DshHostState.Starting or DshMobile.Runtime.Hosting.DshHostState.Ready
            or DshMobile.Runtime.Hosting.DshHostState.Stopping || _kept || _orphans;

        public Func<DshMobile.Runtime.Hosting.DshHostResult>? OnStart;
        public Func<DshMobile.Runtime.Hosting.DshHostResult>? OnStop;
        public readonly List<string> Calls;

        public FakeHost(List<string> calls) => Calls = calls;

        public event EventHandler? StateChanged;

        public Task<DshMobile.Runtime.Hosting.DshHostResult> StartAsync(CancellationToken ct)
        {
            Calls.Add("host.start");
            var r = OnStart?.Invoke() ?? new DshMobile.Runtime.Hosting.DshHostResult(true, "OK", null);
            UiToolsAtStart = r.Ok ? true : null;
            Set(r.Ok ? DshMobile.Runtime.Hosting.DshHostState.Ready : DshMobile.Runtime.Hosting.DshHostState.Failed);
            return Task.FromResult(r);
        }

        public Task<DshMobile.Runtime.Hosting.DshHostResult> StopAsync(CancellationToken ct)
        {
            Calls.Add("host.stop");
            var r = OnStop?.Invoke() ?? new DshMobile.Runtime.Hosting.DshHostResult(true, "OK", null);
            _kept = false;
            _orphans = !r.Ok;
            UiToolsAtStart = null;
            Set(DshMobile.Runtime.Hosting.DshHostState.Stopped);
            return Task.FromResult(r);
        }

        /// <summary>What the health loop does when it gives up: DSH stopped, sandbox kept.</summary>
        public void GiveUp()
        {
            _kept = true;
            Set(DshMobile.Runtime.Hosting.DshHostState.Failed);
        }

        private void Set(DshMobile.Runtime.Hosting.DshHostState s)
        {
            State = s;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private FakeHost? _host;

    /// <summary>Default options (C# host is the default start path) with the fake host behind the factory.</summary>
    private RuntimeCoordinator HostCoordinator()
    {
        _host ??= new FakeHost(_runtime.Calls);
        var layout = new DshMobile.Runtime.RuntimeLayout(Path.Combine(_dir, "files"), null, Path.Combine(_dir, "nolibs"));
        return _coordinator ??= new RuntimeCoordinator(_runtime, new RuntimeStateMachine(_journal, "fake"), _fg, _log, _log,
            new MigrationOptions(), null, layout, hostFactory: (_, _, _) => _host);
    }

    [Fact]
    public void RuntimeHost_DefaultsToCSharp() => Assert.Equal(SliceMode.CSharp, new MigrationOptions().RuntimeHost);

    [Fact]
    public async Task Start_Default_UsesTheCSharpHostOnly()
    {
        var c = HostCoordinator();
        var r = await c.StartAsync(DshProfile.Web, CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal(["host.start"], _runtime.Calls); // the legacy runtime starts nothing
        Assert.True(_fg.Held);
        Assert.Equal((RuntimeState.Running, HarnessState.Ready), (r.Snapshot.State, r.Snapshot.Harness));
        Assert.True(c.HostOwnsProcesses);
        Assert.True(c.HostUiToolsAtStart);
        Assert.Contains(_journal.ReadTail(20), e => e.Reason == "start requested (csharp host)");

        // A second start is a no-op, not a second host.
        var again = await c.StartAsync(DshProfile.Web, CancellationToken.None);
        Assert.True(again.Ok);
        Assert.Equal(["host.start"], _runtime.Calls);
    }

    [Fact]
    public async Task Start_ExplicitLegacy_IsTheFallbackPath()
    {
        var r = await HostCoordinator().StartAsync(DshProfile.Web, CancellationToken.None, SliceMode.Legacy);
        Assert.True(r.Ok);
        Assert.Equal(["sandbox.start", "dsh.start"], _runtime.Calls);
        Assert.False(_coordinator!.HostOwnsProcesses);
    }

    [Fact]
    public async Task Start_Default_WithoutLayout_IsRefusedAndNeverFallsBackToLegacy()
    {
        using var c = new RuntimeCoordinator(_runtime, new RuntimeStateMachine(_journal, "fake"), _fg, _log, _log, new MigrationOptions());
        var r = await c.StartAsync(DshProfile.Web, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(RuntimeErrorCodes.InvalidTransition, r.Code);
        Assert.Empty(_runtime.Calls);
        Assert.False(_fg.Held);
    }

    [Fact]
    public async Task Stop_CSharpHost_RunsCompanionCleanupBeforeSandboxStoppingAndTheHostStop()
    {
        var c = HostCoordinator();
        c.HostStopping = _ => { _runtime.Calls.Add("companions"); return Task.CompletedTask; };
        c.SandboxStopping += () => _runtime.Calls.Add("sandbox-stopping");
        await c.StartAsync(DshProfile.Web, CancellationToken.None);
        var r = await c.StopAsync(CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal(["host.start", "companions", "sandbox-stopping", "host.stop"], _runtime.Calls);
        Assert.False(_fg.Held);
        Assert.Equal(RuntimeState.Ready, r.Snapshot.State);
        Assert.False(c.HostOwnsProcesses);
        Assert.Null(c.HostUiToolsAtStart);
    }

    [Fact]
    public async Task Stop_CSharpHost_FailingCompanionCleanup_DoesNotBlockTheStop()
    {
        var c = HostCoordinator();
        c.HostStopping = _ => throw new InvalidOperationException("bridge gone");
        await c.StartAsync(DshProfile.Web, CancellationToken.None);
        var r = await c.StopAsync(CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Contains("host.stop", _runtime.Calls);
        Assert.Contains(_log.Lines, l => l == "host stopping hook failed: InvalidOperationException: bridge gone");
        Assert.False(_fg.Held);
    }

    [Fact]
    public async Task Stop_CSharpHost_WithOrphans_KeepsTheServiceAndOwnership()
    {
        var c = HostCoordinator();
        await c.StartAsync(DshProfile.Web, CancellationToken.None);
        _host!.OnStop = () => new DshMobile.Runtime.Hosting.DshHostResult(false, DshMobile.Runtime.Hosting.DshHost.Orphans, "1 left");
        var r = await c.StopAsync(CancellationToken.None);
        Assert.False(r.Ok);
        Assert.True(_fg.Held);
        Assert.True(c.HostOwnsProcesses);
        Assert.Equal(RuntimeState.Failed, c.State.Current.State);

        // The retry goes to the host again (not to the legacy "nothing running"), and only a clean stop releases.
        _host.OnStop = null;
        var retry = await c.StopAsync(CancellationToken.None);
        Assert.True(retry.Ok);
        Assert.Equal(["host.start", "host.stop", "host.stop"], _runtime.Calls);
        Assert.False(_fg.Held);
        Assert.False(c.HostOwnsProcesses);
    }

    [Fact]
    public async Task HealthLoopGivingUp_IsJournaled_AndTheHostKeepsOwnership()
    {
        var c = HostCoordinator();
        await c.StartAsync(DshProfile.Web, CancellationToken.None);
        _host!.GiveUp();
        Assert.Equal((RuntimeState.Running, HarnessState.Failed), (c.State.Current.State, c.State.Current.Harness));
        Assert.Contains(_journal.ReadTail(20), e => e.Reason == $"csharp host: dsh unhealthy ({RuntimeCoordinator.DshUnhealthy})");
        Assert.True(c.HostOwnsProcesses);

        // Legacy reports "stopped", but it must neither overwrite the journal nor start a second tree.
        c.Observe(new RuntimeSnapshot(RuntimeState.Ready, HarnessState.Stopped, 0, null, null, null, DateTimeOffset.UnixEpoch));
        var legacy = await c.StartAsync(DshProfile.Web, CancellationToken.None, SliceMode.Legacy);
        Assert.False(legacy.Ok);
        Assert.Equal(RuntimeErrorCodes.InvalidTransition, legacy.Code);
        Assert.Equal(RuntimeState.Running, c.State.Current.State);
        Assert.DoesNotContain("sandbox.start", _runtime.Calls);
    }

    [Fact]
    public async Task Start_AfterTheHealthLoopGaveUp_RestartsTheWholeHost()
    {
        var c = HostCoordinator();
        var companions = 0;
        c.HostStopping = _ => { companions++; return Task.CompletedTask; };
        await c.StartAsync(DshProfile.Web, CancellationToken.None);
        _host!.GiveUp();
        var r = await c.StartAsync(DshProfile.Web, CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal(["host.start", "host.stop", "host.start"], _runtime.Calls);
        Assert.Equal(1, companions);
        Assert.Equal((RuntimeState.Running, HarnessState.Ready), (r.Snapshot.State, r.Snapshot.Harness));
        Assert.True(_fg.Held);
    }

    [Fact]
    public async Task Start_CSharpHostFails_CleansUpCompanionsAndReleasesTheService()
    {
        var c = HostCoordinator();
        var seen = new List<string>();
        c.HostStopping = _ => { seen.Add("companions"); return Task.CompletedTask; };
        c.SandboxStopping += () => seen.Add("sandbox-stopping");
        _host!.OnStart = () => new DshMobile.Runtime.Hosting.DshHostResult(false, DshMobile.Runtime.Hosting.DshHost.NotReady, "not ready");
        var r = await c.StartAsync(DshProfile.Web, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(DshMobile.Runtime.Hosting.DshHost.NotReady, r.Code);
        Assert.Equal(["companions", "sandbox-stopping"], seen);
        Assert.False(_fg.Held);
        Assert.Equal(RuntimeState.Failed, r.Snapshot.State);
        Assert.Equal(["host.start"], _runtime.Calls);

        // Nothing is left, so the legacy fallback is allowed right after.
        Assert.False(c.HostOwnsProcesses);
        var legacy = await c.StartAsync(DshProfile.Web, CancellationToken.None, SliceMode.Legacy);
        Assert.True(legacy.Ok);

        // Regression (emulator run 2026-11-14): the host is still in Failed from its failed start but owns nothing; a
        // default start now must be refused because the legacy runtime runs - not "restart the failed host", which
        // stopped the companions and journaled Ready over the running legacy runtime.
        seen.Clear();
        _runtime.Calls.Clear();
        _host.OnStart = null;
        var again = await c.StartAsync(DshProfile.Web, CancellationToken.None);
        Assert.False(again.Ok);
        Assert.Equal(RuntimeErrorCodes.InvalidTransition, again.Code);
        Assert.Empty(seen);
        Assert.Empty(_runtime.Calls);
        Assert.Equal(RuntimeState.Running, c.State.Current.State);
    }

    [Fact]
    public async Task Install_WhileRunning_IsRefusedWithoutCallingLegacy()
    {
        _runtime.State = RuntimeState.Running;
        var r = await Coordinator().InstallAsync(new RuntimePackage(Path.Combine(_dir, "x.zip")), null, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(RuntimeErrorCodes.InvalidTransition, r.Code);
        Assert.Empty(_runtime.Calls);
    }

    [Fact]
    public async Task CSharpPluginChange_WithoutLayout_IsRefused()
    {
        var r = await Coordinator().ChangePluginWithCSharpAsync("add", "web", "foo", CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(RuntimeErrorCodes.NotInstalled, r.Code);
    }

    [Theory]
    [InlineData("list", "web", "foo")]
    [InlineData("add", "desktop", "foo")]
    [InlineData("add", "web", "pkg; rm -rf /")]
    [InlineData("remove", "web", "foo@1.0.0")]
    public async Task CSharpPluginChange_BadArguments_AreBadRequestAndRunNothing(string verb, string profile, string operand)
    {
        // nativeLibraryDir has no libproot.so: reaching the launcher would fail differently, so BAD_REQUEST proves nothing ran.
        var layout = new DshMobile.Runtime.RuntimeLayout(Path.Combine(_dir, "files"), null, Path.Combine(_dir, "no-lib"));
        var r = await CSharpCoordinator(layout).ChangePluginWithCSharpAsync(verb, profile, operand, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal("BAD_REQUEST", r.Code);
        Assert.Empty(r.Lines);
        Assert.Empty(_runtime.Calls);
    }

    [Theory]
    [InlineData("foo", "foo")]
    [InlineData("foo@^1.0.0", "foo")]
    [InlineData("@scope/foo", "@scope/foo")]
    [InlineData("@scope/foo@1.2.3", "@scope/foo")]
    public void SpecPackageName_StripsOnlyTheVersion(string spec, string name) =>
        Assert.Equal(name, RuntimeCoordinator.SpecPackageName(spec));
}
