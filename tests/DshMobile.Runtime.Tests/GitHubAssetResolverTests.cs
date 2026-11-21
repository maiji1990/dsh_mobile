using System.Net;
using System.Text;
using DshMobile.Runtime.Updates;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// ADR-0022 amendment: when github.com's release-download host does not answer but api.github.com does, the manifest is
/// fetched through the REST API (tag -> asset id) instead. These tests stub the second path entirely.
/// </summary>
public sealed class GitHubAssetResolverTests
{
    private const string ManifestUrl = "https://github.com/maiji1990/dsh_mobile/releases/download/channel-preview/release-manifest.json";

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add(request.RequestUri!.Host + request.RequestUri.AbsolutePath);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(string body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };

    [Fact]
    public void ParsesReleaseDownloadUrls()
    {
        Assert.True(GitHubAssetResolver.TryParse(new Uri(ManifestUrl), out var asset));
        Assert.Equal(("maiji1990", "dsh_mobile", "channel-preview", "release-manifest.json"),
            (asset.Owner, asset.Repo, asset.Tag, asset.Asset));
        Assert.False(GitHubAssetResolver.TryParse(new Uri("https://example.com/releases/download/x/y"), out _));
        Assert.False(GitHubAssetResolver.TryParse(new Uri("https://github.com/o/r/releases/tag/v1"), out _));
    }

    [Fact]
    public async Task ResolvesTheAssetIdForTheMatchingNameOnly()
    {
        var handler = new StubHandler(_ => Json("""
            {"assets":[{"name":"other.zip","url":"https://api.github.com/repos/maiji1990/dsh_mobile/releases/assets/1"},
                       {"name":"release-manifest.json","url":"https://api.github.com/repos/maiji1990/dsh_mobile/releases/assets/2"}]}
            """));
        using var http = new HttpClient(handler);
        var resolved = await GitHubAssetResolver.ResolveAsync(http, new Uri(ManifestUrl), CancellationToken.None);
        Assert.Equal("https://api.github.com/repos/maiji1990/dsh_mobile/releases/assets/2", resolved!.AbsoluteUri);
    }

    [Fact]
    public async Task ReturnsNullWhenTheApiDoesNotKnowTheRelease()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);
        Assert.Null(await GitHubAssetResolver.ResolveAsync(http, new Uri(ManifestUrl), CancellationToken.None));
    }

    [Fact]
    public async Task ManifestSourceFallsBackToTheApiWhenTheDownloadHostIsBlocked()
    {
        var manifest = "{\"schema\":1}";
        var signature = "sig";
        var handler = new StubHandler(request =>
        {
            var host = request.RequestUri!.Host;
            if (host == "github.com")
            {
                throw new HttpRequestException("name or service not known (simulated block)");
            }
            if (request.RequestUri.AbsolutePath.Contains("/releases/tags/", StringComparison.Ordinal))
            {
                return Json("""
                    {"assets":[{"name":"release-manifest.json","url":"https://api.github.com/repos/maiji1990/dsh_mobile/releases/assets/2"},
                               {"name":"release-manifest.json.sig","url":"https://api.github.com/repos/maiji1990/dsh_mobile/releases/assets/3"}]}
                    """);
            }
            var isSig = request.RequestUri.AbsolutePath.EndsWith("/3", StringComparison.Ordinal);
            Assert.Contains("application/octet-stream", request.Headers.Accept.ToString(), StringComparison.Ordinal);
            return Bytes(isSig ? signature : manifest);
        });
        using var http = new HttpClient(handler);
        var files = await new HttpReleaseSource(http, new Uri(ManifestUrl)).FetchAsync(CancellationToken.None);
        Assert.Equal(manifest, Encoding.UTF8.GetString(files.Manifest));
        Assert.Equal(signature, Encoding.UTF8.GetString(files.Signature));
        Assert.Contains(handler.Calls, c => c.StartsWith("github.com/", StringComparison.Ordinal));
        Assert.Contains(handler.Calls, c => c.StartsWith("api.github.com/repos/maiji1990/dsh_mobile/releases/tags/", StringComparison.Ordinal));
    }
}
