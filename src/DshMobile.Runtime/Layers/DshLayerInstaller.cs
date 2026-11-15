// Ported from DSHBox v1.5.1 (7c9d33c) sandbox-manager/src/main/java/com/dshbox/app/sandbox/DshLayer.kt
// (installedVersion, installFromBundle, versionFromPackage) and BundleManager.verifySha256 (GPL-3.0-or-later).
// Parity: tests/TestVectors/dsh-layer.json runs against both (native-bridge LegacyDshLayerParityTest,
// DshLayerParityTests).

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DshMobile.Runtime.Layers;

public static class DshLayerErrorCodes
{
    public const string BundleNotFound = "DSH_BUNDLE_NOT_FOUND";
    public const string Sha256Mismatch = "DSH_SHA256_MISMATCH";
    public const string BundleInvalid = "DSH_BUNDLE_INVALID";
    public const string InstallFailed = "DSH_INSTALL_FAILED";
}

/// <summary>Outcome of a DSH layer install. <paramref name="Version"/> is the installed version afterwards.</summary>
public sealed record DshLayerResult(bool Ok, string Code, string? Message, string? Version, bool Changed, long ElapsedMs);

/// <summary>What <see cref="DshLayerInstaller.Decide"/> would do with an incoming layer, before anything is read.</summary>
public sealed record DshLayerDecision(bool Install, string? Installed, string? Incoming);

/// <summary>
/// The standalone DSH layer at <c>runtime-current/dsh</c> (guest <c>/opt/dshapp/runtime</c>). Same semantics as the
/// legacy <c>DshLayer</c>:
/// <list type="bullet">
/// <item>version arbitration first: with an incoming version and no downgrade allowed, an installed version that is the
/// same or newer is kept (<c>Changed = false</c>) — before the checksum is even looked at;</item>
/// <item>then SHA-256 (a blank expected digest never matches), extraction into a private staging directory, and the
/// shape check (<c>node_modules/@deepseek-ai/dsh/lib/bin.js</c>) before the live layer is touched;</item>
/// <item>swap: drop <c>previous/dsh</c>, rename <c>dsh</c> → <c>previous/dsh</c> (when <c>previous/</c> does not exist the
/// rename fails and the old layer is <b>deleted</b> — inherited, pinned by the vectors), rename staging → <c>dsh</c>
/// (copy as fallback; on failure the previous layer is put back);</item>
/// <item><c>.dshmobile/version</c> = incoming version, else the discovered package version, else <c>unknown</c>.</item>
/// </list>
/// Only <c>dsh</c>, <c>previous/dsh</c> and the staging directory are touched; never <c>user-data</c>. The caller makes
/// sure no DSH process holds the layer. DSH's own files are never rewritten (Android link compatibility is the
/// runtime shim's job).
/// </summary>
public sealed class DshLayerInstaller(RuntimeLayout layout, Func<string, bool>? hostPathExists = null)
{
    public const string VersionFile = RuntimeConstants.LayerStateDir + "/version";
    public const string BinJs = "node_modules/@deepseek-ai/dsh/lib/bin.js";

    public string DshDir => layout.LayerDir("dsh");

    public string PreviousDshDir => Path.Combine(layout.RuntimeCurrentDir, "previous", "dsh");

    /// <summary>Distinct from the legacy <c>dsh-staging</c>, so the two installers never share a directory.</summary>
    public string StagingDir => Path.Combine(layout.RuntimeCurrentDir, "dshmobile-dsh-staging");

    /// <summary>Legacy <c>isInstalled</c>: bin.js or a version record.</summary>
    public bool IsInstalled => File.Exists(Path.Combine(DshDir, BinJs)) || File.Exists(Path.Combine(DshDir, VersionFile));

    /// <summary><c>.dshmobile/version</c> (trimmed, non-empty), else the package version, else null.</summary>
    public string? InstalledVersion()
    {
        var vf = Path.Combine(DshDir, VersionFile);
        if (File.Exists(vf))
        {
            var v = File.ReadAllText(vf, Encoding.UTF8).Trim();
            return v.Length > 0 ? v : null;
        }
        return VersionFromPackage(DshDir);
    }

    public DshLayerDecision Decide(string? newVersion, bool allowDowngrade)
    {
        var current = InstalledVersion();
        var keep = !allowDowngrade && current is not null && newVersion is not null && Versions.Compare(current, newVersion) >= 0;
        return new DshLayerDecision(!keep, current, newVersion);
    }

    public Task<DshLayerResult> InstallFromFileAsync(string bundlePath, string? expectedSha256, string? newVersion, bool allowDowngrade, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(bundlePath);
        return File.Exists(bundlePath)
            ? InstallAsync(() => File.OpenRead(bundlePath), expectedSha256, newVersion, allowDowngrade, ct)
            : Task.FromResult(new DshLayerResult(false, DshLayerErrorCodes.BundleNotFound, $"dsh bundle not found: {bundlePath}", InstalledVersion(), false, 0));
    }

    /// <summary>
    /// Installs from a re-openable source (an APK asset or a file): read once for the checksum, once for extraction
    /// (hashed again while extracting, so a source that changed in between is refused).
    /// </summary>
    public async Task<DshLayerResult> InstallAsync(Func<Stream> open, string? expectedSha256, string? newVersion, bool allowDowngrade, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(open);
        var sw = Stopwatch.StartNew();
        var decision = Decide(newVersion, allowDowngrade);
        if (!decision.Install)
        {
            return new DshLayerResult(true, "OK", $"installed {decision.Installed} >= incoming {newVersion}", decision.Installed, false, sw.ElapsedMilliseconds);
        }
        var previous = PreviousDshDir;
        var dsh = DshDir;
        var staging = StagingDir;
        try
        {
            if (expectedSha256 is not null && !await Sha256MatchesAsync(open, expectedSha256, ct).ConfigureAwait(false))
            {
                return Fail(DshLayerErrorCodes.Sha256Mismatch, "dsh bundle failed SHA-256 verification", sw);
            }

            // Stage 1: extract + validate away from the live layer.
            LayerInstaller.DeleteTree(staging);
            Directory.CreateDirectory(staging);
            LayerExtractResult extracted;
            string actual;
            var raw = open();
            await using (raw.ConfigureAwait(false))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var hashing = new LayerInstaller.HashingReadStream(raw, hash);
                extracted = await LayerExtractor.ExtractAsync(hashing, new DiskExtractionTarget(staging), ct, hostPathExists).ConfigureAwait(false);
                if (extracted.Ok)
                {
                    await hashing.CopyToAsync(Stream.Null, ct).ConfigureAwait(false);
                }
                actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            }
            if (!extracted.Ok)
            {
                return Fail(extracted.Code, extracted.Message, sw);
            }
            if (expectedSha256 is not null && !actual.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return Fail(DshLayerErrorCodes.Sha256Mismatch, "dsh bundle changed between verification and extraction", sw);
            }
            if (!File.Exists(Path.Combine(staging, BinJs)))
            {
                return Fail(DshLayerErrorCodes.BundleInvalid, "Selected file is not a valid DSH layer package (missing node_modules/@deepseek-ai/dsh/lib/bin.js)", sw);
            }
            var discovered = VersionFromPackage(staging);

            // Stage 2: old -> previous (single copy), staging -> dsh.
            if (LayerInstaller.Exists(previous))
            {
                LayerInstaller.DeleteTree(previous);
            }
            if (LayerInstaller.Exists(dsh) && !LayerInstaller.TryRename(dsh, previous))
            {
                LayerInstaller.DeleteTree(dsh); // legacy: "could not move dsh to previous; deleting old instead"
            }
            if (!LayerInstaller.TryRename(staging, dsh))
            {
                try
                {
                    LayerInstaller.CopyTree(staging, dsh);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (!LayerInstaller.Exists(dsh) && LayerInstaller.Exists(previous))
                    {
                        LayerInstaller.TryRename(previous, dsh);
                    }
                    return Fail(DshLayerErrorCodes.InstallFailed, "Cannot place new DSH layer (both rename and copy failed)", sw);
                }
            }

            // Stage 3: version record (legacy: failure only logged).
            var version = !string.IsNullOrWhiteSpace(newVersion) ? newVersion : discovered ?? "unknown";
            try
            {
                Directory.CreateDirectory(Path.Combine(dsh, RuntimeConstants.LayerStateDir));
                File.WriteAllText(Path.Combine(dsh, VersionFile), version); // writeText: no newline, no BOM
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
            return new DshLayerResult(true, "OK", null, InstalledVersion(), true, sw.ElapsedMilliseconds);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (!LayerInstaller.Exists(dsh) && LayerInstaller.Exists(previous))
            {
                LayerInstaller.TryRename(previous, dsh);
            }
            return Fail(DshLayerErrorCodes.InstallFailed, "dsh install failed: " + e.Message, sw);
        }
        finally
        {
            try
            {
                LayerInstaller.DeleteTree(staging);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private DshLayerResult Fail(string code, string? message, Stopwatch sw) =>
        new(false, code, message, InstalledVersion(), false, sw.ElapsedMilliseconds);

    /// <summary>Legacy <c>verifySha256</c>: a blank expected digest never matches; case-insensitive.</summary>
    private static async Task<bool> Sha256MatchesAsync(Func<Stream> open, string expected, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }
        var s = open();
        await using (s.ConfigureAwait(false))
        {
            var digest = await SHA256.HashDataAsync(s, ct).ConfigureAwait(false);
            return Convert.ToHexStringLower(digest).Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The real product version from <c>node_modules/@deepseek-ai/dsh/package.json</c>, else the layer-root stub
    /// <c>package.json</c>; both BOM-tolerant (the shipped stub starts with a UTF-8 BOM). Unreadable files are skipped.
    /// </summary>
    public static string? VersionFromPackage(string dshDirRoot)
    {
        foreach (var rel in (string[])["node_modules/@deepseek-ai/dsh/package.json", "package.json"])
        {
            var pkg = Path.Combine(dshDirRoot, rel);
            if (!File.Exists(pkg))
            {
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(pkg, Encoding.UTF8).TrimStart('﻿'),
                    new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("version", out var v))
                {
                    // org.json optString: strings as-is, other values by their JSON text.
                    var text = v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
                    if (!string.IsNullOrEmpty(text))
                    {
                        return text;
                    }
                }
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
            }
        }
        return null;
    }
}
