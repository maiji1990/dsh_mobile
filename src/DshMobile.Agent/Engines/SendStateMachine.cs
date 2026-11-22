using System.Collections.Concurrent;
using System.Text.Json;
using DshMobile.Agent.Domain;

namespace DshMobile.Agent.Engines;

public enum OutboundSendPhase
{
    Draft,
    Dispatching,
    Enqueued,
    Running,
    SettledSuccess,
    SettledFailure,
    UnknownOutcome,
    RequiresUserConfirmation,
}

public sealed record OutboundSendEntry(
    string ClientOpId,
    string RequestId,
    string SessionId,
    string TextPreview,
    OutboundSendPhase Phase,
    DshFailure? Error = null,
    long? MatchedTurnSeq = null);

/// <summary>
/// Outbound prompt send state machine with atomic per-op claim and duplicate-execution protection (`N-03-06`).
/// Uses Route B `requestId` server idempotency when supported; blocks direct bypasses when in
/// <see cref="OutboundSendPhase.UnknownOutcome"/> or <see cref="OutboundSendPhase.RequiresUserConfirmation"/>.
/// </summary>
public sealed class SendStateMachine
{
    public const int MaxRetainedOps = 512;

    private readonly IPromptGateway _prompts;
    private readonly ISessionGateway _sessions;
    private readonly ConcurrentDictionary<string, ManagedOp> _ops = new(StringComparer.Ordinal);
    private readonly Queue<string> _opFifo = [];
    private readonly Lock _evictLock = new();
    private long _opSeq;

    public SendStateMachine(IPromptGateway prompts, ISessionGateway sessions)
    {
        _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    public event Action<OutboundSendEntry>? EntryChanged;

    public bool TryGet(string clientOpId, out OutboundSendEntry? entry)
    {
        if (_ops.TryGetValue(clientOpId, out var op))
        {
            lock (op.Sync)
            {
                entry = op.Entry;
            }
            return true;
        }
        entry = null;
        return false;
    }

    public OutboundSendEntry CreateDraft(string sessionId, string text, string? requestId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        var seq = Interlocked.Increment(ref _opSeq);
        var opId = $"op-{seq}";
        var reqId = string.IsNullOrWhiteSpace(requestId) ? $"req-{seq}-{Guid.NewGuid():N}" : requestId;
        var preview = SecretRedactor.RedactAndTruncate(text, 80);
        var entry = new OutboundSendEntry(opId, reqId, sessionId, preview, OutboundSendPhase.Draft);
        RegisterOp(opId, entry);
        EntryChanged?.Invoke(entry);
        return entry;
    }

    public Task<OutboundSendEntry> DispatchAsync(
        string clientOpId,
        PromptSendRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var op = _ops.GetOrAdd(
            clientOpId,
            _ => CreateTrackedOp(clientOpId, new OutboundSendEntry(
                clientOpId,
                request.RequestId,
                request.SessionId,
                SecretRedactor.RedactAndTruncate(request.Text, 80),
                OutboundSendPhase.Draft)));

        Task<OutboundSendEntry> sharedTask;
        OutboundSendEntry? earlyReturn = null;
        OutboundSendEntry? publishedDispatching = null;

        lock (op.Sync)
        {
            if (op.InFlightDispatch is { } inflight && !inflight.IsCompleted)
            {
                // Atomic single-claim: concurrent callers await the exact same in-flight RPC.
                return inflight.WaitAsync(ct);
            }

            if (op.Entry.Phase is OutboundSendPhase.Enqueued or OutboundSendPhase.Running or OutboundSendPhase.SettledSuccess)
            {
                earlyReturn = op.Entry;
            }
            else if (op.Entry.Phase is OutboundSendPhase.RequiresUserConfirmation or OutboundSendPhase.UnknownOutcome)
            {
                // Never bypass explicit history verification / user confirmation via DispatchAsync.
                earlyReturn = op.Entry;
            }
            else
            {
                var dispatching = op.Entry with { Phase = OutboundSendPhase.Dispatching, Error = null };
                op.Entry = dispatching;
                publishedDispatching = dispatching;
                sharedTask = ExecuteDispatchAsync(op, request with { RequestId = dispatching.RequestId }, ct);
                op.InFlightDispatch = sharedTask;
            }
        }

        if (earlyReturn is not null)
        {
            return Task.FromResult(earlyReturn);
        }

        EntryChanged?.Invoke(publishedDispatching!);
        return op.InFlightDispatch!;
    }

    /// <summary>
    /// Recovers a send stuck in <see cref="OutboundSendPhase.UnknownOutcome"/>:
    /// safely retries same `requestId` when <paramref name="capabilities"/> guarantees server idempotency;
    /// otherwise inspects session history first and requires explicit confirmation if not found.
    /// </summary>
    public async Task<OutboundSendEntry> RecoverUnknownOutcomeAsync(
        string clientOpId,
        PromptSendRequest originalRequest,
        DshCapabilities capabilities,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(originalRequest);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (!_ops.TryGetValue(clientOpId, out var op))
        {
            throw new KeyNotFoundException($"Unknown clientOpId '{clientOpId}'.");
        }

        await op.RecoveryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            OutboundSendEntry snapshot;
            Task<OutboundSendEntry>? retryTask = null;
            OutboundSendEntry? dispatching = null;

            lock (op.Sync)
            {
                snapshot = op.Entry;
                if (snapshot.Phase != OutboundSendPhase.UnknownOutcome)
                {
                    return snapshot;
                }
                if (capabilities.ServerPromptIdempotency)
                {
                    dispatching = snapshot with { Phase = OutboundSendPhase.Dispatching, Error = null };
                    op.Entry = dispatching;
                    retryTask = ExecuteDispatchAsync(op, originalRequest with { RequestId = snapshot.RequestId }, ct);
                    op.InFlightDispatch = retryTask;
                }
            }

            if (retryTask is not null)
            {
                EntryChanged?.Invoke(dispatching!);
                return await retryTask.ConfigureAwait(false);
            }

            // Non-idempotent transport: must check durable history before ever permitting resend.
            var page = await _sessions.LoadSessionEventsAsync(snapshot.SessionId, maxMessages: 50, ct: ct).ConfigureAwait(false);
            OutboundSendEntry resolved;
            lock (op.Sync)
            {
                if (op.Entry.Phase != OutboundSendPhase.UnknownOutcome)
                {
                    return op.Entry;
                }

                if (page.IsOk && ContainsRequestId(page.Value!, snapshot.RequestId, out var matchedSeq))
                {
                    resolved = op.Entry with
                    {
                        Phase = OutboundSendPhase.Enqueued,
                        Error = null,
                        MatchedTurnSeq = matchedSeq,
                    };
                }
                else
                {
                    resolved = op.Entry with
                    {
                        Phase = OutboundSendPhase.RequiresUserConfirmation,
                        Error = page.IsOk
                            ? new DshFailure(DshFailureCode.Unavailable, "send.unconfirmed_unknown_outcome", Retryable: true)
                            : page.Error,
                    };
                }
                op.Entry = resolved;
            }

            EntryChanged?.Invoke(resolved);
            return resolved;
        }
        finally
        {
            op.RecoveryGate.Release();
        }
    }

    /// <summary>Explicit user-confirmed resend from <see cref="OutboundSendPhase.RequiresUserConfirmation"/>.</summary>
    public async Task<OutboundSendEntry> ConfirmResendAsync(
        string clientOpId,
        PromptSendRequest originalRequest,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(originalRequest);
        if (!_ops.TryGetValue(clientOpId, out var op))
        {
            throw new KeyNotFoundException($"Unknown clientOpId '{clientOpId}'.");
        }

        await op.RecoveryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Task<OutboundSendEntry> dispatchTask;
            OutboundSendEntry dispatching;
            lock (op.Sync)
            {
                if (op.Entry.Phase != OutboundSendPhase.RequiresUserConfirmation)
                {
                    return op.Entry;
                }
                dispatching = op.Entry with { Phase = OutboundSendPhase.Dispatching, Error = null };
                op.Entry = dispatching;
                dispatchTask = ExecuteDispatchAsync(op, originalRequest with { RequestId = dispatching.RequestId }, ct);
                op.InFlightDispatch = dispatchTask;
            }

            EntryChanged?.Invoke(dispatching);
            return await dispatchTask.ConfigureAwait(false);
        }
        finally
        {
            op.RecoveryGate.Release();
        }
    }

    public void MarkRunning(string requestId, long? turnSeq = null)
    {
        foreach (var kv in _ops)
        {
            OutboundSendEntry? next = null;
            lock (kv.Value.Sync)
            {
                var cur = kv.Value.Entry;
                if (cur.RequestId == requestId &&
                    cur.Phase is OutboundSendPhase.Dispatching or OutboundSendPhase.Enqueued or OutboundSendPhase.UnknownOutcome)
                {
                    next = cur with { Phase = OutboundSendPhase.Running, MatchedTurnSeq = turnSeq ?? cur.MatchedTurnSeq };
                    kv.Value.Entry = next;
                }
            }
            if (next is not null)
            {
                EntryChanged?.Invoke(next);
            }
        }
    }

    public void MarkSettled(string requestId, bool succeeded, DshFailure? failure = null)
    {
        foreach (var kv in _ops)
        {
            OutboundSendEntry? next = null;
            lock (kv.Value.Sync)
            {
                var cur = kv.Value.Entry;
                if (cur.RequestId == requestId)
                {
                    next = cur with
                    {
                        Phase = succeeded ? OutboundSendPhase.SettledSuccess : OutboundSendPhase.SettledFailure,
                        Error = failure,
                    };
                    kv.Value.Entry = next;
                }
            }
            if (next is not null)
            {
                EntryChanged?.Invoke(next);
            }
        }
    }

    private async Task<OutboundSendEntry> ExecuteDispatchAsync(
        ManagedOp op,
        PromptSendRequest request,
        CancellationToken ct)
    {
        Outcome<Unit> outcome;
        try
        {
            outcome = await _prompts.SendPromptAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            OutboundSendEntry canceledEntry;
            lock (op.Sync)
            {
                canceledEntry = op.Entry with
                {
                    Phase = OutboundSendPhase.SettledFailure,
                    Error = new DshFailure(DshFailureCode.Unavailable, "send.cancelled"),
                };
                op.Entry = canceledEntry;
                op.InFlightDispatch = null;
            }
            EntryChanged?.Invoke(canceledEntry);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var err = DshFailureMapper.FromException(ex);
            OutboundSendEntry exEntry;
            lock (op.Sync)
            {
                exEntry = op.Entry with
                {
                    Phase = err.Code is DshFailureCode.TransportLost or DshFailureCode.Timeout
                        ? OutboundSendPhase.UnknownOutcome
                        : OutboundSendPhase.SettledFailure,
                    Error = err,
                };
                op.Entry = exEntry;
                op.InFlightDispatch = null;
            }
            EntryChanged?.Invoke(exEntry);
            return exEntry;
        }

        OutboundSendEntry settled;
        lock (op.Sync)
        {
            if (outcome.IsOk)
            {
                settled = op.Entry with { Phase = OutboundSendPhase.Enqueued, Error = null };
            }
            else
            {
                var err = outcome.Error!;
                var nextPhase = err.Code is DshFailureCode.TransportLost or DshFailureCode.Timeout
                    ? OutboundSendPhase.UnknownOutcome
                    : OutboundSendPhase.SettledFailure;
                settled = op.Entry with { Phase = nextPhase, Error = err };
            }
            op.Entry = settled;
            op.InFlightDispatch = null;
        }

        EntryChanged?.Invoke(settled);
        return settled;
    }

    private void RegisterOp(string opId, OutboundSendEntry entry)
    {
        var tracked = CreateTrackedOp(opId, entry);
        _ops[opId] = tracked;
    }

    private ManagedOp CreateTrackedOp(string opId, OutboundSendEntry entry)
    {
        var op = new ManagedOp(entry);
        lock (_evictLock)
        {
            _opFifo.Enqueue(opId);
            while (_ops.Count >= MaxRetainedOps && _opFifo.TryDequeue(out var oldest))
            {
                if (oldest != opId &&
                    _ops.TryGetValue(oldest, out var candidate) &&
                    candidate.Entry.Phase is OutboundSendPhase.SettledSuccess or OutboundSendPhase.SettledFailure)
                {
                    _ops.TryRemove(oldest, out _);
                    break;
                }
            }
        }
        return op;
    }

    private static bool ContainsRequestId(SessionPageView page, string requestId, out long matchedSeq)
    {
        foreach (var ev in page.Events)
        {
            if (ev.Type != "user/message")
            {
                continue;
            }
            var data = ev.Data;
            var msg = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("message", out var nested) ? nested : data;
            if (msg.ValueKind == JsonValueKind.Object &&
                msg.TryGetProperty("source", out var src) &&
                src.ValueKind == JsonValueKind.Object &&
                src.TryGetProperty("rpcId", out var rpcId) &&
                rpcId.ValueKind == JsonValueKind.String &&
                string.Equals(rpcId.GetString(), requestId, StringComparison.Ordinal))
            {
                matchedSeq = ev.Seq;
                return true;
            }
        }
        matchedSeq = -1;
        return false;
    }

    private sealed class ManagedOp(OutboundSendEntry initial)
    {
        public object Sync { get; } = new();
        public SemaphoreSlim RecoveryGate { get; } = new(1, 1);
        public OutboundSendEntry Entry { get; set; } = initial;
        public Task<OutboundSendEntry>? InFlightDispatch { get; set; }
    }
}
