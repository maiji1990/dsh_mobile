// Ported from DSHBox v1.5.1 (7c9d33c) sandbox-manager/.../SandboxProcessRunner.kt readProcTable / childrenOf /
// findDescendantByCmdline / descendantPids / killAll ordering (GPL-3.0-or-later).

namespace DshMobile.Runtime.Supervision;

/// <summary>
/// A snapshot of <c>/proc</c> (pid → ppid, plus lazily read cmdlines), with the legacy lookups used to locate and
/// stop the sandbox / DSH PRoot trees. The proc root is injectable so the logic is testable off-device. Reading is
/// side-effect free; nothing here kills anything (the kill order is computed, not executed).
/// </summary>
public sealed class ProcessTable
{
    private readonly string _procRoot;
    private readonly Dictionary<int, int> _parent;

    private ProcessTable(string procRoot, Dictionary<int, int> parent)
    {
        _procRoot = procRoot;
        _parent = parent;
    }

    public IReadOnlyDictionary<int, int> ParentOf => _parent;

    /// <summary>
    /// <c>readProcTable()</c>: every numeric entry whose <c>stat</c> is readable. Like legacy, the ppid is the 2nd
    /// field after the FIRST <c>") "</c> (a comm containing ") " would be misparsed; kept for parity).
    /// </summary>
    public static ProcessTable? Read(string procRoot = "/proc")
    {
        if (!Directory.Exists(procRoot))
        {
            return null;
        }
        var map = new Dictionary<int, int>();
        foreach (var dir in Directory.EnumerateDirectories(procRoot))
        {
            var name = Path.GetFileName(dir);
            if (name.Length == 0 || !name.All(char.IsAsciiDigit) || !int.TryParse(name, out var pid))
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
            var idx = stat.IndexOf(") ", StringComparison.Ordinal);
            var rest = (idx < 0 ? stat : stat[(idx + 2)..]).Trim();
            var fields = rest.Split(' ');
            if (fields.Length > 1 && int.TryParse(fields[1], out var ppid))
            {
                map[pid] = ppid;
            }
        }
        return new ProcessTable(procRoot, map);
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
