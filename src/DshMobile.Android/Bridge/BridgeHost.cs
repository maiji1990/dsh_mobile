using System.Diagnostics;
using DshMobile.App.Files;
using DshMobile.App.Ui;
using DshMobile.Application;
using DshMobile.Application.Runtime;
using DshMobile.Application.Ui;
using DshMobile.Core.Contracts;
using DshMobile.Core.Diagnostics;
using DshMobile.Core.Files;
using DshMobile.Native;
using DshMobile.Runtime;
using DshMobile.Runtime.Layers;
using Microsoft.Extensions.DependencyInjection;
using Log = Android.Util.Log;

namespace DshMobile.App.Bridge;

/// <summary>
/// Application-scoped owner of the C# runtime (<see cref="RuntimeCoordinator"/>) and of the operation log. Survives
/// Activity recreation (rotation, background/foreground) so in-flight work is not lost. Everything is C# since PR-10
/// (ADR-0018): the DSHBox bridge, its facade and the legacy fallbacks were removed.
/// </summary>
public sealed class BridgeHost : IDisposable, IDiagnosticsLog
{
    public const string LogTag = "DshMobile";
    private const int MaxLogLines = 400;

    private readonly Android.Content.Context _context;
    private readonly object _logGate = new();
    private readonly LinkedList<string> _log = new();
    private int _busy;
    private string? _operation;
    private OperationOutcome? _lastOutcome;
    private RuntimeProgress? _progress;

    public BridgeHost(Android.Content.Context context)
    {
        _context = context.ApplicationContext ?? context;
        Layout = new RuntimeLayout(_context.FilesDir!.AbsolutePath, _context.CacheDir?.AbsolutePath, _context.ApplicationInfo!.NativeLibraryDir!);
        _services = new ServiceCollection()
            .AddSingleton<IForegroundServiceController>(new AndroidForegroundService(_context))
            .AddSingleton<DshMobile.Runtime.Layers.IBundledAssets>(new AndroidBundledAssets(_context.Assets!))
            .AddSingleton<IDiagnosticsLog>(this)
            .AddDshMobileApplication(Layout)
            .AddDshMobileUpdateCheck(new DshMobile.Application.Updates.UpdateCheckOptions(AppVersion(_context), DeviceAbi()), _http)
            .AddDshMobileUpdateInstall(_downloadHttp)
            .BuildServiceProvider();
        Runtime = _services.GetRequiredService<RuntimeCoordinator>();
        Updates = _services.GetRequiredService<DshMobile.Application.Updates.UpdateChecker>();
        UpdateInstaller = _services.GetRequiredService<DshMobile.Application.Updates.UpdateCoordinator>();
        // An update the previous process did not finish is reported once (its layer was already restored by recovery).
        if (UpdateInstaller.SettleInterrupted() is { } interrupted)
        {
            Append($"update: the previous update of {interrupted.Kind} {interrupted.Version} was interrupted ({interrupted.Message})");
        }
        // ADR-0010: the guest-facing C# Relay stops with the sandbox and the ui_* layer is removed.
        Runtime.SandboxStopping += () =>
        {
            if (Automation.Relay.CsRelay.GuestFacing)
            {
                var removed = Automation.Relay.CsRelay.StopGuest(Layout.UserDataDir);
                Append($"c# relay (guest) stopped with the sandbox; ui_* layer {(removed ? "removed" : "left (not ours)")}");
            }
        };
        // The status line follows the journal.
        Runtime.State.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        var s = Runtime.State.Current;
        Append($"runtime coordinator: impl={RuntimeCoordinator.Implementation} journal={s.State}/{s.Harness} gen={s.Generation}{(s.ErrorCode is null ? "" : " last error " + s.ErrorCode)}");
        // The journal may still say what the previous process saw; align it with the disk.
        _ = Task.Run(async () =>
        {
            var now = await Runtime.RefreshStateAsync(CancellationToken.None).ConfigureAwait(false);
            Append($"runtime (C#): journal={now.State}/{now.Harness}");
        });
    }

    private readonly ServiceProvider _services;

    /// <summary>Anonymous https client for the two release-manifest files (no auth headers; ADR-0022).</summary>
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Read-only release discovery (vNext PR-02). Built-in trust is empty until the release key is pinned.</summary>
    public DshMobile.Application.Updates.UpdateChecker Updates { get; }

    /// <summary>Large artifact downloads: no overall timeout (each download is cancellable), no auth headers.</summary>
    private readonly HttpClient _downloadHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Online DSH layer update (vNext PR-05 / PR-06).</summary>
    public DshMobile.Application.Updates.UpdateCoordinator UpdateInstaller { get; }

    /// <summary>The last update check of this process and its channel (the update UI's rows); null before the first check.</summary>
    public DshMobile.Runtime.Updates.ReleaseDiscoveryResult? LastCheck { get; private set; }

    public string? LastCheckChannel { get; private set; }

    private CancellationTokenSource? _updateCts;

    public RuntimeLayout Layout { get; }

    public RuntimeCoordinator Runtime { get; }

    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    /// <summary>The user-started operation running now (<c>start</c>, <c>runtime.import</c>, …), or null.</summary>
    public string? CurrentOperation => Volatile.Read(ref _operation);

    /// <summary>How the last operation ended, for the UI (ADR-0021); the log keeps the details.</summary>
    public OperationOutcome? LastOutcome => Volatile.Read(ref _lastOutcome);

    /// <summary>Progress of a running import (step and bytes), or null.</summary>
    public RuntimeProgress? Progress => Volatile.Read(ref _progress);

    /// <summary>What the home screen shows: the journal, the running operation and the last outcome.</summary>
    public HomeStatus Status => HomeStatus.From(Runtime.State.Current, CurrentOperation, LastOutcome);

    /// <summary>The journal, with the C# host's state while it owns the processes (ADR-0015).</summary>
    public string RuntimeState
    {
        get
        {
            var s = Runtime.State.Current;
            if (Runtime.HostOwnsProcesses)
            {
                return $"C# host={Runtime.HostState} · runtime={s.State} · dsh={s.Harness}{(s.ErrorCode is null ? "" : " · " + s.ErrorCode)}";
            }
            return $"runtime={s.State} · dsh={s.Harness}{(s.DshVersion is { } v ? " · " + v : "")}{(s.ErrorCode is null ? "" : " · " + s.ErrorCode)}";
        }
    }

    /// <summary>Raised on an arbitrary thread whenever the log or busy state changes.</summary>
    public event EventHandler? Changed;

    public string LogText
    {
        get
        {
            lock (_logGate)
            {
                return string.Join('\n', _log);
            }
        }
    }

    public void ClearLog()
    {
        lock (_logGate)
        {
            _log.Clear();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Device / runtime facts: Android APIs, the native library, the runtime on disk.</summary>
    public Task RunInfoAsync() => RunExclusive("info", async () =>
    {
        var app = _context.PackageManager!.GetPackageInfo(_context.PackageName!, 0);
        Append($"app {_context.PackageName} {app?.VersionName ?? "?"} · .NET {Environment.Version} · runtime impl={RuntimeCoordinator.Implementation}");
        Append($"device sdk={(int)Android.OS.Build.VERSION.SdkInt} abis=[{string.Join(",", Android.OS.Build.SupportedAbis ?? [])}] model={Android.OS.Build.Model}");
        var native = DshNativeLibrary.Probe();
        Append($"native={(native.Ok ? "abi " + native.Value : native.Code)} journal={Layout.JournalFile}");
        if (await Runtime.ReadFactsAsync(CancellationToken.None).ConfigureAwait(false) is { } f)
        {
            Append("runtime (C#): " + f.Describe(Runtime.State.Current.State.ToString(), Runtime.State.Current.Harness.ToString(), Runtime.Host?.TokenRevision is not null));
        }
    });

    /// <summary>Copies a selected document to private cache and imports it under the same operation lock.</summary>
    public Task RunPickedImportAsync(Android.Net.Uri uri, bool dsh)
    {
        var operation = dsh ? HomeStatus.ImportDshOperation : HomeStatus.ImportOperation;
        return RunExclusive(operation, () => Task.Run(async () =>
        {
            DirectoryInfo? staging = null;
            try
            {
                string path;
                try
                {
                    var resolver = _context.ContentResolver!;
                    var name = WorkspaceFileSystem.SanitizeFileName(DocumentPicker.DisplayName(resolver, uri));
                    if (dsh && DshLayerInstaller.VersionFromFileName(name) is null)
                    {
                        Report(false, RuntimeErrorCodes.BadRequest, $"expected <version>.tar.zst or <version>.tar.gz: {name}");
                        return;
                    }

                    staging = Directory.CreateDirectory(Path.Combine(_context.CacheDir!.AbsolutePath, "runtime-import", Guid.NewGuid().ToString("N")));
                    path = Path.Combine(staging.FullName, name);
                    Append($"import: copying {name} from picker ...");
                    await using var input = resolver.OpenInputStream(uri) ?? throw new IOException("cannot open picked file");
                    await using var output = File.Create(path);
                    await input.CopyToAsync(output).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or Java.Lang.Exception)
                {
                    Report(false, RuntimeErrorCodes.BundleUnreadable, $"{e.GetType().Name}: {e.Message}");
                    Append($"import: copy failed: {e.Message}");
                    return;
                }

                if (dsh)
                {
                    await ImportDshAsync(path).ConfigureAwait(false);
                }
                else
                {
                    await ImportRuntimeAsync(path).ConfigureAwait(false);
                }
            }
            finally
            {
                try
                {
                    staging?.Delete(recursive: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Append($"import: could not remove the cache copy: {e.Message}");
                }
            }
        }), reportBusy: true);
    }

    private async Task ImportRuntimeAsync(string path)
    {
        var progress = new Progress<RuntimeProgress>(p =>
        {
            Volatile.Write(ref _progress, p);
            Changed?.Invoke(this, EventArgs.Empty);
        });
        var r = await Runtime.InstallAsync(new RuntimePackage(path), progress, CancellationToken.None).ConfigureAwait(false);
        Report(r.Ok, r.Code, r.Message);
        Append(r.Ok ? "import: ok" : $"import FAILED {r.Code}{(r.UpstreamCode is null ? "" : "/" + r.UpstreamCode)}: {r.Message}");
        await LogStatusAsync("status").ConfigureAwait(false);
    }

    private async Task ImportDshAsync(string path)
    {
        var r = await Runtime.InstallDshFromFileAsync(path, CancellationToken.None).ConfigureAwait(false);
        var detail = r.Ok
            ? Rows.Fmt(_context, r.Changed ? Resource.String.runtime_dsh_import_installed : Resource.String.runtime_dsh_import_kept, r.Version ?? "?")
            : r.Message;
        Report(r.Ok, r.Code, detail);
        Append(r.Ok ? $"dsh import: {(r.Changed ? $"installed {r.Version}" : $"kept {r.Version}")}" : $"dsh import FAILED {r.Code}: {r.Message}");
        await LogStatusAsync("status").ConfigureAwait(false);
    }

    /// <summary>MIGRATION row 18: the C# rollback to the previous runtime body (refused while anything runs).</summary>
    public Task RunRollbackAsync() => RunExclusive("runtime.rollback", async () =>
    {
        var r = await Runtime.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        Report(r.Ok, r.Code, r.Message);
        Append(r.Ok ? $"rollback: ok ({r.Message})" : $"rollback FAILED {r.Code}: {r.Message}");
        await LogStatusAsync("status").ConfigureAwait(false);
    });

    /// <summary>Logs a host-side step (e.g. copying a picked file).</summary>
    public void Note(string line) => Append(line);

    /// <summary>A host-side step of <paramref name="operation"/> that failed before the operation itself could run.</summary>
    public void Fail(string operation, string code, string message)
    {
        Volatile.Write(ref _lastOutcome, new OperationOutcome(operation, false, code, message, DateTimeOffset.Now));
        Append($"{operation} FAILED {code}: {message}");
    }

    /// <summary>
    /// Foreground service, sandbox, then DSH, by the C# host and supervised by <see cref="RuntimeCoordinator"/>: a failed
    /// start is cleaned up and releases the service; a failed stop keeps it (PR-00 defect 3).
    /// </summary>
    public Task RunStartAsync()
    {
        // Observability only (docs/STARTUP_PERF.md): Logcat gets startup-perf records for this run while detailed
        // diagnostics are on; the in-app operation log and every existing message stay exactly as they were.
        var trace = UiPrefs.StartupTraceEnabled(_context) ? StartupTrace.Start(line => Log.Info(LogTag, line)) : StartupTrace.Disabled;
        var clicked = trace.Mark();
        return RunExclusive("start", async () =>
        {
            trace.EmitSpan("ui.click_to_bridge", clicked);
            var r = await Runtime.StartAsync(DshProfile.Web, CancellationToken.None, trace).ConfigureAwait(false);
            Report(r.Ok, r.Code, r.Message);
            var s = r.Snapshot;
            Append(r.Ok
                ? $"start: ok sandbox={s.State} dsh={s.Harness} version={s.DshVersion ?? "-"} gen={s.Generation}"
                : $"start FAILED {r.Code}{(r.UpstreamCode is null ? "" : "/" + r.UpstreamCode)}: {r.Message} (state={s.State} dsh={s.Harness})");
            trace.EmitSpan("bridge.complete", clicked, r.Ok ? StartupTrace.Ok : StartupTrace.Failed,
                r.Ok ? null : new Dictionary<string, object?> { ["code"] = r.Code });
        }, startupTrace: trace);
    }

    public Task RunStopAsync() => RunExclusive("stop", async () =>
    {
        var r = await Runtime.StopAsync(CancellationToken.None).ConfigureAwait(false);
        Report(r.Ok, r.Code, r.Message);
        Append(r.Ok ? $"stop: ok ({r.Snapshot.State})" : $"stop FAILED {r.Code}: {r.Message} (state={r.Snapshot.State})");
    });

    /// <summary>Runtime state, health, the host's own view, then the fixed guest diagnostics (uname/debian/node/dsh --version).</summary>
    public Task RunDiagnosticsAsync() => RunExclusive("diagnostics", async () =>
    {
        await LogStatusAsync("status").ConfigureAwait(false);
        var h = await DshMobile.Runtime.Supervision.HttpHealthProbe.CheckAsync().ConfigureAwait(false);
        Append($"health: port={h.PortOpen} webUi={h.WebUiReady} http={h.HttpStatus?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}{(h.Error is null ? "" : " error=" + h.Error)}");
        foreach (var line in await Runtime.DescribeHostAsync(CancellationToken.None).ConfigureAwait(false))
        {
            Append(line);
        }
        // Each command in its own PRoot, independent of the host.
        foreach (var (key, r) in await Runtime.RunGuestDiagnosticsAsync(CancellationToken.None).ConfigureAwait(false))
        {
            var output = r.Output.Trim();
            Append($"diag {key}: {(r.Ok ? output : "FAILED " + (r.Error ?? $"exit={r.ExitCode}") + (output.Length > 0 ? " | " + output[..Math.Min(output.Length, 400)] : ""))}");
        }
    });

    /// <summary>
    /// vNext PR-02 (read-only): fetches and verifies the channel's signed release manifest and logs what could be
    /// updated. Installs nothing. Without a pinned release key this reports UPDATE_NO_TRUSTED_KEYS and does not go online.
    /// </summary>
    public Task RunUpdateCheckAsync(string channel) => RunExclusive("update.check", async () =>
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var r = await Updates.CheckAsync(channel, cts.Token).ConfigureAwait(false);
        LastCheck = r;
        LastCheckChannel = channel;
        Report(r.Ok, r.Code, r.Message);
        foreach (var line in DshMobile.Application.Updates.UpdateChecker.Describe(channel, r))
        {
            Append(line);
        }
    });

    /// <summary>
    /// vNext PR-05 / PR-06: downloads, verifies and installs the DSH update of the last check (<see cref="LastCheck"/>),
    /// with a health check and automatic restore of the old layer. <paramref name="stopConfirmed"/>: the user agreed that a
    /// running DSH is stopped. Cancellable only while downloading (<see cref="CancelUpdate"/>).
    /// </summary>
    public Task RunDshUpdateAsync(bool stopConfirmed) => RunExclusive("update.install", async () =>
    {
        if (LastCheck is not { } check || LastCheckChannel is not { } channel)
        {
            Report(false, DshMobile.Runtime.Updates.UpdateTransactionCodes.NothingToInstall, "check for updates first");
            return;
        }
        using var cts = new CancellationTokenSource();
        Volatile.Write(ref _updateCts, cts);
        try
        {
            var progress = new Progress<DshMobile.Application.Updates.UpdateProgress>(p =>
            {
                Volatile.Write(ref _progress, new RuntimeProgress(p.Phase, p.Received, p.Total));
                Changed?.Invoke(this, EventArgs.Empty);
            });
            var r = await UpdateInstaller.InstallDshAsync(check, channel, stopConfirmed, progress, cts.Token).ConfigureAwait(false);
            Report(r.Ok, r.Code, r.Message);
            Append(r.Ok ? $"update: {r.Message} (DSH {r.InstalledVersion ?? "-"})" : $"update FAILED {r.Code} at {r.Phase}: {r.Message} (DSH {r.InstalledVersion ?? "-"})");
            await LogStatusAsync("status").ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _updateCts, null);
        }
    });

    /// <summary>Cancels a running update download (later phases are not cancellable and ignore this).</summary>
    public void CancelUpdate()
    {
        try
        {
            Volatile.Read(ref _updateCts)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // the download just ended
        }
    }

    /// <summary>Copyable text: the last check, the planner's notes and the last update record (no secrets).</summary>
    public string UpdateDiagnostics()
    {
        var lines = new List<string> { $"app {AppVersion(_context)} abi {DeviceAbi()}" };
        if (LastCheck is { } c && LastCheckChannel is { } ch)
        {
            lines.AddRange(DshMobile.Application.Updates.UpdateChecker.Describe(ch, c));
            if (c.Ok)
            {
                lines.AddRange(DshMobile.Runtime.Updates.UpdatePlanner.Plan(c.Choices).Notes.Select(n => "  note: " + n));
            }
        }
        else
        {
            lines.Add("update check: not run in this session");
        }
        if (UpdateInstaller.LastState is { } s)
        {
            lines.Add($"last update: {s.Kind} {s.Version} {s.Phase}{(s.Code is null ? "" : " " + s.Code)} at {s.UpdatedAt:O}{(s.Message is null ? "" : " — " + s.Message)}");
        }
        return string.Join('\n', lines);
    }

    private static string AppVersion(Android.Content.Context context) =>
        context.PackageManager?.GetPackageInfo(context.PackageName!, 0)?.VersionName is { Length: > 0 } v ? v : "0.0.0";

    /// <summary>The release-manifest ABI of this device: <c>arm64</c> for arm64-v8a, else the Android ABI name.</summary>
    private static string DeviceAbi() =>
        Android.OS.Build.SupportedAbis is { Count: > 0 } abis ? abis[0] == "arm64-v8a" ? "arm64" : abis[0]! : "unknown";

    /// <summary>
    /// PR-06 developer acceptance: the guest login shell on a C#-owned PTY (libdshmobile_native), driven by PtyProbe.
    /// The steps (prompt, tty, resize, ^C, exit, no orphans) are logged.
    /// </summary>
    public Task RunPtyProbeAsync() => RunExclusive(PtyProbe.Slice, async () =>
    {
        var native = DshMobile.Native.DshNativeLibrary.Probe();
        Append(native.Ok ? $"native: libdshmobile_native ABI {native.Value}" : $"native: {native.Code}: {native.Message}");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var r = await Runtime.ProbePtyAsync(cts.Token).ConfigureAwait(false);
        foreach (var step in r.Steps)
        {
            Append($"pty {step.Name}: {(step.Ok ? "ok" : "FAILED")} ({step.Detail}, {step.Ms} ms)");
        }
    });

    /// <summary>
    /// TASK-005: runs the guest probe for every shipped profile, in order, and logs each check (MIGRATION row 24: the C#
    /// PRoot launcher, <see cref="RuntimeCoordinator.ProbeProfileAsync"/>).
    /// </summary>
    public Task RunProbesAsync() => RunExclusive("dsh.probe", async () =>
    {
        var summary = new List<string>();
        foreach (var profile in DshProfiles.All)
        {
            var sw = Stopwatch.StartNew();
            var o = await Runtime.ProbeProfileAsync(profile, CancellationToken.None).ConfigureAwait(false);
            if (o.Result is not { } r)
            {
                Append($"probe {profile} FAILED {o.ErrorCode}: {o.Error}");
                summary.Add($"{profile}=ERROR");
                continue;
            }
            Append($"probe {profile}: {r.Status.ToUpperInvariant()} shim={r.Shim} ({sw.Elapsed.TotalSeconds:F1} s)");
            foreach (var c in r.Checks)
            {
                Append($"  {(c.Ok ? "ok  " : "FAIL")} {c.Name}{(c.Ms is { } ms ? $" {ms} ms" : "")}: {c.Detail}");
            }
            summary.Add($"{profile}={r.Status}");
        }
        Append("probe summary: " + string.Join(" ", summary));
    });

    /// <summary>The status line, from what C# reads on disk.</summary>
    private async Task LogStatusAsync(string what)
    {
        if (await Runtime.ReadFactsAsync(CancellationToken.None).ConfigureAwait(false) is not { } f)
        {
            Append($"{what}: no runtime layout");
            return;
        }
        var s = Runtime.State.Current;
        Append($"{what}: " + f.Describe(s.State.ToString(), s.Harness.ToString(), Runtime.Host?.TokenRevision is not null));
    }

    private async Task RunExclusive(string name, Func<Task> body, bool reportBusy = false, StartupTrace? startupTrace = null)
    {
        var started = startupTrace?.Mark();
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            if (reportBusy)
            {
                Fail(name, RuntimeErrorCodes.InvalidTransition, "another operation is running; retry the import after it completes");
            }
            else
            {
                Append($"{name}: skipped, another operation is running");
            }
            if (startupTrace is not null && started is { } busyMark)
            {
                startupTrace.EmitSpan("bridge.complete", busyMark, StartupTrace.Busy);
            }
            return;
        }
        Volatile.Write(ref _operation, name);
        Volatile.Write(ref _progress, null);
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            await body().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Report(false, "EXCEPTION", $"{e.GetType().Name}: {e.Message}");
            Append($"{name}: EXCEPTION {e.GetType().Name}: {e.Message}");
            Log.Error(LogTag, e.ToString());
            if (startupTrace is not null && started is { } failedMark)
            {
                startupTrace.EmitSpan("bridge.complete", failedMark, StartupTrace.Failed, new Dictionary<string, object?> { ["code"] = "EXCEPTION" });
            }
        }
        finally
        {
            Volatile.Write(ref _operation, null);
            Volatile.Write(ref _progress, null);
            Volatile.Write(ref _busy, 0);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Records how the running operation ended (<see cref="LastOutcome"/>).</summary>
    private void Report(bool ok, string code, string? message)
    {
        if (CurrentOperation is { } op)
        {
            Volatile.Write(ref _lastOutcome, new OperationOutcome(op, ok, code, message, DateTimeOffset.Now));
        }
    }

    private void Append(string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss.fff} {line}";
        Log.Info(LogTag, stamped);
        lock (_logGate)
        {
            _log.AddLast(stamped);
            while (_log.Count > MaxLogLines)
            {
                _log.RemoveFirst();
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _services.Dispose();
        _http.Dispose();
        _downloadHttp.Dispose();
    }
}
