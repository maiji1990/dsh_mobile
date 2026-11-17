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

    // ------------------------------------------------------------------ imported archive naming (ADR-0027)

    [Theory]
    [InlineData("0.2.1-rc.5.tar.zst", "0.2.1-rc.5")]
    [InlineData("1.0.0-patched.tar.gz", "1.0.0")]
    [InlineData("dsh.tar.zst", null)]
    [InlineData("dir/2.0.0.tar.zst", "2.0.0")]
    [InlineData("x.tar", null)]
    [InlineData("x.zip", null)]
    [InlineData("x.tar.zst.sha256", null)]
    [InlineData(".tar.zst", null)]
    [InlineData("README", null)]
    public void VersionFromFileName_MatchesTheReleaseArtifactShape(string fileName, string? version) =>
        Assert.Equal(version, DshLayerInstaller.VersionFromFileName(fileName));
}
