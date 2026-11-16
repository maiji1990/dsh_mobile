using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using DshMobile.Runtime.Hosting;

namespace DshMobile.Runtime.Updates;

/// <summary>Stable error codes of the artifact downloader (vNext PR-04); shown as-is, never mapped to success.</summary>
public static class DownloadErrorCodes
{
    /// <summary>The artifact is not an entry of the given verified manifest (or has unsafe names): nothing is fetched.</summary>
    public const string NotOffered = "DOWNLOAD_NOT_OFFERED";
    public const string InsufficientSpace = "DOWNLOAD_INSUFFICIENT_SPACE";
    public const string CacheFull = "DOWNLOAD_CACHE_FULL";
    public const string HttpFailed = "DOWNLOAD_HTTP_FAILED";
    public const string InsecureRedirect = "DOWNLOAD_INSECURE_REDIRECT";

    /// <summary>The connection ended before the expected size; the partial file is kept and the next attempt resumes.</summary>
    public const string Incomplete = "DOWNLOAD_INCOMPLETE";

    /// <summary>More bytes than the manifest's size, or a Content-Range / Content-Length that contradicts it.</summary>
    public const string SizeMismatch = "DOWNLOAD_SIZE_MISMATCH";

    /// <summary>The complete file does not hash to the manifest's SHA-256; it is deleted, never installable.</summary>
    public const string Sha256Mismatch = "DOWNLOAD_SHA256_MISMATCH";
    public const string WriteFailed = "DOWNLOAD_WRITE_FAILED";
    public const string Cancelled = "DOWNLOAD_CANCELLED";
}

/// <summary>Progress of one artifact: bytes on disk so far (including a resumed prefix) of <see cref="Total"/>.</summary>
public sealed record DownloadProgress(string Archive, long Received, long Total);

/// <summary>Outcome of <see cref="ArtifactDownloader.DownloadAsync"/>; <see cref="File"/> only when <see cref="Ok"/>.</summary>
public sealed record DownloadResult(bool Ok, string Code, string? Message, VerifiedArtifact? File, bool Resumed = false, bool FromCache = false)
{
    internal static DownloadResult Fail(string code, string message) => new(false, code, message, null);
}

/// <summary>
/// Downloads one artifact of a <see cref="VerifiedRelease"/> into <see cref="ArtifactStore"/> (vNext PR-04). It never
/// touches <c>runtime/runtime-current</c> or <c>user-data</c>, and nothing it produces is installable unless the whole
/// file's size and SHA-256 equal the signed manifest's:
/// <list type="bullet">
/// <item>only an artifact the verified manifest offers, with safe names, is fetched (<see cref="DownloadErrorCodes.NotOffered"/>);</item>
/// <item>an already verified copy is re-hashed and returned without network;</item>
/// <item>free space (remaining bytes + <see cref="SpaceReserve"/>) and the cache cap are checked before any byte is fetched;</item>
/// <item>streaming to <c>partial/&lt;sha256&gt;.part</c>; the resume journal is written first, so a killed process resumes;</item>
/// <item>resume = <c>Range: bytes=&lt;have&gt;-</c> + <c>If-Range</c> (ETag, else Last-Modified). Only a 206 whose
/// Content-Range starts at exactly that offset and totals the manifest size is appended; a 200 (range ignored, or the
/// entity changed) starts over from byte 0; nothing else is accepted;</item>
/// <item>every hop must stay https (the initial URL already is, by the manifest parser); a redirect off https fails;</item>
/// <item>more bytes than the manifest size fail at once; a short body keeps the part for the next attempt;</item>
/// <item>at the end the <b>entire</b> file is hashed from disk — a resumed prefix is never trusted — and only then
/// renamed into <c>verified/</c>; a mismatch deletes it.</item>
/// </list>
/// One download at a time per downloader.
/// </summary>
public sealed class ArtifactDownloader : IDisposable
{
    /// <summary>Free space left over after the download (the install that follows needs room too; PR-05 checks its own).</summary>
    public const long SpaceReserve = 64L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly ArtifactStore _store;
    private readonly Func<string, long> _freeBytes;
    private readonly SemaphoreSlim _one = new(1, 1);

    /// <param name="freeBytes">Free bytes on the file system of a path (-1 = unknown); production: <see cref="RuntimeStatusReader.FreeBytes"/>.</param>
    public ArtifactDownloader(HttpClient http, ArtifactStore store, Func<string, long>? freeBytes = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _freeBytes = freeBytes ?? RuntimeStatusReader.FreeBytes;
    }

    public void Dispose() => _one.Dispose();

    public async Task<DownloadResult> DownloadAsync(VerifiedRelease release, ReleaseArtifact artifact, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(artifact);
        if (!release.Offers(artifact) || !ArtifactStore.SafeNames(artifact) || artifact.Url.Scheme != Uri.UriSchemeHttps)
        {
            return DownloadResult.Fail(DownloadErrorCodes.NotOffered, $"{artifact.Kind} {artifact.Version} is not an artifact of the verified manifest");
        }
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await DownloadCoreAsync(release, artifact, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return DownloadResult.Fail(DownloadErrorCodes.Cancelled, "download cancelled; the partial file is kept and resumes next time");
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<DownloadResult> DownloadCoreAsync(VerifiedRelease release, ReleaseArtifact a, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (_store.TryGetVerified(a, ct) is { } cached)
        {
            return new DownloadResult(true, RuntimeErrorCodes.Ok, null, cached, FromCache: true);
        }

        // Resume only a part whose journal describes exactly this artifact.
        var part = _store.PartFile(a);
        var journal = _store.ReadJournal(a);
        var have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (journal is null || journal.Sha256 != a.Sha256 || journal.Size != a.Size || journal.Url != a.Url || have > a.Size)
        {
            _store.DeletePartial(a);
            journal = null;
            have = 0;
        }

        try
        {
            Directory.CreateDirectory(_store.PartialDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return DownloadResult.Fail(DownloadErrorCodes.WriteFailed, $"cannot create {_store.PartialDir}: {e.Message}");
        }
        var free = _freeBytes(_store.PartialDir);
        if (free >= 0 && free < a.Size - have + SpaceReserve)
        {
            return DownloadResult.Fail(DownloadErrorCodes.InsufficientSpace,
                $"{a.Archive} needs {a.Size - have} more bytes plus a {SpaceReserve} byte reserve; {free} free");
        }
        if (!_store.MakeRoom(a.Size, a))
        {
            return DownloadResult.Fail(DownloadErrorCodes.CacheFull, $"{a.Archive} ({a.Size} bytes) does not fit the update cache ({_store.MaxCacheBytes} bytes)");
        }

        var resumed = false;
        if (have == a.Size)
        {
            resumed = true; // complete on disk already (killed after the last byte): just verify below
        }
        else
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, a.Url);
            if (have > 0 && journal is not null && (journal.ETag ?? journal.LastModified) is { } validator)
            {
                request.Headers.Range = new RangeHeaderValue(have, null);
                request.Headers.TryAddWithoutValidation("If-Range", validator);
            }
            else
            {
                have = 0;
            }
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException e)
            {
                return DownloadResult.Fail(DownloadErrorCodes.HttpFailed, $"{a.Url.Host}: {e.Message}");
            }
            using (response)
            {
                if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
                {
                    return DownloadResult.Fail(DownloadErrorCodes.InsecureRedirect, $"redirected off https: {final.Scheme}://{final.Host}");
                }
                long offset;
                if (response.StatusCode == HttpStatusCode.PartialContent && have > 0)
                {
                    var range = response.Content.Headers.ContentRange;
                    if (range is not { HasRange: true, From: { } from } || from != have || range.Length != a.Size)
                    {
                        _store.DeletePartial(a);
                        return DownloadResult.Fail(DownloadErrorCodes.SizeMismatch, $"Content-Range {range} does not continue byte {have} of {a.Size}");
                    }
                    offset = have;
                    resumed = true;
                }
                else if (response.StatusCode == HttpStatusCode.OK)
                {
                    offset = 0; // full body: range ignored or the entity changed (If-Range) -> start over
                }
                else
                {
                    return DownloadResult.Fail(DownloadErrorCodes.HttpFailed, $"{a.Url}: HTTP {(int)response.StatusCode}");
                }
                if (response.Content.Headers.ContentLength is { } len && len != a.Size - offset)
                {
                    _store.DeletePartial(a);
                    return DownloadResult.Fail(DownloadErrorCodes.SizeMismatch, $"server announces {len} bytes, the manifest needs {a.Size - offset} more");
                }
                // Journal first: a process killed mid-stream resumes from what is on disk.
                _store.WriteJournal(new DownloadJournal(a.Sha256, a.Size, a.Url,
                    response.Headers.ETag?.ToString(),
                    response.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture)));
                var r = await CopyAsync(response, part, offset, a, progress, ct).ConfigureAwait(false);
                if (r is not null)
                {
                    if (r.Code == DownloadErrorCodes.SizeMismatch)
                    {
                        _store.DeletePartial(a); // an oversized body is not a prefix of anything: no resume
                    }
                    return r;
                }
            }
        }

        // The whole file, from disk: whatever was resumed, appended or left over is checked as one.
        if (!ArtifactStore.Matches(part, a, ct))
        {
            _store.DeletePartial(a);
            return DownloadResult.Fail(DownloadErrorCodes.Sha256Mismatch, $"{a.Archive} does not match the manifest's SHA-256 / size; deleted");
        }
        try
        {
            return new DownloadResult(true, RuntimeErrorCodes.Ok, null, _store.Promote(a, release), Resumed: resumed);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return DownloadResult.Fail(DownloadErrorCodes.WriteFailed, $"cannot store the verified file: {e.Message}");
        }
    }

    /// <summary>Streams the body to <paramref name="part"/> at <paramref name="offset"/>; null when the expected size was reached.</summary>
    private static async Task<DownloadResult?> CopyAsync(HttpResponseMessage response, string part, long offset, ReleaseArtifact a,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var received = offset;
        try
        {
            var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                var fs = new FileStream(part, offset == 0 ? FileMode.Create : FileMode.Open, FileAccess.Write, FileShare.None, 1 << 16);
                await using (fs.ConfigureAwait(false))
                {
                    fs.SetLength(offset); // drop anything beyond the agreed offset
                    fs.Seek(offset, SeekOrigin.Begin);
                    var buffer = new byte[1 << 16];
                    int n;
                    while ((n = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        if (received + n > a.Size)
                        {
                            return DownloadResult.Fail(DownloadErrorCodes.SizeMismatch, $"{a.Archive}: more than the manifest's {a.Size} bytes");
                        }
                        await fs.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                        received += n;
                        progress?.Report(new DownloadProgress(a.Archive, received, a.Size));
                    }
                    await fs.FlushAsync(ct).ConfigureAwait(false);
                    fs.Flush(flushToDisk: true);
                }
            }
        }
        catch (HttpRequestException e)
        {
            return DownloadResult.Fail(DownloadErrorCodes.Incomplete, $"{a.Archive}: connection lost after {received} of {a.Size} bytes ({e.Message}); resumes next time");
        }
        catch (IOException e) when (e is not FileNotFoundException)
        {
            // A read failure (connection reset) or a write failure (disk full): the bytes on disk are a correct prefix
            // or get replaced on resume; either way the final whole-file hash decides.
            return DownloadResult.Fail(received < a.Size ? DownloadErrorCodes.Incomplete : DownloadErrorCodes.WriteFailed,
                $"{a.Archive}: stopped after {received} of {a.Size} bytes ({e.Message}); resumes next time");
        }
        catch (UnauthorizedAccessException e)
        {
            return DownloadResult.Fail(DownloadErrorCodes.WriteFailed, $"{a.Archive}: {e.Message}");
        }
        if (received < a.Size)
        {
            return DownloadResult.Fail(DownloadErrorCodes.Incomplete, $"{a.Archive}: connection ended after {received} of {a.Size} bytes; resumes next time");
        }
        return null;
    }
}
