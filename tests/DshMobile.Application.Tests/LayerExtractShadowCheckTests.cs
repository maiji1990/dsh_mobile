using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DshMobile.Application.Shadow;
using DshMobile.Runtime;
using DshMobile.Runtime.Bundle;
using DshMobile.Runtime.Layers;

namespace DshMobile.Application.Tests;

/// <summary>
/// The layer-extraction shadow against a simulated legacy install: the "legacy" tree is produced by extracting the
/// same archive to disk (C# disk target, already proven equal to legacy on the shared vectors) plus the sentinel the
/// legacy importer writes; then the tree is tampered with to prove divergences are caught.
/// </summary>
public sealed class LayerExtractShadowCheckTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dshm-layershadow-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] Tar(params (string Name, string Content)[] files)
    {
        using var ms = new MemoryStream();
        using (var w = new TarWriter(ms, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                w.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                });
            }
        }
        return ms.ToArray();
    }

    private (string Zip, BundleInspection Inspection, RuntimeLayout Layout) Setup()
    {
        var layers = new Dictionary<string, byte[]>
        {
            ["base"] = Tar(("etc/debian_version", "13.1\n"), ("usr/bin/tool", "tool")),
            ["node"] = Tar(("usr/local/bin/node", "node")),
            ["android-side"] = Tar(("opt/side.txt", "side")),
        };
        var zipPath = Path.Combine(_dir, "bundle.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var (name, bytes) in layers)
            {
                using var s = zip.CreateEntry(name + ".tar").Open();
                s.Write(bytes);
            }
        }
        var verifications = layers.Select(kv => new LayerVerification(kv.Key, kv.Key + ".tar", kv.Value.Length,
            Convert.ToHexStringLower(SHA256.HashData(kv.Value)), Convert.ToHexStringLower(SHA256.HashData(kv.Value)), true)).ToList();
        var layout = new RuntimeLayout(Path.Combine(_dir, "files"), null, "/lib");
        return (zipPath, new BundleInspection(true, null, null, null, verifications), layout);
    }

    /// <summary>What the legacy importer leaves: extracted layer + `.dshbox/layer-NAME.sha256`.</summary>
    private static async Task InstallLikeLegacy(string zipPath, BundleInspection inspection, RuntimeLayout layout)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var l in inspection.Layers)
        {
            await using var s = await zip.GetEntry(l.ArchivePath)!.OpenAsync();
            var r = await LayerExtractor.ExtractAsync(s, new DiskExtractionTarget(layout.LayerDir(l.Layer)), CancellationToken.None, LayerExtractor.AndroidLikeHost);
            Assert.True(r.Ok, r.Message);
            Directory.CreateDirectory(Path.Combine(layout.LayerDir(l.Layer), ".dshbox"));
            await File.WriteAllTextAsync(Path.Combine(layout.LayerDir(l.Layer), ".dshbox", $"layer-{l.Layer}.sha256"), l.ExpectedSha256);
        }
    }

    private static Task<ShadowReport> Run(string zip, BundleInspection inspection, RuntimeLayout layout) =>
        LayerExtractShadowCheck.RunAsync(zip, inspection, layout, LayerExtractor.AndroidLikeHost, TimeProvider.System, CancellationToken.None);

    [Fact]
    public async Task IdenticalInstall_IsAgree()
    {
        var (zip, inspection, layout) = Setup();
        await InstallLikeLegacy(zip, inspection, layout);
        var r = await Run(zip, inspection, layout);
        Assert.True(r.Verdict == ShadowVerdict.Agree, r.Detail);
        Assert.Equal(LayerExtractShadowCheck.Slice, r.Slice);
        Assert.Contains("base:", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModifiedFile_IsDivergeWithPath()
    {
        var (zip, inspection, layout) = Setup();
        await InstallLikeLegacy(zip, inspection, layout);
        await File.WriteAllTextAsync(Path.Combine(layout.LayerDir("base"), "usr", "bin", "tool"), "tampered");
        var r = await Run(zip, inspection, layout);
        Assert.Equal(ShadowVerdict.Diverge, r.Verdict);
        Assert.Contains("content usr/bin/tool", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtraAndMissingFiles_AreDiverge()
    {
        var (zip, inspection, layout) = Setup();
        await InstallLikeLegacy(zip, inspection, layout);
        File.Delete(Path.Combine(layout.LayerDir("node"), "usr", "local", "bin", "node"));
        await File.WriteAllTextAsync(Path.Combine(layout.LayerDir("android-side"), "stray"), "x");
        var r = await Run(zip, inspection, layout);
        Assert.Equal(ShadowVerdict.Diverge, r.Verdict);
        Assert.Contains("missing File usr/local/bin/node", r.Detail, StringComparison.Ordinal);
        Assert.Contains("unexpected File stray", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingSentinel_IsDiverge()
    {
        var (zip, inspection, layout) = Setup();
        await InstallLikeLegacy(zip, inspection, layout);
        File.Delete(Path.Combine(layout.LayerDir("base"), ".dshbox", "layer-base.sha256"));
        Assert.Equal(ShadowVerdict.Diverge, (await Run(zip, inspection, layout)).Verdict);
    }

    [Fact]
    public async Task NothingInstalled_IsInconclusiveNotACrash()
    {
        var (zip, inspection, layout) = Setup();
        var r = await Run(zip, inspection, layout);
        Assert.Equal(ShadowVerdict.Inconclusive, r.Verdict);
    }
}
