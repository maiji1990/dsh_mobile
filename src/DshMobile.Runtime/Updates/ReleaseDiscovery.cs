using System.Net.Http.Headers;

namespace DshMobile.Runtime.Updates;

/// <summary>The two files of a release: the manifest and its detached signature envelope, as raw bytes.</summary>
public sealed record ReleaseFiles(byte[] Manifest, byte[] Signature);

public interface IReleaseSource
{
    /// <summary>Fetches both files; failures are <see cref="ReleaseSourceException"/> with a stable code.</summary>
    Task<ReleaseFiles> FetchAsync(CancellationToken ct);
}

public sealed class ReleaseSourceException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Fetches <c>&lt;manifest url&gt;</c> and <c>&lt;manifest url&gt;.sig</c> anonymously over https (ADR-0022: binaries and
/// manifests live on a public release host; the private source repository is never contacted and no token, PAT or
/// GitHub credential is sent or embedded). Each file is capped at <see cref="MaxBytes"/>.
/// </summary>
public sealed class HttpReleaseSource : IReleaseSource
{
    public const int MaxBytes = 1024 * 1024;

    private readonly HttpClient _http;
    private readonly Uri _manifest;

    public HttpReleaseSource(HttpClient http, Uri manifest)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(manifest);
        if (!manifest.IsAbsoluteUri || manifest.Scheme != Uri.UriSchemeHttps || manifest.UserInfo.Length > 0)
        {
            throw new ArgumentException("the release manifest URL must be https without credentials", nameof(manifest));
        }
        _http = http;
        _manifest = manifest;
    }

    public async Task<ReleaseFiles> FetchAsync(CancellationToken ct)
    {
        var manifest = await GetAsync(_manifest, ct).ConfigureAwait(false);
        var sig = await GetAsync(new Uri(_manifest.AbsoluteUri + ".sig"), ct).ConfigureAwait(false);
        return new ReleaseFiles(manifest, sig);
    }

    private async Task<byte[]> GetAsync(Uri uri, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(uri, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new ReleaseSourceException(UpdateErrorCodes.FetchFailed, $"{uri.Host}: {e.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ReleaseSourceException(UpdateErrorCodes.FetchFailed,
                $"{uri.Host}: timed out after {GitHubAssetResolver.DirectAttemptTimeout.TotalSeconds:F0} s and the api.github.com fallback did not answer");
        }
        using (response)
        {
            if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
            {
                throw new ReleaseSourceException(UpdateErrorCodes.FetchFailed, $"redirected off https: {final}");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new ReleaseSourceException(UpdateErrorCodes.FetchFailed, $"{uri}: HTTP {(int)response.StatusCode}");
            }
            // the API asset endpoint redirects to the CDN; the bytes are verified by the caller, so only https matters.
            if (response.Content.Headers.ContentLength > MaxBytes)
            {
                throw new ReleaseSourceException(UpdateErrorCodes.TooLarge, $"{uri}: {response.Content.Headers.ContentLength} bytes (max {MaxBytes})");
            }
            var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                int n;
                while ((n = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + n > MaxBytes)
                    {
                        throw new ReleaseSourceException(UpdateErrorCodes.TooLarge, $"{uri}: more than {MaxBytes} bytes");
                    }
                    buffer.Write(chunk, 0, n);
                }
                return buffer.ToArray();
            }
        }
    }

    /// <summary>
    /// Direct attempt (short timeout) then, if the canonical release-download host is unreachable, the same asset through
    /// <see cref="GitHubAssetResolver"/> (ADR-0022 amendment: some networks block github.com but not api.github.com).
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            return await SendOnceAsync(uri, ct, direct: true, assetMediaType: false).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            var viaApi = await GitHubAssetResolver.ResolveAsync(_http, uri, ct).ConfigureAwait(false);
            if (viaApi is null)
            {
                throw;
            }
            return await SendOnceAsync(viaApi, ct, direct: false, assetMediaType: true).ConfigureAwait(false);
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(Uri uri, CancellationToken ct, bool direct, bool assetMediaType)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        if (assetMediaType)
        {
            GitHubAssetResolver.UseAssetMediaType(request);
        }
        using var budget = direct ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
        if (budget is not null)
        {
            budget.CancelAfter(GitHubAssetResolver.DirectAttemptTimeout);
        }
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget?.Token ?? ct).ConfigureAwait(false);
    }
}

/// <summary>Outcome of a discovery run; <see cref="Choices"/> is empty unless <see cref="Ok"/>.</summary>
public sealed record ReleaseDiscoveryResult(bool Ok, string Code, string? Message, ReleaseManifest? Manifest, string? KeyId, IReadOnlyList<ReleaseChoice> Choices)
{
    /// <summary>Set only on success: what the artifact downloader accepts (<see cref="VerifiedRelease"/>).</summary>
    public VerifiedRelease? Release { get; init; }
}

/// <summary>
/// Read-only "what could I update to?" (P0-03): fetch → verify signature and content → evaluate the catalog. Nothing is
/// downloaded besides the two small files, nothing is installed, and no state is written: the caller persists
/// <see cref="ReleaseManifest.GeneratedAt"/> of an accepted manifest and passes it back as <c>notOlderThan</c>.
/// </summary>
public static class ReleaseDiscovery
{
    public static async Task<ReleaseDiscoveryResult> CheckAsync(
        IReleaseSource source,
        ReleaseTrust trust,
        string channel,
        InstalledVersions installed,
        string deviceAbi,
        DateTimeOffset? notOlderThan,
        TimeProvider time,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentNullException.ThrowIfNull(time);
        if (trust.IsEmpty)
        {
            // Do not even contact the host: there is nothing we could trust in the answer.
            return new(false, UpdateErrorCodes.NoTrustedKeys, "this build pins no release public key; online updates are unavailable", null, null, []);
        }
        ReleaseFiles files;
        try
        {
            files = await source.FetchAsync(ct).ConfigureAwait(false);
        }
        catch (ReleaseSourceException e)
        {
            return new(false, e.Code, e.Message, null, null, []);
        }
        var v = ReleaseManifestVerifier.Verify(files.Manifest, files.Signature, trust, channel, time.GetUtcNow(), notOlderThan);
        if (!v.Ok)
        {
            return new(false, v.Code, v.Message, null, null, []);
        }
        return new(true, RuntimeErrorCodes.Ok, null, v.Manifest, v.KeyId, ReleaseCatalog.Evaluate(v.Manifest!, installed, deviceAbi)) { Release = v.Release };
    }
}
