using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using DshMobile.Agent.Domain;

namespace DshMobile.Agent.Engines;

/// <summary>
/// 基于官方 npm Registry HTTPS Search API（<c>https://registry.npmjs.org/-/v1/search</c>）的纯托管远端插件搜索服务：
/// 严格固定 HTTPS 主机、白名单过滤非法包名、全链路 STJ Source-Gen 反序列化（Trim/AOT 安全）并对诊断信息脱敏。
/// </summary>
public sealed class NpmRegistryPluginSearchService : IPluginCatalogSearch, IDisposable
{
    /// <summary>当用户未输入关键词时使用的默认推荐发现搜索词。</summary>
    public const string DefaultDiscoveryQuery = "@deepseek-ai/dsh";

    private const string RegistryHost = "registry.npmjs.org";
    private const string SearchEndpointBase = "https://registry.npmjs.org/-/v1/search";
    private const int MinLimit = 1;
    private const int MaxLimit = 30;
    private const int MaxKeywordsPerEntry = 8;
    private const int MaxDescriptionLength = 240;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(12);

    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;
    private readonly TimeSpan _requestTimeout;
    private int _disposed;

    /// <summary>使用默认安全 <see cref="HttpClient"/> 配置构造远端插件搜索服务。</summary>
    public NpmRegistryPluginSearchService()
        : this(CreateDefaultHttpClient(), disposeClient: true, DefaultTimeout)
    {
    }

    /// <summary>使用外部注入的 <see cref="HttpClient"/> 构造远端插件搜索服务（便于连接复用或离线测试）。</summary>
    public NpmRegistryPluginSearchService(HttpClient httpClient, bool disposeClient = false)
        : this(httpClient, disposeClient, DefaultTimeout)
    {
    }

    /// <summary>供单元测试注入自定义消息处理器与超时时间。</summary>
    internal NpmRegistryPluginSearchService(
        HttpMessageHandler handler,
        TimeSpan? requestTimeout = null,
        bool disposeHandler = true)
        : this(
            new HttpClient(handler ?? throw new ArgumentNullException(nameof(handler)), disposeHandler)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            },
            disposeClient: true,
            requestTimeout ?? DefaultTimeout)
    {
    }

    private NpmRegistryPluginSearchService(HttpClient httpClient, bool disposeClient, TimeSpan requestTimeout)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _disposeClient = disposeClient;
        _requestTimeout = requestTimeout > TimeSpan.Zero ? requestTimeout : DefaultTimeout;
    }

    /// <inheritdoc />
    public async Task<Outcome<PluginCatalogSearchResult>> SearchAsync(
        string query,
        int limit = 25,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        var effectiveQuery = string.IsNullOrWhiteSpace(query)
            ? DefaultDiscoveryQuery
            : query.Trim();
        var clampedLimit = Math.Clamp(limit, MinLimit, MaxLimit);

        var requestUri = new Uri(
            $"{SearchEndpointBase}?text={Uri.EscapeDataString(effectiveQuery)}&size={clampedLimit}",
            UriKind.Absolute);

        // 防御性主机与协议锁定：杜绝任何非官方 HTTPS 主机请求。
        if (!string.Equals(requestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(requestUri.Host, RegistryHost, StringComparison.OrdinalIgnoreCase))
        {
            return Outcome<PluginCatalogSearchResult>.Fail(
                DshFailureCode.InvalidState,
                "plugins.catalog_invalid_endpoint",
                retryable: false);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("DshMobile/1.0");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_requestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            return Outcome<PluginCatalogSearchResult>.Fail(
                DshFailureCode.Timeout,
                "plugins.catalog_timeout",
                retryable: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return Outcome<PluginCatalogSearchResult>.Fail(
                DshFailureCode.TransportLost,
                "plugins.catalog_http_error",
                retryable: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var diagnostic = SecretRedactor.RedactAndTruncate($"plugins.catalog_transport:{ex.GetType().Name}", 160);
            return Outcome<PluginCatalogSearchResult>.Fail(
                DshFailureCode.TransportLost,
                diagnostic,
                retryable: true);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                var retryable = statusCode == 429 || statusCode >= 500;
                return Outcome<PluginCatalogSearchResult>.Fail(
                    DshFailureCode.TransportLost,
                    $"plugins.catalog_http_{statusCode}",
                    retryable);
            }

            NpmSearchResponseDto? payload;
            try
            {
                await using var responseStream = await response.Content
                    .ReadAsStreamAsync(timeoutCts.Token)
                    .ConfigureAwait(false);
                payload = await JsonSerializer
                    .DeserializeAsync(responseStream, NpmRegistrySearchJsonContext.Default.NpmSearchResponseDto, timeoutCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
            {
                return Outcome<PluginCatalogSearchResult>.Fail(
                    DshFailureCode.Timeout,
                    "plugins.catalog_timeout",
                    retryable: true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (JsonException)
            {
                return Outcome<PluginCatalogSearchResult>.Fail(
                    DshFailureCode.ProtocolMismatch,
                    "plugins.catalog_invalid_json",
                    retryable: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var diagnostic = SecretRedactor.RedactAndTruncate($"plugins.catalog_read_failed:{ex.GetType().Name}", 160);
                return Outcome<PluginCatalogSearchResult>.Fail(
                    DshFailureCode.TransportLost,
                    diagnostic,
                    retryable: true);
            }

            if (payload is null || payload.Objects is null)
            {
                return Outcome<PluginCatalogSearchResult>.Fail(
                    DshFailureCode.ProtocolMismatch,
                    "plugins.catalog_invalid_payload",
                    retryable: false);
            }

            var entries = MapValidEntries(payload.Objects);
            var totalCount = Math.Max(entries.Count, payload.Total);
            return Outcome<PluginCatalogSearchResult>.Ok(new PluginCatalogSearchResult(
                Items: entries,
                TotalCount: totalCount));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }

    private static List<PluginCatalogEntryView> MapValidEntries(List<NpmSearchObjectDto> objects)
    {
        var seenPackages = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<PluginCatalogEntryView>(objects.Count);

        foreach (var item in objects)
        {
            var pkg = item?.Package;
            var packageName = pkg?.Name?.Trim();
            if (string.IsNullOrEmpty(packageName) || !PluginPackageValidator.IsValidPackageName(packageName))
            {
                continue;
            }
            if (!seenPackages.Add(packageName))
            {
                continue;
            }

            var version = pkg!.Version?.Trim() ?? string.Empty;
            var recommendedSpec = PluginPackageValidator.BuildRecommendedInstallSpec(packageName, version);
            if (string.IsNullOrEmpty(recommendedSpec))
            {
                continue;
            }

            var description = string.IsNullOrWhiteSpace(pkg.Description)
                ? string.Empty
                : SecretRedactor.RedactAndTruncate(pkg.Description.Trim(), MaxDescriptionLength);

            var keywords = NormalizeKeywords(pkg.Keywords);
            var publisher = string.IsNullOrWhiteSpace(pkg.Publisher?.Username)
                ? null
                : pkg.Publisher!.Username!.Trim();
            var publishedAtUnixMs = ParsePublishedAtUnixMs(pkg.Date);
            var npmUrl = SanitizeHttpsUrl(pkg.Links?.Npm);
            var isOfficialScope = packageName.StartsWith("@deepseek-ai/", StringComparison.Ordinal);

            entries.Add(new PluginCatalogEntryView(
                PackageName: packageName,
                Version: version,
                Description: description,
                Keywords: keywords,
                Publisher: publisher,
                PublishedAtUnixMs: publishedAtUnixMs,
                NpmUrl: npmUrl,
                IsOfficialScope: isOfficialScope,
                IsInstalled: false,
                InstalledVersion: null,
                RecommendedInstallSpec: recommendedSpec));
        }

        return entries;
    }

    private static List<string> NormalizeKeywords(List<string>? rawKeywords)
    {
        if (rawKeywords is null || rawKeywords.Count == 0)
        {
            return [];
        }

        var result = new List<string>(Math.Min(rawKeywords.Count, MaxKeywordsPerEntry));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var keyword in rawKeywords)
        {
            var trimmed = keyword?.Trim();
            if (string.IsNullOrEmpty(trimmed) || !seen.Add(trimmed))
            {
                continue;
            }
            result.Add(trimmed);
            if (result.Count >= MaxKeywordsPerEntry)
            {
                break;
            }
        }
        return result;
    }

    private static long? ParsePublishedAtUnixMs(string? isoDate)
    {
        if (string.IsNullOrWhiteSpace(isoDate))
        {
            return null;
        }
        return DateTimeOffset.TryParse(
            isoDate.Trim(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed.ToUnixTimeMilliseconds()
            : null;
    }

    private static string? SanitizeHttpsUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return null;
        }
        var trimmed = rawUrl.Trim();
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
               string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri
            : null;
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }
}

internal sealed record NpmSearchResponseDto(
    [property: JsonPropertyName("objects")] List<NpmSearchObjectDto>? Objects,
    [property: JsonPropertyName("total")] int Total);

internal sealed record NpmSearchObjectDto(
    [property: JsonPropertyName("package")] NpmSearchPackageDto? Package);

internal sealed record NpmSearchPackageDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("keywords")] List<string>? Keywords,
    [property: JsonPropertyName("date")] string? Date,
    [property: JsonPropertyName("links")] NpmSearchLinksDto? Links,
    [property: JsonPropertyName("publisher")] NpmSearchPublisherDto? Publisher);

internal sealed record NpmSearchLinksDto(
    [property: JsonPropertyName("npm")] string? Npm);

internal sealed record NpmSearchPublisherDto(
    [property: JsonPropertyName("username")] string? Username);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(NpmSearchResponseDto))]
internal sealed partial class NpmRegistrySearchJsonContext : JsonSerializerContext;
