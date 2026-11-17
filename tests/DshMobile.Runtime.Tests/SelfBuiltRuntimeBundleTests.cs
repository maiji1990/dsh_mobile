using DshMobile.Runtime.Bundle;
using DshMobile.Runtime.Layers;
using Xunit.Abstractions;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// ADR-0020: a bundle built by <c>runtime-bundle/build-runtime.sh</c> goes through the app's own importer. Runs only when
/// the build output exists (<c>artifacts/runtime-build/dist</c>); reports NOT RUN otherwise instead of passing silently,
/// and fails when <c>DSHM_REQUIRE_SELF_BUILT=1</c> (runtime-layers.yml verify job).
/// </summary>
public sealed class SelfBuiltRuntimeBundleTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dshm-selfbuilt-").FullName;

    public void Dispose() => LayerInstaller.DeleteTree(_dir);

    private string? BuiltBundle()
    {
        var dist = Repo.PathOf("artifacts", "runtime-build", "dist");
        var zip = Directory.Exists(dist) ? Directory.EnumerateFiles(dist, "dshmobile-runtime-*.zip").OrderDescending().FirstOrDefault() : null;
        if (zip is null)
        {
            // The runtime-layers workflow sets this so a missing build output fails instead of passing as "NOT RUN".
            Assert.True(Environment.GetEnvironmentVariable("DSHM_REQUIRE_SELF_BUILT") != "1", $"DSHM_REQUIRE_SELF_BUILT=1 but no dshmobile-runtime-*.zip in {dist}");
            output.WriteLine($"NOT RUN: no dshmobile-runtime-*.zip in {dist} (bash runtime-bundle/build-runtime.sh)");
        }
        return zip;
    }

    private static string LockValue(string key) =>
        File.ReadLines(Repo.PathOf("runtime-bundle", "runtime-build.lock")).Single(l => l.StartsWith(key + "=", StringComparison.Ordinal))[(key.Length + 1)..].Trim();

    [Fact]
    public async Task SelfBuiltBundle_InstallsWithTheAppsImporter()
    {
        if (BuiltBundle() is not { } zip)
        {
            return;
        }
        var layout = new RuntimeLayout(Path.Combine(_dir, "files"), null, Path.Combine(_dir, "lib"));

        var r = await new LayerInstaller(layout, LayerExtractor.AndroidLikeHost).InstallAsync(zip, null, CancellationToken.None);

        Assert.True(r.Ok, $"{r.Code}: {r.Message}");
        var profile = RuntimeProfile.Parse(File.ReadAllText(Path.Combine(layout.RuntimeCurrentDir, RuntimeBundleLayout.ProfileName)))!;
        Assert.Equal(LockValue("BUNDLE_VERSION"), profile.Version);
        Assert.Equal("arm64", profile.Arch);
        foreach (var name in RuntimeConstants.LayerNames)
        {
            var layer = profile.Layer(name)!;
            Assert.Equal($".dshmobile/env.d/{name}.sh", layer.EnvFile);
            Assert.True(File.Exists(Path.Combine(layout.LayerDir(name), layer.EnvFile)), layer.EnvFile);
            Assert.Equal(layer.Sha256, File.ReadAllText(Path.Combine(layout.LayerDir(name), RuntimeConstants.LayerStateDir, $"layer-{name}.sha256")).Trim());
            Assert.False(Directory.Exists(Path.Combine(layout.LayerDir(name), ".dshbox")), $"{name} still has .dshbox");
        }
        Assert.True(File.Exists(Path.Combine(layout.LayerDir("base"), "etc", "debian_version")));
        Assert.Equal("localhost", File.ReadAllText(Path.Combine(layout.LayerDir("base"), "etc", "hostname")).Trim());
        Assert.True(File.Exists(Path.Combine(layout.LayerDir("node"), "bin", "node")));
        Assert.True(File.Exists(Path.Combine(layout.LayerDir("android-side"), "bin", "proot")));
        Assert.True(RuntimeInstallCheck.Run(layout, heal: false).Installed);

        // The DSH layer is built by the same script; it goes through the app's DSH installer (checksum from SHA256SUMS).
        var dist = Path.GetDirectoryName(zip)!;
        var dshVersion = LockValue("DSH_VERSION");
        var dshTar = Path.Combine(dist, $"{dshVersion}.tar.zst");
        var sha = File.ReadLines(Path.Combine(dist, "SHA256SUMS")).Select(l => l.Split("  ")).Single(p => p[1] == Path.GetFileName(dshTar))[0];
        var dshInstaller = new DshLayerInstaller(layout, LayerExtractor.AndroidLikeHost);
        var d = await dshInstaller.InstallFromFileAsync(dshTar, sha, dshVersion, allowDowngrade: false, CancellationToken.None);
        Assert.True(d.Ok, $"{d.Code}: {d.Message}");
        Assert.Equal(dshVersion, dshInstaller.InstalledVersion());
        Assert.True(File.Exists(Path.Combine(dshInstaller.DshDir, DshLayerInstaller.BinJs)));
        output.WriteLine($"{Path.GetFileName(zip)}: {string.Join("; ", r.Layers.Select(l => $"{l.Format} ok={l.Ok} {l.Bytes}B"))}, {r.ElapsedMs} ms");
    }
}
