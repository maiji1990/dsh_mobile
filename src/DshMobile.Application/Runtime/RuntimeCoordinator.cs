using System.Diagnostics;
using DshMobile.Application.Shadow;
using DshMobile.Application.Terminal;
using DshMobile.Core.Bridge;
using DshMobile.Runtime;
using DshMobile.Runtime.Bundle;
using DshMobile.Runtime.Guest;
using DshMobile.Runtime.Hosting;
using DshMobile.Runtime.Layers;
using DshMobile.Runtime.Plugins;

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
        RuntimeLayout? layout = null,
        PtyOpener? terminalOpener = null)
    {
        _runtime = runtime;
        State = state;
        _foreground = foreground;
        _log = log;
        _shadow = shadow;
        _options = options;
        _time = time ?? TimeProvider.System;
        _layout = layout;
        Terminals = terminalOpener is not null ? new TerminalSessions(terminalOpener, log.Note)
            : layout is null ? null : new TerminalSessions(
            async (rows, columns, ct) =>
            {
                // Like the legacy TerminalController.newSession: make the app uid known to the guest first.
                var user = GuestUser.Provision(Path.Combine(layout.RuntimeCurrentDir, "base"));
                if (user == GuestUser.Outcome.Updated)
                {
                    _log.Note($"{TerminalSessions.Slice}: guest passwd entry written for {GuestUser.UserName}");
                }
                return await new ProotGuestProcess(layout).OpenPtyAsync(new GuestExecutionOptions(), rows, columns, ct).ConfigureAwait(false);
            },
            log.Note);
    }

    /// <summary>
    /// PR-06: the C#-owned terminal sessions (developer opt-in screen). Closed whenever the runtime stops, at the same
    /// point the legacy <c>sandbox.stop</c> stops its terminal shells. Null without a runtime layout (or a test opener).
    /// </summary>
    public TerminalSessions? Terminals { get; }

    private async Task CloseTerminalsAsync()
    {
        if (Terminals is not null && await Terminals.CloseAllAsync().ConfigureAwait(false) is var n and > 0)
        {
            _log.Note($"{TerminalSessions.Slice}: closed {n} session(s) on stop");
        }
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
                // The C# terminal opens its own PRoot shells without a started sandbox, so they can be the only thing
                // running here (device run 2026-11-12: two shells survived "stop: nothing running"). Close them too;
                // then nothing runs, so releasing the service cannot orphan anything (and frees a stale one).
                await CloseTerminalsAsync().ConfigureAwait(false);
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
            await CloseTerminalsAsync().ConfigureAwait(false);
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
    /// PR-05 shadow: re-runs <c>dsh plugin list</c> through the C# PRoot launcher and compares the parsed plugin lists
    /// with the legacy result the caller just showed. Null when not shadowed. Read-only (list only).
    /// </summary>
    public async Task<ShadowReport?> ShadowPluginListAsync(string profile, bool legacyOk, string legacyOutput, CancellationToken ct)
    {
        if (_options.PluginList != SliceMode.LegacyWithShadow || _layout is null)
        {
            return null;
        }
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sw = Stopwatch.StartNew();
            var r = await GuestPlugins.RunAsync(new ProotGuestProcess(_layout), "list", profile, null, ct).ConfigureAwait(false);
            var report = PluginListShadowCheck.Compare(legacyOk, legacyOutput, r, sw.ElapsedMilliseconds, _time.GetUtcNow());
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
    /// PR-07 shadow: the C# port of the ui_* registration reads the patch layer and the accessibility setting after a
    /// legacy <c>relay.*</c> call and is compared with what that call reported. Null when not shadowed or no layout.
    /// Read-only: it never writes or deletes the layer.
    /// </summary>
    public ShadowReport? ShadowUiTools(UiToolsLegacyView legacy, string? enabledAccessibilityServices, string packageName)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        if (_options.UiTools != SliceMode.LegacyWithShadow || _layout is null)
        {
            return null;
        }
        var sw = Stopwatch.StartNew();
        ShadowReport report;
        try
        {
            report = UiToolsShadowCheck.Compare(legacy, _layout.UserDataDir, enabledAccessibilityServices, packageName, 0, _time.GetUtcNow())
                with { CSharpMs = sw.ElapsedMilliseconds };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report = new ShadowReport(UiToolsShadowCheck.Slice, _time.GetUtcNow(), ShadowVerdict.Inconclusive, true, null, false,
                ex.GetType().Name, $"after {legacy.Method}: C# could not read the layer: {ex.Message}", sw.ElapsedMilliseconds);
        }
        _shadow.Report(report);
        _log.Note($"shadow {report.Slice}: {report.Verdict} ({report.Detail})");
        return report;
    }

    /// <summary>
    /// PR-07 segment 2 shadow (developer action, needs a running relay): probes the live Relay mailbox with requests
    /// that are always rejected while decoding and compares Relay's answers with the C# envelope decoder.
    /// Null when the slice is not in shadow mode or there is no layout.
    /// </summary>
    public async Task<ShadowReport?> ShadowRelayEnvelopeAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_options.RelayEnvelopeCodec != SliceMode.LegacyWithShadow || _layout is null)
        {
            return null;
        }
        ShadowReport report;
        try
        {
            report = await RelayEnvelopeProbe.RunAsync(_layout.PilotEntryDir, _time, timeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report = new ShadowReport(RelayEnvelopeProbe.Slice, _time.GetUtcNow(), ShadowVerdict.Inconclusive, false, null, true,
                ex.GetType().Name, $"probe could not use the mailbox: {ex.Message}", 0);
        }
        _shadow.Report(report);
        _log.Note($"shadow {report.Slice}: {report.Verdict} ({report.Detail})");
        return report;
    }

    /// <summary>
    /// PR-07 segment 3 shadow (developer action, needs a running relay): well-formed requests that the admission steps
    /// before the approval gate must reject, answers compared byte for byte with C#. Null when not in shadow mode.
    /// </summary>
    public async Task<ShadowReport?> ShadowRelayAdmissionAsync(string selfPackage, TimeSpan timeout, CancellationToken ct)
    {
        if (_options.RelayAdmission != SliceMode.LegacyWithShadow || _layout is null)
        {
            return null;
        }
        ShadowReport report;
        try
        {
            report = await RelayAdmissionProbe.RunAsync(_layout.PilotEntryDir, selfPackage, UiToolsShadowCheck.AllowedWire, _time, timeout, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report = new ShadowReport(RelayAdmissionProbe.Slice, _time.GetUtcNow(), ShadowVerdict.Inconclusive, false, null, true,
                ex.GetType().Name, $"probe could not use the mailbox: {ex.Message}", 0);
        }
        _shadow.Report(report);
        _log.Note($"shadow {report.Slice}: {report.Verdict} ({report.Detail})");
        return report;
    }

    /// <summary>
    /// PR-07 segment 4 shadow (read-only): Relay's published per-capability surface decision against the C# surface
    /// policy, from the same preference and device facts. <paramref name="preferenceRaw"/> is Relay's stored
    /// <c>surface_preference</c> (null = never set). Null when not in shadow mode.
    /// </summary>
    public ShadowReport? ShadowRelaySurface(string? preferenceRaw, int sdkInt)
    {
        if (_options.RelaySurface != SliceMode.LegacyWithShadow || _layout is null)
        {
            return null;
        }
        var path = Path.Combine(_layout.PilotEntryDir, "capabilities.json");
        ShadowReport report;
        try
        {
            var text = File.Exists(path) ? File.ReadAllText(path) : null;
            report = RelaySurfaceShadowCheck.Compare(text, preferenceRaw, sdkInt, UiToolsShadowCheck.AllowedWire, _time.GetUtcNow());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report = new ShadowReport(RelaySurfaceShadowCheck.Slice, _time.GetUtcNow(), ShadowVerdict.Inconclusive, false, null, true,
                ex.GetType().Name, $"could not read {path}: {ex.Message}", 0);
        }
        _shadow.Report(report);
        _log.Note($"shadow {report.Slice}: {report.Verdict} ({report.Detail})");
        return report;
    }

    /// <summary>
    /// PR-05 C# path (developer opt-in, per call): <c>dsh plugin add/remove</c> through the C# PRoot launcher with the
    /// ported install guard — an install that exits 0 but added a package that cannot load is rolled back (only the
    /// packages this install added); a remove is checked against the profile's dependencies afterwards.
    /// The bad-argument rules are the shared <c>PluginCommand</c> ones (BAD_REQUEST, nothing runs).
    /// </summary>
    public async Task<PluginChangeResult> ChangePluginWithCSharpAsync(string verb, string profile, string operand, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operand);
        if (_layout is null)
        {
            return new PluginChangeResult(false, RuntimeErrorCodes.NotInstalled, "C# plugin changes need the runtime layout", []);
        }
        if (verb is not ("add" or "remove"))
        {
            return new PluginChangeResult(false, BridgeErrorCodes.BadRequest, "unknown plugin verb: " + verb, []);
        }
        if (!PluginCommand.TryBuild(verb, profile, operand, out _, out var error))
        {
            return new PluginChangeResult(false, BridgeErrorCodes.BadRequest, error, []);
        }
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sw = Stopwatch.StartNew();
            var guard = new PluginInstallGuard(new GuestPluginOps(new ProotGuestProcess(_layout), profile), new PluginProfileReader(_layout, profile));
            var r = verb == "add"
                ? await guard.InstallAsync(operand, SpecPackageName(operand), ct).ConfigureAwait(false)
                : await guard.RemoveAsync(operand, ct).ConfigureAwait(false);
            _log.Note($"csharp plugin.{verb} {operand}: " + (r.Ok ? "ok" : $"{r.Code}{(r.RolledBack ? " (rolled back)" : "")}") + $" in {sw.Elapsed.TotalSeconds:F1} s"
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
        var at = spec.LastIndexOf('@');
        return at > 0 ? spec[..at] : spec;
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

        // PR-06 terminal argv: each open legacy terminal session (a PRoot child of this app process) vs the C# builder.
        if (_layout is not null && !OperatingSystem.IsWindows() && DshMobile.Runtime.Supervision.ProcessTable.Read() is { } procs)
        {
            sw.Restart();
            var csharpOwned = Terminals?.Pids ?? [];
            var sessions = procs.ChildrenOf(Environment.ProcessId)
                .Where(pid => !csharpOwned.Contains(pid))
                .Select(procs.Argv)
                .Where(a => a is not null && TerminalArgvShadowCheck.IsTerminalShell(a))
                .Select(a => a!)
                .ToList();
            var terminal = TerminalArgvShadowCheck.Compare(sessions, new TerminalCommandBuilder(_layout).LoginShell(), sw.ElapsedMilliseconds, _time.GetUtcNow());
            _shadow.Report(terminal);
            _log.Note($"shadow {terminal.Slice}: {terminal.Verdict} ({terminal.Detail}; csharp {terminal.CSharpMs} ms)");
        }
        return report;
    }

    /// <summary>
    /// PR-06 developer acceptance: a C#-owned PTY running the guest login shell (no legacy code on the path), driven by
    /// <see cref="PtyProbe"/>. Holds the operation lock so no shadow observes the probe's own PRoot as a legacy session.
    /// </summary>
    public async Task<PtyProbeReport> ProbeCSharpPtyAsync(CancellationToken ct)
    {
        if (_layout is null)
        {
            return new PtyProbeReport(false, [new PtyProbeStep("open", false, "no runtime layout", 0)], "");
        }
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
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
        await CloseTerminalsAsync().ConfigureAwait(false);
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
