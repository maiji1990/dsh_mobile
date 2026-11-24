using DshMobile.Application.Runtime;
using Microsoft.Extensions.Time.Testing;

namespace DshMobile.Application.Tests;

/// <summary>PR-08 策略测试：WorkLeaseManager（单上限/总上限/时钟倒退）、IdleStopPolicy、ThermalHeadroomSampler 与 HeatTierPolicy。</summary>
public sealed class GovernancePolicyTests
{
    private sealed class RegressingTimeProvider : TimeProvider
    {
        private long _ticks = 10_000_000_000L;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan delta) => _ticks += delta.Ticks;

        public void Regress(TimeSpan delta) => _ticks -= delta.Ticks;
    }

    [Fact]
    public void WorkLeaseManager_EnforcesSingleCap_AndIdempotentRelease()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 24, 1, 0, 0, TimeSpan.Zero));
        var manager = new WorkLeaseManager(time, new WorkLeaseOptions(
            SingleLeaseCap: TimeSpan.FromMinutes(2),
            ContinuousTotalCap: TimeSpan.FromMinutes(10)));

        using var chat = manager.Acquire(WorkLeaseKind.Chat, "turn-1");
        Assert.True(manager.HasActiveLease);
        Assert.Equal(1, manager.ActiveCount);

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(manager.HasActiveLease);

        // 超过单租约 2 分钟上限后自动过期回收。
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(manager.HasActiveLease);
        Assert.Equal(0, manager.ActiveCount);

        // 幂等释放不抛异常也不损坏计数。
        chat.Dispose();
        chat.Dispose();
        Assert.Equal(0, manager.ActiveCount);
    }

    [Fact]
    public void WorkLeaseManager_EnforcesContinuousTotalCap_AcrossChainedLeases()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 24, 1, 0, 0, TimeSpan.Zero));
        var manager = new WorkLeaseManager(time, new WorkLeaseOptions(
            SingleLeaseCap: TimeSpan.FromMinutes(3),
            ContinuousTotalCap: TimeSpan.FromMinutes(5)));

        using var l1 = manager.Acquire(WorkLeaseKind.Setup);
        time.Advance(TimeSpan.FromMinutes(2));
        using var l2 = manager.Acquire(WorkLeaseKind.Update);
        l1.Dispose();
        time.Advance(TimeSpan.FromMinutes(2));
        using var l3 = manager.Acquire(WorkLeaseKind.Approval);
        l2.Dispose();
        Assert.True(manager.HasActiveLease);

        // 累计连续忙碌达到 5 分钟总上限，强制清零以防永久阻塞空闲停机。
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(manager.HasActiveLease);
        Assert.Empty(manager.Snapshot());
    }

    [Fact]
    public void WorkLeaseManager_MonotonicClockRegression_ClearsAllLeases()
    {
        var clock = new RegressingTimeProvider();
        var manager = new WorkLeaseManager(clock);

        using var lease = manager.Acquire(WorkLeaseKind.Reconcile);
        Assert.True(manager.HasActiveLease);

        clock.Regress(TimeSpan.FromSeconds(5));
        Assert.False(manager.HasActiveLease);
        Assert.Equal(0, manager.ActiveCount);
        Assert.Equal(1, manager.ClockRegressionResetCount);
    }

    [Theory]
    [InlineData(IdleStopDecision.HostNotRunning, false, true, false, true, 300)]
    [InlineData(IdleStopDecision.InForeground, true, false, false, true, 300)]
    [InlineData(IdleStopDecision.HasActiveLease, true, true, true, true, 300)]
    [InlineData(IdleStopDecision.AgentBusy, true, true, false, false, 300)]
    [InlineData(IdleStopDecision.BelowThreshold, true, true, false, true, 120)]
    [InlineData(IdleStopDecision.BelowThreshold, true, true, false, true, -10)]
    [InlineData(IdleStopDecision.Stop, true, true, false, true, 180)]
    public void IdleStopPolicy_RequiresBackgroundNoLeaseAgentIdleAndThreshold(
        IdleStopDecision expected,
        bool hostRunning,
        bool inBackground,
        bool hasActiveLease,
        bool agentIdle,
        int idleSeconds)
    {
        var actual = IdleStopPolicy.Decide(
            hostRunning,
            inBackground,
            hasActiveLease,
            agentIdle,
            TimeSpan.FromSeconds(idleSeconds),
            TimeSpan.FromMinutes(3));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ThermalHeadroomSampler_EnforcesAtLeast30SecondsBetweenProbes()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 24, 1, 0, 0, TimeSpan.Zero));
        var sampler = new ThermalHeadroomSampler(time);
        var calls = 0;

        float? Probe()
        {
            calls++;
            return calls == 1 ? 0.42f : 0.91f;
        }

        Assert.Equal(0.42f, sampler.Sample(Probe));
        Assert.Equal(1, calls);

        time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(0.42f, sampler.Sample(Probe));
        Assert.Equal(1, calls);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0.91f, sampler.Sample(Probe));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void HeatTierPolicy_AndWarmupPolicy_SuppressWarmupWhenModerateOrHigher()
    {
        var nominal = HeatTierPolicy.Evaluate(androidThermalStatus: 1, thermalHeadroom: 0.50f, lowBattery: false, powerSave: false);
        Assert.Equal(HeatTier.Nominal, nominal.Tier);
        Assert.True(nominal.AllowBackgroundWarmup);
        Assert.Equal(4, nominal.MaxBackgroundConcurrency);

        var moderate = HeatTierPolicy.Evaluate(androidThermalStatus: 2, thermalHeadroom: 0.60f, lowBattery: false, powerSave: false);
        Assert.Equal(HeatTier.Moderate, moderate.Tier);
        Assert.False(moderate.AllowBackgroundWarmup);
        Assert.Equal(2, moderate.MaxBackgroundConcurrency);

        var criticalFromHeadroom = HeatTierPolicy.Evaluate(androidThermalStatus: 0, thermalHeadroom: 0.99f, lowBattery: false, powerSave: false);
        Assert.Equal(HeatTier.Critical, criticalFromHeadroom.Tier);
        Assert.Equal(1, criticalFromHeadroom.MaxBackgroundConcurrency);

        Assert.Equal(
            WarmupDecision.ThermalThrottled,
            WarmupPolicy.Decide(enabled: true, userStopped: false, busy: false, idle: true, lowBattery: false, powerSave: false, HeatTier.Moderate));
        Assert.Equal(
            WarmupDecision.Start,
            WarmupPolicy.Decide(enabled: true, userStopped: false, busy: false, idle: true, lowBattery: false, powerSave: false, HeatTier.Nominal));
    }

    [Fact]
    public void HistoricalExitClassifier_DetectsMemoryLimiterAnonSwap_WithoutRetainingRawDescription()
    {
        const string sensitiveOsDescription = "MemoryLimiter:AnonSwap rss=412MB /proc/secret/path";
        var memory = HistoricalExitClassifier.Classify(reasonCode: 3, sensitiveOsDescription, DateTimeOffset.UnixEpoch);

        Assert.Equal(HistoricalExitKind.MemoryLimited, memory.Kind);
        Assert.Equal(HistoricalExitClassifier.SafeMemoryLimiterKey, memory.SafeDiagnosticKey);
        Assert.DoesNotContain("rss=", memory.SafeDiagnosticKey, StringComparison.Ordinal);

        var other = HistoricalExitClassifier.Classify(reasonCode: 10, "user requested sw", DateTimeOffset.UnixEpoch);
        Assert.Equal(HistoricalExitKind.Other, other.Kind);
        Assert.Equal("android.exit.reason_10", other.SafeDiagnosticKey);
    }
}
