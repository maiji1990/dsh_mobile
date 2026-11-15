using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DshMobile.Runtime;
using DshMobile.Runtime.Bundle;
using DshMobile.Runtime.Layers;

namespace DshMobile.Application.Shadow;

/// <summary>
/// Post-install check of the LayerInstaller (PR-02; a legacy-vs-C# shadow until PR-10): after a bundle is installed, the
/// C# <see cref="LayerExtractor"/> dry-runs every layer archive straight from the same ZIP (nothing written) and the
/// result is compared with what was actually put in <c>runtime-current/&lt;layer&gt;</c>. Must run before anything else
/// touches the layers (the coordinator holds its operation lock).
/// </summary>
public static class LayerExtractShadowCheck
{
    public const string Slice = "runtime.layer-extract";

    /// <summary>The report name used for the check after a C# import (the only kind since PR-10).</summary>
    public const string InstallSlice = "runtime.layer-install";

    public static async Task<ShadowReport> RunAsync(string zipPath, BundleInspection inspection, RuntimeLayout layout, Func<string, bool>? hostPathExists, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(layout);
        var sw = Stopwatch.StartNew();
        var details = new List<string>();
        var diverged = false;
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var layer in inspection.Layers)
            {
                var entry = FindEntry(zip, layer.ArchivePath);
                if (entry is null)
                {
                    return Report(ShadowVerdict.Inconclusive, $"{layer.Layer}: archive {layer.ArchivePath} not found in zip", sw, time);
                }
                LayerExtractResult extracted;
                var s = await entry.OpenAsync(ct).ConfigureAwait(false);
                await using (s.ConfigureAwait(false))
                {
                    extracted = await LayerExtractor.ExtractAsync(s, DryRunExtractionTarget.Instance, ct, hostPathExists).ConfigureAwait(false);
                }
                if (!extracted.Ok)
                {
                    // The legacy importer accepted this layer: a C# rejection is a divergence, except for formats the
                    // port knowingly does not support yet.
                    if (extracted.Code == LayerErrorCodes.FormatUnsupported)
                    {
                        return Report(ShadowVerdict.Inconclusive, $"{layer.Layer}: {extracted.Message}", sw, time);
                    }
                    diverged = true;
                    details.Add($"{layer.Layer}: legacy installed it, csharp rejected {extracted.Code}: {extracted.Message}");
                    continue;
                }
                var expected = WithSentinel(extracted.Entries, layer);
                var actual = await LayerTreeScanner.ScanAsync(layout.LayerDir(layer.Layer), null, ct).ConfigureAwait(false);
                var diff = LayerManifest.Diff(expected, actual);
                if (diff.Count > 0)
                {
                    diverged = true;
                    details.Add($"{layer.Layer}: {diff.Count}{(diff.Count >= 20 ? "+" : "")} differences: {string.Join("; ", diff.Take(5))}");
                }
                else
                {
                    details.Add($"{layer.Layer}: {expected.Count} entries identical");
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Report(ShadowVerdict.Inconclusive, "shadow I/O error: " + e.Message, sw, time);
        }
        return Report(diverged ? ShadowVerdict.Diverge : ShadowVerdict.Agree, string.Join(" | ", details), sw, time);
    }

    /// <summary>Legacy writes <c>.dshmobile/layer-&lt;name&gt;.sha256</c> (the recorded digest, no newline) after extracting.</summary>
    internal static List<LayerEntry> WithSentinel(IReadOnlyList<LayerEntry> entries, LayerVerification layer)
    {
        var list = entries.ToList();
        if (layer.ExpectedSha256 is not { } recorded)
        {
            return list;
        }
        if (!list.Any(e => e.Path == RuntimeConstants.LayerStateDir))
        {
            list.Add(new LayerEntry(RuntimeConstants.LayerStateDir, LayerEntryKind.Dir, null, 0, null, null));
        }
        var bytes = Encoding.UTF8.GetBytes(recorded);
        var path = $"{RuntimeConstants.LayerStateDir}/layer-{layer.Layer}.sha256";
        list.RemoveAll(e => e.Path == path);
        list.Add(new LayerEntry(path, LayerEntryKind.File, null, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), null));
        return list;
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string strippedPath)
    {
        // The inspector reports paths after stripping one common top-level folder; match on that suffix.
        foreach (var e in zip.Entries)
        {
            var n = e.FullName.Replace('\\', '/');
            if (n.StartsWith("./", StringComparison.Ordinal))
            {
                n = n[2..];
            }
            if (n == strippedPath || n.EndsWith("/" + strippedPath, StringComparison.Ordinal))
            {
                return e;
            }
        }
        return null;
    }

    private static ShadowReport Report(ShadowVerdict verdict, string detail, Stopwatch sw, TimeProvider time) =>
        new(Slice, time.GetUtcNow(), verdict, true, null, verdict != ShadowVerdict.Diverge, null, detail, sw.ElapsedMilliseconds);
}
