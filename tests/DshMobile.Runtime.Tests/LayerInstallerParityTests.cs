using System.IO.Compression;
using System.Text.Json;
using DshMobile.Runtime.Layers;
using Xunit.Abstractions;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// C# half of the LayerInstaller contract: tests/TestVectors/install-scenarios.json is also run through the legacy
/// DSHBox DefaultSandboxManager.importRuntimeBundle (native-bridge LegacyBundleImportParityTest). The whole files/
/// tree after the import must be identical, including what is (not) kept in runtime-current/previous.
/// </summary>
public sealed class LayerInstallerParityTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dshm-install-").FullName;

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
                File.WriteAllText(path, v);
            }
        }
    }

    private static SortedDictionary<string, string> Snapshot(string root)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            map[rel] = Directory.Exists(path) ? "dir" : File.ReadAllText(path);
        }
        return map;
    }

    [Fact]
    public async Task Install_MatchesEverySharedScenario()
    {
        var scenarios = Repo.Vectors("install-scenarios.json").GetProperty("scenarios").EnumerateArray().ToList();
        Assert.True(scenarios.Count >= 4);
        foreach (var (s, i) in scenarios.Select((s, i) => (s, i)))
        {
            var label = s.GetProperty("name").GetString();
            var files = Path.Combine(_dir, "files-" + i);
            Directory.CreateDirectory(files);
            Materialize(files, s.GetProperty("before"));
            var installer = new LayerInstaller(new RuntimeLayout(files, null, "/lib"), LayerExtractor.AndroidLikeHost);
            var r = await installer.InstallAsync(Repo.PathOf("tests", "TestVectors", "bundles", s.GetProperty("bundle").GetString()!), null, CancellationToken.None);
            Assert.True(s.GetProperty("result").GetString() == r.Code, $"{label}: expected {s.GetProperty("result")}, got {r.Code} {r.Message}");
            var expected = new SortedDictionary<string, string>(
                s.GetProperty("after").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!), StringComparer.Ordinal);
            Assert.Equal(expected, Snapshot(files));
            output.WriteLine($"{label}: {r.Code} in {r.ElapsedMs} ms");
        }
    }

    [Fact]
    public async Task ProtectedUserData_IsNeverTouched_EvenWhenExtractionFails()
    {
        var files = Path.Combine(_dir, "files");
        Directory.CreateDirectory(Path.Combine(files, "user-data", ".dsh"));
        await File.WriteAllTextAsync(Path.Combine(files, "user-data", ".dsh", "credentials.yaml"), "secret");
        var r = await new LayerInstaller(new RuntimeLayout(files, null, "/lib"), LayerExtractor.AndroidLikeHost)
            .InstallAsync(Repo.PathOf("tests", "TestVectors", "bundles", "corrupt-node.zip"), null, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal("secret", await File.ReadAllTextAsync(Path.Combine(files, "user-data", ".dsh", "credentials.yaml")));
        Assert.False(Directory.Exists(Path.Combine(files, "dshmobile-bundle-staging")));
    }

    [Fact]
    public async Task NotAZip_IsRejectedBeforeAnythingIsWritten()
    {
        var files = Path.Combine(_dir, "files");
        Directory.CreateDirectory(files);
        var zip = Path.Combine(_dir, "x.zip");
        await File.WriteAllTextAsync(zip, "nope");
        var r = await new LayerInstaller(new RuntimeLayout(files, null, "/lib")).InstallAsync(zip, null, CancellationToken.None);
        Assert.Equal("BUNDLE_NOT_A_ZIP", r.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(files));
    }

    /// <summary>Real data: install the pinned bundle into a temp files/ dir and check every layer and sentinel.</summary>
    [Fact]
    public async Task PinnedRealBundle_InstallsAndMatchesDryRun()
    {
        var bundle = Repo.PathOf("artifacts", "runtime", "downloads", "dshapp-runtime-debian-arm64-0.1.0.zip");
        if (!File.Exists(bundle))
        {
            output.WriteLine($"NOT RUN: {bundle} not fetched");
            return;
        }
        if (OperatingSystem.IsWindows())
        {
            // Debian names contain ':' (NTFS alternate data streams) and differ only by case: a rootfs cannot be
            // materialized on Windows. Verified on Android / Linux instead (docs/COMPATIBILITY.md).
            output.WriteLine("NOT RUN: a Linux rootfs needs a case-sensitive POSIX filesystem");
            return;
        }
        if (!CanCreateSymlinks())
        {
            output.WriteLine("NOT RUN: this host cannot create symlinks (base layer has 768)");
            return;
        }
        var files = Path.Combine(_dir, "files");
        var layout = new RuntimeLayout(files, null, "/lib");
        var r = await new LayerInstaller(layout, LayerExtractor.AndroidLikeHost).InstallAsync(bundle, null, CancellationToken.None);
        Assert.True(r.Ok, $"{r.Code} {r.Message}");
        using var zip = ZipFile.OpenRead(bundle);
        foreach (var layer in r.Inspection!.Layers)
        {
            await using var s = await zip.GetEntry(layer.ZipEntryName!)!.OpenAsync();
            var dry = await LayerExtractor.ExtractAsync(s, DryRunExtractionTarget.Instance, CancellationToken.None, LayerExtractor.AndroidLikeHost);
            var installed = await LayerTreeScanner.ScanAsync(layout.LayerDir(layer.Layer), new HashSet<string> { ".dshmobile/layer-" + layer.Layer + ".sha256" }, CancellationToken.None);
            var diff = LayerManifest.Diff(dry.Entries.Where(e => e.Path != ".dshmobile" || dry.Entries.Any(x => x.Path == ".dshmobile")), installed.Where(e => e.Path != ".dshmobile" || dry.Entries.Any(x => x.Path == ".dshmobile")));
            Assert.True(diff.Count == 0, $"{layer.Layer}: {string.Join("; ", diff)}");
            Assert.Equal(layer.ExpectedSha256, await File.ReadAllTextAsync(Path.Combine(layout.LayerDir(layer.Layer), ".dshmobile", $"layer-{layer.Layer}.sha256")));
        }
        output.WriteLine($"installed in {r.ElapsedMs} ms");
    }

    private bool CanCreateSymlinks()
    {
        var link = Path.Combine(_dir, "probe-link");
        try
        {
            File.CreateSymbolicLink(link, "target");
            File.Delete(link);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
