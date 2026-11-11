using System.Diagnostics;
using DshMobile.Application.Shadow;
using DshMobile.Runtime;
using DshMobile.Runtime.Bundle;
using DshMobile.Runtime.Guest;
using DshMobile.Runtime.Hosting;
using DshMobile.Runtime.Layers;

namespace DshMobile.Application.Runtime;

/// <summary>
/// Supervises runtime operations on top of an <see cref="IRuntimeManager"/> (legacy bridge today). Fixes the PR-00
/// defect in the former BridgeHost flow: the foreground service was started first and never released when the
/// sandbox failed to start, and was stopped even when stopping failed (leaving PRoot children without it).
/// <list type="bullet">
/// <item>sandbox start fails -> best-effort sandbox stop, then foreground service released; state Failed.</item>
/// <item>DSH start fails -> sandbox kept (terminal/diagnostics still work), service kept; harness Failed.</item>
/// <item>stop fails -> foreground service is kept (processes may still be alive); state Failed.</item>
/// </list>
/// Every transition goes through <see cref="RuntimeStateMachine"/> (durable journal). Operations are serialized.
/// </summary>
public sealed class RuntimeCoordinator : IDisposable
{
    private readonly IRuntimeManager _runtime;
    private readonly IForegroundServiceController _foreground;
    private readonly IDiagnosticsLog _log;
    private readonly IShadowReportSink _shadow;
    private readonly MigrationOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _op = new(1, 1);

    public RuntimeCoordinator(
        IRuntimeManager runtime,
        RuntimeStateMachine state,
        IForegroundServiceController foreground,
        IDiagnosticsLog log,
        IShadowReportSink shadow,
        MigrationOptions options,
        TimeProvider? time = null,
        RuntimeLayout? layout = null)
    {
        _runtime = runtime;
        State = state;
        _foreground = foreground;
        _log = log;
        _shadow = shadow;
        _options = options;
        _time = time ?? TimeProvider.System;
        _layout = layout;
    }

    /// <summary>Where the legacy installer puts the layers; required for the layer-extraction shadow.</summary>
    private readonly RuntimeLayout? _layout;

    public RuntimeStateMachine State { get; }

    public string Implementation => _runtime.Implementation;

    /// <summary>Feeds an externally observed snapshot (legacy runtime.state event) into the journal.</summary>
    public void Observe(RuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        State.Observe(snapshot.State, snapshot.Harness, snapshot.DshVersion, _runtime.Implementation);
    }

    /// <param name="mode"><see cref="SliceMode.CSharp"/>: developer opt-in - the C# <see cref="DshHost"/> starts and owns
    /// the sandbox and DSH processes (no DSHBox code); otherwise the legacy runtime starts them.</param>
    public async Task<RuntimeStartResult> StartAsync(DshProfile profile, CancellationToken ct, SliceMode? mode = null)
    {
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SyncAsync(ct).ConfigureAwait(false);
            if (mode == SliceMode.CSharp)
            {
                return await StartWithCSharpHostAsync(profile, ct).ConfigureAwait(false);
            }
            if (_host is { IsActive: true })
            {
                return Refused("the C# host is running; stop it before starting the legacy runtime");
            }
            var current = State.Current;
            if (current.State == RuntimeState.Running)
            {
                // Sandbox already up (e.g. retry after a DSH failure): only the harness needs starting.
                _foreground.Acquire();
                if (current.Harness is HarnessState.Starting or HarnessState.Running or HarnessState.Ready)
                {
                    _log.Note($"start: already running (harness {current.Harness})");
                    return new RuntimeStartResult(true, RuntimeErrorCodes.Ok, "already running", current);
                }
                return await StartHarnessAsync(profile, ct).ConfigureAwait(false);
            }
            if (!State.TryTransition(RuntimeState.Starting, "start requested", HarnessState.Stopped))
            {
                return Refused($"cannot start while {current.State}");
            }
            _foreground.Acquire();
            var sw = Stopwatch.StartNew();
            var sandbox = await _runtime.StartSandboxAsync(ct).ConfigureAwait(false);
            _log.Note($"sandbox.start ({sw.ElapsedMilliseconds} ms): {Describe(sandbox)}");
            // Legacy sandbox.start answers ok=true even when the sandbox ended in ERROR: the reported state decides.
            if (!sandbox.Ok || sandbox.Snapshot.State != RuntimeState.Running)
            {
                // Unified cleanup: nothing may stay half-started, and the service must not outlive it.
                var cleanup = await _runtime.StopSandboxAsync(CancellationToken.None).ConfigureAwait(false);
                if (cleanup.Ok)
                {
                    _foreground.Release();
                }
                _log.Note($"sandbox.start cleanup: {Describe(cleanup)}{(cleanup.Ok ? "; foreground service released" : "; foreground service KEPT (stop failed)")}");
                State.TryTransition(RuntimeState.Failed, "sandbox start failed", HarnessState.Stopped,
                    sandbox.UpstreamCode ?? sandbox.Code, sandbox.Message ?? $"sandbox state {sandbox.Snapshot.State}");
                return sandbox with { Ok = false, Snapshot = State.Current };
            }
            State.TryTransition(RuntimeState.Running, "sandbox running", HarnessState.Stopped);
            return await StartHarnessAsync(profile, ct).ConfigureAwait(false);
        }
        finally
        {
            _op.Release();
        }
    }

    /// <summary>Caller holds <see cref="_op"/> and the sandbox is Running.</summary>
    private async Task<RuntimeStartResult> StartHarnessAsync(DshProfile profile, CancellationToken ct)
    {
        State.TryTransition(RuntimeState.Running, "harness start requested", HarnessState.Starting);
        var sw = Stopwatch.StartNew();
        var dsh = await _runtime.StartHarnessAsync(profile, ct).ConfigureAwait(false);
        _log.Note($"dsh.start {profile.Name} ({sw.Elapsed.TotalSeconds:F1} s): {Describe(dsh)}");
        // DSH failing leaves the sandbox (and the service keeping it alive) up on purpose: terminal and diagnostics
        // stay usable, and Stop releases everything.
        var harness = dsh.Ok ? dsh.Snapshot.Harness : HarnessState.Failed;
        State.TryTransition(RuntimeState.Running, dsh.Ok ? "harness started" : "harness start failed", harness, dshVersion: dsh.Snapshot.DshVersion);
        return dsh with { Snapshot = State.Current };
    }

    public async Task<RuntimeStartResult> StopAsync(CancellationToken ct)
    {
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_host is { State: not DshHostState.Stopped } host)
            {
                return await StopCSharpHostAsync(host, ct).ConfigureAwait(false);
            }
            await SyncAsync(ct).ConfigureAwait(false);
            if (State.Current.State is RuntimeState.Ready or RuntimeState.NotInstalled)
            {
                // Nothing runs, so releasing the service cannot orphan anything (and frees a stale one).
                _foreground.Release();
                _log.Note("stop: nothing running");
                return new RuntimeStartResult(true, RuntimeErrorCodes.Ok, "already stopped", State.Current);
            }
            if (!State.TryTransition(RuntimeState.Stopping, "stop requested"))
            {
                return Refused($"cannot stop while {State.Current.State}");
            }
            var dsh = await _runtime.StopHarnessAsync(ct).ConfigureAwait(false);
            _log.Note($"dsh.stop: {Describe(dsh)}");
            var sandbox = await _runtime.StopSandboxAsync(ct).ConfigureAwait(false);
            _log.Note($"sandbox.stop: {Describe(sandbox)}");
            var stopped = dsh.Ok && sandbox.Ok && sandbox.Snapshot.State is RuntimeState.Ready or RuntimeState.NotInstalled;
            if (!stopped)
            {
                var failed = !sandbox.Ok ? sandbox : dsh;
                State.TryTransition(RuntimeState.Failed, "stop failed", HarnessState.Stopped, failed.UpstreamCode ?? failed.Code,
                    failed.Message ?? $"sandbox state {sandbox.Snapshot.State}");
                _log.Note("stop incomplete: foreground service KEPT so remaining processes are not orphaned; retry stop");
                return failed with { Ok = false, Snapshot = State.Current };
            }
            _foreground.Release();
            State.TryTransition(RuntimeState.Ready, "stopped", HarnessState.Stopped);
            return sandbox with { Snapshot = State.Current };
        }
        finally
        {
            _op.Release();
        }
    }

    /// <summary>
    /// Imports a runtime bundle through the legacy installer. In <see cref="SliceMode.LegacyWithShadow"/> the C#
    /// inspector reads the same ZIP concurrently (read-only) and the comparison is reported; the legacy result stands.
    /// </summary>
    /// <param name="mode">Overrides <see cref="MigrationOptions.BundleImport"/> for this import only (developer opt-in
    /// to <see cref="SliceMode.CSharp"/> for device acceptance).</param>
    public async Task<RuntimeInstallResult> InstallAsync(RuntimePackage package, IProgress<RuntimeProgress>? progress, CancellationToken ct, SliceMode? mode = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        var effective = mode ?? _options.BundleImport;
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SyncAsync(ct).ConfigureAwait(false);
            var before = State.Current.State;
            if (!State.TryTransition(RuntimeState.Staging, effective == SliceMode.CSharp ? "import requested (csharp)" : "import requested"))
            {
                return new RuntimeInstallResult(false, RuntimeErrorCodes.InvalidTransition, $"cannot import while {before}");
            }
            if (effective == SliceMode.CSharp)
            {
                return await InstallWithCSharpAsync(package, progress, ct).ConfigureAwait(false);
            }
            Task<(BundleInspection Result, long Ms)>? shadow = null;
            if (effective == SliceMode.LegacyWithShadow)
            {
                shadow = Task.Run(async () =>
                {
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        var r = await RuntimeBundleInspector.InspectAsync(package.Path, null, ct).ConfigureAwait(false);
                        return (r, sw.ElapsedMilliseconds);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        return (new BundleInspection(false, "SHADOW_IO_ERROR", e.Message, null, []), sw.ElapsedMilliseconds);
                    }
                }, ct);
            }

            var legacy = await _runtime.InstallAsync(package, progress, ct).ConfigureAwait(false);
            if (shadow is not null)
            {
                var (inspection, ms) = await shadow.ConfigureAwait(false);
                var report = BundleShadowCheck.Compare(legacy, inspection, ms, _time.GetUtcNow());
                _shadow.Report(report);
                _log.Note($"shadow {report.Slice}: {report.Verdict} ({report.Detail}; csharp {ms} ms)");

                // LayerInstaller shadow: still under the operation lock, so nothing has touched the fresh layers yet.
                if (legacy.Ok && inspection.Ok && _layout is not null && _options.LayerExtract == SliceMode.LegacyWithShadow)
                {
                    _log.Note("shadow runtime.layer-extract: comparing C# dry-run extraction with the installed layers ...");
                    var layers = await LayerExtractShadowCheck.RunAsync(package.Path, inspection, _layout, _options.HostPathExists, _time, ct).ConfigureAwait(false);
                    _shadow.Report(layers);
                    _log.Note($"shadow {layers.Slice}: {layers.Verdict} ({layers.Detail}; {layers.CSharpMs} ms)");
                }
            }

            var after = await _runtime.GetStateAsync(ct).ConfigureAwait(false);
            if (legacy.Ok)
            {
                State.TryTransition(after.State == RuntimeState.NotInstalled ? RuntimeState.NotInstalled : RuntimeState.Ready, "import done", after.Harness, dshVersion: after.DshVersion);
            }
            else
            {
                State.TryTransition(RuntimeState.Failed, "import failed", after.Harness, legacy.UpstreamCode ?? legacy.Code, legacy.Message);
            }
            return legacy;
        }
        finally
        {
            _op.Release();
        }
    }

    /// <summary>
    /// <see cref="SliceMode.CSharp"/> import (caller holds the lock, state is Staging): the C# <see cref="LayerInstaller"/>
    /// decides; the installed tree is then checked against a fresh dry-run of the same ZIP (slice
    /// <c>runtime.layer-install</c>); finally the legacy <c>runtime.prepare</c> provisions the APK-bundled DSH layer,
    /// exactly as the legacy import does after its own install.
    /// </summary>
    private async Task<RuntimeInstallResult> InstallWithCSharpAsync(RuntimePackage package, IProgress<RuntimeProgress>? progress, CancellationToken ct)
    {
        if (_layout is null)
        {
            State.TryTransition(RuntimeState.Failed, "csharp import unavailable", errorCode: RuntimeErrorCodes.NotImplemented, errorMessage: "no runtime layout");
            return new RuntimeInstallResult(false, RuntimeErrorCodes.NotImplemented, "C# import needs the runtime layout");
        }
        _log.Note("import: C# LayerInstaller (legacy importer not used)");
        var installed = await new LayerInstaller(_layout, _options.HostPathExists).InstallAsync(package.Path, progress, ct).ConfigureAwait(false);
        _log.Note(installed.Ok
            ? $"csharp install: ok in {installed.ElapsedMs} ms ({string.Join(", ", installed.Layers.Select((l, i) => $"{installed.Inspection!.Layers[i].Layer}={l.Entries.Count}"))})"
            : $"csharp install: FAILED {installed.Code}: {installed.Message}");
        if (!installed.Ok)
        {
            State.TryTransition(RuntimeState.Failed, "csharp import failed", errorCode: installed.Code, errorMessage: installed.Message);
            return new RuntimeInstallResult(false, RuntimeErrorCodes.Upstream, $"{installed.Code}: {installed.Message}", installed.Code);
        }

        var verify = await LayerExtractShadowCheck.RunAsync(package.Path, installed.Inspection!, _layout, _options.HostPathExists, _time, ct).ConfigureAwait(false);
        verify = verify with { Slice = "runtime.layer-install" };
        _shadow.Report(verify);
        _log.Note($"verify {verify.Slice}: {verify.Verdict} ({verify.Detail}; {verify.CSharpMs} ms)");

        var prepare = await _runtime.PrepareAsync(ct).ConfigureAwait(false);
        _log.Note(prepare.Ok ? $"runtime.prepare: ok ({prepare.Message})" : $"runtime.prepare FAILED {prepare.Code}: {prepare.Message}");

        var after = await _runtime.GetStateAsync(ct).ConfigureAwait(false);
        if (verify.Verdict != ShadowVerdict.Agree || !prepare.Ok || after.State == RuntimeState.NotInstalled)
        {
            var why = verify.Verdict != ShadowVerdict.Agree ? $"installed tree check {verify.Verdict}"
                : !prepare.Ok ? $"runtime.prepare failed: {prepare.Message}"
                : "legacy runtime does not recognize the installed layers";
            State.TryTransition(RuntimeState.Failed, "csharp import incomplete", after.Harness, "RUNTIME_IMPORT_UNVERIFIED", why);
            return new RuntimeInstallResult(false, "RUNTIME_IMPORT_UNVERIFIED", why);
        }
        State.TryTransition(RuntimeState.Ready, "import done (csharp)", after.Harness, dshVersion: after.DshVersion);
        return new RuntimeInstallResult(true, RuntimeErrorCodes.Ok, null);
    }

    /// <summary>
    /// Shadow for the guest-exec slice: runs <see cref="GuestDiagnostics.Commands"/> through the C# PRoot launcher and
    /// compares with the legacy diagnostics results the caller just obtained. Null when the slice is not shadowed.
    /// </summary>
    public async Task<ShadowReport?> ShadowGuestDiagnosticsAsync(IReadOnlyList<(string Key, bool Ok, string Output)> legacy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        if (_options.GuestExec != SliceMode.LegacyWithShadow || _layout is null)
        {
            return null;
        }
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var exec = new ProotGuestProcess(_layout);
            // Paths only (no secrets): lets the device run compare with the legacy process's /proc/<pid>/cmdline.
            _log.Note("csharp guest argv: " + string.Join(' ', exec.Builder.GuestCommand(GuestDiagnostics.Commands[0].Command)));
            var sw = Stopwatch.StartNew();
            var results = await exec.RunDiagnosticsAsync(ct).ConfigureAwait(false);
            var report = GuestExecShadowCheck.Compare(legacy, results, sw.ElapsedMilliseconds, _time.GetUtcNow());
            _shadow.Report(report);
            _log.Note($"shadow {report.Slice}: {report.Verdict} ({report.Detail}; csharp {report.CSharpMs} ms)");
            return report;
        }
        finally
        {
            _op.Release();
        }
    }

    /// <summary>
    /// Read-only supervisor shadow: C# looks at <c>/proc</c> and probes the WebUI itself, and compares with the legacy
    /// status / health the caller just obtained. Never starts, stops or signals anything.
    /// </summary>
    public async Task<ShadowReport?> ShadowSupervisorAsync(string legacySandboxState, string legacyDshState, bool legacyHealthOk, CancellationToken ct)
    {
        if (_options.GuestExec != SliceMode.LegacyWithShadow)
        {
            return null;
        }
        if (_host is { IsActive: true })
        {
            _log.Note("shadow runtime.supervisor-observe: skipped (the C# host owns the processes; legacy reports them as stopped)");
            return null;
        }
        var sw = Stopwatch.StartNew();
        var processes = OperatingSystem.IsWindows() ? null : DshMobile.Runtime.Supervision.RuntimeProcessSnapshot.Observe(Environment.ProcessId);
        var health = await DshMobile.Runtime.Supervision.HttpHealthProbe.CheckAsync(ct: ct).ConfigureAwait(false);
        var report = SupervisorObserveShadowCheck.Compare(legacySandboxState, legacyDshState, legacyHealthOk, processes, health, sw.ElapsedMilliseconds, _time.GetUtcNow());
        _shadow.Report(report);
        _log.Note($"shadow {report.Slice}: {report.Verdict} ({report.Detail}; csharp {report.CSharpMs} ms)");

        // Launcher argv: what C# would start vs the exact argv of the running legacy processes.
        if (processes is not null && _layout is not null && (processes.SandboxRootPid is not null || processes.DshRootPid is not null))
        {
            sw.Restart();
            var table = DshMobile.Runtime.Supervision.ProcessTable.Read();
            var builder = new ProotCommandBuilder(_layout);
            var argv = LauncherArgvShadowCheck.Compare(
                processes.SandboxRootPid is { } s ? table?.Argv(s) : null,
                processes.DshRootPid is { } d ? table?.Argv(d) : null,
                builder.SandboxCommand(), builder.DshCommand(), builder.DshCommand(inPlaceRestart: true),
                sw.ElapsedMilliseconds, _time.GetUtcNow());
            _shadow.Report(argv);
            _log.Note($"shadow {argv.Slice}: {argv.Verdict} ({argv.Detail}; csharp {argv.CSharpMs} ms)");
        }
        return report;
    }

    private DshHost? _host;

    /// <summary>The C# host (created on first C# start); exposes the launch token to the C# WebView only.</summary>
    public DshHost? Host => _host;

    /// <summary>Caller holds <see cref="_op"/>.</summary>
    private async Task<RuntimeStartResult> StartWithCSharpHostAsync(DshProfile profile, CancellationToken ct)
    {
        if (_layout is null)
        {
            return Refused("C# host needs the runtime layout");
        }
        if (profile.Name != RuntimeConstants.DshWebProfile)
        {
            return Refused($"C# host starts only the '{RuntimeConstants.DshWebProfile}' profile");
        }
        if (State.Current.State is RuntimeState.Running or RuntimeState.Starting)
        {
            return Refused($"cannot start the C# host while the runtime is {State.Current.State}");
        }
        if (!State.TryTransition(RuntimeState.Starting, "start requested (csharp host)", HarnessState.Stopped))
        {
            return Refused($"cannot start while {State.Current.State}");
        }
        _host ??= new DshHost(_layout, _log.Note, _time);
        _foreground.Acquire();
        var sw = Stopwatch.StartNew();
        _log.Note("start: C# DshHost (legacy runtime not used)");
        var r = await _host.StartAsync(ct).ConfigureAwait(false);
        _log.Note(r.Ok ? $"csharp host: ready in {sw.Elapsed.TotalSeconds:F1} s" : $"csharp host: FAILED {r.Code}: {r.Message}");
        if (!r.Ok)
        {
            // The host already killed whatever it started; nothing is left for the service to keep alive.
            _foreground.Release();
            State.TryTransition(RuntimeState.Failed, "csharp host start failed", HarnessState.Stopped, r.Code, r.Message);
            return new RuntimeStartResult(false, r.Code, r.Message, State.Current);
        }
        State.TryTransition(RuntimeState.Running, "csharp host ready", HarnessState.Ready);
        return new RuntimeStartResult(true, RuntimeErrorCodes.Ok, null, State.Current);
    }

    /// <summary>Caller holds <see cref="_op"/>.</summary>
    private async Task<RuntimeStartResult> StopCSharpHostAsync(DshHost host, CancellationToken ct)
    {
        State.TryTransition(RuntimeState.Stopping, "stop requested (csharp host)");
        var r = await host.StopAsync(ct).ConfigureAwait(false);
        _log.Note(r.Ok ? "csharp host: stopped, no marker process left" : $"csharp host: stop FAILED {r.Code}: {r.Message}");
        if (!r.Ok)
        {
            State.TryTransition(RuntimeState.Failed, "csharp host stop incomplete", HarnessState.Stopped, r.Code, r.Message);
            return new RuntimeStartResult(false, r.Code, r.Message, State.Current);
        }
        _foreground.Release();
        State.TryTransition(RuntimeState.Ready, "stopped (csharp host)", HarnessState.Stopped);
        return new RuntimeStartResult(true, RuntimeErrorCodes.Ok, null, State.Current);
    }

    /// <summary>Aligns the journal with what the runtime actually reports before deciding a transition.</summary>
    private async Task SyncAsync(CancellationToken ct)
    {
        if (_host is { IsActive: true })
        {
            return; // the legacy runtime does not know about C#-hosted processes; the journal follows the host
        }
        var actual = await _runtime.GetStateAsync(ct).ConfigureAwait(false);
        // A failed status read is itself reported as Failed; keep the journal's own error in that case.
        if (actual.ErrorCode is null || actual.State != RuntimeState.Failed)
        {
            State.Observe(actual.State, actual.Harness, actual.DshVersion, _runtime.Implementation);
        }
    }

    private RuntimeStartResult Refused(string message)
    {
        _log.Note("refused: " + message);
        return new RuntimeStartResult(false, RuntimeErrorCodes.InvalidTransition, message, State.Current);
    }

    public void Dispose() => _op.Dispose();

    private static string Describe(RuntimeStartResult r) =>
        r.Ok
            ? $"ok state={r.Snapshot.State} harness={r.Snapshot.Harness} version={r.Snapshot.DshVersion ?? "-"}"
            : $"FAILED {r.Code}{(r.UpstreamCode is null ? "" : "/" + r.UpstreamCode)}: {r.Message}";
}
