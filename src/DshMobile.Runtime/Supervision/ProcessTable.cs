// Ported from DSHBox v1.5.1 (7c9d33c) sandbox-manager/.../SandboxProcessRunner.kt readProcTable / childrenOf /
// findDescendantByCmdline / descendantPids / killAll ordering (GPL-3.0-or-later).

using System.Globalization;

namespace DshMobile.Runtime.Supervision;

/// <summary>
/// Parsed <c>/proc/&lt;pid&gt;</c> snapshot fields used by <see cref="NativeOrphanReaper"/> and supervision checks (N-08-01).
/// Any unreadable field remains null so orphan detection stays strictly conservative.
/// </summary>
public sealed record ProcessDetails(
    int Pid,
    int Ppid,
    char State,
    long? StartTicks,
    int? Uid,
    int? TracerPid,
    string? ExePath,
    bool ExeDeleted);

/// <summary>
/// A snapshot of <c>/proc</c> (pid → ppid/stat, plus lazily read status/exe/cmdline), with lookups used to locate,
/// stop, and safely reap PRoot trees. The proc root is injectable so the logic is testable off-device. Reading is
/// side-effect free; nothing here kills anything.
/// </summary>
public sealed class ProcessTable
{
    private readonly string _procRoot;
    private readonly Dictionary<int, int> _parent;
    private readonly Dictionary<int, StatRecord> _stats;
    private readonly Func<int, string?>? _readExeTarget;

    private readonly record struct StatRecord(int Ppid, char State, long? StartTicks);

    private ProcessTable(
        string procRoot,
        Dictionary<int, int> parent,
        Dictionary<int, StatRecord> stats,
        Func<int, string?>? readExeTarget)
    {
        _procRoot = procRoot;
        _parent = parent;
        _stats = stats;
        _readExeTarget = readExeTarget;
    }

    public IReadOnlyDictionary<int, int> ParentOf => _parent;

    /// <summary>
    /// Reads every numeric entry in <paramref name="procRoot"/> whose <c>stat</c> is readable. Uses
    /// <c>LastIndexOf(')')</c> so process names containing parentheses or <c>") "</c> parse reliably (N-08-01).
    /// </summary>
    public static ProcessTable? Read(string procRoot = "/proc", Func<int, string?>? readExeTarget = null)
    {
        if (!Directory.Exists(procRoot))
        {
            return null;
        }
        var map = new Dictionary<int, int>();
        var stats = new Dictionary<int, StatRecord>();
        foreach (var dir in Directory.EnumerateDirectories(procRoot))
        {
            var name = Path.GetFileName(dir);
            if (name.Length == 0 || !name.All(char.IsAsciiDigit) || !int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                continue;
            }
            string stat;
            try
            {
                stat = File.ReadAllText(Path.Combine(dir, "stat"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (TryParseStat(stat, out var parsed))
            {
                map[pid] = parsed.Ppid;
                stats[pid] = parsed;
            }
        }
        return new ProcessTable(procRoot, map, stats, readExeTarget);
    }

    /// <summary>Parses a single <c>/proc/&lt;pid&gt;/stat</c> line using <c>LastIndexOf(')')</c> (N-08-01).</summary>
    internal static bool TryParseStat(string stat, out int ppid, out char state, out long? startTicks)
    {
        var ok = TryParseStat(stat, out var parsed);
        ppid = parsed.Ppid;
        state = parsed.State;
        startTicks = parsed.StartTicks;
        return ok;
    }

    private static bool TryParseStat(string stat, out StatRecord record)
    {
        record = default;
        if (string.IsNullOrEmpty(stat))
        {
            return false;
        }
        var idx = stat.LastIndexOf(')');
        if (idx < 0 || idx + 1 >= stat.Length)
        {
            return false;
        }
        var rest = stat[(idx + 1)..].TrimStart();
        var fields = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length <= 1 || !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ppid))
        {
            return false;
        }
        var state = fields[0].Length > 0 ? fields[0][0] : '\0';
        long? startTicks = fields.Length > 19 && long.TryParse(fields[19], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
            ? ticks
            : null;
        record = new StatRecord(ppid, state, startTicks);
        return true;
    }

    /// <summary>Reads the real UID from <c>/proc/self/status</c> (or <paramref name="procRoot"/>/self/status); null if unavailable.</summary>
    public static int? ReadSelfUid(string procRoot = "/proc")
    {
        var statusPath = Path.Combine(procRoot, "self", "status");
        return ReadStatusFields(statusPath).Uid;
    }

    /// <summary>Returns full supervision metadata for <paramref name="pid"/>, or null if <paramref name="pid"/> is not in the table.</summary>
    public ProcessDetails? GetDetails(int pid)
    {
        if (!_stats.TryGetValue(pid, out var stat))
        {
            return null;
        }
        var statusPath = Path.Combine(_procRoot, pid.ToString(CultureInfo.InvariantCulture), "status");
        var (uid, tracerPid) = ReadStatusFields(statusPath);
        var (exePath, exeDeleted) = ReadExe(pid);
        return new ProcessDetails(pid, stat.Ppid, stat.State, stat.StartTicks, uid, tracerPid, exePath, exeDeleted);
    }

    public int? ReadUid(int pid)
    {
        var statusPath = Path.Combine(_procRoot, pid.ToString(CultureInfo.InvariantCulture), "status");
        return ReadStatusFields(statusPath).Uid;
    }

    /// <summary>Reads <c>/proc/&lt;pid&gt;/stat</c> directly to re-verify <c>startTicks</c> and <c>ppid</c> before signaling.</summary>
    internal bool TryReadLiveStat(int pid, out int ppid, out char state, out long? startTicks)
    {
        ppid = 0;
        state = '\0';
        startTicks = null;
        try
        {
            var stat = File.ReadAllText(Path.Combine(_procRoot, pid.ToString(CultureInfo.InvariantCulture), "stat"));
            return TryParseStat(stat, out ppid, out state, out startTicks);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static (int? Uid, int? TracerPid) ReadStatusFields(string statusPath)
    {
        try
        {
            int? uid = null;
            int? tracerPid = null;
            foreach (var line in File.ReadLines(statusPath))
            {
                if (uid is null && line.StartsWith("Uid:", StringComparison.Ordinal))
                {
                    uid = ParseFirstIntAfterColon(line, 4);
                }
                else if (tracerPid is null && line.StartsWith("TracerPid:", StringComparison.Ordinal))
                {
                    tracerPid = ParseFirstIntAfterColon(line, 10);
                }
                if (uid is not null && tracerPid is not null)
                {
                    break;
                }
            }
            return (uid, tracerPid);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    private static int? ParseFirstIntAfterColon(string line, int startIndex)
    {
        var span = line.AsSpan(startIndex).TrimStart();
        if (span.IsEmpty)
        {
            return null;
        }
        var end = 0;
        while (end < span.Length && !char.IsWhiteSpace(span[end]))
        {
            end++;
        }
        return int.TryParse(span[..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private (string? ExePath, bool ExeDeleted) ReadExe(int pid)
    {
        string? raw = null;
        if (_readExeTarget is not null)
        {
            raw = _readExeTarget(pid);
        }
        else
        {
            var exePath = Path.Combine(_procRoot, pid.ToString(CultureInfo.InvariantCulture), "exe");
            try
            {
                raw = File.ResolveLinkTarget(exePath, returnFinalTarget: false)?.FullName
                    ?? new FileInfo(exePath).LinkTarget;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                raw = null;
            }
        }
        return NormalizeExeTarget(raw);
    }

    internal static (string? Path, bool Deleted) NormalizeExeTarget(string? rawTarget)
    {
        if (string.IsNullOrWhiteSpace(rawTarget))
        {
            return (null, false);
        }
        const string deletedSuffix = " (deleted)";
        if (rawTarget.EndsWith(deletedSuffix, StringComparison.Ordinal))
        {
            var trimmed = rawTarget[..^deletedSuffix.Length];
            return (string.IsNullOrWhiteSpace(trimmed) ? null : trimmed, true);
        }
        return (rawTarget, false);
    }

    /// <summary><c>/proc/&lt;pid&gt;/cmdline</c> with NULs as spaces; null when unreadable.</summary>
    public string? Cmdline(int pid)
    {
        try
        {
            return File.ReadAllText(Path.Combine(_procRoot, pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "cmdline")).Replace('\0', ' ');
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Exact argv from <c>/proc/&lt;pid&gt;/cmdline</c> (NUL-separated); null when unreadable.</summary>
    public IReadOnlyList<string>? Argv(int pid)
    {
        try
        {
            var raw = File.ReadAllText(Path.Combine(_procRoot, pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "cmdline"));
            return raw.TrimEnd('\0').Split('\0');
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public IReadOnlyList<int> ChildrenOf(int parent) => _parent.Where(kv => kv.Value == parent).Select(kv => kv.Key).Order().ToList();

    /// <summary>BFS under <paramref name="rootPid"/>; the first process whose cmdline contains the needle.</summary>
    public int? FindDescendantByCmdline(int rootPid, string needle)
    {
        var queue = new Queue<int>(ChildrenOf(rootPid));
        while (queue.TryDequeue(out var pid))
        {
            if (Cmdline(pid) is { } c && c.Contains(needle, StringComparison.Ordinal))
            {
                return pid;
            }
            foreach (var child in ChildrenOf(pid))
            {
                queue.Enqueue(child);
            }
        }
        return null;
    }

    /// <summary>All descendants of <paramref name="rootPid"/>, BFS order (parents before children).</summary>
    public IReadOnlyList<int> DescendantPids(int rootPid)
    {
        var result = new List<int>();
        var queue = new Queue<int>(ChildrenOf(rootPid));
        while (queue.TryDequeue(out var pid))
        {
            result.Add(pid);
            foreach (var child in ChildrenOf(pid))
            {
                queue.Enqueue(child);
            }
        }
        return result;
    }

    /// <summary>
    /// The order legacy <c>killAll(prefix)</c> actually sends SIGKILL in: every process whose cmdline contains the
    /// prefix plus all descendants. Note: the legacy comment says "deepest children die before their parents", but it
    /// reverses a post-order, which yields PARENTS FIRST (root, then children). Kept as-is for parity (harmless in
    /// practice: PRoot's tracees die with it); recorded in docs/MIGRATION.md section 7.
    /// </summary>
    public IReadOnlyList<int> KillAllOrder(string prefix)
    {
        var roots = _parent.Keys.Where(p => Cmdline(p)?.Contains(prefix, StringComparison.Ordinal) == true).ToList();
        if (roots.Count == 0)
        {
            return [];
        }
        var all = new List<int>();
        var seen = new HashSet<int>();
        void AddTree(int parent)
        {
            foreach (var c in ChildrenOf(parent))
            {
                if (seen.Add(c))
                {
                    all.Add(c);
                    AddTree(c);
                }
            }
        }
        foreach (var r in roots)
        {
            AddTree(r);
            if (seen.Add(r))
            {
                all.Add(r);
            }
        }
        var order = new List<int>();
        var visited = new HashSet<int>();
        void PostOrder(int pid)
        {
            if (!visited.Add(pid))
            {
                return;
            }
            foreach (var c in ChildrenOf(pid))
            {
                PostOrder(c);
            }
            order.Add(pid);
        }
        foreach (var p in all)
        {
            PostOrder(p);
        }
        order.Reverse();
        return order;
    }
}

/// <summary>What the C# side can see of the legacy-started PRoot trees.</summary>
public sealed record RuntimeProcessSnapshot(int? SandboxRootPid, int SandboxTreeSize, int? DshRootPid, int DshTreeSize)
{
    public static RuntimeProcessSnapshot? Observe(int selfPid, string procRoot = "/proc")
    {
        var table = ProcessTable.Read(procRoot);
        if (table is null)
        {
            return null;
        }
        var sandbox = table.FindDescendantByCmdline(selfPid, RuntimeConstants.SandboxKeepaliveMarker);
        var dsh = table.FindDescendantByCmdline(selfPid, RuntimeConstants.DshStartScript);
        return new RuntimeProcessSnapshot(
            sandbox, sandbox is { } s ? table.DescendantPids(s).Count + 1 : 0,
            dsh, dsh is { } d ? table.DescendantPids(d).Count + 1 : 0);
    }
}
