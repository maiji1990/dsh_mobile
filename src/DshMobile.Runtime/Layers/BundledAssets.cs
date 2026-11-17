// The APK-bundled DSH layer (assets/dsh/<version>.tar.zst, the former BundledDsh / RuntimePreparer) was removed by
// ADR-0027: the DSH layer is imported from a file (runtime.import.dsh) or updated online. What remains here is the
// read-only asset view the guest-asset provisioner needs and the legacy install check, ported from DSHBox v1.5.1
// (7c9d33c) sandbox-manager DefaultSandboxManager.isRuntimeInstalled / verifyLayersBroken / prootBinary
// (GPL-3.0-or-later).

using System.Text;
using DshMobile.Runtime.Bundle;

namespace DshMobile.Runtime.Layers;

/// <summary>Read-only view of the APK assets (Android <c>AssetManager</c>); paths are relative to the assets root.</summary>
public interface IBundledAssets
{
    /// <summary>Names directly under <paramref name="dir"/>; empty when the directory does not exist.</summary>
    IReadOnlyList<string> List(string dir);

    /// <summary>Opens one asset; a missing or unreadable one throws an <see cref="IOException"/>.</summary>
    Stream Open(string path);
}

/// <summary>Legacy <c>isRuntimeInstalled</c>: PRoot binary + base dir + a profile with an assembly whose layers are intact.</summary>
public sealed record RuntimeInstallCheck(bool Installed, string Reason, IReadOnlyList<string> Broken, IReadOnlyList<string> SentinelsToHeal)
{
    /// <summary>
    /// <paramref name="heal"/> mirrors the legacy self-heal (a layer without a sentinel gets the profile's digest
    /// recorded); shadows pass false and only report what would be written.
    /// </summary>
    public static RuntimeInstallCheck Run(RuntimeLayout layout, bool heal)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var proot = File.Exists(Path.Combine(layout.NativeLibraryDir, "libproot.so"))
            || File.Exists(Path.Combine(layout.LayerDir("android-side"), "bin", "proot"));
        if (!proot || !Directory.Exists(layout.LayerDir("base")))
        {
            return new RuntimeInstallCheck(false, "proot/base missing", [], []);
        }
        var profileFile = Path.Combine(layout.RuntimeCurrentDir, RuntimeBundleLayout.ProfileName);
        RuntimeProfile? profile = null;
        try
        {
            profile = File.Exists(profileFile) ? RuntimeProfile.Parse(File.ReadAllText(profileFile, Encoding.UTF8)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        if (profile is null || profile.Assembly.Count == 0)
        {
            return new RuntimeInstallCheck(false, "profile missing/empty", [], []);
        }
        var broken = new List<string>();
        var heals = new List<string>();
        foreach (var name in profile.Assembly)
        {
            var dir = layout.LayerDir(name);
            if (!Directory.Exists(dir))
            {
                broken.Add($"{name}:dir-missing");
                continue;
            }
            var expected = profile.Layer(name)?.Sha256;
            if (string.IsNullOrWhiteSpace(expected))
            {
                broken.Add($"{name}:profile-has-no-sha");
                continue;
            }
            var sentinel = Path.Combine(dir, RuntimeConstants.LayerStateDir, $"layer-{name}.sha256");
            if (File.Exists(sentinel))
            {
                if (!File.ReadAllText(sentinel, Encoding.UTF8).Trim().Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    broken.Add($"{name}:sha-mismatch");
                }
            }
            else
            {
                heals.Add($"{name}/.dshmobile/layer-{name}.sha256");
                if (heal)
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!);
                        File.WriteAllText(sentinel, expected);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }
        return new RuntimeInstallCheck(broken.Count == 0, broken.Count == 0 ? "ok" : "layer integrity broken", broken, heals);
    }
}
