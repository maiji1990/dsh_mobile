// Verification rules ported from DSHBox v1.5.1 (7c9d33c) DefaultSandboxManager.importRuntimeBundle /
// extractStagedLayers (GPL-3.0-or-later). Error codes are the upstream ones so shadow comparison is 1:1.

using System.IO.Compression;
using System.Security.Cryptography;

namespace DshMobile.Runtime.Bundle;

/// <summary>Upstream-compatible bundle error codes (DSHBox AppError codes), carried as <c>upstreamCode</c>.</summary>
public static class BundleErrorCodes
{
    public const string Empty = "BUNDLE_EMPTY";
    public const string UnsafePath = "BUNDLE_UNSAFE_PATH";
    public const string NoProfile = "BUNDLE_NO_PROFILE";
    public const string BadProfile = "BUNDLE_BAD_PROFILE";
    public const string MissingLayer = "BUNDLE_MISSING_LAYER";
    public const string Sha256Mismatch = "BUNDLE_SHA256_MISMATCH";
    /// <summary>C#-only: the file is not a readable ZIP (upstream surfaces this as an extraction exception).</summary>
    public const string NotAZip = "BUNDLE_NOT_A_ZIP";
}

/// <summary>One verified layer; <paramref name="ZipEntryName"/> is the raw entry name in the ZIP (null when not from a ZIP).</summary>
public sealed record LayerVerification(string Layer, string ArchivePath, long SizeBytes, string? ExpectedSha256, string ActualSha256, bool Verified, string? ZipEntryName = null);

public sealed record BundleInspection(
    bool Ok,
    string? ErrorCode,
    string? Message,
    RuntimeProfile? Profile,
    IReadOnlyList<LayerVerification> Layers,
    string? ProfileEntryName = null)
{
    internal static BundleInspection Fail(string code, string message, RuntimeProfile? profile = null, IReadOnlyList<LayerVerification>? layers = null) =>
        new(false, code, message, profile, layers ?? []);
}

/// <summary>
/// Read-only pre-check of a runtime-bundle ZIP: layout, profile and per-layer SHA-256, applying exactly the upstream
/// acceptance rules (in the same order) without extracting anything. Used as the shadow check next to the legacy
/// import (PR-02) and later as the first stage of the C# LayerInstaller.
/// </summary>
public static class RuntimeBundleInspector
{
    private const int BufferSize = 1 << 20;

    public static async Task<BundleInspection> InspectAsync(string zipPath, IProgress<RuntimeProgress>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(zipPath);
        ZipArchive zip;
        try
        {
            zip = await ZipFile.OpenReadAsync(zipPath, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException e)
        {
            return BundleInspection.Fail(BundleErrorCodes.NotAZip, e.Message);
        }
        await using (zip.ConfigureAwait(false))
        {
            return await InspectAsync(zip, progress, ct).ConfigureAwait(false);
        }
    }

    public static async Task<BundleInspection> InspectAsync(ZipArchive zip, IProgress<RuntimeProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(zip);
        var entries = zip.Entries;
        if (entries.Count == 0)
        {
            return BundleInspection.Fail(BundleErrorCodes.Empty, "Archive contains no files");
        }

        var layout = RuntimeBundleLayout.Analyze(entries.Select(e => e.FullName).ToList());
        if (layout.IsUnsafe)
        {
            return BundleInspection.Fail(BundleErrorCodes.UnsafePath, $"Archive contains illegal path (..), blocked: {layout.UnsafeEntry}");
        }

        // stripped path -> entry. Upstream writes entries to staging in zip order, so for a duplicate name the last
        // one is what it verifies; keep that (parity over strictness for this read-only check).
        var byTarget = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (layout.Targets.TryGetValue(e.FullName, out var stripped))
            {
                byTarget[stripped] = e;
            }
        }

        if (layout.ProfilePath is null || !byTarget.TryGetValue(layout.ProfilePath, out var profileEntry))
        {
            return BundleInspection.Fail(BundleErrorCodes.NoProfile, "Runtime bundle missing runtime-profile.json (cannot verify layer integrity, import rejected)");
        }
        var profile = RuntimeProfile.Parse(await ReadTextAsync(profileEntry, ct).ConfigureAwait(false));
        if (profile is null)
        {
            return BundleInspection.Fail(BundleErrorCodes.BadProfile, "runtime-profile.json cannot be parsed");
        }

        var results = new List<LayerVerification>();
        var total = RuntimeConstants.LayerNames.Sum(l => layout.Archives.TryGetValue(l, out var a) && byTarget.TryGetValue(a, out var e) ? e.Length : 0);
        long done = 0;
        foreach (var layer in RuntimeConstants.LayerNames)
        {
            if (!layout.Archives.TryGetValue(layer, out var archivePath) || !byTarget.TryGetValue(archivePath, out var archive))
            {
                return BundleInspection.Fail(BundleErrorCodes.MissingLayer, $"Runtime bundle missing {layer} layer archive (<layer>.tar[.zst/.gz/.bz2/.xz])", profile, results);
            }
            var inProfile = NullIfBlank(profile.Layer(layer)?.Sha256);
            string? inSidecar = null;
            if (layout.Sidecars.TryGetValue(layer, out var sidecarPath) && byTarget.TryGetValue(sidecarPath, out var sidecarEntry))
            {
                inSidecar = NullIfBlank(FirstToken(await ReadTextAsync(sidecarEntry, ct).ConfigureAwait(false)));
            }
            if (inProfile is not null && inSidecar is not null && !inProfile.Equals(inSidecar, StringComparison.OrdinalIgnoreCase))
            {
                return BundleInspection.Fail(BundleErrorCodes.Sha256Mismatch, $"Layer {layer} .sha256 sidecar does not match runtime-profile.json declaration", profile, results);
            }
            var expected = inSidecar ?? inProfile;
            var baseline = done;
            var actual = await Sha256Async(archive, n => progress?.Report(new RuntimeProgress($"verify {layer}", baseline + n, total)), ct).ConfigureAwait(false);
            done += archive.Length;
            var verified = expected is not null && actual.Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase);
            results.Add(new LayerVerification(layer, archivePath, archive.Length, expected, actual, verified, archive.FullName));
            if (expected is not null && !verified)
            {
                return BundleInspection.Fail(BundleErrorCodes.Sha256Mismatch, $"Layer {layer} SHA-256 verification failed", profile, results);
            }
        }
        return new BundleInspection(true, null, null, profile, results, profileEntry.FullName);
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>Upstream: <c>readText().trim().split(Regex("\\s+")).firstOrNull()</c>.</summary>
    internal static string FirstToken(string text)
    {
        var t = text.Trim();
        var i = 0;
        while (i < t.Length && !char.IsWhiteSpace(t[i]))
        {
            i++;
        }
        return t[..i];
    }

    private static async Task<string> ReadTextAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        var s = await entry.OpenAsync(ct).ConfigureAwait(false);
        await using (s.ConfigureAwait(false))
        {
            using var reader = new StreamReader(s);
            return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task<string> Sha256Async(ZipArchiveEntry entry, Action<long> onBytes, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long read = 0;
        var s = await entry.OpenAsync(ct).ConfigureAwait(false);
        await using (s.ConfigureAwait(false))
        {
            int n;
            while ((n = await s.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, n);
                read += n;
                onBytes(read);
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
