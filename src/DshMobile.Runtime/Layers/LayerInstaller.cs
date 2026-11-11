// Ported from DSHBox v1.5.1 (7c9d33c) sandbox-manager/.../DefaultSandboxManager.kt importRuntimeBundle (ZIP branch),
// extractStagedLayers, swapRuntimeBody and writeFileAtomically (GPL-3.0-or-later). Parity:
// tests/TestVectors/install-scenarios.json runs against both (native-bridge LegacyBundleImportParityTest,
// LayerInstallerParityTests).

using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DshMobile.Runtime.Bundle;

namespace DshMobile.Runtime.Layers;

public sealed record LayerInstallResult(
    bool Ok,
    string Code,
    string? Message,
    BundleInspection? Inspection,
    IReadOnlyList<LayerExtractResult> Layers,
    long ElapsedMs);

/// <summary>
/// Installs a runtime bundle ZIP into <c>runtime/runtime-current</c> with the legacy layout and semantics:
/// inspect (layout, profile, per-layer SHA-256) → extract every layer into a private staging directory and record the
/// <c>.dshbox/layer-&lt;name&gt;.sha256</c> sentinel → move the current body (layers + profile) to
/// <c>runtime-current/previous/</c> → move the new layers in → write the profile atomically. The DSH layer
/// (<c>runtime-current/dsh</c>, <c>previous/dsh</c>) and <c>user-data</c> are never touched. The caller guarantees that
/// the sandbox is stopped. Staging is always removed.
/// </summary>
public sealed class LayerInstaller(RuntimeLayout layout, Func<string, bool>? hostPathExists = null)
{
    public const string ImportFailed = "BUNDLE_IMPORT_FAILED";

    /// <summary>Distinct from the legacy <c>runtime-bundle-staging</c>, so the two importers never share a directory.</summary>
    public string StagingDir => Path.Combine(layout.FilesDir, "dshmobile-bundle-staging");

    public async Task<LayerInstallResult> InstallAsync(string zipPath, IProgress<RuntimeProgress>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(zipPath);
        var sw = Stopwatch.StartNew();
        var inspection = await RuntimeBundleInspector.InspectAsync(zipPath, progress, ct).ConfigureAwait(false);
        if (!inspection.Ok)
        {
            return new LayerInstallResult(false, inspection.ErrorCode!, inspection.Message, inspection, [], sw.ElapsedMilliseconds);
        }
        var staging = StagingDir;
        var extracted = new List<LayerExtractResult>();
        try
        {
            DeleteTree(staging);
            Directory.CreateDirectory(staging);
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var layer in inspection.Layers)
                {
                    progress?.Report(new RuntimeProgress($"extract {layer.Layer}", extracted.Count, inspection.Layers.Count));
                    var entry = (layer.ZipEntryName is { } n ? zip.GetEntry(n) : null)
                        ?? throw new IOException($"layer archive vanished from the zip: {layer.ArchivePath}");
                    var result = await ExtractLayerAsync(entry, layer, Path.Combine(staging, layer.Layer), ct).ConfigureAwait(false);
                    extracted.Add(result);
                    if (!result.Ok)
                    {
                        return new LayerInstallResult(false, result.Code, result.Message, inspection, extracted, sw.ElapsedMilliseconds);
                    }
                }
                var profileEntry = (inspection.ProfileEntryName is { } p ? zip.GetEntry(p) : null)
                    ?? throw new IOException("runtime-profile.json vanished from the zip");
                var es = await profileEntry.OpenAsync(ct).ConfigureAwait(false);
                await using (es.ConfigureAwait(false))
                {
                    using var reader = new StreamReader(es, Encoding.UTF8);
                    await File.WriteAllTextAsync(Path.Combine(staging, RuntimeBundleLayout.ProfileName), await reader.ReadToEndAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                }
            }
            if (!Directory.Exists(Path.Combine(staging, "base")))
            {
                return new LayerInstallResult(false, "BUNDLE_NO_BASE", "Runtime bundle missing base layer", inspection, extracted, sw.ElapsedMilliseconds);
            }
            SwapRuntimeBody(staging, RuntimeConstants.LayerNames);
            return new LayerInstallResult(true, "OK", null, inspection, extracted, sw.ElapsedMilliseconds);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new LayerInstallResult(false, ImportFailed, "import failed: " + e.Message, inspection, extracted, sw.ElapsedMilliseconds);
        }
        finally
        {
            try
            {
                DeleteTree(staging);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Next install clears it first; never fail an import over staging cleanup.
            }
        }
    }

    /// <summary>
    /// Extracts one layer and records its sentinel. The compressed stream is hashed while extracting and must still
    /// match the digest the inspector verified (the zip is read twice; this closes the gap between the reads).
    /// </summary>
    private async Task<LayerExtractResult> ExtractLayerAsync(ZipArchiveEntry entry, LayerVerification layer, string dest, CancellationToken ct)
    {
        LayerExtractResult result;
        string actual;
        var raw = await entry.OpenAsync(ct).ConfigureAwait(false);
        await using (raw.ConfigureAwait(false))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var hashing = new HashingReadStream(raw, hash);
            result = await LayerExtractor.ExtractAsync(hashing, new DiskExtractionTarget(dest), ct, hostPathExists).ConfigureAwait(false);
            if (result.Ok)
            {
                await hashing.CopyToAsync(Stream.Null, ct).ConfigureAwait(false); // tar end padding the reader may not consume
            }
            actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        if (!result.Ok)
        {
            return result;
        }
        if (layer.ExpectedSha256 is { } expected && !actual.Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return result with { Ok = false, Code = BundleErrorCodes.Sha256Mismatch, Message = $"Layer {layer.Layer} SHA-256 changed between verification and extraction" };
        }
        if (layer.ExpectedSha256 is { } recorded)
        {
            try
            {
                var sentinel = Path.Combine(dest, ".dshbox", $"layer-{layer.Layer}.sha256");
                Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!);
                await File.WriteAllTextAsync(sentinel, recorded, ct).ConfigureAwait(false); // writeText: no newline
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Legacy: runCatching; verifyLayersBroken self-heals a missing sentinel on the next launch.
            }
        }
        return result;
    }

    /// <summary>
    /// Legacy <c>swapRuntimeBody</c>, step for step. Note the inherited behavior: when <c>previous/</c> does not exist the
    /// rename fails and the old layer is deleted (no rollback copy) - pinned by install-scenarios.json.
    /// </summary>
    private void SwapRuntimeBody(string staging, IReadOnlyList<string> layerNames)
    {
        var runtimeDir = layout.RuntimeCurrentDir;
        // RuntimeController creates runtime-current before every import (UPSTREAMS: fresh-install rename failure).
        Directory.CreateDirectory(runtimeDir);
        var previous = Path.Combine(runtimeDir, "previous");
        foreach (var layer in layerNames)
        {
            var cur = Path.Combine(runtimeDir, layer);
            var prev = Path.Combine(previous, layer);
            if (Exists(prev))
            {
                DeleteTree(prev);
            }
            if (Exists(cur) && !TryRename(cur, prev))
            {
                DeleteTree(cur);
            }
        }
        var curProfile = Path.Combine(runtimeDir, RuntimeBundleLayout.ProfileName);
        var prevProfile = Path.Combine(previous, RuntimeBundleLayout.ProfileName);
        if (File.Exists(prevProfile))
        {
            File.Delete(prevProfile);
        }
        if (File.Exists(curProfile))
        {
            TryRename(curProfile, prevProfile);
        }
        foreach (var layer in layerNames)
        {
            var src = Path.Combine(staging, layer);
            if (Directory.Exists(src))
            {
                var dest = Path.Combine(runtimeDir, layer);
                if (!TryRename(src, dest))
                {
                    CopyTree(src, dest);
                }
            }
        }
        var staged = Path.Combine(staging, RuntimeBundleLayout.ProfileName);
        if (File.Exists(staged))
        {
            WriteFileAtomically(File.ReadAllText(staged, Encoding.UTF8), curProfile);
        }
    }

    /// <summary>Legacy <c>writeFileAtomically</c>: temp file in the same directory, fsync, rename over the target.</summary>
    private static void WriteFileAtomically(string content, string target)
    {
        var tmp = target + ".tmp";
        var wrote = false;
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(Encoding.UTF8.GetBytes(content));
                fs.Flush(flushToDisk: true);
            }
            wrote = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        if (wrote)
        {
            try
            {
                File.Move(tmp, target, overwrite: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        try
        {
            File.Delete(tmp);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        File.WriteAllText(target, content);
    }

    /// <summary><c>File.renameTo</c>: false instead of throwing (missing parent, existing target, ...).</summary>
    private static bool TryRename(string from, string to)
    {
        try
        {
            if (Directory.Exists(from) && !IsLink(from))
            {
                if (Exists(to))
                {
                    return false;
                }
                Directory.Move(from, to);
            }
            else
            {
                File.Move(from, to, overwrite: false);
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;

    /// <summary>
    /// Recursive delete that never follows symlinks (layers contain literal absolute links to host paths) and first
    /// restores owner rwx on directories, so a layer that archived <c>0555</c> directories can still be removed.
    /// </summary>
    internal static void DeleteTree(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null)
        {
            File.Delete(path);
            return;
        }
        if (!Directory.Exists(path))
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            DeleteTree(child);
        }
        Directory.Delete(path, recursive: false);
    }

    /// <summary>Fallback when renaming staging into place fails: copy, preserving symlinks literally.</summary>
    private static void CopyTree(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var child in Directory.EnumerateFileSystemEntries(src))
        {
            var target = Path.Combine(dest, Path.GetFileName(child));
            var info = new FileInfo(child);
            if (info.LinkTarget is { } link)
            {
                File.CreateSymbolicLink(target, link);
            }
            else if (Directory.Exists(child))
            {
                CopyTree(child, target);
            }
            else
            {
                File.Copy(child, target, overwrite: true);
            }
        }
    }

    private sealed class HashingReadStream(Stream inner, IncrementalHash hash) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = inner.Read(buffer);
            hash.AppendData(buffer[..n]);
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer.Span[..n]);
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
