using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DshMobile.Runtime.Updates;

/// <summary>A downloaded artifact whose size and SHA-256 matched its verified manifest entry when it was returned.</summary>
public sealed record VerifiedArtifact(ReleaseArtifact Artifact, string FilePath, string ManifestKeyId, DateTimeOffset ManifestGeneratedAt);

/// <summary>Resume state of a partial download (written before the first byte, so a killed process can resume).</summary>
public sealed record DownloadJournal(string Sha256, long Size, Uri Url, string? ETag, string? LastModified);

/// <summary>
/// Private on-disk store of release artifacts (vNext PR-04), under <see cref="RuntimeLayout.UpdatesDir"/>
/// (<c>files/updates</c>) — never under <c>runtime/runtime-current</c> or <c>user-data</c>:
/// <list type="bullet">
/// <item><c>partial/&lt;sha256&gt;.part</c> + <c>.part.json</c> (<see cref="DownloadJournal"/>): an unfinished download;
/// <b>never trusted</b>, only resumed and then hashed in full;</item>
/// <item><c>verified/&lt;sha256&gt;/&lt;archive&gt;</c> + <c>verified.json</c>: a complete file whose size and SHA-256
/// matched the verified manifest. Still re-hashed by <see cref="TryGetVerified"/> before it is handed out.</item>
/// </list>
/// Directory names are the 64-hex digest (validated), file names the manifest's plain archive name (validated again);
/// nothing from a URL or a server header ever becomes a path. Verified files beyond <see cref="MaxCacheBytes"/> are
/// evicted oldest first.
/// </summary>
public sealed partial class ArtifactStore(RuntimeLayout layout)
{
    /// <summary>Upper bound for all verified files together (a runtime bundle + a DSH layer + one older of each).</summary>
    public long MaxCacheBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    public string Root => layout.UpdatesDir;

    public string PartialDir => Path.Combine(Root, "partial");

    public string VerifiedDir => Path.Combine(Root, "verified");

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Hex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+\-]{0,127}$")]
    private static partial Regex FileName();

    /// <summary>Refuses anything that could escape the store: non-hex digests, names with separators or dot-prefixes.</summary>
    public static bool SafeNames(ReleaseArtifact a) =>
        Sha256Hex().IsMatch(a.Sha256) && FileName().IsMatch(a.Archive) && Path.GetFileName(a.Archive) == a.Archive && !a.Archive.Contains("..", StringComparison.Ordinal);

    public string PartFile(ReleaseArtifact a) => Path.Combine(PartialDir, a.Sha256 + ".part");

    public string JournalFile(ReleaseArtifact a) => PartFile(a) + ".json";

    public string VerifiedFile(ReleaseArtifact a) => Path.Combine(VerifiedDir, a.Sha256, a.Archive);

    private string VerifiedMeta(ReleaseArtifact a) => Path.Combine(VerifiedDir, a.Sha256, "verified.json");

    /// <summary>A verified file for this artifact, re-hashed now; a file that no longer matches is deleted.</summary>
    public VerifiedArtifact? TryGetVerified(ReleaseArtifact a, CancellationToken ct)
    {
        var file = VerifiedFile(a);
        if (!File.Exists(file))
        {
            return null;
        }
        if (!Matches(file, a, ct))
        {
            DeleteVerified(a);
            return null;
        }
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow); // LRU: recently used survives eviction
        var (keyId, generated) = ReadMeta(a);
        return new VerifiedArtifact(a, file, keyId, generated);
    }

    /// <summary>Whether <paramref name="file"/> has exactly the artifact's size and SHA-256 (the whole file, read now).</summary>
    public static bool Matches(string file, ReleaseArtifact a, CancellationToken ct)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length != a.Size)
        {
            return false;
        }
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 16];
        int n;
        while ((n = fs.Read(buffer)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, n);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset()) == a.Sha256;
    }

    /// <summary>Moves a fully verified part into <c>verified/</c> (rename in the same file system) and records its origin.</summary>
    internal VerifiedArtifact Promote(ReleaseArtifact a, VerifiedRelease release)
    {
        var dir = Path.Combine(VerifiedDir, a.Sha256);
        Directory.CreateDirectory(dir);
        var target = VerifiedFile(a);
        File.Move(PartFile(a), target, overwrite: true);
        WriteDurably(VerifiedMeta(a), Encoding.UTF8.GetBytes(JsonSerializeMeta(a, release)));
        DeletePartial(a);
        return new VerifiedArtifact(a, target, release.KeyId, release.Manifest.GeneratedAt);
    }

    internal DownloadJournal? ReadJournal(ReleaseArtifact a)
    {
        var path = JournalFile(a);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var r = doc.RootElement;
            string? Str(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new DownloadJournal(Str("sha256") ?? "", r.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : -1,
                Uri.TryCreate(Str("url"), UriKind.Absolute, out var u) ? u : new Uri("about:blank"), Str("etag"), Str("lastModified"));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null; // unreadable journal: the part is discarded and the download starts over
        }
    }

    internal void WriteJournal(DownloadJournal j)
    {
        Directory.CreateDirectory(PartialDir);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("sha256", j.Sha256);
            w.WriteNumber("size", j.Size);
            w.WriteString("url", j.Url.AbsoluteUri);
            if (j.ETag is not null)
            {
                w.WriteString("etag", j.ETag);
            }
            if (j.LastModified is not null)
            {
                w.WriteString("lastModified", j.LastModified);
            }
            w.WriteEndObject();
        }
        WriteDurably(Path.Combine(PartialDir, j.Sha256 + ".part.json"), ms.ToArray());
    }

    internal void DeletePartial(ReleaseArtifact a)
    {
        TryDelete(PartFile(a));
        TryDelete(JournalFile(a));
    }

    private void DeleteVerified(ReleaseArtifact a)
    {
        try
        {
            Directory.Delete(Path.Combine(VerifiedDir, a.Sha256), recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Bytes of all verified files.</summary>
    public long VerifiedBytes() =>
        Directory.Exists(VerifiedDir) ? Directory.EnumerateFiles(VerifiedDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;

    /// <summary>
    /// Makes room for <paramref name="incoming"/> bytes under <see cref="MaxCacheBytes"/> by deleting the least recently
    /// used verified artifacts (never <paramref name="keep"/>). False when even an empty cache would not fit it.
    /// </summary>
    internal bool MakeRoom(long incoming, ReleaseArtifact keep)
    {
        if (incoming > MaxCacheBytes)
        {
            return false;
        }
        if (!Directory.Exists(VerifiedDir))
        {
            return true;
        }
        var entries = Directory.EnumerateDirectories(VerifiedDir)
            .Where(d => Path.GetFileName(d) != keep.Sha256)
            .Select(d => (Dir: d, Bytes: Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length),
                Used: Directory.EnumerateFiles(d).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max()))
            .OrderBy(e => e.Used)
            .ToList();
        var total = VerifiedBytes();
        foreach (var e in entries)
        {
            if (total + incoming <= MaxCacheBytes)
            {
                break;
            }
            try
            {
                Directory.Delete(e.Dir, recursive: true);
                total -= e.Bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return total + incoming <= MaxCacheBytes;
    }

    private (string KeyId, DateTimeOffset Generated) ReadMeta(ReleaseArtifact a)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(VerifiedMeta(a)));
            var r = doc.RootElement;
            return (r.GetProperty("keyId").GetString() ?? "",
                DateTimeOffset.Parse(r.GetProperty("manifestGeneratedAt").GetString()!, CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return ("", DateTimeOffset.MinValue);
        }
    }

    private static string JsonSerializeMeta(ReleaseArtifact a, VerifiedRelease release)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("kind", a.Kind);
            w.WriteString("version", a.Version);
            w.WriteString("abi", a.Abi);
            w.WriteString("archive", a.Archive);
            w.WriteString("sha256", a.Sha256);
            w.WriteNumber("size", a.Size);
            w.WriteString("url", a.Url.AbsoluteUri);
            w.WriteString("channel", release.Manifest.Channel);
            w.WriteString("keyId", release.KeyId);
            w.WriteString("manifestGeneratedAt", release.Manifest.GeneratedAt.ToString("O", CultureInfo.InvariantCulture));
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteDurably(string path, byte[] content)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(content);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
