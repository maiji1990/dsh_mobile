using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using DshMobile.Core.Diagnostics;
using DshMobile.Runtime.Guest;
using DshMobile.Runtime.Supervision;

namespace DshMobile.Runtime.Diagnostics;

/// <summary>
/// P0 Web-profile diagnostic (docs/WEB_STARTUP_DIAGNOSIS.md). E5 now runs the exact production argv/env inside one isolated
/// cache root: a cold start flushes the Node compile cache after HTTP readiness, a dedicated warm round samples the
/// COMPILE_CACHE debug counters, and only then do the E5-OFF / E5-PROFILE timing rounds run (without the debug flag, so
/// timings are not polluted). Diagnostics only: no production code path calls it, no inspector port is opened, and the raw
/// profile is copied to a caller-owned private directory.
/// </summary>
public sealed class WebProfileRunner
{
    public const string Busy = "EXPERIMENT_BUSY";
    public const string DshRunning = "EXPERIMENT_DSH_RUNNING";
    public const string PortBusy = "EXPERIMENT_PORT_BUSY";
    public const string BadReps = "EXPERIMENT_BAD_REPS";
    public const string NoPreload = "EXPERIMENT_NO_PRELOAD";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan PortCloseWait = TimeSpan.FromSeconds(3);

    private readonly RuntimeLayout _layout;
    private readonly Action<string> _log;
    private readonly Func<bool> _productionRunning;
    private readonly Func<int, bool> _portOpen;
    private readonly ProotCommandBuilder _builder;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WebProfileRunner(
        RuntimeLayout layout,
        Action<string> log,
        Func<bool> productionRunning,
        Func<int, bool>? portOpen = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(productionRunning);
        _layout = layout;
        _log = log;
        _productionRunning = productionRunning;
        _portOpen = portOpen ?? (port => PortOpen(RuntimeConstants.DshHost, port, TimeSpan.FromMilliseconds(250)));
        _builder = new ProotCommandBuilder(layout);
    }

    /// <summary>Copies the diagnostic preload into the isolated guest cache; returns its host directory.</summary>
    public static string StagePreload(IsolatedGuestLayout isolated, ReadOnlySpan<byte> preload)
    {
        ArgumentNullException.ThrowIfNull(isolated);
        if (preload.Length == 0)
        {
            throw new ArgumentException("preload is empty", nameof(preload));
        }
        var hostDir = Path.Combine(isolated.CacheDir, "experiments", "e5");
        Directory.CreateDirectory(hostDir);
        File.WriteAllBytes(Path.Combine(hostDir, WebProfileExperiments.PreloadName), preload.ToArray());
        return hostDir;
    }

    /// <summary>
    /// 能力 A：只读盘点生产 <c>_layout.GuestCacheDir/node-compile-cache</c>，返回只含计数、版本/架构前缀与固定错误码的
    /// 结构化记录。调用方只把 <see cref="CompileCacheRecord.Summary"/> 写日志、把 JSONL 写入 App 私有目录。
    /// </summary>
    public static CompileCacheRecord InspectProductionCompileCache(RuntimeLayout layout, DateTimeOffset? startedAt = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var inventory = CompileCacheInventory.Inspect(Path.Combine(layout.GuestCacheDir, "node-compile-cache"));
        return CompileCacheRecord.ForInventory(inventory, startedAt ?? DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 运行一轮与生产同口径的 E5 套件。<paramref name="reps"/> 是热轮 E5-OFF / E5-PROFILE 的配对数；
    /// 先执行 <paramref name="coldRounds"/> 轮独立冷启动（每轮前清空 node-compile-cache），再执行一次命中率采样，
    /// 最后一轮冷启动落盘的缓存由 probe 与所有 warm 轮复用。
    /// </summary>
    public async Task<ExperimentSuiteResult> RunAsync(
        int reps,
        byte[] preload,
        string profilesDir,
        Action<WebProfileRecord> sink,
        CancellationToken ct,
        Action<CompileCacheRecord>? cacheSink = null,
        int coldRounds = 1)
    {
        ArgumentNullException.ThrowIfNull(preload);
        ArgumentNullException.ThrowIfNull(profilesDir);
        ArgumentNullException.ThrowIfNull(sink);
        if (coldRounds < 1 || coldRounds > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(coldRounds));
        }
        cacheSink ??= static _ => { };
        if (reps is < 1 or > 50)
        {
            return new ExperimentSuiteResult(false, BadReps, "reps must be 1..50", 0, 0);
        }
        if (preload.Length == 0)
        {
            return new ExperimentSuiteResult(false, NoPreload, "the diagnostic preload asset is empty", 0, 0);
        }
        if (!_gate.Wait(0))
        {
            return new ExperimentSuiteResult(false, Busy, "another experiment is running", 0, 0);
        }
        try
        {
            if (_productionRunning())
            {
                return new ExperimentSuiteResult(false, DshRunning, "stop DSH before running experiments", 0, 0);
            }
            if (_portOpen(RuntimeConstants.DshPort))
            {
                return new ExperimentSuiteResult(false, PortBusy, $"port {RuntimeConstants.DshPort} is in use", 0, 0);
            }
            Directory.CreateDirectory(profilesDir);
            var root = Path.Combine(_layout.FilesDir, "startup-experiments", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-E5");
            var isolated = IsolatedGuestLayout.Create(_layout, Path.Combine(root, "e5"));
            var runs = 0;
            var failures = 0;
            void Emit(WebProfileRecord record)
            {
                runs++;
                if (!string.Equals(record.Result, ExperimentRecord.Ok, StringComparison.Ordinal))
                {
                    failures++;
                }
                sink(record);
            }
            void EmitCache(CompileCacheRecord record)
            {
                if (!string.Equals(record.Result, ExperimentRecord.Ok, StringComparison.Ordinal))
                {
                    failures++;
                }
                cacheSink(record);
                _log("web-profile cache: " + record.Summary);
            }
            try
            {
                var stateDir = StagePreload(isolated, preload);
                // 能力 A：先对生产缓存做只读盘点；目录不存在也如实记录为 absent，不伪造“已落盘”。
                EmitCache(InspectProductionCompileCache(_layout));

                // 多轮独立冷启动：每轮都面对空的 node-compile-cache；最后一轮 flush 后保留给 probe 与 warm 复用。
                Directory.CreateDirectory(isolated.NodeCompileCacheDir);
                for (var c = 1; c <= coldRounds; c++)
                {
                    if (c > 1)
                    {
                        // 不留上一轮的缓存文件，否则本轮就不是冷启动。
                        if (Directory.Exists(isolated.NodeCompileCacheDir))
                        {
                            Directory.Delete(isolated.NodeCompileCacheDir, recursive: true);
                        }
                        Directory.CreateDirectory(isolated.NodeCompileCacheDir);
                    }
                    var cold = await RunOneAsync(
                        WebProfileExperiments.All[0], c, isolated, stateDir, preload, profilesDir,
                        cacheMode: "cold", debugCompileCache: false, flushOnStop: true, ct).ConfigureAwait(false);
                    var coldInventory = CompileCacheInventory.Inspect(isolated.NodeCompileCacheDir);
                    EmitCache(CompileCacheRecord.ForCold(cold.Record, coldInventory, cold.Flush, cold.Record.StartedAt, c));
                }

                // 单独的命中率采样轮：开启 NODE_DEBUG_NATIVE，但不进入 E5 计时样本，避免 stderr 输出污染 httpReadyMs / CPU。
                var probe = await RunOneAsync(
                    WebProfileExperiments.All[0], 1, isolated, stateDir, preload, profilesDir,
                    cacheMode: "probe", debugCompileCache: true, flushOnStop: false, ct).ConfigureAwait(false);
                var probeInventory = CompileCacheInventory.Inspect(isolated.NodeCompileCacheDir);
                var debugCounts = probe.Debug?.Counts() ?? new CompileCacheDebugCounts(0, 0, 0, 0, 0, 0, null);
                EmitCache(CompileCacheRecord.ForProbe(probe.Record, probeInventory, debugCounts, probe.Record.StartedAt, 1));

                // 热缓存计时轮：不开启 NODE_DEBUG_NATIVE；OFF/PROFILE 按参数交错。
                for (var i = 1; i <= reps; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var order = i % 2 == 1
                        ? new[] { WebProfileExperiments.Off, WebProfileExperiments.Profile }
                        : new[] { WebProfileExperiments.Profile, WebProfileExperiments.Off };
                    foreach (var id in order)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!WebProfileExperiments.TryGet(id, out var variant))
                        {
                            continue;
                        }
                        var run = await RunOneAsync(
                            variant, i, isolated, stateDir, preload, profilesDir,
                            cacheMode: "warm", debugCompileCache: false, flushOnStop: false, ct).ConfigureAwait(false);
                        Emit(run.Record);
                    }
                }
                return new ExperimentSuiteResult(true, "ok", $"{runs} E5 record(s), {failures} not ok", runs, failures);
            }
            finally
            {
                isolated.Dispose();
                TryDeleteTree(root);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 构造与当前生产 <see cref="ProotCommandBuilder.DshCommand"/> 一致的 E5 argv：生产完整前缀 + --preserve-symlinks +
    /// preload/link/startup-fs-cache 三个 import + bin.js --profile web --patch ... --no-open。仅固定诊断代码调用。
    /// </summary>
    internal IReadOnlyList<string> BuildArgv(IsolatedGuestLayout isolated)
    {
        ArgumentNullException.ThrowIfNull(isolated);
        var argv = new List<string>(_builder.DiagnosticPrefix(isolated.WorkspaceDir, isolated.CacheDir))
        {
            "--cwd=/root/projects",
            "--kill-on-exit",
            StartupExperiments.Node,
            "--expose-internals",
            "--preserve-symlinks",
            "--import",
            WebProfileExperiments.GuestDir + "/" + WebProfileExperiments.PreloadName,
        };
        if (File.Exists(_layout.ShimFile))
        {
            argv.Add("--import");
            argv.Add(RuntimeConstants.LinkShimGuestPath);
        }
        argv.Add("--import");
        argv.Add(GuestAssetProvisioner.GuestStartupFsCacheShim);
        argv.Add(RuntimeConstants.GuestDshBin);
        argv.Add("--profile");
        argv.Add(RuntimeConstants.DshWebProfile);
        argv.Add("--patch");
        argv.Add(GuestAssetProvisioner.GuestWebTrimPatch);
        argv.Add("--no-open");
        return argv;
    }

    /// <summary>
    /// 生产同口径 env：隔离 <c>NODE_COMPILE_CACHE</c>、ADR-0030 的只读前缀缓存三个键；只有命中率采样轮才加
    /// <c>NODE_DEBUG_NATIVE=COMPILE_CACHE</c>，只有冷启动轮才要求 preload 在 stop 时主动 flush。
    /// </summary>
    internal IReadOnlyDictionary<string, string> BuildEnv(
        IsolatedGuestLayout isolated,
        WebProfileVariant variant,
        bool debugCompileCache,
        bool flushOnStop)
    {
        ArgumentNullException.ThrowIfNull(isolated);
        ArgumentNullException.ThrowIfNull(variant);
        var env = new Dictionary<string, string>(_builder.Environment("dsh"), StringComparer.Ordinal)
        {
            ["DSHM_PROFILE_DIR"] = WebProfileExperiments.GuestDir,
            ["DSHM_PROFILE_MODE"] = variant.Mode,
            ["NODE_COMPILE_CACHE"] = WebProfileExperiments.NodeCompileCacheGuest,
            ["PROOT_RO_CACHE_PREFIXES"] = ProotCommandBuilder.ReadonlyPrefixCachePaths,
            ["PROOT_RO_CACHE_NONEXIST"] = "0",
            ["PROOT_RO_CACHE_NONEXIST_PREFIXES"] = ProotCommandBuilder.ReadonlyNonexistCachePaths,
        };
        if (flushOnStop)
        {
            env["DSHM_COMPILE_CACHE_FLUSH_ON_STOP"] = "1";
        }
        else
        {
            env.Remove("DSHM_COMPILE_CACHE_FLUSH_ON_STOP");
        }
        if (debugCompileCache)
        {
            env["NODE_DEBUG_NATIVE"] = CompileCacheDebugAggregator.DebugFlag;
        }
        else
        {
            env.Remove("NODE_DEBUG_NATIVE");
        }
        return env;
    }

    private async Task<RunOneResult> RunOneAsync(
        WebProfileVariant variant,
        int iteration,
        IsolatedGuestLayout isolated,
        string stateDir,
        byte[] preload,
        string profilesDir,
        string cacheMode,
        bool debugCompileCache,
        bool flushOnStop,
        CancellationToken ct)
    {
        _ = preload;
        var runId = StartupTrace.NewRunId();
        var startedAt = DateTimeOffset.UtcNow;
        foreach (var name in new[]
                 {
                     WebProfileExperiments.StopRequest,
                     WebProfileExperiments.Done,
                     WebProfileExperiments.Summary,
                     WebProfileExperiments.ProfileFile,
                     WebProfileExperiments.CompileCacheFlush,
                 })
        {
            TryDelete(Path.Combine(stateDir, name));
        }

        var env = BuildEnv(isolated, variant, debugCompileCache, flushOnStop);
        var argv = BuildArgv(isolated);
        var psi = StartInfo(argv, env);
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            _log($"web-profile {variant.Id}: spawn failed: {e.GetType().Name}");
            var failed = Record(variant, runId, iteration, startedAt, null, null, null, null, null, null, null, null,
                ExperimentRecord.Failed, null, false) with
            {
                CacheMode = cacheMode,
                CompileCacheFileCount = 0,
                CompileCacheBytes = 0,
            };
            return new RunOneResult(failed, null, null);
        }
        var runClock = Stopwatch.GetTimestamp();
        double? httpReadyMs = null;
        var timedOut = false;
        double? captureMs = null;
        WebProfileSummary? summary = null;
        CompileCacheFlushSummary? flush = null;
        var aggregator = debugCompileCache ? new CompileCacheDebugAggregator() : null;
        var doneSeen = false;
        var profileCopied = false;
        using (process)
        {
            process.StandardInput.Close();
            var stdout = PumpAsync(process.StandardOutput, _ => { });
            var stderr = aggregator is null
                ? PumpAsync(process.StandardError, _ => { })
                : PumpAsync(process.StandardError, aggregator.Observe);
            try
            {
                var deadline = TimeSpan.FromSeconds(120);
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    if (process.HasExited)
                    {
                        break;
                    }
                    var health = await HttpHealthProbe.CheckAsync(
                        connectTimeout: TimeSpan.FromMilliseconds(250),
                        readTimeout: TimeSpan.FromMilliseconds(600),
                        ct: ct).ConfigureAwait(false);
                    if (health.HttpStatus is not null)
                    {
                        httpReadyMs = Stopwatch.GetElapsedTime(runClock).TotalMilliseconds;
                        break;
                    }
                    if (Stopwatch.GetElapsedTime(runClock) > deadline)
                    {
                        timedOut = true;
                        break;
                    }
                    await Task.Delay(PollInterval, ct).ConfigureAwait(false);
                }
                if (httpReadyMs is not null)
                {
                    var requestClock = Stopwatch.GetTimestamp();
                    await File.WriteAllTextAsync(Path.Combine(stateDir, WebProfileExperiments.StopRequest), "stop", ct).ConfigureAwait(false);
                    var donePath = Path.Combine(stateDir, WebProfileExperiments.Done);
                    var captureDeadline = requestClock + (long)(WebProfileExperiments.CaptureTimeout.TotalSeconds * Stopwatch.Frequency);
                    while (!File.Exists(donePath) && Stopwatch.GetTimestamp() < captureDeadline)
                    {
                        ct.ThrowIfCancellationRequested();
                        await Task.Delay(25, ct).ConfigureAwait(false);
                    }
                    doneSeen = File.Exists(donePath);
                    captureMs = Stopwatch.GetElapsedTime(requestClock).TotalMilliseconds;
                    if (doneSeen)
                    {
                        summary = ReadSummary(Path.Combine(stateDir, WebProfileExperiments.Summary));
                        flush = ReadFlush(Path.Combine(stateDir, WebProfileExperiments.CompileCacheFlush));
                        if (variant.Mode == "profile" && TryCopyProfile(stateDir, profilesDir, runId))
                        {
                            profileCopied = true;
                        }
                    }
                }
            }
            finally
            {
                Kill(process);
                await WaitExitAsync(process, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            var portFree = WaitPortClosed(RuntimeConstants.DshPort, PortCloseWait);
            if (!portFree)
            {
                _log($"web-profile {variant.Id}: port {RuntimeConstants.DshPort} still open after the run");
            }
            var (result, error) = Outcome(variant, httpReadyMs, timedOut, doneSeen, summary, profileCopied);
            if (!portFree && string.Equals(result, ExperimentRecord.Ok, StringComparison.Ordinal))
            {
                result = ExperimentRecord.Failed;
            }
            var cache = CompileCacheInventory.Count(isolated.NodeCompileCacheDir);
            var record = Record(variant, runId, iteration, startedAt, httpReadyMs, captureMs,
                summary?.ProfileSampleCount, summary?.NodeUserCpuMs, summary?.NodeSystemCpuMs,
                summary?.EventLoopActiveMs, summary?.EventLoopIdleMs, summary?.StopLatencyMs,
                result, error, profileCopied) with
            {
                CacheMode = cacheMode,
                CompileCacheFileCount = cache.Files,
                CompileCacheBytes = cache.Bytes,
            };
            return new RunOneResult(record, flush, aggregator);
        }
    }

    private static (string Result, string? Error) Outcome(
        WebProfileVariant variant,
        double? readyMs,
        bool timedOut,
        bool doneSeen,
        WebProfileSummary? summary,
        bool profileCopied)
    {
        if (readyMs is null)
        {
            return (timedOut ? ExperimentRecord.Timeout : ExperimentRecord.Failed, null);
        }
        if (!doneSeen)
        {
            return (ExperimentRecord.Failed, WebProfileExperiments.ProfileTimeout);
        }
        if (summary is null)
        {
            return (ExperimentRecord.Failed, WebProfileExperiments.ProfileWriteFailed);
        }
        if (summary.Error is { } error)
        {
            return (ExperimentRecord.Failed, error);
        }
        if (variant.Mode == "profile" && !profileCopied)
        {
            return (ExperimentRecord.Failed, WebProfileExperiments.ProfileEmpty);
        }
        return (ExperimentRecord.Ok, null);
    }

    private static bool TryCopyProfile(string stateDir, string profilesDir, string runId)
    {
        var source = Path.Combine(stateDir, WebProfileExperiments.ProfileFile);
        try
        {
            var info = new FileInfo(source);
            if (!info.Exists || info.Length < 2)
            {
                return false;
            }
            using (var probe = File.OpenRead(source))
            {
                var first = probe.ReadByte();
                if (first != '{')
                {
                    return false;
                }
            }
            Directory.CreateDirectory(profilesDir);
            var destination = Path.Combine(profilesDir, runId + ".cpuprofile");
            File.Copy(source, destination, overwrite: true);
            try
            {
                // The app's own files are 0600; make the copy group-readable so a developer can adb pull it from the
                // app's external files dir (the same mode the JSONL records get). Never widens to world-readable.
                File.SetUnixFileMode(destination,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static WebProfileSummary? ReadSummary(string path)
    {
        try
        {
            return File.Exists(path) ? WebProfileSummary.TryParse(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static CompileCacheFlushSummary? ReadFlush(string path)
    {
        try
        {
            return File.Exists(path) ? CompileCacheFlushSummary.TryParse(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static WebProfileRecord Record(
        WebProfileVariant variant,
        string runId,
        int iteration,
        DateTimeOffset startedAt,
        double? httpReadyMs,
        double? captureMs,
        int? sampleCount,
        double? userCpuMs,
        double? systemCpuMs,
        double? eluActiveMs,
        double? eluIdleMs,
        double? stopLatencyMs,
        string result,
        string? error,
        bool profileStored) =>
        new()
        {
            Variant = variant.Id,
            RunId = runId,
            Iteration = iteration,
            HttpReadyMs = httpReadyMs,
            ProfileCaptureMs = captureMs,
            ProfileSampleCount = sampleCount,
            NodeUserCpuMs = userCpuMs,
            NodeSystemCpuMs = systemCpuMs,
            EventLoopActiveMs = eluActiveMs,
            EventLoopIdleMs = eluIdleMs,
            StopLatencyMs = stopLatencyMs,
            Result = result,
            ErrorCode = error,
            ProfileStoredPrivately = profileStored,
            StartedAt = startedAt,
        };

    private static ProcessStartInfo StartInfo(IReadOnlyList<string> argv, IReadOnlyDictionary<string, string> env)
    {
        var psi = new ProcessStartInfo(argv[0])
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in argv.Skip(1))
        {
            psi.ArgumentList.Add(argument);
        }
        foreach (var (key, value) in env)
        {
            psi.Environment[key] = value;
        }
        return psi;
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                onLine(line);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }
    }

    private static async Task WaitExitAsync(Process process, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or InvalidOperationException)
        {
        }
    }

    private bool WaitPortClosed(int port, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (!_portOpen(port))
            {
                return true;
            }
            Thread.Sleep(50);
        }
        return !_portOpen(port);
    }

    private static bool PortOpen(string host, int port, TimeSpan timeout)
    {
        using var client = new TcpClient();
        try
        {
            var task = client.ConnectAsync(host, port);
            return task.Wait(timeout) && client.Connected;
        }
        catch (Exception e) when (e is SocketException or AggregateException or OperationCanceledException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteTree(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record RunOneResult(
        WebProfileRecord Record,
        CompileCacheFlushSummary? Flush,
        CompileCacheDebugAggregator? Debug);
}
