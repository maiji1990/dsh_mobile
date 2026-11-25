using System.Text.Json;
using DshMobile.Core.Diagnostics;
using DshMobile.Runtime;
using DshMobile.Runtime.Diagnostics;
using DshMobile.Runtime.Guest;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// E5 新口径的静态契约：生产同口径 argv/env、隔离缓存 staging、compile-cache 只读盘点的隐私边界、
/// COMPILE_CACHE stderr 只聚合不保存原文，以及“诊断程序集只在诊断构建被引用”的普通 Release 隔离。
/// </summary>
public sealed class CompileCacheDiagnosticsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dshm-cache-diag-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private RuntimeLayout Layout()
    {
        var files = Path.Combine(_root, "files");
        Directory.CreateDirectory(files);
        return new RuntimeLayout(files, Path.Combine(_root, "cache"), Path.Combine(_root, "libs"));
    }

    private static WebProfileRecord WebRun(
        string result = ExperimentRecord.Ok,
        string? errorCode = null) =>
        new()
        {
            Variant = WebProfileExperiments.Off,
            RunId = "0123456789ab",
            Iteration = 1,
            HttpReadyMs = 1000,
            Result = result,
            ErrorCode = errorCode,
            StartedAt = new DateTimeOffset(2026, 11, 25, 2, 3, 4, TimeSpan.Zero),
        };

    private static CompileCacheInventory Inventory(int fileCount, long totalBytes) =>
        new(true, fileCount, totalBytes, 1, ["v26.13.1"], ["arm64"], null);

    [Fact]
    public void Inventory_CountsFilesAndBytes_ButOnlyExposesVersionAndArchitecturePrefixes()
    {
        var cache = Path.Combine(_root, "node-compile-cache");
        var arm = Path.Combine(cache, "v26.13.1-arm64-5fad6d45-501");
        var x64 = Path.Combine(cache, "v27.0.0-x64-01234567-501");
        var unknown = Path.Combine(cache, "private-user-token");
        Directory.CreateDirectory(arm);
        Directory.CreateDirectory(x64);
        Directory.CreateDirectory(unknown);
        File.WriteAllBytes(Path.Combine(arm, "a.cache"), new byte[10]);
        File.WriteAllBytes(Path.Combine(arm, "b.cache"), new byte[20]);
        File.WriteAllBytes(Path.Combine(x64, "c.cache"), new byte[30]);
        File.WriteAllBytes(Path.Combine(unknown, "secret-user-file.cache"), new byte[40]);

        var inventory = CompileCacheInventory.Inspect(cache);
        var json = inventory.ToJsonLine();

        Assert.True(inventory.Exists);
        Assert.Equal(4, inventory.FileCount);
        Assert.Equal(100, inventory.TotalBytes);
        Assert.Equal(3, inventory.DirectoryCount);
        Assert.Equal(["v26.13.1", "v27.0.0"], inventory.VersionDirectories);
        Assert.Equal(["arm64", "x64"], inventory.Architectures);
        Assert.Null(inventory.ErrorCode);
        Assert.DoesNotContain("5fad6d45", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-user-token", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-user-file", json, StringComparison.Ordinal);
        Assert.DoesNotContain(_root, json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"inventory\"", json, StringComparison.Ordinal);
        Assert.Contains("\"fileCount\":4", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Inventory_MissingDirectory_IsAnEmptyObservationNotAnError()
    {
        var inventory = CompileCacheInventory.Inspect(Path.Combine(_root, "does-not-exist"));

        Assert.False(inventory.Exists);
        Assert.Equal(0, inventory.FileCount);
        Assert.Equal(0, inventory.TotalBytes);
        Assert.Empty(inventory.VersionDirectories);
        Assert.Empty(inventory.Architectures);
        Assert.Null(inventory.ErrorCode);
        Assert.Contains("cache=absent", inventory.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void DebugAggregator_CountsOnlyFixedMarkers_AndNeverRetainsOrEmitsTheRawLine()
    {
        var aggregator = new CompileCacheDebugAggregator();
        aggregator.Observe("[compile cache] reading cache from /data/user/0/com.example/secret for ESM /root/projects/token=mytoken success, size=123");
        aggregator.Observe("[compile cache] reading cache from /data/user/0/com.example/other for ESM /root/projects/secret.mjs");
        aggregator.Observe(" ENOENT");
        aggregator.Observe("[compile cache] reading cache from /tmp/private for ESM /root/projects/third.mjs magic number mismatch: expected 1, actual 2");
        aggregator.Observe("[compile cache] Renaming /tmp/private/tmp.cache to /tmp/private/final.cache...success");
        aggregator.Observe("[compile cache] V8 code cache for ESM /root/projects/reject.mjs was rejected, initializing the in-memory entry");

        var counts = aggregator.Counts();
        Assert.Equal(3, counts.Readings);
        Assert.Equal(1, counts.Hits);
        Assert.Equal(2, counts.Misses);
        Assert.Equal(1, counts.Writes);
        Assert.Equal(1, counts.Rejects);
        Assert.Equal(1, counts.ReadFailures);
        Assert.Equal(0.3333, counts.HitRate!.Value, 4);

        var record = new CompileCacheRecord
        {
            Kind = CompileCacheRecord.ProbeKind,
            CacheMode = "warm",
            FileCount = 3,
            TotalBytes = 123,
            Debug = counts,
            Result = ExperimentRecord.Ok,
            StartedAt = DateTimeOffset.UtcNow,
        };
        var json = record.ToJsonLine();
        Assert.DoesNotContain("/data/", json, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/", json, StringComparison.Ordinal);
        Assert.DoesNotContain("/root/", json, StringComparison.Ordinal);
        Assert.DoesNotContain("mytoken", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", json, StringComparison.Ordinal);
        Assert.Contains("\"readings\":3", json, StringComparison.Ordinal);
        Assert.Contains("\"hitRate\":0.3333", json, StringComparison.Ordinal);
    }

    [Fact]
    public void FlushSummary_ParsesOnlySchemaOneBooleansAndASafeErrorToken()
    {
        var ok = CompileCacheFlushSummary.TryParse("""{"schema":1,"supported":true,"flushed":true,"error":null,"path":"/data/private"}""");
        Assert.NotNull(ok);
        Assert.True(ok!.Supported);
        Assert.True(ok.Flushed);
        Assert.Null(ok.Error);

        var failed = CompileCacheFlushSummary.TryParse("""{"schema":1,"supported":true,"flushed":false,"error":"ERR_ACCESS_DENIED"}""");
        Assert.NotNull(failed);
        Assert.Equal("ERR_ACCESS_DENIED", failed!.Error);

        Assert.Null(CompileCacheFlushSummary.TryParse("""{"schema":2,"supported":true,"flushed":true}"""));
        Assert.Null(CompileCacheFlushSummary.TryParse("""{"schema":1,"supported":true,"flushed":true,"error":"/data/private"}"""));
        Assert.Null(CompileCacheFlushSummary.TryParse("not json"));
        Assert.Null(CompileCacheFlushSummary.TryParse(""));
    }

    [Fact]
    public void BuildArgv_IsTheProductionLatestArgv_WithPreloadThenLinkThenStartupFsCacheShim()
    {
        var layout = Layout();
        Directory.CreateDirectory(layout.ShimDir);
        File.WriteAllText(layout.ShimFile, "// link shim");
        using var isolated = IsolatedGuestLayout.Create(layout, Path.Combine(_root, "iso-argv"));
        var runner = new WebProfileRunner(layout, _ => { }, () => false, _ => false);

        var argv = runner.BuildArgv(isolated).ToList();
        var expose = argv.IndexOf("--expose-internals");
        var preserve = argv.IndexOf("--preserve-symlinks");

        Assert.True(File.Exists(Path.Combine(isolated.CacheDir, "dshmobile", GuestAssetProvisioner.StartupFsCacheShimName)));
        Assert.True(File.Exists(Path.Combine(isolated.CacheDir, "dshmobile", GuestAssetProvisioner.WebTrimPatchName)));
        Assert.Equal("--preserve-symlinks", argv[expose + 1]);
        Assert.Equal(expose + 2, preserve + 1);
        Assert.Equal("--import", argv[preserve + 1]);
        Assert.Equal(WebProfileExperiments.GuestDir + "/" + WebProfileExperiments.PreloadName, argv[preserve + 2]);
        Assert.Equal("--import", argv[preserve + 3]);
        Assert.Equal(RuntimeConstants.LinkShimGuestPath, argv[preserve + 4]);
        Assert.Equal("--import", argv[preserve + 5]);
        Assert.Equal(GuestAssetProvisioner.GuestStartupFsCacheShim, argv[preserve + 6]);
        Assert.Equal(RuntimeConstants.GuestDshBin, argv[preserve + 7]);
        Assert.Equal("--profile", argv[preserve + 8]);
        Assert.Equal(RuntimeConstants.DshWebProfile, argv[preserve + 9]);
        Assert.Equal("--patch", argv[preserve + 10]);
        Assert.Equal(GuestAssetProvisioner.GuestWebTrimPatch, argv[preserve + 11]);
        Assert.Equal("--no-open", argv[^1]);
        Assert.DoesNotContain("--inspect", argv);
        Assert.Single(argv, item => item == "--preserve-symlinks");
    }

    [Fact]
    public void BuildEnv_MatchesProductionAndOnlyTheProbeRoundEnablesCompileCacheDebug()
    {
        var layout = Layout();
        using var isolated = IsolatedGuestLayout.Create(layout, Path.Combine(_root, "iso-env"));
        var runner = new WebProfileRunner(layout, _ => { }, () => false, _ => false);
        var off = WebProfileExperiments.All[0];
        var profile = WebProfileExperiments.All[1];

        var cold = runner.BuildEnv(isolated, off, debugCompileCache: false, flushOnStop: true);
        Assert.Equal("/opt/dshmobile-cache/node-compile-cache", cold["NODE_COMPILE_CACHE"]);
        Assert.Equal("/opt/dshapp/runtime:/usr/local", cold["PROOT_RO_CACHE_PREFIXES"]);
        Assert.Equal("0", cold["PROOT_RO_CACHE_NONEXIST"]);
        Assert.Equal("/opt/dshapp/runtime", cold["PROOT_RO_CACHE_NONEXIST_PREFIXES"]);
        Assert.Equal("1", cold["DSHM_COMPILE_CACHE_FLUSH_ON_STOP"]);
        Assert.Equal(off.Mode, cold["DSHM_PROFILE_MODE"]);
        Assert.False(cold.ContainsKey("NODE_DEBUG_NATIVE"));

        var probe = runner.BuildEnv(isolated, off, debugCompileCache: true, flushOnStop: false);
        Assert.Equal(CompileCacheDebugAggregator.DebugFlag, probe["NODE_DEBUG_NATIVE"]);
        Assert.False(probe.ContainsKey("DSHM_COMPILE_CACHE_FLUSH_ON_STOP"));

        var timing = runner.BuildEnv(isolated, profile, debugCompileCache: false, flushOnStop: false);
        Assert.Equal(profile.Mode, timing["DSHM_PROFILE_MODE"]);
        Assert.False(timing.ContainsKey("NODE_DEBUG_NATIVE"));
    }

    [Fact]
    public void IsolatedLayout_StagesBothProductionAssets_AndUsesTheProductionCompileCacheDirectoryName()
    {
        var layout = Layout();
        using var isolated = IsolatedGuestLayout.Create(layout, Path.Combine(_root, "iso-stage"));

        Assert.Equal(Path.Combine(isolated.CacheDir, "node-compile-cache"), isolated.NodeCompileCacheDir);
        Assert.True(File.Exists(Path.Combine(isolated.CacheDir, "dshmobile", GuestAssetProvisioner.StartupFsCacheShimName)));
        Assert.True(File.Exists(Path.Combine(isolated.CacheDir, "dshmobile", GuestAssetProvisioner.WebTrimPatchName)));
    }

    [Fact]
    public void Records_ExposeCacheCountsButNeverTheCacheOrProfilePath()
    {
        var record = new WebProfileRecord
        {
            Variant = WebProfileExperiments.Off,
            RunId = "0123456789ab",
            Iteration = 3,
            HttpReadyMs = 5123.5,
            Result = ExperimentRecord.Ok,
            StartedAt = new DateTimeOffset(2026, 11, 25, 2, 3, 4, TimeSpan.Zero),
            CacheMode = "warm",
            CompileCacheFileCount = 42,
            CompileCacheBytes = 4096,
        };
        var json = record.ToJsonLine();
        var parsed = JsonElement.Parse(json);
        Assert.Equal("warm", parsed.GetProperty("cacheMode").GetString());
        Assert.Equal(42, parsed.GetProperty("compileCacheFileCount").GetInt32());
        Assert.Equal(4096, parsed.GetProperty("compileCacheBytes").GetInt64());
        Assert.DoesNotContain(_root, json, StringComparison.Ordinal);

        var cold = CompileCacheRecord.ForCold(record, CompileCacheInventory.Empty, null, record.StartedAt);
        Assert.Equal(CompileCacheRecord.FlushMissing, cold.ErrorCode);
        Assert.Equal(ExperimentRecord.Failed, cold.Result);

        var inventory = CompileCacheRecord.ForInventory(CompileCacheInventory.Empty, record.StartedAt);
        Assert.Equal(ExperimentRecord.Ok, inventory.Result);
        Assert.DoesNotContain(_root, inventory.ToJsonLine(), StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticPreload_FlushesCompileCacheOnStop_AndWritesOnlyAFixedCodeSummary()
    {
        var script = File.ReadAllText(Repo.PathOf("src", "DshMobile.Android", "Diagnostics", "web-profile-preload.mjs"));

        Assert.Contains("nodeModule.flushCompileCache()", script, StringComparison.Ordinal);
        Assert.Contains("DSHM_COMPILE_CACHE_FLUSH_ON_STOP", script, StringComparison.Ordinal);
        Assert.Contains("compile-cache.flush.json", script, StringComparison.Ordinal);
        Assert.Contains("COMPILE_CACHE_FLUSH_UNSUPPORTED", script, StringComparison.Ordinal);
        Assert.DoesNotContain("console.log", script, StringComparison.Ordinal);
        Assert.DoesNotContain("writeFileSync(join(dir, 'summary.json'), JSON.stringify(cpu", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticsAssembly_IsReferencedOnlyByTheDiagnosticBuildAndTests()
    {
        var android = File.ReadAllText(Repo.PathOf("src", "DshMobile.Android", "DshMobile.Android.csproj"));
        var runtime = File.ReadAllText(Repo.PathOf("src", "DshMobile.Runtime", "DshMobile.Runtime.csproj"));
        var tests = File.ReadAllText(Repo.PathOf("tests", "DshMobile.Runtime.Tests", "DshMobile.Runtime.Tests.csproj"));

        Assert.Contains("Condition=\"'$(DshDiagnostics)' == 'true'\"", android, StringComparison.Ordinal);
        Assert.Contains("..\\DshMobile.Runtime.Diagnostics\\DshMobile.Runtime.Diagnostics.csproj", android, StringComparison.Ordinal);
        Assert.DoesNotContain("Runtime.Diagnostics", runtime, StringComparison.Ordinal);
        Assert.Contains("..\\..\\src\\DshMobile.Runtime.Diagnostics\\DshMobile.Runtime.Diagnostics.csproj", tests, StringComparison.Ordinal);
    }

    [Fact]
    public void ForProbe_RequiresReadingsAndPreservesRunFailure()
    {
        var run = WebRun();
        var ok = CompileCacheRecord.ForProbe(
            run,
            Inventory(2, 20),
            new CompileCacheDebugCounts(4, 1, 3, 0, 0, 0, 0.25),
            run.StartedAt,
            iteration: 1);
        Assert.Equal(ExperimentRecord.Ok, ok.Result);
        Assert.Null(ok.ErrorCode);
        Assert.Equal(1, ok.Iteration);
        Assert.Equal(4, ok.Debug!.Readings);

        var noReadings = CompileCacheRecord.ForProbe(
            run,
            Inventory(2, 20),
            new CompileCacheDebugCounts(0, 0, 0, 0, 0, 0, null),
            run.StartedAt,
            iteration: 1);
        Assert.Equal(ExperimentRecord.Failed, noReadings.Result);
        Assert.Equal(CompileCacheRecord.NoDebugOutput, noReadings.ErrorCode);

        var failedRun = WebRun(ExperimentRecord.Timeout, WebProfileExperiments.ProfileTimeout);
        var failed = CompileCacheRecord.ForProbe(
            failedRun,
            Inventory(0, 0),
            new CompileCacheDebugCounts(0, 0, 0, 0, 0, 0, null),
            failedRun.StartedAt);
        Assert.Equal(ExperimentRecord.Timeout, failed.Result);
        Assert.Equal(WebProfileExperiments.ProfileTimeout, failed.ErrorCode);
        Assert.Null(failed.Iteration);
    }

    [Fact]
    public void ForCold_MapsFlushAndEmptyFailures_AndCarriesIteration()
    {
        var run = WebRun();

        var unsupported = CompileCacheRecord.ForCold(
            run, Inventory(1, 10), new CompileCacheFlushSummary(false, false, null), run.StartedAt, 2);
        Assert.Equal(ExperimentRecord.Failed, unsupported.Result);
        Assert.Equal(CompileCacheRecord.FlushUnsupported, unsupported.ErrorCode);

        var failed = CompileCacheRecord.ForCold(
            run, Inventory(1, 10), new CompileCacheFlushSummary(true, false, "ERR_ACCESS_DENIED"), run.StartedAt, 3);
        Assert.Equal(ExperimentRecord.Failed, failed.Result);
        Assert.Equal(CompileCacheRecord.FlushFailed, failed.ErrorCode);
        Assert.Equal("ERR_ACCESS_DENIED", failed.FlushError);

        var empty = CompileCacheRecord.ForCold(
            run, Inventory(0, 0), new CompileCacheFlushSummary(true, true, null), run.StartedAt, 4);
        Assert.Equal(ExperimentRecord.Failed, empty.Result);
        Assert.Equal(CompileCacheRecord.CacheEmpty, empty.ErrorCode);

        var ok = CompileCacheRecord.ForCold(
            run, Inventory(3, 30), new CompileCacheFlushSummary(true, true, null), run.StartedAt, 5);
        Assert.Equal(ExperimentRecord.Ok, ok.Result);
        Assert.Null(ok.ErrorCode);
        Assert.Equal(5, ok.Iteration);
        Assert.True(ok.FlushOk is true);
    }

    [Fact]
    public void Count_ReturnsFileCountAndTotalBytes()
    {
        var cache = Path.Combine(_root, "count-cache");
        Directory.CreateDirectory(Path.Combine(cache, "v26.13.1-arm64"));
        File.WriteAllBytes(Path.Combine(cache, "v26.13.1-arm64", "a.cache"), new byte[4]);
        File.WriteAllBytes(Path.Combine(cache, "v26.13.1-arm64", "b.cache"), new byte[6]);

        var counted = CompileCacheInventory.Count(cache);
        Assert.Equal(2, counted.Files);
        Assert.Equal(10L, counted.Bytes);

        var missing = CompileCacheInventory.Count(Path.Combine(_root, "count-missing"));
        Assert.Equal(0, missing.Files);
        Assert.Equal(0L, missing.Bytes);
    }

    [Fact]
    public async Task RunAsync_RejectsColdRoundsOutsideOneToFifty()
    {
        var runner = new WebProfileRunner(Layout(), _ => { }, () => false, _ => false);
        var preload = new byte[] { 1 };
        var profiles = Path.Combine(_root, "cold-rounds");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            runner.RunAsync(1, preload, profiles, _ => { }, CancellationToken.None, coldRounds: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            runner.RunAsync(1, preload, profiles, _ => { }, CancellationToken.None, coldRounds: 51));
    }

    [Fact]
    public void BuildArgv_BindsOnlyTheIsolatedWorkspaceAndCache()
    {
        var layout = Layout();
        Directory.CreateDirectory(layout.ShimDir);
        File.WriteAllText(layout.ShimFile, "// link shim");
        using var isolated = IsolatedGuestLayout.Create(layout, Path.Combine(_root, "iso-isolation"));
        var runner = new WebProfileRunner(layout, _ => { }, () => false, _ => false);

        var argv = runner.BuildArgv(isolated);
        Assert.DoesNotContain(argv, item => item.Contains(layout.UserDataDir, StringComparison.Ordinal));
        Assert.DoesNotContain(argv, item => item.Contains(layout.GuestCacheDir, StringComparison.Ordinal));
        Assert.Contains("--bind=" + isolated.WorkspaceDir + ":" + RuntimeConstants.SandboxWorkspace, argv);
        Assert.Contains("--bind=" + isolated.CacheDir + ":" + RuntimeConstants.GuestCacheDir, argv);
    }

    [Fact]
    public void BuildEnv_KeepsEveryProductionDshEnvironmentKey()
    {
        var layout = Layout();
        Directory.CreateDirectory(layout.RuntimeCurrentDir);
        File.WriteAllText(
            Path.Combine(layout.RuntimeCurrentDir, "runtime-profile.json"),
            "{\"bundle\":{\"version\":\"0.1.0\",\"arch\":\"arm64\",\"compression\":\"zstd\"},\"layers\":[],\"assembly\":[]}");
        using var isolated = IsolatedGuestLayout.Create(layout, Path.Combine(_root, "iso-env-parity"));
        var runner = new WebProfileRunner(layout, _ => { }, () => false, _ => false);
        var production = new ProotCommandBuilder(layout).Environment("dsh");
        Assert.Contains("NODE_COMPILE_CACHE", production.Keys);
        var env = runner.BuildEnv(isolated, WebProfileExperiments.All[0], debugCompileCache: false, flushOnStop: false);

        foreach (var pair in production)
        {
            Assert.True(env.TryGetValue(pair.Key, out var value), pair.Key);
            Assert.Equal(pair.Value, value);
        }
        Assert.Equal(WebProfileExperiments.GuestDir, env["DSHM_PROFILE_DIR"]);
    }
}
