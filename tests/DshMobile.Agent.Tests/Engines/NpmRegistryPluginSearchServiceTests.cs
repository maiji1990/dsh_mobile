using System.Globalization;
using System.Net;
using System.Text;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;

namespace DshMobile.Agent.Tests.Engines;

public sealed class NpmRegistryPluginSearchServiceTests
{
    [Fact]
    public async Task SearchAsync_ParsesValidPackages_FiltersInvalidNames_SanitizesUrlsAndSecrets()
    {
        Uri? capturedUri = null;
        var payload = """
            {
              "total": 42,
              "objects": [
                {
                  "package": {
                    "name": "@deepseek-ai/dsh-timer",
                    "version": "1.2.3",
                    "description": "延时任务插件 token=sk-live-secret-12345",
                    "keywords": ["dsh", "timer", "DSH", "  ", "cron"],
                    "date": "2026-06-01T12:00:00.000Z",
                    "links": {
                      "npm": "https://www.npmjs.com/package/@deepseek-ai/dsh-timer"
                    },
                    "publisher": {
                      "username": "deepseek"
                    }
                  }
                },
                {
                  "package": {
                    "name": "community-dsh-helper",
                    "version": "not-semver-tag",
                    "description": "社区辅助插件",
                    "keywords": ["helper"],
                    "date": "invalid-date",
                    "links": {
                      "npm": "javascript:alert(1)"
                    },
                    "publisher": {
                      "username": "alice"
                    }
                  }
                },
                {
                  "package": {
                    "name": "@deepseek-ai/dsh-timer",
                    "version": "1.2.2",
                    "description": "重复包名应去重"
                  }
                },
                {
                  "package": {
                    "name": "../evil-traversal",
                    "version": "1.0.0",
                    "description": "非法路径穿越"
                  }
                },
                {
                  "package": {
                    "name": "bad; rm -rf /",
                    "version": "1.0.0",
                    "description": "非法命令注入"
                  }
                },
                {
                  "package": {
                    "name": "UpperCasePkg",
                    "version": "1.0.0",
                    "description": "非法大写包名"
                  }
                }
              ]
            }
            """;

        using var handler = new StubHttpMessageHandler((req, _) =>
        {
            capturedUri = req.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
        });
        using var service = new NpmRegistryPluginSearchService(handler);

        var outcome = await service.SearchAsync("  dsh timer  ", limit: 99);

        Assert.True(outcome.IsOk);
        Assert.NotNull(capturedUri);
        Assert.Equal("https", capturedUri!.Scheme);
        Assert.Equal("registry.npmjs.org", capturedUri.Host);
        Assert.Equal("/-/v1/search", capturedUri.AbsolutePath);
        Assert.Contains("text=dsh%20timer", capturedUri.Query, StringComparison.Ordinal);
        Assert.Contains("size=30", capturedUri.Query, StringComparison.Ordinal);

        var result = outcome.Value!;
        Assert.Equal(42, result.TotalCount);
        Assert.Equal(2, result.Items.Count);

        var official = result.Items[0];
        Assert.Equal("@deepseek-ai/dsh-timer", official.PackageName);
        Assert.Equal("1.2.3", official.Version);
        Assert.Equal("@deepseek-ai/dsh-timer@1.2.3", official.RecommendedInstallSpec);
        Assert.True(official.IsOfficialScope);
        Assert.False(official.IsInstalled);
        Assert.Null(official.InstalledVersion);
        Assert.Equal("deepseek", official.Publisher);
        Assert.Equal(DateTimeOffset.Parse("2026-06-01T12:00:00.000Z", CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(), official.PublishedAtUnixMs);
        Assert.Equal("https://www.npmjs.com/package/@deepseek-ai/dsh-timer", official.NpmUrl);
        Assert.Equal(["dsh", "timer", "cron"], official.Keywords);
        Assert.DoesNotContain("sk-live-secret-12345", official.Description, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", official.Description, StringComparison.Ordinal);

        var community = result.Items[1];
        Assert.Equal("community-dsh-helper", community.PackageName);
        Assert.Equal("community-dsh-helper", community.RecommendedInstallSpec);
        Assert.False(community.IsOfficialScope);
        Assert.Null(community.PublishedAtUnixMs);
        Assert.Null(community.NpmUrl);
    }

    [Fact]
    public async Task SearchAsync_EmptyQueryUsesDefaultDiscoveryQuery_AndClampsMinLimit()
    {
        Uri? capturedUri = null;
        using var handler = new StubHttpMessageHandler((req, _) =>
        {
            capturedUri = req.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"objects":[],"total":0}""", Encoding.UTF8, "application/json"),
            });
        });
        using var service = new NpmRegistryPluginSearchService(handler);

        var outcome = await service.SearchAsync("   ", limit: 0);

        Assert.True(outcome.IsOk);
        Assert.NotNull(capturedUri);
        Assert.Contains($"text={Uri.EscapeDataString(NpmRegistryPluginSearchService.DefaultDiscoveryQuery)}", capturedUri!.Query, StringComparison.Ordinal);
        Assert.Contains("size=1", capturedUri.Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public async Task SearchAsync_NonSuccessStatus_ReturnsStableTransportFailure(HttpStatusCode statusCode, bool expectedRetryable)
    {
        using var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(statusCode)));
        using var service = new NpmRegistryPluginSearchService(handler);

        var outcome = await service.SearchAsync("dsh");

        Assert.False(outcome.IsOk);
        Assert.Equal(DshFailureCode.TransportLost, outcome.Error!.Code);
        Assert.Equal($"plugins.catalog_http_{(int)statusCode}", outcome.Error.DiagnosticKey);
        Assert.Equal(expectedRetryable, outcome.Error.Retryable);
    }

    [Theory]
    [InlineData("{invalid json")]
    [InlineData("null")]
    [InlineData("""{"total":5}""")]
    public async Task SearchAsync_MalformedJsonOrMissingObjects_ReturnsProtocolMismatch(string body)
    {
        using var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            }));
        using var service = new NpmRegistryPluginSearchService(handler);

        var outcome = await service.SearchAsync("dsh");

        Assert.False(outcome.IsOk);
        Assert.Equal(DshFailureCode.ProtocolMismatch, outcome.Error!.Code);
    }

    [Fact]
    public async Task SearchAsync_HttpRequestException_ReturnsTransportLost()
    {
        using var handler = new StubHttpMessageHandler((_, _) =>
            throw new HttpRequestException("DNS lookup failed"));
        using var service = new NpmRegistryPluginSearchService(handler);

        var outcome = await service.SearchAsync("dsh");

        Assert.False(outcome.IsOk);
        Assert.Equal(DshFailureCode.TransportLost, outcome.Error!.Code);
        Assert.True(outcome.Error.Retryable);
    }

    [Fact]
    public async Task SearchAsync_InternalTimeoutReturnsTimeoutFailure_WhileCallerCancellationPropagates()
    {
        using var timeoutHandler = new StubHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var timeoutService = new NpmRegistryPluginSearchService(timeoutHandler, requestTimeout: TimeSpan.FromMilliseconds(40));

        var timeoutOutcome = await timeoutService.SearchAsync("dsh");
        Assert.False(timeoutOutcome.IsOk);
        Assert.Equal(DshFailureCode.Timeout, timeoutOutcome.Error!.Code);
        Assert.True(timeoutOutcome.Error.Retryable);

        using var callerCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        using var slowService = new NpmRegistryPluginSearchService(timeoutHandler, requestTimeout: TimeSpan.FromSeconds(10), disposeHandler: false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slowService.SearchAsync("dsh", ct: callerCts.Token));
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
