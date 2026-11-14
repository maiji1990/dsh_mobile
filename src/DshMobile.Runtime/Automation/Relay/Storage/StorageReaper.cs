// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/storage/StorageReaper.kt, storage/MediaLibraryUsage.kt (pure parts)
// Registered in docs/UPSTREAMS.md. Locked by tests/TestVectors/relay-storage.json.

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using DshMobile.Runtime.Automation.Relay.Logging;

namespace DshMobile.Runtime.Automation.Relay.Storage;

/// <summary>
/// Upstream <c>StorageReaper</c>: the only place the relay deletes. Calls hold an execution slot that a sweep or a
/// manual clear waits out; artifacts are staged outside the bound tree and renamed in; pinned artifacts (their
/// response not yet read) survive the TTL; requests, responses, uploads, staging and the delivery tree are each
/// reclaimed by age, count and total. Every path inside the bound tree is checked link-free first.
/// </summary>
public sealed class StorageReaper
{
    public static IReadOnlySet<string> AllowedExtensions { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "png", "jpg", "webp", "mp4", "m4a", "webm", "txt", "json" };

    private const int PinSuffixBytes = 4;
    private const int CreateAttempts = 4;
    private const int QuotaAttempts = 3;
    private const int SweepRetryPauseMs = 60;
    public const long StagingMaxAgeMs = 10L * 60L * 1000L;
    public const long UploadTotalBytes = 64L * 1024L * 1024L;
    public const int UploadMaxEntries = 128;
    public const int UploadMaxDepth = 4;
    public const long PinMaxAgeMs = QuotaLedger.ResponseTtlMs;

    public sealed record SweepResult(int StaleRequests = 0, int StaleResponses = 0, int DeletedArtifacts = 0, long FreedBytes = 0, bool Skipped = false);

    public sealed record OutUsage(long Bytes, int Files, IReadOnlyDictionary<string, long> ByKind);

    private readonly RelayPaths _paths;
    private readonly QuotaLedger _ledger;
    private readonly RunLog _log;
    private readonly Func<long> _now;
    private readonly Func<long> _wallNow;
    private readonly Func<string, long> _availableBytes;
    private readonly Lock _state = new();
    private bool _sweeping;
    private bool _clearing;
    private int _inFlight;
    private readonly ConcurrentDictionary<string, long> _processingSince = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _pins = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _pinnedAt = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, QuotaLedger.Reservation> _pending = new(StringComparer.Ordinal);

    /// <param name="availableBytes">Free bytes on the volume holding a directory (upstream <c>StatFs</c>); throwing = no room.</param>
    public StorageReaper(RelayPaths paths, QuotaLedger ledger, RunLog runLog, Func<long>? now = null, Func<long>? wallNow = null,
        Func<string, long>? availableBytes = null)
    {
        _paths = paths;
        _ledger = ledger;
        _log = runLog;
        _now = now ?? RelayClock.MonotonicNow;
        _wallNow = wallNow ?? RelayClock.WallNow;
        _availableBytes = availableBytes ?? VolumeAvailableBytes;
        if (!paths.EnsureWritableDirs())
        {
            _log.Event(LogSubsystem.Storage, LogEvent.DirsUnavailable);
        }
    }

    public bool BeginCall()
    {
        lock (_state)
        {
            if (_sweeping || _clearing)
            {
                return false;
            }
            _inFlight++;
        }
        return true;
    }

    public void EndCall()
    {
        lock (_state)
        {
            _inFlight--;
        }
    }

    public bool CanAccept(long bytes) => !_ledger.OverQuota() && DeviceHasRoom(bytes) && _ledger.UsableBytes() >= bytes;

    /// <summary>Upstream <c>newArtifactFile</c>: a fresh staged file with its quota reserved, or null.</summary>
    public string? NewArtifactFile(string requestId, string extension, long expectedBytes)
    {
        if (!AllowedExtensions.Contains(extension))
        {
            throw new ArgumentException("unsupported artifact extension: " + extension, nameof(extension));
        }
        if (AcquireQuotaRoom(expectedBytes) is not { } reservation)
        {
            _log.Event(LogSubsystem.Storage, LogEvent.QuotaExhausted, ("want", expectedBytes.ToString(CultureInfo.InvariantCulture)));
            return null;
        }
        var stem = ArtifactStem(requestId);
        for (var i = 0; i < CreateAttempts; i++)
        {
            var candidate = Path.Combine(_paths.StagingDir, $"{stem}-{Hex()}.{extension}");
            if (CreateExclusive(candidate))
            {
                _pending[Path.GetFullPath(candidate)] = reservation;
                return candidate;
            }
        }
        _ledger.Release(reservation);
        _log.Event(LogSubsystem.Storage, LogEvent.ArtifactCreateFailed, ("cap", stem));
        return null;
    }

    /// <summary>Upstream <c>publishArtifact</c>: rename the staged file into <c>run/artifacts</c>, pin it, charge it.</summary>
    public string? PublishArtifact(string staged, string requestId)
    {
        var target = Path.Combine(_paths.ArtifactsDir, Path.GetFileName(staged));
        if (!RelayStorageRules.SymlinkFreeFrom(_paths.EntryDir, target))
        {
            TryDelete(staged);
            ReleasePending(staged);
            _log.Event(LogSubsystem.Storage, LogEvent.AtomicWriteFailed, ("file", Path.GetFileName(target)));
            return null;
        }
        try
        {
            File.Move(staged, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(target);
            TryDelete(staged);
            ReleasePending(staged);
            _log.Event(LogSubsystem.Storage, LogEvent.AtomicWriteFailed, ("file", Path.GetFileName(target)));
            return null;
        }
        RecordArtifact(target, requestId);
        ReleasePending(staged);
        return target;
    }

    public void RecordArtifact(string file, string requestId)
    {
        var id = PinOwner(requestId);
        _pins[Path.GetFullPath(file)] = id;
        _pinnedAt[id] = _now();
        _ledger.RecordWritten(Length(file));
    }

    /// <summary>
    /// Upstream <c>writeTextAtomic</c>: temp file in the same directory, then rename. Inside the bound tree the
    /// target path must be link-free (a guest-planted link would redirect the host's write).
    /// </summary>
    public bool WriteTextAtomic(string target, string text)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(target));
        if (parent is null)
        {
            return false;
        }
        var entry = Path.GetFullPath(_paths.EntryDir).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(target).StartsWith(entry, StringComparison.Ordinal) && !RelayStorageRules.SymlinkFreeFrom(_paths.EntryDir, target))
        {
            _log.Event(LogSubsystem.Storage, LogEvent.AtomicWriteFailed, ("file", Path.GetFileName(target)));
            return false;
        }
        try
        {
            Directory.CreateDirectory(parent);
            var temp = Path.Combine(parent, Path.GetFileName(target) + "-" + Hex() + Hex() + ".tmp");
            File.WriteAllText(temp, text);
            try
            {
                File.Move(temp, target, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(temp);
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Event(LogSubsystem.Storage, LogEvent.AtomicWriteFailed, ("file", Path.GetFileName(target)));
            return false;
        }
    }

    public void NoteProcessing(string name) => _processingSince[name] = _now();

    public void ClearProcessing(string name) => _processingSince.TryRemove(name, out _);

    public OutUsage DeliveryUsage()
    {
        long total = 0;
        var count = 0;
        var byKind = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var kind in ArtifactOut.Subdirs)
        {
            var files = Files(OutDirFor(kind));
            var bytes = files.Sum(f => f.Length);
            byKind[kind] = bytes;
            total += bytes;
            count += files.Count;
        }
        return new OutUsage(total, count, byKind);
    }

    public SweepResult Sweep()
    {
        lock (_state)
        {
            if (_sweeping || _clearing)
            {
                return new SweepResult(Skipped: true);
            }
            _sweeping = true;
        }
        try
        {
            return DoSweep();
        }
        finally
        {
            lock (_state)
            {
                _sweeping = false;
            }
        }
    }

    /// <summary>Upstream <c>clearAllArtifacts</c>: null while any call is in flight (never clears under a running call).</summary>
    public long? ClearAllArtifacts()
    {
        lock (_state)
        {
            if (_inFlight > 0 || _sweeping || _clearing)
            {
                return null;
            }
            _clearing = true;
        }
        try
        {
            var uploadsFreed = ClearUploads();
            var outFreed = ClearOutTree();
            var freed = Files(_paths.ArtifactsDir).Sum(f => Delete(f.FullName)) + uploadsFreed + outFreed;
            _pins.Clear();
            _pinnedAt.Clear();
            _ledger.ReconcileFromDisk(_paths.ArtifactsDir);
            _log.Event(LogSubsystem.Storage, LogEvent.ManualClear, ("freed", freed.ToString(CultureInfo.InvariantCulture)));
            return freed;
        }
        finally
        {
            lock (_state)
            {
                _clearing = false;
            }
        }
    }

    public long UploadsBytes() => UploadBytes(_paths.UploadsDir, 0);

    public long MailboxBytes()
    {
        long total = 0;
        foreach (var dir in new[] { _paths.InboxDir, _paths.ProcessingDir, _paths.OutboxDir })
        {
            if (!RelayStorageRules.SymlinkFreeFrom(_paths.EntryDir, dir))
            {
                continue;
            }
            total += Files(dir).Where(f => f.LinkTarget is null).Sum(f => f.Length);
        }
        return total;
    }

    public long UsageBytes() => _ledger.TotalBytes;

    public bool LedgerPersistHealthy() => _ledger.PersistHealthy;

    public long Discard(string file)
    {
        ReleasePending(file);
        return Delete(file);
    }

    /// <summary>Upstream: letters, digits, '-' and '_' of the request id, at most 48, else "artifact".</summary>
    public static string ArtifactStem(string requestId) => Keep(requestId, 48, "artifact");

    /// <summary>Upstream <c>recordArtifact</c>'s pin owner: same filter, at most 64, else "unknown".</summary>
    public static string PinOwner(string requestId) => Keep(requestId, 64, "unknown");

    private static string Keep(string value, int max, string fallback)
    {
        var kept = new string([.. value.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').Take(max)]);
        return string.IsNullOrWhiteSpace(kept) ? fallback : kept;
    }

    /// <summary>
    /// The delivery-tree decision of upstream <c>reapOutTree</c> as a pure function over (path, kind, mtime, bytes):
    /// keep everything inside the 10-minute grace; per kind, beyond the newest 20 or older than 7 days goes; then, over
    /// 256 MiB, oldest first except each kind's newest. Returns the paths to delete in upstream order.
    /// </summary>
    public static IReadOnlyList<string> OutTreeVictims(IReadOnlyList<(string Path, string Kind, long MtimeMs, long Bytes)> files, long nowWallMs)
    {
        ArgumentNullException.ThrowIfNull(files);
        var ageCutoff = nowWallMs - ArtifactOut.MaxAgeMs;
        var graceCutoff = nowWallMs - ArtifactOut.RecentGraceMs;
        var victims = new List<string>();
        var live = new List<(string Path, string Kind, long MtimeMs, long Bytes)>();
        foreach (var kind in ArtifactOut.Subdirs)
        {
            var ofKind = files.Where(f => f.Kind == kind).OrderByDescending(f => f.MtimeMs).ToList();
            for (var index = 0; index < ofKind.Count; index++)
            {
                var f = ofKind[index];
                if (f.MtimeMs >= graceCutoff)
                {
                    live.Add(f);
                    continue;
                }
                if (index >= ArtifactOut.KeepPerKind || f.MtimeMs < ageCutoff)
                {
                    victims.Add(f.Path);
                }
                else
                {
                    live.Add(f);
                }
            }
        }
        var total = live.Sum(f => f.Bytes);
        if (total > ArtifactOut.MaxTotalBytes)
        {
            var newest = new Dictionary<string, (string Path, long MtimeMs)>(StringComparer.Ordinal);
            foreach (var f in live)
            {
                if (!newest.TryGetValue(f.Kind, out var cur) || f.MtimeMs > cur.MtimeMs)
                {
                    newest[f.Kind] = (f.Path, f.MtimeMs);
                }
            }
            var protectedPaths = newest.Values.Select(v => v.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var f in live.OrderBy(f => f.MtimeMs))
            {
                if (total <= ArtifactOut.MaxTotalBytes)
                {
                    break;
                }
                if (protectedPaths.Contains(f.Path))
                {
                    continue;
                }
                victims.Add(f.Path);
                total -= f.Bytes;
            }
        }
        return victims;
    }

    /// <summary>Upstream <c>MediaLibraryUsage.galleryPrefixes</c> / <c>audioPrefixes</c> / <c>selection</c> / <c>selectionArgs</c>.</summary>
    public static IReadOnlyList<string> GalleryPrefixes(string albumDir) => [$"Pictures/{albumDir}/", $"Movies/{albumDir}/"];

    public static IReadOnlyList<string> AudioPrefixes(string albumDir) => [$"Music/{albumDir}/"];

    public static string MediaSelection(IReadOnlyList<string> prefixes) => "(" + string.Join(" OR ", prefixes.Select(_ => "relative_path LIKE ?")) + ")";

    public static IReadOnlyList<string> MediaSelectionArgs(IReadOnlyList<string> prefixes) => [.. prefixes.Select(p => p + "%")];

    private SweepResult DoSweep()
    {
        var staleRequests = 0;
        var staleResponses = 0;
        var deletedArtifacts = 0;
        long freed = 0;
        var cutoff = _now() - QuotaLedger.OrphanRequestTtlMs;
        staleRequests += DeleteOlderFiles(_paths.InboxDir, _wallNow() - QuotaLedger.OrphanRequestTtlMs);
        staleRequests += DeleteStaleProcessing(cutoff);
        staleRequests += DeleteOlderFiles(_paths.ControlInboxDir, _wallNow() - QuotaLedger.OrphanRequestTtlMs);
        staleResponses += DeleteOlderFiles(_paths.OutboxDir, _wallNow() - QuotaLedger.ResponseTtlMs);
        staleResponses += DeleteOlderFiles(_paths.ControlOutboxDir, _wallNow() - QuotaLedger.ResponseTtlMs);
        DeleteOlderFiles(_paths.StagingDir, _wallNow() - StagingMaxAgeMs);
        ReleaseReservationsForVanishedStaging();
        DeleteOlderFiles(_paths.UploadsDir, _wallNow() - QuotaLedger.OrphanRequestTtlMs);
        TrimUploadBudget();
        ReleaseExpiredPins();

        var ttlCutoff = _wallNow() - QuotaLedger.ArtifactTtlMs;
        var protectedPaths = _pins.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var file in Files(_paths.ArtifactsDir).OrderBy(Mtime).TakeWhile(f => Mtime(f) < ttlCutoff))
        {
            if (protectedPaths.Contains(file.FullName))
            {
                continue;
            }
            var bytes = Delete(file.FullName);
            freed += bytes;
            deletedArtifacts++;
        }
        var budget = _ledger.TotalBytes - _ledger.MaxBytes;
        if (budget > 0)
        {
            foreach (var file in Files(_paths.ArtifactsDir).OrderBy(Mtime))
            {
                if (budget <= 0)
                {
                    break;
                }
                if (protectedPaths.Contains(file.FullName))
                {
                    continue;
                }
                var bytes = Delete(file.FullName);
                budget -= bytes;
                freed += bytes;
                deletedArtifacts++;
            }
        }
        var (outDeleted, outFreed) = ReapOutTree();
        deletedArtifacts += outDeleted;
        freed += outFreed;
        if (deletedArtifacts > 0 || staleRequests > 0 || staleResponses > 0)
        {
            _log.Event(LogSubsystem.Storage, LogEvent.SweepDone,
                ("requests", staleRequests.ToString(CultureInfo.InvariantCulture)),
                ("responses", staleResponses.ToString(CultureInfo.InvariantCulture)),
                ("artifacts", deletedArtifacts.ToString(CultureInfo.InvariantCulture)),
                ("freed", freed.ToString(CultureInfo.InvariantCulture)));
        }
        return new SweepResult(staleRequests, staleResponses, deletedArtifacts, freed);
    }

    private (int Deleted, long Freed) ReapOutTree()
    {
        var all = new List<(string Path, string Kind, long MtimeMs, long Bytes)>();
        foreach (var kind in ArtifactOut.Subdirs)
        {
            var dir = OutDirFor(kind);
            if (!RelayStorageRules.SymlinkFreeFrom(_paths.EntryDir, dir))
            {
                _log.Error(LogSubsystem.Storage, LogEvent.UnsafePathRejected, ("name", Path.GetFileName(dir)));
                continue;
            }
            all.AddRange(Files(dir).Select(f => (f.FullName, kind, Mtime(f), f.Length)));
        }
        var deleted = 0;
        long freed = 0;
        foreach (var victim in OutTreeVictims(all, _wallNow()))
        {
            var bytes = DeleteOutsideLedger(victim);
            if (bytes > 0)
            {
                freed += bytes;
                deleted++;
            }
        }
        return (deleted, freed);
    }

    private long ClearOutTree()
    {
        long freed = 0;
        foreach (var kind in ArtifactOut.Subdirs)
        {
            var dir = OutDirFor(kind);
            if (!RelayStorageRules.SymlinkFreeFrom(_paths.EntryDir, dir))
            {
                _log.Error(LogSubsystem.Storage, LogEvent.UnsafePathRejected, ("name", Path.GetFileName(dir)));
                continue;
            }
            var files = Files(dir).OrderByDescending(Mtime).ToList();
            foreach (var file in files.Skip(1))
            {
                freed += DeleteOutsideLedger(file.FullName);
            }
        }
        return freed;
    }

    private string OutDirFor(string kind) => kind switch
    {
        ArtifactOut.Shot => _paths.OutShotDir,
        ArtifactOut.Video => _paths.OutVideoDir,
        ArtifactOut.Audio => _paths.OutAudioDir,
        _ => _paths.OutFileDir,
    };

    private QuotaLedger.Reservation? AcquireQuotaRoom(long expectedBytes)
    {
        if (!DeviceHasRoom(expectedBytes))
        {
            return null;
        }
        if (_ledger.Reserve(expectedBytes) is { } first)
        {
            return first;
        }
        for (var i = 0; i < QuotaAttempts; i++)
        {
            if (Sweep().Skipped)
            {
                Thread.Sleep(SweepRetryPauseMs);
            }
            if (!DeviceHasRoom(expectedBytes))
            {
                return null;
            }
            if (_ledger.Reserve(expectedBytes) is { } r)
            {
                return r;
            }
        }
        return null;
    }

    /// <summary>
    /// Upstream <c>StatFs(dir).availableBytes</c>: free space on the volume that holds <paramref name="dir"/> itself.
    /// On Unix (Android) DriveInfo statvfs()es the name it is given, so it gets the directory; the path root would be
    /// "/", the read-only system partition, which has next to no room and made every artifact "quota exhausted"
    /// (found on the device, PR-07 relay-host shadow). Windows only accepts a drive root.
    /// </summary>
    internal static long VolumeAvailableBytes(string dir)
    {
        var full = Path.GetFullPath(dir);
        return new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(full)! : full).AvailableFreeSpace;
    }

    private bool DeviceHasRoom(long bytes)
    {
        try
        {
            return QuotaLedger.DeviceHasRoom(_availableBytes(_paths.RunDir), bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private long ClearUploads() =>
        RelayStorageRules.SymlinkFreeFrom(_paths.EntryDir, _paths.UploadsDir) ? DeleteUploadTree(_paths.UploadsDir, 1) : 0;

    private static long DeleteUploadTree(string root, int depth)
    {
        FileSystemInfo info = Directory.Exists(root) ? new DirectoryInfo(root) : new FileInfo(root);
        if (!info.Exists)
        {
            return 0;
        }
        if (info.LinkTarget is not null || info is FileInfo)
        {
            var bytes = info is FileInfo f && info.LinkTarget is null ? f.Length : 0;
            return TryDelete(root) ? bytes : 0;
        }
        long freed = 0;
        foreach (var entry in SafeEntries((DirectoryInfo)info))
        {
            if (entry is DirectoryInfo d && entry.LinkTarget is null)
            {
                if (depth < UploadMaxDepth)
                {
                    freed += DeleteUploadTree(d.FullName, depth + 1);
                }
                TryDeleteDir(d.FullName);
            }
            else if (entry is FileInfo file && entry.LinkTarget is null)
            {
                var bytes = file.Length;
                if (TryDelete(file.FullName))
                {
                    freed += bytes;
                }
            }
            else
            {
                TryDelete(entry.FullName);
            }
        }
        return freed;
    }

    private static long UploadBytes(string dir, int depth)
    {
        if (depth > UploadMaxDepth || !Directory.Exists(dir))
        {
            return 0;
        }
        long total = 0;
        foreach (var entry in SafeEntries(new DirectoryInfo(dir)))
        {
            if (entry.LinkTarget is not null)
            {
                continue;
            }
            total += entry switch
            {
                DirectoryInfo d => UploadBytes(d.FullName, depth + 1),
                FileInfo f => f.Length,
                _ => 0,
            };
        }
        return total;
    }

    private void ReleaseExpiredPins()
    {
        var stamp = _now();
        foreach (var (path, owner) in _pins.ToArray())
        {
            var responsePending = File.Exists(Path.Combine(_paths.OutboxDir, owner + ".json"));
            var age = _pinnedAt.TryGetValue(owner, out var at) ? stamp - at : long.MaxValue;
            if (!responsePending || age > PinMaxAgeMs)
            {
                _pins.TryRemove(path, out _);
                if (!_pins.Values.Contains(owner, StringComparer.Ordinal))
                {
                    _pinnedAt.TryRemove(owner, out _);
                }
            }
        }
    }

    private void ReleaseReservationsForVanishedStaging()
    {
        foreach (var (path, reservation) in _pending.ToArray())
        {
            if (!File.Exists(path) && _pending.TryRemove(path, out _))
            {
                _ledger.Release(reservation);
            }
        }
    }

    private int DeleteStaleProcessing(long cutoff)
    {
        var deleted = 0;
        foreach (var file in Files(_paths.ProcessingDir))
        {
            var known = _processingSince.TryGetValue(file.Name, out var since);
            if (known && since > cutoff)
            {
                continue;
            }
            if (!known && Mtime(file) > _wallNow() - QuotaLedger.OrphanRequestTtlMs)
            {
                continue;
            }
            if (TryDelete(file.FullName))
            {
                _processingSince.TryRemove(file.Name, out _);
                deleted++;
            }
        }
        return deleted;
    }

    private void TrimUploadBudget()
    {
        if (!RelayStorageRules.SymlinkFreeFrom(_paths.EntryDir, _paths.UploadsDir))
        {
            _log.Error(LogSubsystem.Storage, LogEvent.UnsafePathRejected, ("name", Path.GetFileName(_paths.UploadsDir)));
            return;
        }
        if (!Directory.Exists(_paths.UploadsDir))
        {
            return;
        }
        var entries = SafeEntries(new DirectoryInfo(_paths.UploadsDir)).OrderBy(e => e.LastWriteTimeUtc).ToList();
        var remaining = entries.Count;
        var total = UploadsBytes();
        foreach (var entry in entries)
        {
            if (total <= UploadTotalBytes && remaining <= UploadMaxEntries)
            {
                break;
            }
            total -= DeleteUploadTree(entry.FullName, 1);
            if (entry is DirectoryInfo)
            {
                TryDeleteDir(entry.FullName);
            }
            else
            {
                TryDelete(entry.FullName);
            }
            if (!File.Exists(entry.FullName) && !Directory.Exists(entry.FullName))
            {
                remaining--;
            }
        }
    }

    private int DeleteOlderFiles(string dir, long cutoffWallMs)
    {
        var entryRoot = Path.GetFullPath(_paths.EntryDir).TrimEnd('/', '\\');
        var full = Path.GetFullPath(dir);
        var inside = full == entryRoot || full.StartsWith(entryRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        if (inside && !RelayStorageRules.SymlinkFreeFrom(_paths.EntryDir, dir))
        {
            _log.Error(LogSubsystem.Storage, LogEvent.UnsafePathRejected, ("name", Path.GetFileName(dir)));
            return 0;
        }
        return Files(dir).Where(f => Mtime(f) < cutoffWallMs).Count(f => TryDelete(f.FullName));
    }

    private long Delete(string file)
    {
        var bytes = Length(file);
        if (!TryDelete(file))
        {
            return 0;
        }
        _pins.TryRemove(Path.GetFullPath(file), out _);
        _ledger.RecordFreed(bytes);
        return bytes;
    }

    private long DeleteOutsideLedger(string file)
    {
        var bytes = Length(file);
        if (!TryDelete(file))
        {
            return 0;
        }
        _pins.TryRemove(Path.GetFullPath(file), out _);
        return bytes;
    }

    private void ReleasePending(string file)
    {
        if (_pending.TryRemove(Path.GetFullPath(file), out var r))
        {
            _ledger.Release(r);
        }
    }

    private bool CreateExclusive(string candidate)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(candidate))!;
        if (!Directory.Exists(parent) && !RelayPaths.TryMakeDir(parent))
        {
            return false;
        }
        try
        {
            using (new FileStream(candidate, FileMode.CreateNew, FileAccess.Write))
            {
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        var root = Path.GetFullPath(_paths.StagingDir).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(candidate).StartsWith(root, StringComparison.Ordinal) || new FileInfo(candidate).LinkTarget is not null)
        {
            TryDelete(candidate);
            return false;
        }
        return true;
    }

    private static string Hex() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(PinSuffixBytes));

    private static List<FileInfo> Files(string dir)
    {
        try
        {
            return Directory.Exists(dir) ? [.. new DirectoryInfo(dir).GetFiles()] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static FileSystemInfo[] SafeEntries(DirectoryInfo dir)
    {
        try
        {
            return dir.GetFileSystemInfos();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static long Mtime(FileInfo f) => f.LastWriteTimeUtc.ToUnixMs();

    private static long Length(string file)
    {
        try
        {
            return File.Exists(file) ? new FileInfo(file).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    internal static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path) && new FileInfo(path).LinkTarget is null)
            {
                return false;
            }
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            Directory.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal static class FileTimeMs
{
    public static long ToUnixMs(this DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
}
