namespace DshMobile.Runtime.Supervision;

/// <summary>
/// Android 12+ 32 幻影进程预算监控快照（N-08-03）：
/// 仅基于只读 <see cref="ProcessTable"/> 统计当前 App UID 子进程数量，绝不越权或尝试修改系统设置。
/// </summary>
public sealed record PhantomProcessReport(
    int? SelfUid,
    int ChildProcessCount,
    int Limit,
    int WarningThreshold,
    bool NearLimit,
    bool Unsupported)
{
    public const int DefaultLimit = 32;
    public const int DefaultWarningThreshold = 28;
}

/// <summary>
/// 通过 <see cref="ProcessTable"/> 评估当前 App UID 下的子进程预算使用情况（N-08-03）。
/// 当子进程数达到或超过 28 时发出预警，便于界面引导用户前往 Android 14+ 开发者选项关闭限制。
/// </summary>
public static class PhantomProcessMonitor
{
    /// <summary>从指定 proc 根目录（默认 <c>/proc</c>）采样当前进程的子进程预算状态。</summary>
    public static PhantomProcessReport Observe(
        int appPid,
        string procRoot = "/proc",
        Func<int, string?>? readExeTarget = null,
        int limit = PhantomProcessReport.DefaultLimit,
        int warningThreshold = PhantomProcessReport.DefaultWarningThreshold)
    {
        var table = ProcessTable.Read(procRoot, readExeTarget);
        var selfUid = ProcessTable.ReadSelfUid(procRoot);
        return Evaluate(table, selfUid, appPid, limit, warningThreshold);
    }

    /// <summary>对已读取的 <see cref="ProcessTable"/> 执行离线子进程计数（可注入用于单元测试）。</summary>
    public static PhantomProcessReport Evaluate(
        ProcessTable? table,
        int? appUid,
        int appPid,
        int limit = PhantomProcessReport.DefaultLimit,
        int warningThreshold = PhantomProcessReport.DefaultWarningThreshold)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (warningThreshold <= 0 || warningThreshold > limit)
        {
            throw new ArgumentOutOfRangeException(nameof(warningThreshold));
        }
        if (table is null || appUid is not { } targetUid)
        {
            return new PhantomProcessReport(appUid, 0, limit, warningThreshold, NearLimit: false, Unsupported: true);
        }

        var childCount = 0;
        foreach (var pid in table.ParentOf.Keys)
        {
            if (pid == appPid)
            {
                continue;
            }
            // 读取每个进程的真实 UID；若在采样瞬间已退出或无权限则保守跳过。
            if (table.ReadUid(pid) == targetUid)
            {
                childCount++;
            }
        }

        return new PhantomProcessReport(
            SelfUid: targetUid,
            ChildProcessCount: childCount,
            Limit: limit,
            WarningThreshold: warningThreshold,
            NearLimit: childCount >= warningThreshold,
            Unsupported: false);
    }
}
