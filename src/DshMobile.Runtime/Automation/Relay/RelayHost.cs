// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/runtime/RelayContainer.kt (assembly root; start, initSequence, initStep,
//     initFailed, stop, the shizuku listener, rerouteApprovalPresentation, revokeApprovalPresenters,
//     resolveApproval / expireApproval / dismissApprovalNotification, onForegroundChanged / onHeartbeat,
//     trustedDisplayReady, digestSalt, the ParkBridgeHolder)
//   src/main/java/interlock/relay/core/runtime/RelayRuntime.kt (the process-wide scope that swallows and logs)
// Registered in docs/UPSTREAMS.md.
//
//   RelayContainer.kt refreshAssets / refreshAssetsIfChanged / refreshSerialized / republishAssets / scheduleRefresh
//     and the "assets" init step (GuestAssets.cs, RelayCapabilityListing.cs; ADR-0010)
//
// Not carried over (legacy keeps them; ADR-0010): the approval overlay and the accessibility-gap notice.
// Which mailbox a host serves is its path policy: the opt-in shadow host uses its own (pilot-cs), the guest-facing
// host the guest's (pilot), and the legacy Relay never runs on the same one at the same time.

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

    /// <summary>
    /// Upstream <c>surfaces.approvalOverlay()</c>: the floating approval card (ADR-0011), shown over other apps when the
    /// user granted "display over other apps"; null = no overlay route.
    /// </summary>
    public IOverlayApprovalChannel? Overlay { get; init; }

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

    /// <summary>Upstream <c>ShizukuState.snapshot()</c>: running, granted, server uid and version (-1 / -1 without a binder).</summary>
    public Func<ShizukuSnapshot>? ShizukuState { get; init; }

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
    private readonly GuestAssets _assets;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private volatile string? _lastCapabilitiesJson;
    private volatile ShizukuSnapshot _shizukuSnapshot;
    private long _lastHeartbeatRefresh;
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
            overlay: platform.Overlay,
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
            // Creating / releasing the trusted display rewrites the usable column of the background capabilities.
            OnSurfaceChanged = () => ScheduleRefresh(onlyIfChanged: true),
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
        _assets = new GuestAssets(Paths, RunLog, time: time);
        _shizukuSnapshot = ReadShizuku();
        // The user service binds asynchronously: "ready" is in the connection callback, not in the Shizuku state.
        _shizukuBackend.BoundChanged += () => ScheduleRefresh();
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
    /// <remarks>
    /// This is the gate's probe, so an accessibility gap seen here is a call that was really held back and is reported
    /// to the user (<see cref="AccessibilityNotice"/>). The listing probes without the hook (upstream <c>listingThread</c>):
    /// its periodic checks must not pop a hint while the assistant has tried nothing.
    /// </remarks>
    public ProbeResult Probe(string wire, SurfaceKind route) => RelaySystemProbe.Probe(wire, route, Facts(), PublishAccessibilityGap);

    /// <summary>Upstream <c>AccessibilityNotice</c>: which accessibility step was missing when a call was held back.</summary>
    public sealed record AccessibilityNoticeInfo(AccessibilityGap Gap, string Capability);

    private readonly Lock _noticeLock = new();
    private AccessibilityNoticeInfo? _notice;
    private AccessibilityGap? _silencedGap;

    /// <summary>The hint to show (null = none); the host UI renders it and calls <see cref="DismissAccessibilityNotice"/>.</summary>
    public AccessibilityNoticeInfo? AccessibilityNotice
    {
        get
        {
            lock (_noticeLock)
            {
                return _notice;
            }
        }
    }

    /// <summary>The hint appeared or went away (any thread).</summary>
    public event Action<AccessibilityNoticeInfo?>? AccessibilityNoticeChanged;

    /// <summary>
    /// Upstream <c>publishAccessibilityGap</c>: not while an approval is up (two dialogs on one screen and the user only
    /// sees the later one while the assistant waits on the first); the same gap once dismissed stays quiet until the
    /// binding changes.
    /// </summary>
    private void PublishAccessibilityGap(string wire, AccessibilityGap gap)
    {
        AccessibilityNoticeInfo notice;
        lock (_noticeLock)
        {
            if (_routing?.Request is not null || _silencedGap == gap || _notice?.Gap == gap)
            {
                return;
            }
            notice = new AccessibilityNoticeInfo(gap, wire);
            _notice = notice;
        }
        AccessibilityNoticeChanged?.Invoke(notice);
    }

    /// <summary>Upstream <c>dismissAccessibilityNotice</c>: the user saw it; the same gap stays quiet until the binding moves.</summary>
    public void DismissAccessibilityNotice()
    {
        lock (_noticeLock)
        {
            if (_notice is null)
            {
                return;
            }
            _silencedGap = _notice.Gap;
            _notice = null;
        }
        AccessibilityNoticeChanged?.Invoke(null);
    }

    /// <summary>Clears the hint without silencing it (an approval took the screen; the gap is still there).</summary>
    private void ClearAccessibilityNotice()
    {
        lock (_noticeLock)
        {
            if (_notice is null)
            {
                return;
            }
            _notice = null;
        }
        AccessibilityNoticeChanged?.Invoke(null);
    }

    /// <summary>
    /// Upstream <c>RelayAccessibilityService.onBoundChanged</c>: the earlier "seen" no longer holds (conditions changed,
    /// so the hint may come again), the pending question is re-routed and the listing re-checked.
    /// </summary>
    public void OnAccessibilityBoundChanged()
    {
        lock (_noticeLock)
        {
            _silencedGap = null;
        }
        OnForegroundChanged();
    }

    /// <summary>
    /// A relay preference (surface preference, tier, shell verb, verbose log) was written by this app's own UI: the
    /// listing and manual follow at once instead of at the next heartbeat check.
    /// </summary>
    public void OnPreferencesChanged() => ScheduleRefresh(onlyIfChanged: true);

    /// <summary>What the host page shows about this relay (C#-owned status line, ADR-0011).</summary>
    public sealed record StatusInfo(
        RelayRuntimePhase Phase,
        bool ChannelRunning,
        string? DegradedReason,
        AccessibilityGap? AccessibilityGap,
        IReadOnlyList<string> Allowed,
        string GuestEntry);

    public StatusInfo Status()
    {
        var facts = Facts();
        var allowed = RelayCapabilityCatalog.All.Select(c => c.Wire).Where(w => _tiers.TierOf(w) != AccessTier.Denied).ToList();
        return new StatusInfo(Phase, IsChannelRunning, DegradedReason, RelaySystemProbe.Gap(facts), allowed, Paths.GuestScript);
    }

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
            // Assets before the channel: a guest exec'ing a missing or half-written entry script gets an exit code it
            // cannot tell from a usage error. Not through the single-flight gate (it must not be merged away); a mixed
            // set from an overlapping event-driven pass is caught by the marker (Intact) on the next check.
            InitStep(job, undo, "assets", null, () => RepublishAssets(CapabilitiesJson()));
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
        _shizukuSnapshot = ReadShizuku() with { Running = running, Granted = granted };
        if (running && granted)
        {
            _shizukuBackend.Connect();
            _ = Task.Run(_shizukuBackend.RefreshDisplayAsync);
        }
        RunLog.Event(LogSubsystem.Gate, LogEvent.ShizukuState,
            ("binder", running ? "true" : "false"), ("granted", granted ? "true" : "false"));
        // The listing and the manual follow the grant, so the guest reads the current values.
        ScheduleRefresh();
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
        // Also the accessibility service's bind / unbind (upstream RelayAccessibilityService.onBoundChanged).
        ScheduleRefresh();
    }

    /// <summary>Interval of the heartbeat's listing check (the host's ticker is faster).</summary>
    public static readonly TimeSpan HeartbeatRefreshInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The trusted-display flip, and every <see cref="HeartbeatRefreshInterval"/> a content check of the listing.
    /// Upstream gets the notification-listener binding and tier / preference changes as callbacks; here the legacy
    /// pages still write those preferences and the C# listener has no binding callback, so the check stands in for them
    /// (it writes nothing while the content is unchanged).
    /// </summary>
    public void OnHeartbeat()
    {
        CheckTrustedDisplayFlip();
        var now = _platform.Time.GetTimestamp();
        var last = Interlocked.Read(ref _lastHeartbeatRefresh);
        if (last == 0 || _platform.Time.GetElapsedTime(last, now) >= HeartbeatRefreshInterval)
        {
            Interlocked.Exchange(ref _lastHeartbeatRefresh, now);
            ScheduleRefresh(onlyIfChanged: true);
        }
    }

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
        lock (_noticeLock)
        {
            // Under the hint's lock: a probe deciding to show the hint either sees this approval or is cleared below.
            _routing = routing;
        }
        if (routing.Request is not null)
        {
            // Upstream: an approval coming up takes the hint down (not silenced: the next probe reminds again).
            ClearAccessibilityNotice();
        }
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
        _ = Task.Run(async () =>
        {
            _shizukuSnapshot = ReadShizuku();
            await _shizukuBackend.RefreshDisplayAsync().ConfigureAwait(false);
            RefreshAssetsIfChanged();
        });
    }

    private ShizukuSnapshot ReadShizuku()
    {
        try
        {
            return _platform.ShizukuState?.Invoke() ?? ShizukuSnapshot.None with { Running = _platform.Shizuku.ServerAvailable };
        }
#pragma warning disable CA1031 // a binder probe failing means "not running", like upstream's runCatching
        catch (Exception)
#pragma warning restore CA1031
        {
            return ShizukuSnapshot.None;
        }
    }

    /// <summary>Upstream <c>capabilityRows</c>, the system facts read once for the whole listing.</summary>
    public IReadOnlyList<CapabilityRow> CapabilityRows()
    {
        var facts = Facts();
        return RelayCapabilityListing.Rows(_policy, (wire, route) => RelaySystemProbe.Probe(wire, route, facts), _tiers.TierOf,
            (wire, route) => _dispatcher.Pick(wire, route) is not null, _dispatcher.IsImplemented);
    }

    /// <summary>Upstream <c>capabilitiesJson</c>: what <c>&lt;entry&gt;/capabilities.json</c> holds.</summary>
    public string CapabilitiesJson()
    {
        var displayId = _shizukuBackend.DisplayId;
        return RelayCapabilityListing.Json(CapabilityRows(), Paths.GuestScript, _platform.SelfPackage, _shizukuSnapshot,
            _shizukuBackend.BoundSnapshot,
            new TrustedDisplayState(_shizukuBackend.DisplayAlive(), displayId, _a11yBackend.TrustedNodeTreeState(displayId)));
    }

    /// <summary>The last listing published completely (null until then): the base of "changed, so republish".</summary>
    public string? PublishedCapabilitiesJson => _lastCapabilitiesJson;

    /// <summary>
    /// Upstream <c>refreshAssets</c>: not once the channel stopped (a listing for a channel that takes no work only
    /// sends the guest into calls nobody answers); the next start publishes again.
    /// </summary>
    public void RefreshAssets()
    {
        if (Phase is not (RelayRuntimePhase.Running or RelayRuntimePhase.Starting))
        {
            return;
        }
        RefreshSerialized(() => RepublishAssets(CapabilitiesJson()));
    }

    /// <summary>
    /// Upstream <c>refreshAssetsIfChanged</c>: only when the listing changed, or the published files are no longer one
    /// complete batch (the guest can delete, rewrite or replace them by links; a pass abandoned half way leaves a mix).
    /// </summary>
    public void RefreshAssetsIfChanged()
    {
        if (Phase is not (RelayRuntimePhase.Running or RelayRuntimePhase.Starting))
        {
            return;
        }
        RefreshSerialized(() =>
        {
            var json = CapabilitiesJson();
            if (json != _lastCapabilitiesJson || !_assets.Intact())
            {
                RepublishAssets(json);
            }
        });
    }

    /// <summary>
    /// Upstream <c>refreshSerialized</c>: a trigger that finds a pass running joins it; the running pass re-checks once
    /// at its end, so a change after it built its listing is not lost.
    /// </summary>
    private void RefreshSerialized(Action work)
    {
        if (!_refreshGate.Wait(0))
        {
            return;
        }
        try
        {
            work();
            var fresh = CapabilitiesJson();
            if (fresh != _lastCapabilitiesJson || !_assets.Intact())
            {
                RepublishAssets(fresh);
            }
        }
#pragma warning disable CA1031 // a refresh runs on the pool: a failure keeps the old record, the next check retries
        catch (Exception ex)
#pragma warning restore CA1031
        {
            RunLog.Warn(LogSubsystem.Storage, LogEvent.AtomicWriteFailed, ("file", "capabilities.json " + ex.GetType().Name));
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Upstream <c>republishAssets</c>: the record moves only after a complete publish.</summary>
    private void RepublishAssets(string json)
    {
        if (_assets.Materialize(json))
        {
            _lastCapabilitiesJson = json;
        }
    }

    private void ScheduleRefresh(bool onlyIfChanged = false)
    {
        if (_scope is null)
        {
            return;
        }
        _ = Task.Run(onlyIfChanged ? RefreshAssetsIfChanged : RefreshAssets);
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
        _refreshGate.Dispose();
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
