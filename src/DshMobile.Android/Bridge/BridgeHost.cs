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
/// Application-scoped owner of the C# runtime (<see cref="RuntimeCoordinator"/>) and of the legacy facade. Survives
/// Activity recreation (rotation, background/foreground) so in-flight work is not lost. Default paths are C#
/// (ADR-0015 / ADR-0016); the legacy facade (DSHBox RuntimeController + sandbox-manager) is created only the first time
/// an explicit legacy fallback uses it, so a process that never falls back runs no DSHBox code.
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
        _transport = new NativeFacadeTransport(_facade, EnsureLegacy);
        Client = new BridgeClient(_transport);
        Client.EventReceived += OnBridgeEvent;
        Append("legacy facade: not initialized (created on the first legacy fallback)");

        Layout = new RuntimeLayout(_context.FilesDir!.AbsolutePath, _context.CacheDir?.AbsolutePath, _context.ApplicationInfo!.NativeLibraryDir!);
        _services = new ServiceCollection()
            .AddSingleton<IForegroundServiceController>(new AndroidForegroundService(_context))
            .AddSingleton<DshMobile.Runtime.Layers.IBundledAssets>(new AndroidBundledAssets(_context.Assets!))
            .AddSingleton<IDiagnosticsLog>(this)
            .AddDshMobileApplication(Layout, Client)
            .BuildServiceProvider();
        Runtime = _services.GetRequiredService<RuntimeCoordinator>();
        RemoteShadow = _services.GetRequiredService<DshMobile.Application.Shadow.RemoteHostsShadow>();
        // ADR-0010: the guest-facing C# Relay stops with the sandbox, as the legacy sandbox.stop stops its Relay and
        // removes the ui_* layer.
        Runtime.SandboxStopping += () =>
        {
            if (Automation.Relay.CsRelay.GuestFacing)
            {
                var removed = Automation.Relay.CsRelay.StopGuest(Layout.UserDataDir);
                Append($"c# relay (guest) stopped with the sandbox; ui_* layer {(removed ? "removed" : "left (not ours)")}");
            }
        };
        // ADR-0015: the C# host never calls the legacy sandbox.stop, which is where legacy terminal sessions and the
        // legacy Relay (+ ui_* layer) stop. Do the same here with the existing bridge calls.
        Runtime.HostStopping = StopLegacyCompanionsAsync;
        // The status line follows the journal while the C# host owns the processes (legacy events say "stopped").
        Runtime.State.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        var s = Runtime.State.Current;
        Append($"runtime coordinator: impl={Runtime.Implementation} journal={s.State}/{s.Harness} gen={s.Generation}{(s.ErrorCode is null ? "" : " last error " + s.ErrorCode)}");
        // The journal may still say what the previous process saw; align it with the disk (C#, no bridge call).
        _ = Task.Run(async () =>
        {
            var now = await Runtime.RefreshStateAsync(CancellationToken.None).ConfigureAwait(false);
            Append($"runtime (C#): journal={now.State}/{now.Harness}");
        });
    }

    private readonly object _legacyGate = new();
    private Task? _legacyInit;
    private volatile bool _legacyReady;
    private volatile bool _legacyTerminalUsed;

    /// <summary>Whether the legacy facade was created in this process (only by an explicit legacy fallback).</summary>
    public bool LegacyInitialized => _legacyReady;

    /// <summary>
    /// Creates the legacy facade on first use. <c>DshNativeFacade.initialize</c> builds the DSHBox RuntimeController and
    /// sandbox-manager, which used to happen at app start; it runs on the main thread as before. A background caller
    /// waits for the main thread (the main thread itself never waits on the bridge, so this cannot deadlock).
    /// </summary>
    private void EnsureLegacy()
    {
        if (_legacyReady)
        {
            return;
        }
        if (Android.OS.Looper.MyLooper() == Android.OS.Looper.MainLooper)
        {
            InitializeLegacyOnMain();
            return;
        }
        LazyInitializer.EnsureInitialized(ref _legacyInit, () => OnMainThreadAsync(() =>
        {
            InitializeLegacyOnMain();
            return true;
        })).GetAwaiter().GetResult();
    }

    private void InitializeLegacyOnMain()
    {
        lock (_legacyGate) // only ever taken on the main thread
        {
            if (_legacyReady)
            {
                return;
            }
            _facade.Initialize(_context);
            _legacyReady = true;
        }
        Append($"legacy facade initialized on first use, bridge {DshNativeFacade.BridgeVersion}");
    }

    private static readonly TimeSpan CompanionStopDeadline = TimeSpan.FromSeconds(30);

    /// <summary>
    /// What the legacy <c>sandbox.stop</c> does before stopping its sandbox (RuntimeController): stop every terminal
    /// session, stop the Relay and remove the ui_* layer. Runs when the C# host stops; the C# guest-facing Relay is
    /// handled by <see cref="RuntimeCoordinator.SandboxStopping"/>.
    /// </summary>
    private async Task StopLegacyCompanionsAsync(CancellationToken ct)
    {
        // Legacy terminal sessions exist only if the legacy terminal (fallback) was opened in this process.
        var closed = !_legacyTerminalUsed ? 0 : await OnMainThreadAsync(() =>
        {
            var ids = new List<string>();
            using (var doc = JsonDocument.Parse(_facade.TerminalSessionsJson() ?? "[]"))
            {
                foreach (var session in doc.RootElement.EnumerateArray())
                {
                    if (session.TryGetProperty("id", out var id) && id.GetString() is { } sid)
                    {
                        ids.Add(sid);
                    }
                }
            }
            foreach (var id in ids)
            {
                _facade.CloseTerminalSession(id);
            }
            return ids.Count;
        }).ConfigureAwait(false);
        if (closed > 0)
        {
            Append($"legacy terminal: closed {closed} session(s) with the C# host");
        }
        // Only a legacy Relay started on the fallback from this process can be running (ADR-0016).
        if (!Automation.Relay.CsRelay.GuestFacing && Automation.Relay.CsRelay.LegacyFallbackActive)
        {
            var relay = await Client.ExecuteAsync(BridgeMethods.RelayStop, deadline: CompanionStopDeadline, cancellationToken: ct).ConfigureAwait(false);
            Append(relay.Ok ? "legacy relay: stopped with the C# host (ui_* layer removed if ours)" : $"legacy relay: stop FAILED {relay.Code}: {relay.Message}");
            if (relay.Ok)
            {
                Automation.Relay.CsRelay.LegacyFallbackActive = false;
            }
        }
    }

    private static Task<T> OnMainThreadAsync<T>(Func<T> body)
    {
        var looper = Android.OS.Looper.MainLooper!;
        if (Android.OS.Looper.MyLooper() == looper)
        {
            return Task.FromResult(body());
        }
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Android.OS.Handler(looper);
        handler.Post(() =>
        {
            try
            {
                tcs.SetResult(body());
            }
            catch (Exception e)
            {
                tcs.SetException(e);
            }
        });
        return tcs.Task;
    }

    private readonly ServiceProvider _services;

    public BridgeClient Client { get; }

    public RuntimeLayout Layout { get; }

    public RuntimeCoordinator Runtime { get; }

    /// <summary>PR-09 shadow of the remote host slice (read-only; SSH stays legacy).</summary>
    public DshMobile.Application.Shadow.RemoteHostsShadow RemoteShadow { get; }

    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    /// <summary>
    /// The journal (C#): with the C# host's state while it owns the processes (ADR-0015). Only while a fallback-started
    /// legacy runtime may be running, the latest sandbox/DSH state pushed by the legacy runtime.state event.
    /// </summary>
    public string RuntimeState
    {
        get
        {
            var s = Runtime.State.Current;
            if (Runtime.HostOwnsProcesses)
            {
                return $"C# host={Runtime.HostState} · runtime={s.State} · dsh={s.Harness}{(s.ErrorCode is null ? "" : " · " + s.ErrorCode)}";
            }
            if (Runtime.LegacyRuntimeActive)
            {
                return "legacy " + Volatile.Read(ref _runtimeState);
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

    /// <summary>
    /// Device / runtime facts. Default: C# only (Android APIs, the native library, the runtime on disk); with
    /// <paramref name="legacy"/> the legacy <c>bridge.info</c> (bridge, Kotlin and DSHBox versions) for comparison.
    /// </summary>
    public Task RunInfoAsync(bool legacy = false) => legacy ? RunLegacyInfoAsync() : RunExclusive("info", async () =>
    {
        var app = _context.PackageManager!.GetPackageInfo(_context.PackageName!, 0);
        Append($"app {_context.PackageName} {app?.VersionName ?? "?"} · .NET {Environment.Version} · runtime impl={Runtime.Implementation}");
        Append($"device sdk={(int)Android.OS.Build.VERSION.SdkInt} abis=[{string.Join(",", Android.OS.Build.SupportedAbis ?? [])}] model={Android.OS.Build.Model}");
        var native = DshNativeLibrary.Probe();
        Append($"native={(native.Ok ? "abi " + native.Value : native.Code)} journal={Layout.JournalFile}");
        if (await Runtime.ReadFactsAsync(CancellationToken.None).ConfigureAwait(false) is { } f)
        {
            Append("runtime (C#): " + f.Describe(Runtime.State.Current.State.ToString(), Runtime.State.Current.Harness.ToString(), Runtime.Host?.TokenRevision is not null));
        }
        Append($"legacy facade: {(LegacyInitialized ? "initialized (a fallback was used)" : "not initialized")} · long-press = legacy bridge.info");
    });

    private Task RunLegacyInfoAsync() => RunExclusive("bridge.info", async () =>
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

    private static readonly (string A, string B)[] VersionCases = [("0.1.1", "0.1.1-rc.2"), ("0.1.0-rc.9", "0.1.0-rc.10"), ("v1.5.1", "1.5.1")];

    /// <summary>
    /// DSH version ordering. Default: the C# <see cref="Versions.Compare"/> (the port the import and the DSH layer
    /// arbitration use); with <paramref name="legacy"/> also the legacy Kotlin <c>Versions.compare</c> and whether they agree.
    /// </summary>
    public Task RunKotlinCompareAsync(bool legacy = false) => RunExclusive("versions.compare", async () =>
    {
        foreach (var (a, b) in VersionCases)
        {
            var cs = Math.Sign(Versions.Compare(a, b));
            if (!legacy)
            {
                Append($"Versions.Compare(\"{a}\", \"{b}\") = {cs} (C#)");
                continue;
            }
            var payload = BridgeCodec.ToPayload(new VersionsCompareRequest(a, b), BridgeJsonContext.Default.VersionsCompareRequest);
            var resp = await Client.ExecuteAsync(BridgeMethods.DshboxVersionsCompare, payload).ConfigureAwait(false);
            if (!resp.Ok)
            {
                Append($"Versions.compare FAILED {resp.Code}: {resp.Message}");
                continue;
            }
            var kt = Math.Sign(BridgeCodec.ReadPayload(resp, BridgeJsonContext.Default.VersionsCompareResult)!.Result);
            Append($"Versions.compare(\"{a}\", \"{b}\") = {kt} (Kotlin) / {cs} (C#) {(kt == cs ? "Agree" : "DIVERGE")}");
        }
    });

    // ------------------------------------------------------------------ TASK-006 WebUI

    /// <summary>Main thread only. The launch token never reaches managed code (see native WebUiController).</summary>
    public void AttachWebUi(Android.Webkit.WebView view, IWebUiListener listener)
    {
        EnsureLegacy();
        _facade.AttachWebUi(view, listener);
    }

    public void DetachWebUi()
    {
        if (_legacyReady)
        {
            _facade.DetachWebUi();
        }
    }

    public void ReloadWebUi()
    {
        EnsureLegacy();
        _facade.ReloadWebUi();
    }

    /// <summary>Answers a pending WebUI file chooser (null/empty = cancelled). Main thread only.</summary>
    public void CompleteWebFileChooser(Android.Net.Uri[]? uris)
    {
        EnsureLegacy();
        _facade.CompleteWebFileChooser(uris);
    }

    // ------------------------------------------------------------------ TASK-008 terminal (main thread)

    /// <summary>Native TerminalView bound to the active shell session, or null when no runtime is installed.</summary>
    public Android.Views.View? CreateTerminalView(Android.Content.Context activity, ITerminalListener listener)
    {
        EnsureLegacy();
        _legacyTerminalUsed = true;
        return _facade.CreateTerminalView(activity, listener);
    }

    public void ReleaseTerminalView()
    {
        if (_legacyReady)
        {
            _facade.ReleaseTerminalView();
        }
    }

    public string? NewTerminalSession()
    {
        EnsureLegacy();
        _legacyTerminalUsed = true;
        return _facade.NewTerminalSession();
    }

    public void ActivateTerminalSession(string id)
    {
        EnsureLegacy();
        _facade.ActivateTerminalSession(id);
    }

    public void CloseTerminalSession(string id)
    {
        EnsureLegacy();
        _facade.CloseTerminalSession(id);
    }

    public string? TerminalSessionsJson()
    {
        EnsureLegacy();
        return _facade.TerminalSessionsJson();
    }

    public bool TerminalSendKey(string key)
    {
        EnsureLegacy();
        return _facade.TerminalSendKey(key);
    }

    public void SetTerminalCtrl(bool on)
    {
        EnsureLegacy();
        _facade.SetTerminalCtrl(on);
    }

    public void TerminalPaste()
    {
        EnsureLegacy();
        _facade.TerminalPaste();
    }

    // ------------------------------------------------------------------ TASK-004 runtime

    // Import / sandbox / DSH deadlines now live in LegacyBridgeRuntimeManager (DshMobile.Runtime.Legacy).
    private static readonly TimeSpan DiagnosticsDeadline = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Installs the APK-bundled DSH layer when base/node exist (MIGRATION row 17). Default (ADR-0016): the C#
    /// <c>RuntimePreparer</c>; <paramref name="mode"/> = LegacyWithShadow is the explicit legacy fallback.
    /// </summary>
    public Task RunPrepareAsync(SliceMode? mode = null) => RunExclusive("runtime.prepare", async () =>
    {
        Append((mode ?? Runtime.Options.DshProvision) == SliceMode.CSharp
            ? "prepare: C# RuntimePreparer"
            : "prepare: legacy runtime.prepare (fallback, C# shadow)");
        var r = await Runtime.PrepareAsync(CancellationToken.None, mode).ConfigureAwait(false);
        if (!r.Ok)
        {
            Append($"prepare FAILED {r.Code}: {r.Message}"); // the coordinator already logged the steps on success
        }
    });

    /// <summary>
    /// Imports a DSHBox runtime bundle zip (base/node/android-side). <paramref name="zipPath"/> comes from the
    /// system picker copy (MainActivity); without it, falls back to the app-specific external directory
    /// where a developer can `adb push` the bundle.
    /// </summary>
    public Task RunImportAsync(string? zipPath = null, bool deleteAfter = false, SliceMode? mode = null) => RunExclusive("runtime.importBundle", async () =>
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
            // Default (ADR-0016): the C# LayerInstaller decides. mode = LegacyWithShadow: the legacy installer decides
            // for this import and the C# inspector / extractor shadow-check the same zip (PR-02).
            Append((mode ?? Runtime.Options.BundleImport) == SliceMode.CSharp ? "import: C# LayerInstaller" : "import: legacy installer (fallback, C# shadow)");
            var r = await Runtime.InstallAsync(new RuntimePackage(zip), null, CancellationToken.None, mode).ConfigureAwait(false);
            Append($"import: took {sw.Elapsed.TotalSeconds:F1} s");
            Append(r.Ok ? "import: ok" : $"import FAILED {r.Code}{(r.UpstreamCode is null ? "" : "/" + r.UpstreamCode)}: {r.Message}");
            if ((mode ?? Runtime.Options.BundleImport) == SliceMode.CSharp)
            {
                await LogCSharpStatusAsync("status").ConfigureAwait(false);
            }
            else
            {
                LogStatus("status", await Client.ExecuteAsync(BridgeMethods.RuntimeStatus).ConfigureAwait(false)); // legacy fallback import
            }
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
        Append(r.Ok ? $"rollback: ok ({r.Message})" : $"rollback FAILED {r.Code}: {r.Message}");
        await LogCSharpStatusAsync("status").ConfigureAwait(false);
    });

    /// <summary>Logs a host-side step that is not a bridge call (e.g. copying a picked file).</summary>
    public void Note(string line) => Append(line);

    /// <summary>
    /// Foreground service, sandbox, then DSH, supervised by <see cref="RuntimeCoordinator"/>: a failed sandbox start
    /// is cleaned up and releases the service; a failed stop keeps it (PR-00 defect 3).
    /// </summary>
    /// <param name="mode">Null: <see cref="MigrationOptions.RuntimeHost"/> (the C# host, ADR-0015);
    /// <see cref="SliceMode.Legacy"/>: the explicit legacy fallback (browser automation).</param>
    public Task RunStartAsync(SliceMode? mode = null) => RunExclusive("start", async () =>
    {
        var r = await Runtime.StartAsync(DshProfile.Web, CancellationToken.None, mode).ConfigureAwait(false);
        var s = r.Snapshot;
        var owner = Runtime.HostOwnsProcesses ? "csharp host" : "legacy";
        Append(r.Ok
            ? $"start: ok ({owner}) sandbox={s.State} dsh={s.Harness} version={s.DshVersion ?? "-"} gen={s.Generation}"
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
        if (Runtime.HostOwnsProcesses)
        {
            // ADR-0015: legacy status / health do not know the C#-hosted processes and would report them as stopped.
            foreach (var line in await Runtime.DescribeHostAsync(CancellationToken.None).ConfigureAwait(false))
            {
                Append(line);
            }
            Append("legacy status / health: skipped (the C# host owns the processes)");
            await Runtime.ShadowSupervisorAsync("", "", false, CancellationToken.None).ConfigureAwait(false);
            await RunGuestDiagnosticsAsync().ConfigureAwait(false);
            return;
        }
        if (!Runtime.LegacyRuntimeActive)
        {
            // MIGRATION row 23: nothing was started by the legacy runtime in this process, so C# reads the state itself.
            await LogCSharpStatusAsync("status").ConfigureAwait(false);
            var h = await DshMobile.Runtime.Supervision.HttpHealthProbe.CheckAsync().ConfigureAwait(false);
            Append($"health (C#): port={h.PortOpen} webUi={h.WebUiReady} http={h.HttpStatus?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}{(h.Error is null ? "" : " error=" + h.Error)}");
            foreach (var line in await Runtime.DescribeHostAsync(CancellationToken.None).ConfigureAwait(false))
            {
                Append(line);
            }
            await RunGuestDiagnosticsAsync().ConfigureAwait(false);
            return;
        }
        var statusResp = await Client.ExecuteAsync(BridgeMethods.RuntimeStatus).ConfigureAwait(false);
        LogStatus("status", statusResp);
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
        // PR-03 read-only shadow, right after the legacy readings: C# /proc view + own HTTP probe.
        if (statusResp.Ok && BridgeCodec.ReadPayload(statusResp, BridgeJsonContext.Default.RuntimeStatus) is { } st)
        {
            await Runtime.ShadowSupervisorAsync(st.SandboxState, st.DshState, health.Ok, CancellationToken.None).ConfigureAwait(false);
        }
        await RunGuestDiagnosticsAsync().ConfigureAwait(false);
    });

    /// <summary>
    /// The fixed guest commands, each in its own PRoot (independent of who owns the sandbox). Default (ADR-0016): the C#
    /// PRoot launcher only; otherwise legacy <c>runtime.diagnostics</c> + the guest-exec shadow.
    /// </summary>
    private async Task RunGuestDiagnosticsAsync()
    {
        if (Runtime.Options.GuestExec == SliceMode.CSharp)
        {
            foreach (var (key, r) in await Runtime.RunGuestDiagnosticsAsync(CancellationToken.None).ConfigureAwait(false))
            {
                var output = r.Output.Trim();
                Append($"diag {key}: {(r.Ok ? output : "FAILED " + (r.Error ?? $"exit={r.ExitCode}") + (output.Length > 0 ? " | " + output[..Math.Min(output.Length, 400)] : ""))}");
            }
            return;
        }
        var diag = await Client.ExecuteAsync(BridgeMethods.RuntimeDiagnostics, deadline: DiagnosticsDeadline).ConfigureAwait(false);
        if (!diag.Ok)
        {
            Append($"diagnostics FAILED {diag.Code}: {diag.Message}");
            return;
        }
        var legacy = new List<(string Key, bool Ok, string Output)>();
        foreach (var (key, r) in BridgeCodec.ReadPayload(diag, BridgeJsonContext.Default.DictionaryStringDiagnosticResult)!)
        {
            Append($"diag {key}: {(r.Ok ? r.Output.Trim() : "FAILED " + r.Error)}");
            legacy.Add((key, r.Ok, r.Output));
        }
        // PR-03 shadow: the same fixed commands through the C# PRoot launcher (no DSHBox code); legacy result stands.
        await Runtime.ShadowGuestDiagnosticsAsync(legacy, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// PR-06 developer acceptance: the guest login shell on a C#-owned PTY (libdshmobile_native), driven by PtyProbe.
    /// Independent of the legacy terminal; the steps (prompt, tty, resize, ^C, exit, no orphans) are logged.
    /// </summary>
    public Task RunCSharpPtyProbeAsync() => RunExclusive("terminal.pty-probe", async () =>
    {
        var native = DshMobile.Native.DshNativeLibrary.Probe();
        Append(native.Ok ? $"native: libdshmobile_native ABI {native.Value}" : $"native: {native.Code}: {native.Message}");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var r = await Runtime.ProbeCSharpPtyAsync(cts.Token).ConfigureAwait(false);
        foreach (var step in r.Steps)
        {
            Append($"pty {step.Name}: {(step.Ok ? "ok" : "FAILED")} ({step.Detail}, {step.Ms} ms)");
        }
    });

    private static readonly TimeSpan ProbeDeadline = TimeSpan.FromMinutes(10);

    /// <summary>
    /// TASK-005: runs the guest probe for every shipped profile, in order, and logs each check. Default (MIGRATION row 24):
    /// the C# PRoot launcher (<see cref="RuntimeCoordinator.ProbeProfileAsync"/>); <paramref name="legacy"/>: the legacy
    /// <c>dsh.probe</c> (fallback).
    /// </summary>
    public Task RunProbesAsync(bool legacy = false) => legacy ? RunLegacyProbesAsync() : RunExclusive("dsh.probe (C#)", async () =>
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
            Append($"probe {profile}: {r.Status.ToUpperInvariant()} shim={r.Shim} ({sw.Elapsed.TotalSeconds:F1} s, C#)");
            foreach (var c in r.Checks)
            {
                Append($"  {(c.Ok ? "ok  " : "FAIL")} {c.Name}{(c.Ms is { } ms ? $" {ms} ms" : "")}: {c.Detail}");
            }
            summary.Add($"{profile}={r.Status}");
        }
        Append("probe summary (C#): " + string.Join(" ", summary));
    });

    private Task RunLegacyProbesAsync() => RunExclusive("dsh.probe", async () =>
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

    /// <summary>The legacy status line, from what C# reads on disk (no bridge call).</summary>
    private async Task LogCSharpStatusAsync(string what)
    {
        if (await Runtime.ReadFactsAsync(CancellationToken.None).ConfigureAwait(false) is not { } f)
        {
            Append($"{what} (C#): no runtime layout");
            return;
        }
        var s = Runtime.State.Current;
        Append($"{what} (C#): " + f.Describe(s.State.ToString(), s.Harness.ToString(), Runtime.Host?.TokenRevision is not null));
    }

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

    /// <summary>
    /// M1 acceptance of the legacy bridge itself: N concurrent round-trips; afterwards both in-flight tables must be 0.
    /// It tests the transitional JSON bridge, so it necessarily uses it (and goes away with it in PR-10).
    /// </summary>
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
