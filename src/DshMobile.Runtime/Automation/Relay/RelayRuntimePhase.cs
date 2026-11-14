// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/runtime/RelayRuntimePhase.kt (RuntimePhase, RuntimeStateMachine)
// Registered in docs/UPSTREAMS.md.

namespace DshMobile.Runtime.Automation.Relay;

/// <summary>Upstream <c>RuntimePhase</c>: the channel's start-up phase as the UI sees it.</summary>
public enum RelayRuntimePhase
{
    Stopped,
    Starting,
    Running,
    Degraded,
}

/// <summary>
/// Upstream <c>RuntimeStateMachine</c>: the one source of truth for the phase and the failure reason. Every move is
/// atomic; <c>onTransition</c> runs inside the lock, so it must be light and must not re-enter the machine.
/// </summary>
public sealed class RelayRuntimeStateMachine(Action<RelayRuntimePhase>? onTransition = null)
{
    private readonly Lock _lock = new();
    private volatile RelayRuntimePhase _current = RelayRuntimePhase.Stopped;
    private volatile string? _failReason;

    public RelayRuntimePhase Phase => _current;

    /// <summary>Only DEGRADED carries a reason; a successful retry clears it.</summary>
    public string? DegradedReason => _failReason;

    /// <summary>STOPPED or DEGRADED → STARTING. False means someone else already started (or is starting).</summary>
    public bool BeginStart() => MoveTo([RelayRuntimePhase.Stopped, RelayRuntimePhase.Degraded], RelayRuntimePhase.Starting);

    public void CommitRunning() => MoveTo([RelayRuntimePhase.Starting], RelayRuntimePhase.Running);

    /// <summary>STARTING → DEGRADED with the reason; false when the phase already moved on (stop took over).</summary>
    public bool Fail(string reason)
    {
        lock (_lock)
        {
            if (_current != RelayRuntimePhase.Starting)
            {
                return false;
            }
            _failReason = reason;
            _current = RelayRuntimePhase.Degraded;
            onTransition?.Invoke(_current);
        }
        return true;
    }

    /// <summary>STARTING or RUNNING → STOPPED. DEGRADED has nothing to stop (the failure path already cleaned up).</summary>
    public bool BeginStop() => MoveTo([RelayRuntimePhase.Starting, RelayRuntimePhase.Running], RelayRuntimePhase.Stopped);

    private bool MoveTo(RelayRuntimePhase[] from, RelayRuntimePhase to)
    {
        lock (_lock)
        {
            if (Array.IndexOf(from, _current) < 0)
            {
                return false;
            }
            if (to == RelayRuntimePhase.Running)
            {
                _failReason = null;
            }
            _current = to;
            onTransition?.Invoke(to);
        }
        return true;
    }
}
