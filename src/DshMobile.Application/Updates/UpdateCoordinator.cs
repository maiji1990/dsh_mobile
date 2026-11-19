using DshMobile.Application.Runtime;
using DshMobile.Runtime;
using DshMobile.Runtime.Updates;

namespace DshMobile.Application.Updates;

/// <summary>Where an update is right now (for the UI): its phase and, while downloading, bytes so far.</summary>
public sealed record UpdateProgress(string Phase, string? Detail, long Received = 0, long Total = 0);

/// <summary>How an update ended. <see cref="InstalledVersion"/> is what is installed afterwards (new or the old one).</summary>
public sealed record UpdateResult(bool Ok, string Code, string? Message, string Phase, string? InstalledVersion, bool Restarted = false);

/// <summary>
/// The online DSH layer update (vNext PR-05), end to end and in this order:
/// <list type="number">
/// <item><b>DOWNLOADING</b> the artifact of a verified discovery (<see cref="ArtifactDownloader"/>: resumable, whole-file
/// SHA-256); cancellable only here;</item>
/// <item><b>VERIFIED</b>; <b>STOPPING</b> the local DSH if it runs — only when the caller says the user
/// confirmed, otherwise refused;</item>
/// <item><b>SWAPPING</b>: <see cref="RuntimeCoordinator.InstallDshUpdateAsync"/> re-hashes the file and installs it
/// <i>held</i> (old layer kept aside);</item>
/// <item><b>PROBING</b>: start DSH web through the host (READY = port + WebUI + HTTP 401), check the running version is the
/// target, stop it;</item>
/// <item><b>COMMITTED</b> (old layer retired) — or <b>ROLLING_BACK</b> → <b>FAILED</b> with the old layer restored.</item>
/// </list>
/// Each phase is recorded in <see cref="UpdateStateStore"/> before it starts. A process killed at any point comes back
/// with the old layer (the layer journal, undone by the next sync) or, after COMMITTED, the new one; the record then
/// reads <see cref="UpdateTransactionCodes.Interrupted"/>. DSH is restarted afterwards if it was running before. User data,
/// sessions and credentials are never touched: only <c>runtime-current/dsh</c> and the update store change.
/// </summary>
public sealed class UpdateCoordinator : IDisposable
{
    private readonly RuntimeCoordinator _runtime;
    private readonly ArtifactDownloader _downloader;
    private readonly UpdateStateStore _state;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _one = new(1, 1);

    public UpdateCoordinator(RuntimeCoordinator runtime, ArtifactDownloader downloader, UpdateStateStore state, TimeProvider time)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public void Dispose() => _one.Dispose();

    /// <summary>The last recorded transaction (after a restart, an unfinished one reads as interrupted).</summary>
    public UpdateState? LastState => _state.Read();

    /// <summary>Call once at startup: settles a transaction the previous process did not finish.</summary>
    public UpdateState? SettleInterrupted() => _state.SettleInterrupted(_time.GetUtcNow());

    /// <param name="stopConfirmed">The user agreed to stop DSH for the update.</param>
    public Task<UpdateResult> InstallDshAsync(ReleaseDiscoveryResult check, string channel, bool stopConfirmed, IProgress<UpdateProgress>? progress, CancellationToken ct) =>
        InstallCoreAsync(check, channel, stopConfirmed, dshOnly: true, progress, ct);

    /// <summary>
    /// ADR-0029: 按 <see cref="UpdatePlanner.Plan"/> 执行在线安装事务（支持 Runtime 单项、DSH 单项、以及 Runtime + DSH 联合安装）。
    /// 所有涉及的层均以 <c>holdForProbe</c> 模式暂存旧层，经端到端健康探测通过后才依次提交；任一步失败或中途强杀均自动恢复到更新前状态。
    /// </summary>
    public Task<UpdateResult> InstallPlanAsync(ReleaseDiscoveryResult check, string channel, bool stopConfirmed, IProgress<UpdateProgress>? progress, CancellationToken ct) =>
        InstallCoreAsync(check, channel, stopConfirmed, dshOnly: false, progress, ct);

    private async Task<UpdateResult> InstallCoreAsync(
        ReleaseDiscoveryResult check,
        string channel,
        bool stopConfirmed,
        bool dshOnly,
        IProgress<UpdateProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (!await _one.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
        {
            return new UpdateResult(false, UpdateTransactionCodes.Busy, "an update is already running", "", null);
        }
        try
        {
            if (!check.Ok || check.Release is not { } release)
            {
                return new UpdateResult(false, check.Code, check.Message ?? "no verified release", "", null);
            }
            var plan = UpdatePlanner.Plan(check.Choices);
            var targetRuntime = dshOnly ? null : plan.Runtime;
            var targetDsh = plan.Dsh;
            if (targetRuntime is null && targetDsh is null)
            {
                return new UpdateResult(false, UpdateTransactionCodes.NothingToInstall, dshOnly ? "no applicable DSH update" : "no applicable update", "", null);
            }

            var targetKind = targetRuntime is not null && targetDsh is not null ? "runtime+dsh"
                : targetRuntime is not null ? targetRuntime.Kind
                : targetDsh!.Kind;
            var targetVersion = targetRuntime is not null && targetDsh is not null ? $"{targetRuntime.Version}+{targetDsh.Version}"
                : targetRuntime is not null ? targetRuntime.Version
                : targetDsh!.Version;
            var targetSha = (targetDsh ?? targetRuntime)!.Sha256;
            var totalBytes = (targetRuntime?.Size ?? 0) + (targetDsh?.Size ?? 0);

            var started = _time.GetUtcNow();
            void Record(string phase, string? code = null, string? message = null)
            {
                _state.Write(new UpdateState(phase, targetKind, targetVersion, targetSha, channel, started, _time.GetUtcNow(), code, message));
                progress?.Report(new UpdateProgress(phase, message));
            }
            UpdateResult End(bool ok, string code, string? message, string phase, bool restarted = false)
            {
                Record(phase, ok ? null : code, message);
                var installedVersion = targetRuntime is not null && targetDsh is null
                    ? InstalledVersionsReader.Read(_runtime.Layout, "0.0.0").Runtime ?? _runtime.State.Current.DshVersion
                    : _runtime.State.Current.DshVersion;
                return new UpdateResult(ok, code, message, phase, installedVersion, restarted);
            }

            // 1. Download (the only cancellable phase).
            Record(UpdatePhases.Downloading);
            DownloadResult? runtimeDownload = null;
            if (targetRuntime is not null)
            {
                runtimeDownload = await _downloader.DownloadAsync(
                    release,
                    targetRuntime,
                    progress is null ? null : new Relay(p => progress.Report(new UpdateProgress(UpdatePhases.Downloading, targetRuntime.Archive, p.Received, totalBytes))),
                    ct).ConfigureAwait(false);
                if (!runtimeDownload.Ok)
                {
                    return End(false, runtimeDownload.Code, runtimeDownload.Message, UpdatePhases.Failed);
                }
            }

            DownloadResult? dshDownload = null;
            if (targetDsh is not null)
            {
                var baseBytes = targetRuntime?.Size ?? 0;
                dshDownload = await _downloader.DownloadAsync(
                    release,
                    targetDsh,
                    progress is null ? null : new Relay(p => progress.Report(new UpdateProgress(UpdatePhases.Downloading, targetRuntime is null ? null : targetDsh.Archive, baseBytes + p.Received, totalBytes))),
                    ct).ConfigureAwait(false);
                if (!dshDownload.Ok)
                {
                    return End(false, dshDownload.Code, dshDownload.Message, UpdatePhases.Failed);
                }
            }

            var verifiedDetail = targetRuntime is not null && targetDsh is not null
                ? $"{targetRuntime.Archive} sha256 {targetRuntime.Sha256[..12]}… + {targetDsh.Archive} sha256 {targetDsh.Sha256[..12]}…"
                : $"{(targetDsh ?? targetRuntime)!.Archive} sha256 {targetSha[..12]}…";
            Record(UpdatePhases.Verified, message: verifiedDetail);

            // 2. Stop what runs on the layer, only with the user's consent. From here on nothing is cancellable.
            var wasRunning = _runtime.HostOwnsProcesses || _runtime.State.Current.State is RuntimeState.Running or RuntimeState.Starting;
            if (wasRunning)
            {
                if (!stopConfirmed)
                {
                    return End(false, RuntimeErrorCodes.InvalidTransition, "DSH is running: confirm stopping it to install the update", UpdatePhases.Failed);
                }
                Record(UpdatePhases.Stopping);
                var stopped = await _runtime.StopAsync(CancellationToken.None).ConfigureAwait(false);
                if (!stopped.Ok)
                {
                    return End(false, stopped.Code, "could not stop DSH: " + stopped.Message, UpdatePhases.Failed);
                }
            }

            // 3. Swap, held.
            Record(UpdatePhases.Swapping);
            var runtimeHeld = false;
            if (runtimeDownload is not null)
            {
                var rtInstalled = await _runtime.InstallRuntimeUpdateAsync(runtimeDownload.File!, null, CancellationToken.None).ConfigureAwait(false);
                if (!rtInstalled.Ok)
                {
                    var r = End(false, rtInstalled.Code, rtInstalled.Message, UpdatePhases.Failed);
                    return wasRunning ? await RestartAsync(r).ConfigureAwait(false) : r;
                }
                runtimeHeld = true;
            }

            var dshHeld = false;
            DshMobile.Runtime.Layers.DshLayerResult? dshInstalled = null;
            if (dshDownload is not null)
            {
                dshInstalled = await _runtime.InstallDshUpdateAsync(dshDownload.File!, CancellationToken.None).ConfigureAwait(false);
                if (!dshInstalled.Ok)
                {
                    if (runtimeHeld)
                    {
                        _ = await _runtime.FinishRuntimeUpdateAsync(commit: false, CancellationToken.None).ConfigureAwait(false);
                    }
                    var r = End(false, dshInstalled.Code, dshInstalled.Message, UpdatePhases.Failed);
                    return wasRunning ? await RestartAsync(r).ConfigureAwait(false) : r;
                }
                dshHeld = dshInstalled.Changed;
            }

            if (!runtimeHeld && !dshHeld)
            {
                var r = End(true, RuntimeErrorCodes.Ok, $"DSH {dshInstalled!.Version} is already installed", UpdatePhases.Committed);
                return wasRunning ? await RestartAsync(r).ConfigureAwait(false) : r;
            }

            // 4. Health check on the new layer(s).
            var expectedDshVersion = targetDsh?.Version ?? _runtime.State.Current.DshVersion;
            var healthy = true;
            var why = "ready";
            if (expectedDshVersion is not null)
            {
                Record(UpdatePhases.Probing);
                (healthy, why) = await ProbeAsync(expectedDshVersion).ConfigureAwait(false);
            }

            if (healthy)
            {
                var rtCommit = runtimeHeld
                    ? await _runtime.FinishRuntimeUpdateAsync(commit: true, CancellationToken.None).ConfigureAwait(false)
                    : new DshMobile.Runtime.Layers.RuntimeBodyRecovery(true, "none", null);
                var dshCommit = dshHeld
                    ? await _runtime.FinishDshUpdateAsync(commit: true, CancellationToken.None).ConfigureAwait(false)
                    : new DshMobile.Runtime.Layers.DshLayerRecovery(true, "none", null);
                var commitOk = rtCommit.Ok && dshCommit.Ok;
                var okLabel = targetRuntime is not null && targetDsh is not null
                    ? $"Runtime {targetRuntime.Version} and DSH {targetDsh.Version} installed and healthy"
                    : targetRuntime is not null
                        ? $"Runtime {targetRuntime.Version} installed and healthy"
                        : $"DSH {targetDsh!.Version} installed and healthy";
                var r = commitOk
                    ? End(true, RuntimeErrorCodes.Ok, okLabel, UpdatePhases.Committed)
                    : End(false, UpdateTransactionCodes.RollbackFailed, "health check passed but the commit failed: " + (rtCommit.Message ?? dshCommit.Message), UpdatePhases.Failed);
                return wasRunning ? await RestartAsync(r).ConfigureAwait(false) : r;
            }

            // 5. Unhealthy: the old layer(s) go back in reverse order.
            Record(UpdatePhases.RollingBack, UpdateTransactionCodes.HealthFailed, why);
            var dshUndo = dshHeld
                ? await _runtime.FinishDshUpdateAsync(commit: false, CancellationToken.None).ConfigureAwait(false)
                : new DshMobile.Runtime.Layers.DshLayerRecovery(true, "none", null);
            var rtUndo = runtimeHeld
                ? await _runtime.FinishRuntimeUpdateAsync(commit: false, CancellationToken.None).ConfigureAwait(false)
                : new DshMobile.Runtime.Layers.RuntimeBodyRecovery(true, "none", null);
            var undoOk = dshUndo.Ok && rtUndo.Ok;
            var failSubject = targetRuntime is not null && targetDsh is not null
                ? $"Runtime {targetRuntime.Version} + DSH {targetDsh.Version}"
                : targetRuntime is not null
                    ? $"Runtime {targetRuntime.Version}"
                    : $"DSH {targetDsh!.Version}";
            var restoredWhat = targetRuntime is not null && targetDsh is not null
                ? "the previous layers were restored"
                : targetRuntime is not null
                    ? "the previous runtime body was restored"
                    : "the previous layer was restored";
            var failed = undoOk
                ? End(false, UpdateTransactionCodes.HealthFailed, $"{failSubject} failed its health check ({why}); {restoredWhat}", UpdatePhases.Failed)
                : End(false, UpdateTransactionCodes.RollbackFailed, $"health check failed ({why}) and restoring failed: {dshUndo.Message ?? rtUndo.Message}; retried on the next start", UpdatePhases.Failed);
            return wasRunning && undoOk ? await RestartAsync(failed).ConfigureAwait(false) : failed;
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>READY through the host (port + WebUI + HTTP 401), the running version is the target, then stopped again.</summary>
    private async Task<(bool Ok, string Why)> ProbeAsync(string version)
    {
        var start = await _runtime.StartAsync(DshProfile.Web, CancellationToken.None).ConfigureAwait(false);
        if (!start.Ok)
        {
            return (false, $"start: {start.Code} {start.Message}");
        }
        var running = start.Snapshot.DshVersion;
        var stop = await _runtime.StopAsync(CancellationToken.None).ConfigureAwait(false);
        if (!string.Equals(running, version, StringComparison.Ordinal))
        {
            return (false, $"running version {running ?? "?"} is not {version}");
        }
        return stop.Ok ? (true, "ready") : (false, $"stop: {stop.Code} {stop.Message}");
    }

    private async Task<UpdateResult> RestartAsync(UpdateResult r)
    {
        var s = await _runtime.StartAsync(DshProfile.Web, CancellationToken.None).ConfigureAwait(false);
        return r with
        {
            Restarted = s.Ok,
            Message = r.Message + (s.Ok ? "; DSH restarted" : $"; DSH was running before but did not restart: {s.Code} {s.Message}"),
            InstalledVersion = _runtime.State.Current.DshVersion,
        };
    }

    private sealed class Relay(Action<DownloadProgress> on) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => on(value);
    }
}
