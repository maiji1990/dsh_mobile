// Ported from DSH Mobile native-bridge RuntimeController.kt `runtime.prepare` / provisionBundledDsh (itself ported from
// DSHBox v1.5.1 SandboxService.provisionBundledDsh) and DSHBox v1.5.1 (7c9d33c) sandbox-manager
// BundledRuntimeInstaller.installIfAbsent (slot markers / asset detection only) and DefaultSandboxManager
// isRuntimeInstalled / verifyLayersBroken / prootBinary (GPL-3.0-or-later).

using System.Text;
using DshMobile.Runtime.Bundle;

namespace DshMobile.Runtime.Layers;

/// <summary>Read-only view of the APK assets (Android <c>AssetManager</c>); paths are relative to the assets root.</summary>
public interface IBundledAssets
{
    /// <summary>Names directly under <paramref name="dir"/>; empty when the directory does not exist.</summary>
    IReadOnlyList<string> List(string dir);

    Stream Open(string path);
}

/// <summary>The APK's DSH baseline: <c>assets/dsh/&lt;version&gt;.tar.zst</c> (or <c>.tar.gz</c>) plus an optional <c>.sha256</c> sidecar.</summary>
public sealed record BundledDshAsset(string Tarball, string Version, bool HasSidecar)
{
    public string AssetPath => $"{BundledDsh.AssetDir}/{Tarball}";

    public string SidecarPath => AssetPath + ".sha256";
}

/// <summary>A provisioning plan computed without touching the DSH layer.</summary>
public sealed record BundledDshPlan(BundledDshAsset? Asset, DshLayerDecision? Decision)
{
    public bool WouldInstall => Decision?.Install == true;
}

public static class BundledDsh
{
    public const string AssetDir = "dsh";
    public const string NoBaseline = "no DSH baseline in APK";

    /// <summary>Legacy selection: first <c>.tar.zst</c>, else first <c>.tar.gz</c>; version = name minus those suffixes, then minus <c>-patched</c>.</summary>
    public static BundledDshAsset? Select(IReadOnlyList<string>? entries)
    {
        if (entries is null)
        {
            return null;
        }
        var tarball = entries.FirstOrDefault(e => e.EndsWith(".tar.zst", StringComparison.Ordinal))
            ?? entries.FirstOrDefault(e => e.EndsWith(".tar.gz", StringComparison.Ordinal));
        if (tarball is null)
        {
            return null;
        }
        var version = RemoveSuffix(RemoveSuffix(RemoveSuffix(tarball, ".tar.zst"), ".tar.gz"), "-patched");
        return new BundledDshAsset(tarball, version, entries.Contains(tarball + ".sha256"));
    }

    /// <summary>Kotlin <c>trim().split(Regex("\\s+")).firstOrNull()</c>: the first token, "" for a blank file.</summary>
    public static string ParseSidecar(string text) => text.Trim().Split((char[]?)null, StringSplitOptions.None)[0];

    public static BundledDshPlan Plan(IBundledAssets assets, DshLayerInstaller installer)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(installer);
        var asset = Select(SafeList(assets, AssetDir));
        return new BundledDshPlan(asset, asset is null ? null : installer.Decide(asset.Version, allowDowngrade: false));
    }

    /// <summary>
    /// Installs the bundled DSH layer with version arbitration (installed-newer wins) and returns the legacy step text:
    /// <c>DSH &lt;v&gt; installed</c> / <c>DSH kept at &lt;v&gt;</c> / <c>DSH provision failed: &lt;code&gt; &lt;message&gt;</c> /
    /// <c>no DSH baseline in APK</c>. Streams straight from the asset (the legacy code first copies it to the cache).
    /// </summary>
    public static async Task<(string Step, DshLayerResult? Result)> ProvisionAsync(IBundledAssets assets, DshLayerInstaller installer, CancellationToken ct)
    {
        var plan = Plan(assets, installer);
        if (plan.Asset is not { } asset)
        {
            return (NoBaseline, null);
        }
        DshLayerResult r;
        try
        {
            string? sha = null;
            if (asset.HasSidecar && plan.WouldInstall)
            {
                using var reader = new StreamReader(assets.Open(asset.SidecarPath), Encoding.UTF8);
                sha = ParseSidecar(await reader.ReadToEndAsync(ct).ConfigureAwait(false));
            }
            r = await installer.InstallAsync(() => assets.Open(asset.AssetPath), sha, asset.Version, allowDowngrade: false, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            r = new DshLayerResult(false, DshLayerErrorCodes.InstallFailed, "cannot read the bundled DSH asset: " + e.Message, installer.InstalledVersion(), false, 0);
        }
        return (StepText(r), r);
    }

    public static string StepText(DshLayerResult r) =>
        r.Ok ? (r.Changed ? $"DSH {r.Version} installed" : $"DSH kept at {r.Version}") : $"DSH provision failed: {r.Code} {r.Message}";

    internal static IReadOnlyList<string> SafeList(IBundledAssets assets, string dir)
    {
        try
        {
            return assets.List(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string RemoveSuffix(string s, string suffix) =>
        s.EndsWith(suffix, StringComparison.Ordinal) ? s[..^suffix.Length] : s;
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
            var sentinel = Path.Combine(dir, ".dshbox", $"layer-{name}.sha256");
            if (File.Exists(sentinel))
            {
                if (!File.ReadAllText(sentinel, Encoding.UTF8).Trim().Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    broken.Add($"{name}:sha-mismatch");
                }
            }
            else
            {
                heals.Add($"{name}/.dshbox/layer-{name}.sha256");
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

public sealed record RuntimePrepareResult(bool Ok, string Code, string? Message, IReadOnlyList<string> Steps, string? DshVersion, bool DshChanged);

/// <summary>
/// C# <c>runtime.prepare</c>. Step 1 (APK-embedded full runtime): this APK only carries android-side + DSH, so — like
/// the legacy installer — there is nothing to do; an APK that does embed <c>runtime-profile.json</c> + <c>base.tar.*</c>
/// (or a <c>.dshb</c>) while the slot is incomplete is refused with <see cref="RuntimeErrorCodes.NotImplemented"/>
/// instead of being silently skipped. Step 2: the bundled DSH layer when the runtime is installed.
/// </summary>
public static class RuntimePreparer
{
    public const string NoEmbeddedRuntime = "no full layered runtime in APK (expected; base/node come from import)";
    public const string LayersMissing = "base/node layers missing: import a runtime bundle (runtime.importBundle)";

    /// <summary>Legacy <c>installIfAbsent</c> would install an APK-embedded runtime (which the C# port does not support).</summary>
    public static bool EmbeddedRuntimeWouldInstall(RuntimeLayout layout, IBundledAssets assets)
    {
        var slot = layout.RuntimeCurrentDir;
        if (File.Exists(Path.Combine(slot, RuntimeBundleLayout.ProfileName)) && Directory.Exists(Path.Combine(slot, "base")))
        {
            return false;
        }
        if (File.Exists(Path.Combine(slot, "debian", "opt", "dshapp", "start_dsh.sh")))
        {
            return false;
        }
        var names = BundledDsh.SafeList(assets, "runtime");
        var layered = names.Contains(RuntimeBundleLayout.ProfileName) && names.Any(n => n.StartsWith("base.tar.", StringComparison.Ordinal));
        return layered || names.Any(n => n.EndsWith(".dshb", StringComparison.Ordinal));
    }

    public static async Task<RuntimePrepareResult> PrepareAsync(RuntimeLayout layout, IBundledAssets assets, CancellationToken ct, Func<string, bool>? hostPathExists = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(assets);
        var installer = new DshLayerInstaller(layout, hostPathExists);
        if (EmbeddedRuntimeWouldInstall(layout, assets))
        {
            return new RuntimePrepareResult(false, RuntimeErrorCodes.NotImplemented,
                "the APK embeds a full layered runtime; the C# preparer does not install it (use the legacy implementation)", [], installer.InstalledVersion(), false);
        }
        var steps = new List<string> { NoEmbeddedRuntime };
        if (!RuntimeInstallCheck.Run(layout, heal: true).Installed)
        {
            steps.Add(LayersMissing);
            return new RuntimePrepareResult(true, RuntimeErrorCodes.Ok, null, steps, installer.InstalledVersion(), false);
        }
        var (step, r) = await BundledDsh.ProvisionAsync(assets, installer, ct).ConfigureAwait(false);
        steps.Add(step);
        // Legacy: a failed provision is a step text, not a failed prepare.
        return new RuntimePrepareResult(true, RuntimeErrorCodes.Ok, null, steps, r?.Version ?? installer.InstalledVersion(), r?.Changed == true);
    }
}
