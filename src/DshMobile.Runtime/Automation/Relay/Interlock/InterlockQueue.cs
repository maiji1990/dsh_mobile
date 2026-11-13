// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/interlock/InterlockQueue.kt
//   src/main/java/interlock/relay/core/interlock/ParkedAnswerMailbox.kt
//   src/main/java/interlock/relay/core/interlock/InterlockBroker.kt (ApprovalPrompt, RiskLevel, sameQuestionAs, isAnswer,
//     terminatesWhenAnswerDropped, downgradedFor, InterlockPresenter)
// Registered in docs/UPSTREAMS.md. Locked by tests/TestVectors/relay-approval.json.
//
// Security: one question is answerable in exactly one place at a time; an answer settles once and revokes every
// presenter; a click after settlement is never buffered for a retry; ALLOW_SESSION is downgraded where the ceiling
// has no session grant; nothing here ever approves on its own (no route → UNABLE_TO_SHOW, cancel → DENY).

using System.Text;

namespace DshMobile.Runtime.Automation.Relay.Interlock;

public enum RiskLevel
{
    Low,
    Medium,
    High,
}

/// <summary>
/// Upstream <c>ApprovalPrompt</c>. The Android title/summary string resources are resolved by the host UI from
/// <see cref="Capability"/>; they carry no decision weight and are not part of the question identity.
/// </summary>
public sealed record ApprovalPrompt(
    string Capability,
    string ArgsIdentity,
    bool VolatileTarget,
    string? TargetLabel,
    RiskLevel Risk,
    int CallIndexInSession,
    string? ParamDetail,
    bool AllowsSessionGrant,
    long DeadlineAtMs)
{
    /// <summary>Upstream <c>sameQuestionAs</c>: capability, args identity and whether a session grant is on offer.</summary>
    public bool SameQuestionAs(ApprovalPrompt other) =>
        other is not null && Capability == other.Capability && ArgsIdentity == other.ArgsIdentity && AllowsSessionGrant == other.AllowsSessionGrant;

    /// <summary>Upstream <c>terminatesWhenAnswerDropped</c>: a dropped late answer ends the question (no session tier to fall back to).</summary>
    public bool TerminatesWhenAnswerDropped => VolatileTarget && !AllowsSessionGrant;
}

public static class InterlockChoices
{
    public static bool IsAnswer(this InterlockChoice c) => c is InterlockChoice.AllowOnce or InterlockChoice.AllowSession or InterlockChoice.Deny;

    /// <summary>ALLOW_SESSION becomes ALLOW_ONCE where the prompt offers no session grant.</summary>
    public static InterlockChoice DowngradedFor(this InterlockChoice c, ApprovalPrompt prompt) =>
        c == InterlockChoice.AllowSession && !prompt.AllowsSessionGrant ? InterlockChoice.AllowOnce : c;

    /// <summary>Upstream enum names (wire / log form).</summary>
    public static string Wire(this InterlockChoice c) => c switch
    {
        InterlockChoice.AllowOnce => "ALLOW_ONCE",
        InterlockChoice.AllowSession => "ALLOW_SESSION",
        InterlockChoice.Deny => "DENY",
        InterlockChoice.UnableToShow => "UNABLE_TO_SHOW",
        InterlockChoice.Expired => "EXPIRED",
        InterlockChoice.Awaiting => "AWAITING",
        _ => "WAITING_TURN",
    };
}

/// <summary>Upstream <c>InterlockPresenter</c>.</summary>
public interface IInterlockPresenter
{
    Task<InterlockChoice> PresentAsync(ApprovalPrompt prompt, CancellationToken cancellationToken);
}

/// <summary>Upstream <c>OverlayApprovalChannel</c>: the floating card route (DSH Mobile does not wire one today).</summary>
public interface IOverlayApprovalChannel
{
    bool CanShow();

    Task<InterlockChoice> PresentAsync(ApprovalPrompt prompt, CancellationToken cancellationToken);

    bool IsPresented();

    void Withdraw();

    Action<ApprovalPrompt, InterlockChoice>? OnParkedChoice { get; set; }

    Action? OnHidden { get; set; }
}

/// <summary>Upstream <c>InterlockChannel</c>: the one active presenter.</summary>
public enum InterlockChannel
{
    Page,
    Overlay,
    Notification,
    None,
}

/// <summary>Upstream <c>ApprovalDeadEnd</c>.</summary>
public enum ApprovalDeadEnd
{
    NotificationsBlocked,
    BackgroundOverlaySkipped,
    QuestionCardOnScreen,
}

/// <summary>Upstream <c>LateVerdict</c>.</summary>
public abstract record LateVerdict
{
    public sealed record Answered(InterlockChoice Choice) : LateVerdict;

    public sealed record Discarded : LateVerdict;
}

/// <summary>Upstream <c>ParkedAnswerMailbox</c>: one slot; take-once; expires on the monotonic clock.</summary>
public sealed class ParkedAnswerMailbox(Func<long> now)
{
    private sealed record Entry(ApprovalPrompt Prompt, LateVerdict Verdict, long ExpiresAtMs);

    private Entry? _entry;

    public void Put(ApprovalPrompt prompt, InterlockChoice choice, long expiresAtMs) =>
        Volatile.Write(ref _entry, new Entry(prompt, new LateVerdict.Answered(choice), expiresAtMs));

    public void PutDiscarded(ApprovalPrompt prompt, long expiresAtMs) =>
        Volatile.Write(ref _entry, new Entry(prompt, new LateVerdict.Discarded(), expiresAtMs));

    public LateVerdict? Take(ApprovalPrompt prompt)
    {
        var current = Volatile.Read(ref _entry);
        if (current is null)
        {
            return null;
        }
        if (current.ExpiresAtMs - now() <= 0)
        {
            Interlocked.CompareExchange(ref _entry, null, current);
            return null;
        }
        if (!current.Prompt.SameQuestionAs(prompt))
        {
            return null;
        }
        return ReferenceEquals(Interlocked.CompareExchange(ref _entry, null, current), current) ? current.Verdict : null;
    }

    public void Clear() => Volatile.Write(ref _entry, null);
}

/// <summary>Upstream <c>InterlockQueue</c>: the approval queue and the single-presenter arbitration.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "Upstream type name, kept for traceability.")]
public sealed class InterlockQueue : IInterlockPresenter
{
    /// <summary>One displayed + two waiting.</summary>
    public const int Capacity = 3;
    public const int AnyRequest = -1;
    public const long InlineApprovalWaitMs = 8_000;
    private const int NoneRequestId = 0;
    private const int SettledMemory = 4;

    /// <summary>One pending question; every route presents the same instance and it settles once.</summary>
    public sealed class Request
    {
        private readonly TaskCompletionSource<InterlockChoice> _deferred = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _awaiting;

        internal Request(int id, ApprovalPrompt prompt)
        {
            Id = id;
            Prompt = prompt;
        }

        public int Id { get; }

        public ApprovalPrompt Prompt { get; }

        public bool Awaiting
        {
            get => _awaiting;
            internal set => _awaiting = value;
        }

        internal Task<InterlockChoice> Answer => _deferred.Task;

        internal void Resolve(InterlockChoice choice) => _deferred.TrySetResult(choice);

        internal InterlockChoice? PeekAnswered() => _deferred.Task.IsCompletedSuccessfully ? _deferred.Task.Result : null;

        public bool Matches(ApprovalPrompt other) => Prompt.SameQuestionAs(other);
    }

    public sealed record PendingSnapshot(Request? Displayed, int WaitingCount);

    public sealed record Presentation(Request? Request, InterlockChannel Via, int WaitingCount, IReadOnlySet<ApprovalDeadEnd> DeadEnds);

    private readonly record struct RoutingKey(int RequestId, InterlockChannel Via, int Waiting);

    private abstract record Slot
    {
        public sealed record Displayed(Request Request, InterlockChannel Via) : Slot;

        public sealed record Queued(Request Request) : Slot;

        public sealed record Full : Slot;
    }

    private readonly Func<bool> _isForegroundCapable;
    private readonly Action<Presentation> _onRouting;
    private readonly IOverlayApprovalChannel? _overlay;
    private readonly Func<bool> _overlayBlind;
    private readonly Func<bool> _canNotify;
    private readonly Func<bool> _otherCardOnScreen;
    private readonly Action<Request> _onRevokePresenters;
    private readonly Func<long> _now;
    private readonly long _inlineWaitMs;
    private readonly Lock _lock = new();
    private readonly LinkedList<Request> _waiting = new();
    private readonly LinkedList<ApprovalPrompt> _settled = new();
    private readonly ParkedAnswerMailbox _lateAnswers;
    private int _ids;
    private Request? _pending;
    private RoutingKey? _lastRoutingKey;
    private int? _overlayExhaustedFor;
    private string? _deadEndReason;

    public InterlockQueue(
        Func<bool> isForegroundCapable,
        Action<Presentation>? onRouting = null,
        IOverlayApprovalChannel? overlay = null,
        Func<bool>? overlayBlind = null,
        Func<bool>? canNotify = null,
        Func<bool>? otherCardOnScreen = null,
        Action<Request>? onRevokePresenters = null,
        Func<long>? now = null,
        long inlineWaitMs = InlineApprovalWaitMs,
        int? idSeed = null)
    {
        _isForegroundCapable = isForegroundCapable;
        _onRouting = onRouting ?? (_ => { });
        _overlay = overlay;
        _overlayBlind = overlayBlind ?? (() => false);
        _canNotify = canNotify ?? (() => false);
        _otherCardOnScreen = otherCardOnScreen ?? (() => false);
        _onRevokePresenters = onRevokePresenters ?? (_ => { });
        _now = now ?? Logging.RelayClock.MonotonicNow;
        _inlineWaitMs = inlineWaitMs;
        // Positive random base per process: a notification from the previous process must not answer a new question.
        _ids = idSeed ?? Random.Shared.Next(1, int.MaxValue / 4);
        _lateAnswers = new ParkedAnswerMailbox(_now);
        if (_overlay is not null)
        {
            _overlay.OnParkedChoice = RecordLateAnswer;
            _overlay.OnHidden = OnOverlayHidden;
        }
    }

    private void OnOverlayHidden()
    {
        lock (_lock)
        {
            if (_pending is not { } request)
            {
                return;
            }
            _overlayExhaustedFor = request.Id;
            if (_overlay?.IsPresented() == true)
            {
                _overlay.Withdraw();
            }
            EmitRoutingLocked(force: false);
        }
    }

    /// <summary>Re-arbitrate after a visibility change; idempotent.</summary>
    public void Reroute()
    {
        lock (_lock)
        {
            EmitRoutingLocked(force: false);
        }
    }

    /// <summary>The notification route's own timeout: close the request and revoke every presenter.</summary>
    public bool ExpireIfCurrent(int requestId)
    {
        lock (_lock)
        {
            if (_pending is not { } request || request.Id != requestId)
            {
                return false;
            }
            RememberSettledLocked(request.Prompt);
            _onRevokePresenters(request);
            if (request.Awaiting)
            {
                request.Resolve(InterlockChoice.Expired);
            }
            CloseSlotLocked(request);
            return true;
        }
    }

    public string? LastDeadEndReason()
    {
        lock (_lock)
        {
            return _deadEndReason;
        }
    }

    /// <summary>A click nobody was waiting for (parked overlay, late page tap): stored for the next identical retry.</summary>
    public void RecordLateAnswer(ApprovalPrompt prompt, InterlockChoice choice)
    {
        lock (_lock)
        {
            if (!choice.IsAnswer())
            {
                return;
            }
            if (_pending is { } live && live.Matches(prompt))
            {
                if (live.Awaiting)
                {
                    live.Resolve(choice.DowngradedFor(live.Prompt));
                    RememberSettledLocked(live.Prompt);
                    _onRevokePresenters(live);
                    CloseSlotLocked(live);
                }
                else
                {
                    RecordLate(live.Prompt, choice);
                }
                return;
            }
            RecordLate(prompt, choice);
        }
    }

    private Slot Acquire(ApprovalPrompt prompt)
    {
        lock (_lock)
        {
            var displayedBefore = _pending;
            PruneExpiredLocked();
            void EmitIfDisplayMoved()
            {
                if (!ReferenceEquals(_pending, displayedBefore))
                {
                    EmitRoutingLocked(force: true);
                }
            }
            if (_pending is { } current && current.Matches(prompt))
            {
                current.Awaiting = true;
                return new Slot.Displayed(current, CurrentRouteLocked());
            }
            if (_waiting.FirstOrDefault(r => r.Matches(prompt)) is { } queued)
            {
                queued.Awaiting = true;
                EmitIfDisplayMoved();
                return new Slot.Queued(queued);
            }
            var created = new Request(Interlocked.Increment(ref _ids), prompt) { Awaiting = true };
            Slot slot;
            if (_pending is null)
            {
                _pending = created;
                _overlayExhaustedFor = null;
                slot = new Slot.Displayed(created, CurrentRouteLocked());
            }
            else if (_waiting.Count < Capacity - 1)
            {
                _waiting.AddLast(created);
                slot = new Slot.Queued(created);
            }
            else
            {
                EmitIfDisplayMoved();
                return new Slot.Full();
            }
            EmitIfDisplayMoved();
            return slot;
        }
    }

    /// <summary>Upstream <c>currentRouteLocked</c>: page, then overlay, then notification, else none.</summary>
    private InterlockChannel CurrentRouteLocked()
    {
        if (_pending is not { } request)
        {
            return InterlockChannel.None;
        }
        if (_isForegroundCapable())
        {
            return InterlockChannel.Page;
        }
        var blind = _overlayBlind();
        var up = !blind && _overlay is not null && _overlay.IsPresented();
        var usable = !blind && _overlay is not null && _overlay.CanShow() && _overlayExhaustedFor != request.Id && !_otherCardOnScreen();
        if (up || usable)
        {
            return InterlockChannel.Overlay;
        }
        return _canNotify() ? InterlockChannel.Notification : InterlockChannel.None;
    }

    private HashSet<ApprovalDeadEnd> DeadEndsLocked()
    {
        var set = new HashSet<ApprovalDeadEnd>();
        if (!_canNotify())
        {
            set.Add(ApprovalDeadEnd.NotificationsBlocked);
        }
        if (_overlayBlind())
        {
            set.Add(ApprovalDeadEnd.BackgroundOverlaySkipped);
        }
        if (_otherCardOnScreen() && CurrentRouteLocked() == InterlockChannel.None)
        {
            set.Add(ApprovalDeadEnd.QuestionCardOnScreen);
        }
        return set;
    }

    private void EmitRoutingLocked(bool force)
    {
        var request = _pending;
        var via = CurrentRouteLocked();
        var key = new RoutingKey(request?.Id ?? NoneRequestId, via, _waiting.Count);
        if (!force && key == _lastRoutingKey)
        {
            return;
        }
        var wasOverlay = _lastRoutingKey?.Via == InterlockChannel.Overlay;
        _lastRoutingKey = key;
        _deadEndReason = request is not null && via == InterlockChannel.None ? ReasonFor(DeadEndsLocked()) : null;
        if (wasOverlay && via != InterlockChannel.Overlay)
        {
            _overlay?.Withdraw();
        }
        _onRouting(new Presentation(request, via, _waiting.Count,
            via == InterlockChannel.None ? DeadEndsLocked() : new HashSet<ApprovalDeadEnd>()));
    }

    private void PruneExpiredLocked()
    {
        if (_pending is { } p && p.Prompt.DeadlineAtMs - _now() <= 0)
        {
            RememberSettledLocked(p.Prompt);
            _onRevokePresenters(p);
            if (p.Awaiting)
            {
                p.Resolve(InterlockChoice.Expired);
            }
            _pending = null;
        }
        if (_waiting.Count > 0)
        {
            var expired = _waiting.Where(r => r.Prompt.DeadlineAtMs - _now() <= 0).ToList();
            foreach (var r in expired)
            {
                _waiting.Remove(r);
            }
            foreach (var r in expired.Where(r => r.Awaiting))
            {
                r.Resolve(InterlockChoice.Expired);
            }
        }
        _pending ??= TakeNextWaitingLocked();
    }

    private Request? TakeNextWaitingLocked()
    {
        while (_waiting.First is { } node)
        {
            _waiting.RemoveFirst();
            var next = node.Value;
            if (next.Prompt.DeadlineAtMs - _now() > 0)
            {
                return next;
            }
            if (next.Awaiting)
            {
                next.Resolve(InterlockChoice.Expired);
            }
        }
        return null;
    }

    private void RememberSettledLocked(ApprovalPrompt prompt)
    {
        _settled.AddLast(prompt);
        while (_settled.Count > SettledMemory)
        {
            _settled.RemoveFirst();
        }
    }

    private bool WasSettledLocked(ApprovalPrompt prompt) => _settled.Any(p => p.SameQuestionAs(prompt));

    private void Close(Request request)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_pending, request))
            {
                return;
            }
            RememberSettledLocked(request.Prompt);
            _onRevokePresenters(request);
            CloseSlotLocked(request);
        }
    }

    private void CloseSlotLocked(Request request)
    {
        if (!ReferenceEquals(_pending, request))
        {
            return;
        }
        _pending = TakeNextWaitingLocked();
        EmitRoutingLocked(force: true);
    }

    public async Task<InterlockChoice> PresentAsync(ApprovalPrompt prompt, CancellationToken cancellationToken)
    {
        switch (_lateAnswers.Take(prompt))
        {
            case LateVerdict.Discarded:
                return InterlockChoice.Expired;
            case LateVerdict.Answered answered:
                return answered.Choice;
        }
        if (NoRouteAtAll() is { } deadEnds)
        {
            lock (_lock)
            {
                _deadEndReason = ReasonFor(deadEnds);
            }
            return InterlockChoice.UnableToShow;
        }
        switch (Acquire(prompt))
        {
            case Slot.Full:
                return InterlockChoice.WaitingTurn;
            case Slot.Queued queued:
                return await WaitForAnswerAsync(queued.Request, cancellationToken);
            case Slot.Displayed displayed:
                var request = displayed.Request;
                if (displayed.Via == InterlockChannel.Overlay)
                {
                    InterlockChoice? choice = _overlay is null ? null : await _overlay.PresentAsync(prompt, cancellationToken);
                    switch (choice)
                    {
                        case null or InterlockChoice.UnableToShow:
                            lock (_lock)
                            {
                                _overlayExhaustedFor = request.Id;
                                EmitRoutingLocked(force: true);
                            }
                            break;
                        case InterlockChoice.Awaiting:
                            lock (_lock)
                            {
                                request.Awaiting = false;
                            }
                            return InterlockChoice.Awaiting;
                        default:
                            Close(request);
                            return choice.Value;
                    }
                }
                if (!_isForegroundCapable() && !_canNotify())
                {
                    lock (_lock)
                    {
                        request.Awaiting = false;
                        RememberSettledLocked(request.Prompt);
                        _deadEndReason = ReasonFor(DeadEndsLocked());
                    }
                    Close(request);
                    return InterlockChoice.UnableToShow;
                }
                return await WaitForAnswerAsync(request, cancellationToken);
            default:
                throw new InvalidOperationException("unknown slot");
        }
    }

    private HashSet<ApprovalDeadEnd>? NoRouteAtAll()
    {
        var notify = _canNotify();
        var overlayOk = !_overlayBlind() && _overlay?.CanShow() == true && !_otherCardOnScreen();
        if (_isForegroundCapable() || overlayOk || notify)
        {
            return null;
        }
        var set = new HashSet<ApprovalDeadEnd>();
        if (!notify)
        {
            set.Add(ApprovalDeadEnd.NotificationsBlocked);
        }
        if (_overlayBlind())
        {
            set.Add(ApprovalDeadEnd.BackgroundOverlaySkipped);
        }
        if (_otherCardOnScreen())
        {
            set.Add(ApprovalDeadEnd.QuestionCardOnScreen);
        }
        return set;
    }

    /// <summary>Wait at most the inline window (narrowed to the remaining deadline); no answer → AWAITING, the request stays.</summary>
    private async Task<InterlockChoice> WaitForAnswerAsync(Request request, CancellationToken cancellationToken)
    {
        var budget = Math.Max(0L, Math.Min(_inlineWaitMs, request.Prompt.DeadlineAtMs - _now()));
        InterlockChoice? choice;
        try
        {
            choice = await request.Answer.WaitAsync(TimeSpan.FromMilliseconds(budget), cancellationToken);
        }
        catch (TimeoutException)
        {
            choice = null;
        }
        finally
        {
            lock (_lock)
            {
                request.Awaiting = false;
            }
        }
        if (choice is null)
        {
            if (request.PeekAnswered() is { } answered)
            {
                Close(request);
                return answered;
            }
            return InterlockChoice.Awaiting;
        }
        Close(request);
        return choice.Value;
    }

    /// <summary>Answer the displayed request (only that one; <paramref name="requestId"/> pins which). Returns whether it answered one.</summary>
    public bool ResolveCurrent(InterlockChoice choice, int requestId = AnyRequest)
    {
        lock (_lock)
        {
            if (_pending is not { } request || (requestId != AnyRequest && request.Id != requestId))
            {
                return false;
            }
            var effective = choice.DowngradedFor(request.Prompt);
            RememberSettledLocked(request.Prompt);
            if (request.Awaiting)
            {
                request.Resolve(effective);
            }
            else
            {
                RecordLate(request.Prompt, effective);
            }
            _onRevokePresenters(request);
            CloseSlotLocked(request);
            return true;
        }
    }

    private void RecordLate(ApprovalPrompt prompt, InterlockChoice choice)
    {
        if (!choice.IsAnswer() || WasSettledLocked(prompt))
        {
            return;
        }
        if (prompt.VolatileTarget)
        {
            if (prompt.TerminatesWhenAnswerDropped)
            {
                _lateAnswers.PutDiscarded(prompt, prompt.DeadlineAtMs);
            }
            return;
        }
        _lateAnswers.Put(prompt, choice.DowngradedFor(prompt), prompt.DeadlineAtMs);
    }

    public PendingSnapshot Snapshot()
    {
        lock (_lock)
        {
            PruneExpiredLocked();
            EmitRoutingLocked(force: false);
            return new PendingSnapshot(_pending, _waiting.Count);
        }
    }

    /// <summary>Clear everything (UI destroyed / user left): waiters get DENY, never an approval.</summary>
    public void CancelAll()
    {
        Request? current;
        List<Request> queued;
        lock (_lock)
        {
            _lateAnswers.Clear();
            current = _pending;
            _pending = null;
            queued = [.. _waiting];
            _waiting.Clear();
            if (current is not null || queued.Count > 0)
            {
                foreach (var r in current is null ? queued : [.. queued, current])
                {
                    RememberSettledLocked(r.Prompt);
                }
                foreach (var r in queued)
                {
                    _onRevokePresenters(r);
                }
                if (current is not null)
                {
                    _onRevokePresenters(current);
                }
                EmitRoutingLocked(force: true);
            }
        }
        current?.Resolve(InterlockChoice.Deny);
        foreach (var r in queued.Where(r => r.Awaiting))
        {
            r.Resolve(InterlockChoice.Deny);
        }
    }

    /// <summary>Upstream <c>reasonFor</c>: name the step the user can take.</summary>
    public static string ReasonFor(IReadOnlySet<ApprovalDeadEnd> deadEnds)
    {
        ArgumentNullException.ThrowIfNull(deadEnds);
        var sb = new StringBuilder("no_surface");
        if (deadEnds.Contains(ApprovalDeadEnd.NotificationsBlocked))
        {
            sb.Append("; turn on notifications for this app in system settings");
        }
        if (deadEnds.Contains(ApprovalDeadEnd.BackgroundOverlaySkipped))
        {
            sb.Append("; end background mode or open the assistant page to answer");
        }
        if (deadEnds.Contains(ApprovalDeadEnd.QuestionCardOnScreen))
        {
            sb.Append("; a question card is on screen - answer it first and this can be asked again");
        }
        if (deadEnds.Count == 0)
        {
            sb.Append("; no presenter is available right now");
        }
        return sb.ToString();
    }
}
