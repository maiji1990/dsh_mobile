// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/runtime/RelayContainer.kt (assembly root; start, initSequence, initStep,
//     initFailed, stop, the shizuku listener, rerouteApprovalPresentation, revokeApprovalPresenters,
//     resolveApproval / expireApproval / dismissApprovalNotification, onForegroundChanged / onHeartbeat,
//     trustedDisplayReady, digestSalt, the ParkBridgeHolder)
//   src/main/java/interlock/relay/core/runtime/RelayRuntime.kt (the process-wide scope that swallows and logs)
// Registered in docs/UPSTREAMS.md.
//
// Not carried over (legacy keeps them while the slice is LegacyWithShadow; ADR-0007 / MIGRATION.md):
//   GuestAssets + capabilityRows / capabilitiesJson (the guest-visible entry script, manual and capability list),
//   the approval overlay and the accessibility-gap notice. A host built from this class serves its own mailbox only;
//   the guest keeps talking to the legacy Relay until the cut-over.

using System.Globalization;
using System.Security.Cryptography;
using DshMobile.Runtime.Automation.Relay.A11y;
using DshMobile.Runtime.Automation.Relay.Exec;
using DshMobile.Runtime.Automation.Relay.Exec.Direct;
using DshMobile.Runtime.Automation.Relay.Exec.Shizuku;
using DshMobile.Runtime.Automation.Relay.Interlock;
using DshMobile.Runtime.Automation.Relay.Logging;
using DshMobile.Runtime.Automation.Relay.Storage;
using DshMobile.Runtime.Automation.Relay.Transport;

namespace DshMobile.Runtime.Automation.Relay;

/// <summary>Upstream <c>RelayPrefs</c> (SharedPreferences by file name).</summary>
public interface IRelayPrefs
{
    string? GetString(string file, string key);

    void PutString(string file, string key, string value);

    bool GetBoolean(string file, string key, bool defValue);
}

/// <summary>Everything the host needs from Android (one object, built in DshMobile.Android).</summary>
public sealed class RelayHostPlatform
{
    public required string FilesDir { get; init; }

    public RelayPathPolicy PathPolicy { get; init; } = RelayPathPolicy.DshMobile;

    public required string SelfPackage { get; init; }

    public required int SdkInt { get; init; }

    public required IRelayPrefs Prefs { get; init; }

    public required IA11yPlatform A11y { get; init; }

    public required IDirectPlatform Direct { get; init; }

    public required IAudioRecorder Audio { get; init; }

    public required IProjectionPlatform Projection { get; init; }

    public required IShizukuPlatform Shizuku { get; init; }

    /// <summary>The probe's device facts; <c>ShellAvailable</c> / <c>TrustedDisplayAvailable</c> are filled in by the host.</summary>
    public required Func<RelaySystemFacts> DeviceFacts { get; init; }

    /// <summary>Upstream <c>surfaces.uiInForeground()</c>: the host's own assistant page is in front.</summary>
    public required Func<bool> UiInForeground { get; init; }

    /// <summary>Upstream <c>appInForeground()</c>: the app process is at foreground importance.</summary>
    public required Func<bool> AppInForeground { get; init; }

    public Func<bool> DeviceInteractive { get; init; } = () => true;

    public IApprovalNotificationSurface? Notifications { get; init; }

    /// <summary>The window the notification listener feeds (process-wide); a private empty one when absent.</summary>
    public NotificationWindow? NotificationFeed { get; init; }

    public IRelayAskSurface AskSurface { get; init; } = IRelayAskSurface.Unavailable;

    /// <summary>Upstream <c>RelayText.charCount</c> ("%1$d characters").</summary>
    public Func<int, string> CharCount { get; init; } = n => n.ToString(CultureInfo.InvariantCulture) + " characters";

    /// <summary><c>getPackageInfo(pkg).applicationInfo.uid</c> (consent-surface detection).</summary>
    public Func<string, int?>? UidResolver { get; init; }

    /// <summary>The channel's foreground service (upstream <c>RelayService.start/stop</c>); no-ops when absent.</summary>
    public Action StartChannelService { get; init; } = () => { };

    public Action StopChannelService { get; init; } = () => { };

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>Upstream <c>RelayContainer</c> (core assembly and lifecycle) plus <c>RelayRuntime</c>'s logging scope.</summary>
public sealed class RelayHost : IDisposable
{
    public const string KeySalt = "digest_salt";
    public const string KeyVerbose = "verbose_log";
    public const string FileShellVerbs = "relay_shell_verbs";
    public const string ShellVerbPrefix = "verb_";
    public const int SaltBytes = 16;

    private readonly RelayHostPlatform _platform;
    private readonly Lock _gate = new();
    private readonly RelayRuntimeStateMachine _stateMachine;
    private readonly QuotaLedger _ledger;
    private readonly StorageReaper _reaper;
    private readonly RequestStateStore _requestStateStore;
    private readonly RelayTierStore _tiers;
    private readonly AuditLog _auditLog;
    private readonly InterlockQueue _approvalQueue;
    private readonly ScreenProjection _screenProjection;
    private readonly A11yBackend _a11yBackend;
    private readonly DirectBackend _directBackend;
    private readonly ShizukuBackend _shizukuBackend;
    private readonly BackendDispatcher _dispatcher;
    private readonly SurfacePolicy _policy;
    private readonly RelayCoordinator _coordinator;
    private readonly MailboxServer _mailbox;
    private readonly ParkBridgeHolder _parkBridge = new();
    private CancellationTokenSource? _scope;
    private CancellationTokenSource? _initJob;
    private bool? _lastTrustedDisplayReady;
    private volatile InterlockQueue.Presentation? _routing;

    public RelayHost(RelayHostPlatform platform)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _stateMachine = new RelayRuntimeStateMachine(phase => PhaseChanged?.Invoke(phase));
        var time = platform.Time;
        Func<long> mono = () => time.GetTimestamp() * 1000 / time.TimestampFrequency;
        Func<long> wall = () => time.GetUtcNow().ToUnixTimeMilliseconds();
        Paths = new RelayPaths(platform.FilesDir, platform.PathPolicy);
        RunLog = new RunLog(Paths.LogsDir, verboseEnabled: () => platform.Prefs.GetBoolean(RelaySurfaces.PrefsFile, KeyVerbose, false));
        _ledger = new QuotaLedger(Paths.QuotaFile);
        _reaper = new StorageReaper(Paths, _ledger, RunLog);
        _requestStateStore = new RequestStateStore(Paths.RequestStateDir, runLog: RunLog);
        _tiers = new RelayTierStore(key => platform.Prefs.GetString(RelaySurfaces.PrefsFile, key));
        _auditLog = new AuditLog(Paths.AuditDir, DigestSalt(),
            reportFailure: detail => RunLog.Warn(LogSubsystem.Storage, LogEvent.AuditWriteFailed, ("detail", detail)));
        _approvalQueue = new InterlockQueue(
            isForegroundCapable: platform.UiInForeground,
            onRouting: RerouteApprovalPresentation,
            overlay: null,
            overlayBlind: TrustedDisplayReady,
            canNotify: () => platform.Notifications?.CanShow() == true,
            otherCardOnScreen: platform.AskSurface.IsPresented,
            onRevokePresenters: request => platform.Notifications?.CancelFor(request.Id));
        _screenProjection = new ScreenProjection(platform.Projection, _reaper, RunLog, time);
        Func<long, CancellationToken, Task> delay = (ms, ct) => Task.Delay(TimeSpan.FromMilliseconds(ms), time, ct);
        Func<bool> hostInFront = () => platform.UiInForeground() || platform.AppInForeground();
        _directBackend = new DirectBackend(platform.Direct, platform.NotificationFeed ?? new NotificationWindow(wall), new AudioCapture(_reaper, platform.Audio, delay),
            _screenProjection, clipboardUsable: hostInFront, hostInFront: hostInFront, wall, delay, platform.PathPolicy.MediaAlbumDir);
        _shizukuBackend = new ShizukuBackend(platform.Shizuku, RunLog, _reaper, time, delay);
        _a11yBackend = new A11yBackend(platform.A11y, _reaper, time, () => TrustedDisplayReady() ? _shizukuBackend.DisplayId : -1);
        _dispatcher = new BackendDispatcher(new Dictionary<RelayBackendId, IRelayBackend>
        {
            [RelayBackendId.Direct] = _directBackend,
            [RelayBackendId.A11y] = _a11yBackend,
            [RelayBackendId.Shizuku] = _shizukuBackend,
        }, RunLog, mono);
        _policy = new SurfacePolicy(
            () => RelaySurfaces.ParsePreference(platform.Prefs.GetString(RelaySurfaces.PrefsFile, RelaySurfaces.PreferenceKey)),
            () => platform.Shizuku.ServerAvailable, _dispatcher.CanServe, platform.SdkInt);
        var stage = new RelayExecutionStage(_policy, _dispatcher.IsImplemented,
            new RelayRateLimiter(mono, RelayRateLimiter.ExecutionLimit), platform.SelfPackage);
        var admission = new RelayAdmission(RelaySurfaces.Wires, platform.SelfPackage, new RelayRateLimiter(mono, RelayRateLimiter.EntryLimit));
        var gate = new RelayGate(_tiers, mono);
        _coordinator = new RelayCoordinator(new RelayCoordinatorParts(platform.SelfPackage, admission, stage, gate, _approvalQueue,
            _dispatcher, Probe, Paths, RunLog, _auditLog, platform.CharCount)
        {
            NodeOwner = _a11yBackend.TargetOwnerOf,
            DispatchGuard = id => _requestStateStore.Get(id) is null || _requestStateStore.MarkExecDispatchGuard(id),
            ParkBridge = _parkBridge,
            ParkScope = () => _scope?.Token,
            IsCancelRequested = id => _requestStateStore.Get(id)?.CancelRequested == true,
            ApprovalDeadEnd = _approvalQueue.LastDeadEndReason,
            Time = time,
        });
        _mailbox = new MailboxServer(Paths, _reaper, RunLog, _coordinator.Handler,
            isDeviceInteractive: platform.DeviceInteractive,
            onMaintenance: OnMaintenance,
            stateStore: _requestStateStore,
            controlFactory: (mailbox, scope) =>
            {
                var control = new ControlChannelServer(Paths, _reaper, _requestStateStore, RunLog, mailbox.Health, platform.AskSurface);
                control.Start(scope);
                return control;
            });
        if (platform.UidResolver is { } resolver)
        {
            ConsentSurfaces.UidResolver = resolver;
        }
        _shizukuBackend.ShellOptInOpen = verb => platform.Prefs.GetBoolean(FileShellVerbs, ShellVerbPrefix + verb, false);
        _parkBridge.Delegate = _mailbox;
    }

    /// <summary>Every phase move (inside the state machine's lock: keep handlers light).</summary>
    public event Action<RelayRuntimePhase>? PhaseChanged;

    /// <summary>The approval routing moved (pending request + where it is shown); the in-app card renders from this.</summary>
    public event Action<InterlockQueue.Presentation>? ApprovalRoutingChanged;

    public RelayPaths Paths { get; }

    public RunLog RunLog { get; }

    public RelayRuntimePhase Phase => _stateMachine.Phase;

    public string? DegradedReason => _stateMachine.DegradedReason;

    public bool IsChannelRunning => _mailbox.IsRunning;

    public WorkLoopHealth? Health => _mailbox.IsRunning ? _mailbox.Health() : null;

    public InterlockQueue.Presentation? ApprovalRouting => _routing;

    public ScreenProjection ScreenProjection => _screenProjection;

    public ShizukuBackend Shizuku => _shizukuBackend;

    public BackendDispatcher Dispatcher => _dispatcher;

    public SurfacePolicy Policy => _policy;

    /// <summary>The system probe on the decided route, with the two facts only the host knows filled in.</summary>
    public ProbeResult Probe(string wire, SurfaceKind route) => RelaySystemProbe.Probe(wire, route, Facts());

    public RelaySystemFacts Facts() => _platform.DeviceFacts() with
    {
        ShellAvailable = _platform.Shizuku.ServerAvailable,
        TrustedDisplayAvailable = TrustedDisplayReady(),
    };

    /// <summary>Upstream <c>trustedDisplayReady</c>.</summary>
    public bool TrustedDisplayReady() => _platform.Shizuku.ServerAvailable && _shizukuBackend.DisplayAlive();

    /// <summary>
    /// Upstream <c>start</c>: only flips the phase and returns; the steps (disk and binder work) run on the thread pool.
    /// When the host may serve is told by <see cref="Phase"/>.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (!_stateMachine.BeginStart())
            {
                return;
            }
            _scope = new CancellationTokenSource();
            var job = new CancellationTokenSource();
            _initJob = job;
            RunLog.Info(LogSubsystem.Transport, LogEvent.RuntimeStarting);
            _ = Task.Run(() => InitSequence(job.Token, _scope.Token));
        }
    }

    /// <summary>Runs the init sequence on the calling thread (tests, and callers already off the main thread).</summary>
    internal void StartInline()
    {
        CancellationToken job;
        CancellationToken scope;
        lock (_gate)
        {
            if (!_stateMachine.BeginStart())
            {
                return;
            }
            _scope = new CancellationTokenSource();
            _initJob = new CancellationTokenSource();
            job = _initJob.Token;
            scope = _scope.Token;
            RunLog.Info(LogSubsystem.Transport, LogEvent.RuntimeStarting);
        }
        InitSequence(job, scope);
    }

    private sealed class InitAbortedException : Exception;

    private sealed class InitStepFailedException(string step, Exception cause) : Exception(step, cause)
    {
        public string Step { get; } = step;
    }

    private void InitSequence(CancellationToken job, CancellationToken scope)
    {
        var undo = new Stack<Action>();
        try
        {
            InitStep(job, undo, "ledger", null, () => _ledger.Load(detail => RunLog.Warn(LogSubsystem.Storage, LogEvent.LedgerCorrupted, ("detail", detail))));
            InitStep(job, undo, "ledger", null, () => _ledger.ReconcileFromDisk(Paths.ArtifactsDir));
            InitStep(job, undo, "ledger", null, () => _reaper.Sweep());
            InitStep(job, undo, "ledger", null, () => ReportAuditPurge(_auditLog.PurgeExpired()));
            // A process kill skips disconnect: drop the previous life's "last frame" before binding again.
            InitStep(job, undo, "shizuku", null, _shizukuBackend.ClearFrameCache);
            InitStep(job, undo, "shizuku", () => _shizukuBackend.DisconnectAsync().GetAwaiter().GetResult(), _shizukuBackend.Connect);
            InitStep(job, undo, "mailbox", _mailbox.Stop, () =>
            {
                _mailbox.Start(scope);
                if (!_mailbox.IsRunning)
                {
                    throw new IOException("mailbox directories unavailable");
                }
            });
            // The persistent notification only once the channel takes work (never "an assistant is waiting" while it is not).
            InitStep(job, undo, "channel_service", _platform.StopChannelService, _platform.StartChannelService);
            lock (_gate)
            {
                if (job.IsCancellationRequested || _stateMachine.Phase != RelayRuntimePhase.Starting)
                {
                    throw new InitAbortedException();
                }
                _stateMachine.CommitRunning();
                RunLog.Info(LogSubsystem.Transport, LogEvent.RuntimeRunning);
            }
        }
        catch (InitAbortedException)
        {
            // Whoever aborted (stop, or a newer sequence) owns the cleanup and the phase.
        }
#pragma warning disable CA1031 // every failure must land in DEGRADED: stuck in STARTING is worse (no retry entry)
        catch (Exception failed)
#pragma warning restore CA1031
        {
            InitFailed(undo, failed, job);
        }
    }

    /// <summary>One step inside the lifecycle lock; the undo is registered before the body runs (a half-done step is undone too).</summary>
#pragma warning disable CA1068 // the token is the step's guard, kept first like upstream's job parameter
    private void InitStep(CancellationToken job, Stack<Action> undo, string step, Action? undoStep, Action body)
#pragma warning restore CA1068
    {
        lock (_gate)
        {
            if (job.IsCancellationRequested || _stateMachine.Phase != RelayRuntimePhase.Starting)
            {
                throw new InitAbortedException();
            }
            if (undoStep is not null)
            {
                undo.Push(undoStep);
            }
            try
            {
                body();
            }
#pragma warning disable CA1031
            catch (Exception cause)
#pragma warning restore CA1031
            {
                throw new InitStepFailedException(step, cause);
            }
        }
    }

    private void InitFailed(Stack<Action> undo, Exception failed, CancellationToken job)
    {
        var step = (failed as InitStepFailedException)?.Step ?? "unknown";
        var cause = failed.InnerException ?? failed;
        var detail = cause.GetType().Name + ": " + (string.IsNullOrEmpty(cause.Message) ? "no message" : cause.Message);
        lock (_gate)
        {
            if (job.IsCancellationRequested || _stateMachine.Phase != RelayRuntimePhase.Starting)
            {
                return;
            }
            while (undo.TryPop(out var u))
            {
                try
                {
                    u();
                }
#pragma warning disable CA1031 // best-effort cleanup, in reverse order
                catch (Exception)
#pragma warning restore CA1031
                {
                }
            }
            _stateMachine.Fail(step + " " + detail);
            RunLog.Warn(LogSubsystem.Transport, LogEvent.RuntimeDegraded, ("step", step), ("cause", detail));
        }
    }

    /// <summary>Upstream <c>stop</c>. DEGRADED has nothing to stop (the failure path cleaned up).</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (!_stateMachine.BeginStop())
            {
                return;
            }
            _initJob?.Cancel();
            _initJob?.Dispose();
            _initJob = null;
            _scope?.Cancel();
            _scope?.Dispose();
            _scope = null;
            _mailbox.Stop();
            _ = _shizukuBackend.DisconnectAsync();
            _approvalQueue.CancelAll();
            _platform.AskSurface.Withdraw();
            _screenProjection.Release();
            _platform.StopChannelService();
            // Session boundary: "allow for this session" ends with the channel.
            _tiers.ClearSession();
            RunLog.Info(LogSubsystem.Gate, LogEvent.ChannelStopped);
            RunLog.Info(LogSubsystem.Transport, LogEvent.RuntimeStopped);
        }
    }

    /// <summary>
    /// Upstream <c>shizukuListener.onStateChanged</c>: binding only succeeds once running and granted, which may both
    /// happen after start; try again at that moment and re-check the display (a new server process is a new display).
    /// </summary>
    public void OnShizukuStateChanged(bool running, bool granted)
    {
        var phase = _stateMachine.Phase;
        if (phase is RelayRuntimePhase.Stopped or RelayRuntimePhase.Degraded)
        {
            return;
        }
        if (running && granted)
        {
            _shizukuBackend.Connect();
            _ = Task.Run(_shizukuBackend.RefreshDisplayAsync);
        }
        RunLog.Event(LogSubsystem.Gate, LogEvent.ShizukuState,
            ("binder", running ? "true" : "false"), ("granted", granted ? "true" : "false"));
    }

    /// <summary>The notification's answer, for that request id only (a minute-old tap must not answer a newer question).</summary>
    public bool ResolveApproval(InterlockChoice choice, int requestId) => _approvalQueue.ResolveCurrent(choice, requestId);

    public bool ExpireApproval(int requestId) => _approvalQueue.ExpireIfCurrent(requestId);

    /// <summary>An answer no pending request took: clear the card so the presenter does not think it is still up.</summary>
    public void DismissApprovalNotification(int requestId)
    {
        if (IsChannelRunning)
        {
            _platform.Notifications?.CancelFor(requestId);
        }
    }

    /// <summary>Entering or leaving the assistant page re-decides where the pending question is shown.</summary>
    public void OnForegroundChanged()
    {
        CheckTrustedDisplayFlip();
        _approvalQueue.Reroute();
    }

    public void OnHeartbeat() => CheckTrustedDisplayFlip();

    private void CheckTrustedDisplayFlip()
    {
        var ready = TrustedDisplayReady();
        var last = _lastTrustedDisplayReady;
        _lastTrustedDisplayReady = ready;
        if (last is { } l && l != ready)
        {
            _approvalQueue.Reroute();
        }
    }

    private void RerouteApprovalPresentation(InterlockQueue.Presentation routing)
    {
        _routing = routing;
        ApprovalNotificationContent.ApplyRouting(routing, _platform.Notifications);
        ApprovalRoutingChanged?.Invoke(routing);
        if (routing.Request is { } pending)
        {
            RunLog.Info(LogSubsystem.Gate, LogEvent.ApprovalPresenter,
                ("via", routing.Via.ToString().ToLowerInvariant()), ("waiting", routing.WaitingCount.ToString(CultureInfo.InvariantCulture)));
            RunLog.Info(LogSubsystem.Gate, LogEvent.ApprovalRouted,
                ("cap", pending.Prompt.Capability), ("postState", _platform.Notifications?.LastPostState() ?? "none"));
        }
    }

    private void OnMaintenance()
    {
        ReportAuditPurge(_auditLog.PurgeExpired());
        _platform.StartChannelService();
        _shizukuBackend.Reconnect();
        _ = Task.Run(_shizukuBackend.RefreshDisplayAsync);
    }

    private void ReportAuditPurge(int removed)
    {
        if (removed > 0)
        {
            RunLog.Info(LogSubsystem.Storage, LogEvent.AuditPurged, ("files", removed.ToString(CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>Upstream <c>digestSalt</c>: per install, kept in the host's own prefs (shared with the legacy Relay's file).</summary>
    private string DigestSalt()
    {
        if (_platform.Prefs.GetString(RelaySurfaces.PrefsFile, KeySalt) is { } existing)
        {
            return existing;
        }
        var generated = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(SaltBytes));
        _platform.Prefs.PutString(RelaySurfaces.PrefsFile, KeySalt, generated);
        return generated;
    }

    public void Dispose()
    {
        Stop();
        _shizukuBackend.Dispose();
        _scope?.Dispose();
        _initJob?.Dispose();
    }

    /// <summary>Upstream <c>ParkBridgeHolder</c>: the coordinator is built before the mailbox it parks into.</summary>
    private sealed class ParkBridgeHolder : IParkedRequestBridge
    {
        public volatile MailboxServer? Delegate;

        public bool HasParkCapacity() => Delegate?.HasParkCapacity() ?? false;

        public bool Register(ParkedRequest park) => Delegate?.Register(park) ?? false;

        public void ResumeParked(string requestId) => Delegate?.ResumeParked(requestId);
    }
}
