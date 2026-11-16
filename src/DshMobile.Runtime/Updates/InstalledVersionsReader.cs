using System.Text.Json;
using DshMobile.Runtime.Bundle;
using DshMobile.Runtime.Layers;

namespace DshMobile.Runtime.Updates;

/// <summary>
/// What is installed now, for <see cref="ReleaseCatalog"/>: the runtime bundle version (<c>runtime-profile.json</c>
/// <c>bundle.version</c>), the Node layer version (the profile's <c>node</c> layer), the DSH layer version
/// (<see cref="DshLayerInstaller.InstalledVersion"/>). Anything missing or unreadable is null ("not installed /
/// unknown"), so the catalog offers it rather than claiming it is up to date. Read-only.
/// </summary>
public static class InstalledVersionsReader
{
    public static InstalledVersions Read(RuntimeLayout layout, string appVersion)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrEmpty(appVersion);
        string? runtime = null;
        string? node = null;
        var profile = Path.Combine(layout.RuntimeCurrentDir, RuntimeBundleLayout.ProfileName);
        if (File.Exists(profile))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(profile));
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("bundle", out var b) && b.ValueKind == JsonValueKind.Object
                        && b.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                    {
                        runtime = v.GetString();
                    }
                    if (root.TryGetProperty("layers", out var layers) && layers.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var l in layers.EnumerateArray())
                        {
                            if (l.ValueKind == JsonValueKind.Object && l.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() == "node"
                                && l.TryGetProperty("version", out var nv) && nv.ValueKind == JsonValueKind.String)
                            {
                                node = nv.GetString();
                            }
                        }
                    }
                }
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
            }
        }
        return new InstalledVersions(appVersion, Blank(runtime), Blank(node), Blank(new DshLayerInstaller(layout).InstalledVersion()));
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
