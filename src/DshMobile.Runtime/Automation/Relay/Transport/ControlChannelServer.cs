// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/transport/ControlChannelServer.kt
// Registered in docs/UPSTREAMS.md. Locked by tests/TestVectors/relay-control.json.
//
// The control channel never executes a capability: submit only re-envelopes into the work inbox (the serial loop and
// every gate still decide), status/health only read, cancel only touches state and unclaimed inbox files, and ask only
// shows a question through the injected surface.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DshMobile.Runtime.Automation.Relay.Interlock;
using DshMobile.Runtime.Automation.Relay.Logging;
using DshMobile.Runtime.Automation.Relay.Storage;

namespace DshMobile.Runtime.Automation.Relay.Transport;

/// <summary>Upstream <c>ControlOps</c>.</summary>
public static class ControlOps
{
    public const string Submit = "submit";
    public const string Status = "status";
    public const string Cancel = "cancel";
    public const string Health = "health";
    public const string Ask = "ask";
    public const string Unknown = "unknown";
}

/// <summary>Upstream <c>ControlResults</c>: the closed set of CONTROL_OP result literals.</summary>
public static class ControlResults
{
    public const string Ok = "ok";
    public const string Rejected = "rejected";
    public const string Failed = "failed";
    public const string NotFound = "not_found";
    public const string Cancelled = "cancelled";
    public const string AlreadySettled = "already_settled";
    public const string CancelRequested = "cancel_requested";
    public const string Accepted = "accepted";

    public static IReadOnlySet<string> Literals { get; } = new HashSet<string>
    {
        Ok, Rejected, Failed, NotFound, Cancelled, AlreadySettled, CancelRequested, Accepted,
    };
}

/// <summary>Upstream <c>CancelPlan</c>.</summary>
public abstract record CancelPlan
{
    public sealed record NotFound : CancelPlan;

    public sealed record AlreadySettled(RequestPhase Phase) : CancelPlan;

    public sealed record RemoveUnclaimed : CancelPlan;

    public sealed record MarkRequested : CancelPlan;
}

/// <summary>Upstream <c>ControlChannelLogic</c>: the side-effect-free core of the control channel.</summary>
public static class ControlChannelLogic
{
    /// <summary>A submit re-enveloped as a v1 work request (ts = wall clock, ttl = host-clamped).</summary>
    public static string SubmitEnvelopeV1(ControlRequest.Submit submit, long tsMs)
    {
        ArgumentNullException.ThrowIfNull(submit);
        var o = new LibcoreJson.ObjectWriter();
        o.Put("v", RelayProtocol.V1);
        o.Put("id", submit.Id);
        o.Put("ts", tsMs);
        o.Put("capability", submit.Capability);
        o.PutRaw("args", LibcoreJson.Serialize(submit.Args));
        o.Put("ttlMs", submit.HostTtlMs);
        return o.ToString();
    }

    public static IReadOnlyList<KeyValuePair<string, object?>> StatusPayload(RequestState? state, string targetId)
    {
        var p = new List<KeyValuePair<string, object?>> { new("targetId", targetId), new("found", state is not null) };
        if (state is not null)
        {
            p.Add(new("state", state.Phase.Wire()));
            p.Add(new("result", MailboxServer.ResultObjectOrNull(state.ResultJson)));
            p.Add(new("mayHaveDispatched", state.MayHaveDispatched));
            p.Add(new("cancelRequested", state.CancelRequested));
        }
        return p;
    }

    public static IReadOnlyList<KeyValuePair<string, object?>> HealthPayload(
        bool channelLive, int queueDepth, long? oldestQueuedAgeMs, int inboxDepth = 0, bool stalled = false) =>
    [
        new("channelLive", channelLive),
        new("queueDepth", queueDepth),
        new("oldestQueuedAgeMs", oldestQueuedAgeMs is { } o ? (object)o : MailboxServer.JsonNull),
        new("inboxDepth", inboxDepth),
        new("stalled", stalled),
    ];

    public static CancelPlan CancelPlanOf(RequestState? state, bool fileInInbox) =>
        state is null ? new CancelPlan.NotFound()
        : state.IsTerminal ? new CancelPlan.AlreadySettled(state.Phase)
        : fileInInbox ? new CancelPlan.RemoveUnclaimed()
        : new CancelPlan.MarkRequested();

    public const long AskStaleGraceMs = 12_000;

    /// <summary>Upstream <c>askStaleMs</c>: the request's age when it outlived its window plus the grace, else 0.</summary>
    public static long AskStaleMs(long submittedAtWall, long timeoutMs, long nowWall)
    {
        if (submittedAtWall <= 0)
        {
            return 0;
        }
        var age = nowWall - submittedAtWall;
        return age > timeoutMs + AskStaleGraceMs ? age : 0;
    }

    public static IReadOnlyList<KeyValuePair<string, object?>> AskFailurePayload(string cause)
    {
        var err = new LibcoreJson.ObjectWriter();
        err.Put("code", PromptCodes.CodeOf(cause));
        err.Put("retryable", PromptCodes.Retryable(cause));
        err.Put("exitCode", PromptCodes.ExitCodeFor(cause));
        return [new("cause", cause), new("error", JsonElement.Parse(err.ToString()))];
    }

    public static IReadOnlyList<KeyValuePair<string, object?>> AskChosenPayload(int index, string label)
    {
        var choice = new LibcoreJson.ObjectWriter();
        choice.Put("kind", "option");
        choice.Put("index", index);
        choice.Put("label", label);
        return [new("choice", JsonElement.Parse(choice.ToString()))];
    }

    public static IReadOnlyList<KeyValuePair<string, object?>> AskRejectAllPayload() =>
        [new("choice", JsonElement.Parse("{\"kind\":\"reject_all\"}"))];

    public static IReadOnlyList<KeyValuePair<string, object?>> AskReaskPayload() =>
        [new("choice", JsonElement.Parse("{\"kind\":\"reask\"}"))];
}

/// <summary>
/// Upstream <c>ControlChannelServer</c>: consumes <c>control-inbox/</c> on its own loop, isolated from the work loop, and
/// answers into <c>control-outbox/&lt;id&gt;.json</c>. Same preconditions as <see cref="MailboxServer"/>.
/// </summary>
public sealed class ControlChannelServer : IRelayControlChannel
{
    public const long IdlePollMs = 5_000;
    public const int MaxPerRound = 8;
    public const int MaxMalformedPerRound = 8;
    public const string PartSuffix = ".part";
    public const long ObserverRebuildMs = 30_000;
    private const int Capacity = 16;

    public static IReadOnlySet<string> OpFields { get; } = new HashSet<string> { "op", "result", "ms" };

    private readonly RelayPaths _paths;
    private readonly StorageReaper _reaper;
    private readonly RequestStateStore _stateStore;
    private readonly RunLog _runLog;
    private readonly Func<WorkLoopHealth> _health;
    private readonly IInterlockPrompt? _askPresenter;
    private readonly Func<long> _now;
    private readonly Func<long> _wallNow;
    private readonly HashSet<string> _inFlightAskIds = new(StringComparer.Ordinal);
    private readonly Lock _askLock = new();
    private readonly TaskScheduler _work = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, 1).ExclusiveScheduler;

    private Channel<bool> _signals = NewSignals();
    private FileSystemWatcher? _observer;
    private CancellationTokenSource? _cts;
    private Task? _job;
    private CancellationToken? _scope;
    private volatile bool _dirsOk;

    public ControlChannelServer(
        RelayPaths paths,
        StorageReaper reaper,
        RequestStateStore stateStore,
        RunLog runLog,
        Func<WorkLoopHealth> health,
        IInterlockPrompt? askPresenter = null,
        Func<long>? now = null,
        Func<long>? wallNow = null)
    {
        _paths = paths;
        _reaper = reaper;
        _stateStore = stateStore;
        _runLog = runLog;
        _health = health;
        _askPresenter = askPresenter;
        _now = now ?? RelayClock.MonotonicNow;
        _wallNow = wallNow ?? RelayClock.WallNow;
    }

    public bool IsRunning => _job is { IsCompleted: false };

    public Task? Completion => _job;

    private static Channel<bool> NewSignals() =>
        Channel.CreateBounded<bool>(new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropWrite });

    /// <summary>Starts the loop; asks wait on <paramref name="scope"/> (outlives <see cref="Halt"/>, as upstream's outer scope).</summary>
    public void Start(CancellationToken scope = default)
    {
        if (_job is { IsCompleted: false })
        {
            return;
        }
        if (!_paths.EnsureMailboxDirs())
        {
            _dirsOk = false;
            _runLog.Error(LogSubsystem.Storage, LogEvent.DirsUnavailable);
            return;
        }
        _dirsOk = true;
        _scope = scope;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(scope);
        _cts = cts;
        _signals = NewSignals();
        var token = cts.Token;
        _job = Task.Factory.StartNew(async () =>
        {
            _runLog.Info(LogSubsystem.Transport, LogEvent.ControlStarted);
            try
            {
                await LoopAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            finally
            {
                StopWatching();
            }
        }, token, TaskCreationOptions.DenyChildAttach, _work).Unwrap();
    }

    /// <summary>Upstream <c>stop</c>; in-flight asks are not cancelled here (the assembly root withdraws the card).</summary>
    public void Halt()
    {
        _cts?.Cancel();
        _cts = null;
        _job = null;
        _scope = null;
        _runLog.Info(LogSubsystem.Transport, LogEvent.ControlStopped);
    }

    private void RestartWatching()
    {
        StopWatching();
        try
        {
            var w = new FileSystemWatcher(_paths.ControlInboxDir) { IncludeSubdirectories = false };
            w.Renamed += (_, _) => _signals.Writer.TryWrite(true);
            w.Created += (_, _) => _signals.Writer.TryWrite(true);
            w.EnableRaisingEvents = true;
            _observer = w;
        }
        catch (Exception cause)
        {
            _runLog.Warn(LogSubsystem.Transport, LogEvent.ChannelDegraded, ("where", "control.startWatching"), ("cause", cause.GetType().Name));
        }
    }

    private void StopWatching()
    {
        _observer?.Dispose();
        _observer = null;
    }

    private long EnsureDirs(long stamp, long lastRebuild)
    {
        var healthy = _paths.EnsureMailboxDirs();
        if (healthy != _dirsOk)
        {
            _runLog.Warn(LogSubsystem.Transport, LogEvent.ChannelDegraded, ("dirs", healthy ? "true" : "false"));
            _dirsOk = healthy;
        }
        if (!healthy || stamp - lastRebuild < ObserverRebuildMs)
        {
            return lastRebuild;
        }
        RestartWatching();
        return stamp;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        long lastRebuild = 0;
        RestartWatching();
        while (!ct.IsCancellationRequested)
        {
            lastRebuild = EnsureDirs(_now(), lastRebuild);
            if (!_dirsOk)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(IdlePollMs), ct);
                continue;
            }
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromMilliseconds(IdlePollMs));
                try
                {
                    await _signals.Reader.ReadAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                }
            }
            Drain(ct);
        }
    }

    private void Drain(CancellationToken ct)
    {
        List<FileInfo> files;
        try
        {
            if (!Directory.Exists(_paths.ControlInboxDir))
            {
                return;
            }
            files = [.. new DirectoryInfo(_paths.ControlInboxDir).EnumerateFiles()];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }
        var valid = files.Where(f => RelayEnvelope.IsRequestFileName(f.Name)).ToList();
        foreach (var f in files.Where(f => !RelayEnvelope.IsRequestFileName(f.Name) && !MailboxServer.IsIncomingPartName(f.Name)).Take(MaxMalformedPerRound))
        {
            RejectMalformed(f.FullName);
        }
        var handled = 0;
        foreach (var source in valid.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }
            if (handled >= MaxPerRound)
            {
                break;
            }
            handled++;
            Serve(source.FullName);
        }
    }

    private void Serve(string source)
    {
        var startedAt = _now();
        var submittedAtWall = LastModifiedWall(source);
        var op = ControlOps.Unknown;
        var result = ControlResults.Failed;
        try
        {
            string text;
            try
            {
                text = File.ReadAllText(source, Encoding.UTF8);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                RejectMalformed(source, "unreadable");
                result = ControlResults.Rejected;
                return;
            }
            StorageReaper.TryDelete(source);
            switch (RelayEnvelope.DecodeControl(text, Path.GetFileName(source)))
            {
                case ControlDecodeResult.Rejected rejected:
                    Reject(rejected.Id, ControlOps.Unknown, rejected.Error.Code + "/" + rejected.Cause);
                    result = ControlResults.Rejected;
                    break;
                case ControlDecodeResult.Valid { Control: ControlRequest.Submit submit }:
                    op = ControlOps.Submit;
                    result = HandleSubmit(submit);
                    break;
                case ControlDecodeResult.Valid { Control: ControlRequest.Status status }:
                    op = ControlOps.Status;
                    result = HandleStatus(status);
                    break;
                case ControlDecodeResult.Valid { Control: ControlRequest.Cancel cancel }:
                    op = ControlOps.Cancel;
                    result = HandleCancel(cancel);
                    break;
                case ControlDecodeResult.Valid { Control: ControlRequest.Health health }:
                    op = ControlOps.Health;
                    result = HandleHealth(health);
                    break;
                case ControlDecodeResult.Valid { Control: ControlRequest.Ask ask }:
                    op = ControlOps.Ask;
                    result = HandleAsk(ask, submittedAtWall);
                    break;
            }
        }
        catch (Exception)
        {
            result = ControlResults.Failed;
        }
        finally
        {
            _runLog.Info(LogSubsystem.Transport, LogEvent.ControlOp, ("op", op), ("result", result), ("ms", Ms(startedAt)));
        }
    }

    private string Ms(long startedAt) => Math.Max(0L, _now() - startedAt).ToString(CultureInfo.InvariantCulture);

    private static long LastModifiedWall(string path)
    {
        try
        {
            return File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds() : 0L;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0L;
        }
    }

    private void RejectMalformed(string source, string? cause = null)
    {
        var name = Path.GetFileName(source);
        cause ??= "malformed request file name " + (name.Length <= 48 ? name : name[..48]);
        var id = MailboxServer.MalformedNameId(name);
        Reject(id, ControlOps.Unknown, cause);
        _reaper.WriteTextAtomic(Path.Combine(_paths.ControlOutboxDir, name), RelayEncoding.EncodeControl(id, ControlOps.Unknown, false, [new("cause", cause)]));
        StorageReaper.TryDelete(source);
    }

    private void Reject(string id, string op, string cause)
    {
        _runLog.Warn(LogSubsystem.Transport, LogEvent.ControlRequestRejected, ("op", op), ("cause", cause));
        Reply(id, op, false, [new("cause", cause)]);
    }

    private string HandleSubmit(ControlRequest.Submit submit)
    {
        switch (_stateStore.Create(submit.Id, submit.PayloadDigest, submit.Capability, submit.HostTtlMs))
        {
            case CreateOutcome.Created:
                if (!Enqueue(submit))
                {
                    _stateStore.RecordTerminal(submit.Id, RequestPhase.InterruptedBeforeExecution);
                    Reject(submit.Id, ControlOps.Submit, "E_INTERNAL/enqueue failed (mailbox write failed)");
                    return ControlResults.Failed;
                }
                Reply(submit.Id, ControlOps.Submit, true,
                    [new("state", RequestPhase.Queued.Wire()), new("targetId", submit.Id), new("digest", submit.PayloadDigest)]);
                return ControlResults.Ok;
            case CreateOutcome.Existing existing:
                var payload = new List<KeyValuePair<string, object?>>
                {
                    new("state", existing.State.Phase.Wire()), new("targetId", submit.Id), new("digest", submit.PayloadDigest),
                };
                if (existing.State.ResultJson is { } r)
                {
                    payload.Add(new("result", MailboxServer.ResultObjectOrNull(r)));
                }
                Reply(submit.Id, ControlOps.Submit, true, payload);
                return ControlResults.Ok;
            case CreateOutcome.DigestConflict:
                Reject(submit.Id, ControlOps.Submit, "digest conflict");
                return ControlResults.Rejected;
            default:
                Reject(submit.Id, ControlOps.Submit, "E_INTERNAL/state persist failed");
                return ControlResults.Failed;
        }
    }

    private bool Enqueue(ControlRequest.Submit submit)
    {
        try
        {
            var part = Path.Combine(_paths.InboxDir, submit.Id + PartSuffix);
            if (!_reaper.WriteTextAtomic(part, ControlChannelLogic.SubmitEnvelopeV1(submit, _wallNow())))
            {
                return false;
            }
            var target = Path.Combine(_paths.InboxDir, submit.Id + RelayEnvelope.JsonSuffix);
            if (TryRename(part, target))
            {
                return true;
            }
            StorageReaper.TryDelete(target);
            return TryRename(part, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>rename(2) semantics: replaces an existing target.</summary>
    private static bool TryRename(string source, string target)
    {
        try
        {
            File.Move(source, target, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string HandleStatus(ControlRequest.Status status)
    {
        Reply(status.Id, ControlOps.Status, true, ControlChannelLogic.StatusPayload(_stateStore.Get(status.TargetId), status.TargetId));
        return ControlResults.Ok;
    }

    private string HandleCancel(ControlRequest.Cancel cancel)
    {
        var state = _stateStore.Get(cancel.TargetId);
        var fileInInbox = File.Exists(Path.Combine(_paths.InboxDir, cancel.TargetId + RelayEnvelope.JsonSuffix));
        return ControlChannelLogic.CancelPlanOf(state, fileInInbox) switch
        {
            CancelPlan.NotFound => Settled(cancel, "NOT_FOUND", null, ControlResults.NotFound),
            CancelPlan.AlreadySettled s => Settled(cancel, "COMPLETED", s.Phase.Wire(), ControlResults.AlreadySettled),
            CancelPlan.RemoveUnclaimed => CancelUnclaimed(cancel),
            _ => CancelInFlight(cancel),
        };
    }

    /// <summary>Unclaimed: move to processing then delete; losing that race to the work loop means "cancel requested".</summary>
    private string CancelUnclaimed(ControlRequest.Cancel cancel)
    {
        var source = Path.Combine(_paths.InboxDir, cancel.TargetId + RelayEnvelope.JsonSuffix);
        var parked = Path.Combine(_paths.ProcessingDir, cancel.TargetId + RelayEnvelope.JsonSuffix);
        if (!TryMoveNoReplace(source, parked))
        {
            return CancelInFlight(cancel);
        }
        if (!StorageReaper.TryDelete(parked))
        {
            _runLog.Warn(LogSubsystem.Transport, LogEvent.RequestDropped, ("file", Path.GetFileName(parked)), ("cause", "cancel leftover"));
        }
        var recorded = _stateStore.RecordTerminal(cancel.TargetId, RequestPhase.Cancelled);
        if (recorded?.Phase == RequestPhase.Cancelled)
        {
            _runLog.Info(LogSubsystem.Transport, LogEvent.RequestCancelled, ("id", cancel.TargetId));
            Reply(cancel.Id, ControlOps.Cancel, true, [new("targetId", cancel.TargetId), new("outcome", "CANCELLED")]);
            return ControlResults.Cancelled;
        }
        return Settled(cancel, recorded is null ? "UNKNOWN" : "COMPLETED", recorded?.Phase.Wire(),
            recorded is null ? ControlResults.Failed : ControlResults.AlreadySettled);
    }

    /// <summary>File.renameTo of a source that must exist (a vanished source means the work loop claimed it).</summary>
    private static bool TryMoveNoReplace(string source, string target)
    {
        try
        {
            if (!File.Exists(source))
            {
                return false;
            }
            File.Move(source, target, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string CancelInFlight(ControlRequest.Cancel cancel)
    {
        switch (_stateStore.RequestCancel(cancel.TargetId))
        {
            case CancelOutcome.Requested r:
                _runLog.Info(LogSubsystem.Transport, LogEvent.RequestCancelRequested, ("id", cancel.TargetId));
                Reply(cancel.Id, ControlOps.Cancel, true,
                    [new("targetId", cancel.TargetId), new("outcome", "CANCEL_REQUESTED"), new("provablyUndispatched", r.ProvablyUndispatched)]);
                return ControlResults.CancelRequested;
            case CancelOutcome.AlreadyTerminal t:
                return Settled(cancel, "COMPLETED", t.State.Phase.Wire(), ControlResults.AlreadySettled);
            case CancelOutcome.NotRecorded:
                return Settled(cancel, "UNKNOWN", null, ControlResults.Failed);
            default:
                return Settled(cancel, "NOT_FOUND", null, ControlResults.NotFound);
        }
    }

    private string Settled(ControlRequest.Cancel cancel, string outcome, string? phase, string result)
    {
        Reply(cancel.Id, ControlOps.Cancel, true,
            [new("targetId", cancel.TargetId), new("outcome", outcome), new("state", phase is null ? (object)MailboxServer.JsonNull : phase)]);
        return result;
    }

    private string HandleHealth(ControlRequest.Health request)
    {
        var s = _health();
        Reply(request.Id, ControlOps.Health, true,
            ControlChannelLogic.HealthPayload(s.ChannelLive, s.QueueDepth, s.OldestQueuedAgeMs, s.InboxDepth, s.Stalled));
        return ControlResults.Ok;
    }

    /// <summary>Upstream <c>handleAsk</c>: duplicate-id gate first, stale gate second; the wait runs off the control loop.</summary>
    private string HandleAsk(ControlRequest.Ask ask, long submittedAtWall)
    {
        var presenter = _askPresenter;
        var running = _scope;
        var startedAt = _now();
        if (presenter is null || running is not { } scope)
        {
            return AskRejected(ask.Id, PromptCodes.InternalNoChannel, startedAt);
        }
        lock (_askLock)
        {
            if (!_inFlightAskIds.Add(ask.Id))
            {
                _runLog.Warn(LogSubsystem.Transport, LogEvent.AskUnavailable, ("id", ask.Id), ("cause", PromptCodes.InternalDuplicate), ("ms", Ms(startedAt)));
                return ControlResults.Failed;
            }
        }
        if (ControlChannelLogic.AskStaleMs(submittedAtWall, ask.TimeoutMs, _wallNow()) > 0)
        {
            ReleaseAsk(ask.Id);
            return AskRejected(ask.Id, PromptCodes.TimeoutExpired, startedAt);
        }
        _ = Task.Run(async () =>
        {
            PromptOutcome outcome;
            try
            {
                outcome = await presenter.AskAsync(ask.Question, ask.Options, ask.TimeoutMs, scope);
            }
            catch (OperationCanceledException) when (scope.IsCancellationRequested)
            {
                ReleaseAsk(ask.Id);
                return;
            }
            catch (Exception t)
            {
                outcome = new PromptOutcome.Unavailable($"{PromptCodes.Internal}/presenter threw {t.GetType().Name}");
            }
            ReleaseAsk(ask.Id);
            var ms = Ms(startedAt);
            switch (outcome)
            {
                case PromptOutcome.Chosen c:
                    _runLog.Info(LogSubsystem.Transport, LogEvent.AskAnswered, ("id", ask.Id), ("choice", $"option[{c.Index}]"), ("ms", ms));
                    Reply(ask.Id, ControlOps.Ask, true, ControlChannelLogic.AskChosenPayload(c.Index, c.Label));
                    break;
                case PromptOutcome.RejectedAll:
                    _runLog.Info(LogSubsystem.Transport, LogEvent.AskAnswered, ("id", ask.Id), ("choice", "reject_all"), ("ms", ms));
                    Reply(ask.Id, ControlOps.Ask, true, ControlChannelLogic.AskRejectAllPayload());
                    break;
                case PromptOutcome.Reasked:
                    _runLog.Info(LogSubsystem.Transport, LogEvent.AskAnswered, ("id", ask.Id), ("choice", "reask"), ("ms", ms));
                    Reply(ask.Id, ControlOps.Ask, true, ControlChannelLogic.AskReaskPayload());
                    break;
                case PromptOutcome.TimedOut t:
                    var cause = t.Covered ? PromptCodes.TimeoutCovered : PromptCodes.TimeoutNoAnswer;
                    _runLog.Warn(LogSubsystem.Transport, LogEvent.AskUnavailable, ("id", ask.Id), ("cause", cause), ("ms", ms));
                    Reply(ask.Id, ControlOps.Ask, false, ControlChannelLogic.AskFailurePayload(cause));
                    break;
                case PromptOutcome.Unavailable u:
                    _runLog.Warn(LogSubsystem.Transport, LogEvent.AskUnavailable, ("id", ask.Id), ("cause", u.Cause), ("ms", ms));
                    Reply(ask.Id, ControlOps.Ask, false, ControlChannelLogic.AskFailurePayload(u.Cause));
                    break;
            }
        }, CancellationToken.None);
        return ControlResults.Accepted;
    }

    private void ReleaseAsk(string id)
    {
        lock (_askLock)
        {
            _inFlightAskIds.Remove(id);
        }
    }

    private string AskRejected(string id, string cause, long startedAt)
    {
        _runLog.Warn(LogSubsystem.Transport, LogEvent.AskUnavailable, ("id", id), ("cause", cause), ("ms", Ms(startedAt)));
        Reply(id, ControlOps.Ask, false, ControlChannelLogic.AskFailurePayload(cause));
        return ControlResults.Failed;
    }

    private void Reply(string id, string op, bool ok, IEnumerable<KeyValuePair<string, object?>> payload)
    {
        var text = RelayEncoding.EncodeControl(id, op, ok, payload);
        if (!_reaper.WriteTextAtomic(Path.Combine(_paths.ControlOutboxDir, id + RelayEnvelope.JsonSuffix), text))
        {
            _runLog.Error(LogSubsystem.Transport, LogEvent.ResponseWriteFailed, ("id", id));
        }
    }
}
