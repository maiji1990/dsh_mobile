using System.Text;
using System.Text.Json;
using DshMobile.Runtime.Layers;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// C# half of the DSH layer / install-check contract: tests/TestVectors/dsh-layer.json and runtime-installed.json are
/// also run through the legacy DSHBox DshLayer.installFromBundle and DefaultSandboxManager.isRuntimeInstalled
/// (native-bridge LegacyDshLayerParityTest). The whole files/ tree afterwards must be identical.
/// </summary>
public sealed class DshLayerParityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dshm-dshlayer-").FullName;

    public void Dispose() => LayerInstaller.DeleteTree(_dir);

    private static void Materialize(string root, JsonElement tree)
    {
        foreach (var p in tree.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var path = Path.Combine(root, p.Name);
            var v = p.Value.GetString()!;
            if (v == "dir")
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, Encoding.UTF8.GetBytes(v));
            }
        }
    }

    /// <summary>Like Kotlin <c>readText()</c>: a BOM stays a U+FEFF character.</summary>
    private static SortedDictionary<string, string> Snapshot(string root)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            map[rel] = Directory.Exists(path) ? "dir" : Encoding.UTF8.GetString(File.ReadAllBytes(path));
        }
        return map;
    }

    private static SortedDictionary<string, string> Tree(JsonElement s, string name) =>
        new(s.GetProperty(name).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!), StringComparer.Ordinal);

    [Fact]
    public async Task DshLayerInstall_MatchesEverySharedScenario()
    {
        var scenarios = Repo.Vectors("dsh-layer.json").GetProperty("scenarios").EnumerateArray().ToList();
        Assert.True(scenarios.Count >= 12);
        foreach (var (s, i) in scenarios.Select((s, i) => (s, i)))
        {
            var label = s.GetProperty("name").GetString();
            var files = Path.Combine(_dir, "files-" + i);
            Directory.CreateDirectory(files);
            Materialize(files, s.GetProperty("before"));
            var installer = new DshLayerInstaller(new RuntimeLayout(files, null, "/lib"), LayerExtractor.AndroidLikeHost);
            var r = await installer.InstallFromFileAsync(
                Repo.PathOf("tests", "TestVectors", "bundles", s.GetProperty("bundle").GetString()!),
                s.StringOrNull("sha256"), s.StringOrNull("newVersion"), s.GetProperty("allowDowngrade").GetBoolean(), CancellationToken.None);
            Assert.True(s.GetProperty("result").GetString() == r.Code, $"{label}: expected {s.GetProperty("result")}, got {r.Code} {r.Message}");
            Assert.True(s.GetProperty("changed").GetBoolean() == r.Changed, $"{label}: changed");
            Assert.True(s.StringOrNull("version") == r.Version, $"{label}: version {r.Version}");
            Assert.True(s.StringOrNull("version") == installer.InstalledVersion(), $"{label}: installed version");
            Assert.Equal(Tree(s, "after"), Snapshot(files));
        }
    }

    [Fact]
    public void InstallCheck_MatchesEverySharedScenario()
    {
        var scenarios = Repo.Vectors("runtime-installed.json").GetProperty("scenarios").EnumerateArray().ToList();
        Assert.True(scenarios.Count >= 8);
        foreach (var (s, i) in scenarios.Select((s, i) => (s, i)))
        {
            var label = s.GetProperty("name").GetString();
            var files = Path.Combine(_dir, "check-" + i);
            Directory.CreateDirectory(files);
            Materialize(files, s.GetProperty("before"));
            var layout = new RuntimeLayout(files, null, Path.Combine(_dir, "no-native-lib"));
            var dry = RuntimeInstallCheck.Run(layout, heal: false);
            Assert.Equal(Tree(s, "before"), Snapshot(files)); // the shadow form never writes
            var r = RuntimeInstallCheck.Run(layout, heal: true);
            Assert.True(s.GetProperty("installed").GetBoolean() == r.Installed, $"{label}: {r.Reason} {string.Join(",", r.Broken)}");
            Assert.Equal(dry.Installed, r.Installed);
            Assert.Equal(Tree(s, "after"), Snapshot(files));
        }
    }

    [Fact]
    public async Task UserData_IsNeverTouched_AndNoStagingIsLeft()
    {
        var files = Path.Combine(_dir, "files");
        Directory.CreateDirectory(Path.Combine(files, "user-data", ".dsh"));
        await File.WriteAllTextAsync(Path.Combine(files, "user-data", ".dsh", "credentials.yaml"), "secret");
        var installer = new DshLayerInstaller(new RuntimeLayout(files, null, "/lib"), LayerExtractor.AndroidLikeHost);
        foreach (var bundle in (string[])["dsh-good.tar", "dsh-invalid.tar"])
        {
            await installer.InstallFromFileAsync(Repo.PathOf("tests", "TestVectors", "bundles", bundle), null, null, true, CancellationToken.None);
            Assert.False(Directory.Exists(installer.StagingDir));
        }
        Assert.Equal("secret", await File.ReadAllTextAsync(Path.Combine(files, "user-data", ".dsh", "credentials.yaml")));
    }

    [Fact]
    public async Task SourceChangingBetweenChecksumAndExtraction_IsRefused()
    {
        var files = Path.Combine(_dir, "files");
        Directory.CreateDirectory(files);
        var good = await File.ReadAllBytesAsync(Repo.PathOf("tests", "TestVectors", "bundles", "dsh-good.tar"));
        var other = await File.ReadAllBytesAsync(Repo.PathOf("tests", "TestVectors", "bundles", "dsh-stub-only.tar"));
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(good));
        var opens = 0;
        var installer = new DshLayerInstaller(new RuntimeLayout(files, null, "/lib"), LayerExtractor.AndroidLikeHost);
        var r = await installer.InstallAsync(() => new MemoryStream(opens++ == 0 ? good : other), sha, "1.0.0", false, CancellationToken.None);
        Assert.Equal(DshLayerErrorCodes.Sha256Mismatch, r.Code);
        Assert.False(installer.IsInstalled);
        Assert.False(Directory.Exists(installer.StagingDir));
    }

    // ------------------------------------------------------------------ bundled asset selection (RuntimeController.kt)

    [Theory]
    [InlineData(new[] { "0.2.1-rc.5.tar.zst", "0.2.1-rc.5.tar.zst.sha256" }, "0.2.1-rc.5.tar.zst", "0.2.1-rc.5", true)]
    [InlineData(new[] { "1.0.0-patched.tar.gz" }, "1.0.0-patched.tar.gz", "1.0.0", false)]
    [InlineData(new[] { "a.tar.gz", "b.tar.zst" }, "b.tar.zst", "b", false)]
    [InlineData(new[] { "x-patched.tar.zst.sha256", "x-patched.tar.zst" }, "x-patched.tar.zst", "x", true)]
    public void BundledAsset_SelectionMatchesLegacy(string[] entries, string tarball, string version, bool sidecar)
    {
        var a = BundledDsh.Select(entries);
        Assert.NotNull(a);
        Assert.Equal(tarball, a.Tarball);
        Assert.Equal(version, a.Version);
        Assert.Equal(sidecar, a.HasSidecar);
        Assert.Equal($"dsh/{tarball}", a.AssetPath);
    }

    [Fact]
    public void BundledAsset_NoneWithoutATarball()
    {
        Assert.Null(BundledDsh.Select(null));
        Assert.Null(BundledDsh.Select([]));
        Assert.Null(BundledDsh.Select(["README", "x.tar", "x.zip"]));
    }

    [Theory]
    [InlineData("abc  build/x.tar.zst\n", "abc")]
    [InlineData("  ABC\n", "ABC")]
    [InlineData("\n", "")]
    public void Sidecar_FirstTokenLikeKotlin(string text, string expected) => Assert.Equal(expected, BundledDsh.ParseSidecar(text));

    private sealed class FakeAssets(Dictionary<string, byte[]> files) : IBundledAssets
    {
        public int Opens { get; private set; }

        public IReadOnlyList<string> List(string dir) =>
            files.Keys.Where(k => k.StartsWith(dir + "/", StringComparison.Ordinal) && !k[(dir.Length + 1)..].Contains('/'))
                .Select(k => k[(dir.Length + 1)..]).ToList();

        public Stream Open(string path)
        {
            Opens++;
            return files.TryGetValue(path, out var b) ? new MemoryStream(b) : throw new FileNotFoundException(path);
        }
    }

    private static FakeAssets Assets(string version, bool sidecar = true, string? sha = null)
    {
        var tar = File.ReadAllBytes(Repo.PathOf("tests", "TestVectors", "bundles", "dsh-good.tar"));
        var map = new Dictionary<string, byte[]> { [$"dsh/{version}.tar.zst"] = tar, ["runtime/android-side.tar.zst"] = [1] };
        if (sidecar)
        {
            map[$"dsh/{version}.tar.zst.sha256"] = Encoding.UTF8.GetBytes((sha ?? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(tar))) + "  out/dsh.tar.zst\n");
        }
        return new FakeAssets(map);
    }

    [Fact]
    public async Task Provision_InstallsThenKeeps_WithLegacyStepTexts()
    {
        var files = Path.Combine(_dir, "files");
        Directory.CreateDirectory(files);
        var installer = new DshLayerInstaller(new RuntimeLayout(files, null, "/lib"), LayerExtractor.AndroidLikeHost);
        var assets = Assets("1.2.0");
        Assert.True(BundledDsh.Plan(assets, installer).WouldInstall);
        var (step, r) = await BundledDsh.ProvisionAsync(assets, installer, CancellationToken.None);
        Assert.Equal("DSH 1.2.0 installed", step);
        Assert.True(r!.Changed);
        var opens = assets.Opens;
        (step, _) = await BundledDsh.ProvisionAsync(assets, installer, CancellationToken.None);
        Assert.Equal("DSH kept at 1.2.0", step);
        Assert.Equal(opens, assets.Opens); // kept: the 90 MB asset is not even opened
        Assert.False(BundledDsh.Plan(assets, installer).WouldInstall);
    }

    [Fact]
    public async Task Provision_BadSidecar_IsAFailedStepNotAFailedPrepare()
    {
        var files = Path.Combine(_dir, "files");
        Directory.CreateDirectory(files);
        var installer = new DshLayerInstaller(new RuntimeLayout(files, null, "/lib"), LayerExtractor.AndroidLikeHost);
        var (step, r) = await BundledDsh.ProvisionAsync(Assets("1.2.0", sha: new string('0', 64)), installer, CancellationToken.None);
        Assert.Equal("DSH provision failed: DSH_SHA256_MISMATCH dsh bundle failed SHA-256 verification", step);
        Assert.False(r!.Ok);
        Assert.Equal("no DSH baseline in APK", (await BundledDsh.ProvisionAsync(new FakeAssets([]), installer, CancellationToken.None)).Step);
    }

    private static void InstalledRuntime(string files)
    {
        var rc = Path.Combine(files, "runtime", "runtime-current");
        Directory.CreateDirectory(Path.Combine(rc, "base"));
        Directory.CreateDirectory(Path.Combine(rc, "android-side", "bin"));
        File.WriteAllText(Path.Combine(rc, "android-side", "bin", "proot"), "elf");
        File.WriteAllText(Path.Combine(rc, "runtime-profile.json"),
            """{"bundle":{"version":"1"},"layers":[{"name":"base","sha256":"aa"}],"assembly":["base"]}""");
    }

    [Fact]
    public async Task Prepare_RuntimeMissing_SkipsTheDshLayer()
    {
        var files = Path.Combine(_dir, "files");
        Directory.CreateDirectory(files);
        var layout = new RuntimeLayout(files, null, Path.Combine(_dir, "nolib"));
        var r = await RuntimePreparer.PrepareAsync(layout, Assets("1.2.0"), CancellationToken.None, LayerExtractor.AndroidLikeHost);
        Assert.True(r.Ok);
        Assert.Equal([RuntimePreparer.NoEmbeddedRuntime, RuntimePreparer.LayersMissing], r.Steps);
        Assert.False(new DshLayerInstaller(layout).IsInstalled);
    }

    [Fact]
    public async Task Prepare_InstalledRuntime_ProvisionsAndHealsTheSentinel()
    {
        var files = Path.Combine(_dir, "files");
        InstalledRuntime(files);
        var layout = new RuntimeLayout(files, null, Path.Combine(_dir, "nolib"));
        var r = await RuntimePreparer.PrepareAsync(layout, Assets("1.2.0"), CancellationToken.None, LayerExtractor.AndroidLikeHost);
        Assert.True(r.Ok);
        Assert.Equal([RuntimePreparer.NoEmbeddedRuntime, "DSH 1.2.0 installed"], r.Steps);
        Assert.Equal("1.2.0", r.DshVersion);
        Assert.True(r.DshChanged);
        Assert.Equal("aa", File.ReadAllText(Path.Combine(layout.LayerDir("base"), ".dshbox", "layer-base.sha256")));
    }

    [Fact]
    public async Task Prepare_ApkEmbeddingAFullRuntime_IsRefusedNotSkipped()
    {
        var files = Path.Combine(_dir, "files");
        Directory.CreateDirectory(files);
        var assets = new FakeAssets(new() { ["runtime/runtime-profile.json"] = [1], ["runtime/base.tar.zst"] = [1] });
        var r = await RuntimePreparer.PrepareAsync(new RuntimeLayout(files, null, "/lib"), assets, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(RuntimeErrorCodes.NotImplemented, r.Code);
        // ...but not once the slot is complete (legacy installIfAbsent: nothing to do).
        InstalledRuntime(files);
        Assert.False(RuntimePreparer.EmbeddedRuntimeWouldInstall(new RuntimeLayout(files, null, "/lib"), assets));
    }
}
