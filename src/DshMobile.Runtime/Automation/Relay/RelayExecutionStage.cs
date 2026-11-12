// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/runtime/RelayCoordinator.kt (handle: bring-to-front surface, policy.decide,
//     requiresMode → E_SURFACE_MODE_REQUIRED, !dispatcher.isImplemented → E_NOT_IMPLEMENTED; finishAfterGate: execRate)
// Registered in docs/UPSTREAMS.md. Locked by tests/TestVectors/relay-surface.json.

namespace DshMobile.Runtime.Automation.Relay;

/// <summary>Where a call stands after the surface step: refused, or going on to the gate with this decision.</summary>
public sealed record SurfaceStepResult(SurfaceDecision Decision, RelayResponse? Rejection);

/// <summary>
/// The two Relay steps around the approval gate that this slice ports. Before the gate (after
/// <see cref="RelayAdmission"/>): decide the surface, refuse a call whose only surface the preference cannot reach,
/// and refuse a call no backend implements — both before anyone is asked to approve a call that cannot run. After the
/// gate allowed a call: the per-minute execution quota, charged only to calls that did not go through an approval
/// prompt (those are already counted by the gate), before anything is dispatched.
/// </summary>
public sealed class RelayExecutionStage
{
    private readonly SurfacePolicy _policy;
    private readonly Func<string, bool> _isImplemented;
    private readonly RelayRateLimiter _execRate;
    private readonly string _selfPackage;

    public RelayExecutionStage(SurfacePolicy policy, Func<string, bool> isImplemented, RelayRateLimiter execRate, string selfPackage)
    {
        _policy = policy;
        _isImplemented = isImplemented;
        _execRate = execRate;
        _selfPackage = selfPackage;
    }

    /// <summary>Upstream order: bring-to-front lands on the foreground; then requiresMode; then isImplemented.</summary>
    public SurfaceStepResult BeforeGate(RelayRequest request, long elapsedMs = 0)
    {
        ArgumentNullException.ThrowIfNull(request);
        var wire = request.Capability;
        // Bringing the host back must happen on the screen the user is looking at, whatever the preference.
        var decision = RelayAdmission.IsHostBringToFront(request, _selfPackage)
            ? new SurfaceDecision(SurfaceKind.Foreground)
            : _policy.Decide(wire);
        if (decision.RequiresMode is { } mode)
        {
            return new(decision, RelayAdmission.Rejected(request.Id, RelayError.SurfaceModeRequired,
                $"capability {wire} only runs on the {mode.Wire()} execution mode; " +
                "switch the surface preference or bring that display up, then retry",
                wire, elapsedMs, decision.Surface, decision.DegradedFrom));
        }
        if (!_isImplemented(wire))
        {
            return new(decision, RelayAdmission.Rejected(request.Id, RelayError.CapabilityNotImplemented, wire, wire, elapsedMs,
                decision.Surface, decision.DegradedFrom));
        }
        return new(decision, null);
    }

    /// <summary>
    /// After the gate allowed the call: the execution quota. <paramref name="requiresApproval"/> is the gate's
    /// <c>requiresApproval</c>; such calls are not charged here. Null means dispatch.
    /// </summary>
    public RelayResponse? AfterGate(RelayRequest request, SurfaceDecision decision, bool requiresApproval, long elapsedMs = 0)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decision);
        if (requiresApproval || _execRate.Accepted(request.Capability))
        {
            return null;
        }
        return RelayAdmission.Rejected(request.Id, RelayError.RateLimited, RelayError.RateLimited.Code + "/execution_rate",
            request.Capability, elapsedMs, decision.Surface, decision.DegradedFrom);
    }
}
