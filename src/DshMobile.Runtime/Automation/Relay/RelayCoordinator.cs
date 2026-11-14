// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/runtime/RelayCoordinator.kt (handle, finishAfterGate, canPark, parkForApproval,
//     approvalWaitRounds, cancelledGate, approvalTimedOutGate, logWaitingUser, logApprovalVerdict, respond, record,
//     consentTarget; ParkedWaitTable)
//   src/main/java/interlock/relay/core/interlock/InterlockGate.kt (riskOf, the ApprovalPrompt it builds)
// Registered in docs/UPSTREAMS.md. The main call chain, cheapest and side-effect-free checks first:
// admission (RelayAdmission) → surface step (RelayExecutionStage.BeforeGate) → system probe for the decided route →
// the gate with its approval prompt (RelayGate + the interlock presenter), parked out of the execution slot when the
// budget allows → quota (AfterGate) → cancel re-check → dispatch guard → BackendDispatcher → response + audit.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DshMobile.Runtime.Automation.Relay.Exec;
using DshMobile.Runtime.Automation.Relay.Interlock;
using DshMobile.Runtime.Automation.Relay.Logging;
using DshMobile.Runtime.Automation.Relay.Storage;
using DshMobile.Runtime.Automation.Relay.Transport;

namespace DshMobile.Runtime.Automation.Relay;

/// <summary>Everything the coordinator is assembled from (upstream constructor parameters).</summary>
public sealed record RelayCoordinatorParts(
    string SelfPackage,
    RelayAdmission Admission,
    RelayExecutionStage Stage,
    RelayGate Gate,
    IInterlockPresenter Presenter,
    BackendDispatcher Dispatcher,
    Func<string, SurfaceKind, ProbeResult> Probe,
    RelayPaths Paths,
    RunLog RunLog,
    AuditLog AuditLog,
    Func<int, string> CharCount)
{
    /// <summary>Who owns the node at this id (consent-screen check); null when unknown.</summary>
    public Func<int, TargetOwner?> NodeOwner { get; init; } = _ => null;

    /// <summary>The trusted display came or went: capabilities.json must be re-rendered.</summary>
    public Action OnSurfaceChanged { get; init; } = () => { };

    /// <summary>v2: pins the "may have been dispatched" marker before dispatch; false refuses the dispatch.</summary>
    public Func<string, bool>? DispatchGuard { get; init; }

    public IParkedRequestBridge? ParkBridge { get; init; }

    /// <summary>The channel's running token, null when stopped (then nothing parks).</summary>
    public Func<CancellationToken?>? ParkScope { get; init; }

    public Func<string, bool>? IsCancelRequested { get; init; }

    /// <summary>The hint for "nobody can be asked" (notifications off / running in background mode).</summary>
    public Func<string?> ApprovalDeadEnd { get; init; } = () => null;

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

public sealed class RelayCoordinator
{
    public const string WaitingKindRelay = "relay";
    public const long ApprovalReplyMarginMs = 1_500L;
    public const long ApprovalRoundBackoffMs = 500L;
    public const long DecisionHandoffGraceMs = 2_000L;
    public const long MinDispatchAfterApprovalMs = 2_000L;
    public const long ApprovalWindowCapMs = 60_000L;

    private static readonly HashSet<string> HighRisk = new(StringComparer.Ordinal)
    {
        "app.install", "sys.settings.write", "appops.set", "app.stop", "audio.capture",
    };

    private static readonly HashSet<string> MediumRisk = new(StringComparer.Ordinal)
    {
        "screen.capture", "screen.record", "screen.observe", "notify.read", "clip.read", "contact.read", "contact.write", "loc.read",
        "media.write",
    };

    private readonly RelayCoordinatorParts _p;
    private readonly ParkedWaitTable _parkedWaits;

    public RelayCoordinator(RelayCoordinatorParts parts)
    {
        _p = parts ?? throw new ArgumentNullException(nameof(parts));
        _parkedWaits = new ParkedWaitTable(Now);
    }

    /// <summary>The channel's handler (MailboxServer / ControlChannelServer).</summary>
    public RelayRequestHandler Handler => HandleAsync;

    private long Now() => _p.Time.GetTimestamp() * 1000L / _p.Time.TimestampFrequency;

    public async Task<HandlerResult> HandleAsync(RelayRequest request, long budgetMs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ct = cancellationToken;
        var startedAt = Now();
        long Elapsed() => Now() - startedAt;
        var wire = request.Capability;

        if (_p.Admission.Check(request, Elapsed()) is { } refused)
        {
            return Done(LogRejection(refused, refused.RetryPolicy is null ? null : wire));
        }
        var bringToFront = RelayAdmission.IsHostBringToFront(request, _p.SelfPackage);
        var (targetLabel, paramDetail) = InterlockTargets.Of(wire, request.Args, _p.CharCount);
        var step = _p.Stage.BeforeGate(request, Elapsed());
        if (step.Rejection is { } surfaceRefused)
        {
            return Done(LogRejection(surfaceRefused, wire));
        }
        var decision = step.Decision;
        var system = _p.Probe(wire, decision.Surface);
        var canonical = LibcoreJson.Serialize(request.Args);
        var argsIdentity = _p.AuditLog.ArgsDigestOf(canonical);
        if (_p.ParkBridge is not null && _parkedWaits.IsSameQuestionParked(ParkKey(wire, argsIdentity)))
        {
            return Done(LogRejection(Rejected(request.Id, RelayError.GateAwaitingConsent,
                "an identical request is already waiting for the user's approval; its outcome " +
                "covers this one, ask again after it settles", wire, Elapsed()), wire));
        }
        var question = new Question(request, wire, targetLabel, paramDetail, argsIdentity, InterlockTargets.IsVolatileTarget(wire, request.Args),
            ConsentTarget(request.Args), system, decision, budgetMs, startedAt, canonical);
        var gateStartedAt = Now();
        var gate = await AuthorizeAsync(question, budgetMs - ApprovalReplyMarginMs, bringToFront, ct).ConfigureAwait(false);
        if (!gate.Allowed && gate.Error == RelayError.GateAwaitingConsent && CanPark(budgetMs, gateStartedAt))
        {
            LogWaitingUser(request.Id);
            return ParkForApproval(question, gateStartedAt, gate);
        }
        if (!gate.Allowed && gate.Error == RelayError.GateAwaitingConsent)
        {
            LogWaitingUser(request.Id);
        }
        return Done(await FinishAfterGateAsync(question, gate, ct).ConfigureAwait(false));
    }

    private static HandlerResult.Done Done(RelayResponse response) => new(response);

    /// <summary>The facts of one call that both halves (inline and parked continuation) need.</summary>
    private sealed record Question(
        RelayRequest Request,
        string Wire,
        string? TargetLabel,
        string? ParamDetail,
        string ArgsIdentity,
        bool VolatileTarget,
        bool ConsentScreen,
        ProbeResult System,
        SurfaceDecision Decision,
        long BudgetMs,
        long StartedAt,
        string CanonicalArgs);

    /// <summary>One round of the gate; the prompt carries the labels, risk and deadline the broker would set.</summary>
    private Task<GateResult> AuthorizeAsync(Question q, long budgetMs, bool hostBringToFront, CancellationToken ct) =>
        _p.Gate.AuthorizeAsync(
            new GateCall(q.Wire, q.ArgsIdentity, q.System, budgetMs, q.ConsentScreen, hostBringToFront),
            (gp, token) => _p.Presenter.PresentAsync(
                new ApprovalPrompt(gp.Capability, gp.ArgsIdentity, q.VolatileTarget, q.TargetLabel, RiskOf(gp.Capability), gp.CallIndexInSession,
                    q.ParamDetail, gp.AllowsSessionGrant, Now() + Math.Min(RelayGatePolicy.DefaultApprovalWindowMs, budgetMs)),
                token),
            ct);

    public static RiskLevel RiskOf(string wire) =>
        HighRisk.Contains(wire) ? RiskLevel.High : MediumRisk.Contains(wire) ? RiskLevel.Medium : RiskLevel.Low;

    /// <summary>The shared second half: quota, cancel re-check, dispatch guard, dispatch, response.</summary>
    private async Task<RelayResponse> FinishAfterGateAsync(Question q, GateResult gate, CancellationToken ct)
    {
        var request = q.Request;
        if (!gate.Allowed)
        {
            var deniedElapsed = Now() - q.StartedAt;
            var deadEnd = gate.Error == RelayError.GateNoForeground ? _p.ApprovalDeadEnd() : null;
            var denied = _p.Stage.AfterGate(request, q.Decision, gate, deniedElapsed, deadEnd)!;
            Record(request.Id, q.Wire, gate.SystemState, gate.Tier, gate.DecisionLabel, "denied", null, null, 0L,
                gate.Error ?? RelayError.GateHostDenied, deniedElapsed, q.TargetLabel, null);
            LogApprovalVerdict(request.Id, gate);
            return denied;
        }
        LogApprovalVerdict(request.Id, gate);
        var limitedElapsed = Now() - q.StartedAt;
        if (_p.Stage.AfterGate(request, q.Decision, gate.RequiresApproval, limitedElapsed) is { } limited)
        {
            Record(request.Id, q.Wire, gate.SystemState, gate.Tier, "rate_limited", "denied", null, null, 0L, RelayError.RateLimited,
                limitedElapsed, q.TargetLabel, null);
            return limited;
        }
        if (_p.IsCancelRequested?.Invoke(request.Id) == true)
        {
            return await FinishAfterGateAsync(q, CancelledGate(gate), ct).ConfigureAwait(false);
        }
        if (_p.DispatchGuard is { } guard && !guard(request.Id))
        {
            return Respond(q, gate, q.Decision, "failed", null, [], 0L,
                "dispatch guard refused this action: the request state is no longer " +
                "executable (already settled or untracked) or the pre-execution marker " +
                "was not persisted; submit a fresh request id",
                q.Decision.Reason, RelayError.InternalError);
        }
        _p.RunLog.Info(LogSubsystem.Gate, LogEvent.RequestDispatchArmed, ("id", request.Id));
        var dispatched = await _p.Dispatcher.DispatchAsync(q.Wire, q.Decision,
            new BackendCall(request.Id, q.Wire, request.Args, q.Decision.Surface, _p.Paths,
                q.BudgetMs - (Now() - q.StartedAt) - ApprovalReplyMarginMs),
            ct).ConfigureAwait(false);
        if (q.Wire == "surface.virtual")
        {
            _p.OnSurfaceChanged();
        }
        return dispatched.Result switch
        {
            BackendResult.Ok ok => Respond(q, gate, dispatched.Decision, "success", ok.Data, ok.Artifacts, ok.ArtifactBytes, ok.Reason,
                dispatched.Decision.Reason, null),
            BackendResult.Failed failed => Respond(q, gate, dispatched.Decision, "failed", null, [], 0L, failed.Reason,
                dispatched.Decision.Reason, failed.Error, failed.ConsentKind),
            _ => throw new InvalidOperationException("unknown backend result"),
        };
    }

    // ── parking ──

    private CancellationToken? ParkScope() => _p.ParkScope?.Invoke();

    /// <summary>Worth parking only when the bridge has room, the channel runs, and the budget outlasts the approval window.</summary>
    private bool CanPark(long budgetMs, long gateStartedAt)
    {
        if (_p.ParkBridge is not { } bridge || !bridge.HasParkCapacity() || ParkScope() is null)
        {
            return false;
        }
        var afterApproval = budgetMs - (Now() - gateStartedAt) - ApprovalWindowMs(budgetMs) - ApprovalReplyMarginMs;
        return afterApproval >= MinDispatchAfterApprovalMs;
    }

    public static long ApprovalWindowMs(long budgetMs) => Math.Min(budgetMs - ApprovalReplyMarginMs, ApprovalWindowCapMs);

    private static string ParkKey(string wire, string argsIdentity) => wire + ":" + argsIdentity;

    private HandlerResult ParkForApproval(Question q, long gateStartedAt, GateResult gate)
    {
        var request = q.Request;
        var bridge = _p.ParkBridge!;
        var deadlineAtMs = gateStartedAt + ApprovalWindowMs(q.BudgetMs);
        var key = ParkKey(q.Wire, q.ArgsIdentity);
        var verdict = new TaskCompletionSource<GateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var park = new ParkedRequest(
            request.Id,
            async token =>
            {
                GateResult outcomeGate;
                if (_p.IsCancelRequested?.Invoke(request.Id) == true)
                {
                    outcomeGate = CancelledGate(gate);
                }
                else
                {
                    var grace = Task.Delay(TimeSpan.FromMilliseconds(DecisionHandoffGraceMs), _p.Time, token);
                    outcomeGate = await Task.WhenAny(verdict.Task, grace).ConfigureAwait(false) == verdict.Task
                        ? await verdict.Task.ConfigureAwait(false)
                        : ApprovalTimedOutGate(gate);
                }
                return await FinishAfterGateAsync(q, outcomeGate, token).ConfigureAwait(false);
            },
            deadlineAtMs,
            q.StartedAt + q.BudgetMs,
            q.Wire);
        var identical = "an identical request is already waiting for the user's approval; its outcome " +
            "covers this one, ask again after it settles";
        if (_parkedWaits.Register(key, deadlineAtMs) is not { } entry)
        {
            return Done(LogRejection(Rejected(request.Id, RelayError.GateAwaitingConsent, identical, q.Wire, Now() - q.StartedAt), q.Wire));
        }
        if (!bridge.Register(park))
        {
            _parkedWaits.CondRemove(key, entry);
            return Done(LogRejection(Rejected(request.Id, RelayError.GateAwaitingConsent,
                "the approval is on screen but the parked queue is full; ask again shortly", q.Wire, Now() - q.StartedAt), q.Wire));
        }
        if (ParkScope() is not { } scope)
        {
            _parkedWaits.CondRemove(key, entry);
            return new HandlerResult.Parked(park);
        }
        _ = Task.Run(() => ApprovalWaitRoundsAsync(q, gate, deadlineAtMs, verdict, key, entry, scope), CancellationToken.None);
        return new HandlerResult.Parked(park);
    }

    /// <summary>The wait loop outside the execution slot: plain gate rounds until a final verdict; it executes nothing.</summary>
    private async Task ApprovalWaitRoundsAsync(Question q, GateResult gate, long deadlineAtMs, TaskCompletionSource<GateResult> verdict,
        string key, ParkedWaitTable.Entry entry, CancellationToken scope)
    {
        var requestId = q.Request.Id;
        try
        {
            var outcome = gate;
            while (true)
            {
                if (ParkScope() is null)
                {
                    return;
                }
                var remaining = deadlineAtMs - Now();
                if (remaining <= 0L)
                {
                    outcome = ApprovalTimedOutGate(gate);
                    break;
                }
                if (_p.IsCancelRequested?.Invoke(requestId) == true)
                {
                    outcome = CancelledGate(gate);
                    break;
                }
                if (ParkScope() is null)
                {
                    return;
                }
                var round = await AuthorizeAsync(q, remaining, hostBringToFront: false, scope).ConfigureAwait(false);
                if (round.Allowed || (round.Error != RelayError.GateAwaitingConsent && round.Error != RelayError.GateWaitingTurn))
                {
                    outcome = round;
                    break;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(ApprovalRoundBackoffMs), _p.Time, scope).ConfigureAwait(false);
            }
            verdict.TrySetResult(outcome);
            _p.ParkBridge?.ResumeParked(requestId);
        }
        catch (OperationCanceledException) when (scope.IsCancellationRequested)
        {
            // The channel stopped: the park and its continuation are void; the backstop settles it.
        }
#pragma warning disable CA1031 // upstream catch (t: Throwable): a broken loop must not leave the park hanging
        catch (Exception)
#pragma warning restore CA1031
        {
            verdict.TrySetResult(ApprovalTimedOutGate(gate));
            _p.ParkBridge?.ResumeParked(requestId);
        }
        finally
        {
            // Conditional: only this loop's own registration (a same-question re-entry may already own the key).
            _parkedWaits.CondRemove(key, entry);
        }
    }

    private static GateResult CancelledGate(GateResult template) =>
        new(false, true, RelayError.GateCancelled, template.SystemState, template.Tier, "cancelled_before_execution");

    private static GateResult ApprovalTimedOutGate(GateResult template) =>
        new(false, true, RelayError.GateApprovalTimeout, template.SystemState, template.Tier,
            RelayGatePolicy.ApprovalLabel(ApprovalOutcome.TimedOut, RelayError.GateApprovalTimeout));

    // ── logging and records ──

    private void LogWaitingUser(string requestId) =>
        _p.RunLog.Info(LogSubsystem.Gate, LogEvent.RequestWaitingUser, ("id", requestId), ("kind", WaitingKindRelay));

    /// <summary>Only real verdicts: still waiting and "nobody could be asked" are not decisions.</summary>
    public static string? VerdictOf(GateResult gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (!gate.RequiresApproval)
        {
            return null;
        }
        return gate.DecisionLabel switch
        {
            "approved_once" => "allow_once",
            "approved_session" => "allow_session",
            "cancelled_before_execution" => "cancelled",
            "no_surface" or "awaiting_consent" or "waiting_turn" => null,
            var label when label.StartsWith("expired_", StringComparison.Ordinal) => "timeout",
            _ when gate.Error == RelayError.GateCancelled => "cancelled",
            _ when gate.Error == RelayError.GateApprovalTimeout => "timeout",
            _ => "deny",
        };
    }

    private void LogApprovalVerdict(string requestId, GateResult gate)
    {
        if (VerdictOf(gate) is { } verdict)
        {
            _p.RunLog.Info(LogSubsystem.Gate, LogEvent.RequestApprovalDecided, ("id", requestId), ("verdict", verdict));
        }
    }

    private RelayResponse LogRejection(RelayResponse response, string? capability)
    {
        _p.RunLog.Warn(LogSubsystem.Gate, LogEvent.CallFailed, ("id", response.Id), ("type", capability ?? response.Error?.Code ?? ""));
        return response;
    }

    private static RelayResponse Rejected(string id, RelayError error, string detail, string wire, long elapsedMs) =>
        RelayAdmission.Rejected(id, error, detail, wire, elapsedMs);

    private RelayResponse Respond(Question q, GateResult gate, SurfaceDecision decision, string outcome, System.Text.Json.Nodes.JsonObject? data,
        IReadOnlyList<string> artifacts, long bytes, string? detail, string? degradeReason, RelayError? error, string? backendConsentKind = null)
    {
        var elapsedMs = Now() - q.StartedAt;
        Record(q.Request.Id, q.Wire, gate.SystemState, gate.Tier, gate.DecisionLabel, outcome, decision.Surface, decision.DegradedFrom, bytes,
            error, elapsedMs, q.TargetLabel, q.CanonicalArgs);
        JsonElement? element = data is null ? null : JsonElement.Parse(data.ToJsonString());
        return new RelayResponse(q.Request.Id, error is null, element, artifacts, decision.Surface, decision.DegradedFrom, detail, error, elapsedMs,
            degradeReason, RelaySemantics.RetryPolicyOf(q.Wire), RelaySemantics.EffectStateFor(error is null, error),
            backendConsentKind ?? RelaySemantics.ConsentKindFor(error));
    }

    /// <summary>One record per call, with the same request id as the run log and the response envelope.</summary>
    private void Record(string requestId, string wire, SystemGrantState system, AccessTier tier, string decision, string outcome,
        SurfaceKind? surface, SurfaceKind? degradedFrom, long bytes, RelayError? error, long elapsedMs, string? target, string? canonicalArgs)
    {
        var systemName = UpperSnake(system.ToString());
        _p.AuditLog.Record(new AuditEntry(requestId, wire, target, systemName, UpperSnake(tier.ToString()), decision,
            system == SystemGrantState.Granted ? "system_granted" : systemName, "app", outcome, surface?.Wire(), degradedFrom?.Wire(), error?.Code,
            elapsedMs, bytes, canonicalArgs));
        _p.RunLog.Event(LogSubsystem.Gate, LogEvent.CallDone, ("cap", wire), ("result", outcome),
            ("ms", elapsedMs.ToString(CultureInfo.InvariantCulture)), ("bytes", bytes.ToString(CultureInfo.InvariantCulture)));
        if (_p.AuditLog.ArgsDigest(canonicalArgs) is { } digest)
        {
            _p.RunLog.Verbose(LogSubsystem.Gate, LogEvent.CallArgs, ("cap", wire), ("digest", digest));
        }
    }

    /// <summary>Kotlin enum names: <c>RuntimeAskable</c> → <c>RUNTIME_ASKABLE</c>.</summary>
    public static string UpperSnake(string pascal)
    {
        ArgumentNullException.ThrowIfNull(pascal);
        var sb = new StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascal[i]))
            {
                sb.Append('_');
            }
            sb.Append(char.ToUpperInvariant(pascal[i]));
        }
        return sb.ToString();
    }

    /// <summary>The target is a system consent screen: from the selector arguments, or the node's owner looked up by id.</summary>
    private bool ConsentTarget(JsonElement args)
    {
        if (ConsentSurfaces.LooksLikeConsentFromArgs(args))
        {
            return true;
        }
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(InterlockTargets.KeyNodeId, out _))
        {
            return false;
        }
        return _p.NodeOwner(OrgJson.OptInt(args, InterlockTargets.KeyNodeId)) is { } owner &&
            ConsentSurfaces.LooksLikeConsentNode(owner.PackageName, owner.ViewId);
    }
}

/// <summary>
/// Upstream <c>ParkedWaitTable</c>: "the same question is already parked". Registration yields to a live incumbent
/// (never overwrites); removal is conditional on the caller's own entry; expired entries heal by deadline.
/// </summary>
public sealed class ParkedWaitTable(Func<long> now)
{
    /// <summary>One registration; equality is by reference (each registration is unique).</summary>
    public sealed class Entry(long writerSeq, long deadlineAtMs)
    {
        public long WriterSeq { get; } = writerSeq;

        public long DeadlineAtMs { get; } = deadlineAtMs;
    }

    private readonly ConcurrentDictionary<string, Entry> _waits = new(StringComparer.Ordinal);
    private long _writerSeq;

    public Entry? Register(string key, long deadlineAtMs)
    {
        while (true)
        {
            var fresh = new Entry(Interlocked.Increment(ref _writerSeq), deadlineAtMs);
            if (_waits.TryAdd(key, fresh))
            {
                return fresh;
            }
            if (!_waits.TryGetValue(key, out var incumbent))
            {
                continue;
            }
            if (incumbent.DeadlineAtMs - now() > 0L)
            {
                return null;
            }
            _waits.TryRemove(new KeyValuePair<string, Entry>(key, incumbent));
        }
    }

    public bool CondRemove(string key, Entry entry) => _waits.TryRemove(new KeyValuePair<string, Entry>(key, entry));

    public bool IsSameQuestionParked(string key) => _waits.TryGetValue(key, out var entry) && entry.DeadlineAtMs - now() > 0L;
}
