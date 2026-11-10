using System.Diagnostics;
using System.Text.Json;
using DshMobile.Application;
using DshMobile.Application.Runtime;
using DshMobile.Core.Bridge;
using DshMobile.Native;
using DshMobile.NativeBridge;
using DshMobile.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Log = Android.Util.Log;

namespace DshMobile.App.Bridge;

/// <summary>
/// Application-scoped owner of the native facade and the C# client. Survives Activity
/// recreation (rotation, background/foreground) so in-flight work is not lost.
/// Runtime start/stop/import go through the C# <see cref="RuntimeCoordinator"/> (ADR-0007); the implementation
/// underneath is still the legacy DSHBox bridge until the C# runtime slice passes real-device acceptance.
/// </summary>
public sealed class BridgeHost : IDisposable, IDiagnosticsLog
{
    public const string LogTag = "DshMobile";
    private const int MaxLogLines = 400;

    private readonly Android.Content.Context _context;
    private readonly DshNativeFacade _facade;
    private readonly NativeFacadeTransport _transport;
    private readonly object _logGate = new();
    private readonly LinkedList<string> _log = new();
    private int _busy;

    private string _runtimeState = "runtime: unknown";

    public BridgeHost(Android.Content.Context context)
    {
        _context = context.ApplicationContext ?? context;
        _facade = new DshNativeFacade();
        _transport = new NativeFacadeTransport(_facade);
        Client = new BridgeClient(_transport);
        Client.EventReceived += OnBridgeEvent;
        _facade.Initialize(context);
        Append($"facade initialized, bridge {DshNativeFacade.BridgeVersion}");

        Layout = new RuntimeLayout(_context.FilesDir!.AbsolutePath, _context.CacheDir?.AbsolutePath, _context.ApplicationInfo!.NativeLibraryDir!);
        _services = new ServiceCollection()
            .AddSingleton<IForegroundServiceController>(new AndroidForegroundService(_context))
            .AddSingleton<IDiagnosticsLog>(this)
            .AddDshMobileApplication(Layout, Client)
            .BuildServiceProvider();
        Runtime = _services.GetRequiredService<RuntimeCoordinator>();
        var s = Runtime.State.Current;
        Append($"runtime coordinator: impl={Runtime.Implementation} journal={s.State}/{s.Harness} gen={s.Generation}{(s.ErrorCode is null ? "" : " last error " + s.ErrorCode)}");
    }

    private readonly ServiceProvider _services;

    public BridgeClient Client { get; }

    public RuntimeLayout Layout { get; }

    public RuntimeCoordinator Runtime { get; }

    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    /// <summary>Latest sandbox/DSH state pushed by the native runtime.state event.</summary>
    public string RuntimeState => Volatile.Read(ref _runtimeState);

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

    public Task RunInfoAsync() => RunExclusive("bridge.info", async () =>
    {
        var resp = await Client.ExecuteAsync(BridgeMethods.Info).ConfigureAwait(false);
        if (!resp.Ok)
        {
            Append($"bridge.info FAILED {resp.Code}: {resp.Message}");
            return;
        }
        var info = BridgeCodec.ReadPayload(resp, BridgeJsonContext.Default.BridgeInfo)!;
        Append($"bridge {info.BridgeVersion} protocol={info.Protocol} kotlin={info.KotlinVersion}");
        Append($"upstream dshbox={info.Upstream.GetValueOrDefault("dshbox", "?")}");
        Append($"device sdk={info.SdkInt} abis=[{string.Join(",", info.Abis)}] model={info.DeviceModel}");
        // C#-first layer status (ADR-0007): native library is not shipped before its own PR, so this must say so.
        var native = DshNativeLibrary.Probe();
        Append($"csharp runtime impl={Runtime.Implementation} native={(native.Ok ? "abi " + native.Value : native.Code)} journal={Layout.JournalFile}");
    });

    public Task RunKotlinCompareAsync() => RunExclusive("dshbox.versions.compare", async () =>
    {
        (string A, string B)[] cases = [("0.1.1", "0.1.1-rc.2"), ("0.1.0-rc.9", "0.1.0-rc.10"), ("v1.5.1", "1.5.1")];
        foreach (var (a, b) in cases)
        {
            var payload = BridgeCodec.ToPayload(new VersionsCompareRequest(a, b), BridgeJsonContext.Default.VersionsCompareRequest);
            var resp = await Client.ExecuteAsync(BridgeMethods.DshboxVersionsCompare, payload).ConfigureAwait(false);
            Append(resp.Ok
                ? $"Versions.compare(\"{a}\", \"{b}\") = {BridgeCodec.ReadPayload(resp, BridgeJsonContext.Default.VersionsCompareResult)!.Result}"
                : $"Versions.compare FAILED {resp.Code}: {resp.Message}");
        }
    });

    // ------------------------------------------------------------------ TASK-006 WebUI

    /// <summary>Main thread only. The launch token never reaches managed code (see native WebUiController).</summary>
    public void AttachWebUi(Android.Webkit.WebView view, IWebUiListener listener) => _facade.AttachWebUi(view, listener);

    public void DetachWebUi() => _facade.DetachWebUi();

    public void ReloadWebUi() => _facade.ReloadWebUi();

    /// <summary>Answers a pending WebUI file chooser (null/empty = cancelled). Main thread only.</summary>
    public void CompleteWebFileChooser(Android.Net.Uri[]? uris) => _facade.CompleteWebFileChooser(uris);

    // ------------------------------------------------------------------ TASK-008 terminal (main thread)

    /// <summary>Native TerminalView bound to the active shell session, or null when no runtime is installed.</summary>
    public Android.Views.View? CreateTerminalView(Android.Content.Context activity, ITerminalListener listener) =>
        _facade.CreateTerminalView(activity, listener);

    public void ReleaseTerminalView() => _facade.ReleaseTerminalView();

    public string? NewTerminalSession() => _facade.NewTerminalSession();

    public void ActivateTerminalSession(string id) => _facade.ActivateTerminalSession(id);

    public void CloseTerminalSession(string id) => _facade.CloseTerminalSession(id);

    public string? TerminalSessionsJson() => _facade.TerminalSessionsJson();

    public bool TerminalSendKey(string key) => _facade.TerminalSendKey(key);

    public void SetTerminalCtrl(bool on) => _facade.SetTerminalCtrl(on);

    public void TerminalPaste() => _facade.TerminalPaste();

    // ------------------------------------------------------------------ TASK-004 runtime

    // Import / sandbox / DSH deadlines now live in LegacyBridgeRuntimeManager (DshMobile.Runtime.Legacy).
    private static readonly TimeSpan PrepareDeadline = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DiagnosticsDeadline = TimeSpan.FromMinutes(3);

    /// <summary>Installs APK-bundled layers (android-side) and, when base/node exist, the bundled DSH layer.</summary>
    public Task RunPrepareAsync() => RunExclusive("runtime.prepare", async () =>
    {
        var resp = await Client.ExecuteAsync(BridgeMethods.RuntimePrepare, deadline: PrepareDeadline).ConfigureAwait(false);
        LogStatus("prepare", resp);
    });

    /// <summary>
    /// Imports a DSHBox runtime bundle zip (base/node/android-side). <paramref name="zipPath"/> comes from the
    /// system picker copy (MainActivity); without it, falls back to the app-specific external directory
    /// where a developer can `adb push` the bundle.
    /// </summary>
    public Task RunImportAsync(string? zipPath = null, bool deleteAfter = false) => RunExclusive("runtime.importBundle", async () =>
    {
        var dir = _context.GetExternalFilesDir(null)?.AbsolutePath;
        var zip = zipPath ?? (dir is null ? null : Directory.EnumerateFiles(dir, "dshapp-runtime-*.zip").OrderDescending().FirstOrDefault());
        if (zip is null)
        {
            Append($"import: no dshapp-runtime-*.zip in {dir ?? "(external files dir unavailable)"}");
            return;
        }
        Append($"import: {Path.GetFileName(zip)} ({new FileInfo(zip).Length / 1024 / 1024} MiB) ...");
        var sw = Stopwatch.StartNew();
        try
        {
            // Legacy installer decides; the C# inspector shadow-checks the same zip (PR-02, see shadow-reports.jsonl).
            var r = await Runtime.InstallAsync(new RuntimePackage(zip), null, CancellationToken.None).ConfigureAwait(false);
            Append($"import: took {sw.Elapsed.TotalSeconds:F1} s");
            Append(r.Ok ? "import: ok" : $"import FAILED {r.Code}{(r.UpstreamCode is null ? "" : "/" + r.UpstreamCode)}: {r.Message}");
            LogStatus("status", await Client.ExecuteAsync(BridgeMethods.RuntimeStatus).ConfigureAwait(false));
        }
        finally
        {
            if (deleteAfter)
            {
                File.Delete(zip);
            }
        }
    });

    /// <summary>Logs a host-side step that is not a bridge call (e.g. copying a picked file).</summary>
    public void Note(string line) => Append(line);

    /// <summary>
    /// Foreground service, sandbox, then DSH, supervised by <see cref="RuntimeCoordinator"/>: a failed sandbox start
    /// is cleaned up and releases the service; a failed stop keeps it (PR-00 defect 3).
    /// </summary>
    public Task RunStartAsync() => RunExclusive("start", async () =>
    {
        var r = await Runtime.StartAsync(DshProfile.Web, CancellationToken.None).ConfigureAwait(false);
        var s = r.Snapshot;
        Append(r.Ok
            ? $"start: ok sandbox={s.State} dsh={s.Harness} version={s.DshVersion ?? "-"} gen={s.Generation}"
            : $"start FAILED {r.Code}{(r.UpstreamCode is null ? "" : "/" + r.UpstreamCode)}: {r.Message} (state={s.State} dsh={s.Harness})");
    });

    public Task RunStopAsync() => RunExclusive("stop", async () =>
    {
        var r = await Runtime.StopAsync(CancellationToken.None).ConfigureAwait(false);
        Append(r.Ok ? $"stop: ok ({r.Snapshot.State})" : $"stop FAILED {r.Code}: {r.Message} (state={r.Snapshot.State})");
    });

    /// <summary>Fixed guest diagnostics (uname/debian/node/dsh --version) plus health.</summary>
    public Task RunDiagnosticsAsync() => RunExclusive("diagnostics", async () =>
    {
        LogStatus("status", await Client.ExecuteAsync(BridgeMethods.RuntimeStatus).ConfigureAwait(false));
        var health = await Client.ExecuteAsync(BridgeMethods.RuntimeHealth).ConfigureAwait(false);
        if (health.Ok)
        {
            var h = BridgeCodec.ReadPayload(health, BridgeJsonContext.Default.RuntimeHealth)!;
            Append($"health: process={h.DshProcessRunning} port={h.PortOpen} webUi={h.WebUiReady} lastError={h.LastError ?? "-"}");
        }
        else
        {
            Append($"health FAILED {health.Code}: {health.Message}");
        }
        var diag = await Client.ExecuteAsync(BridgeMethods.RuntimeDiagnostics, deadline: DiagnosticsDeadline).ConfigureAwait(false);
        if (!diag.Ok)
        {
            Append($"diagnostics FAILED {diag.Code}: {diag.Message}");
            return;
        }
        foreach (var (key, r) in BridgeCodec.ReadPayload(diag, BridgeJsonContext.Default.DictionaryStringDiagnosticResult)!)
        {
            Append($"diag {key}: {(r.Ok ? r.Output.Trim() : "FAILED " + r.Error)}");
        }
    });

    private static readonly TimeSpan ProbeDeadline = TimeSpan.FromMinutes(10);

    /// <summary>TASK-005: runs the guest probe for every shipped profile, in order, and logs each check.</summary>
    public Task RunProbesAsync() => RunExclusive("dsh.probe", async () =>
    {
        var summary = new List<string>();
        foreach (var profile in DshProfiles.All)
        {
            var sw = Stopwatch.StartNew();
            var payload = BridgeCodec.ToPayload(new ProbeRequest(profile), BridgeJsonContext.Default.ProbeRequest);
            var resp = await Client.ExecuteAsync(BridgeMethods.DshProbe, payload, ProbeDeadline).ConfigureAwait(false);
            if (!resp.Ok)
            {
                Append($"probe {profile} FAILED {resp.Code}: {resp.Message}");
                summary.Add($"{profile}=ERROR");
                continue;
            }
            var r = BridgeCodec.ReadPayload(resp, BridgeJsonContext.Default.ProbeResult)!;
            Append($"probe {profile}: {r.Status.ToUpperInvariant()} shim={r.Shim} ({sw.Elapsed.TotalSeconds:F1} s)");
            foreach (var c in r.Checks)
            {
                Append($"  {(c.Ok ? "ok  " : "FAIL")} {c.Name}{(c.Ms is { } ms ? $" {ms} ms" : "")}: {c.Detail}");
            }
            summary.Add($"{profile}={r.Status}");
        }
        Append("probe summary: " + string.Join(" ", summary));
    });

    private void LogStatus(string what, BridgeResponse resp)
    {
        if (!resp.Ok)
        {
            Append($"{what} FAILED {resp.Code}: {resp.Message}");
            return;
        }
        var st = BridgeCodec.ReadPayload(resp, BridgeJsonContext.Default.RuntimeStatus);
        if (st is null)
        {
            Append($"{what}: ok");
            return;
        }
        Append($"{what}: sandbox={st.SandboxState} dsh={st.DshState} version={st.DshVersion ?? "-"} runtime={st.RuntimeInstalled} node={st.NodeLayerInstalled} token={st.HasLaunchToken} shim={st.LinkShim} free={st.FreeBytes / 1024 / 1024} MiB");
        foreach (var step in st.Steps ?? [])
        {
            Append($"  - {step}");
        }
        if (st.DshProvision is not null)
        {
            Append($"  - {st.DshProvision}");
        }
    }

    private void OnBridgeEvent(object? sender, BridgeEvent e)
    {
        if (e.Event == BridgeEvents.RuntimeState && e.Payload is { } p)
        {
            var st = p.Deserialize(BridgeJsonContext.Default.RuntimeStateEvent);
            if (st is not null)
            {
                Volatile.Write(ref _runtimeState, $"sandbox={st.SandboxState} · dsh={st.DshState} · {st.DshVersion ?? "-"}");
            }
        }
        Append($"event {e.Event} {e.Payload}");
    }

    /// <summary>M1 acceptance: N concurrent round-trips; afterwards both in-flight tables must be 0.</summary>
    public Task RunStressAsync(int count = 1000) => RunExclusive($"stress x{count}", async () =>
    {
        var sw = Stopwatch.StartNew();
        var gcBefore = GC.GetTotalMemory(forceFullCollection: false);
        var tasks = new Task<BridgeResponse>[count];
        for (var i = 0; i < count; i++)
        {
            tasks[i] = Client.ExecuteAsync(BridgeMethods.Ping);
        }
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        sw.Stop();

        // Native removes a request from its table right after our callback returns; allow it to settle.
        var settle = Stopwatch.StartNew();
        while (_facade.InFlightCount != 0 && settle.ElapsedMilliseconds < 1000)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }

        var ok = results.Count(r => r.Ok);
        var failures = results.Where(r => !r.Ok).GroupBy(r => r.Code).Select(g => $"{g.Key}={g.Count()}");
        Append($"stress: ok={ok} failed={count - ok} [{string.Join(",", failures)}] in {sw.ElapsedMilliseconds} ms");
        Append($"stress: inFlight C#={Client.InFlightCount} native={_facade.InFlightCount} orphans={Client.OrphanResponses} malformed={Client.MalformedMessages}");
        Append($"stress: managed heap {gcBefore / 1024} KiB -> {GC.GetTotalMemory(false) / 1024} KiB");
        Append($"stress: after GC {MeasureRetained()}");
    });

    /// <summary>
    /// Retained memory after forcing GC on both runtimes, plus JNI global refs. Lazy GC makes raw
    /// dumpsys numbers rise between runs; only post-GC values that keep growing indicate a leak.
    /// </summary>
    private static string MeasureRetained()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Java.Lang.JavaSystem.Gc();
        Java.Lang.JavaSystem.RunFinalization();
        Java.Lang.JavaSystem.Gc();
        var rt = Java.Lang.Runtime.GetRuntime()!;
        var javaUsed = rt.TotalMemory() - rt.FreeMemory();
        var grefs = Java.Interop.JniRuntime.CurrentRuntime.GlobalReferenceCount;
        return $"managed={GC.GetTotalMemory(false) / 1024} KiB java={javaUsed / 1024} KiB grefs={grefs}";
    }

    private async Task RunExclusive(string name, Func<Task> body)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            Append($"{name}: skipped, another operation is running");
            return;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            await body().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Append($"{name}: EXCEPTION {e.GetType().Name}: {e.Message}");
            Log.Error(LogTag, e.ToString());
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
            Changed?.Invoke(this, EventArgs.Empty);
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
        Client.Dispose();
        _facade.Unsubscribe(_transport);
        _facade.Close();
        _transport.Dispose();
    }
}
