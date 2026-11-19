using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using DshMobile.Core.Diagnostics;
using DshMobile.Runtime.Guest;
using DshMobile.Runtime.Supervision;

namespace DshMobile.Runtime.Diagnostics;

/// <summary>
/// P0 Web-profile diagnostic (docs/WEB_STARTUP_DIAGNOSIS.md): starts the fixed, isolated DSH Web argv with a fixed Node
/// preload that captures a V8 CPU profile in-process, waits for HTTP readiness, then asks the preload to stop through a
/// file in the isolated cache directory and only then tears the process tree down. Diagnostics only: no production code
/// path calls it, no inspector port is opened, and the raw profile is copied to a caller-owned private directory.
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

    /// <summary>Alternating OFF, PROFILE, PROFILE, OFF... so both ends share the same device state.</summary>
    public async Task<ExperimentSuiteResult> RunAsync(
        int reps,
        byte[] preload,
        string profilesDir,
        Action<WebProfileRecord> sink,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(preload);
        ArgumentNullException.ThrowIfNull(profilesDir);
        ArgumentNullException.ThrowIfNull(sink);
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
            Directory.CreateDirectory(root);
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
            try
            {
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
                        Emit(await RunOneAsync(variant, i, root, preload, profilesDir, ct).ConfigureAwait(false));
                    }
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

    private async Task<WebProfileRecord> RunOneAsync(
        WebProfileVariant variant,
        int iteration,
        string root,
        byte[] preload,
        string profilesDir,
        CancellationToken ct)
    {
        var runId = StartupTrace.NewRunId();
        var startedAt = DateTimeOffset.UtcNow;
        using var isolated = IsolatedGuestLayout.Create(_layout, Path.Combine(root, $"iter-{iteration}-{variant.Id}"));
        var stateDir = StagePreload(isolated, preload);
        foreach (var name in new[] { WebProfileExperiments.StopRequest, WebProfileExperiments.Done, WebProfileExperiments.Summary, WebProfileExperiments.ProfileFile })
        {
            TryDelete(Path.Combine(stateDir, name));
        }

        var env = new Dictionary<string, string>(_builder.Environment("dsh"), StringComparer.Ordinal)
        {
            ["DSHM_PROFILE_DIR"] = WebProfileExperiments.GuestDir,
            ["DSHM_PROFILE_MODE"] = variant.Mode,
        };
        var argv = BuildArgv(isolated);
        var psi = StartInfo(argv, env);
        Process process;
        var spawnClock = Stopwatch.GetTimestamp();
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            _log($"web-profile {variant.Id}: spawn failed: {e.GetType().Name}");
            return Record(variant, runId, iteration, startedAt, null, null, null, null, null, null, null, null,
                ExperimentRecord.Failed, null, false);
        }
        var spawnMs = Stopwatch.GetElapsedTime(spawnClock).TotalMilliseconds;
        _ = spawnMs;
        var runClock = Stopwatch.GetTimestamp();
        double? httpReadyMs = null;
        var timedOut = false;
        double? captureMs = null;
        WebProfileSummary? summary = null;
        var doneSeen = false;
        var profileCopied = false;
        using (process)
        {
            process.StandardInput.Close();
            var stdout = PumpAsync(process.StandardOutput);
            var stderr = PumpAsync(process.StandardError);
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
            return Record(variant, runId, iteration, startedAt, httpReadyMs, captureMs,
                summary?.ProfileSampleCount, summary?.NodeUserCpuMs, summary?.NodeSystemCpuMs,
                summary?.EventLoopActiveMs, summary?.EventLoopIdleMs, summary?.StopLatencyMs,
                result, error, profileCopied);
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

    private IReadOnlyList<string> BuildArgv(IsolatedGuestLayout isolated)
    {
        var argv = new List<string>(_builder.DiagnosticPrefix(isolated.WorkspaceDir, isolated.CacheDir))
        {
            "--cwd=/root/projects",
            "--kill-on-exit",
            StartupExperiments.Node,
            "--expose-internals",
            "--import",
            WebProfileExperiments.GuestDir + "/" + WebProfileExperiments.PreloadName,
        };
        if (File.Exists(_layout.ShimFile))
        {
            argv.Add("--import");
            argv.Add(RuntimeConstants.LinkShimGuestPath);
        }
        argv.Add(RuntimeConstants.GuestDshBin);
        argv.Add("--profile");
        argv.Add(RuntimeConstants.DshWebProfile);
        return argv;
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

    private static async Task PumpAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is not null)
            {
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
}
