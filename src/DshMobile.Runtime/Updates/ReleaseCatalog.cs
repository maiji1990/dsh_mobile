namespace DshMobile.Runtime.Updates;

/// <summary>What is installed now; null = not installed / unknown.</summary>
public sealed record InstalledVersions(string App, string? Runtime, string? Node, string? Dsh)
{
    public string? Of(string kind) => kind switch
    {
        ReleaseKinds.App => App,
        ReleaseKinds.Runtime => Runtime,
        ReleaseKinds.Node => Node,
        ReleaseKinds.Dsh => Dsh,
        _ => null,
    };
}

/// <summary>An artifact as offered on this device, with the reason it cannot be applied (null = applicable).</summary>
public sealed record ReleaseOffer(ReleaseArtifact Artifact, string? BlockedBy);

/// <summary>
/// One row of the catalog: the installed version of a kind, every offer for this device's ABI (newest first), and the
/// newest applicable offer that is newer than what is installed (null = up to date or nothing applicable).
/// </summary>
public sealed record ReleaseChoice(string Kind, string? Installed, IReadOnlyList<ReleaseOffer> Offers, ReleaseArtifact? Update);

/// <summary>
/// Read-only version discovery over a verified manifest (ADR-0022): never downloads, installs or switches anything.
/// Constraints are evaluated against what is installed now: <c>minAppVersion</c> against the app, <c>requiresNode</c>
/// (<c>24.x</c> / exact) against the installed Node layer, ABI against the device. Runtime / Node / DSH / App are four
/// independent rows; picking a combination and installing it is a later slice (P0-06).
/// </summary>
public static class ReleaseCatalog
{
    public static IReadOnlyList<ReleaseChoice> Evaluate(ReleaseManifest manifest, InstalledVersions installed, string deviceAbi)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentException.ThrowIfNullOrEmpty(deviceAbi);
        var rows = new List<ReleaseChoice>();
        foreach (var kind in ReleaseKinds.All)
        {
            var current = installed.Of(kind);
            var offers = manifest.Artifacts
                .Where(a => a.Kind == kind && a.Abi == deviceAbi)
                .OrderByDescending(a => a.Version, Comparer<string>.Create(Versions.Compare))
                .Select(a => new ReleaseOffer(a, BlockedBy(a, installed)))
                .ToList();
            var update = offers.FirstOrDefault(o => o.BlockedBy is null && (current is null || Versions.IsNewer(o.Artifact.Version, current)))?.Artifact;
            rows.Add(new ReleaseChoice(kind, current, offers, update));
        }
        return rows;
    }

    private static string? BlockedBy(ReleaseArtifact a, InstalledVersions installed)
    {
        if (a.MinAppVersion is { } min && Versions.Compare(installed.App, min) < 0)
        {
            return $"needs app {min} or newer (installed {installed.App})";
        }
        if (a.RequiresNode is { } node && !NodeMatches(node, installed.Node))
        {
            return $"needs Node {node} (installed {installed.Node ?? "none"})";
        }
        return null;
    }

    /// <summary><c>24.x</c> matches any 24.*; anything else must compare equal.</summary>
    internal static bool NodeMatches(string requirement, string? installed)
    {
        if (installed is null)
        {
            return false;
        }
        var v = installed.Trim().TrimStart('v');
        if (requirement.EndsWith(".x", StringComparison.Ordinal))
        {
            var major = requirement[..^2].TrimStart('v');
            return v == major || v.StartsWith(major + ".", StringComparison.Ordinal);
        }
        return Versions.Compare(v, requirement) == 0;
    }
}
