// ADR-0031 (P2): when may the app start DSH in the background without the user tapping "启动 DSH"?
namespace DshMobile.Application.Runtime;

/// <summary>Why an eager warmup did or did not run. Every value except <see cref="Start"/> suppresses the start.</summary>
public enum WarmupDecision
{
    Start,
    Disabled,
    UserStopped,
    Busy,
    RuntimeNotReady,
    LowBattery,
    PowerSave,
}

/// <summary>
/// Pure decision for the eager warmup (ADR-0031): the policy lives here so the Android layer only supplies facts and
/// the unit tests cover every branch without a device.
/// </summary>
public static class WarmupPolicy
{
    /// <param name="enabled">The user's "start DSH in the background" setting.</param>
    /// <param name="userStopped">The user pressed stop in this app session (do not resurrect it behind their back).</param>
    /// <param name="busy">Another bridge operation holds the single-writer lock.</param>
    /// <param name="idle">Runtime is installed and the journal says Ready/Stopped.</param>
    /// <param name="lowBattery">Battery is low and not charging.</param>
    /// <param name="powerSave">The system is in power-save mode.</param>
    public static WarmupDecision Decide(
        bool enabled,
        bool userStopped,
        bool busy,
        bool idle,
        bool lowBattery,
        bool powerSave)
    {
        if (!enabled)
        {
            return WarmupDecision.Disabled;
        }
        if (userStopped)
        {
            return WarmupDecision.UserStopped;
        }
        if (busy)
        {
            return WarmupDecision.Busy;
        }
        if (!idle)
        {
            return WarmupDecision.RuntimeNotReady;
        }
        if (lowBattery)
        {
            return WarmupDecision.LowBattery;
        }
        if (powerSave)
        {
            return WarmupDecision.PowerSave;
        }
        return WarmupDecision.Start;
    }
}
