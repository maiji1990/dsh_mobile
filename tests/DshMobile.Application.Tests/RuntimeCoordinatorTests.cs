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
/// layer installer and its installed-tree check, prepare from the APK assets, rollback, plugin changes and self-test.
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

    private sealed class FakeAssets(Dictionary<string, byte[]> files) : IBundledAssets
    {
        public IReadOnlyList<string> List(string dir) =>
            files.Keys.Where(k => k.StartsWith(dir + "/", StringComparison.Ordinal)).Select(k => k[(dir.Length + 1)..]).ToList();

        public Stream Open(string path) =>
            files.TryGetValue(path, out var b) ? new MemoryStream(b) : throw new FileNotFoundException("no asset " + path);
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

    private static FakeAssets DshAssets()
    {
        var tar = File.ReadAllBytes(FixtureBundle("dsh-good.tar"));
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(tar));
        return new FakeAssets(new()
        {
            ["dsh/1.2.0.tar.zst"] = tar,
            ["dsh/1.2.0.tar.zst.sha256"] = System.Text.Encoding.UTF8.GetBytes(sha + "  dsh.tar.zst\n"),
            ["runtime/android-side.tar.zst"] = [1],
        });
    }

    [Fact]
    public async Task Install_InstallsVerifiesAndPrepares()
    {
        var layout = Layout(LibDirWithProot());
        var c = Coordinator(layout, DshAssets());
        var r = await c.InstallAsync(new RuntimePackage(FixtureBundle("good.zip")), null, CancellationToken.None);
        Assert.True(r.Ok, r.Message);
        Assert.True(File.Exists(Path.Combine(layout.LayerDir("base"), "etc", "os-release")));
        Assert.Equal("1.2.0", new DshLayerInstaller(layout).InstalledVersion());
        var verify = Assert.Single(_log.Reports);
        Assert.Equal(LayerExtractShadowCheck.InstallSlice, verify.Slice);
        Assert.Equal(ShadowVerdict.Agree, verify.Verdict);
        Assert.Equal(RuntimeState.Ready, c.State.Current.State);
        Assert.Contains(_journal.ReadTail(20), e => e.Reason == "import done");
    }

    [Fact]
    public async Task Install_CorruptBundle_IsFailedWithTheUpstreamCode()
    {
        var c = Coordinator(assets: DshAssets());
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
        var r = await Coordinator(assets: DshAssets()).InstallAsync(new RuntimePackage(path), null, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(BundleErrorCodes.NotAZip, r.UpstreamCode);
    }

    [Fact]
    public async Task Install_PrepareFailure_IsNotReportedAsSuccess()
    {
        // No APK assets: the layers install, but runtime.prepare cannot run, so the import is not complete.
        var c = Coordinator(Layout(LibDirWithProot()));
        var r = await c.InstallAsync(new RuntimePackage(FixtureBundle("good.zip")), null, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal("RUNTIME_IMPORT_UNVERIFIED", r.Code);
        Assert.StartsWith("runtime.prepare failed", r.Message, StringComparison.Ordinal);
        Assert.Equal(RuntimeState.Failed, c.State.Current.State);
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

    // ------------------------------------------------------------------ runtime.prepare / bundled DSH (MIGRATION row 17)

    private const string Step1 = "no full layered runtime in APK (expected; base/node come from import)";

    private RuntimeLayout InstalledLayout(string? dshVersion = null)
    {
        var layout = Layout();
        TestRuntime.Install(layout.FilesDir);
        if (dshVersion is not null)
        {
            Directory.CreateDirectory(Path.Combine(layout.LayerDir("dsh"), ".dshbox"));
            File.WriteAllText(Path.Combine(layout.LayerDir("dsh"), ".dshbox", "version"), dshVersion);
        }
        return layout;
    }

    [Fact]
    public async Task Prepare_InstallsTheBundledLayer_ThenKeepsIt()
    {
        var layout = InstalledLayout();
        var c = Coordinator(layout, DshAssets());
        var r = await c.PrepareAsync(CancellationToken.None);
        Assert.True(r.Ok, r.Message);
        Assert.Equal($"{Step1}; DSH 1.2.0 installed", r.Message);
        Assert.Equal("1.2.0", new DshLayerInstaller(layout).InstalledVersion());
        Assert.Equal($"{Step1}; DSH kept at 1.2.0", (await c.PrepareAsync(CancellationToken.None)).Message);
        Assert.Empty(_log.Reports);
    }

    [Fact]
    public async Task Prepare_RefusesToReplaceTheLayerWhileRunning_ButKeepingIsFine()
    {
        var layout = InstalledLayout("1.0.0");
        var c = Coordinator(layout, DshAssets());
        Assert.True((await c.StartAsync(DshProfile.Web, CancellationToken.None)).Ok);
        var refused = await c.PrepareAsync(CancellationToken.None);
        Assert.False(refused.Ok);
        Assert.Equal(RuntimeErrorCodes.InvalidTransition, refused.Code);
        Assert.Equal("1.0.0", new DshLayerInstaller(layout).InstalledVersion());

        File.WriteAllText(Path.Combine(layout.LayerDir("dsh"), ".dshbox", "version"), "9.0.0");
        var kept = await c.PrepareAsync(CancellationToken.None);
        Assert.True(kept.Ok, kept.Message);
        Assert.EndsWith("DSH kept at 9.0.0", kept.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prepare_WithoutAssets_IsNotImplemented()
    {
        var r = await Coordinator(InstalledLayout()).PrepareAsync(CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(RuntimeErrorCodes.NotImplemented, r.Code);
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
