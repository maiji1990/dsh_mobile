using System.Net;

namespace DshMobile.Agent.Transport;

/// <summary>
/// Configuration for <see cref="DshRemoteConnection"/> (Route B, ADR-0033).
/// Enforces loopback-only addressing (ADR-0032 / SECURITY.md).
/// </summary>
public sealed record RemoteConnectionOptions
{
    public Uri BaseUri { get; }

    public TimeSpan BackoffBase { get; init; } = TimeSpan.FromMilliseconds(500);

    public double BackoffFactor { get; init; } = 2.0;

    public TimeSpan BackoffMax { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan UnaryTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Hard cap on a single inbound WebSocket message frame sequence (default 16 MiB).</summary>
    public int MaxInboundMessageBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>Declared interaction participation on `/connect` (`answer` or `none`).</summary>
    public string InteractionsMode { get; init; } = "answer";

    public RemoteConnectionOptions(Uri baseUri)
    {
        BaseUri = ValidateLoopbackBaseUri(baseUri);
    }

    /// <summary>校验并规范化仅限本机回环的 http/https 基地址（禁止 userinfo/query/fragment 或非回环地址）。</summary>
    public static Uri ValidateLoopbackBaseUri(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("BaseUri must be an absolute http:// or https:// URI.", nameof(baseUri));
        }
        if (!string.IsNullOrEmpty(baseUri.UserInfo) || !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
        {
            throw new ArgumentException("BaseUri must not contain userinfo, query, or fragment.", nameof(baseUri));
        }
        if (!IsLoopbackHost(baseUri.Host))
        {
            throw new ArgumentException($"BaseUri host '{baseUri.Host}' is not a loopback address.", nameof(baseUri));
        }

        // Normalize to scheme://host:port/
        var builder = new UriBuilder(baseUri.Scheme, baseUri.Host, baseUri.Port);
        return builder.Uri;
    }

    public static bool IsLoopbackHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var bare = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        return IPAddress.TryParse(bare, out var ip) && IPAddress.IsLoopback(ip);
    }
}
