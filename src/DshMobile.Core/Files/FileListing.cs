using System.Globalization;

namespace DshMobile.Core.Files;

public enum FileSort
{
    Name,
    Size,
    Modified,
    Type,
}

/// <summary>One crumb of the path bar: label and the provider path it opens ("" = root).</summary>
public sealed record Breadcrumb(string Label, string Path);

/// <summary>Results of a recursive name search; <see cref="Truncated"/> when a limit stopped it early.</summary>
public sealed record SearchResult(IReadOnlyList<WorkspaceEntry> Matches, bool Truncated, int Visited);

/// <summary>
/// Sorting, filtering, breadcrumbs and recursive name search for the Files screen (vNext PR-09). Pure functions over
/// <see cref="WorkspaceEntry"/> lists so the UI only displays; the search walks the workspace without following
/// symlinks and never descends into the protected <c>.dsh</c> tree.
/// </summary>
public static class FileListing
{
    public const int DefaultSearchLimit = 500;
    public const int DefaultVisitLimit = 200_000;

    /// <summary>Directories first (always), then by <paramref name="sort"/>; ties by natural name order.</summary>
    public static IReadOnlyList<WorkspaceEntry> Sort(IEnumerable<WorkspaceEntry> entries, FileSort sort, bool descending)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var comparer = Comparer<WorkspaceEntry>.Create((a, b) =>
        {
            var c = sort switch
            {
                FileSort.Size => a.Size.CompareTo(b.Size),
                FileSort.Modified => a.Modified.CompareTo(b.Modified),
                FileSort.Type => string.Compare(Extension(a), Extension(b), StringComparison.OrdinalIgnoreCase),
                _ => 0,
            };
            return c != 0 ? c : NaturalCompare(a.Name, b.Name);
        });
        var dirs = entries.Where(e => e.Kind == WorkspaceEntryKind.Directory);
        var rest = entries.Where(e => e.Kind != WorkspaceEntryKind.Directory);
        IEnumerable<WorkspaceEntry> Order(IEnumerable<WorkspaceEntry> x) => descending ? x.OrderDescending(comparer) : x.Order(comparer);
        return [.. Order(dirs), .. Order(rest)];
    }

    private static string Extension(WorkspaceEntry e) => e.Kind == WorkspaceEntryKind.Directory ? "" : Path.GetExtension(e.Name);

    /// <summary>"file2" before "file10"; case-insensitive (ordinal on ties, so the order is total and stable).</summary>
    public static int NaturalCompare(string? a, string? b)
    {
        a ??= "";
        b ??= "";
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i]))
                {
                    i++;
                }
                while (j < b.Length && char.IsAsciiDigit(b[j]))
                {
                    j++;
                }
                var na = a.AsSpan(si, i - si).TrimStart('0');
                var nb = b.AsSpan(sj, j - sj).TrimStart('0');
                var c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : na.SequenceCompareTo(nb);
                if (c != 0)
                {
                    return c;
                }
                continue;
            }
            var x = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
            if (x != 0)
            {
                return x;
            }
            i++;
            j++;
        }
        var rest = (a.Length - i).CompareTo(b.Length - j);
        return rest != 0 ? rest : string.CompareOrdinal(a, b);
    }

    /// <summary>Entries whose name contains <paramref name="query"/> (case-insensitive, invariant culture).</summary>
    public static IReadOnlyList<WorkspaceEntry> Filter(IEnumerable<WorkspaceEntry> entries, string? query)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (string.IsNullOrWhiteSpace(query))
        {
            return entries as IReadOnlyList<WorkspaceEntry> ?? [.. entries];
        }
        var q = query.Trim();
        return [.. entries.Where(e => CultureInfo.InvariantCulture.CompareInfo.IndexOf(e.Name, q, CompareOptions.IgnoreCase) >= 0)];
    }

    public static IReadOnlyList<Breadcrumb> Breadcrumbs(string rootLabel, string? path)
    {
        var norm = WorkspaceFileSystem.NormalizeRelative(path);
        var crumbs = new List<Breadcrumb> { new(rootLabel, "") };
        var acc = "";
        foreach (var part in norm.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            acc = acc.Length == 0 ? part : acc + "/" + part;
            crumbs.Add(new Breadcrumb(part, acc));
        }
        return crumbs;
    }

    /// <summary>
    /// Breadth-first name search below <paramref name="start"/>: never follows a symlink, never enters <c>.dsh</c>
    /// (which still matches by name), stops at <paramref name="maxResults"/> matches or <paramref name="maxVisited"/>
    /// entries (<see cref="SearchResult.Truncated"/>). Unreadable directories are skipped.
    /// </summary>
    public static SearchResult Search(WorkspaceFileSystem fs, string? start, string query, int maxResults = DefaultSearchLimit,
        int maxVisited = DefaultVisitLimit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var q = query.Trim();
        var matches = new List<WorkspaceEntry>();
        var visited = 0;
        var queue = new Queue<string>();
        queue.Enqueue(WorkspaceFileSystem.NormalizeRelative(start));
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = queue.Dequeue();
            IReadOnlyList<WorkspaceEntry> entries;
            try
            {
                entries = fs.List(dir);
            }
            catch (Exception e) when (e is WorkspaceException or IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var e in entries)
            {
                if (++visited > maxVisited)
                {
                    return new SearchResult(matches, true, visited - 1);
                }
                if (CultureInfo.InvariantCulture.CompareInfo.IndexOf(e.Name, q, CompareOptions.IgnoreCase) >= 0)
                {
                    matches.Add(e);
                    if (matches.Count >= maxResults)
                    {
                        return new SearchResult(matches, true, visited);
                    }
                }
                if (e.Kind == WorkspaceEntryKind.Directory && !e.Protected)
                {
                    queue.Enqueue(e.RelativePath);
                }
            }
        }
        return new SearchResult(matches, false, visited);
    }
}
