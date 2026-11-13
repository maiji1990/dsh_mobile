// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/storage/QuotaLedger.kt
// Registered in docs/UPSTREAMS.md. Locked by tests/TestVectors/relay-storage.json.

using System.Globalization;

namespace DshMobile.Runtime.Automation.Relay.Storage;

/// <summary>
/// Upstream <c>QuotaLedger</c>: storage accounting in 4 KiB blocks, every change under one lock and persisted at
/// once; writers <see cref="Reserve"/> first, then <see cref="Commit"/> the real size or <see cref="Release"/>.
/// A probe that fails counts as "no room".
/// </summary>
public sealed class QuotaLedger
{
    public const long DefaultMaxBytes = 200L * 1024 * 1024;
    public const long ArtifactTtlMs = 24L * 60L * 60L * 1000L;
    public const long OrphanRequestTtlMs = 10L * 60L * 1000L;
    public const long ResponseTtlMs = 30L * 60L * 1000L;
    public const long ReservedFloorBytes = 64L * 1024 * 1024;
    public const long BlockBytes = 4096L;
    private const string KeyTotal = "totalBytes";
    private const string KeyCount = "artifactCount";
    private const string KeyUpdated = "updatedAtWallClock";

    public sealed record Reservation(long Bytes, long Seq);

    private readonly string _file;
    private readonly Func<long> _wallNowMs;
    private readonly Lock _lock = new();
    private readonly Dictionary<long, long> _reservations = [];
    private long _reservedBytes;
    private long _nextSeq;

    public QuotaLedger(string file, long maxBytes = DefaultMaxBytes, Func<long>? wallNowMs = null)
    {
        _file = file;
        MaxBytes = maxBytes;
        _wallNowMs = wallNowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public long MaxBytes { get; }
    public long TotalBytes { get; private set; }
    public int ArtifactCount { get; private set; }
    public bool PersistHealthy { get; private set; } = true;

    /// <summary>Upstream <c>load</c>: a lost or half ledger is reported (reconcile from disk), never zeroed silently.</summary>
    public bool Load(Action<string>? onCorrupted = null)
    {
        if (!File.Exists(_file))
        {
            return true;
        }
        string text;
        try
        {
            text = File.ReadAllText(_file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onCorrupted?.Invoke("quota ledger unreadable");
            return false;
        }
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var index = line.IndexOf('=', StringComparison.Ordinal);
            if (index > 0)
            {
                map[line[..index]] = line[(index + 1)..];
            }
        }
        long total = -1;
        var count = 0;
        var ok = map.TryGetValue(KeyTotal, out var t) && long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out total)
            && map.TryGetValue(KeyCount, out var c) && int.TryParse(c, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out count);
        if (!ok || total < 0)
        {
            onCorrupted?.Invoke("quota ledger malformed, reconcile from disk required");
            return false;
        }
        lock (_lock)
        {
            TotalBytes = total;
            ArtifactCount = count;
        }
        return true;
    }

    public void RecordWritten(long bytes) => Mutate(Charged(bytes), () => ArtifactCount += 1);

    public void RecordFreed(long bytes) => Mutate(-Charged(bytes), () => ArtifactCount = Math.Max(0, ArtifactCount - 1));

    public Reservation? Reserve(long bytes)
    {
        lock (_lock)
        {
            var need = Charged(bytes);
            if (UsableLocked() < need)
            {
                return null;
            }
            var seq = _nextSeq++;
            _reservations[seq] = need;
            _reservedBytes += need;
            return new Reservation(need, seq);
        }
    }

    public void Commit(Reservation reservation, long actualBytes)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        lock (_lock)
        {
            if (!_reservations.Remove(reservation.Seq, out var pledged))
            {
                return;
            }
            _reservedBytes -= pledged;
            Mutate(Charged(actualBytes), () => ArtifactCount += 1);
        }
    }

    public void Release(Reservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        lock (_lock)
        {
            if (_reservations.Remove(reservation.Seq, out var pledged))
            {
                _reservedBytes -= pledged;
            }
        }
    }

    /// <summary>Upstream <c>reconcileFromDisk</c>: recount the artifacts directory (regular files only).</summary>
    public void ReconcileFromDisk(string artifactsDir)
    {
        var files = Directory.Exists(artifactsDir) ? new DirectoryInfo(artifactsDir).GetFiles() : [];
        lock (_lock)
        {
            TotalBytes = files.Sum(f => Charged(f.Length));
            ArtifactCount = files.Length;
            PersistLocked();
        }
    }

    public bool OverQuota()
    {
        lock (_lock)
        {
            return TotalBytes + _reservedBytes > MaxBytes;
        }
    }

    public long UsableBytes()
    {
        lock (_lock)
        {
            return UsableLocked();
        }
    }

    /// <summary>Upstream <c>deviceHasRoom</c>: free space above the size plus a 64 MiB floor; a failed probe is "no".</summary>
    public static bool DeviceHasRoom(long availableBytes, long bytes) => availableBytes > Charged(bytes) + ReservedFloorBytes;

    /// <summary>The persisted text (upstream <c>persistLocked</c> snapshot).</summary>
    public static string Snapshot(long total, int count, long wallNowMs) =>
        string.Create(CultureInfo.InvariantCulture, $"{KeyTotal}={total}\n{KeyCount}={count}\n{KeyUpdated}={wallNowMs}\n");

    /// <summary>Upstream <c>charged</c>: round up to whole 4 KiB blocks; non-positive is 0.</summary>
    public static long Charged(long bytes) => bytes <= 0 ? 0 : (bytes + BlockBytes - 1) / BlockBytes * BlockBytes;

    private long UsableLocked() => Math.Max(0, MaxBytes - TotalBytes - _reservedBytes);

    private void Mutate(long delta, Action extra)
    {
        lock (_lock)
        {
            TotalBytes = Math.Max(0, TotalBytes + delta);
            extra();
            PersistLocked();
        }
    }

    private void PersistLocked()
    {
        var snapshot = Snapshot(TotalBytes, ArtifactCount, _wallNowMs());
        try
        {
            var dir = Path.GetDirectoryName(_file)!;
            Directory.CreateDirectory(dir);
            var temp = Path.Combine(dir, "quota-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(temp, snapshot);
            try
            {
                File.Move(temp, _file, overwrite: true);
            }
            catch (IOException)
            {
                File.WriteAllText(_file, snapshot);
                File.Delete(temp);
            }
            PersistHealthy = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PersistHealthy = false;
        }
    }
}
