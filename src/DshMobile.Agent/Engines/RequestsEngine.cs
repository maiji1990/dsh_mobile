using DshMobile.Agent.Domain;

namespace DshMobile.Agent.Engines;

public enum PendingRequestState
{
    Waiting,
    Held,
    Sending,
    Settled,
    Cancelled,
    TransportLost,
}

public enum PendingRequestKind
{
    Approval,
    UserQuestion,
}

public sealed record PendingInteractionCard(
    string EventId,
    string ClientId,
    SessionKey Scope,
    PendingRequestKind Kind,
    PendingRequestState State,
    string? CallId,
    string? ToolName,
    bool ArgumentsReady,
    string? SanitizedArgumentsSummary,
    string? DiagnosticKey,
    ApprovalDecision? HeldDecision,
    IReadOnlyList<UserQuestionItemView>? Questions);

/// <summary>
/// Manages DSH guest-tool approvals and user questions with `callId` parameter backtracking,
/// Hold-to-undo (race-free hold generation), single-claim atomic dispatch, settled-ledger zombie protection,
/// `TransportLost` reconnect recovery, and cancellation cleanup (N-03-07).
/// Strictly separated from Relay `ui_*` device approvals (AGENTS.md rule 6).
/// </summary>
public sealed class RequestsEngine : IAsyncDisposable
{
    public static readonly TimeSpan DefaultHoldWindow = TimeSpan.FromMilliseconds(1500);
    public const int MaxSettledLedgerSize = 2048;

    private readonly IApprovalGateway _approvals;
    private readonly IQuestionGateway _questions;
    private readonly TranscriptEngine _transcripts;
    private readonly TimeProvider _time;
    private readonly TimeSpan _holdWindow;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RequestEntry> _active = new(StringComparer.Ordinal);
    private readonly HashSet<string> _settledIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _settledFifo = [];
    private readonly List<Task> _inflightTasks = [];
    private int _disposed;

    public RequestsEngine(
        IApprovalGateway approvals,
        IQuestionGateway questions,
        TranscriptEngine transcripts,
        TimeProvider? time = null,
        TimeSpan? holdWindow = null)
    {
        _approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
        _questions = questions ?? throw new ArgumentNullException(nameof(questions));
        _transcripts = transcripts ?? throw new ArgumentNullException(nameof(transcripts));
        _time = time ?? TimeProvider.System;
        _holdWindow = holdWindow ?? DefaultHoldWindow;
        _transcripts.ToolInvocationRecorded += OnToolInvocationRecorded;
    }

    private void OnToolInvocationRecorded(SessionKey scope, ToolInvocationSummary summary)
    {
        PendingInteractionCard[]? nextSnapshot = null;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || _active.Count == 0)
            {
                return;
            }
            var changed = false;
            foreach (var entry in _active.Values)
            {
                if (entry.Kind != PendingRequestKind.Approval ||
                    entry.Scope != scope ||
                    !string.Equals(entry.CallId, summary.CallId, StringComparison.Ordinal))
                {
                    continue;
                }
                if (!entry.ArgumentsReady ||
                    !string.Equals(entry.SanitizedArgumentsSummary, summary.SanitizedArguments, StringComparison.Ordinal) ||
                    entry.ToolName is null or "unknown")
                {
                    if (entry.ToolName is null or "unknown")
                    {
                        entry.ToolName = summary.ToolName;
                    }
                    entry.ArgumentsReady = true;
                    entry.SanitizedArgumentsSummary = summary.SanitizedArguments;
                    entry.DiagnosticKey = null;
                    changed = true;
                }
            }
            if (changed)
            {
                nextSnapshot = SnapshotLocked();
            }
        }
        if (nextSnapshot is not null)
        {
            RequestsChanged?.Invoke(nextSnapshot);
        }
    }

    public event Action<IReadOnlyList<PendingInteractionCard>>? RequestsChanged;

    public IReadOnlyList<PendingInteractionCard> Snapshot()
    {
        lock (_gate)
        {
            return SnapshotLocked();
        }
    }

    public void IngestForwardedEvent(SessionKey scope, string clientId, ForwardedGatewayEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        switch (evt)
        {
            case ApprovalGatewayEvent ap:
                IngestApprovalRequest(scope, clientId, ap.Request);
                break;
            case UserQuestionGatewayEvent uq:
                IngestQuestionRequest(scope, clientId, uq.Request);
                break;
        }
    }

    public void IngestApprovalRequest(SessionKey scope, string clientId, ApprovalRequestView request)
    {
        ArgumentNullException.ThrowIfNull(request);
        PendingInteractionCard[]? nextSnapshot = null;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || _settledIds.Contains(request.EventId))
            {
                return;
            }
            if (_active.TryGetValue(request.EventId, out var existing))
            {
                // Allow reconnect server replay to re-arm a `TransportLost` card cleanly, never duplicate live/sending cards.
                if (existing.State != PendingRequestState.TransportLost)
                {
                    return;
                }
                existing.HoldTimer?.Dispose();
                existing.HoldTimer = null;
            }

            var argsReady = false;
            string? argsSummary = null;
            var diagnostic = "approval.arguments_unavailable";
            if (!string.IsNullOrEmpty(request.CallId) &&
                _transcripts.TryGetToolInvocation(scope, request.CallId, out var summary))
            {
                argsReady = true;
                argsSummary = summary.SanitizedArguments;
                diagnostic = null;
            }

            var entry = new RequestEntry(
                request.EventId,
                clientId,
                scope,
                PendingRequestKind.Approval,
                request.CallId,
                request.ToolName,
                argsReady,
                argsSummary,
                diagnostic,
                null);
            _active[request.EventId] = entry;
            nextSnapshot = SnapshotLocked();
        }

        RequestsChanged?.Invoke(nextSnapshot);
    }

    public void IngestQuestionRequest(SessionKey scope, string clientId, UserQuestionRequestView request)
    {
        ArgumentNullException.ThrowIfNull(request);
        PendingInteractionCard[]? nextSnapshot = null;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || _settledIds.Contains(request.EventId))
            {
                return;
            }
            if (_active.TryGetValue(request.EventId, out var existing))
            {
                if (existing.State != PendingRequestState.TransportLost)
                {
                    return;
                }
                existing.HoldTimer?.Dispose();
                existing.HoldTimer = null;
            }

            var entry = new RequestEntry(
                request.EventId,
                clientId,
                scope,
                PendingRequestKind.UserQuestion,
                request.CallId,
                toolName: null,
                argumentsReady: true,
                sanitizedArgumentsSummary: null,
                diagnosticKey: null,
                questions: request.Questions);
            _active[request.EventId] = entry;
            nextSnapshot = SnapshotLocked();
        }

        RequestsChanged?.Invoke(nextSnapshot);
    }

    /// <summary>Starts the Hold-to-undo countdown from UI interaction (`Waiting -> Held`).</summary>
    public bool HoldApprovalDecision(string eventId, ApprovalDecision decision)
    {
        PendingInteractionCard[]? nextSnapshot = null;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 ||
                !_active.TryGetValue(eventId, out var entry) ||
                entry.Kind != PendingRequestKind.Approval ||
                entry.State != PendingRequestState.Waiting)
            {
                return false;
            }

            entry.HoldGeneration++;
            var gen = entry.HoldGeneration;
            entry.State = PendingRequestState.Held;
            entry.HeldDecision = decision;
            entry.HoldTimer?.Dispose();
            entry.HoldTimer = _time.CreateTimer(
                static s =>
                {
                    var ctx = (HoldTimerContext)s!;
                    ctx.Engine.PromoteHeldToSendByTimer(ctx.EventId, ctx.HoldGeneration);
                },
                new HoldTimerContext(this, eventId, gen),
                _holdWindow,
                Timeout.InfiniteTimeSpan);

            nextSnapshot = SnapshotLocked();
        }

        RequestsChanged?.Invoke(nextSnapshot);
        return true;
    }

    /// <summary>Cancels Hold-to-undo window cleanly; invalidates any pending timer via <c>HoldGeneration</c>.</summary>
    public bool UndoHold(string eventId)
    {
        PendingInteractionCard[]? nextSnapshot = null;
        lock (_gate)
        {
            if (!_active.TryGetValue(eventId, out var entry) ||
                entry.State != PendingRequestState.Held)
            {
                return false;
            }

            entry.HoldGeneration++;
            entry.HoldTimer?.Dispose();
            entry.HoldTimer = null;
            entry.HeldDecision = null;
            entry.State = PendingRequestState.Waiting;
            nextSnapshot = SnapshotLocked();
        }

        RequestsChanged?.Invoke(nextSnapshot);
        return true;
    }

    /// <summary>
    /// Atomic single-claim dispatch (notification quick action or in-card instant submit).
    /// Concurrent clicks return the exact same in-flight send; a <c>TransportLost</c> card can be cleanly retried.
    /// </summary>
    public Task<Outcome<Unit>> DecideApprovalImmediateAsync(
        string eventId,
        ApprovalDecision decision,
        CancellationToken ct = default)
    {
        RequestEntry entry;
        Task<Outcome<Unit>> sharedSend;
        PendingInteractionCard[]? nextSnapshot = null;

        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || _settledIds.Contains(eventId))
            {
                return Task.FromResult(Outcome<Unit>.Fail(DshFailureCode.StaleRequest, "approval.already_settled"));
            }
            if (!_active.TryGetValue(eventId, out entry!))
            {
                return Task.FromResult(Outcome<Unit>.Fail(DshFailureCode.StaleRequest, "approval.not_found"));
            }
            if (entry.InFlightSend is { } alreadySending)
            {
                return alreadySending;
            }
            if (entry.State is PendingRequestState.Settled or PendingRequestState.Cancelled)
            {
                return Task.FromResult(Outcome<Unit>.Fail(DshFailureCode.StaleRequest, "approval.terminal"));
            }

            entry.HoldGeneration++;
            entry.HoldTimer?.Dispose();
            entry.HoldTimer = null;
            entry.HeldDecision = decision;
            entry.State = PendingRequestState.Sending;
            sharedSend = DispatchApprovalInternalAsync(entry, decision, ct);
            entry.InFlightSend = sharedSend;
            TrackInflightLocked(sharedSend);
            nextSnapshot = SnapshotLocked();
        }

        RequestsChanged?.Invoke(nextSnapshot);
        return sharedSend;
    }

    public Task<Outcome<Unit>> AnswerQuestionAsync(
        string eventId,
        UserQuestionAnswerBatch answer,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(answer);
        RequestEntry entry;
        Task<Outcome<Unit>> sharedSend;
        PendingInteractionCard[]? nextSnapshot = null;

        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || _settledIds.Contains(eventId))
            {
                return Task.FromResult(Outcome<Unit>.Fail(DshFailureCode.StaleRequest, "question.already_settled"));
            }
            if (!_active.TryGetValue(eventId, out entry!) || entry.Kind != PendingRequestKind.UserQuestion)
            {
                return Task.FromResult(Outcome<Unit>.Fail(DshFailureCode.StaleRequest, "question.not_found"));
            }
            if (entry.InFlightSend is { } alreadySending)
            {
                return alreadySending;
            }
            if (entry.State is PendingRequestState.Settled or PendingRequestState.Cancelled)
            {
                return Task.FromResult(Outcome<Unit>.Fail(DshFailureCode.StaleRequest, "question.terminal"));
            }

            entry.State = PendingRequestState.Sending;
            sharedSend = DispatchQuestionInternalAsync(entry, answer, ct);
            entry.InFlightSend = sharedSend;
            TrackInflightLocked(sharedSend);
            nextSnapshot = SnapshotLocked();
        }

        RequestsChanged?.Invoke(nextSnapshot);
        return sharedSend;
    }

    /// <summary>Handles server cancellation (`user-question.cancelled` / approval withdrawn) immediately.</summary>
    public bool CancelRequest(string eventId)
    {
        PendingInteractionCard[]? nextSnapshot = null;
        lock (_gate)
        {
            RecordSettledLocked(eventId);
            if (!_active.TryGetValue(eventId, out var entry))
            {
                return false;
            }

            entry.HoldGeneration++;
            entry.HoldTimer?.Dispose();
            entry.HoldTimer = null;
            entry.State = PendingRequestState.Cancelled;
            _active.Remove(eventId);
            nextSnapshot = SnapshotLocked();
        }

        RequestsChanged?.Invoke(nextSnapshot);
        return true;
    }

    /// <summary>Immediately promotes all `Held` decisions and awaits in-flight sends (called on leave/HostStopping).</summary>
    public async Task FlushHeldAsync(CancellationToken ct = default)
    {
        List<Task> tasksToAwait = [];
        PendingInteractionCard[]? nextSnapshot = null;
        var promotedAny = false;

        lock (_gate)
        {
            foreach (var entry in _active.Values)
            {
                if (entry.State == PendingRequestState.Held &&
                    entry.HeldDecision is { } heldDecision &&
                    entry.InFlightSend is null)
                {
                    entry.HoldGeneration++;
                    entry.HoldTimer?.Dispose();
                    entry.HoldTimer = null;
                    entry.State = PendingRequestState.Sending;
                    var send = DispatchApprovalInternalAsync(entry, heldDecision, ct);
                    entry.InFlightSend = send;
                    TrackInflightLocked(send);
                    promotedAny = true;
                }
                if (entry.InFlightSend is { } inflight)
                {
                    tasksToAwait.Add(inflight);
                }
            }
            if (promotedAny)
            {
                nextSnapshot = SnapshotLocked();
            }
        }

        if (nextSnapshot is not null)
        {
            RequestsChanged?.Invoke(nextSnapshot);
        }

        if (tasksToAwait.Count > 0)
        {
            await Task.WhenAll(tasksToAwait).WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Marks non-held waiting requests as `TransportLost` without auto-approving.</summary>
    public void MarkTransportLost()
    {
        PendingInteractionCard[]? nextSnapshot = null;
        lock (_gate)
        {
            if (_active.Count == 0)
            {
                return;
            }
            foreach (var entry in _active.Values)
            {
                entry.HoldGeneration++;
                entry.HoldTimer?.Dispose();
                entry.HoldTimer = null;
                if (entry.State is PendingRequestState.Waiting or PendingRequestState.Held)
                {
                    entry.State = PendingRequestState.TransportLost;
                }
            }
            nextSnapshot = SnapshotLocked();
        }

        RequestsChanged?.Invoke(nextSnapshot);
    }

    private void PromoteHeldToSendByTimer(string eventId, long expectedHoldGeneration)
    {
        PendingInteractionCard[]? nextSnapshot = null;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 ||
                !_active.TryGetValue(eventId, out var entry) ||
                entry.State != PendingRequestState.Held ||
                entry.HoldGeneration != expectedHoldGeneration ||
                entry.HeldDecision is not { } held ||
                entry.InFlightSend is not null)
            {
                // Atomically reject stale timer callbacks if UndoHold or another click already took ownership.
                return;
            }

            entry.HoldGeneration++;
            entry.HoldTimer?.Dispose();
            entry.HoldTimer = null;
            entry.State = PendingRequestState.Sending;
            var send = DispatchApprovalInternalAsync(entry, held, CancellationToken.None);
            entry.InFlightSend = send;
            TrackInflightLocked(send);
            nextSnapshot = SnapshotLocked();
        }

        RequestsChanged?.Invoke(nextSnapshot);
    }

    private async Task<Outcome<Unit>> DispatchApprovalInternalAsync(
        RequestEntry entry,
        ApprovalDecision decision,
        CancellationToken ct)
    {
        Outcome<Unit> result;
        try
        {
            result = await _approvals.ReplyApprovalAsync(entry.ClientId, entry.EventId, decision, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TransitionAfterSend(entry.EventId, PendingRequestState.Cancelled);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TransitionAfterSend(entry.EventId, PendingRequestState.TransportLost);
            return Outcome<Unit>.Fail(DshFailureMapper.FromException(ex));
        }

        TransitionAfterSend(
            entry.EventId,
            result.IsOk ? PendingRequestState.Settled : PendingRequestState.TransportLost,
            recordSettled: result.IsOk);
        return result;
    }

    private async Task<Outcome<Unit>> DispatchQuestionInternalAsync(
        RequestEntry entry,
        UserQuestionAnswerBatch answer,
        CancellationToken ct)
    {
        Outcome<Unit> result;
        try
        {
            result = await _questions.ReplyOpenQuestionAsync(entry.ClientId, entry.EventId, answer, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TransitionAfterSend(entry.EventId, PendingRequestState.Cancelled);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TransitionAfterSend(entry.EventId, PendingRequestState.TransportLost);
            return Outcome<Unit>.Fail(DshFailureMapper.FromException(ex));
        }

        TransitionAfterSend(
            entry.EventId,
            result.IsOk ? PendingRequestState.Settled : PendingRequestState.TransportLost,
            recordSettled: result.IsOk);
        return result;
    }

    private void TransitionAfterSend(string eventId, PendingRequestState finalState, bool recordSettled = false)
    {
        PendingInteractionCard[]? nextSnapshot = null;
        lock (_gate)
        {
            if (recordSettled)
            {
                RecordSettledLocked(eventId);
            }
            if (_active.TryGetValue(eventId, out var entry))
            {
                entry.State = finalState;
                if (finalState == PendingRequestState.Settled)
                {
                    _active.Remove(eventId);
                }
                else
                {
                    entry.InFlightSend = null;
                }
                nextSnapshot = SnapshotLocked();
            }
        }

        if (nextSnapshot is not null)
        {
            RequestsChanged?.Invoke(nextSnapshot);
        }
    }

    private void RecordSettledLocked(string eventId)
    {
        if (_settledIds.Add(eventId))
        {
            _settledFifo.Enqueue(eventId);
            while (_settledIds.Count > MaxSettledLedgerSize && _settledFifo.TryDequeue(out var oldest))
            {
                _settledIds.Remove(oldest);
            }
        }
    }

    private void TrackInflightLocked(Task task)
    {
        _inflightTasks.RemoveAll(t => t.IsCompleted);
        _inflightTasks.Add(task);
    }

    private PendingInteractionCard[] SnapshotLocked() =>
        _active.Values.Select(e => new PendingInteractionCard(
            e.EventId,
            e.ClientId,
            e.Scope,
            e.Kind,
            e.State,
            e.CallId,
            e.ToolName,
            e.ArgumentsReady,
            e.SanitizedArgumentsSummary,
            e.DiagnosticKey,
            e.HeldDecision,
            e.Questions)).ToArray();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _transcripts.ToolInvocationRecorded -= OnToolInvocationRecorded;
        await FlushHeldAsync(CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        Task[] remaining;
        lock (_gate)
        {
            foreach (var entry in _active.Values)
            {
                entry.HoldGeneration++;
                entry.HoldTimer?.Dispose();
                entry.HoldTimer = null;
            }
            _active.Clear();
            remaining = [.. _inflightTasks];
            _inflightTasks.Clear();
        }
        await Task.WhenAll(remaining).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private sealed record HoldTimerContext(RequestsEngine Engine, string EventId, long HoldGeneration);

    private sealed class RequestEntry(
        string eventId,
        string clientId,
        SessionKey scope,
        PendingRequestKind kind,
        string? callId,
        string? toolName,
        bool argumentsReady,
        string? sanitizedArgumentsSummary,
        string? diagnosticKey,
        IReadOnlyList<UserQuestionItemView>? questions)
    {
        public string EventId { get; } = eventId;
        public string ClientId { get; } = clientId;
        public SessionKey Scope { get; } = scope;
        public PendingRequestKind Kind { get; } = kind;
        public string? CallId { get; } = callId;
        public string? ToolName { get; set; } = toolName;
        public bool ArgumentsReady { get; set; } = argumentsReady;
        public string? SanitizedArgumentsSummary { get; set; } = sanitizedArgumentsSummary;
        public string? DiagnosticKey { get; set; } = diagnosticKey;
        public IReadOnlyList<UserQuestionItemView>? Questions { get; } = questions;
        public PendingRequestState State { get; set; } = PendingRequestState.Waiting;
        public long HoldGeneration { get; set; }
        public ApprovalDecision? HeldDecision { get; set; }
        public ITimer? HoldTimer { get; set; }
        public Task<Outcome<Unit>>? InFlightSend { get; set; }
    }
}
