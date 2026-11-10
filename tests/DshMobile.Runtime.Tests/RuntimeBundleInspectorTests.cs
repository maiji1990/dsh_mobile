using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DshMobile.Runtime.Bundle;
using Xunit.Abstractions;

namespace DshMobile.Runtime.Tests;

public sealed class RuntimeBundleInspectorTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dshm-bundle-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private static readonly byte[] Base = Encoding.UTF8.GetBytes("base-layer-bytes");
    private static readonly byte[] Node = Encoding.UTF8.GetBytes("node-layer-bytes");
    private static readonly byte[] Side = Encoding.UTF8.GetBytes("android-side-bytes");

    private static string Profile(string? baseSha = null, string? nodeSha = null, string? sideSha = null) => $$"""
        {"bundle":{"version":"0.1.0","arch":"arm64","compression":"zstd","zstd_level":19},
         "layers":[
          {"name":"base","version":"0.1.0","compression":"zstd","sha256":"{{baseSha ?? Sha(Base)}}","size_bytes":16,"env_file":".dshbox/env.d/base.sh","file":"base.tar.zst","deps":[]},
          {"name":"node","version":"24.19.0","compression":"zstd","sha256":"{{nodeSha ?? Sha(Node)}}","size_bytes":16,"env_file":".dshbox/env.d/node.sh","file":"node.tar.zst","deps":["base"]},
          {"name":"android-side","version":"0.1.0","compression":"zstd","sha256":"{{sideSha ?? Sha(Side)}}","size_bytes":18,"env_file":"","file":"android-side.tar.zst","deps":["base"]}],
         "assembly":["base","node","android-side"]}
        """;

    private string Zip(params (string Name, byte[] Data)[] entries)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, data) in entries)
        {
            using var s = zip.CreateEntry(name).Open();
            s.Write(data);
        }
        return path;
    }

    private static (string, byte[]) Text(string name, string text) => (name, Encoding.UTF8.GetBytes(text));

    private (string, byte[])[] Official(string? profile = null) =>
    [
        ("base.tar.zst", Base), Text("base.tar.zst.sha256", Sha(Base) + "  build/layers/base.tar.zst\n"),
        ("node.tar.zst", Node), Text("node.tar.zst.sha256", Sha(Node) + "  build/layers/node.tar.zst\n"),
        ("android-side.tar.zst", Side), Text("android-side.tar.zst.sha256", Sha(Side) + "  build/layers/android-side.tar.zst\n"),
        Text("runtime-profile.json", profile ?? Profile()),
    ];

    private static Task<BundleInspection> Inspect(string path) => RuntimeBundleInspector.InspectAsync(path, null, CancellationToken.None);

    [Fact]
    public async Task OfficialLayout_IsAcceptedAndEveryLayerVerified()
    {
        var r = await Inspect(Zip(Official()));
        Assert.True(r.Ok, r.Message);
        Assert.Equal(["base", "node", "android-side"], r.Layers.Select(l => l.Layer));
        Assert.All(r.Layers, l => Assert.True(l.Verified));
        Assert.Equal("0.1.0", r.Profile!.Version);
        Assert.Equal(["base"], r.Profile.Layer("node")!.Deps);
    }

    [Fact]
    public async Task CorruptLayer_IsSha256Mismatch()
    {
        var entries = Official();
        entries[2] = ("node.tar.zst", Encoding.UTF8.GetBytes("tampered"));
        var r = await Inspect(Zip(entries));
        Assert.False(r.Ok);
        Assert.Equal(BundleErrorCodes.Sha256Mismatch, r.ErrorCode);
        Assert.Contains("node", r.Message, StringComparison.Ordinal);
        // base verified first, then node failed: same order as upstream; the failing layer carries its actual hash.
        Assert.Equal(["base", "node"], r.Layers.Select(l => l.Layer));
        Assert.False(r.Layers[1].Verified);
    }

    [Fact]
    public async Task SidecarDisagreeingWithProfile_IsRejectedBeforeHashing()
    {
        var r = await Inspect(Zip(Official(Profile(baseSha: new string('a', 64)))));
        Assert.Equal(BundleErrorCodes.Sha256Mismatch, r.ErrorCode);
        Assert.Contains("sidecar does not match", r.Message, StringComparison.Ordinal);
        Assert.Empty(r.Layers);
    }

    [Fact]
    public async Task MissingProfile_IsNoProfile()
    {
        var r = await Inspect(Zip([.. Official().Where(e => e.Item1 != "runtime-profile.json")]));
        Assert.Equal(BundleErrorCodes.NoProfile, r.ErrorCode);
    }

    [Fact]
    public async Task UnparsableProfile_IsBadProfile()
    {
        Assert.Equal(BundleErrorCodes.BadProfile, (await Inspect(Zip(Official("{not json")))).ErrorCode);
        Assert.Equal(BundleErrorCodes.BadProfile, (await Inspect(Zip(Official("""{"layers":[]}""")))).ErrorCode);
    }

    [Fact]
    public async Task MissingLayer_IsMissingLayer()
    {
        var r = await Inspect(Zip([.. Official().Where(e => !e.Item1.StartsWith("android-side", StringComparison.Ordinal))]));
        Assert.Equal(BundleErrorCodes.MissingLayer, r.ErrorCode);
        Assert.Contains("android-side", r.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TraversalEntry_IsUnsafePath()
    {
        var r = await Inspect(Zip([.. Official(), Text("../../evil.sh", "x")]));
        Assert.Equal(BundleErrorCodes.UnsafePath, r.ErrorCode);
    }

    [Fact]
    public async Task EmptyZip_IsEmpty() => Assert.Equal(BundleErrorCodes.Empty, (await Inspect(Zip())).ErrorCode);

    [Fact]
    public async Task NotAZip_IsReportedNotThrown()
    {
        var path = Path.Combine(_dir, "garbage.zip");
        await File.WriteAllTextAsync(path, "this is not a zip");
        Assert.Equal(BundleErrorCodes.NotAZip, (await Inspect(path)).ErrorCode);
    }

    [Fact]
    public async Task WrappedInFolder_IsAccepted()
    {
        var r = await Inspect(Zip([.. Official().Select(e => ("runtime/" + e.Item1, e.Item2))]));
        Assert.True(r.Ok, r.Message);
    }

    [Fact]
    public async Task NoSidecarAndNoProfileHash_IsAcceptedButUnverified()
    {
        // Upstream accepts a layer with no declared hash at all; the C# result must say it was not verified.
        var entries = Official(Profile(baseSha: "", nodeSha: "", sideSha: "")).Where(e => !e.Item1.EndsWith(".sha256", StringComparison.Ordinal)).ToArray();
        var r = await Inspect(Zip(entries));
        Assert.True(r.Ok, r.Message);
        Assert.All(r.Layers, l => Assert.False(l.Verified));
    }

    [Fact]
    public void FirstToken_MatchesUpstreamSidecarParsing()
    {
        Assert.Equal("abc", RuntimeBundleInspector.FirstToken("  abc  build/layers/base.tar.zst\n"));
        Assert.Equal("abc", RuntimeBundleInspector.FirstToken("abc"));
        Assert.Equal("", RuntimeBundleInspector.FirstToken("   \n"));
    }

    /// <summary>
    /// The pinned real bundle (runtime-bundle/fetch-runtime.ps1) when it has been fetched: the archive digest must match
    /// runtime-assets.lock and every layer must verify. Reports (does not fake a pass) when the bundle is absent.
    /// </summary>
    [Fact]
    public async Task PinnedRealBundle_VerifiesWhenFetched()
    {
        const string name = "dshapp-runtime-debian-arm64-0.1.0.zip";
        var path = Repo.PathOf("artifacts", "runtime", "downloads", name);
        if (!File.Exists(path))
        {
            output.WriteLine($"NOT RUN: {path} not fetched (pwsh runtime-bundle/fetch-runtime.ps1)");
            return;
        }
        var locked = File.ReadLines(Repo.PathOf("runtime-bundle", "runtime-assets.lock"))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Single(p => p.Length >= 2 && p[0] == name)[1];
        await using (var fs = File.OpenRead(path))
        {
            Assert.Equal(locked, Convert.ToHexStringLower(await SHA256.HashDataAsync(fs)));
        }
        var r = await Inspect(path);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(3, r.Layers.Count);
        Assert.All(r.Layers, l => Assert.True(l.Verified, l.Layer));
        Assert.All(r.Layers, l => Assert.Equal(r.Profile!.Layer(l.Layer)!.Sha256, l.ActualSha256));
        output.WriteLine(string.Join("; ", r.Layers.Select(l => $"{l.Layer} {l.SizeBytes} {l.ActualSha256}")));
    }
}
