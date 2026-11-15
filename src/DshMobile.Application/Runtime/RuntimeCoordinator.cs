using System.Diagnostics;
using DshMobile.Application.Shadow;
using DshMobile.Application.Terminal;
using DshMobile.Core.Contracts;
using DshMobile.Runtime;
using DshMobile.Runtime.Automation;
using DshMobile.Runtime.Guest;
using DshMobile.Runtime.Hosting;
using DshMobile.Runtime.Layers;
using DshMobile.Runtime.Migration;
using DshMobile.Runtime.Plugins;

namespace DshMobile.Application.Runtime;

/// <summary>
/// Supervises runtime operations, all in C# since PR-10 (ADR-0018): the <see cref="DshHost"/> starts and owns the sandbox
/// and DSH processes, the runtime state is read from disk (<see cref="RuntimeStatusReader"/>), imports, the bundled DSH
/// layer, rollback, guest commands, plugins, probes and the terminal go through the C# layers. Keeps the PR-00 fix of the
/// former bridge flow:
/// <list type="bullet">
/// <item>start fails -> whatever was started is gone, then the foreground service is released; state Failed.</item>
/// <item>DSH given up by the health loop -> sandbox kept (terminal / diagnostics still work), service kept; harness Failed.</item>
/// <item>stop fails -> foreground service is kept (processes may still be alive); state Failed.</item>
/// </list>
/// Every transition goes through <see cref="RuntimeStateMachine"/> (durable journal). Operations are serialized.
/// </summary>
public sealed class RuntimeCoordinator : IDisposable
{
    /// <summary>Implementation name in the journal and the diagnostics.</summary>
    public const string Implementation = "csharp";

    /// <summary>Journal error code for a C#-hosted DSH the health loop gave up on.</summary>
    public const string DshUnhealthy = "DSH_UNHEALTHY";

    private readonly RuntimeLayout _layout;
    private readonly IForegroundServiceController _foreground;
    private readonly IDiagnosticsLog _log;
    private readonly IShadowReportSink _reports;
    private readonly RuntimeOptions _options;
    private readonly TimeProvider _time;
    private readonly IBundledAssets? _assets;
    private readonly Func<RuntimeLayout, Action<string>, TimeProvider, IDshHost> _hostFactory;
    private readonly SemaphoreSlim _op = new(1, 1);
    private readonly Lock _migration = new();
    private bool _namesMigrated;
    private IDshHost? _host;

    public RuntimeCoordinator(
        RuntimeLayout layout,
        RuntimeStateMachine state,
        IForegroundServiceController foreground,
        IDiagnosticsLog log,
        IShadowReportSink reports,
        RuntimeOptions? options = null,
        TimeProvider? time = null,
        PtyOpener? terminalOpener = null,
        Func<RuntimeLayout, Action<string>, TimeProvider, IDshHost>? hostFactory = null,
        IBundledAssets? bundledAssets = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _layout = layout;
        State = state;
        _foreground = foreground;
        _log = log;
        _reports = reports;
        _options = options ?? new RuntimeOptions();
        _time = time ?? TimeProvider.System;
        _assets = bundledAssets;
        _hostFactory = hostFactory ?? ((l, note, t) => new DshHost(l, note, t));
        Terminals = new TerminalSessions(terminalOpener ?? OpenTerminalAsync, log.Note);
    }

    public RuntimeStateMachine State { get; }

    /// <summary>The C#-owned terminal sessions (PR-06). Closed whenever the runtime stops.</summary>
    public TerminalSessions Terminals { get; }

    /// <summary>
    /// Raised right before the sandbox is stopped (normal stop, cleanup after a failed start): the C# guest-facing Relay
    /// (ADR-0010) stops here and the ui_* layer is removed. Handlers run synchronously and must not throw.
    /// </summary>
    public event Action? SandboxStopping;

    /// <summary>
    /// ADR-0015 decision 4: awaited when the C# host stops (and when a failed start is cleaned up), right before
    /// <see cref="SandboxStopping"/>, for the platform's companions. A failure is logged and does not block the stop.
    /// </summary>
    public Func<CancellationToken, Task>? HostStopping { get; set; }

    /// <summary>The C# host (created on the first start); exposes the launch token to the C# WebView only.</summary>
    public DshHost? Host => _host as DshHost;

    /// <summary>State of the C# host, or null before its first start.</summary>
    public DshHostState? HostState => _host?.State;

    /// <summary>
    /// True while the C# host owns the sandbox / DSH processes, including <see cref="DshHostState.Failed"/> (DSH given up,
    /// sandbox kept).
    /// </summary>
    public bool HostOwnsProcesses => _host is { OwnsProcesses: true };

    /// <summary>Whether the running DSH was spawned with the <c>ui_*</c> layer registered (null: unknown / not hosted).</summary>
    public bool? HostUiToolsAtStart => HostOwnsProcesses ? _host!.UiToolsAtStart : null;

    private async Task<ProotPtySession> OpenTerminalAsync(int rows, int columns, CancellationToken ct)
    {
        await EnsureGuestAssetsAsync(ct).ConfigureAwait(false);
        // Like the former TerminalController.newSession: make the app uid known to the guest first.
        var user = GuestUser.Provision(Path.Combine(_layout.RuntimeCurrentDir, "base"));
        if (user == GuestUser.Outcome.Updated)
        {
            _log.Note($"{TerminalSessions.Slice}: guest passwd entry written for {GuestUser.UserName}");
        }
        return await new ProotGuestProcess(_layout).OpenPtyAsync(new GuestExecutionOptions(), rows, columns, ct).ConfigureAwait(false);
    }

    private void NotifySandboxStopping()
    {
        try
        {
            SandboxStopping?.Invoke();
        }
#pragma warning disable CA1031 // a failing listener must not keep the sandbox running
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Note($"sandbox stopping listener failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task NotifyHostStoppingAsync(CancellationToken ct)
    {
        if (HostStopping is not { } hook)
        {
            return;
        }
        try
        {
            await hook(ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // a failing companion cleanup must not keep the runtime running
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Note($"host stopping hook failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task CloseTerminalsAsync()
    {
        if (await Terminals.CloseAllAsync().ConfigureAwait(false) is var n and > 0)
        {
            _log.Note($"{TerminalSessions.Slice}: closed {n} session(s) on stop");
        }
    }

    /// <summary>The installed runtime as read from disk.</summary>
    public Task<RuntimeFacts> ReadFactsAsync(CancellationToken ct) => Task.Run(() => RuntimeStatusReader.Read(_layout), ct);

    /// <summary>
    /// Stages the host files every guest path expects (link shim, probe / ui_* scripts, corepack cache, dsh / pnpm
    /// wrappers). Failures are logged and do not stop the caller; they show up as the guest command's own failure.
    /// </summary>
    private async Task<GuestAssetReport> EnsureGuestAssetsAsync(CancellationToken ct)
    {
        MigrateNamesOnce();
        var r = await GuestAssetProvisioner.EnsureAsync(_layout, _assets, ct).ConfigureAwait(false);
        if (r.Written.Count > 0 || !r.Ok)
        {
            _log.Note($"guest assets: {r.Describe()}");
        }
        return r;
    }

    /// <summary>
    /// ADR-0019: moves DSHBox-era names (<c>dshbox</c>) to <c>dshmobile</c> once per process, before the first sync or
    /// guest-asset staging, and only while no guest process is owned. A failed step is logged and retried next process.
    /// </summary>
    private void MigrateNamesOnce()
    {
        lock (_migration)
        {
            if (_namesMigrated || HostOwnsProcesses)
            {
                return;
            }
            _namesMigrated = true;
            var r = DshboxNameMigration.Run(_layout);
            if (r.Changed || !r.Ok)
            {
                _log.Note($"name migration (ADR-0019): {r.Describe()}");
            }
        }
    }

    /// <summary>
    /// Aligns the journal with the runtime as it is now and returns it; used at app start so the status line does not
    /// show what the previous process saw.
    /// </summary>
    public async Task<RuntimeSnapshot> RefreshStateAsync(CancellationToken ct)
    {
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SyncAsync(ct).ConfigureAwait(false);
            return State.Current;
        }
        finally
        {
            _op.Release();
        }
    }

    /// <summary>
    /// Aligns the journal with the disk before deciding a transition. While the host owns processes the journal follows
    /// the host instead (its own transitions).
    /// </summary>
    private async Task SyncAsync(CancellationToken ct)
    {
        if (HostOwnsProcesses)
        {
            return;
        }
        MigrateNamesOnce();
        var facts = await ReadFactsAsync(ct).ConfigureAwait(false);
        var actual = RuntimeStatusReader.Idle(facts, _time.GetUtcNow());
        State.Observe(actual.State, actual.Harness, actual.DshVersion, Implementation);
    }

    /// <summary><c>runtime.prepare</c>: installs the APK-bundled DSH layer when base/node are installed (MIGRATION row 17).</summary>
    public async Task<RuntimeInstallResult> PrepareAsync(CancellationToken ct)
    {
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SyncAsync(ct).ConfigureAwait(false);
            return await PrepareCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _op.Release();
        }
    }

    /// <summary>
    /// Caller holds the operation lock. Refuses to replace the DSH layer while sandbox / DSH processes are alive;
    /// keeping it is fine.
    /// </summary>
    private async Task<RuntimeInstallResult> PrepareCoreAsync(CancellationToken ct)
    {
        if (_assets is null)
        {
            return new RuntimeInstallResult(false, RuntimeErrorCodes.NotImplemented, "prepare needs the APK assets");
        }
        var installer = new DshLayerInstaller(_layout, _options.HostPathExists);
        var alive = HostOwnsProcesses || State.Current.State is RuntimeState.Running or RuntimeState.Starting or RuntimeState.Stopping;
        if (alive && BundledDsh.Plan(_assets, installer).WouldInstall)
        {
            _log.Note("runtime.prepare: refused, the bundled DSH layer would be replaced while processes are alive");
            return new RuntimeInstallResult(false, RuntimeErrorCodes.InvalidTransition, "stop DSH before the bundled DSH layer is replaced");
        }
        var r = await RuntimePreparer.PrepareAsync(_layout, _assets, ct, _options.HostPathExists).ConfigureAwait(false);
        var text = string.Join("; ", r.Steps);
        _log.Note(r.Ok ? $"runtime.prepare: ok ({text})" : $"runtime.prepare FAILED {r.Code}: {r.Message}");
        // The journal's DSH version follows the next state read (the installed layer's version record).
        return new RuntimeInstallResult(r.Ok, r.Code, r.Ok ? text : r.Message);
    }

    /// <summary>
    /// Starts the runtime with the C# <see cref="DshHost"/> (web profile only). A start whose preconditions fail returns
    /// their error code.
    /// </summary>
    public async Task<RuntimeStartResult> StartAsync(DshProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SyncAsync(ct).ConfigureAwait(false);
            if (profile.Name != RuntimeConstants.DshWebProfile)
            {
                return Refused($"the host starts only the '{RuntimeConstants.DshWebProfile}' profile");
            }
            if (_host is { State: DshHostState.Ready })
            {
                _log.Note("start: host already ready");
                return new RuntimeStartResult(true, RuntimeErrorCodes.Ok, "already running", State.Current);
            }
            // Only a host that still owns processes (health loop gave up, sandbox kept). A failed START also ends in
            // Failed but owns nothing.
            if (_host is { State: DshHostState.Failed, OwnsProcesses: true } failed)
            {
                // DSH was given up with the sandbox kept: restart the whole host; nothing of it can be reused safely.
                _log.Note("start: the host failed earlier (sandbox kept); stopping it before a fresh start");
                var stopped = await StopHostAsync(failed, ct).ConfigureAwait(false);
                if (!stopped.Ok)
                {
                    return stopped;
                }
            }
            if (State.Current.State is RuntimeState.Running or RuntimeState.Starting)
            {
                return Refused($"cannot start while the runtime is {State.Current.State}");
            }
            if (!State.TryTransition(RuntimeState.Starting, "start requested", HarnessState.Stopped))
            {
                return Refused($"cannot start while {State.Current.State}");
            }
            _host ??= CreateHost();
            _foreground.Acquire();
            var sw = Stopwatch.StartNew();
            await EnsureGuestAssetsAsync(ct).ConfigureAwait(false);
            _log.Note("start: DshHost");
            var r = await _host.StartAsync(ct).ConfigureAwait(false);
            _log.Note(r.Ok ? $"host: ready in {sw.Elapsed.TotalSeconds:F1} s" : $"host: FAILED {r.Code}: {r.Message}");
            if (!r.Ok)
            {
                // The host already killed whatever it started; companions that follow the sandbox stop too, then nothing
                // is left for the service to keep alive.
                await NotifyHostStoppingAsync(CancellationToken.None).ConfigureAwait(false);
                NotifySandboxStopping();
                _foreground.Release();
                State.TryTransition(RuntimeState.Failed, "host start failed", HarnessState.Stopped, r.Code, r.Message);
                return new RuntimeStartResult(false, r.Code, r.Message, State.Current);
            }
            State.TryTransition(RuntimeState.Running, "host ready", HarnessState.Ready);
            return new RuntimeStartResult(true, RuntimeErrorCodes.Ok, null, State.Current);
        }
        finally
        {
            _op.Release();
        }
    }

    public async Task<RuntimeStartResult> StopAsync(CancellationToken ct)
    {
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_host is { OwnsProcesses: true } host)
            {
                return await StopHostAsync(host, ct).ConfigureAwait(false);
            }
            await SyncAsync(ct).ConfigureAwait(false);
            // Nothing hosted: the C# terminal opens its own PRoot shells without a started sandbox, so they can be the only
            // thing running (device run 2026-11-12). Close them; then releasing the service cannot orphan anything.
            await CloseTerminalsAsync().ConfigureAwait(false);
            _foreground.Release();
            _log.Note("stop: nothing running");
            return new RuntimeStartResult(true, RuntimeErrorCodes.Ok, "already stopped", State.Current);
        }
        finally
        {
            _op.Release();
        }
    }

    private IDshHost CreateHost()
    {
        var host = _hostFactory(_layout, _log.Note, _time);
        host.StateChanged += (_, _) => OnHostStateChanged(host);
        return host;
    }

    /// <summary>
    /// ADR-0015 decision 3: the health loop giving up (DSH stopped, sandbox kept) is journaled as "DSH failed, sandbox
    /// kept". Start / stop transitions are journaled by their own operations (they hold <see cref="_op"/>).
    /// </summary>
    private void OnHostStateChanged(IDshHost host)
    {
        if (host.State == DshHostState.Failed && State.Current is { State: RuntimeState.Running, Harness: HarnessState.Ready })
        {
            _log.Note("host: DSH given up by the health loop (sandbox kept); start restarts the host, stop releases everything");
            // Running keeps no error code in the journal: the code is in the reason.
            State.TryTransition(RuntimeState.Running, $"host: dsh unhealthy ({DshUnhealthy})", HarnessState.Failed);
        }
    }

    /// <summary>Caller holds <see cref="_op"/>.</summary>
    private async Task<RuntimeStartResult> StopHostAsync(IDshHost host, CancellationToken ct)
    {
        State.TryTransition(RuntimeState.Stopping, "stop requested");
        await CloseTerminalsAsync().ConfigureAwait(false);
        await NotifyHostStoppingAsync(ct).ConfigureAwait(false);
        NotifySandboxStopping();
        var r = await host.StopAsync(ct).ConfigureAwait(false);
        _log.Note(r.Ok ? "host: stopped, no marker process left" : $"host: stop FAILED {r.Code}: {r.Message}");
        if (!r.Ok)
        {
            State.TryTransition(RuntimeState.Failed, "host stop incomplete", HarnessState.Stopped, r.Code, r.Message);
            _log.Note("stop incomplete: foreground service KEPT so remaining processes are not orphaned; retry stop");
            return new RuntimeStartResult(false, r.Code, r.Message, State.Current);
        }
        _foreground.Release();
        State.TryTransition(RuntimeState.Ready, "stopped", HarnessState.Stopped);
        return new RuntimeStartResult(true, RuntimeErrorCodes.Ok, null, State.Current);
    }

    /// <summary>
    /// What the host itself sees, for diagnostics. Read-only; token-free (states, pids, HTTP status only).
    /// </summary>
    public async Task<IReadOnlyList<string>> DescribeHostAsync(CancellationToken ct)
    {
        if (_host is not { } host)
        {
            return ["host: not started in this process"];
        }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var snap = State.Current;
        var lines = new List<string>
        {
            $"host: {host.State} · journal {snap.State}/{snap.Harness} gen={snap.Generation}"
                + (snap.ErrorCode is { } e ? $" last error {e}" : "")
                + $" · ui_* at DSH start={(host.UiToolsAtStart is { } u ? (u ? "registered" : "off") : "-")}",
        };
        var health = await DshMobile.Runtime.Supervision.HttpHealthProbe.CheckAsync(ct: ct).ConfigureAwait(false);
        lines.Add($"host health: webUi={health.WebUiReady} http={health.HttpStatus?.ToString(inv) ?? "-"}");
        if (!OperatingSystem.IsWindows() && DshMobile.Runtime.Supervision.RuntimeProcessSnapshot.Observe(Environment.ProcessId) is { } p)
        {
            lines.Add($"host /proc: sandbox root={p.SandboxRootPid?.ToString(inv) ?? "-"} ({p.SandboxTreeSize}) dsh root={p.DshRootPid?.ToString(inv) ?? "-"} ({p.DshTreeSize})");
        }
        return lines;
    }

    /// <summary>
    /// Imports a runtime bundle with the <see cref="LayerInstaller"/>; the installed tree is then checked against a fresh
    /// dry-run of the same ZIP (report <c>runtime.layer-install</c>), the bundled DSH layer is provisioned, and the result
    /// must pass the install check.
    /// </summary>
    public async Task<RuntimeInstallResult> InstallAsync(RuntimePackage package, IProgress<RuntimeProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(package);
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SyncAsync(ct).ConfigureAwait(false);
            var before = State.Current.State;
            if (HostOwnsProcesses || !State.TryTransition(RuntimeState.Staging, "import requested"))
            {
                return new RuntimeInstallResult(false, RuntimeErrorCodes.InvalidTransition, $"cannot import while {before}");
            }
            var installed = await new LayerInstaller(_layout, _options.HostPathExists).InstallAsync(package.Path, progress, ct).ConfigureAwait(false);
            _log.Note(installed.Ok
                ? $"install: ok in {installed.ElapsedMs} ms ({string.Join(", ", installed.Layers.Select((l, i) => $"{installed.Inspection!.Layers[i].Layer}={l.Entries.Count}"))})"
                : $"install: FAILED {installed.Code}: {installed.Message}");
            if (!installed.Ok)
            {
                State.TryTransition(RuntimeState.Failed, "import failed", errorCode: installed.Code, errorMessage: installed.Message);
                return new RuntimeInstallResult(false, RuntimeErrorCodes.Upstream, $"{installed.Code}: {installed.Message}", installed.Code);
            }

            var verify = await LayerExtractShadowCheck.RunAsync(package.Path, installed.Inspection!, _layout, _options.HostPathExists, _time, ct).ConfigureAwait(false);
            verify = verify with { Slice = LayerExtractShadowCheck.InstallSlice };
            _reports.Report(verify);
            _log.Note($"verify {verify.Slice}: {verify.Verdict} ({verify.Detail}; {verify.CSharpMs} ms)");

            var prepare = await PrepareCoreAsync(ct).ConfigureAwait(false);
            var facts = await ReadFactsAsync(ct).ConfigureAwait(false);
            if (verify.Verdict != ShadowVerdict.Agree || !prepare.Ok || !facts.RuntimeInstalled)
            {
                var why = verify.Verdict != ShadowVerdict.Agree ? $"installed tree check {verify.Verdict}"
                    : !prepare.Ok ? $"runtime.prepare failed: {prepare.Message}"
                    : $"runtime install check: {facts.InstallReason}";
                State.TryTransition(RuntimeState.Failed, "import incomplete", HarnessState.Stopped, "RUNTIME_IMPORT_UNVERIFIED", why);
                return new RuntimeInstallResult(false, "RUNTIME_IMPORT_UNVERIFIED", why);
            }
            State.TryTransition(RuntimeState.Ready, "import done", HarnessState.Stopped, dshVersion: facts.DshVersion);
            return new RuntimeInstallResult(true, RuntimeErrorCodes.Ok, null);
        }
        finally
        {
            _op.Release();
        }
    }

    /// <summary>
    /// MIGRATION row 18: puts the previous runtime body (<c>runtime-current/previous</c>) back with
    /// <see cref="LayerRollback"/>. Refused while any runtime process may be running; the journal goes through
    /// <see cref="RuntimeState.Staging"/> and ends in what is then read from disk (or Failed with the rollback code).
    /// </summary>
    public async Task<RuntimeInstallResult> RollbackAsync(CancellationToken ct)
    {
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SyncAsync(ct).ConfigureAwait(false);
            var before = State.Current.State;
            if (HostOwnsProcesses || before is RuntimeState.Running or RuntimeState.Starting or RuntimeState.Stopping)
            {
                _log.Note("rollback: refused, stop the runtime first");
                return new RuntimeInstallResult(false, RuntimeErrorCodes.InvalidTransition, $"stop the runtime before rolling back (runtime {before})");
            }
            if (!LayerRollback.Available(_layout))
            {
                _log.Note($"rollback: {LayerRollback.NoRollbackSlot} (no previous runtime body)");
                return new RuntimeInstallResult(false, LayerRollback.NoRollbackSlot, "no previous runtime body to roll back to");
            }
            if (!State.TryTransition(RuntimeState.Staging, "rollback requested"))
            {
                return new RuntimeInstallResult(false, RuntimeErrorCodes.InvalidTransition, $"cannot roll back while {before}");
            }
            var r = await Task.Run(() => LayerRollback.Rollback(_layout), ct).ConfigureAwait(false);
            var facts = await ReadFactsAsync(ct).ConfigureAwait(false);
            _log.Note(r.Ok
                ? $"rollback: restored {string.Join(", ", r.Restored)}; runtime={facts.RuntimeInstalled} ({facts.InstallReason})"
                : $"rollback FAILED {r.Code}: {r.Message}");
            if (!r.Ok)
            {
                State.TryTransition(RuntimeState.Failed, "rollback failed", errorCode: r.Code, errorMessage: r.Message);
                return new RuntimeInstallResult(false, r.Code, r.Message, r.Code);
            }
            if (!facts.RuntimeInstalled)
            {
                State.TryTransition(RuntimeState.Failed, "rollback incomplete", errorCode: "RUNTIME_ROLLBACK_UNVERIFIED", errorMessage: facts.InstallReason);
                return new RuntimeInstallResult(false, "RUNTIME_ROLLBACK_UNVERIFIED", $"restored body fails the install check: {facts.InstallReason}");
            }
            State.TryTransition(RuntimeState.Ready, "rolled back", HarnessState.Stopped, dshVersion: facts.DshVersion);
            return new RuntimeInstallResult(true, RuntimeErrorCodes.Ok, $"restored {string.Join(", ", r.Restored)}");
        }
        finally
        {
            _op.Release();
        }
    }

    /// <summary>
    /// The fixed <see cref="GuestDiagnostics.Commands"/> through the C# PRoot launcher, each in its own PRoot (independent
    /// of the host).
    /// </summary>
    public async Task<IReadOnlyList<(string Key, GuestProcessResult Result)>> RunGuestDiagnosticsAsync(CancellationToken ct)
    {
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureGuestAssetsAsync(ct).ConfigureAwait(false);
            var sw = Stopwatch.StartNew();
            var results = await new ProotGuestProcess(_layout).RunDiagnosticsAsync(ct).ConfigureAwait(false);
            _log.Note($"guest diagnostics: {results.Count} commands in {sw.ElapsedMilliseconds} ms");
            return results;
        }
        finally
        {
            _op.Release();
        }
    }

    /// <summary>
    /// <c>relay.selftest</c>: stages the ui_* guest scripts, then runs <see cref="UiFacadeAssets.SelfTestCommand"/> through
    /// the C# PRoot launcher, serialized with the other runtime operations. The result is the check's own exit code and
    /// output (last 400 lines), never inferred.
    /// </summary>
    public async Task<GuestProcessResult> RunRelaySelfTestAsync(TimeSpan timeout, CancellationToken ct)
    {
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                UiFacadeAssets.Stage(_layout.GuestCacheDir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new GuestProcessResult(false, null, "", $"{UiFacadeAssets.NotStaged}: {e.Message}");
            }
            await EnsureGuestAssetsAsync(ct).ConfigureAwait(false);
            var sw = Stopwatch.StartNew();
            var result = await new ProotGuestProcess(_layout)
                .RunShellAsync(UiFacadeAssets.SelfTestCommand, new GuestExecutionOptions(Timeout: timeout, MaxOutputLines: UiFacadeAssets.SelfTestMaxLines), ct)
                .ConfigureAwait(false);
            _log.Note($"relay selftest: exit={result.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"} in {sw.ElapsedMilliseconds} ms");
            return result;
        }
        finally
        {
            _op.Release();
        }
    }

    /// <summary>Plugin list: <c>dsh plugin list --json --depth 0</c> through the C# PRoot launcher.</summary>
    public async Task<GuestPluginResult> ListPluginsAsync(string profile, CancellationToken ct)
    {
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureGuestAssetsAsync(ct).ConfigureAwait(false);
            return await GuestPlugins.RunAsync(new ProotGuestProcess(_layout), "list", profile, null, ct).ConfigureAwait(false);
        }
        finally
        {
            _op.Release();
        }
    }

    /// <summary>
    /// <c>dsh plugin add/remove</c> through the C# PRoot launcher with the ported install guard: an install that exits 0
    /// but added a package that cannot load is rolled back (only the packages this install added); a remove is checked
    /// against the profile's dependencies afterwards. Bad arguments are the shared <c>PluginCommand</c> rules
    /// (<see cref="RuntimeErrorCodes.BadRequest"/>, nothing runs).
    /// </summary>
    public async Task<PluginChangeResult> ChangePluginAsync(string verb, string profile, string operand, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operand);
        if (verb is not ("add" or "remove"))
        {
            return new PluginChangeResult(false, RuntimeErrorCodes.BadRequest, "unknown plugin verb: " + verb, []);
        }
        if (!PluginCommand.TryBuild(verb, profile, operand, out _, out var error))
        {
            return new PluginChangeResult(false, RuntimeErrorCodes.BadRequest, error, []);
        }
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureGuestAssetsAsync(ct).ConfigureAwait(false);
            var sw = Stopwatch.StartNew();
            var guard = new PluginInstallGuard(new GuestPluginOps(new ProotGuestProcess(_layout), profile), new PluginProfileReader(_layout, profile));
            var r = verb == "add"
                ? await guard.InstallAsync(operand, SpecPackageName(operand), ct).ConfigureAwait(false)
                : await guard.RemoveAsync(operand, ct).ConfigureAwait(false);
            _log.Note($"plugin.{verb} {operand}: " + (r.Ok ? "ok" : $"{r.Code}{(r.RolledBack ? " (rolled back)" : "")}") + $" in {sw.Elapsed.TotalSeconds:F1} s"
                + (r.Lines.FirstOrDefault(l => l.StartsWith("guard:", StringComparison.Ordinal)) is { } g ? "; " + g : ""));
            return r;
        }
        finally
        {
            _op.Release();
        }
    }

    /// <summary>The package name of a validated <c>name[@range]</c> spec (verification-only fallback for the guard).</summary>
    public static string SpecPackageName(string spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var at = spec.LastIndexOf('@');
        return at > 0 ? spec[..at] : spec;
    }

    /// <summary>
    /// TASK-005 / MIGRATION row 24: probes one shipped DSH profile through the C# PRoot launcher (<see cref="DshProbe"/>),
    /// serialized with the other runtime operations. Independent of the host: the probe launches DSH itself in a fresh guest.
    /// </summary>
    public async Task<DshProbeOutcome> ProbeProfileAsync(string profile, CancellationToken ct)
    {
        if (!DshProfiles.All.Contains(profile))
        {
            return new DshProbeOutcome(profile, null, DshProbe.UnknownProfile, $"profile must be one of {string.Join(", ", DshProfiles.All)}");
        }
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureGuestAssetsAsync(ct).ConfigureAwait(false);
            var run = await new ProotGuestProcess(_layout)
                .RunShellAsync(DshProbe.Command(profile), new GuestExecutionOptions(Timeout: DshProbe.Timeout, MaxOutputLines: DshProbe.MaxLines), ct)
                .ConfigureAwait(false);
            return DshProbe.Parse(profile, run);
        }
        finally
        {
            _op.Release();
        }
    }

    /// <summary>
    /// PR-06 developer acceptance: a C#-owned PTY running the guest login shell, driven by <see cref="PtyProbe"/>.
    /// </summary>
    public async Task<PtyProbeReport> ProbePtyAsync(CancellationToken ct)
    {
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureGuestAssetsAsync(ct).ConfigureAwait(false);
            var exec = new ProotGuestProcess(_layout);
            var report = await PtyProbe.RunAsync(
                c => exec.OpenPtyAsync(new GuestExecutionOptions(), 24, 80, c),
                pid => DshMobile.Runtime.Supervision.ProcessTable.Read()?.DescendantPids(pid) ?? [],
                pid => Directory.Exists("/proc/" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ct).ConfigureAwait(false);
            _log.Note($"{PtyProbe.Slice}: {report.Summary}");
            return report;
        }
        finally
        {
            _op.Release();
        }
    }

    private RuntimeStartResult Refused(string message)
    {
        _log.Note("refused: " + message);
        return new RuntimeStartResult(false, RuntimeErrorCodes.InvalidTransition, message, State.Current);
    }

    public void Dispose() => _op.Dispose();
}
