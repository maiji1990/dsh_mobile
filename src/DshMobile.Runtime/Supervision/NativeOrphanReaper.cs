using System.Diagnostics;

namespace DshMobile.Runtime.Supervision;

/// <summary>识别到的孤儿 PRoot loader 候选进程。</summary>
public sealed record OrphanProcessCandidate(int Pid, long StartTicks, string ExePath, bool ExeDeleted);

/// <summary>孤儿进程安全清理执行结果。</summary>
public sealed record OrphanReapReport(
    int CandidatesFound,
    IReadOnlyList<int> KilledPids,
    IReadOnlyList<int> SkippedPids)
{
    public static readonly OrphanReapReport Empty = new(0, [], []);
}

/// <summary>
/// 保守多条件交集孤儿进程回收器（N-08-01）：
/// 仅当 <c>uid == appUid &amp;&amp; pid &gt; 1 &amp;&amp; pid != appPid &amp;&amp; ppid == 1 &amp;&amp; tracer == 0 &amp;&amp; state != 'Z' &amp;&amp; exe matches libproot-loader*.so &amp;&amp; !inLiveTrees</c>
/// 且在真正发送信号前二次校验 <c>startTicks</c> 未发生 PID 复用时，才执行清理。任何 <c>/proc</c> 字段不可读一律保守跳过。
/// </summary>
public static class NativeOrphanReaper
{
    private const string ProotLoaderPrefix = "libproot-loader";
    private const string SharedLibrarySuffix = ".so";

    public static bool IsProotLoaderExe(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }
        var (normalized, _) = ProcessTable.NormalizeExeTarget(exePath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }
        // 同时兼容 POSIX `/` 与 Windows `\` 路径分隔符，确保跨平台离线单测行为一致。
        var lastSep = Math.Max(normalized.LastIndexOf('/'), normalized.LastIndexOf('\\'));
        var fileName = lastSep >= 0 ? normalized[(lastSep + 1)..] : normalized;
        return fileName.StartsWith(ProotLoaderPrefix, StringComparison.Ordinal)
            && fileName.EndsWith(SharedLibrarySuffix, StringComparison.Ordinal);
    }

    /// <summary>从当前进程表快照中筛选满足所有安全条件的孤儿 PRoot loader 候选列表。</summary>
    public static IReadOnlyList<OrphanProcessCandidate> FindOrphans(
        ProcessTable table,
        int appUid,
        int appPid,
        IReadOnlyCollection<int> liveRootPids)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(liveRootPids);
        if (appUid < 0 || appPid <= 1)
        {
            return [];
        }

        var liveTreePids = BuildLiveTreePids(table, appPid, liveRootPids);
        var candidates = new List<OrphanProcessCandidate>();

        foreach (var pid in table.ParentOf.Keys.Order())
        {
            if (pid <= 1 || pid == appPid || liveTreePids.Contains(pid))
            {
                continue;
            }
            var details = table.GetDetails(pid);
            if (details is null)
            {
                continue;
            }
            if (details.Uid != appUid
                || details.Ppid != 1
                || details.TracerPid != 0
                || details.State is '\0' or 'Z'
                || details.StartTicks is not { } startTicks
                || !IsProotLoaderExe(details.ExePath))
            {
                continue;
            }
            candidates.Add(new OrphanProcessCandidate(pid, startTicks, details.ExePath!, details.ExeDeleted));
        }

        return candidates;
    }

    /// <summary>
    /// 扫描 <paramref name="procRoot"/> 并安全回收孤儿进程；发信号前再次读取 <c>stat</c> 核对 <c>startTicks</c> 与 <c>ppid</c>，防止 PID 复用误杀。
    /// </summary>
    public static OrphanReapReport Reap(
        string procRoot = "/proc",
        int? appPid = null,
        IReadOnlyCollection<int>? liveRootPids = null,
        Func<int, bool>? killPid = null,
        Func<int, string?>? readExeTarget = null,
        int? appUidOverride = null)
    {
        var selfPid = appPid ?? Environment.ProcessId;
        var table = ProcessTable.Read(procRoot, readExeTarget);
        if (table is null)
        {
            return OrphanReapReport.Empty;
        }

        var appUid = appUidOverride ?? table.ReadUid(selfPid) ?? ProcessTable.ReadSelfUid(procRoot);
        if (appUid is not { } uid || uid < 0)
        {
            return OrphanReapReport.Empty;
        }

        var candidates = FindOrphans(table, uid, selfPid, liveRootPids ?? []);
        if (candidates.Count == 0)
        {
            return OrphanReapReport.Empty;
        }

        var kill = killPid ?? KillByPid;
        var killed = new List<int>();
        var skipped = new List<int>();

        foreach (var candidate in candidates)
        {
            // 二次核对 startTicks 与 ppid，避免在扫描与发信号间隙 PID 被内核复用给其他进程。
            if (!table.TryReadLiveStat(candidate.Pid, out var livePpid, out var liveState, out var liveTicks)
                || livePpid != 1
                || liveState is '\0' or 'Z'
                || liveTicks != candidate.StartTicks)
            {
                skipped.Add(candidate.Pid);
                continue;
            }

            if (kill(candidate.Pid))
            {
                killed.Add(candidate.Pid);
            }
            else
            {
                skipped.Add(candidate.Pid);
            }
        }

        return new OrphanReapReport(candidates.Count, killed, skipped);
    }

    private static HashSet<int> BuildLiveTreePids(ProcessTable table, int appPid, IReadOnlyCollection<int> liveRootPids)
    {
        var live = new HashSet<int> { appPid };
        foreach (var child in table.DescendantPids(appPid))
        {
            live.Add(child);
        }
        foreach (var root in liveRootPids)
        {
            if (root <= 1)
            {
                continue;
            }
            live.Add(root);
            foreach (var child in table.DescendantPids(root))
            {
                live.Add(child);
            }
        }
        return live;
    }

    private static bool KillByPid(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill();
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }
}
