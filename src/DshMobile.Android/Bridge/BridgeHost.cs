using System.Diagnostics;
using DshMobile.Application;
using DshMobile.Application.Runtime;
using DshMobile.Application.Ui;
using DshMobile.Core.Contracts;
using DshMobile.Native;
using DshMobile.Runtime;
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
            .BuildServiceProvider();
        Runtime = _services.GetRequiredService<RuntimeCoordinator>();
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

    /// <summary>Installs the APK-bundled DSH layer when base/node exist (MIGRATION row 17, C# RuntimePreparer).</summary>
    public Task RunPrepareAsync() => RunExclusive("runtime.prepare", async () =>
    {
        var r = await Runtime.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        // The legacy prepare is ok even when the DSH layer was not installed (kept, failed, no layers); the UI says which.
        var o = r.Ok ? PrepareOutcome.Parse(r.Message) : null;
        if (o is { Failed: true })
        {
            Report(false, o.Code ?? RuntimeErrorCodes.Upstream, o.Detail);
        }
        else
        {
            Report(r.Ok, r.Code, r.Message);
        }
        if (!r.Ok)
        {
            Append($"prepare FAILED {r.Code}: {r.Message}"); // the coordinator already logged the steps on success
        }
    });

    /// <summary>
    /// Imports a runtime bundle zip (base/node/android-side) with the C# LayerInstaller. <paramref name="zipPath"/>
    /// comes from the system picker copy (MainActivity); without it, falls back to the app-specific external directory
    /// where a developer can adb-push the bundle: the self-built <c>dshmobile-runtime-*.zip</c> (ADR-0020) first, else an
    /// older DSHBox <c>dshapp-runtime-*.zip</c>.
    /// </summary>
    public Task RunImportAsync(string? zipPath = null, bool deleteAfter = false) => RunExclusive("runtime.import", async () =>
    {
        var dir = _context.GetExternalFilesDir(null)?.AbsolutePath;
        var zip = zipPath ?? (dir is null ? null : new[] { "dshmobile-runtime-*.zip", "dshapp-runtime-*.zip" }
            .Select(p => Directory.EnumerateFiles(dir, p).OrderDescending().FirstOrDefault())
            .FirstOrDefault(z => z is not null));
        if (zip is null)
        {
            Report(false, RuntimeErrorCodes.BundleNotFound, "no runtime bundle");
            Append($"import: no dshmobile-runtime-*.zip (or dshapp-runtime-*.zip) in {dir ?? "(external files dir unavailable)"}");
            return;
        }
        Append($"import: {Path.GetFileName(zip)} ({new FileInfo(zip).Length / 1024 / 1024} MiB) ...");
        var sw = Stopwatch.StartNew();
        try
        {
            var progress = new Progress<RuntimeProgress>(p =>
            {
                Volatile.Write(ref _progress, p);
                Changed?.Invoke(this, EventArgs.Empty);
            });
            var r = await Runtime.InstallAsync(new RuntimePackage(zip), progress, CancellationToken.None).ConfigureAwait(false);
            Report(r.Ok, r.Code, r.Message);
            Append($"import: took {sw.Elapsed.TotalSeconds:F1} s");
            Append(r.Ok ? "import: ok" : $"import FAILED {r.Code}{(r.UpstreamCode is null ? "" : "/" + r.UpstreamCode)}: {r.Message}");
            await LogStatusAsync("status").ConfigureAwait(false);
        }
        finally
        {
            if (deleteAfter)
            {
                File.Delete(zip);
            }
        }
    });

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
    public Task RunStartAsync() => RunExclusive("start", async () =>
    {
        var r = await Runtime.StartAsync(DshProfile.Web, CancellationToken.None).ConfigureAwait(false);
        Report(r.Ok, r.Code, r.Message);
        var s = r.Snapshot;
        Append(r.Ok
            ? $"start: ok sandbox={s.State} dsh={s.Harness} version={s.DshVersion ?? "-"} gen={s.Generation}"
            : $"start FAILED {r.Code}{(r.UpstreamCode is null ? "" : "/" + r.UpstreamCode)}: {r.Message} (state={s.State} dsh={s.Harness})");
    });

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
    /// PR-06 developer acceptance: the guest login shell on a C#-owned PTY (libdshmobile_native), driven by PtyProbe.
    /// The steps (prompt, tty, resize, ^C, exit, no orphans) are logged.
    /// </summary>
    public Task RunPtyProbeAsync() => RunExclusive("terminal.pty-probe", async () =>
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

    private async Task RunExclusive(string name, Func<Task> body)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            Append($"{name}: skipped, another operation is running");
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
    }
}
