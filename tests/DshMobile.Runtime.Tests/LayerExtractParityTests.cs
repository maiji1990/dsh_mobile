using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using DshMobile.Runtime.Layers;
using Xunit.Abstractions;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// C# half of the LayerInstaller extraction contract: tests/TestVectors/layer-extract.json is also run against the
/// legacy DSHBox BundleManager (native-bridge LegacyLayerExtractParityTest).
/// </summary>
public sealed class LayerExtractParityTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dshm-layers-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (UnauthorizedAccessException)
        {
            // read-only fixture dirs on Linux runners; temp cleanup is best effort
        }
    }

    private static IEnumerable<JsonElement> Cases() => Repo.Vectors("layer-extract.json").GetProperty("cases").EnumerateArray();

    private static JsonElement CSharpExpectation(JsonElement c) => c.TryGetProperty("csharp", out var o) ? o : c.GetProperty("expect");

    private static string Archive(JsonElement c) => Repo.PathOf("tests", "TestVectors", "layers", c.GetProperty("archive").GetString()!);

    private static List<LayerEntry> ExpectedEntries(JsonElement expect) =>
        expect.GetProperty("entries").EnumerateArray().Select(e => new LayerEntry(
            e.GetProperty("path").GetString()!,
            Enum.Parse<LayerEntryKind>(e.GetProperty("kind").GetString()!, ignoreCase: true),
            e.StringOrNull("mode"),
            e.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
            e.StringOrNull("sha256"),
            e.StringOrNull("link"))).ToList();

    [Fact]
    public async Task DryRun_MatchesEverySharedVector()
    {
        var cases = Cases().ToList();
        Assert.True(cases.Count >= 15, $"only {cases.Count} cases");
        foreach (var c in cases)
        {
            var label = c.GetProperty("name").GetString();
            var expect = CSharpExpectation(c);
            var r = await LayerExtractor.ExtractAsync(Archive(c), DryRunExtractionTarget.Instance, CancellationToken.None, LayerExtractor.AndroidLikeHost);
            Assert.True(expect.GetProperty("result").GetString() == r.Code, $"{label}: expected {expect.GetProperty("result")}, got {r.Code} ({r.Message})");
            if (!r.Ok)
            {
                continue;
            }
            Assert.Equal(ExpectedEntries(expect), r.Entries);
            Assert.True(expect.GetProperty("warnings").GetInt32() == r.Warnings.Count, $"{label}: warnings [{string.Join("; ", r.Warnings)}]");
        }
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
            output.WriteLine($"symlink cases NOT RUN on disk: {e.Message}");
            return false;
        }
    }

    /// <summary>Disk extraction then a scan of the result must equal the dry-run manifest (same decisions, real I/O).</summary>
    [Fact]
    public async Task DiskExtraction_EqualsDryRunManifest()
    {
        var symlinks = CanCreateSymlinks();
        var ran = 0;
        foreach (var (c, i) in Cases().Select((c, i) => (c, i)))
        {
            var label = c.GetProperty("name").GetString();
            var dry = await LayerExtractor.ExtractAsync(Archive(c), DryRunExtractionTarget.Instance, CancellationToken.None, LayerExtractor.AndroidLikeHost);
            if (!dry.Ok || (!symlinks && dry.Entries.Any(e => e.Kind == LayerEntryKind.Symlink)))
            {
                continue;
            }
            var dest = Path.Combine(_dir, "disk-" + i);
            var disk = await LayerExtractor.ExtractAsync(Archive(c), new DiskExtractionTarget(dest), CancellationToken.None, LayerExtractor.AndroidLikeHost);
            Assert.True(disk.Ok, $"{label}: {disk.Code} {disk.Message}");
            Assert.Equal(dry.Entries, disk.Entries);
            var scanned = await LayerTreeScanner.ScanAsync(dest, null, CancellationToken.None);
            var diff = LayerManifest.Diff(dry.Entries, scanned);
            Assert.True(diff.Count == 0, $"{label}: {string.Join("; ", diff)}");
            ran++;
        }
        Assert.True(ran >= 5, $"only {ran} disk cases ran");
    }

    [Fact]
    public async Task DiskTarget_RefusesNonEmptyRoot()
    {
        var dest = Path.Combine(_dir, "busy");
        Directory.CreateDirectory(dest);
        await File.WriteAllTextAsync(Path.Combine(dest, "x"), "x");
        Assert.Throws<IOException>(() => new DiskExtractionTarget(dest));
    }

    [Fact]
    public void Diff_ReportsEveryKindOfDifference()
    {
        LayerEntry F(string p, string sha, string? mode = "rw-") => new(p, LayerEntryKind.File, mode, 1, sha, null);
        var expected = new[] { F("a", "1"), F("b", "2"), F("m", "3", "rwx"), new LayerEntry("l", LayerEntryKind.Symlink, null, 0, null, "x") };
        var actual = new[] { F("a", "1"), F("b", "9"), F("m", "3", "rw-"), new LayerEntry("l", LayerEntryKind.Symlink, null, 0, null, "y"), F("extra", "4") };
        var d = LayerManifest.Diff(expected, actual);
        Assert.Contains(d, s => s.StartsWith("content b", StringComparison.Ordinal));
        Assert.Contains(d, s => s.StartsWith("mode m", StringComparison.Ordinal));
        Assert.Contains(d, s => s.StartsWith("link l", StringComparison.Ordinal));
        Assert.Contains(d, s => s.StartsWith("unexpected File extra", StringComparison.Ordinal));
        Assert.Empty(LayerManifest.Diff(expected, expected));
        Assert.Empty(LayerManifest.Diff([F("a", "1", "rwx")], [F("a", "1", null)])); // mode unreadable (Windows) is not a diff
    }

    [Theory]
    [InlineData(new byte[] { 0x28, 0xB5, 0x2F, 0xFD }, LayerArchiveFormat.Zstd)]
    [InlineData(new byte[] { 0x1F, 0x8B, 0x08 }, LayerArchiveFormat.Gzip)]
    [InlineData(new byte[] { 0x42, 0x5A, 0x68, 0x39 }, LayerArchiveFormat.Bzip2)]
    [InlineData(new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 }, LayerArchiveFormat.Xz)]
    [InlineData(new byte[] { 0x00 }, LayerArchiveFormat.Unknown)]
    public void Sniff_UsesUpstreamMagic(byte[] head, LayerArchiveFormat expected) => Assert.Equal(expected, LayerExtractor.Sniff(head));

    /// <summary>
    /// Real data: dry-run every layer of the pinned runtime bundle straight from the zip (non-seekable entry streams,
    /// zstd). Legacy imported this exact bundle on a real Android 17 device (TASK-004), so every layer must succeed.
    /// Reports (does not fake a pass) when the bundle has not been fetched.
    /// </summary>
    [Fact]
    public async Task PinnedRealBundle_EveryLayerExtractsInDryRun()
    {
        var path = Repo.PathOf("artifacts", "runtime", "downloads", "dshapp-runtime-debian-arm64-0.1.0.zip");
        if (!File.Exists(path))
        {
            output.WriteLine($"NOT RUN: {path} not fetched (pwsh runtime-bundle/fetch-runtime.ps1)");
            return;
        }
        using var zip = ZipFile.OpenRead(path);
        foreach (var layer in RuntimeConstants.LayerNames)
        {
            var entry = zip.GetEntry(layer + ".tar.zst");
            Assert.NotNull(entry);
            var sw = Stopwatch.StartNew();
            await using var s = await entry.OpenAsync();
            var r = await LayerExtractor.ExtractAsync(s, DryRunExtractionTarget.Instance, CancellationToken.None, LayerExtractor.AndroidLikeHost);
            Assert.True(r.Ok, $"{layer}: {r.Code} {r.Message}");
            Assert.Equal(LayerArchiveFormat.Zstd, r.Format);
            var kinds = r.Entries.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count());
            output.WriteLine($"{layer}: {r.Entries.Count} entries ({string.Join(", ", kinds.Select(k => $"{k.Key}={k.Value}"))}), {r.Bytes / 1024 / 1024} MiB, " +
                $"{r.Warnings.Count} warnings, {sw.Elapsed.TotalSeconds:F1} s");
            foreach (var w in r.Warnings.Take(5))
            {
                output.WriteLine("  warning: " + w);
            }
            Assert.NotEmpty(r.Entries);
        }
    }
}
