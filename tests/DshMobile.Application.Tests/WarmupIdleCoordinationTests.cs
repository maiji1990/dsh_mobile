using DshMobile.Application.Runtime;

namespace DshMobile.Application.Tests;

/// <summary>5C：空闲自动停机与回前台预热的闭环——只有用户手动停止才抑制预热，空闲回收不抑制。</summary>
public sealed class WarmupIdleCoordinationTests
{
    [Fact]
    public void IdleAutoStop_KeepsWarmupEligible_WhenBackToForeground()
    {
        // 后台满 3 分钟且无租约、Agent Idle：允许空闲优雅停机。
        Assert.Equal(
            IdleStopDecision.Stop,
            IdleStopPolicy.Decide(
                hostRunning: true,
                inBackground: true,
                hasActiveLease: false,
                agentIdle: true,
                backgroundIdleDuration: TimeSpan.FromMinutes(3),
                threshold: TimeSpan.FromMinutes(3)));

        // 空闲停机不置位“用户已停止”，回前台后仍可按 WarmupPolicy 自动预热。
        Assert.Equal(
            WarmupDecision.Start,
            WarmupPolicy.Decide(enabled: true, userStopped: false, busy: false, idle: true, lowBattery: false, powerSave: false));

        // 只有用户主动停止（_userStopped）才抑制预热。
        Assert.Equal(
            WarmupDecision.UserStopped,
            WarmupPolicy.Decide(enabled: true, userStopped: true, busy: false, idle: true, lowBattery: false, powerSave: false));
    }

    [Fact]
    public void IdleThreshold_RemainsThreeMinutes()
        => Assert.Equal(TimeSpan.FromMinutes(3), IdleStopPolicy.DefaultIdleThreshold);
}
