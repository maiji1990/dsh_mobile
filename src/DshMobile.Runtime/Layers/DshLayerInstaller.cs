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
/// Outcome of <see cref="DshLayerInstaller.Recover"/>: <paramref name="Action"/> is <c>none</c> (no interrupted install),
/// <c>restored</c> (the layer from before the interrupted install is back), <c>completed</c> (the new layer had been
/// placed; only the clean-up was left) or <c>failed</c> (the journal is kept, the next call tries again).
/// </summary>
public sealed record DshLayerRecovery(bool Ok, string Action, string? Message);

/// <summary>
/// The filesystem primitives the layer swap uses. Production uses the real ones; tests inject failures (rename refused,
/// disk full while copying, process death at any step) to prove the old layer survives every one of them.
/// </summary>
internal sealed class LayerFileOps
{
    public static LayerFileOps Real { get; } = new();

    public Func<string, string, bool> Rename { get; init; } = LayerInstaller.TryRename;

    public Action<string, string> CopyTree { get; init; } = LayerInstaller.CopyTree;

    public Action<string> DeleteTree { get; init; } = LayerInstaller.DeleteTree;

    /// <summary>Durable replace: temp file in the same directory, fsync, rename over the target.</summary>
    public Action<string, string> WriteFile { get; init; } = WriteDurably;

    public Action<string> DeleteFile { get; init; } = File.Delete;

    private static void WriteDurably(string path, string content)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(Encoding.UTF8.GetBytes(content));
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>
/// The standalone DSH layer at <c>runtime-current/dsh</c> (guest <c>/opt/dshapp/runtime</c>). Same semantics as the
/// legacy <c>DshLayer</c>:
/// <list type="bullet">
/// <item>version arbitration first: with an incoming version and no downgrade allowed, an installed version that is the
/// same or newer is kept (<c>Changed = false</c>) — before the checksum is even looked at;</item>
/// <item>then SHA-256 (a blank expected digest never matches), extraction into a private staging directory, and the
/// shape check (<c>node_modules/@deepseek-ai/dsh/lib/bin.js</c>) before the live layer is touched;</item>
/// <item>swap (a journaled transaction, <see cref="JournalFile"/>): the current <c>dsh</c> is renamed aside to
/// <see cref="AsideDir"/> (same parent, so the rename cannot hit a missing directory; if it still fails the install is
/// refused and nothing changes), staging is renamed to <c>dsh</c> (copy as fallback), the version is recorded, and only
/// then is the old layer retired: to <c>previous/dsh</c> when <c>previous/</c> exists (an older <c>previous/dsh</c> is
/// dropped), otherwise deleted. If the new layer cannot be placed, the old one is renamed back. The end states equal
/// the legacy <c>DshLayer</c> (pinned by the vectors), but the old layer is never deleted before the new one is in
/// place — the legacy code deleted it right away when <c>previous/</c> did not exist;</item>
/// <item>an install interrupted by process death is finished or undone by <see cref="Recover"/> (run first by every
/// install, and by the coordinator before a start);</item>
/// <item><c>.dshmobile/version</c> = incoming version, else the discovered package version, else <c>unknown</c>.</item>
/// </list>
/// Only <c>dsh</c>, <c>previous/dsh</c>, the aside / staging directories and the journal are touched; never
/// <c>user-data</c>. <c>previous/</c> itself is never created here (whether body imports keep a rollback copy is a
/// separate decision, MIGRATION §7). The caller makes sure no DSH process holds the layer. DSH's own files are never
/// rewritten (Android link compatibility is the runtime shim's job).
/// </summary>
public sealed class DshLayerInstaller(RuntimeLayout layout, Func<string, bool>? hostPathExists = null)
{
    public const string VersionFile = RuntimeConstants.LayerStateDir + "/version";
    public const string BinJs = "node_modules/@deepseek-ai/dsh/lib/bin.js";

    public string DshDir => layout.LayerDir("dsh");

    public string PreviousDshDir => Path.Combine(layout.RuntimeCurrentDir, "previous", "dsh");

    /// <summary>Distinct from the legacy <c>dsh-staging</c>, so the two installers never share a directory.</summary>
    public string StagingDir => Path.Combine(layout.RuntimeCurrentDir, "dshmobile-dsh-staging");

    /// <summary>Where the current layer waits while the new one is placed (a sibling of <c>dsh</c>).</summary>
    public string AsideDir => Path.Combine(layout.RuntimeCurrentDir, "dshmobile-dsh-old");

    /// <summary>Write-ahead record of an install in progress (<c>phase=begin|placed</c>); absent when none is.</summary>
    public string JournalFile => Path.Combine(layout.RuntimeCurrentDir, "dshmobile-dsh-install.journal");

    /// <summary>The filesystem primitives of the swap (tests inject faults).</summary>
    internal LayerFileOps Ops { get; init; } = LayerFileOps.Real;

    private const string PhaseBegin = "begin";
    private const string PhasePlaced = "placed";

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
        var recovery = Recover();
        if (!recovery.Ok)
        {
            return Fail(DshLayerErrorCodes.InstallFailed, "an earlier interrupted DSH install could not be recovered: " + recovery.Message, sw);
        }
        var decision = Decide(newVersion, allowDowngrade);
        if (!decision.Install)
        {
            return new DshLayerResult(true, "OK", $"installed {decision.Installed} >= incoming {newVersion}", decision.Installed, false, sw.ElapsedMilliseconds);
        }
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

            // Stage 2 (journaled): old -> aside, staging -> dsh, version record, then the old layer is retired.
            var version = !string.IsNullOrWhiteSpace(newVersion) ? newVersion : discovered ?? "unknown";
            var hadOld = LayerInstaller.Exists(dsh);
            WriteJournal(PhaseBegin, hadOld);
            if (hadOld && !Ops.Rename(dsh, AsideDir))
            {
                Ops.DeleteFile(JournalFile);
                return Fail(DshLayerErrorCodes.InstallFailed, "Cannot move the current DSH layer aside; nothing was changed", sw);
            }
            if (!Place(staging, dsh))
            {
                var undo = Recover();
                return Fail(DshLayerErrorCodes.InstallFailed, "Cannot place new DSH layer (both rename and copy failed); " +
                    (undo.Ok ? "the previous layer was kept" : "restoring the previous layer failed, retried on the next install or start: " + undo.Message), sw);
            }

            // Stage 3: version record (legacy: failure only logged), then commit.
            try
            {
                Directory.CreateDirectory(Path.Combine(dsh, RuntimeConstants.LayerStateDir));
                Ops.WriteFile(Path.Combine(dsh, VersionFile), version); // no newline, no BOM
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
            WriteJournal(PhasePlaced, hadOld);
            var done = Recover(); // retires the old layer and drops the journal
            return new DshLayerResult(true, "OK", done.Ok ? null : "new layer installed; clean-up of the old layer is retried later: " + done.Message,
                InstalledVersion(), true, sw.ElapsedMilliseconds);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            var undo = Recover();
            return Fail(DshLayerErrorCodes.InstallFailed, "dsh install failed: " + e.Message + (undo.Ok ? "" : "; recovery pending: " + undo.Message), sw);
        }
        finally
        {
            try
            {
                Ops.DeleteTree(staging);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Staging → <c>dsh</c> by rename, else by copy; a partial copy is removed again. False when neither worked.</summary>
    private bool Place(string staging, string dsh)
    {
        if (Ops.Rename(staging, dsh))
        {
            return true;
        }
        try
        {
            Ops.CopyTree(staging, dsh);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false; // Recover() removes the partial copy and brings the old layer back
        }
    }

    /// <summary>
    /// Finishes or undoes an install that did not complete (process death, a failed placement, a failed clean-up),
    /// according to <see cref="JournalFile"/>:
    /// <list type="bullet">
    /// <item><c>begin</c>: the new layer was not committed. Whatever sits at <c>dsh</c> is dropped if the old layer is
    /// aside (or if there was no old layer) and the old layer is renamed back — the installed version is the one from
    /// before the install;</item>
    /// <item><c>placed</c>: the new layer and its version record are in place; the old layer is retired (to
    /// <c>previous/dsh</c> when <c>previous/</c> exists, else deleted).</item>
    /// </list>
    /// Then the journal is removed. Staging leftovers are always removed; without a journal nothing else is touched,
    /// except an aside layer with no <c>dsh</c> next to it, which is put back. The caller makes sure no DSH process holds the layer.
    /// </summary>
    public DshLayerRecovery Recover()
    {
        var dsh = DshDir;
        var aside = AsideDir;
        try
        {
            if (LayerInstaller.Exists(StagingDir))
            {
                Ops.DeleteTree(StagingDir); // an extraction the process did not survive
            }
            var journal = ReadJournal();
            if (journal is null)
            {
                if (LayerInstaller.Exists(aside) && !LayerInstaller.Exists(dsh))
                {
                    return Ops.Rename(aside, dsh)
                        ? new DshLayerRecovery(true, "restored", "aside layer without a journal put back")
                        : new DshLayerRecovery(false, "failed", "cannot rename the aside layer back to dsh");
                }
                return new DshLayerRecovery(true, "none", null);
            }
            var (phase, hadOld) = journal.Value;
            string action;
            if (phase == PhasePlaced)
            {
                RetireAside();
                action = "completed";
            }
            else
            {
                // begin (or anything unreadable): undo. With hadOld and nothing aside, the rename never happened and
                // dsh is still the old layer.
                if (LayerInstaller.Exists(aside) || !hadOld)
                {
                    if (LayerInstaller.Exists(dsh))
                    {
                        Ops.DeleteTree(dsh);
                    }
                    if (LayerInstaller.Exists(aside) && !Ops.Rename(aside, dsh))
                    {
                        return new DshLayerRecovery(false, "failed", "cannot rename the aside layer back to dsh");
                    }
                }
                action = "restored";
            }
            Ops.DeleteFile(JournalFile);
            return new DshLayerRecovery(true, action, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new DshLayerRecovery(false, "failed", e.Message);
        }
    }

    /// <summary>Legacy end state for the replaced layer: <c>previous/dsh</c> when <c>previous/</c> exists, else gone.</summary>
    private void RetireAside()
    {
        var aside = AsideDir;
        if (!LayerInstaller.Exists(aside))
        {
            return;
        }
        var previous = PreviousDshDir;
        if (Directory.Exists(Path.GetDirectoryName(previous)))
        {
            if (LayerInstaller.Exists(previous))
            {
                Ops.DeleteTree(previous);
            }
            if (Ops.Rename(aside, previous))
            {
                return;
            }
        }
        Ops.DeleteTree(aside);
    }

    private void WriteJournal(string phase, bool hadOld)
    {
        Directory.CreateDirectory(layout.RuntimeCurrentDir);
        Ops.WriteFile(JournalFile, $"phase={phase}\nhadOld={(hadOld ? "true" : "false")}\n");
    }

    private (string Phase, bool HadOld)? ReadJournal()
    {
        if (!File.Exists(JournalFile))
        {
            return null;
        }
        var phase = "";
        var hadOld = true; // unreadable: assume there was an old layer, so nothing at dsh is deleted without one aside
        foreach (var line in File.ReadAllLines(JournalFile, Encoding.UTF8))
        {
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }
            var (key, value) = (line[..eq].Trim(), line[(eq + 1)..].Trim());
            if (key == "phase")
            {
                phase = value;
            }
            else if (key == "hadOld")
            {
                hadOld = value != "false";
            }
        }
        return (phase, hadOld);
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
