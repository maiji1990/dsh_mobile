// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/transport/RequestStateStore.kt
// Registered in docs/UPSTREAMS.md. Locked by tests/TestVectors/relay-mailbox.json.

using System.Text;
using System.Text.Json;
using DshMobile.Runtime.Automation.Relay.Logging;
using DshMobile.Runtime.Automation.Relay.Storage;

namespace DshMobile.Runtime.Automation.Relay.Transport;

/// <summary>Upstream <c>RequestPhase</c> (wire names are the UPPER_SNAKE forms, see <see cref="RequestPhases"/>).</summary>
public enum RequestPhase
{
    Queued,
    Admitted,
    WaitingRelayUser,
    WaitingAndroidUser,
    Ready,
    Executing,
    Succeeded,
    Failed,
    Cancelled,
    CancelRequested,
    InterruptedBeforeExecution,
    Unknown,
    Expired,
}

public static class RequestPhases
{
    private static readonly string[] Names =
    [
        "QUEUED", "ADMITTED", "WAITING_RELAY_USER", "WAITING_ANDROID_USER", "READY", "EXECUTING", "SUCCEEDED", "FAILED",
        "CANCELLED", "CANCEL_REQUESTED", "INTERRUPTED_BEFORE_EXECUTION", "UNKNOWN", "EXPIRED",
    ];

    public static string Wire(this RequestPhase p) => Names[(int)p];

    public static RequestPhase? Parse(string? wire)
    {
        var i = Array.IndexOf(Names, wire);
        return i < 0 ? null : (RequestPhase)i;
    }

    public static IReadOnlySet<RequestPhase> Terminal { get; } = new HashSet<RequestPhase>
    {
        RequestPhase.Succeeded, RequestPhase.Failed, RequestPhase.Cancelled, RequestPhase.InterruptedBeforeExecution,
        RequestPhase.Unknown, RequestPhase.Expired,
    };
}

/// <summary>Upstream <c>RequestState</c>.</summary>
public sealed record RequestState(
    string Id,
    string Digest,
    string Capability,
    RequestPhase Phase,
    long CreatedAtMs,
    long? AdmittedAtMs,
    long TtlMs,
    bool MayHaveDispatched = false,
    long? TerminalAtMs = null,
    string? ResultJson = null,
    bool CancelRequested = false)
{
    public bool IsTerminal => RequestPhases.Terminal.Contains(Phase);
}

public abstract record CreateOutcome
{
    public sealed record Created(RequestState State) : CreateOutcome;
    public sealed record Existing(RequestState State) : CreateOutcome;
    public sealed record DigestConflict(RequestState ExistingState) : CreateOutcome;
    public sealed record PersistFailed : CreateOutcome;
}

public abstract record CancelOutcome
{
    public sealed record Requested(RequestState State, bool ProvablyUndispatched) : CancelOutcome;
    public sealed record AlreadyTerminal(RequestState State) : CancelOutcome;
    public sealed record NotRecorded : CancelOutcome;
    public sealed record NotFound : CancelOutcome;
}

public abstract record DispatchGuardOutcome
{
    public sealed record Armed(RequestState State) : DispatchGuardOutcome;
    public sealed record NotExecutable(RequestState? State) : DispatchGuardOutcome;
    public sealed record PersistFailed : DispatchGuardOutcome;
}

/// <summary>
/// Upstream <c>RequestStateStore</c>: one JSON record per submitted (v2) request in the private state directory.
/// Terminal records never change; the dispatch guard and terminals are fsynced; records older than the orphan TTL
/// that never settled become UNKNOWN (may have run) or INTERRUPTED_BEFORE_EXECUTION; settled results are kept
/// 30 min then reduced to EXPIRED, which is deleted after 24 h.
/// </summary>
public sealed class RequestStateStore
{
    public const int MaxTracked = 512;
    public const string WaitingKindRelay = "relay";
    public const string WaitingKindAndroid = "android";
    public const long ResultRetentionMs = 30L * 60L * 1000L;
    public const long StateRetentionMs = 24L * 60L * 60L * 1000L;

    private readonly string _dir;
    private readonly Func<long> _now;
    private readonly Func<long> _wallNow;
    private readonly RunLog? _log;
    private readonly Lock _lock = new();

    public RequestStateStore(string stateDir, Func<long>? now = null, Func<long>? wallNow = null, RunLog? runLog = null)
    {
        _dir = stateDir;
        _now = now ?? RelayClock.MonotonicNow;
        _wallNow = wallNow ?? RelayClock.WallNow;
        _log = runLog;
    }

    public static bool NonTerminalRecordExpired(RequestState state, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(state);
        return !state.IsTerminal && nowMs - state.CreatedAtMs >= QuotaLedger.OrphanRequestTtlMs;
    }

    public static LogEvent? StageEntryEvent(RequestPhase? before, RequestPhase? after) =>
        before is null || after is null || before == after ? null
        : after == RequestPhase.Admitted ? LogEvent.RequestAdmitted
        : after is RequestPhase.WaitingRelayUser or RequestPhase.WaitingAndroidUser ? LogEvent.RequestWaitingUser
        : null;

    public static string? WaitingKindOf(RequestPhase? phase) => phase switch
    {
        RequestPhase.WaitingRelayUser => WaitingKindRelay,
        RequestPhase.WaitingAndroidUser => WaitingKindAndroid,
        _ => null,
    };

    public CreateOutcome Create(string id, string digest, string capability, long ttlMs)
    {
        lock (_lock)
        {
            if (ReadState(Target(id)) is { } existing)
            {
                return existing.Digest == digest ? new CreateOutcome.Existing(existing) : new CreateOutcome.DigestConflict(existing);
            }
            if (StateFiles().Count > MaxTracked)
            {
                PurgeExpiredLocked(_now());
            }
            var state = new RequestState(id, digest, capability, RequestPhase.Queued, _now(), null, ttlMs);
            if (!Persist(state, force: false))
            {
                return new CreateOutcome.PersistFailed();
            }
            _log?.Info(LogSubsystem.Transport, LogEvent.RequestStateCreated, ("id", id), ("cap", capability));
            return new CreateOutcome.Created(state);
        }
    }

    public RequestState? Get(string id) => ReadState(Target(id));

    public RequestState? MarkAdmitted(string id)
    {
        lock (_lock)
        {
            var (before, after) = Transition(id, s => s with { Phase = RequestPhase.Admitted, AdmittedAtMs = s.AdmittedAtMs ?? _now() });
            LogEntry(before, after, id);
            return after;
        }
    }

    public RequestState? MarkWaiting(string id, bool relayUser)
    {
        lock (_lock)
        {
            var phase = relayUser ? RequestPhase.WaitingRelayUser : RequestPhase.WaitingAndroidUser;
            var (before, after) = Transition(id, s => s with { Phase = phase });
            LogEntry(before, after, id);
            return after;
        }
    }

    public bool MarkExecDispatchGuard(string id) => ArmExecDispatchGuard(id) is DispatchGuardOutcome.Armed;

    /// <summary>Upstream <c>armExecDispatchGuard</c>: EXECUTING + mayHaveDispatched, fsynced, before the backend runs.</summary>
    public DispatchGuardOutcome ArmExecDispatchGuard(string id)
    {
        lock (_lock)
        {
            var current = ReadState(Target(id));
            if (current is null || current.IsTerminal || NonTerminalRecordExpired(current, _now()))
            {
                _log?.Warn(LogSubsystem.Transport, LogEvent.RequestDispatchNotExecutable, ("id", id), ("phase", current?.Phase.Wire() ?? "absent"));
                return new DispatchGuardOutcome.NotExecutable(current);
            }
            var guarded = current with { Phase = RequestPhase.Executing, MayHaveDispatched = true };
            if (!Persist(guarded, force: true))
            {
                _log?.Error(LogSubsystem.Transport, LogEvent.RequestDispatchGuardFailed, ("id", id));
                return new DispatchGuardOutcome.PersistFailed();
            }
            return new DispatchGuardOutcome.Armed(guarded);
        }
    }

    public RequestState? RecordTerminal(string id, RequestPhase phase, string? resultJson = null)
    {
        lock (_lock)
        {
            if (!RequestPhases.Terminal.Contains(phase) || ReadState(Target(id)) is not { } current)
            {
                return null;
            }
            if (current.IsTerminal)
            {
                return current;
            }
            var terminal = current with { Phase = phase, ResultJson = resultJson, TerminalAtMs = _now() };
            if (!Persist(terminal, force: true))
            {
                return null;
            }
            _log?.Info(LogSubsystem.Transport, LogEvent.RequestStateTerminal, ("id", id), ("phase", phase.Wire()));
            return terminal;
        }
    }

    public CancelOutcome RequestCancel(string id)
    {
        lock (_lock)
        {
            if (ReadState(Target(id)) is not { } current)
            {
                return new CancelOutcome.NotFound();
            }
            if (current.IsTerminal)
            {
                return new CancelOutcome.AlreadyTerminal(current);
            }
            var marked = current with { CancelRequested = true };
            return Persist(marked, force: false)
                ? new CancelOutcome.Requested(marked, !marked.MayHaveDispatched)
                : new CancelOutcome.NotRecorded();
        }
    }

    public void PurgeExpired(long nowMs)
    {
        lock (_lock)
        {
            PurgeExpiredLocked(nowMs);
        }
    }

    /// <summary>Upstream <c>recoverUnfinished</c>: after a restart nothing non-terminal survives.</summary>
    public IReadOnlyList<RequestState> RecoverUnfinished()
    {
        lock (_lock)
        {
            var recovered = new List<RequestState>();
            foreach (var file in StateFiles())
            {
                if (ReadState(file) is not { IsTerminal: false } state)
                {
                    continue;
                }
                var phase = state.MayHaveDispatched ? RequestPhase.Unknown : RequestPhase.InterruptedBeforeExecution;
                var settled = state with { Phase = phase, TerminalAtMs = _now() };
                if (!Persist(settled, force: false))
                {
                    continue;
                }
                _log?.Warn(LogSubsystem.Transport, LogEvent.RequestRecovered, ("id", state.Id), ("phase", phase.Wire()));
                recovered.Add(settled);
            }
            return recovered;
        }
    }

    /// <summary>Upstream <c>toJson</c> text (org.json insertion order).</summary>
    public static string ToJson(RequestState s, long wallNowMs)
    {
        ArgumentNullException.ThrowIfNull(s);
        var o = new LibcoreJson.ObjectWriter();
        o.Put("id", s.Id);
        o.Put("digest", s.Digest);
        o.Put("capability", s.Capability);
        o.Put("phase", s.Phase.Wire());
        o.Put("createdAtMs", s.CreatedAtMs);
        if (s.AdmittedAtMs is { } a)
        {
            o.Put("admittedAtMs", a);
        }
        o.Put("ttlMs", s.TtlMs);
        o.Put("mayHaveDispatched", s.MayHaveDispatched);
        if (s.TerminalAtMs is { } t)
        {
            o.Put("terminalAtMs", t);
        }
        if (s.ResultJson is { } r)
        {
            o.Put("resultJson", r);
        }
        o.Put("cancelRequested", s.CancelRequested);
        o.Put("updatedAtWallMs", wallNowMs);
        return o.ToString();
    }

    /// <summary>Upstream <c>readState</c>; null for a missing, unreadable or malformed record.</summary>
    public static RequestState? FromJson(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var j = doc.RootElement;
            if (j.ValueKind != JsonValueKind.Object || RequestPhases.Parse(j.GetProperty("phase").GetString()) is not { } phase)
            {
                return null;
            }
            long? Opt(string k) => j.TryGetProperty(k, out var p) ? p.GetInt64() : null;
            bool OptBool(string k) => j.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.True;
            return new RequestState(
                j.GetProperty("id").GetString()!, j.GetProperty("digest").GetString()!, j.GetProperty("capability").GetString()!,
                phase, j.GetProperty("createdAtMs").GetInt64(), Opt("admittedAtMs"), j.GetProperty("ttlMs").GetInt64(),
                OptBool("mayHaveDispatched"), Opt("terminalAtMs"),
                j.TryGetProperty("resultJson", out var r) ? OrgJson.JavaString(r) : null,
                OptBool("cancelRequested"));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }

    private void PurgeExpiredLocked(long nowMs)
    {
        foreach (var file in StateFiles())
        {
            if (ReadState(file) is not { } state)
            {
                continue;
            }
            if (NonTerminalRecordExpired(state, nowMs))
            {
                var phase = state.MayHaveDispatched ? RequestPhase.Unknown : RequestPhase.InterruptedBeforeExecution;
                Persist(state with { Phase = phase, TerminalAtMs = nowMs }, force: false);
                continue;
            }
            if (state.TerminalAtMs is not { } terminalAt)
            {
                continue;
            }
            if (state.Phase == RequestPhase.Expired && nowMs - terminalAt >= StateRetentionMs)
            {
                StorageReaper.TryDelete(file);
            }
            else if (state.Phase != RequestPhase.Expired && nowMs - terminalAt >= ResultRetentionMs)
            {
                Persist(state with { Phase = RequestPhase.Expired, ResultJson = null }, force: false);
            }
        }
    }

    private (RequestState? Before, RequestState? After) Transition(string id, Func<RequestState, RequestState> apply)
    {
        if (ReadState(Target(id)) is not { } current)
        {
            return (null, null);
        }
        if (current.IsTerminal)
        {
            return (current, current);
        }
        var candidate = apply(current);
        return Persist(candidate, force: false) ? (current, candidate) : (current, null);
    }

    private void LogEntry(RequestState? before, RequestState? after, string id)
    {
        var ev = StageEntryEvent(before?.Phase, after?.Phase);
        if (ev is null || _log is null)
        {
            return;
        }
        if (ev == LogEvent.RequestAdmitted)
        {
            _log.Info(LogSubsystem.Transport, ev, ("id", id));
        }
        else
        {
            _log.Info(LogSubsystem.Transport, ev, ("id", id), ("kind", WaitingKindOf(after?.Phase) ?? "unknown"));
        }
    }

    private string Target(string id) => Path.Combine(_dir, id + ".json");

    private List<string> StateFiles()
    {
        try
        {
            return Directory.Exists(_dir) ? [.. Directory.GetFiles(_dir, "*.json")] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static RequestState? ReadState(string file)
    {
        try
        {
            return File.Exists(file) ? FromJson(File.ReadAllText(file, Encoding.UTF8)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool Persist(RequestState state, bool force)
    {
        string? temp = null;
        try
        {
            Directory.CreateDirectory(_dir);
            temp = Path.Combine(_dir, "reqstate-" + Guid.NewGuid().ToString("N") + ".tmp");
            var bytes = Encoding.UTF8.GetBytes(ToJson(state, _wallNow()));
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                fs.Write(bytes);
                if (force)
                {
                    fs.Flush(flushToDisk: true);
                }
            }
            File.Move(temp, Target(state.Id), overwrite: true);
            temp = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (temp is not null)
            {
                StorageReaper.TryDelete(temp);
            }
        }
    }
}
