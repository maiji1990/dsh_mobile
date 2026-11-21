using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using DshMobile.Core.Diagnostics;
using DshMobile.Runtime.Guest;
using DshMobile.Runtime.Supervision;

namespace DshMobile.Runtime.Diagnostics;

/// <summary>Outcome of one diagnostic suite run (no record emitted when it never started).</summary>
public sealed record ExperimentSuiteResult(bool Ran, string Code, string Message, int Runs, int Failures);

/// <summary>
/// Runs the fixed E1-E4 experiments (docs/STARTUP_EXPERIMENTS.md) - observability only. Nothing here is reachable from
/// production start/stop: it refuses while DSH runs or port 3080 is busy, uses only the variant table, binds isolated
/// directories for E3/E4, kills only its own process tree, and deletes the experiment tree on the way out.
/// </summary>
public sealed class StartupExperimentRunner
{
    public const string Busy = "EXPERIMENT_BUSY";
    public const string DshRunning = "EXPERIMENT_DSH_RUNNING";
    public const string PortBusy = "EXPERIMENT_PORT_BUSY";
    public const string UnknownSuite = "EXPERIMENT_UNKNOWN_SUITE";
    public const string Cancelled = "EXPERIMENT_CANCELLED";

    private const string CompileCacheGuest = RuntimeConstants.GuestCacheDir + "/experiments/node-cache";
    private static readonly TimeSpan PortPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan PortCloseWait = TimeSpan.FromSeconds(3);

    private readonly RuntimeLayout _layout;
    private readonly Action<string> _log;
    private readonly Func<bool> _productionRunning;
    private readonly Func<int, bool> _portOpen;
    private readonly ProotCommandBuilder _builder;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public StartupExperimentRunner(
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

    /// <summary>Runs one suite and emits every record to <paramref name="sink"/> (the caller persists them).</summary>
    public async Task<ExperimentSuiteResult> RunAsync(string suite, int reps, Action<ExperimentRecord> sink, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(suite);
        ArgumentNullException.ThrowIfNull(sink);
        if (reps is < 1 or > 50)
        {
            return new ExperimentSuiteResult(false, "EXPERIMENT_BAD_REPS", "reps must be 1..50", 0, 0);
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
            var root = Path.Combine(_layout.FilesDir, "startup-experiments", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(root);
            var runs = 0;
            var failures = 0;
            void Emit(ExperimentRecord r)
            {
                runs++;
                if (!string.Equals(r.Result, ExperimentRecord.Ok, StringComparison.Ordinal))
                {
                    failures++;
                }
                sink(r);
            }
            try
            {
                switch (suite)
                {
                    case StartupExperiments.SuiteE1E2:
                        await RunE1E2Async(reps, root, Emit, ct).ConfigureAwait(false);
                        break;
                    case StartupExperiments.SuiteE2:
                        await RunVariantGroupAsync(StartupExperiments.For("E2"), reps, Emit, ct).ConfigureAwait(false);
                        break;
                    case StartupExperiments.SuiteE2Config:
                        await RunVariantGroupAsync(StartupExperiments.For("E2").Where(v => v.Id == "E2-3").ToList(), reps, Emit, ct).ConfigureAwait(false);
                        break;
                    case StartupExperiments.SuiteE3:
                        await RunE3Async(reps, root, Emit, ct).ConfigureAwait(false);
                        break;
                    case StartupExperiments.SuiteE4:
                        await RunE4Async(reps, root, Emit, ct).ConfigureAwait(false);
                        break;
                    case StartupExperiments.SuiteE6:
                        await RunE6Async(reps, root, Emit, ct).ConfigureAwait(false);
                        break;
                    case StartupExperiments.SuiteE7:
                        await RunE7Async(reps, root, Emit, ct).ConfigureAwait(false);
                        break;
                    default:
                        return new ExperimentSuiteResult(false, UnknownSuite, "unknown suite: " + suite, 0, 0);
                }
                return new ExperimentSuiteResult(true, "ok", $"{runs} record(s), {failures} not ok", runs, failures);
            }
            finally
            {
                TryDeleteTree(root);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------ suites

    private async Task RunE1E2Async(int reps, string root, Action<ExperimentRecord> emit, CancellationToken ct)
    {
        await RunVariantGroupAsync(StartupExperiments.For("E1"), reps, emit, ct).ConfigureAwait(false);
        await RunVariantGroupAsync(StartupExperiments.For("E2"), reps, emit, ct).ConfigureAwait(false);
        _ = root;
    }

    private async Task RunVariantGroupAsync(IReadOnlyList<StartupExperimentVariant> variants, int reps, Action<ExperimentRecord> emit, CancellationToken ct)
    {
        foreach (var variant in variants)
        {
            for (var warm = 0; warm < 2; warm++)
            {
                ct.ThrowIfCancellationRequested();
                await RunExitAsync(variant, 0, ct).ConfigureAwait(false);
            }
        }
        for (var i = 1; i <= reps; i++)
        {
            foreach (var variant in variants)
            {
                ct.ThrowIfCancellationRequested();
                emit(await RunExitAsync(variant, i, ct).ConfigureAwait(false));
            }
        }
    }

    private async Task RunE3Async(int reps, string root, Action<ExperimentRecord> emit, CancellationToken ct)
    {
        var order = new[] { "E3-P", "E3-0", "E3-1" };
        for (var i = 1; i <= reps; i++)
        {
            foreach (var id in order)
            {
                ct.ThrowIfCancellationRequested();
                if (!StartupExperiments.TryGet(id, out var variant))
                {
                    continue;
                }
                IsolatedGuestLayout? isolated = null;
                try
                {
                    IReadOnlyList<string> overlays = [];
                    if (variant.IsolatedWorkspace)
                    {
                        isolated = IsolatedGuestLayout.Create(_layout, Path.Combine(root, $"iter-{i}-{variant.Id}"));
                        if (variant.IncludeRegisteredOverlays)
                        {
                            overlays = isolated.CopyRegisteredOverlays(_layout);
                        }
                    }
                    emit(await RunWebAsync(variant, i, isolated, overlays, cacheHost: null, cacheMode: "unknown", ct).ConfigureAwait(false));
                }
                finally
                {
                    isolated?.Dispose();
                }
            }
        }
    }

    private async Task RunE4Async(int reps, string root, Action<ExperimentRecord> emit, CancellationToken ct)
    {
        var cacheHost = Path.Combine(root, "node-cache");
        Directory.CreateDirectory(cacheHost);

        // E4-A: no NODE_COMPILE_CACHE at all.
        if (StartupExperiments.TryGet("E4-A", out var off))
        {
            for (var i = 1; i <= reps; i++)
            {
                ct.ThrowIfCancellationRequested();
                using var isolated = IsolatedGuestLayout.Create(_layout, Path.Combine(root, $"A-{i}"));
                emit(await RunWebAsync(off, i, isolated, [], cacheHost: null, cacheMode: "off", ct).ConfigureAwait(false));
            }
        }

        // E4-B: first run against an empty cache directory, then count what Node actually flushed.
        int coldFiles;
        long coldBytes;
        if (StartupExperiments.TryGet("E4-B", out var cold))
        {
            ResetDirectory(cacheHost);
            using var isolated = IsolatedGuestLayout.Create(_layout, Path.Combine(root, "B-1"));
            var before = CountFiles(cacheHost);
            emit(await RunWebAsync(cold, 1, isolated, [], cacheHost, "cold", ct, before.Count, before.Bytes).ConfigureAwait(false));
            var after = CountFiles(cacheHost);
            coldFiles = after.Count;
            coldBytes = after.Bytes;
            _log($"experiment E4-B: cache files after exit = {coldFiles}, bytes = {coldBytes}");
        }
        else
        {
            coldFiles = 0;
            coldBytes = 0;
        }

        // E4-C: prewarm the compile cache with a short CLI that exits normally (the Web process is killed, so it cannot
        // flush by itself), then verify files exist before claiming anything about a warm start.
        if (StartupExperiments.TryGet("E4-C", out var warm))
        {
            if (StartupExperiments.TryGet("E2-1", out var prewarm))
            {
                await RunExitAsync(prewarm, 0, ct, cacheHost).ConfigureAwait(false);
            }
            var warmFiles = CountFiles(cacheHost);
            if (warmFiles.Count == 0)
            {
                using var isolated = IsolatedGuestLayout.Create(_layout, Path.Combine(root, "C-0"));
                emit(await RunWebAsync(warm, 1, isolated, [], cacheHost, "warm", ct, 0, 0, "CACHE_EMPTY").ConfigureAwait(false));
                _log("experiment E4-C: not-evaluable, the compile cache did not persist");
                _ = coldFiles;
                _ = coldBytes;
                return;
            }
            for (var i = 1; i <= reps; i++)
            {
                ct.ThrowIfCancellationRequested();
                using var isolated = IsolatedGuestLayout.Create(_layout, Path.Combine(root, $"C-{i}"));
                emit(await RunWebAsync(warm, i, isolated, [], cacheHost, "warm", ct, warmFiles.Count, warmFiles.Bytes).ConfigureAwait(false));
            }
        }
    }

    private async Task RunE6Async(int reps, string root, Action<ExperimentRecord> emit, CancellationToken ct)
    {
        var order = new[] { "E6-0", "E6-1", "E6-2", "E6-3" };
        for (var i = 1; i <= reps; i++)
        {
            foreach (var id in order)
            {
                ct.ThrowIfCancellationRequested();
                if (!StartupExperiments.TryGet(id, out var variant))
                {
                    continue;
                }
                using var isolated = IsolatedGuestLayout.Create(_layout, Path.Combine(root, $"iter-{i}-{variant.Id}"));
                emit(await RunWebAsync(variant, i, isolated, [], cacheHost: null, cacheMode: "unknown", ct).ConfigureAwait(false));
            }
        }
    }

    private async Task RunE7Async(int reps, string root, Action<ExperimentRecord> emit, CancellationToken ct)
    {
        var order = new[] { "E7-0", "E7-1", "E7-2", "E7-3", "E7-4" };
        for (var i = 1; i <= reps; i++)
        {
            foreach (var id in order)
            {
                ct.ThrowIfCancellationRequested();
                if (!StartupExperiments.TryGet(id, out var variant))
                {
                    continue;
                }
                using var isolated = IsolatedGuestLayout.Create(_layout, Path.Combine(root, $"iter-{i}-{variant.Id}"));
                emit(await RunWebAsync(variant, i, isolated, [], cacheHost: null, cacheMode: "unknown", ct).ConfigureAwait(false));
            }
        }
    }

    // ------------------------------------------------------------------ one process

    private async Task<ExperimentRecord> RunExitAsync(StartupExperimentVariant variant, int iteration, CancellationToken ct, string? cacheHost = null)
    {
        var runId = StartupTrace.NewRunId();
        var startedAt = DateTimeOffset.UtcNow;
        var argv = BuildExitArgv(variant, cacheHost);
        var env = new Dictionary<string, string>(_builder.Environment("dsh"), StringComparer.Ordinal);
        if (cacheHost is not null)
        {
            env["NODE_COMPILE_CACHE"] = CompileCacheGuest;
        }
        var psi = StartInfo(argv, env);
        Process process;
        var spawnClock = Stopwatch.GetTimestamp();
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            return Record(variant, runId, iteration, startedAt, ExperimentRecord.Failed, note: "SPAWN", durationMs: null, spawnMs: null,
                markerMs: null, readyMs: null, status: null, exitCode: null, timedOut: false, cacheMode: variant.CacheMode, cacheFiles: null,
                cacheBytes: null);
        }
        var spawnMs = Stopwatch.GetElapsedTime(spawnClock).TotalMilliseconds;
        var runClock = Stopwatch.GetTimestamp();
        double? markerMs = null;
        long markerTicks = 0;
        var markerSeen = 0;
        var timedOut = false;
        using (process)
        {
            process.StandardInput.Close();
            var stdout = PumpAsync(process.StandardOutput, line =>
            {
                if (variant.Marker is { } marker && Interlocked.Exchange(ref markerSeen, 1) == 0
                    && line.Contains(marker, StringComparison.Ordinal))
                {
                    Volatile.Write(ref markerTicks, Stopwatch.GetTimestamp());
                }
            });
            var stderr = PumpAsync(process.StandardError, _ => { });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(variant.TimeoutSeconds));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                await WaitExitAsync(process, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                ct.ThrowIfCancellationRequested();
                timedOut = true;
            }
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (markerTicks != 0)
            {
                markerMs = Stopwatch.GetElapsedTime(runClock, Volatile.Read(ref markerTicks)).TotalMilliseconds;
            }
            int? code = process.HasExited ? process.ExitCode : null;
            var duration = Stopwatch.GetElapsedTime(runClock).TotalMilliseconds;
            var result = timedOut ? ExperimentRecord.Timeout : code == 0 ? ExperimentRecord.Ok : ExperimentRecord.Failed;
            return Record(variant, runId, iteration, startedAt, result, timedOut ? "TIMEOUT" : code == 0 ? null : "EXIT",
                duration, spawnMs, markerMs, null, null, code, timedOut, variant.CacheMode, cacheHost is null ? null : CountFiles(cacheHost).Count,
                cacheHost is null ? null : CountFiles(cacheHost).Bytes);
        }
    }

    private async Task<ExperimentRecord> RunWebAsync(
        StartupExperimentVariant variant,
        int iteration,
        IsolatedGuestLayout? isolated,
        IReadOnlyList<string> overlays,
        string? cacheHost,
        string cacheMode,
        CancellationToken ct,
        int? cacheFiles = null,
        long? cacheBytes = null,
        string? note = null)
    {
        var runId = StartupTrace.NewRunId();
        var startedAt = DateTimeOffset.UtcNow;
        var argv = BuildWebArgv(variant, isolated, overlays);
        var env = BuildWebEnv(variant, cacheHost);
        var psi = StartInfo(argv, env);
        Process process;
        var spawnClock = Stopwatch.GetTimestamp();
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            return Record(variant, runId, iteration, startedAt, ExperimentRecord.Failed, note: "SPAWN", durationMs: null, spawnMs: null,
                markerMs: null, readyMs: null, status: null, exitCode: null, timedOut: false, cacheMode: cacheMode, cacheFiles: cacheFiles,
                cacheBytes: cacheBytes);
        }
        var spawnMs = Stopwatch.GetElapsedTime(spawnClock).TotalMilliseconds;
        var runClock = Stopwatch.GetTimestamp();
        double? readyMs = null;
        int? status = null;
        var timedOut = false;
        var exitEarly = false;
        using (process)
        {
            process.StandardInput.Close();
            var stdout = PumpAsync(process.StandardOutput, _ => { });
            var stderr = PumpAsync(process.StandardError, _ => { });
            try
            {
                var deadline = TimeSpan.FromSeconds(variant.TimeoutSeconds);
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    if (process.HasExited)
                    {
                        exitEarly = true;
                        break;
                    }
                    var health = await HttpHealthProbe.CheckAsync(connectTimeout: TimeSpan.FromMilliseconds(250), readTimeout: TimeSpan.FromMilliseconds(600), ct: ct).ConfigureAwait(false);
                    if (health.HttpStatus is { } s)
                    {
                        status = s;
                        readyMs = Stopwatch.GetElapsedTime(runClock).TotalMilliseconds;
                        break;
                    }
                    if (Stopwatch.GetElapsedTime(runClock) > deadline)
                    {
                        timedOut = true;
                        break;
                    }
                    await Task.Delay(PortPollInterval, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                Kill(process);
                await WaitExitAsync(process, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            var duration = Stopwatch.GetElapsedTime(runClock).TotalMilliseconds;
            var portFree = WaitPortClosed(RuntimeConstants.DshPort, PortCloseWait);
            int? code = process.HasExited ? process.ExitCode : null;
            var result = readyMs is not null && portFree
                ? ExperimentRecord.Ok
                : timedOut ? ExperimentRecord.Timeout : ExperimentRecord.Failed;
            var reason = note ?? (portFree ? timedOut ? "TIMEOUT" : exitEarly ? "EXIT" : null : "PORT_LEAK");
            return Record(variant, runId, iteration, startedAt, result, reason, duration, spawnMs, null, readyMs, status, code, timedOut,
                cacheMode, cacheFiles, cacheBytes);
        }
    }

    // ------------------------------------------------------------------ argv / process helpers

    private IReadOnlyList<string> BuildExitArgv(StartupExperimentVariant variant, string? cacheHost)
    {
        var argv = new List<string>(_builder.DiagnosticPrefix(_layout.UserDataDir, cacheHost ?? _layout.GuestCacheDir))
        {
            "--cwd=/root/projects",
            "--kill-on-exit",
            variant.Program,
        };
        argv.AddRange(variant.Arguments);
        return argv;
    }

    internal IReadOnlyList<string> BuildWebArgv(StartupExperimentVariant variant, IsolatedGuestLayout? isolated, IReadOnlyList<string> overlays)
    {
        if (!variant.IsolatedWorkspace)
        {
            return _builder.DshCommand();
        }
        var argv = new List<string>(_builder.DiagnosticPrefix(isolated!.WorkspaceDir, isolated.CacheDir))
        {
            "--cwd=/root/projects",
            "--kill-on-exit",
            StartupExperiments.Node,
            "--expose-internals",
        };
        if (variant.PreserveSymlinks)
        {
            argv.Add("--preserve-symlinks");
        }
        if (File.Exists(_layout.ShimFile))
        {
            argv.Add("--import");
            argv.Add(RuntimeConstants.LinkShimGuestPath);
        }
        if (variant.StartupFsCacheShim)
        {
            argv.Add("--import");
            argv.Add(GuestAssetProvisioner.GuestStartupFsCacheShim);
        }
        argv.Add(RuntimeConstants.GuestDshBin);
        argv.Add("--profile");
        argv.Add(RuntimeConstants.DshWebProfile);
        foreach (var patch in overlays)
        {
            argv.Add("--patch");
            argv.Add(patch);
        }
        return argv;
    }

    /// <summary>按固定实验变体构造 Web 诊断环境变量：显式控制 PRoot P1 opt-in 开关，避免与外部默认值混淆。</summary>
    internal Dictionary<string, string> BuildWebEnv(StartupExperimentVariant variant, string? cacheHost)
    {
        var env = new Dictionary<string, string>(_builder.Environment("dsh"), StringComparer.Ordinal);
        if (cacheHost is not null)
        {
            env["NODE_COMPILE_CACHE"] = CompileCacheGuest;
        }
        if (variant.ProotSkipCloseIntercept)
        {
            env["PROOT_NO_CLOSE_INTERCEPT"] = "1";
        }
        else
        {
            env.Remove("PROOT_NO_CLOSE_INTERCEPT");
        }
        if (variant.ProotReadonlyPrefixCache)
        {
            env["PROOT_RO_CACHE_PREFIXES"] = StartupExperiments.ProotReadonlyCachePrefixes;
        }
        else
        {
            env.Remove("PROOT_RO_CACHE_PREFIXES");
        }
        return env;
    }

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

    private static ExperimentRecord Record(
        StartupExperimentVariant variant,
        string runId,
        int iteration,
        DateTimeOffset startedAt,
        string result,
        string? note,
        double? durationMs,
        double? spawnMs,
        double? markerMs,
        double? readyMs,
        int? status,
        int? exitCode,
        bool timedOut,
        string cacheMode,
        int? cacheFiles,
        long? cacheBytes) =>
        new()
        {
            Experiment = variant.Experiment,
            Variant = variant.Id,
            Kind = variant.Kind,
            RunId = runId,
            Iteration = iteration,
            DurationMs = durationMs,
            SpawnMs = spawnMs,
            GuestFirstMarkerMs = markerMs,
            HttpReadyMs = readyMs,
            HttpStatus = status,
            ExitCode = exitCode,
            TimedOut = timedOut,
            Result = result,
            CacheMode = cacheMode,
            CacheFileCount = cacheFiles,
            CacheBytes = cacheBytes,
            StartedAt = startedAt,
            Note = note,
        };

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

    private static (int Count, long Bytes) CountFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return (0, 0);
        }
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToList();
        return (files.Count, files.Sum(f => new FileInfo(f).Length));
    }

    private static void ResetDirectory(string directory)
    {
        TryDeleteTree(directory);
        Directory.CreateDirectory(directory);
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
}
