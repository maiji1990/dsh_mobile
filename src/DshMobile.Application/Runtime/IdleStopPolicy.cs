namespace DshMobile.Application.Runtime;

/// <summary>后台空闲停机策略裁决结果（N-08-02）。仅当返回 <see cref="Stop"/> 时允许触发优雅停机。</summary>
public enum IdleStopDecision
{
    HostNotRunning,
    InForeground,
    HasActiveLease,
    AgentBusy,
    BelowThreshold,
    Stop,
}

/// <summary>
/// 后台空闲优雅停机策略（N-08-02）：
/// 仅在同时满足「宿主正在运行 + App 处于后台 + 无活跃工作租约 + 确认 Agent 全 Idle + 后台空闲时长达到阈值」时才放行停机。
/// </summary>
public static class IdleStopPolicy
{
    public static readonly TimeSpan DefaultIdleThreshold = TimeSpan.FromMinutes(3);

    public static IdleStopDecision Decide(
        bool hostRunning,
        bool inBackground,
        bool hasActiveLease,
        bool agentIdle,
        TimeSpan backgroundIdleDuration,
        TimeSpan? threshold = null)
    {
        var limit = threshold ?? DefaultIdleThreshold;
        if (limit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold));
        }
        if (!hostRunning)
        {
            return IdleStopDecision.HostNotRunning;
        }
        if (!inBackground)
        {
            return IdleStopDecision.InForeground;
        }
        if (hasActiveLease)
        {
            return IdleStopDecision.HasActiveLease;
        }
        if (!agentIdle)
        {
            return IdleStopDecision.AgentBusy;
        }
        // 单调时钟负差值视为 0，不允许因时钟异常误杀运行中的后端。
        if (backgroundIdleDuration < TimeSpan.Zero || backgroundIdleDuration < limit)
        {
            return IdleStopDecision.BelowThreshold;
        }
        return IdleStopDecision.Stop;
    }
}
