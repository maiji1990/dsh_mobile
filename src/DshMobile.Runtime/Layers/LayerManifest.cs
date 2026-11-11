using System.Text.Json.Serialization;

namespace DshMobile.Runtime.Layers;

public enum LayerEntryKind
{
    Dir,
    File,
    Symlink,
}

/// <summary>
/// One extracted path. <see cref="Mode"/> is the owner permission triple the legacy extractor applies (<c>rwx</c>,
/// <c>r-x</c>, ...; x = ANY execute bit in the tar mode), or null when not set by the archive (implicit parents,
/// symlinks). The root directory is never listed.
/// </summary>
public sealed record LayerEntry(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("kind")] LayerEntryKind Kind,
    [property: JsonPropertyName("mode")] string? Mode,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("link")] string? Link);

public static class LayerManifest
{
    /// <summary>Legacy <c>applyEntryMode</c>: owner r = 0400, owner w = 0200, x = any of 0111.</summary>
    public static string OwnerMode(int tarMode) =>
        string.Create(3, tarMode, static (span, m) =>
        {
            span[0] = (m & 0x100) != 0 ? 'r' : '-';
            span[1] = (m & 0x80) != 0 ? 'w' : '-';
            span[2] = (m & 0x49) != 0 ? 'x' : '-';
        });

    /// <summary>
    /// Differences between an expected manifest (C# extraction) and an actual one (legacy-installed tree), at most
    /// <paramref name="max"/>. Modes are compared only where the expectation has one and the actual side could read
    /// it (null on Windows).
    /// </summary>
    public static IReadOnlyList<string> Diff(IEnumerable<LayerEntry> expected, IEnumerable<LayerEntry> actual, int max = 20)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        var a = actual.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var diffs = new List<string>();
        void Add(string s)
        {
            if (diffs.Count < max)
            {
                diffs.Add(s);
            }
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in expected)
        {
            seen.Add(e.Path);
            if (!a.TryGetValue(e.Path, out var x))
            {
                Add($"missing {e.Kind} {e.Path}");
                continue;
            }
            if (x.Kind != e.Kind)
            {
                Add($"kind {e.Path}: expected {e.Kind}, got {x.Kind}");
                continue;
            }
            if (e.Kind == LayerEntryKind.File && (x.Size != e.Size || !string.Equals(x.Sha256, e.Sha256, StringComparison.OrdinalIgnoreCase)))
            {
                Add($"content {e.Path}: expected {e.Size} B {e.Sha256}, got {x.Size} B {x.Sha256}");
            }
            if (e.Kind == LayerEntryKind.Symlink && !string.Equals(x.Link, e.Link, StringComparison.Ordinal))
            {
                Add($"link {e.Path}: expected -> {e.Link}, got -> {x.Link}");
            }
            if (e.Mode is not null && x.Mode is not null && e.Kind != LayerEntryKind.Symlink && !string.Equals(e.Mode, x.Mode, StringComparison.Ordinal))
            {
                Add($"mode {e.Path}: expected {e.Mode}, got {x.Mode}");
            }
        }
        foreach (var x in a.Values.Where(x => !seen.Contains(x.Path)).OrderBy(x => x.Path, StringComparer.Ordinal))
        {
            Add($"unexpected {x.Kind} {x.Path}");
        }
        return diffs;
    }
}
