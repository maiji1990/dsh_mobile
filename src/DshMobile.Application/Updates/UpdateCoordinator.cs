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
/// <item><b>VERIFIED</b>; <b>STOPPING</b> the local DSH and terminals if they run — only when the caller says the user
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

    /// <param name="stopConfirmed">The user agreed that a running DSH (and its terminals) is stopped for the update.</param>
    public async Task<UpdateResult> InstallDshAsync(ReleaseDiscoveryResult check, string channel, bool stopConfirmed, IProgress<UpdateProgress>? progress, CancellationToken ct)
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
            if (UpdatePlanner.Plan(check.Choices).Dsh is not { } target)
            {
                return new UpdateResult(false, UpdateTransactionCodes.NothingToInstall, "no applicable DSH update", "", null);
            }
            var started = _time.GetUtcNow();
            void Record(string phase, string? code = null, string? message = null)
            {
                _state.Write(new UpdateState(phase, target.Kind, target.Version, target.Sha256, channel, started, _time.GetUtcNow(), code, message));
                progress?.Report(new UpdateProgress(phase, message));
            }
            UpdateResult End(bool ok, string code, string? message, string phase, bool restarted = false)
            {
                Record(phase, ok ? null : code, message);
                return new UpdateResult(ok, code, message, phase, _runtime.State.Current.DshVersion, restarted);
            }

            // 1. Download (the only cancellable phase).
            Record(UpdatePhases.Downloading);
            var dl = await _downloader.DownloadAsync(release, target,
                progress is null ? null : new Relay(p => progress.Report(new UpdateProgress(UpdatePhases.Downloading, null, p.Received, p.Total))), ct).ConfigureAwait(false);
            if (!dl.Ok)
            {
                return End(false, dl.Code, dl.Message, UpdatePhases.Failed);
            }
            Record(UpdatePhases.Verified, message: $"{target.Archive} sha256 {target.Sha256[..12]}…");

            // 2. Stop what runs on the layer, only with the user's consent. From here on nothing is cancellable.
            var wasRunning = _runtime.HostOwnsProcesses || _runtime.State.Current.State is RuntimeState.Running or RuntimeState.Starting;
            if (wasRunning)
            {
                if (!stopConfirmed)
                {
                    return End(false, RuntimeErrorCodes.InvalidTransition, "DSH is running: confirm stopping it (and its terminals) to install the update", UpdatePhases.Failed);
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
            var installed = await _runtime.InstallDshUpdateAsync(dl.File!, CancellationToken.None).ConfigureAwait(false);
            if (!installed.Ok)
            {
                var r = End(false, installed.Code, installed.Message, UpdatePhases.Failed);
                return wasRunning ? await RestartAsync(r).ConfigureAwait(false) : r;
            }
            if (!installed.Changed)
            {
                var r = End(true, RuntimeErrorCodes.Ok, $"DSH {installed.Version} is already installed", UpdatePhases.Committed);
                return wasRunning ? await RestartAsync(r).ConfigureAwait(false) : r;
            }

            // 4. Health check on the new layer.
            Record(UpdatePhases.Probing);
            var (healthy, why) = await ProbeAsync(target.Version).ConfigureAwait(false);
            if (healthy)
            {
                var commit = await _runtime.FinishDshUpdateAsync(commit: true, CancellationToken.None).ConfigureAwait(false);
                var r = commit.Ok
                    ? End(true, RuntimeErrorCodes.Ok, $"DSH {target.Version} installed and healthy", UpdatePhases.Committed)
                    : End(false, UpdateTransactionCodes.RollbackFailed, "health check passed but the commit failed: " + commit.Message, UpdatePhases.Failed);
                return wasRunning ? await RestartAsync(r).ConfigureAwait(false) : r;
            }

            // 5. Unhealthy: the old layer goes back.
            Record(UpdatePhases.RollingBack, UpdateTransactionCodes.HealthFailed, why);
            var undo = await _runtime.FinishDshUpdateAsync(commit: false, CancellationToken.None).ConfigureAwait(false);
            var failed = undo.Ok
                ? End(false, UpdateTransactionCodes.HealthFailed, $"DSH {target.Version} failed its health check ({why}); the previous layer was restored", UpdatePhases.Failed)
                : End(false, UpdateTransactionCodes.RollbackFailed, $"health check failed ({why}) and restoring failed: {undo.Message}; retried on the next start", UpdatePhases.Failed);
            return wasRunning && undo.Ok ? await RestartAsync(failed).ConfigureAwait(false) : failed;
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
