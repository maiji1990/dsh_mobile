using DshMobile.Application.Runtime;

namespace DshMobile.Application.Tests;

/// <summary>ADR-0031: the eager-warmup decision, independent of Android and of the host.</summary>
public sealed class WarmupPolicyTests
{
    [Fact]
    public void StartsOnlyWhenEveryConstraintPasses()
        => Assert.Equal(WarmupDecision.Start, WarmupPolicy.Decide(enabled: true, userStopped: false, busy: false, idle: true, lowBattery: false, powerSave: false));

    [Theory]
    [InlineData(WarmupDecision.Disabled, false, false, false, true, false, false)]
    [InlineData(WarmupDecision.UserStopped, true, true, false, true, false, false)]
    [InlineData(WarmupDecision.Busy, true, false, true, true, false, false)]
    [InlineData(WarmupDecision.RuntimeNotReady, true, false, false, false, false, false)]
    [InlineData(WarmupDecision.LowBattery, true, false, false, true, true, false)]
    [InlineData(WarmupDecision.PowerSave, true, false, false, true, false, true)]
    public void EachConstraintSuppressesTheStart(WarmupDecision expected, bool enabled, bool userStopped, bool busy, bool idle, bool lowBattery, bool powerSave)
        => Assert.Equal(expected, WarmupPolicy.Decide(enabled, userStopped, busy, idle, lowBattery, powerSave));

    [Fact]
    public void DisabledWinsOverEverything()
        => Assert.Equal(WarmupDecision.Disabled, WarmupPolicy.Decide(enabled: false, userStopped: true, busy: true, idle: false, lowBattery: true, powerSave: true));

    [Fact]
    public void UserStopWinsOverBusyAndRuntimeState()
        => Assert.Equal(WarmupDecision.UserStopped, WarmupPolicy.Decide(enabled: true, userStopped: true, busy: true, idle: false, lowBattery: false, powerSave: false));
}
