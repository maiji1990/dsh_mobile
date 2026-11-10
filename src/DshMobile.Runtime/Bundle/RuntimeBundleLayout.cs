// Ported from DSHBox v1.5.1 (7c9d33c) sandbox-manager/.../RuntimeBundleLayout.kt (GPL-3.0-or-later).
// Parity: tests/TestVectors/runtime-bundle-layout.json runs against both implementations.

using System.Text.RegularExpressions;

namespace DshMobile.Runtime.Bundle;

/// <summary>
/// Pure analysis of a runtime-bundle ZIP's entry names. Encodes the upstream 1.1.0 fixes: layer archives match
/// exactly <c>&lt;layer&gt;.tar[.ext]</c> (a <c>.sha256</c> sidecar never matches); one common top-level folder
/// shared by ALL entries is stripped; any <c>..</c> segment makes the whole archive unsafe.
/// </summary>
public static partial class RuntimeBundleLayout
{
    public const string ProfileName = "runtime-profile.json";

    [GeneratedRegex(@"^(base|node|android-side)\.tar(\.[A-Za-z0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ArchiveName();

    /// <summary>Layer name (<c>base</c>/<c>node</c>/<c>android-side</c>) for an archive file name, else null.</summary>
    public static string? LayerOfArchiveName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var clean = name.Replace('\\', '/');
        clean = clean[(clean.LastIndexOf('/') + 1)..];
        if (clean.EndsWith(".sha256", StringComparison.Ordinal) || !ArchiveName().IsMatch(clean))
        {
            return null;
        }
        if (clean.StartsWith("base.tar", StringComparison.Ordinal))
        {
            return "base";
        }
        if (clean.StartsWith("node.tar", StringComparison.Ordinal))
        {
            return "node";
        }
        return clean.StartsWith("android-side.tar", StringComparison.Ordinal) ? "android-side" : null;
    }

    public static BundleLayoutResult Analyze(IReadOnlyList<string> rawEntryNames)
    {
        ArgumentNullException.ThrowIfNull(rawEntryNames);
        if (rawEntryNames.Count == 0)
        {
            return BundleLayoutResult.Ok(new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, string>(), null);
        }

        // Kotlin associateWith: duplicate raw names collapse to one entry, first-insertion order kept.
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var raw in rawEntryNames)
        {
            if (normalized.TryAdd(raw, Normalize(raw)))
            {
                order.Add(raw);
            }
        }

        var first = normalized[order[0]];
        var slash = first.IndexOf('/', StringComparison.Ordinal);
        var firstSeg = slash < 0 ? "" : first[..slash];
        string? prefix = null;
        if (firstSeg.Length > 0 && order.All(r => normalized[r].StartsWith(firstSeg + "/", StringComparison.Ordinal)))
        {
            prefix = firstSeg + "/";
        }

        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in order)
        {
            var n = normalized[raw];
            var stripped = prefix is not null && n.StartsWith(prefix, StringComparison.Ordinal) ? n[prefix.Length..] : n;
            if (stripped.Length == 0)
            {
                continue;
            }
            if (stripped.Split('/').Any(s => s == ".."))
            {
                return BundleLayoutResult.Unsafe(raw);
            }
            targets[raw] = stripped;
        }

        var archives = new Dictionary<string, string>(StringComparer.Ordinal);
        var sidecars = new Dictionary<string, string>(StringComparer.Ordinal);
        string? profile = null;
        var targetNames = targets.Values.ToHashSet(StringComparer.Ordinal);
        foreach (var stripped in targets.Values)
        {
            if (stripped == ProfileName)
            {
                profile = stripped;
                continue;
            }
            var layer = LayerOfArchiveName(stripped);
            if (layer is not null)
            {
                archives[layer] = stripped;
                var sidecar = stripped + ".sha256";
                if (targetNames.Contains(sidecar))
                {
                    sidecars[layer] = sidecar;
                }
            }
        }
        return BundleLayoutResult.Ok(targets, archives, sidecars, profile);
    }

    internal static string Normalize(string name)
    {
        var n = name.Replace('\\', '/');
        return n.StartsWith("./", StringComparison.Ordinal) ? n[2..] : n;
    }
}

/// <summary>Outcome of <see cref="RuntimeBundleLayout.Analyze"/>.</summary>
public sealed record BundleLayoutResult
{
    private BundleLayoutResult()
    {
    }

    public bool IsUnsafe => UnsafeEntry is not null;

    /// <summary>The raw entry name that contained a traversal segment; null when the layout is safe.</summary>
    public string? UnsafeEntry { get; private init; }

    /// <summary>Raw zip entry name -> normalized relative path.</summary>
    public IReadOnlyDictionary<string, string> Targets { get; private init; } = new Dictionary<string, string>();

    /// <summary>Layer -> archive path (relative, normalized).</summary>
    public IReadOnlyDictionary<string, string> Archives { get; private init; } = new Dictionary<string, string>();

    /// <summary>Layer -> sidecar <c>.sha256</c> path (only when present).</summary>
    public IReadOnlyDictionary<string, string> Sidecars { get; private init; } = new Dictionary<string, string>();

    public string? ProfilePath { get; private init; }

    internal static BundleLayoutResult Ok(
        IReadOnlyDictionary<string, string> targets,
        IReadOnlyDictionary<string, string> archives,
        IReadOnlyDictionary<string, string> sidecars,
        string? profilePath) =>
        new() { Targets = targets, Archives = archives, Sidecars = sidecars, ProfilePath = profilePath };

    internal static BundleLayoutResult Unsafe(string entry) => new() { UnsafeEntry = entry };
}
