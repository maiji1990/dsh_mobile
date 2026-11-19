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
/// Outcome of <see cref="LayerInstaller.Recover"/>: <paramref name="Action"/> is <c>none</c> (no interrupted import),
/// <c>restored</c> (the body from before the interrupted import is back), <c>completed</c> (the new body had been placed;
/// only retiring the old one was left) or <c>failed</c> (the journal is kept, the next call tries again).
/// </summary>
public sealed record RuntimeBodyRecovery(bool Ok, string Action, string? Message);

/// <summary>
/// Installs a runtime bundle ZIP into <c>runtime/runtime-current</c> with the legacy layout and end states:
/// inspect (layout, profile, per-layer SHA-256) → extract every layer into a private staging directory and record the
/// <c>.dshmobile/layer-&lt;name&gt;.sha256</c> sentinel → swap the body (layers + profile) as one journaled transaction
/// (<see cref="JournalFile"/>): the current body goes to <see cref="AsideDir"/>, the new one is placed, and only then is
/// the old one retired — into <c>runtime-current/previous/</c> when that exists, otherwise deleted. An import interrupted
/// by process death is finished or undone by <see cref="Recover"/> (run first by every import, and by the coordinator
/// before each read of the runtime state from disk, so before a start or a rollback too). The DSH layer (<c>runtime-current/dsh</c>, <c>previous/dsh</c>) and <c>user-data</c> are
/// never touched; <c>previous/</c> is never created (whether imports keep a rollback copy is a separate decision,
/// MIGRATION §7). The caller guarantees that the sandbox is stopped. Staging is always removed.
/// </summary>
public sealed class LayerInstaller(RuntimeLayout layout, Func<string, bool>? hostPathExists = null)
{
    public const string ImportFailed = "BUNDLE_IMPORT_FAILED";

    /// <summary>The body items in swap order: the layers, then the profile.</summary>
    private static IReadOnlyList<string> BodyItems => LayerRollback.BodyItems;

    private const string PhaseBegin = "begin";
    private const string PhasePlaced = "placed";

    /// <summary>
    /// ADR-0029: 新运行时主体已就位但尚未提交——旧层仍暂存在 <see cref="AsideDir"/>，等待装后核对与健康检查。
    /// <see cref="CommitProbed"/> 提交并退役旧层，<see cref="AbortProbed"/>（或非持有探测进程调用的 <see cref="Recover"/>）恢复旧层。
    /// </summary>
    private const string PhaseProbing = "probing";

    /// <summary>Distinct from the legacy <c>runtime-bundle-staging</c>, so the two importers never share a directory.</summary>
    public string StagingDir => Path.Combine(layout.FilesDir, "dshmobile-bundle-staging");

    /// <summary>Where the current body waits while the new one is placed (inside <c>runtime-current</c>).</summary>
    public string AsideDir => Path.Combine(layout.RuntimeCurrentDir, "dshmobile-body-old");

    /// <summary>Write-ahead record of a body swap in progress (<c>phase=begin|placed|probing</c>, <c>old=</c> items); absent when none is.</summary>
    public string JournalFile => Path.Combine(layout.RuntimeCurrentDir, "dshmobile-body-install.journal");

    /// <summary>The filesystem primitives of the swap (tests inject faults).</summary>
    internal LayerFileOps Ops { get; init; } = LayerFileOps.Real;

    public async Task<LayerInstallResult> InstallAsync(string zipPath, IProgress<RuntimeProgress>? progress, CancellationToken ct, bool holdForProbe = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(zipPath);
        var sw = Stopwatch.StartNew();
        var recovery = Recover();
        if (!recovery.Ok)
        {
            return new LayerInstallResult(false, ImportFailed, "an earlier interrupted runtime import could not be recovered: " + recovery.Message, null, [], sw.ElapsedMilliseconds);
        }
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
            var (swapped, swapMessage) = SwapRuntimeBody(staging, holdForProbe);
            return new LayerInstallResult(swapped, swapped ? "OK" : ImportFailed, swapMessage, inspection, extracted, sw.ElapsedMilliseconds);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Before the journal this undoes nothing; after it (e.g. the journal itself could not be written) it is the undo.
            var undo = Recover();
            return new LayerInstallResult(false, ImportFailed, "import failed: " + e.Message + (undo.Ok ? "" : "; recovery pending: " + undo.Message),
                inspection, extracted, sw.ElapsedMilliseconds);
        }
        finally
        {
            try
            {
                Ops.DeleteTree(staging);
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
                var sentinel = Path.Combine(dest, RuntimeConstants.LayerStateDir, $"layer-{layer.Layer}.sha256");
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
    /// Legacy <c>swapRuntimeBody</c> end states, reached as one journaled transaction over the whole body (the layers and
    /// <c>runtime-profile.json</c>, <see cref="LayerRollback.BodyItems"/>):
    /// <list type="number">
    /// <item>journal <c>phase=begin</c> with the items that exist now;</item>
    /// <item>each existing item is renamed into <see cref="AsideDir"/> (inside <c>runtime-current</c>, created first, so the
    /// rename cannot hit a missing directory). A refused rename puts the moved items back and refuses the import —
    /// nothing changes;</item>
    /// <item>the staged layers are renamed into place (copy as fallback), then the profile is written durably. If any of it
    /// fails, the placed items are dropped and the old body is renamed back;</item>
    /// <item>journal <c>phase=placed</c>, then <see cref="Recover"/> retires the old body.</item>
    /// </list>
    /// The legacy code deleted every current layer as soon as the rename into the (normally missing)
    /// <c>previous/</c> failed, before the new layers were moved in; a failing move or copy then left no body at all.
    /// Ok when the new body is in place (the message then says whether retiring the old one is still pending); otherwise
    /// the message says why not and whether the old body is back or its recovery is pending.
    /// </summary>
    private (bool Ok, string? Message) SwapRuntimeBody(string staging, bool holdForProbe = false)
    {
        var runtimeDir = layout.RuntimeCurrentDir;
        // RuntimeController creates runtime-current before every import (UPSTREAMS: fresh-install rename failure).
        Directory.CreateDirectory(runtimeDir);
        var old = BodyItems.Where(item => Exists(Path.Combine(runtimeDir, item))).ToList();
        WriteJournal(PhaseBegin, old);
        if (old.Count > 0)
        {
            Directory.CreateDirectory(AsideDir);
        }
        foreach (var item in old)
        {
            if (!Ops.Rename(Path.Combine(runtimeDir, item), Path.Combine(AsideDir, item)))
            {
                var undo = Recover();
                return (false, $"Cannot move the current runtime body ({item}) aside; " +
                    (undo.Ok ? "nothing was changed" : "putting it back failed, retried on the next import or start: " + undo.Message));
            }
        }
        string? failure = null;
        try
        {
            foreach (var layer in RuntimeConstants.LayerNames)
            {
                var src = Path.Combine(staging, layer);
                if (Directory.Exists(src) && !Place(src, Path.Combine(runtimeDir, layer)))
                {
                    failure = $"Cannot place the new {layer} layer (both rename and copy failed)";
                    break;
                }
            }
            var staged = Path.Combine(staging, RuntimeBundleLayout.ProfileName);
            if (failure is null && File.Exists(staged))
            {
                Ops.WriteFile(Path.Combine(runtimeDir, RuntimeBundleLayout.ProfileName), File.ReadAllText(staged, Encoding.UTF8));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            failure = "Cannot place the new runtime body: " + e.Message;
        }
        if (failure is not null)
        {
            var undo = Recover();
            return (false, failure + "; " + (undo.Ok ? "the previous runtime body was kept" : "restoring the previous body failed, retried on the next import or start: " + undo.Message));
        }
        if (holdForProbe)
        {
            WriteJournal(PhaseProbing, old);
            return (true, "installed; held for the update health check (old runtime body kept aside)");
        }
        WriteJournal(PhasePlaced, old);
        var done = Recover(); // retires the old body and drops the journal
        return (true, done.Ok ? null : "new body installed; retiring the old one is retried on the next import or start: " + done.Message);
    }

    /// <summary>Staging → live by rename, else by copy. False when neither worked (<see cref="Recover"/> drops a partial copy).</summary>
    private bool Place(string src, string dest)
    {
        if (Ops.Rename(src, dest))
        {
            return true;
        }
        try
        {
            Ops.CopyTree(src, dest);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Finishes or undoes a body swap that did not complete (process death, a failed placement, a failed clean-up),
    /// according to <see cref="JournalFile"/>:
    /// <list type="bullet">
    /// <item><c>begin</c>: the new body was not committed. Every item the journal lists as old that sits in
    /// <see cref="AsideDir"/> is renamed back (whatever was placed over it is dropped first); items that did not exist
    /// before the import are dropped. The body is the one from before the import;</item>
    /// <item><c>placed</c>: the new body is in place; the old one is retired with the legacy end state — when
    /// <c>runtime-current/previous/</c> exists each old item replaces <c>previous/&lt;item&gt;</c> and the previous copies of
    /// items that did not exist are dropped; otherwise the old items are deleted.</item>
    /// </list>
    /// Then the aside directory and the journal are removed. Staging leftovers are always removed; without a journal
    /// nothing else is touched, except aside items whose live item is missing, which are put back.
    /// <c>previous/</c> is never created. The caller makes sure no runtime process uses the body.
    /// </summary>
    public RuntimeBodyRecovery Recover(bool keepProbing = false)
    {
        var runtimeDir = layout.RuntimeCurrentDir;
        var aside = AsideDir;
        try
        {
            if (Exists(StagingDir))
            {
                Ops.DeleteTree(StagingDir); // an extraction the process did not survive
            }
            foreach (var tmp in (string[])[JournalFile + ".tmp", Path.Combine(runtimeDir, RuntimeBundleLayout.ProfileName) + ".tmp"])
            {
                if (File.Exists(tmp))
                {
                    Ops.DeleteFile(tmp); // a durable write the process did not survive
                }
            }
            var journal = ReadJournal();
            if (journal is null)
            {
                if (!Directory.Exists(aside))
                {
                    return new RuntimeBodyRecovery(true, "none", null);
                }
                foreach (var item in BodyItems)
                {
                    var cur = Path.Combine(runtimeDir, item);
                    if (Exists(Path.Combine(aside, item)) && !Exists(cur) && !Ops.Rename(Path.Combine(aside, item), cur))
                    {
                        return new RuntimeBodyRecovery(false, "failed", $"cannot rename the aside {item} back");
                    }
                }
                // What is left has a live item next to it: without a journal no swap is in progress, the live one wins.
                Ops.DeleteTree(aside);
                return new RuntimeBodyRecovery(true, "restored", "aside body without a journal put back");
            }
            var (phase, old) = journal.Value;
            if (phase == PhaseProbing && keepProbing)
            {
                return new RuntimeBodyRecovery(true, "held", null);
            }
            string action;
            if (phase == PhasePlaced)
            {
                RetireOldBody(old);
                action = "completed";
            }
            else
            {
                // begin (or anything unreadable): undo. An old item not aside was never moved and is still live.
                foreach (var item in BodyItems)
                {
                    var cur = Path.Combine(runtimeDir, item);
                    var moved = Path.Combine(aside, item);
                    if (Exists(moved) || !old.Contains(item))
                    {
                        if (Exists(cur))
                        {
                            Ops.DeleteTree(cur);
                        }
                        if (Exists(moved) && !Ops.Rename(moved, cur))
                        {
                            return new RuntimeBodyRecovery(false, "failed", $"cannot rename the aside {item} back");
                        }
                    }
                }
                action = "restored";
            }
            if (Exists(aside))
            {
                Ops.DeleteTree(aside);
            }
            Ops.DeleteFile(JournalFile);
            return new RuntimeBodyRecovery(true, action, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new RuntimeBodyRecovery(false, "failed", e.Message);
        }
    }

    /// <summary>当前是否有处于 <c>probing</c> 阶段的运行时主体安装（新层已就位、旧层暂存 aside、等待健康检查）。</summary>
    public bool IsHeldForProbe => ReadJournal() is { Phase: PhaseProbing };

    /// <summary>健康检查通过：提交暂存的运行时主体安装（退役旧层并删除日志）。</summary>
    public RuntimeBodyRecovery CommitProbed()
    {
        if (ReadJournal() is not { Phase: PhaseProbing, Old: var old })
        {
            return new RuntimeBodyRecovery(false, "failed", "no runtime body install is held for a health check");
        }
        try
        {
            WriteJournal(PhasePlaced, old);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new RuntimeBodyRecovery(false, "failed", "cannot commit: " + e.Message);
        }
        return Recover();
    }

    /// <summary>健康检查未通过（或中止）：恢复旧运行时主体并丢弃新层。</summary>
    public RuntimeBodyRecovery AbortProbed() =>
        IsHeldForProbe ? Recover(keepProbing: false) : new RuntimeBodyRecovery(false, "failed", "no runtime body install is held for a health check");

    /// <summary>
    /// Legacy end state for the replaced body, idempotent so a recovery can repeat it after dying halfway: an old item
    /// still aside replaces <c>previous/&lt;item&gt;</c> (when <c>previous/</c> exists; else it is deleted); an item that did
    /// not exist before the import only drops its stale <c>previous/&lt;item&gt;</c>; an old item no longer aside was
    /// retired already.
    /// </summary>
    private void RetireOldBody(IReadOnlySet<string> old)
    {
        var previous = LayerRollback.PreviousDir(layout);
        var keep = Directory.Exists(previous);
        foreach (var item in BodyItems)
        {
            var moved = Path.Combine(AsideDir, item);
            var prev = Path.Combine(previous, item);
            if (Exists(moved))
            {
                if (keep)
                {
                    if (Exists(prev))
                    {
                        Ops.DeleteTree(prev);
                    }
                    if (Ops.Rename(moved, prev))
                    {
                        continue;
                    }
                }
                Ops.DeleteTree(moved);
            }
            else if (!old.Contains(item) && keep && Exists(prev))
            {
                Ops.DeleteTree(prev);
            }
        }
    }

    private void WriteJournal(string phase, IEnumerable<string> old) =>
        Ops.WriteFile(JournalFile, $"phase={phase}\nold={string.Join(',', old)}\n");

    private (string Phase, IReadOnlySet<string> Old)? ReadJournal()
    {
        if (!File.Exists(JournalFile))
        {
            return null;
        }
        var phase = "";
        // Unreadable: every item counts as old, so nothing live is deleted without its old copy aside.
        IReadOnlySet<string> old = BodyItems.ToHashSet(StringComparer.Ordinal);
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
            else if (key == "old")
            {
                old = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
            }
        }
        return (phase, old);
    }

    /// <summary><c>File.renameTo</c>: false instead of throwing (missing parent, existing target, ...).</summary>
    internal static bool TryRename(string from, string to)
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

    internal static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

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
    internal static void CopyTree(string src, string dest)
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

    internal sealed class HashingReadStream(Stream inner, IncrementalHash hash) : Stream
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
