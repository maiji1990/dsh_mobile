// ADR-0022 amendment (2026-11-21): on some networks github.com's release-download edge is unreachable (DNS/edge
// blocking) while api.github.com and objects.githubusercontent.com still answer. The same asset is then resolved through
// the REST API by tag + asset name; bytes are still signature/sha256 verified by the callers, so this only changes
// *how* the file is fetched, never what is trusted.
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DshMobile.Runtime.Updates;

/// <summary>A <c>github.com/&lt;owner&gt;/&lt;repo&gt;/releases/download/&lt;tag&gt;/&lt;asset&gt;</c> URL split into its parts.</summary>
public readonly partial record struct GitHubReleaseAsset(string Owner, string Repo, string Tag, string Asset);

/// <summary>Fetches release assets through <c>api.github.com</c> when the canonical download host does not answer.</summary>
public static partial class GitHubAssetResolver
{
    public const string ApiHost = "api.github.com";

    /// <summary>How long the direct download host gets before the API fallback is used.</summary>
    public static readonly TimeSpan DirectAttemptTimeout = TimeSpan.FromSeconds(6);

    [GeneratedRegex("^https://github\\.com/(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+)/releases/download/(?<tag>[^/]+)/(?<asset>[^/?#]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseDownload();

    public static bool TryParse(Uri url, out GitHubReleaseAsset asset)
    {
        asset = default;
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }
        var m = ReleaseDownload().Match(url.AbsoluteUri);
        if (!m.Success)
        {
            return false;
        }
        asset = new GitHubReleaseAsset(
            m.Groups["owner"].Value, m.Groups["repo"].Value, m.Groups["tag"].Value, Uri.UnescapeDataString(m.Groups["asset"].Value));
        return true;
    }

    /// <summary>
    /// The API asset URL (<c>api.github.com/repos/.../releases/assets/{id}</c>) for <paramref name="canonical"/>, or null
    /// when the URL is not a GitHub release download, the API is unreachable or the asset name is not in that release.
    /// </summary>
    public static async Task<Uri?> ResolveAsync(HttpClient http, Uri canonical, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (!TryParse(canonical, out var asset))
        {
            return null;
        }
        var tagUrl = $"https://{ApiHost}/repos/{asset.Owner}/{asset.Repo}/releases/tags/{Uri.EscapeDataString(asset.Tag)}";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, tagUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd("dshmobile-update/1");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            foreach (var entry in assets.EnumerateArray())
            {
                if (!entry.TryGetProperty("name", out var name) || name.GetString() != asset.Asset)
                {
                    continue;
                }
                if (entry.TryGetProperty("url", out var url) && url.GetString() is { } text
                    && Uri.TryCreate(text, UriKind.Absolute, out var parsed)
                    && parsed.Scheme == Uri.UriSchemeHttps && parsed.Host == ApiHost)
                {
                    return parsed;
                }
            }
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The API asset endpoint returns the bytes only for this media type; the CDN redirect keeps it.</summary>
    public static void UseAssetMediaType(HttpRequestMessage request) =>
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
}
