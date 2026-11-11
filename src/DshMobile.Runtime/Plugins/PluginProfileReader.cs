// Ported from DSHBox plugin-manager (GPL-3.0), commit 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4:
//   plugin-manager/src/main/java/com/dshbox/pluginmanager/market/data/ProfileReader.kt
//     (dependencyNames, facts, carrierEntryUsable)
//   plugin-manager/src/main/java/com/dshbox/pluginmanager/core/PluginPaths.kt (user-data/.dsh/profiles/<profile>)
// Registered in docs/UPSTREAMS.md. Deviation: absolute declared patch paths are ignored
// (upstream resolves them on the host, where a guest path means nothing).

using System.Text.Json;
using DshMobile.Runtime.Guest;

namespace DshMobile.Runtime.Plugins;

/// <summary>Where the install guard reads its facts from (a fake in tests).</summary>
public interface IPluginProfileFacts
{
    /// <summary>Keys of the profile's <c>dependencies</c>; null when the manifest is missing or unreadable.</summary>
    IReadOnlySet<string>? DependencyNames();

    PluginPackageFacts Facts(string name);
}

/// <summary>Reads a DSH profile from the host side of the sandbox workspace (<c>user-data/.dsh/profiles/&lt;profile&gt;</c>).</summary>
public sealed class PluginProfileReader : IPluginProfileFacts
{
    private readonly string _runtimeCurrentDir;

    public PluginProfileReader(RuntimeLayout layout, string profile)
        : this(Path.Combine(layout?.UserDataDir ?? throw new ArgumentNullException(nameof(layout)), ".dsh", "profiles"), profile, layout.RuntimeCurrentDir)
    {
    }

    public PluginProfileReader(string profilesDir, string profile, string runtimeCurrentDir)
    {
        if (!PluginCommand.Profiles.Contains(profile))
        {
            throw new ArgumentException("unknown profile: " + profile, nameof(profile));
        }
        ProfilesDir = profilesDir;
        ProfileDir = Path.Combine(profilesDir, profile);
        _runtimeCurrentDir = runtimeCurrentDir;
    }

    public string ProfilesDir { get; }

    public string ProfileDir { get; }

    public string NodeModulesDir => Path.Combine(ProfileDir, PluginPackageRules.NodeModules);

    public IReadOnlySet<string>? DependencyNames()
    {
        try
        {
            var file = Path.Combine(ProfileDir, PluginPackageRules.Manifest);
            if (!File.Exists(file))
            {
                return null;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(file).TrimStart('﻿'));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (doc.RootElement.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
            {
                foreach (var d in deps.EnumerateObject())
                {
                    names.Add(d.Name);
                }
            }
            return names;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public PluginPackageFacts Facts(string name)
    {
        // Dependency keys come from a file: never let one walk out of node_modules.
        if (!PluginCommand.PackageName().IsMatch(name))
        {
            return PluginPackageFacts.Missing(name);
        }
        var dir = Path.Combine(NodeModulesDir, name);
        if (!Directory.Exists(dir))
        {
            return PluginPackageFacts.Missing(name);
        }
        var manifest = Path.Combine(dir, PluginPackageRules.Manifest);
        string text;
        PluginPackageFacts facts;
        try
        {
            text = File.ReadAllText(manifest);
            facts = PluginPackageRules.Parse(name, text, rel => IsFileUnder(dir, rel));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return PluginPackageFacts.Unreadable(name);
        }
        if (facts.HasBundle && !facts.BundleEntryExists && CarrierEntryUsable(dir, text, name))
        {
            return facts with { HasCarrierEntry = true };
        }
        return facts;
    }

    /// <summary>Upstream carrier rule: a package mounting another package (by patch <c>name:</c>) that has a real entry.</summary>
    private bool CarrierEntryUsable(string packageDir, string manifestText, string ownName)
    {
        var patchFiles = PluginPackageRules.DeclaredPatchFiles(manifestText)
            .Select(p => SafeUnder(packageDir, p.Trim().Replace("file://", "", StringComparison.Ordinal)))
            .Append(Path.Combine(packageDir, PluginPackageRules.ConventionalPatch))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal);
        var targets = patchFiles
            .SelectMany(f => { try { return File.Exists(f) ? PluginPackageRules.PatchTargetNames(File.ReadAllText(f)) : []; } catch (IOException) { return []; } })
            .Where(n => n != ownName && PluginCommand.PackageName().IsMatch(n))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (targets.Count == 0)
        {
            return false;
        }
        string[] roots =
        [
            NodeModulesDir,
            Path.Combine(ProfilesDir, PluginPackageRules.NodeModules),
            Path.Combine(_runtimeCurrentDir, "dsh", PluginPackageRules.NodeModules),
            Path.Combine(packageDir, PluginPackageRules.NodeModules),
        ];
        return targets.Any(t => roots.Any(r => EntryArtifactExists(Path.Combine(r, t))));
    }

    private static bool EntryArtifactExists(string dir)
    {
        var manifest = Path.Combine(dir, PluginPackageRules.Manifest);
        try
        {
            return File.Exists(manifest)
                && PluginPackageRules.EntryArtifactCandidates(File.ReadAllText(manifest)).Any(c => IsFileUnder(dir, c));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Upstream probes entries with <c>File(dir, relative)</c>, which joins even a leading <c>/</c> under the package
    /// directory; <c>..</c> is kept (pnpm package directories are symlinks into <c>.pnpm/</c>). Existence probe only.
    /// </summary>
    private static bool IsFileUnder(string dir, string relative)
    {
        var rel = relative.Trim();
        rel = (rel.StartsWith("./", StringComparison.Ordinal) ? rel[2..] : rel).TrimStart('/');
        return rel.Length > 0 && !Path.IsPathRooted(rel) && File.Exists(Path.Combine(dir, rel));
    }

    /// <summary>A declared patch file under the package directory; null for an absolute path (deviation, see header).</summary>
    private static string? SafeUnder(string dir, string relative)
    {
        var rel = relative.Trim();
        rel = rel.StartsWith("./", StringComparison.Ordinal) ? rel[2..] : rel;
        return rel.Length == 0 || rel.StartsWith('/') || Path.IsPathRooted(rel) ? null : Path.Combine(dir, rel);
    }
}
