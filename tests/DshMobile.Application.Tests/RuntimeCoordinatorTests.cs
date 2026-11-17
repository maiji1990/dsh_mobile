using DshMobile.Application.Runtime;
using DshMobile.Application.Shadow;
using DshMobile.Runtime;
using DshMobile.Runtime.Bundle;
using DshMobile.Runtime.Hosting;
using DshMobile.Runtime.Journal;
using DshMobile.Runtime.Layers;

namespace DshMobile.Application.Tests;

/// <summary>
/// The C#-only coordinator (PR-10, ADR-0018): state read from disk, start / stop through the C# host (a
/// <see cref="FakeDshHost"/> here, or the real <see cref="DshHost"/> where its preconditions are the subject), import with the
/// layer installer and its installed-tree check, DSH layer import from a file (ADR-0027), rollback, plugin changes and
/// self-test.
/// </summary>
public sealed class RuntimeCoordinatorTests : IDisposable
{
    private sealed class FakeForeground : IForegroundServiceController
    {
        public bool Held;

        public void Acquire() => Held = true;

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
    private readonly List<string> _calls = [];
    private readonly FakeForeground _fg = new();
    private readonly ListLog _log = new();
    private readonly InMemoryRuntimeJournal _journal = new();
    private FakeDshHost? _host;
    private RuntimeCoordinator? _coordinator;

    public void Dispose()
    {
        _coordinator?.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private RuntimeLayout Layout(string? libDir = null) => new(Path.Combine(_dir, "files"), null, libDir ?? Path.Combine(_dir, "nolibs"));

    /// <summary>The coordinator under test; <paramref name="realHost"/> uses the real <see cref="DshHost"/> instead of the fake.</summary>
    private RuntimeCoordinator Coordinator(RuntimeLayout? layout = null, IBundledAssets? assets = null, bool realHost = false)
    {
        _host ??= new FakeDshHost(_calls);
        return _coordinator ??= new RuntimeCoordinator(layout ?? Layout(), new RuntimeStateMachine(_journal, RuntimeCoordinator.Implementation), _fg, _log, _log,
            new RuntimeOptions { HostPathExists = LayerExtractor.AndroidLikeHost },
            hostFactory: realHost ? null : (_, _, _) => _host, bundledAssets: assets);
    }

    /// <summary>An installed runtime on disk and the fake host.</summary>
    private RuntimeCoordinator HostCoordinator()
    {
        var layout = Layout();
        TestRuntime.Install(layout.FilesDir);
        return Coordinator(layout);
    }

    private static string FixtureBundle(string name) => TestRuntime.RepoPath("tests", "TestVectors", "bundles", name);

    /// <summary>A nativeLibraryDir holding <c>libproot.so</c>, as on a device (the install check looks for it).</summary>
    private string LibDirWithProot()
    {
        var lib = Path.Combine(_dir, "lib");
        Directory.CreateDirectory(lib);
        File.WriteAllText(Path.Combine(lib, "libproot.so"), "elf");
        return lib;
    }

    [Fact]
    public void Constructor_RequiresALayout() =>
        Assert.Throws<ArgumentNullException>(() => new RuntimeCoordinator(null!, new RuntimeStateMachine(_journal, "x"), _fg, _log, _log));

    // ------------------------------------------------------------------ state from disk

    [Fact]
    public async Task RefreshState_ReadsTheDisk()
    {
        var c = Coordinator();
        Assert.Equal(RuntimeState.NotInstalled, (await c.RefreshStateAsync(CancellationToken.None)).State);
        TestRuntime.Install(Layout().FilesDir);
        Assert.Equal(RuntimeState.Ready, (await c.RefreshStateAsync(CancellationToken.None)).State);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task RefreshState_FirstMigratesDshboxNames_SoALegacyInstallStaysReady()
    {
        // A runtime installed before ADR-0019: the base sentinel sits in .dshbox and the workspace dir is .dsh/dshbox.
        var files = Layout().FilesDir;
        TestRuntime.Install(files);
        var rc = Path.Combine(files, "runtime", "runtime-current");
        Directory.Move(Path.Combine(rc, "base", ".dshmobile"), Path.Combine(rc, "base", ".dshbox"));
        Directory.CreateDirectory(Path.Combine(files, "user-data", ".dsh", "dshbox", "bin"));

        var c = Coordinator();
        Assert.Equal(RuntimeState.Ready, (await c.RefreshStateAsync(CancellationToken.None)).State);

        Assert.True(File.Exists(Path.Combine(rc, "base", ".dshmobile", "layer-base.sha256")));
        Assert.True(Directory.Exists(Path.Combine(files, "user-data", ".dsh", "dshmobile", "bin")));
        Assert.Contains(_log.Lines, l => l.StartsWith("name migration (ADR-0019): ", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ start / stop through the host (ADR-0015)

    [Fact]
    public async Task Start_UsesTheHost_AndASecondStartIsANoOp()
    {
        var c = HostCoordinator();
        var r = await c.StartAsync(DshProfile.Web, CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal(["host.start"], _calls);
        Assert.True(_fg.Held);
        Assert.Equal((RuntimeState.Running, HarnessState.Ready), (r.Snapshot.State, r.Snapshot.Harness));
        Assert.True(c.HostOwnsProcesses);
        Assert.True(c.HostUiToolsAtStart);

        var again = await c.StartAsync(DshProfile.Web, CancellationToken.None);
        Assert.True(again.Ok);
        Assert.Equal(["host.start"], _calls);
    }

    [Fact]
    public async Task Start_WithoutRuntime_IsRefused()
    {
        var r = await Coordinator().StartAsync(DshProfile.Web, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(RuntimeErrorCodes.InvalidTransition, r.Code);
        Assert.Empty(_calls);
        Assert.False(_fg.Held);
    }

    [Fact]
    public async Task Start_FirstRecoversAnInterruptedDshLayerInstall()
    {
        var c = HostCoordinator();
        var dsh = new DshLayerInstaller(Layout());
        // The process died mid-swap: the old layer is aside, half a new one sits at dsh, the journal says "begin".
        Directory.CreateDirectory(Path.Combine(dsh.AsideDir, "node_modules", "@deepseek-ai", "dsh", "lib"));
        File.WriteAllText(Path.Combine(dsh.AsideDir, DshLayerInstaller.BinJs), "bin");
        Directory.CreateDirectory(Path.Combine(dsh.AsideDir, ".dshmobile"));
        File.WriteAllText(Path.Combine(dsh.AsideDir, DshLayerInstaller.VersionFile), "1.0.0");
        if (Directory.Exists(dsh.DshDir))
        {
            Directory.Delete(dsh.DshDir, recursive: true);
        }
        Directory.CreateDirectory(Path.Combine(dsh.DshDir, "node_modules"));
        File.WriteAllText(dsh.JournalFile, "phase=begin\nhadOld=true\n");

        var r = await c.StartAsync(DshProfile.Web, CancellationToken.None);
        Assert.True(r.Ok, r.Message);
        // Since vNext PR-05 the sync at the start of every operation settles it, before StartAsync looks.
        Assert.Contains("dsh layer: interrupted install restored", _log.Lines);
        Assert.Equal("1.0.0", dsh.InstalledVersion());
        Assert.False(File.Exists(dsh.JournalFile));
        Assert.False(Directory.Exists(dsh.AsideDir));
        Assert.Equal(["host.start"], _calls);
    }

    /// <summary>The process died mid-import: the old body is aside, half a new base is live, the journal says "begin".</summary>
    private static LayerInstaller InterruptImport(RuntimeLayout layout)
    {
        var body = new LayerInstaller(layout);
        Directory.CreateDirectory(body.AsideDir);
        foreach (var item in (string[])["base", "android-side"])
        {
            Directory.Move(Path.Combine(layout.RuntimeCurrentDir, item), Path.Combine(body.AsideDir, item));
        }
        File.Move(Path.Combine(layout.RuntimeCurrentDir, "runtime-profile.json"), Path.Combine(body.AsideDir, "runtime-profile.json"));
        Directory.CreateDirectory(Path.Combine(layout.RuntimeCurrentDir, "base", "etc"));
        File.WriteAllText(body.JournalFile, "phase=begin\nold=base,android-side,runtime-profile.json\n");
        return body;
    }

    [Fact]
    public async Task Start_FirstRecoversAnInterruptedRuntimeImport()
    {
        var c = HostCoordinator();
        var layout = Layout();
        var body = InterruptImport(layout);

        var r = await c.StartAsync(DshProfile.Web, CancellationToken.None);
        Assert.True(r.Ok, r.Message);
        Assert.Contains("runtime body: interrupted import restored", _log.Lines);
        Assert.True(File.Exists(Path.Combine(layout.RuntimeCurrentDir, "base", ".dshmobile", "layer-base.sha256")));
        Assert.False(Directory.Exists(Path.Combine(layout.RuntimeCurrentDir, "base", "etc")));
        Assert.False(File.Exists(body.JournalFile));
        Assert.False(Directory.Exists(body.AsideDir));
        Assert.Equal(["host.start"], _calls);
    }

    [Fact]
    public async Task Refresh_AtAppStart_SettlesAnInterruptedRuntimeImport_SoTheStateIsThatOfAWholeBody()
    {
        var c = HostCoordinator();
        var body = InterruptImport(Layout());
        Assert.Equal(RuntimeState.Ready, (await c.RefreshStateAsync(CancellationToken.None)).State);
        Assert.Contains("runtime body: interrupted import restored", _log.Lines);
        Assert.False(File.Exists(body.JournalFile));
    }

    [Fact]
    public async Task Start_OtherProfile_IsRefused()
    {
        var r = await HostCoordinator().StartAsync(new DshProfile("headless"), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(RuntimeErrorCodes.InvalidTransition, r.Code);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Start_RealHost_FailingPreconditions_IsFailedAndReleasesService()
    {
        var layout = Layout();
        TestRuntime.Install(layout.FilesDir); // installed (guest PRoot in android-side), but no PRoot in nativeLibraryDir
        var r = await Coordinator(layout, realHost: true).StartAsync(DshProfile.Web, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal("EXECUTABLE_NOT_ALLOWED", r.Code);
        Assert.False(_fg.Held);
        Assert.Equal(RuntimeState.Failed, r.Snapshot.State);
        Assert.False(_coordinator!.HostOwnsProcesses);
    }

    [Fact]
    public async Task Start_HostFails_CleansUpCompanionsAndReleasesTheService_ThenARetryStarts()
    {
        var c = HostCoordinator();
        var seen = new List<string>();
        c.HostStopping = _ => { seen.Add("companions"); return Task.CompletedTask; };
        c.SandboxStopping += () => seen.Add("sandbox-stopping");
        _host!.OnStart = () => new DshHostResult(false, DshHost.NotReady, "not ready");
        var r = await c.StartAsync(DshProfile.Web, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(DshHost.NotReady, r.Code);
        Assert.Equal(["companions", "sandbox-stopping"], seen);
        Assert.False(_fg.Held);
        Assert.Equal(RuntimeState.Failed, r.Snapshot.State);
        Assert.False(c.HostOwnsProcesses);

        // A failed start owns nothing: the next start is a fresh one (not "stop the failed host first").
        seen.Clear();
        _host.OnStart = null;
        var again = await c.StartAsync(DshProfile.Web, CancellationToken.None);
        Assert.True(again.Ok, again.Message);
        Assert.Empty(seen);
        Assert.Equal(["host.start", "host.start"], _calls);
    }

    [Fact]
    public async Task Stop_RunsCompanionCleanupBeforeSandboxStoppingAndTheHostStop()
    {
        var c = HostCoordinator();
        c.HostStopping = _ => { _calls.Add("companions"); return Task.CompletedTask; };
        c.SandboxStopping += () => _calls.Add("sandbox-stopping");
        await c.StartAsync(DshProfile.Web, CancellationToken.None);
        var r = await c.StopAsync(CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal(["host.start", "companions", "sandbox-stopping", "host.stop"], _calls);
        Assert.False(_fg.Held);
        Assert.Equal(RuntimeState.Ready, r.Snapshot.State);
        Assert.False(c.HostOwnsProcesses);
        Assert.Null(c.HostUiToolsAtStart);
    }

    [Fact]
    public async Task Stop_FailingListeners_DoNotBlockTheStop()
    {
        var c = HostCoordinator();
        c.HostStopping = _ => throw new InvalidOperationException("companion gone");
        c.SandboxStopping += () => throw new InvalidOperationException("listener");
        await c.StartAsync(DshProfile.Web, CancellationToken.None);
        var r = await c.StopAsync(CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Contains("host.stop", _calls);
        Assert.Contains("host stopping hook failed: InvalidOperationException: companion gone", _log.Lines);
        Assert.Contains("sandbox stopping listener failed: InvalidOperationException: listener", _log.Lines);
        Assert.False(_fg.Held);
    }

    [Fact]
    public async Task Stop_NothingRunning_ReleasesAndRaisesNothing()
    {
        var c = HostCoordinator();
        var raised = 0;
        c.SandboxStopping += () => raised++;
        _fg.Held = true;
        var r = await c.StopAsync(CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal(0, raised);
        Assert.Empty(_calls);
        Assert.False(_fg.Held);
        Assert.Contains("stop: nothing running", _log.Lines);
    }

    [Fact]
    public async Task Stop_WithOrphans_KeepsTheServiceAndOwnership_UntilACleanStop()
    {
        var c = HostCoordinator();
        await c.StartAsync(DshProfile.Web, CancellationToken.None);
        _host!.OnStop = () => new DshHostResult(false, DshHost.Orphans, "1 left");
        var r = await c.StopAsync(CancellationToken.None);
        Assert.False(r.Ok);
        Assert.True(_fg.Held);
        Assert.True(c.HostOwnsProcesses);
        Assert.Equal(RuntimeState.Failed, c.State.Current.State);

        _host.OnStop = null;
        var retry = await c.StopAsync(CancellationToken.None);
        Assert.True(retry.Ok);
        Assert.Equal(["host.start", "host.stop", "host.stop"], _calls);
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
        Assert.Contains(_journal.ReadTail(20), e => e.Reason == $"host: dsh unhealthy ({RuntimeCoordinator.DshUnhealthy})");
        Assert.True(c.HostOwnsProcesses);
        Assert.True(_fg.Held);
        // The disk says "installed, idle", but while the host owns processes the journal follows the host.
        Assert.Equal(RuntimeState.Running, (await c.RefreshStateAsync(CancellationToken.None)).State);
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
        Assert.Equal(["host.start", "host.stop", "host.start"], _calls);
        Assert.Equal(1, companions);
        Assert.Equal((RuntimeState.Running, HarnessState.Ready), (r.Snapshot.State, r.Snapshot.Harness));
        Assert.True(_fg.Held);
    }

    [Fact]
    public async Task EveryCommandedTransition_IsJournaled()
    {
        var c = HostCoordinator();
        await c.StartAsync(DshProfile.Web, CancellationToken.None);
        await c.StopAsync(CancellationToken.None);
        var reasons = _journal.ReadTail(100).Select(e => e.Reason).ToList();
        Assert.Contains("start requested", reasons);
        Assert.Contains("host ready", reasons);
        Assert.Contains("stop requested", reasons);
        Assert.Contains("stopped", reasons);
    }

    // ------------------------------------------------------------------ import (MIGRATION rows 14-16)

    [Fact]
    public async Task Install_InstallsAndVerifies()
    {
        var layout = Layout(LibDirWithProot());
        var c = Coordinator(layout);
        var r = await c.InstallAsync(new RuntimePackage(FixtureBundle("good.zip")), null, CancellationToken.None);
        Assert.True(r.Ok, r.Message);
        Assert.True(File.Exists(Path.Combine(layout.LayerDir("base"), "etc", "os-release")));
        // ADR-0027: the DSH layer is not bundled with the APK; it is imported separately.
        Assert.Null(new DshLayerInstaller(layout).InstalledVersion());
        var verify = Assert.Single(_log.Reports);
        Assert.Equal(LayerExtractShadowCheck.InstallSlice, verify.Slice);
        Assert.Equal(ShadowVerdict.Agree, verify.Verdict);
        Assert.Equal(RuntimeState.Ready, c.State.Current.State);
        Assert.Contains(_journal.ReadTail(20), e => e.Reason == "import done");
    }

    [Fact]
    public async Task Install_CorruptBundle_IsFailedWithTheUpstreamCode()
    {
        var c = Coordinator();
        var r = await c.InstallAsync(new RuntimePackage(FixtureBundle("corrupt-node.zip")), null, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(RuntimeErrorCodes.Upstream, r.Code);
        Assert.Equal(BundleErrorCodes.Sha256Mismatch, r.UpstreamCode);
        Assert.Equal(RuntimeState.Failed, c.State.Current.State);
        Assert.Equal(BundleErrorCodes.Sha256Mismatch, c.State.Current.ErrorCode);
        Assert.Empty(_log.Reports);
    }

    [Fact]
    public async Task Install_NotAZip_IsFailed()
    {
        var path = Path.Combine(_dir, "bundle.zip");
        await File.WriteAllTextAsync(path, "not a zip");
        var r = await Coordinator().InstallAsync(new RuntimePackage(path), null, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(BundleErrorCodes.NotAZip, r.UpstreamCode);
    }

    [Fact]
    public async Task Install_WhileTheHostRuns_IsRefused()
    {
        var c = HostCoordinator();
        await c.StartAsync(DshProfile.Web, CancellationToken.None);
        var r = await c.InstallAsync(new RuntimePackage(Path.Combine(_dir, "x.zip")), null, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(RuntimeErrorCodes.InvalidTransition, r.Code);
        Assert.Equal(RuntimeState.Running, c.State.Current.State);
    }

    // ------------------------------------------------------------------ rollback (MIGRATION row 18)

    [Fact]
    public async Task Rollback_WithoutPreviousBody_IsReportedAndJournalUntouched()
    {
        var c = HostCoordinator();
        var r = await c.RollbackAsync(CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(LayerRollback.NoRollbackSlot, r.Code);
        Assert.Equal(RuntimeState.Ready, c.State.Current.State);
    }

    [Fact]
    public async Task Rollback_WhileTheHostRuns_IsRefused()
    {
        var c = HostCoordinator();
        Assert.True((await c.StartAsync(DshProfile.Web, CancellationToken.None)).Ok);
        var r = await c.RollbackAsync(CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(RuntimeErrorCodes.InvalidTransition, r.Code);
    }

    [Fact]
    public async Task Rollback_RestoresThePreviousBody_AndEndsReady()
    {
        var c = HostCoordinator();
        var layout = Layout();
        TestRuntime.Install(layout.FilesDir, version: "0", sha: "bb", body: Path.Combine(layout.RuntimeCurrentDir, "previous"));
        var r = await c.RollbackAsync(CancellationToken.None);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(RuntimeState.Ready, c.State.Current.State);
        Assert.Contains("\"version\":\"0\"", File.ReadAllText(Path.Combine(layout.RuntimeCurrentDir, "runtime-profile.json")), StringComparison.Ordinal);
        Assert.Contains(_journal.ReadTail(20), e => e.Reason == "rolled back");
    }

    [Fact]
    public async Task Rollback_FirstRecoversAnInterruptedImport_ThenSwapsThePreviousBodyIn()
    {
        var c = HostCoordinator();
        var layout = Layout();
        TestRuntime.Install(layout.FilesDir, version: "0", sha: "bb", body: Path.Combine(layout.RuntimeCurrentDir, "previous"));
        InterruptImport(layout);
        var r = await c.RollbackAsync(CancellationToken.None);
        Assert.True(r.Ok, r.Message);
        Assert.Contains("runtime body: interrupted import restored", _log.Lines);
        Assert.Contains("\"version\":\"0\"", File.ReadAllText(Path.Combine(layout.RuntimeCurrentDir, "runtime-profile.json")), StringComparison.Ordinal);
        // failed/ holds the body from before the interrupted import, not the half-placed one.
        var failed = LayerRollback.FailedDir(layout);
        Assert.Equal("aa", File.ReadAllText(Path.Combine(failed, "base", ".dshmobile", "layer-base.sha256")));
        Assert.False(Directory.Exists(Path.Combine(failed, "base", "etc")));
    }

    // ------------------------------------------------------------------ DSH layer import (ADR-0027)

    private RuntimeLayout InstalledLayout(string? dshVersion = null)
    {
        var layout = Layout();
        TestRuntime.Install(layout.FilesDir);
        if (dshVersion is not null)
        {
            Directory.CreateDirectory(Path.Combine(layout.LayerDir("dsh"), ".dshmobile"));
            File.WriteAllText(Path.Combine(layout.LayerDir("dsh"), ".dshmobile", "version"), dshVersion);
        }
        return layout;
    }

    /// <summary>The good DSH layer fixture under the release artifact's name (<c>&lt;version&gt;.tar.zst</c>).</summary>
    private string DshBundle(string name)
    {
        var dest = Path.Combine(_dir, name);
        using var compressor = new ZstdSharp.Compressor();
        File.WriteAllBytes(dest, compressor.Wrap(File.ReadAllBytes(FixtureBundle("dsh-good.tar"))));
        return dest;
    }

    [Fact]
    public async Task InstallDsh_FromFile_Installs_ThenKeepsTheSameVersion()
    {
        var layout = InstalledLayout();
        var c = Coordinator(layout);
        var path = DshBundle("1.3.0.tar.zst");
        var r = await c.InstallDshFromFileAsync(path, CancellationToken.None);
        Assert.True(r.Ok, r.Message);
        Assert.True(r.Changed);
        Assert.Equal("1.3.0", r.Version);
        Assert.Equal("1.3.0", new DshLayerInstaller(layout).InstalledVersion());
        Assert.Equal("1.3.0", c.State.Current.DshVersion);

        var same = await c.InstallDshFromFileAsync(path, CancellationToken.None);
        Assert.True(same.Ok, same.Message);
        Assert.False(same.Changed);
        Assert.Equal("1.3.0", same.Version);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task InstallDsh_OlderPackage_KeepsInstalledVersion()
    {
        var c = Coordinator(InstalledLayout("1.4.0"));
        var r = await c.InstallDshFromFileAsync(DshBundle("1.3.0.tar.zst"), CancellationToken.None);
        Assert.True(r.Ok, r.Message);
        Assert.False(r.Changed);
        Assert.Equal("1.4.0", r.Version);
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData("9.9.9.tar.zst")]
    [InlineData("1.0.0.tar.zst")]
    public async Task InstallDsh_RenamedPackage_IsRejectedBeforeVersionArbitration(string name)
    {
        var c = Coordinator(InstalledLayout("1.4.0"));
        var r = await c.InstallDshFromFileAsync(DshBundle(name), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(DshLayerErrorCodes.BundleInvalid, r.Code);
        Assert.Equal("1.4.0", r.Version);
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstallDsh_Replacement_CommitsOnlyAfterHealthCheck(bool healthy)
    {
        var layout = InstalledLayout("1.2.0");
        var c = Coordinator(layout);
        var layer = new DshLayerInstaller(layout);
        _host!.OnStart = () =>
        {
            Assert.True(layer.IsHeldForProbe);
            Assert.Equal("1.2.0", File.ReadAllText(Path.Combine(layer.AsideDir, DshLayerInstaller.VersionFile)));
            return new DshHostResult(healthy, healthy ? RuntimeErrorCodes.Ok : "DSH_EXITED", null);
        };

        var r = await c.InstallDshFromFileAsync(DshBundle("1.3.0.tar.zst"), CancellationToken.None);

        Assert.Equal(healthy, r.Ok);
        Assert.Equal(healthy, r.Changed);
        Assert.Equal(healthy ? RuntimeErrorCodes.Ok : DshLayerErrorCodes.HealthFailed, r.Code);
        Assert.Equal(healthy ? "1.3.0" : "1.2.0", layer.InstalledVersion());
        Assert.Equal(layer.InstalledVersion(), c.State.Current.DshVersion);
        Assert.Equal(healthy ? ["host.start", "host.stop"] : new[] { "host.start" }, _calls);
        Assert.False(File.Exists(layer.JournalFile));
        Assert.False(Directory.Exists(layer.AsideDir));
        Assert.False(_fg.Held);
    }

    [Fact]
    public async Task InstallDsh_ProbeStopFails_RetainsOldLayerUntilStopped()
    {
        var layout = InstalledLayout("1.2.0");
        var c = Coordinator(layout);
        var layer = new DshLayerInstaller(layout);
        _host!.OnStop = () => new DshHostResult(false, "DSH_STOP_FAILED", "process still alive");

        var r = await c.InstallDshFromFileAsync(DshBundle("1.3.0.tar.zst"), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(DshLayerErrorCodes.HealthFailed, r.Code);
        Assert.True(_fg.Held);
        Assert.True(layer.IsHeldForProbe);
        Assert.True(Directory.Exists(layer.AsideDir));

        _host.OnStop = null;
        var stopped = await c.StopAsync(CancellationToken.None);
        Assert.True(stopped.Ok);
        Assert.Equal("1.2.0", stopped.Snapshot.DshVersion);
        Assert.False(_fg.Held);
        Assert.False(File.Exists(layer.JournalFile));
    }

    [Fact]
    public async Task InstallDsh_InvalidLayer_IsBundleInvalid()
    {
        var layout = InstalledLayout();
        var c = Coordinator(layout);
        var path = Path.Combine(_dir, "1.2.0.tar.zst");
        File.Copy(FixtureBundle("dsh-invalid.tar"), path, overwrite: true);
        var r = await c.InstallDshFromFileAsync(path, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(DshLayerErrorCodes.BundleInvalid, r.Code);
        Assert.False(new DshLayerInstaller(layout).IsInstalled);
    }

    [Fact]
    public async Task InstallDsh_NotADshArchiveName_IsBadRequest()
    {
        var c = Coordinator(InstalledLayout());
        var r = await c.InstallDshFromFileAsync(DshBundle("dsh-layer.zip"), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(RuntimeErrorCodes.BadRequest, r.Code);
    }

    [Fact]
    public async Task InstallDsh_MissingFile_IsBundleNotFound()
    {
        var c = Coordinator(InstalledLayout());
        var r = await c.InstallDshFromFileAsync(Path.Combine(_dir, "1.0.0.tar.zst"), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(DshLayerErrorCodes.BundleNotFound, r.Code);
    }

    [Fact]
    public async Task InstallDsh_WhileTheHostRuns_IsRefused()
    {
        var layout = InstalledLayout("1.0.0");
        var c = Coordinator(layout);
        Assert.True((await c.StartAsync(DshProfile.Web, CancellationToken.None)).Ok);
        var r = await c.InstallDshFromFileAsync(DshBundle("9.9.9.tar.zst"), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(RuntimeErrorCodes.InvalidTransition, r.Code);
        Assert.Equal("1.0.0", new DshLayerInstaller(layout).InstalledVersion());
    }

    // ------------------------------------------------------------------ guest commands

    [Fact]
    public async Task Probe_UnknownProfile_IsAnError_NotAPass()
    {
        var o = await HostCoordinator().ProbeProfileAsync("web;id", CancellationToken.None);
        Assert.Null(o.Result);
        Assert.Equal(DshMobile.Runtime.Guest.DshProbe.UnknownProfile, o.ErrorCode);
    }

    [Theory]
    [InlineData("list", "web", "foo")]
    [InlineData("add", "desktop", "foo")]
    [InlineData("add", "web", "pkg; rm -rf /")]
    [InlineData("remove", "web", "foo@1.0.0")]
    public async Task PluginChange_BadArguments_AreBadRequestAndRunNothing(string verb, string profile, string operand)
    {
        // nativeLibraryDir has no libproot.so: reaching the launcher would fail differently, so BAD_REQUEST proves nothing ran.
        var r = await Coordinator().ChangePluginAsync(verb, profile, operand, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(RuntimeErrorCodes.BadRequest, r.Code);
        Assert.Empty(r.Lines);
    }

    [Theory]
    [InlineData("foo", "foo")]
    [InlineData("foo@^1.0.0", "foo")]
    [InlineData("@scope/foo", "@scope/foo")]
    [InlineData("@scope/foo@1.2.3", "@scope/foo")]
    public void SpecPackageName_StripsOnlyTheVersion(string spec, string name) =>
        Assert.Equal(name, RuntimeCoordinator.SpecPackageName(spec));

    [Fact]
    public async Task RelaySelfTest_StagesTheUiScripts_ThenReportsTheLauncherRefusal()
    {
        var layout = Layout();
        var c = Coordinator(layout);
        var r = await c.RunRelaySelfTestAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(DshMobile.Runtime.Automation.UiFacadeAssets.Intact(layout.GuestCacheDir));
        Assert.False(r.Ok); // no PRoot in nativeLibraryDir: a stable error, never a fake pass
        Assert.Null(r.ExitCode);
        Assert.NotNull(r.Error);
    }
}
