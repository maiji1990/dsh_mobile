// Ported from DSHBox v1.5.1 (7c9d33c) sandbox-manager/.../DshHealthPolicy.kt (GPL-3.0-or-later).
// Parity: tests/TestVectors/dsh-health-policy.json (legacy through reflection: LegacyDshHealthPolicyParityTest).

namespace DshMobile.Runtime.Supervision;

/// <summary>Mutable judgement state of the DSH health loop (all times are monotonic milliseconds).</summary>
public sealed record DshWindowState(bool WasReady, int RestartAttempts, long? UnresponsiveSinceMs, long StartedAtMs, int HealthyStreak)
{
    public static DshWindowState Fresh(long nowMs) => new(false, 0, null, nowMs, 0);
}

/// <param name="WebUiReady">Port open and an HTTP response (any status).</param>
/// <param name="ProbeGapMs">Real gap since the previous probe (0 on the first one).</param>
public sealed record DshHealthProbe(long NowMs, bool WebUiReady, bool ProcessAlive, bool DeviceInteractive, long ProbeGapMs);

public enum DshHealthAction
{
    /// <summary>Probe fine: state is ready.</summary>
    Ready,
    /// <summary>Keep watching; do not touch the process.</summary>
    Observe,
    /// <summary>Bounded restart of the DSH process.</summary>
    Restart,
    /// <summary>First start never became ready in time: clean up, ERROR.</summary>
    InitialTimeout,
    /// <summary>Restart budget used up: re-check, then clean up and ERROR if still down.</summary>
    Exhausted,
}

public sealed record DshHealthDecision(DshHealthAction Action, DshWindowState Next);

/// <summary>
/// Side-effect-free health decision of the DSH loop: success streak before closing the unresponsive window, immediate
/// restart on a dead handle, the readiness timeout before first ready, re-arming on device sleep or sampling gaps.
/// </summary>
public static class DshHealthPolicy
{
    public const int HealthyStreakToClose = 3;
    public const long ProbeGapRearmMs = 15_000;
    public const long UnresponsiveHintMs = 15_000;

    public static DshHealthDecision Decide(DshWindowState state, DshHealthProbe probe, long readyTimeoutMs, long unresponsiveGraceMs, int maxRestartAttempts)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(probe);
        var current = state;
        if (!probe.DeviceInteractive || probe.ProbeGapMs > ProbeGapRearmMs)
        {
            current = current with { UnresponsiveSinceMs = null, StartedAtMs = probe.NowMs };
        }

        if (probe.WebUiReady)
        {
            var streak = current.HealthyStreak + 1;
            var closed = streak >= HealthyStreakToClose;
            return new DshHealthDecision(DshHealthAction.Ready, current with
            {
                WasReady = true,
                HealthyStreak = streak,
                UnresponsiveSinceMs = closed ? null : current.UnresponsiveSinceMs,
                RestartAttempts = closed ? 0 : current.RestartAttempts,
            });
        }

        current = current with { HealthyStreak = 0 };

        if (!probe.ProcessAlive)
        {
            return RestartOrExhausted(current, probe.NowMs, maxRestartAttempts);
        }
        if (!probe.DeviceInteractive)
        {
            return new DshHealthDecision(DshHealthAction.Observe, current);
        }
        if (!current.WasReady)
        {
            return probe.NowMs - current.StartedAtMs > readyTimeoutMs
                ? new DshHealthDecision(DshHealthAction.InitialTimeout, current)
                : new DshHealthDecision(DshHealthAction.Observe, current);
        }

        var since = current.UnresponsiveSinceMs ?? probe.NowMs;
        current = current with { UnresponsiveSinceMs = since };
        return probe.NowMs - since >= unresponsiveGraceMs
            ? RestartOrExhausted(current, probe.NowMs, maxRestartAttempts)
            : new DshHealthDecision(DshHealthAction.Observe, current);
    }

    private static DshHealthDecision RestartOrExhausted(DshWindowState state, long nowMs, int maxRestartAttempts)
    {
        var attempts = state.RestartAttempts + 1;
        return attempts < maxRestartAttempts
            ? new DshHealthDecision(DshHealthAction.Restart, state with { RestartAttempts = attempts, StartedAtMs = nowMs, UnresponsiveSinceMs = null, WasReady = false })
            : new DshHealthDecision(DshHealthAction.Exhausted, state with { RestartAttempts = attempts });
    }
}
