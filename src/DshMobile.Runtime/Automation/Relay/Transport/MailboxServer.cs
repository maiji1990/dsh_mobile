// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/transport/MailboxServer.kt
// Registered in docs/UPSTREAMS.md. Locked by tests/TestVectors/relay-mailbox.json.
//
// Execution model: upstream runs the loop and every resumption on one Dispatchers.IO.limitedParallelism(1) view
// (one thread at a time, interleaving at suspension points). The port uses one ExclusiveScheduler of a
// ConcurrentExclusiveSchedulerPair for the same guarantee: awaits inside the loop and the resumptions continue on it.
// Arrival marks: upstream FileObserver watches MOVED_TO only. FileSystemWatcher reports an in-directory rename as
// Renamed (marked, settle skipped); a move from another directory surfaces as Created and is not distinguishable
// from a direct write, so it only wakes the loop and the settle window applies (the conservative side).

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DshMobile.Runtime.Automation.Relay.Logging;
using DshMobile.Runtime.Automation.Relay.Storage;

namespace DshMobile.Runtime.Automation.Relay.Transport;

/// <summary>Upstream <c>HandlerResult</c>.</summary>
public abstract record HandlerResult
{
    /// <summary>Done: commit the terminal state and write the response.</summary>
    public sealed record Done(RelayResponse Response) : HandlerResult;

    /// <summary>Waiting for the user: the slot is released, the continuation answers later.</summary>
    public sealed record Parked(ParkedRequest Park) : HandlerResult;
}

/// <summary>Upstream <c>RelayRequestHandler</c>: the request passed shape checks; <paramref name="budgetMs"/> is host time left.</summary>
public delegate Task<HandlerResult> RelayRequestHandler(RelayRequest request, long budgetMs, CancellationToken cancellationToken);

/// <summary>Upstream <c>ParkedRequest</c>: an in-memory park; nothing of it survives a process death.</summary>
public sealed record ParkedRequest(
    string RequestId,
    Func<CancellationToken, Task<RelayResponse>> Resume,
    long ApprovalDeadlineAtMs,
    long ExecutionDeadlineAtMs,
    string ApprovalOwner);

/// <summary>Upstream <c>ParkedRequestBridge</c>.</summary>
public interface IParkedRequestBridge
{
    bool HasParkCapacity();

    bool Register(ParkedRequest park);

    /// <summary>First caller wins; a repeated trigger is a no-op.</summary>
    void ResumeParked(string requestId);
}

/// <summary>Upstream <c>ParkedRegistry</c>: bounded, take-once.</summary>
public sealed class ParkedRegistry(int capacity, long backstopGraceMs)
{
    private readonly Dictionary<string, ParkedRequest> _parks = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public bool Register(ParkedRequest park)
    {
        lock (_gate)
        {
            return _parks.Count < capacity && _parks.TryAdd(park.RequestId, park);
        }
    }

    public ParkedRequest? Take(string requestId)
    {
        lock (_gate)
        {
            return _parks.Remove(requestId, out var park) ? park : null;
        }
    }

    public int Size()
    {
        lock (_gate)
        {
            return _parks.Count;
        }
    }

    /// <summary>Parks whose approval deadline passed by at least the grace, earliest deadline first.</summary>
    public IReadOnlyList<string> Expired(long nowMs)
    {
        lock (_gate)
        {
            return _parks.Values.Where(p => nowMs - p.ApprovalDeadlineAtMs >= backstopGraceMs)
                .OrderBy(p => p.ApprovalDeadlineAtMs).Select(p => p.RequestId).ToList();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _parks.Clear();
        }
    }
}

/// <summary>Upstream <c>MailboxServer.WorkLoopHealth</c>.</summary>
public sealed record WorkLoopHealth(bool ChannelLive, int QueueDepth, long? OldestQueuedAgeMs, int InboxDepth, bool Stalled);

/// <summary>Upstream <c>MailboxServer.QueueCounts</c>.</summary>
public readonly record struct QueueCounts(int Depth, long? OldestAgeMs);

/// <summary>
/// Upstream <c>MailboxServer</c>: consumes <c>inbox/</c>, processes serially, answers into <c>outbox/</c>. Preconditions
/// are upstream's: one instance per inbox, ledger loaded and first sweep done before <see cref="Start"/>, Start/Stop
/// from one thread, and a handler that shows UI marshals to the main thread itself.
/// </summary>
public sealed class MailboxServer : IParkedRequestBridge
{
    public const int Capacity = 16;
    public const long HotPollMs = 120;
    public const long IdlePollMs = 5_000;
    public const long HotWindowMs = 30_000;
    public const long SweepIntervalMs = 30L * 60L * 1000L;
    public const long MaintenanceIntervalMs = 60_000;
    public const long SettleMs = 300;
    public const int CallSlotAttempts = 4;
    public const long CallSlotRetryMs = 80;
    public const int ParkedCapacity = 8;
    public const long ParkedBackstopGraceMs = 12_000;
    public const int MaxPerRound = 8;
    public const int MaxMalformedPerRound = 8;
    public const int MaxPendingFiles = 256;
    public const long RetryAfterMs = 1_000;
    public const string PartSuffix = ".part";
    public const string TempSuffix = ".tmp";
    public const long WriteReserveMs = 1_500;
    public const long ObserverRebuildMs = 30_000;

    /// <summary>Field names the two phase events fill (must equal the template placeholders).</summary>
    public static IReadOnlySet<string> CompletedFields { get; } = new HashSet<string> { "id", "state", "ms" };

    public static IReadOnlySet<string> PublishedFields { get; } = new HashSet<string> { "id", "bytes" };

    private readonly RelayPaths _paths;
    private readonly StorageReaper _reaper;
    private readonly RunLog _runLog;
    private readonly RelayRequestHandler _handler;
    private readonly Func<bool> _isDeviceInteractive;
    private readonly Action _onMaintenance;
    private readonly Func<long> _now;
    private readonly RequestStateStore? _stateStore;
    private readonly Func<MailboxServer, CancellationToken, IRelayControlChannel?>? _controlFactory;

    private readonly TaskScheduler _work = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, 1).ExclusiveScheduler;
    private readonly ParkedRegistry _parks = new(ParkedCapacity, ParkedBackstopGraceMs);
    private readonly Dictionary<string, long> _firstSeen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _admittedAt = new(StringComparer.Ordinal);
    private readonly HashSet<string> _arrivalMarks = new(StringComparer.Ordinal);
    private readonly Lock _books = new();

    private Channel<bool> _signals = NewSignals();
    private CancellationTokenSource? _cts;
    private volatile CancellationTokenSource? _loopScope;
    private IRelayControlChannel? _control;
    private FileSystemWatcher? _observer;
    private Task? _job;
    private long _lastWorkAtMs;
    private volatile bool _dirsOk;

    public MailboxServer(
        RelayPaths paths,
        StorageReaper reaper,
        RunLog runLog,
        RelayRequestHandler handler,
        Func<bool>? isDeviceInteractive = null,
        Action? onMaintenance = null,
        Func<long>? now = null,
        RequestStateStore? stateStore = null,
        Func<MailboxServer, CancellationToken, IRelayControlChannel?>? controlFactory = null)
    {
        _paths = paths;
        _reaper = reaper;
        _runLog = runLog;
        _handler = handler;
        _isDeviceInteractive = isDeviceInteractive ?? (() => true);
        _onMaintenance = onMaintenance ?? (() => { });
        _now = now ?? RelayClock.MonotonicNow;
        _stateStore = stateStore;
        _controlFactory = controlFactory;
    }

    public bool IsRunning => _job is { IsCompleted: false } && _dirsOk;

    /// <summary>The loop task (completes after <see cref="Stop"/>); null before a successful start.</summary>
    public Task? Completion => _job;

    private long LastWorkAtMs
    {
        get => Interlocked.Read(ref _lastWorkAtMs);
        set => Interlocked.Exchange(ref _lastWorkAtMs, value);
    }

    private static Channel<bool> NewSignals() =>
        Channel.CreateBounded<bool>(new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Start(CancellationToken outer = default)
    {
        if (_job is { IsCompleted: false })
        {
            return;
        }
        _paths.EnsureWritableDirs();
        if (!_paths.EnsureMailboxDirs())
        {
            _dirsOk = false;
            _runLog.Error(LogSubsystem.Storage, LogEvent.DirsUnavailable);
            return;
        }
        _dirsOk = true;
        // Crash recovery precedes all consumption and the control service (status must not read recovered rows as queued).
        _stateStore?.RecoverUnfinished();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        _cts = cts;
        if (_stateStore is not null && _controlFactory is not null)
        {
            _control = _controlFactory(this, outer);
        }
        _signals = NewSignals();
        _loopScope = cts;
        var token = cts.Token;
        _job = Task.Factory.StartNew(async () =>
        {
            _runLog.Info(LogSubsystem.Transport, LogEvent.ChannelStarted);
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

    public void Stop()
    {
        _control?.Halt();
        _control = null;
        _cts?.Cancel();
        _cts = null;
        _job = null;
        _loopScope = null;
        _parks.Clear();
        _runLog.Info(LogSubsystem.Transport, LogEvent.ChannelStopped);
    }

    private void RestartWatching()
    {
        StopWatching();
        try
        {
            var w = new FileSystemWatcher(_paths.InboxDir) { IncludeSubdirectories = false };
            w.Renamed += (_, e) =>
            {
                lock (_books)
                {
                    _arrivalMarks.Add(e.Name ?? "");
                }
                _signals.Writer.TryWrite(true);
            };
            w.Created += (_, _) => _signals.Writer.TryWrite(true);
            w.EnableRaisingEvents = true;
            _observer = w;
        }
        catch (Exception cause)
        {
            _runLog.Warn(LogSubsystem.Transport, LogEvent.ChannelDegraded, ("where", "startWatching"), ("cause", cause.GetType().Name));
        }
    }

    private void StopWatching()
    {
        _observer?.Dispose();
        _observer = null;
    }

    private long EnsureChannelDirs(long now, long lastRebuild)
    {
        var healthy = _paths.EnsureMailboxDirs();
        if (healthy != _dirsOk)
        {
            _runLog.Warn(LogSubsystem.Transport, LogEvent.ChannelDegraded, ("dirs", healthy ? "true" : "false"));
            _dirsOk = healthy;
        }
        if (!healthy || now - lastRebuild < ObserverRebuildMs)
        {
            return lastRebuild;
        }
        RestartWatching();
        return now;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        long lastSweep = 0, lastMaintenance = 0, lastRebuild = 0;
        RestartWatching();
        while (!ct.IsCancellationRequested)
        {
            var ts = _now();
            lastRebuild = EnsureChannelDirs(ts, lastRebuild);
            if (!_dirsOk)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(IdlePollMs), ct);
                continue;
            }
            var waitMs = _isDeviceInteractive() ? PollIntervalMs() : IdlePollMs;
            await WaitSignalAsync(waitMs, ct);
            await DrainAsync(ct);
            foreach (var id in _parks.Expired(_now()))
            {
                ResumeParked(id);
            }
            var after = _now();
            if (after - lastMaintenance >= MaintenanceIntervalMs)
            {
                lastMaintenance = after;
                Guarded("maintenance", _onMaintenance);
                if (_stateStore is { } store)
                {
                    Guarded("state-purge", () => store.PurgeExpired(_now()));
                }
            }
            if (after - lastSweep >= SweepIntervalMs)
            {
                lastSweep = after;
                Guarded("sweep", () => _reaper.Sweep());
            }
        }
    }

    private void Guarded(string where, Action action)
    {
        try
        {
            action();
        }
        catch (Exception cause)
        {
            _runLog.Warn(LogSubsystem.Transport, LogEvent.ChannelDegraded, ("where", where), ("cause", cause.GetType().Name));
        }
    }

    private async Task WaitSignalAsync(long waitMs, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(waitMs));
        try
        {
            await _signals.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
    }

    private long PollIntervalMs() => _now() - LastWorkAtMs < HotWindowMs ? HotPollMs : IdlePollMs;

    private static List<string>? ListFileNames(string dir)
    {
        try
        {
            return Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles().Select(f => f.Name).ToList() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        if (ListFileNames(_paths.InboxDir) is not { } all)
        {
            return;
        }
        AnswerMalformedNames(all.Where(n => !RelayEnvelope.IsRequestFileName(n) && !IsIncomingPartName(n)).ToList());
        var pendingNames = all.Where(RelayEnvelope.IsRequestFileName).ToList();
        IReadOnlyList<string> ordered;
        lock (_books)
        {
            foreach (var name in pendingNames)
            {
                _firstSeen.TryAdd(name, _now());
            }
            ordered = OrderPending(pendingNames.Select(n => (n, _firstSeen.TryGetValue(n, out var t) ? t : (long?)null)).ToList());
        }
        var victims = EvictionVictims(ordered, MaxPendingFiles);
        IReadOnlyList<string> queue = ordered;
        if (victims.Count > 0)
        {
            queue = ordered.Take(ordered.Count - victims.Count).ToList();
            var counts = Counts(pendingNames, ListFileNames(_paths.ProcessingDir) ?? []);
            _runLog.Warn(LogSubsystem.Transport, LogEvent.MailboxBackpressure,
                ("pending", pendingNames.Count.ToString(CultureInfo.InvariantCulture)), ("evicted", victims.Count.ToString(CultureInfo.InvariantCulture)),
                ("queueDepth", counts.Depth.ToString(CultureInfo.InvariantCulture)), ("oldestAgeMs", (counts.OldestAgeMs ?? 0L).ToString(CultureInfo.InvariantCulture)));
            var reason = $"retryAfterMs={RetryAfterMs} queueDepth={counts.Depth} oldestAgeMs={counts.OldestAgeMs ?? 0L}";
            foreach (var name in victims)
            {
                RejectAtSource(name, RelayError.QueueFull, reason);
            }
        }
        var settling = false;
        var handled = 0;
        foreach (var name in queue)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }
            if (handled >= MaxPerRound)
            {
                LastWorkAtMs = _now();
                break;
            }
            handled++;
            var source = Path.Combine(_paths.InboxDir, name);
            bool settle;
            lock (_books)
            {
                if (!_firstSeen.TryGetValue(name, out var seenAt))
                {
                    seenAt = _now();
                    _firstSeen[name] = seenAt;
                }
                settle = NeedsSettle(seenAt, _now(), _arrivalMarks.Contains(name));
                if (!settle)
                {
                    _firstSeen.Remove(name);
                    _arrivalMarks.Remove(name);
                }
            }
            if (settle)
            {
                settling = true;
                continue;
            }
            var working = Path.Combine(_paths.ProcessingDir, name);
            var id = RequestId(name);
            if (_stateStore is { } store && ClaimBlockedByInFlight(store.Get(id), _now(), File.Exists(working)))
            {
                _runLog.Warn(LogSubsystem.Transport, LogEvent.RequestDropped, ("file", name), ("cause", "duplicate id still in flight"));
                LastWorkAtMs = _now();
                WriteResponse(Failure(id, RelayError.InternalError, _now(),
                    "a request with this id is already in flight; wait for it to settle " +
                    "and resubmit to get the recorded outcome, or use a fresh id"));
                if (!StorageReaper.TryDelete(source))
                {
                    _runLog.Warn(LogSubsystem.Transport, LogEvent.RequestDropped, ("file", name), ("cause", "conflict delete"));
                }
                continue;
            }
            if (File.Exists(working) && !StorageReaper.TryDelete(working))
            {
                _runLog.Warn(LogSubsystem.Transport, LogEvent.RequestDropped, ("file", name), ("cause", "stale target"));
                continue;
            }
            if (!TryRename(source, working))
            {
                continue;
            }
            _reaper.NoteProcessing(name);
            lock (_books)
            {
                _admittedAt[name] = _now();
            }
            LastWorkAtMs = _now();
            var acceptedAt = LastWorkAtMs;
            _runLog.Info(LogSubsystem.Transport, LogEvent.CallAccepted, ("id", id));
            var parked = false;
            try
            {
                if (!await AwaitCallSlotAsync(working, acceptedAt, ct))
                {
                    continue;
                }
                try
                {
                    parked = await ProcessAsync(working, acceptedAt, ct);
                }
                finally
                {
                    if (!parked)
                    {
                        _reaper.EndCall();
                    }
                }
                if (!parked)
                {
                    LogSettled(acceptedAt);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception t)
            {
                _runLog.Error(LogSubsystem.Transport, LogEvent.CallFailed, ("id", name), ("type", t.GetType().Name));
                StorageReaper.TryDelete(working);
            }
            finally
            {
                if (!parked)
                {
                    _reaper.ClearProcessing(name);
                    lock (_books)
                    {
                        _admittedAt.Remove(name);
                    }
                }
            }
        }
        var remainingInbox = (ListFileNames(_paths.InboxDir) ?? []).ToHashSet(StringComparer.Ordinal);
        var remainingProcessing = (ListFileNames(_paths.ProcessingDir) ?? []).ToHashSet(StringComparer.Ordinal);
        lock (_books)
        {
            foreach (var k in _firstSeen.Keys.Where(k => !remainingInbox.Contains(k)).ToList())
            {
                _firstSeen.Remove(k);
            }
            _arrivalMarks.IntersectWith(remainingInbox);
            foreach (var k in _admittedAt.Keys.Where(k => !remainingProcessing.Contains(k)).ToList())
            {
                _admittedAt.Remove(k);
            }
        }
        if (settling)
        {
            LastWorkAtMs = _now();
        }
    }

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

    /// <summary>Upstream <c>answerMalformedNames</c>: reply under the original name, with a sanitized id inside.</summary>
    private void AnswerMalformedNames(IReadOnlyList<string> rejected)
    {
        foreach (var name in rejected.Take(MaxMalformedPerRound))
        {
            var id = MalformedNameId(name);
            _runLog.Warn(LogSubsystem.Transport, LogEvent.RequestDropped, ("id", id),
                ("detail", "malformed request file name " + Take(name, 80)));
            var reply = Failure(id, RelayError.TransportMalformed, _now());
            if (!_reaper.WriteTextAtomic(Path.Combine(_paths.OutboxDir, name), RelayEncoding.Encode(reply)))
            {
                WriteResponse(reply);
            }
            StorageReaper.TryDelete(Path.Combine(_paths.InboxDir, name));
        }
    }

    private void RejectAtSource(string name, RelayError error, string reason)
    {
        var working = Path.Combine(_paths.ProcessingDir, name);
        if (File.Exists(working) && !StorageReaper.TryDelete(working))
        {
            return;
        }
        if (!TryRename(Path.Combine(_paths.InboxDir, name), working))
        {
            return;
        }
        var reply = Failure(RequestId(name), error, _now(), reason);
        SettleStateful(reply);
        WriteResponse(reply);
        StorageReaper.TryDelete(working);
        lock (_books)
        {
            _firstSeen.Remove(name);
            _arrivalMarks.Remove(name);
        }
    }

    private static string RequestId(string fileName) =>
        fileName.EndsWith(RelayEnvelope.JsonSuffix, StringComparison.Ordinal) ? fileName[..^RelayEnvelope.JsonSuffix.Length] : fileName;

    private long? AdmittedSince(string id)
    {
        lock (_books)
        {
            return _admittedAt.TryGetValue(id + RelayEnvelope.JsonSuffix, out var a) ? a
                : _admittedAt.TryGetValue(id, out var b) ? b : null;
        }
    }

    private long ElapsedSinceAdmitted(string id) => AdmittedSince(id) is { } admitted ? Math.Max(0L, _now() - admitted) : 0L;

    private void LogSettled(long acceptedAt) =>
        _runLog.Info(LogSubsystem.Transport, LogEvent.CallSettled, ("ms", (_now() - acceptedAt).ToString(CultureInfo.InvariantCulture)));

    private async Task<bool> AwaitCallSlotAsync(string working, long acceptedAt, CancellationToken ct)
    {
        for (var i = 0; i < CallSlotAttempts; i++)
        {
            if (_reaper.BeginCall())
            {
                return true;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(CallSlotRetryMs), ct);
        }
        var name = Path.GetFileName(working);
        _runLog.Warn(LogSubsystem.Transport, LogEvent.CallDeferred, ("id", name), ("cause", "sweep busy"));
        var reply = Failure(RequestId(name), RelayError.QueueFull, acceptedAt, QueueFullReason());
        SettleStateful(reply);
        WriteResponse(reply);
        StorageReaper.TryDelete(working);
        LogSettled(acceptedAt);
        return false;
    }

    /// <returns>true = parked (file kept, no response, slot returned); false = settled with a response.</returns>
    private async Task<bool> ProcessAsync(string working, long acceptedAt, CancellationToken ct)
    {
        var name = Path.GetFileName(working);
        var id = RequestId(name);
        var store = _stateStore;
        if (store?.Get(id) is not null)
        {
            store.MarkAdmitted(id);
        }
        var length = FileLength(working);
        if (length > RelayEnvelope.MaxRequestBytes)
        {
            var reply = Failure(id, RelayError.RequestTooLarge, acceptedAt);
            SettleStateful(reply);
            WriteResponse(reply);
            StorageReaper.TryDelete(working);
            _runLog.Error(LogSubsystem.Transport, LogEvent.RequestTooLarge, ("bytes", length.ToString(CultureInfo.InvariantCulture)));
            return false;
        }
        string text;
        try
        {
            text = File.ReadAllText(working, Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StorageReaper.TryDelete(working);
            _runLog.Warn(LogSubsystem.Transport, LogEvent.RequestDropped, ("file", name), ("cause", "unreadable"));
            SettleStateful(Failure(id, RelayError.TransportMalformed, acceptedAt, "unreadable"));
            return false;
        }
        var decoded = RelayEnvelope.DecodeRequest(text, name);
        if (decoded is RelayDecodeResult.Rejected rejected)
        {
            var reply = Failure(rejected.Id, rejected.Error, acceptedAt);
            SettleStateful(reply);
            StorageReaper.TryDelete(working);
            _runLog.Warn(LogSubsystem.Transport, LogEvent.RequestDropped, ("file", name), ("cause", rejected.Cause));
            WriteResponse(reply);
            return false;
        }
        var request = ((RelayDecodeResult.Valid)decoded).Request;
        var budget = Budget(request.TimeoutMs, _now() - acceptedAt);
        HandlerResult result;
        if (budget <= 0)
        {
            result = new HandlerResult.Done(Failure(id, RelayError.TransportTimeout, acceptedAt));
        }
        else
        {
            using var call = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                result = await _handler(request, budget, call.Token).WaitAsync(TimeSpan.FromMilliseconds(budget), ct);
            }
            catch (TimeoutException)
            {
                call.Cancel();
                result = new HandlerResult.Done(Failure(id, RelayError.TransportTimeout, acceptedAt));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception t)
            {
                result = new HandlerResult.Done(Failure(id, RelayError.InternalError, acceptedAt, t.GetType().Name));
            }
        }
        switch (result)
        {
            case HandlerResult.Done done:
                SettleStateful(done.Response);
                if (!WriteResponse(done.Response))
                {
                    _runLog.Error(LogSubsystem.Transport, LogEvent.ResponseWriteFailed, ("id", request.Id));
                }
                StorageReaper.TryDelete(working);
                return false;
            case HandlerResult.Parked:
                if (store?.Get(id) is not null)
                {
                    store.MarkWaiting(id, relayUser: true);
                }
                _reaper.EndCall();
                _runLog.Warn(LogSubsystem.Transport, LogEvent.CallDeferred, ("id", id), ("cause", "awaiting_approval"));
                return true;
            default:
                throw new InvalidOperationException("unknown handler result");
        }
    }

    private static long FileLength(string path)
    {
        try
        {
            return new FileInfo(path) is { Exists: true } f ? f.Length : 0L;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0L;
        }
    }

    public void ResumeParked(string requestId)
    {
        if (_parks.Take(requestId) is not { } park || _loopScope is not { } scope)
        {
            return;
        }
        var token = scope.Token;
        _ = Task.Factory.StartNew(() => ResumeOneAsync(park, token), token, TaskCreationOptions.DenyChildAttach, _work).Unwrap();
    }

    public bool HasParkCapacity() => _parks.Size() < ParkedCapacity;

    public bool Register(ParkedRequest park) => _parks.Register(park);

    private async Task ResumeOneAsync(ParkedRequest park, CancellationToken ct)
    {
        if (_loopScope is null)
        {
            return;
        }
        var working = Path.Combine(_paths.ProcessingDir, park.RequestId + RelayEnvelope.JsonSuffix);
        var acceptedAt = AdmittedSince(park.RequestId);
        try
        {
            if (!await AwaitResumeSlotAsync(park, acceptedAt, ct))
            {
                return;
            }
            try
            {
                FinishParked(working, await ComputeResumedResponseAsync(park, acceptedAt, ct), acceptedAt);
            }
            finally
            {
                _reaper.EndCall();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // stopping: the file stays for the next start's orphan sweep and state recovery
        }
        catch (Exception t)
        {
            _runLog.Error(LogSubsystem.Transport, LogEvent.CallFailed, ("id", Path.GetFileName(working)), ("type", t.GetType().Name));
            try
            {
                FinishParked(working, Failure(park.RequestId, RelayError.InternalError, acceptedAt ?? _now(), t.GetType().Name), acceptedAt);
            }
            catch (Exception)
            {
                // best effort, as upstream runCatching
            }
        }
        finally
        {
            _reaper.ClearProcessing(Path.GetFileName(working));
            lock (_books)
            {
                _admittedAt.Remove(park.RequestId);
                _admittedAt.Remove(park.RequestId + RelayEnvelope.JsonSuffix);
            }
        }
    }

    private async Task<bool> AwaitResumeSlotAsync(ParkedRequest park, long? acceptedAt, CancellationToken ct)
    {
        for (var i = 0; i < CallSlotAttempts; i++)
        {
            if (_reaper.BeginCall())
            {
                return true;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(CallSlotRetryMs), ct);
        }
        _runLog.Warn(LogSubsystem.Transport, LogEvent.CallDeferred, ("id", park.RequestId), ("cause", "resume slot busy"));
        var reply = Failure(park.RequestId, RelayError.QueueFull, acceptedAt ?? _now(), QueueFullReason());
        FinishParked(Path.Combine(_paths.ProcessingDir, park.RequestId + RelayEnvelope.JsonSuffix), reply, acceptedAt);
        return false;
    }

    private async Task<RelayResponse> ComputeResumedResponseAsync(ParkedRequest park, long? acceptedAt, CancellationToken ct)
    {
        var remaining = park.ExecutionDeadlineAtMs - _now();
        if (remaining <= 0)
        {
            return Failure(park.RequestId, RelayError.TransportTimeout, acceptedAt ?? _now());
        }
        using var call = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            return await park.Resume(call.Token).WaitAsync(TimeSpan.FromMilliseconds(remaining), ct);
        }
        catch (TimeoutException)
        {
            call.Cancel();
            return Failure(park.RequestId, RelayError.TransportTimeout, acceptedAt ?? _now());
        }
    }

    private void FinishParked(string working, RelayResponse response, long? acceptedAt)
    {
        SettleStateful(response);
        if (!WriteResponse(response))
        {
            _runLog.Error(LogSubsystem.Transport, LogEvent.ResponseWriteFailed, ("id", response.Id));
        }
        if (File.Exists(working) && !StorageReaper.TryDelete(working))
        {
            _runLog.Warn(LogSubsystem.Transport, LogEvent.RequestDropped, ("file", Path.GetFileName(working)), ("cause", "settle delete"));
        }
        LogSettled(acceptedAt ?? _now());
    }

    /// <summary>Upstream <c>settleStateful</c>: terminal state first, then the control frame that tells the same story.</summary>
    private void SettleStateful(RelayResponse response)
    {
        if (_stateStore is not { } store || store.Get(response.Id) is null)
        {
            return;
        }
        var encoded = RelayEncoding.Encode(response);
        var phase = TerminalPhaseOf(response);
        var recorded = store.RecordTerminal(response.Id, phase, encoded);
        _runLog.Info(LogSubsystem.Transport, LogEvent.RequestCompleted, ("id", response.Id),
            ("state", (recorded?.Phase ?? RequestPhase.Unknown).Wire()), ("ms", ElapsedSinceAdmitted(response.Id).ToString(CultureInfo.InvariantCulture)));
        var frame = TerminalFrame(response.Id, response.Ok, recorded);
        if (!_reaper.WriteTextAtomic(Path.Combine(_paths.ControlOutboxDir, response.Id + RelayEnvelope.JsonSuffix), frame))
        {
            _runLog.Error(LogSubsystem.Transport, LogEvent.ResponseWriteFailed, ("id", response.Id));
        }
    }

    private string QueueFullReason()
    {
        var counts = Counts(ListFileNames(_paths.InboxDir) ?? [], ListFileNames(_paths.ProcessingDir) ?? []);
        return $"retryAfterMs={RetryAfterMs} queueDepth={counts.Depth} oldestAgeMs={counts.OldestAgeMs ?? 0L}";
    }

    private QueueCounts Counts(IReadOnlyCollection<string> inbox, IReadOnlyCollection<string> processing)
    {
        lock (_books)
        {
            return QueueCountsOf(inbox, processing, _firstSeen, _admittedAt, _now());
        }
    }

    /// <summary>Upstream <c>workLoopHealth</c>: read-only; stalled = inbox backlog and no progress within the hot window.</summary>
    public WorkLoopHealth Health()
    {
        var stamp = _now();
        var inbox = ListFileNames(_paths.InboxDir) ?? [];
        var processing = ListFileNames(_paths.ProcessingDir) ?? [];
        QueueCounts counts;
        lock (_books)
        {
            counts = QueueCountsOf(inbox, processing, _firstSeen, _admittedAt, stamp);
        }
        var stalled = inbox.Count > 0 && stamp - LastWorkAtMs >= HotWindowMs;
        return new WorkLoopHealth(!stalled, counts.Depth, counts.OldestAgeMs, inbox.Count, stalled);
    }

    private bool WriteResponse(RelayResponse response)
    {
        var target = Path.Combine(_paths.OutboxDir, response.Id + RelayEnvelope.JsonSuffix);
        if (File.Exists(target))
        {
            _runLog.Warn(LogSubsystem.Transport, LogEvent.ResponseOverwritten, ("id", response.Id));
        }
        var encoded = RelayEncoding.Encode(response);
        if (encoded.Length > RelayEnvelope.MaxResponseChars)
        {
            _runLog.Warn(LogSubsystem.Transport, LogEvent.ResponseTooLarge, ("id", response.Id));
            var shrunk = RelayEncoding.Encode(Shrunk(response));
            if (!_reaper.WriteTextAtomic(target, shrunk))
            {
                return false;
            }
            LogResponsePublished(response.Id, shrunk);
            return true;
        }
        if (!_reaper.WriteTextAtomic(target, encoded))
        {
            return false;
        }
        LogResponsePublished(response.Id, encoded);
        return true;
    }

    private void LogResponsePublished(string id, string writtenBody) =>
        _runLog.Info(LogSubsystem.Transport, LogEvent.ResponsePublished, ("id", id), ("bytes", Encoding.UTF8.GetByteCount(writtenBody).ToString(CultureInfo.InvariantCulture)));

    private RelayResponse Failure(string id, RelayError error, long startedAt, string? reason = null) =>
        RelayResponse.Failure(id, error, _now() - startedAt, reason);

    // ---- pure functions (upstream companion) ----

    /// <summary>Upstream <c>writeResponse</c> oversize branch: same timing, no data/artifacts, REQUEST_TOO_LARGE.</summary>
    public static RelayResponse Shrunk(RelayResponse response) => response with
    {
        Ok = false,
        Data = null,
        Artifacts = [],
        Reason = $"response exceeds {RelayEnvelope.MaxResponseChars} chars",
        Error = RelayError.RequestTooLarge,
    };

    /// <summary>Upstream <c>settleStateful</c> phase rule.</summary>
    public static RequestPhase TerminalPhaseOf(RelayResponse response) =>
        response.Ok ? RequestPhase.Succeeded
        : response.Error == RelayError.GateCancelled ? RequestPhase.Cancelled
        : RequestPhase.Failed;

    /// <summary>Upstream <c>process</c> budget: ttl minus time since claim minus a proportional write reserve.</summary>
    public static long Budget(long timeoutMs, long sinceAcceptedMs) => timeoutMs - sinceAcceptedMs - Math.Min(WriteReserveMs, timeoutMs / 4);

    /// <summary>Upstream <c>answerMalformedNames</c> id: <c>[A-Za-z0-9_-]</c> else '_', first 64, else "unnamed".</summary>
    public static string MalformedNameId(string fileName)
    {
        var stem = fileName.EndsWith(RelayEnvelope.JsonSuffix, StringComparison.Ordinal) ? fileName[..^RelayEnvelope.JsonSuffix.Length] : fileName;
        var sb = new StringBuilder(stem.Length);
        foreach (var c in stem)
        {
            sb.Append(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' ? c : '_');
        }
        var id = Take(sb.ToString(), 64);
        return id.Length == 0 ? "unnamed" : id;
    }

    private static string Take(string s, int n) => s.Length <= n ? s : s[..n];

    public static IReadOnlyList<string> OrderPending(IReadOnlyList<(string Name, long? SeenAt)> entries) =>
        entries.OrderBy(e => e.SeenAt is null).ThenBy(e => e.SeenAt ?? long.MaxValue).ThenBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => e.Name).ToList();

    public static IReadOnlyList<string> EvictionVictims(IReadOnlyList<string> orderedNames, int capacity) =>
        orderedNames.Count <= capacity ? [] : orderedNames.Skip(capacity).ToList();

    public static bool NeedsSettle(long seenAtMs, long nowMs, bool arrivedByRename) => !arrivedByRename && nowMs - seenAtMs < SettleMs;

    /// <summary>Upstream <c>terminalFramePayload</c>, in libcore key order.</summary>
    public static IReadOnlyList<KeyValuePair<string, object?>> TerminalFramePayload(string id, RequestState? recorded)
    {
        var payload = new List<KeyValuePair<string, object?>>
        {
            new("state", (recorded?.Phase ?? RequestPhase.Unknown).Wire()),
            new("targetId", id),
        };
        if (recorded is null)
        {
            payload.Add(new("cause", "E_INTERNAL/terminal state not persisted; outcome unknown"));
        }
        else
        {
            payload.Add(new("result", ResultObjectOrNull(recorded.ResultJson)));
        }
        return payload;
    }

    /// <summary>The control frame <c>settleStateful</c> writes to <c>control-outbox/&lt;id&gt;.json</c>.</summary>
    public static string TerminalFrame(string id, bool responseOk, RequestState? recorded) =>
        RelayEncoding.EncodeControl(id, "submit", responseOk && recorded is not null, TerminalFramePayload(id, recorded));

    internal static readonly JsonElement JsonNull = JsonElement.Parse("null");

    internal static JsonElement ResultObjectOrNull(string? resultJson)
    {
        if (resultJson is null)
        {
            return JsonNull;
        }
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : JsonNull;
        }
        catch (JsonException)
        {
            return JsonNull;
        }
    }

    public static bool ClaimBlockedByInFlight(RequestState? existing, long nowMs, bool processingFileExists) =>
        processingFileExists
        || (existing is not null && !existing.IsTerminal && existing.Phase != RequestPhase.Queued
            && !RequestStateStore.NonTerminalRecordExpired(existing, nowMs));

    public static bool IsIncomingPartName(string name) =>
        name.StartsWith('.') || name.EndsWith(PartSuffix, StringComparison.Ordinal) || name.EndsWith(TempSuffix, StringComparison.Ordinal);

    public static QueueCounts QueueCountsOf(
        IReadOnlyCollection<string> inbox,
        IReadOnlyCollection<string> processing,
        IReadOnlyDictionary<string, long> seenAt,
        IReadOnlyDictionary<string, long> admittedAt,
        long nowMs)
    {
        long? oldest = null;
        foreach (var name in inbox.Concat(processing))
        {
            long? stamp = seenAt.TryGetValue(name, out var s)
                ? admittedAt.TryGetValue(name, out var a) ? Math.Min(s, a) : s
                : admittedAt.TryGetValue(name, out var a2) ? a2 : null;
            if (stamp is { } t && (oldest is null || t < oldest))
            {
                oldest = t;
            }
        }
        return new QueueCounts(inbox.Count + processing.Count, oldest is { } o ? Math.Max(0L, nowMs - o) : null);
    }
}

/// <summary>The control channel the mailbox starts alongside itself when a state store is wired (upstream <c>ControlChannelServer</c>).</summary>
public interface IRelayControlChannel
{
    void Halt();
}
