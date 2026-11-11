// Ported from DSHBox plugin-manager (GPL-3.0), commit 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4:
//   plugin-manager/src/main/java/com/dshbox/pluginmanager/market/data/ProfileReader.kt
//     (PackageFacts, hasUsableEntry, parsePackageFacts, entryArtifactExists)
//   plugin-manager/src/main/java/com/dshbox/pluginmanager/market/data/InstallGuard.kt (installGap, declaredPackageName)
//   plugin-manager/src/main/java/com/dshbox/pluginmanager/market/data/PackageRowIds.kt (declaredPatchFiles, patchTargetNames)
//   plugin-manager/src/main/java/com/dshbox/pluginmanager/contract/{HostContract,PatchSyntax}.kt (field names, NAME_VALUE)
// Registered in docs/UPSTREAMS.md. Pure parsing only; file-system reads live in PluginProfileReader.

using System.Text.Json;
using System.Text.RegularExpressions;

namespace DshMobile.Runtime.Plugins;

/// <summary>On-disk facts about one installed package (upstream <c>PackageFacts</c>, minus market-only fields).</summary>
public sealed record PluginPackageFacts(
    string Name,
    string? Version,
    bool Installed,
    bool ManifestReadable,
    bool HasDshField,
    bool HasBundle,
    bool HasClient,
    bool BundleEntryExists = false,
    bool ClientEntryExists = false,
    bool HasCarrierEntry = false)
{
    public static PluginPackageFacts Missing(string name) => new(name, null, false, false, false, false, false);

    public static PluginPackageFacts Unreadable(string name) => new(name, null, true, false, false, false, false);

    /// <summary>A declared entry whose artifact really exists (own, or mounted through a carrier patch).</summary>
    public bool HasUsableEntry =>
        (HasBundle && (BundleEntryExists || HasCarrierEntry)) || (HasClient && ClientEntryExists);
}

/// <summary>Why an installed package cannot be loaded; Chinese and English halves (one " / " when joined).</summary>
public sealed record PluginInstallGap(string Zh, string En);

public static partial class PluginPackageRules
{
    public const string Manifest = "package.json";
    public const string NodeModules = "node_modules";
    public const string ConventionalPatch = "cordis.patch.yml";

    private static readonly string[] ExportEntryKeys = ["import", "require", "default", "browser", "node"];

    [GeneratedRegex(@"^(@[A-Za-z0-9._-]+/)?[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex DeclaredNameShape();

    [GeneratedRegex(@"^\s*-?\s*name:\s*['""]?([^'""\s]+)", RegexOptions.CultureInvariant)]
    private static partial Regex PatchNameValue();

    /// <summary>Upstream <c>installGap</c>: null when loadable. Order is priority.</summary>
    public static PluginInstallGap? InstallGap(PluginPackageFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (!facts.Installed)
        {
            return new("node_modules 里找不到它的目录", "its directory is missing from node_modules");
        }
        if (!facts.ManifestReadable)
        {
            return new("它的 package.json 读不出来", "its package.json could not be read");
        }
        if (!facts.HasDshField)
        {
            return new("它的 package.json 里没有 dsh 字段（不是 DSH 插件）", "its package.json has no dsh field (it is not a DSH plugin)");
        }
        if ((facts.HasBundle || facts.HasClient) && !facts.HasUsableEntry)
        {
            return new("它声明的入口产物文件不存在（构建脚本被拦时插件会装成没有产物的形态）",
                "the entry artifact it declares does not exist (with build scripts blocked the plugin installs without its artifacts)");
        }
        if (!facts.HasBundle && !facts.HasClient)
        {
            return new("它的 dsh 字段既没有 bundle 也没有 client（没有入口产物）", "its dsh field declares neither bundle nor client (no entry artifact)");
        }
        return null;
    }

    /// <summary>Upstream <c>declaredPackageName</c>: a fallback name only when it has a package shape.</summary>
    public static string? DeclaredPackageName(string? declaredName)
    {
        var trimmed = declaredName?.Trim() ?? "";
        return trimmed.Length > 0 && DeclaredNameShape().IsMatch(trimmed) ? trimmed : null;
    }

    /// <summary>
    /// Upstream <c>parsePackageFacts</c>: a package manifest text → facts. <paramref name="entryExists"/> answers
    /// whether a path relative to the package directory is a file. Throws <see cref="JsonException"/> on bad JSON.
    /// </summary>
    public static PluginPackageFacts Parse(string name, string manifestText, Func<string, bool> entryExists)
    {
        ArgumentNullException.ThrowIfNull(manifestText);
        ArgumentNullException.ThrowIfNull(entryExists);
        using var doc = JsonDocument.Parse(manifestText.TrimStart('﻿'));
        var obj = doc.RootElement;
        if (obj.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("package.json is not an object");
        }
        var dsh = Child(obj, "dsh") is { ValueKind: JsonValueKind.Object } d ? d : (JsonElement?)null;
        var bundle = dsh is { } d1 ? Child(d1, "bundle") : null;
        var client = dsh is { } d2 ? Child(d2, "client") : null;
        var hasBundle = bundle is { ValueKind: not JsonValueKind.Null };
        var hasClient = client is { ValueKind: not JsonValueKind.Null };
        var exports = Child(obj, "exports");

        var entries = new List<string>();
        if (Child(obj, "main") is { ValueKind: JsonValueKind.String } main)
        {
            entries.Add(Relative(main.GetString()!));
        }
        if (exports is { ValueKind: JsonValueKind.String } es)
        {
            entries.Add(Relative(es.GetString()!));
        }
        else if (exports is { ValueKind: JsonValueKind.Object } eo)
        {
            entries.AddRange(StringsFrom(Child(eo, ".")));
        }
        entries.Add("lib/index.js");
        entries.Add("index.js");

        var clients = new List<string>();
        clients.AddRange(StringsFrom(client));
        if (exports is { ValueKind: JsonValueKind.Object } eo2)
        {
            clients.AddRange(StringsFrom(Child(eo2, "./client")));
        }
        clients.Add("client/client.js");

        var version = Child(obj, "version") is { } v
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())
            : null;
        return new PluginPackageFacts(
            name,
            string.IsNullOrWhiteSpace(version) ? null : version,
            Installed: true,
            ManifestReadable: true,
            HasDshField: dsh is not null,
            HasBundle: hasBundle,
            HasClient: hasClient,
            BundleEntryExists: hasBundle && entries.Where(e => e.Length > 0).Distinct().Any(entryExists),
            ClientEntryExists: hasClient && clients.Where(e => e.Length > 0).Distinct().Any(entryExists));
    }

    /// <summary>Upstream <c>entryArtifactExists</c> candidates: <c>main</c>, <c>exports</c> (string / "." string / every "." value), else index.js.</summary>
    public static IReadOnlyList<string> EntryArtifactCandidates(string manifestText)
    {
        using var doc = JsonDocument.Parse(manifestText.TrimStart('﻿'));
        var obj = doc.RootElement;
        var candidates = new List<string>();
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return candidates;
        }
        if (Child(obj, "main") is { ValueKind: JsonValueKind.String } main)
        {
            candidates.Add(Relative(main.GetString()!));
        }
        switch (Child(obj, "exports"))
        {
            case { ValueKind: JsonValueKind.String } s:
                candidates.Add(Relative(s.GetString()!));
                break;
            case { ValueKind: JsonValueKind.Object } o:
                switch (Child(o, "."))
                {
                    case { ValueKind: JsonValueKind.String } rs:
                        candidates.Add(Relative(rs.GetString()!));
                        break;
                    case { ValueKind: JsonValueKind.Object } ro:
                        foreach (var p in ro.EnumerateObject())
                        {
                            if (p.Value.ValueKind == JsonValueKind.String)
                            {
                                candidates.Add(Relative(p.Value.GetString()!));
                            }
                        }
                        break;
                }
                break;
        }
        if (candidates.Count == 0)
        {
            candidates.Add("index.js");
        }
        return candidates.Where(c => c.Length > 0).ToList();
    }

    /// <summary>Upstream <c>declaredPatchFiles</c>: <c>dsh.bundle.patch</c> (string or array), or a <c>.yml</c> bundle string.</summary>
    public static IReadOnlyList<string> DeclaredPatchFiles(string manifestText)
    {
        try
        {
            using var doc = JsonDocument.Parse(manifestText.TrimStart('﻿'));
            if (doc.RootElement.ValueKind != JsonValueKind.Object || Child(doc.RootElement, "dsh") is not { ValueKind: JsonValueKind.Object } dsh)
            {
                return [];
            }
            switch (Child(dsh, "bundle"))
            {
                case { ValueKind: JsonValueKind.Object } b:
                    return Child(b, "patch") switch
                    {
                        { ValueKind: JsonValueKind.String } p => string.IsNullOrWhiteSpace(p.GetString()) ? [] : [p.GetString()!],
                        { ValueKind: JsonValueKind.Array } a => a.EnumerateArray()
                            .Where(e => e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
                            .Select(e => e.GetString()!).ToList(),
                        _ => [],
                    };
                case { ValueKind: JsonValueKind.String } s:
                    var value = s.GetString()!;
                    return value.EndsWith(".yml", StringComparison.Ordinal) || value.EndsWith(".yaml", StringComparison.Ordinal) ? [value] : [];
                default:
                    return [];
            }
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Upstream <c>patchTargetNames</c>: every <c>name:</c> value in a patch text, first-seen order.</summary>
    public static IReadOnlyList<string> PatchTargetNames(string patchText)
    {
        ArgumentNullException.ThrowIfNull(patchText);
        var names = new List<string>();
        foreach (var raw in patchText.Split('\n'))
        {
            var m = PatchNameValue().Match(raw.TrimEnd('\r'));
            if (m.Success)
            {
                var name = Unquote(m.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name))
                {
                    names.Add(name);
                }
            }
        }
        return names;
    }

    private static string Unquote(string value)
    {
        var t = value.Trim();
        if (t.Length >= 2 && t[0] == '\'' && t[^1] == '\'')
        {
            return t[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }
        return t.Length >= 2 && t[0] == '"' && t[^1] == '"' ? t[1..^1] : t;
    }

    private static string Relative(string value)
    {
        var t = value.Trim();
        return t.StartsWith("./", StringComparison.Ordinal) ? t[2..] : t;
    }

    private static IEnumerable<string> StringsFrom(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.String } s => [Relative(s.GetString()!)],
        { ValueKind: JsonValueKind.Object } o => ExportEntryKeys
            .Select(k => Child(o, k))
            .Where(e => e is { ValueKind: JsonValueKind.String })
            .Select(e => Relative(e!.Value.GetString()!)),
        _ => [],
    };

    private static JsonElement? Child(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) ? v : null;
}
