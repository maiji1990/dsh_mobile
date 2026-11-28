using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using DshMobile.Runtime.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// <see cref="LoopbackPortalProxy"/> 的真实回环回归：伪上游同时提供 HTTP 与 RFC 6455 WebSocket，
/// 覆盖一次性票据兑换、重放 / 过期拒绝、请求头改写、双向 WebSocket 透传与令牌吊销。
/// </summary>
public sealed class LoopbackPortalProxyTests
{
    private const string LaunchToken = "launch-token-secret-123";
    private const string UpstreamCookieValue = "dsh-auth-test=upstream-cookie-secret";
    private const string TicketRejectedBody = "一次性访问票据已失效或已使用，请返回应用重新打开。";
    private static readonly DateTimeOffset StartTime = DateTimeOffset.Parse(
        "2026-11-16T12:00:00Z",
        CultureInfo.InvariantCulture);

    [Fact]
    public async Task TryIssueLaunchUri_UsesOpaqueNonceAndNeverContainsLaunchToken()
    {
        await using var harness = new Harness();
        var first = harness.Proxy.TryIssueLaunchUri();
        Assert.NotNull(first);
        Assert.Equal(Uri.UriSchemeHttp, first.Scheme);
        Assert.Equal("127.0.0.1", first.Host);
        Assert.Equal("/__dshmobile/launch", first.AbsolutePath);
        Assert.DoesNotContain(LaunchToken, first.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain(Uri.EscapeDataString(LaunchToken), first.AbsoluteUri, StringComparison.Ordinal);

        var nonce = QueryValue(first, "nonce");
        Assert.NotNull(nonce);
        Assert.Matches("^[0-9a-f]{32}$", nonce);

        var second = harness.Proxy.TryIssueLaunchUri();
        Assert.NotNull(second);
        Assert.NotEqual(nonce, QueryValue(second, "nonce"));

        var logs = string.Join("\n", harness.Logs);
        Assert.DoesNotContain(nonce, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(LaunchToken, logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchTicket_FirstRedemption_Returns303WithStrictHttpOnlyPortalCookie()
    {
        await using var harness = new Harness();
        using var client = CreateHttpClient();
        var launch = harness.Proxy.TryIssueLaunchUri();
        Assert.NotNull(launch);

        using var response = await client.GetAsync(launch);
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));

        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("__dshmobile_portal=", setCookie, StringComparison.Ordinal);
        Assert.Contains("Path=/", setCookie, StringComparison.Ordinal);
        Assert.Contains("HttpOnly", setCookie, StringComparison.Ordinal);
        Assert.Contains("SameSite=Strict", setCookie, StringComparison.Ordinal);

        var tokenRequest = Assert.Single(
            harness.Upstream.Requests,
            r => r.Path == "/" && QueryValue(r.Query, "token") is not null);
        Assert.Equal(LaunchToken, QueryValue(tokenRequest.Query, "token"));

        var secret = ExtractPortalSecret(response);
        var logs = string.Join("\n", harness.Logs);
        Assert.DoesNotContain(LaunchToken, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(UpstreamCookieValue, logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchTicket_Replay_Returns403AndNeverRedemptsTwice()
    {
        await using var harness = new Harness();
        using var client = CreateHttpClient();
        var launch = harness.Proxy.TryIssueLaunchUri();
        Assert.NotNull(launch);

        using var first = await client.GetAsync(launch);
        Assert.Equal(HttpStatusCode.SeeOther, first.StatusCode);

        using var replay = await client.GetAsync(launch);
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", replay.Content.Headers.ContentType?.ToString());
        Assert.True(replay.Headers.CacheControl?.NoStore);
        Assert.Equal("no-referrer", Header(replay, "Referrer-Policy"));
        Assert.Equal(TicketRejectedBody, await replay.Content.ReadAsStringAsync());

        var tokenRequests = harness.Upstream.Requests
            .Count(r => r.Path == "/" && QueryValue(r.Query, "token") is not null);
        Assert.Equal(1, tokenRequests);
    }

    [Fact]
    public async Task LaunchTicket_After60SecondTtl_Returns403WithoutContactingUpstream()
    {
        await using var harness = new Harness();
        using var client = CreateHttpClient();
        var launch = harness.Proxy.TryIssueLaunchUri();
        Assert.NotNull(launch);

        harness.Time.Advance(TimeSpan.FromSeconds(61));

        using var response = await client.GetAsync(launch);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(TicketRejectedBody, await response.Content.ReadAsStringAsync());
        Assert.Empty(harness.Upstream.Requests);
    }

    [Fact]
    public async Task PortalRequest_ForwardsGetPostChunkedAndStreamingWithRewrittenHeaders()
    {
        await using var harness = new Harness();
        using var client = CreateHttpClient();
        var launch = harness.Proxy.TryIssueLaunchUri();
        Assert.NotNull(launch);
        using var redemption = await client.GetAsync(launch);
        Assert.Equal(HttpStatusCode.SeeOther, redemption.StatusCode);
        var secret = ExtractPortalSecret(redemption);
        var portalBase = $"http://127.0.0.1:{launch.Port}/";
        var cookie = PortalCookie(secret);
        var proxyOrigin = $"http://127.0.0.1:{launch.Port}";

        using var get = await SendGetAsync(
            client,
            portalBase + "panel?tab=one",
            cookie,
            origin: proxyOrigin,
            referer: proxyOrigin + "/plugins");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("ok:GET:/panel", await get.Content.ReadAsStringAsync());

        var getRequest = Assert.Single(harness.Upstream.Requests, r => r.Method == "GET" && r.Path == "/panel");
        Assert.Equal($"127.0.0.1:{harness.Upstream.Port}", getRequest.Headers["Host"]);
        Assert.Equal($"http://127.0.0.1:{harness.Upstream.Port}", getRequest.Headers["Origin"]);
        Assert.Equal($"http://127.0.0.1:{harness.Upstream.Port}/plugins", getRequest.Headers["Referer"]);
        Assert.Equal(UpstreamCookieValue, getRequest.Headers["Cookie"]);

        using var postContent = new StringContent("hello-body", Encoding.UTF8, "text/plain");
        using var postRequest = new HttpRequestMessage(HttpMethod.Post, portalBase + "echo")
        {
            Content = postContent,
        };
        postRequest.Headers.TryAddWithoutValidation("Cookie", cookie);
        using var post = await client.SendAsync(postRequest);
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.Equal("ok:POST:/echo", await post.Content.ReadAsStringAsync());
        var postRequestSeen = Assert.Single(harness.Upstream.Requests, r => r.Method == "POST" && r.Path == "/echo");
        Assert.Equal("hello-body", postRequestSeen.Body);

        using var chunkedContent = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("chunked-body")));
        using var chunkedRequest = new HttpRequestMessage(HttpMethod.Post, portalBase + "chunked")
        {
            Content = chunkedContent,
        };
        chunkedRequest.Headers.TransferEncodingChunked = true;
        chunkedRequest.Headers.TryAddWithoutValidation("Cookie", cookie);
        using var chunked = await client.SendAsync(chunkedRequest);
        Assert.Equal(HttpStatusCode.OK, chunked.StatusCode);
        var chunkedRequestSeen = Assert.Single(
            harness.Upstream.Requests,
            r => r.Method == "POST" && r.Path == "/chunked");
        Assert.Equal("chunked-body", chunkedRequestSeen.Body);

        using var stream = await SendGetAsync(client, portalBase + "stream", cookie);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        Assert.True(stream.Headers.TransferEncodingChunked);
        Assert.Equal("firstsecond", await stream.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PortalWebSocket_IsBidirectionalAndCountsAsActiveClient()
    {
        await using var harness = new Harness();
        using var client = CreateHttpClient();
        var launch = harness.Proxy.TryIssueLaunchUri();
        Assert.NotNull(launch);
        using var redemption = await client.GetAsync(launch);
        var secret = ExtractPortalSecret(redemption);

        var (tcp, socket) = await OpenWebSocketAsync(launch.Port, secret, $"http://127.0.0.1:{launch.Port}");
        using (tcp)
        using (socket)
        {
            using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await socket.SendAsync(Encoding.UTF8.GetBytes("ping"), WebSocketMessageType.Text, true, sendTimeout.Token);
            Assert.Equal("echo:ping", await ReceiveTextAsync(socket, TimeSpan.FromSeconds(10)));

            var wsRequest = harness.Upstream.Requests.Last(r => r.Path == "/api/remote.mux");
            Assert.Equal($"127.0.0.1:{harness.Upstream.Port}", wsRequest.Headers["Host"]);
            Assert.Equal($"http://127.0.0.1:{harness.Upstream.Port}", wsRequest.Headers["Origin"]);
            Assert.Equal(UpstreamCookieValue, wsRequest.Headers["Cookie"]);

            harness.Time.Advance(TimeSpan.FromSeconds(10));
            Assert.True(
                await WaitUntilAsync(
                    () => harness.Proxy.HasActiveOrRecentClients(TimeSpan.FromSeconds(1)),
                    TimeSpan.FromSeconds(5)));
        }

        harness.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.True(
            await WaitUntilAsync(
                () => !harness.Proxy.HasActiveOrRecentClients(TimeSpan.FromSeconds(1)),
                TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task PortalRequest_RejectsForgedOriginAndForgedHost()
    {
        await using var harness = new Harness();
        using var client = CreateHttpClient();
        var launch = harness.Proxy.TryIssueLaunchUri();
        Assert.NotNull(launch);
        using var redemption = await client.GetAsync(launch);
        var secret = ExtractPortalSecret(redemption);
        var portalBase = $"http://127.0.0.1:{launch.Port}/";
        var cookie = PortalCookie(secret);

        using var forgedOrigin = await SendGetAsync(
            client,
            portalBase,
            cookie,
            origin: "http://attacker.example");
        Assert.Equal(HttpStatusCode.Forbidden, forgedOrigin.StatusCode);
        Assert.True(forgedOrigin.Headers.CacheControl?.NoStore);

        var raw = string.Create(
            CultureInfo.InvariantCulture,
            $"GET / HTTP/1.1\r\nHost: attacker.example\r\nCookie: {cookie}\r\nConnection: close\r\n\r\n");
        var rawResponse = await SendRawAndReadAllAsync(launch.Port, raw);
        Assert.StartsWith("HTTP/1.1 403 Forbidden", rawResponse, StringComparison.Ordinal);
        Assert.Contains("Cache-Control: no-store", rawResponse, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HasActiveOrRecentClients_TracksRecentRequestsAndExpiresThem()
    {
        await using var harness = new Harness();
        using var client = CreateHttpClient();
        var launch = harness.Proxy.TryIssueLaunchUri();
        Assert.NotNull(launch);

        Assert.False(harness.Proxy.HasActiveOrRecentClients(TimeSpan.FromSeconds(5)));

        using var redemption = await client.GetAsync(launch);
        var secret = ExtractPortalSecret(redemption);
        Assert.True(harness.Proxy.HasActiveOrRecentClients(TimeSpan.FromSeconds(5)));

        harness.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.False(harness.Proxy.HasActiveOrRecentClients(TimeSpan.FromSeconds(5)));

        using var page = await SendGetAsync(
            client,
            $"http://127.0.0.1:{launch.Port}/",
            PortalCookie(secret));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.True(harness.Proxy.HasActiveOrRecentClients(TimeSpan.FromSeconds(5)));

        harness.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.False(harness.Proxy.HasActiveOrRecentClients(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task TokenChanged_RevokesSessionAndDisconnectsActiveWebSocket()
    {
        await using var harness = new Harness();
        using var client = CreateHttpClient();
        var launch = harness.Proxy.TryIssueLaunchUri();
        Assert.NotNull(launch);
        using var redemption = await client.GetAsync(launch);
        var secret = ExtractPortalSecret(redemption);

        var (tcp, socket) = await OpenWebSocketAsync(launch.Port, secret);
        using (tcp)
        using (socket)
        {
            using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await socket.SendAsync(Encoding.UTF8.GetBytes("before"), WebSocketMessageType.Text, true, sendTimeout.Token);
            Assert.Equal("echo:before", await ReceiveTextAsync(socket, TimeSpan.FromSeconds(10)));

            harness.Tokens.SetToken("rotated-token-secret-456");

            Assert.True(await WaitForCloseAsync(socket, TimeSpan.FromSeconds(5)));
        }

        using var revoked = await SendGetAsync(
            client,
            $"http://127.0.0.1:{launch.Port}/",
            PortalCookie(secret));
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Contains(
            revoked.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("__dshmobile_portal=;", StringComparison.Ordinal) &&
                     value.Contains("Max-Age=0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IneligibleUpstream_ReturnsNullUriAndUnauthorized()
    {
        await using var harness = new Harness();
        using var client = CreateHttpClient();
        var launch = harness.Proxy.TryIssueLaunchUri();
        Assert.NotNull(launch);
        using var redemption = await client.GetAsync(launch);
        var secret = ExtractPortalSecret(redemption);

        harness.Eligible = false;

        Assert.Null(harness.Proxy.TryIssueLaunchUri());
        using var denied = await SendGetAsync(
            client,
            $"http://127.0.0.1:{launch.Port}/",
            PortalCookie(secret));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [Fact]
    public async Task Dispose_RevokesSessionDisconnectsWebSocketAndRejectsWith401()
    {
        await using var harness = new Harness();
        using var client = CreateHttpClient();
        var launch = harness.Proxy.TryIssueLaunchUri();
        Assert.NotNull(launch);
        using var redemption = await client.GetAsync(launch);
        var secret = ExtractPortalSecret(redemption);

        var (tcp, socket) = await OpenWebSocketAsync(launch.Port, secret);
        using (tcp)
        using (socket)
        {
            using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await socket.SendAsync(Encoding.UTF8.GetBytes("before-dispose"), WebSocketMessageType.Text, true, sendTimeout.Token);
            Assert.Equal("echo:before-dispose", await ReceiveTextAsync(socket, TimeSpan.FromSeconds(10)));

            harness.Proxy.Dispose();

            Assert.True(await WaitForCloseAsync(socket, TimeSpan.FromSeconds(5)));
        }

        Assert.Null(harness.Proxy.TryIssueLaunchUri());

        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt < 10 && response is null; attempt++)
        {
            try
            {
                response = await SendGetAsync(
                    client,
                    $"http://127.0.0.1:{launch.Port}/",
                    PortalCookie(secret));
            }
            catch (HttpRequestException)
            {
                await Task.Delay(50);
            }
        }
        Assert.NotNull(response);
        using (response)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task RequestsConcurrentWithDispose_GetExplicit401InsteadOfConnectionReset()
    {
        await using var harness = new Harness();
        using var client = CreateHttpClient();
        var launch = harness.Proxy.TryIssueLaunchUri();
        Assert.NotNull(launch);

        string secret;
        using (var redemption = await client.GetAsync(launch))
        {
            Assert.Equal(HttpStatusCode.SeeOther, redemption.StatusCode);
            secret = ExtractPortalSecret(redemption);
        }

        harness.Proxy.Dispose();
        var since = Stopwatch.StartNew();

        // 旧实现：排空窗口内接受连接后立刻回复 401 并关闭，入站请求字节尚未读取 —— Windows 上关闭含未读数据的
        // socket 会发出 RST，客户端拿到 HttpRequestException 而不是 401。并发请求让这个竞态稳定复现。
        var attempts = Enumerable.Range(0, 10)
            .Select(async _ =>
            {
                try
                {
                    using var response = await SendGetAsync(
                        client,
                        $"http://127.0.0.1:{launch.Port}/",
                        PortalCookie(secret));
                    return (HttpStatusCode?)response.StatusCode;
                }
                catch (HttpRequestException e) when (IsRefusedAfterDrainWindow(e, since.Elapsed))
                {
                    // 排空窗口结束后监听器已停止，连接被拒绝属于预期；只接受这一种失败形态。
                    return null;
                }
            })
            .ToArray();

        var statuses = await Task.WhenAll(attempts);

        Assert.Contains(statuses, status => status is not null);
        Assert.All(
            statuses,
            status => Assert.True(
                status is null || status == HttpStatusCode.Unauthorized,
                $"排空窗口内的请求必须拿到明确的 401，实际为 {status?.ToString() ?? "连接被拒绝"}"));
    }

    /// <summary>仅在排空窗口结束后，才把“连接被拒绝”视为预期结果；其他任何 <see cref="HttpRequestException"/>（RST 等）都算回归。</summary>
    private static bool IsRefusedAfterDrainWindow(HttpRequestException exception, TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.FromSeconds(1))
        {
            return false;
        }
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
            {
                return true;
            }
        }
        return false;
    }

    [Fact]
    public async Task TryIssueLaunchUri_RequiresEligibleHostValidPortAndToken()
    {
        await using var upstream = new FakeUpstream();
        var tokens = new TestTokenSource(LaunchToken);
        var eligible = true;
        int? port = upstream.Port;
        using var proxy = new LoopbackPortalProxy(
            () => port,
            tokens,
            () => eligible,
            new FakeTimeProvider(StartTime));
        Assert.NotNull(proxy.TryIssueLaunchUri());

        eligible = false;
        Assert.Null(proxy.TryIssueLaunchUri());
        eligible = true;

        port = 0;
        Assert.Null(proxy.TryIssueLaunchUri());
        port = 70000;
        Assert.Null(proxy.TryIssueLaunchUri());
        port = null;
        Assert.Null(proxy.TryIssueLaunchUri());
        port = upstream.Port;

        tokens.SetToken(null);
        Assert.Null(proxy.TryIssueLaunchUri());

        tokens.SetToken(LaunchToken);
        Assert.NotNull(proxy.TryIssueLaunchUri());
        proxy.Dispose();
        Assert.Null(proxy.TryIssueLaunchUri());
    }

    [Fact]
    public async Task TryIssueLaunchUri_EvictsOldestWhenMoreThanSixteenUnconsumed()
    {
        await using var harness = new Harness();
        var issued = new List<Uri>();
        for (var index = 0; index < 17; index++)
        {
            var uri = harness.Proxy.TryIssueLaunchUri();
            Assert.NotNull(uri);
            issued.Add(uri);
        }

        using var client = CreateHttpClient();
        using var evicted = await client.GetAsync(issued[0]);
        Assert.Equal(HttpStatusCode.Forbidden, evicted.StatusCode);
        Assert.Equal(TicketRejectedBody, await evicted.Content.ReadAsStringAsync());

        using var accepted = await client.GetAsync(issued[1]);
        Assert.Equal(HttpStatusCode.SeeOther, accepted.StatusCode);
        Assert.Single(
            harness.Upstream.Requests,
            request => request.Path == "/" && QueryValue(request.Query, "token") is not null);
    }

    [Fact]
    public async Task PortalRequest_WithoutOrForgedPortalCookie_Returns401AndClearsCookie()
    {
        await using var harness = new Harness();
        using var client = CreateHttpClient();
        var launch = harness.Proxy.TryIssueLaunchUri();
        Assert.NotNull(launch);
        using var redemption = await client.GetAsync(launch);
        Assert.Equal(HttpStatusCode.SeeOther, redemption.StatusCode);
        var portalBase = $"http://127.0.0.1:{launch.Port}/";

        using var noCookie = await SendGetAsync(client, portalBase);
        Assert.Equal(HttpStatusCode.Unauthorized, noCookie.StatusCode);
        Assert.Contains(
            noCookie.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("__dshmobile_portal=;", StringComparison.Ordinal) &&
                     value.Contains("Max-Age=0", StringComparison.Ordinal));

        using var forged = await SendGetAsync(client, portalBase, PortalCookie(new string('0', 64)));
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Contains(
            forged.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("__dshmobile_portal=;", StringComparison.Ordinal) &&
                     value.Contains("Max-Age=0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RevokeAll_RevokesSessionAndDisconnectsActiveWebSocket()
    {
        await using var harness = new Harness();
        using var client = CreateHttpClient();
        var launch = harness.Proxy.TryIssueLaunchUri();
        Assert.NotNull(launch);
        using var redemption = await client.GetAsync(launch);
        var secret = ExtractPortalSecret(redemption);

        var (tcp, socket) = await OpenWebSocketAsync(launch.Port, secret);
        using (tcp)
        using (socket)
        {
            using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await socket.SendAsync(Encoding.UTF8.GetBytes("before-revoke"), WebSocketMessageType.Text, true, sendTimeout.Token);
            Assert.Equal("echo:before-revoke", await ReceiveTextAsync(socket, TimeSpan.FromSeconds(10)));

            harness.Proxy.RevokeAll();

            Assert.True(await WaitForCloseAsync(socket, TimeSpan.FromSeconds(5)));
        }

        using var revoked = await SendGetAsync(
            client,
            $"http://127.0.0.1:{launch.Port}/",
            PortalCookie(secret));
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Fact]
    public async Task PortalRequest_RefererOutsideProxyOriginIsLeftUnchanged()
    {
        await using var harness = new Harness();
        using var client = CreateHttpClient();
        var launch = harness.Proxy.TryIssueLaunchUri();
        Assert.NotNull(launch);
        using var redemption = await client.GetAsync(launch);
        var secret = ExtractPortalSecret(redemption);
        var portalBase = $"http://127.0.0.1:{launch.Port}/";
        var proxyOrigin = $"http://127.0.0.1:{launch.Port}";
        var lookalike = proxyOrigin + ".evil.example/path";

        using var exact = await SendGetAsync(
            client,
            portalBase + "exact",
            PortalCookie(secret),
            origin: proxyOrigin,
            referer: proxyOrigin);
        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);

        using var outside = await SendGetAsync(
            client,
            portalBase + "outside",
            PortalCookie(secret),
            origin: proxyOrigin,
            referer: lookalike);
        Assert.Equal(HttpStatusCode.OK, outside.StatusCode);

        var requests = harness.Upstream.Requests
            .Where(request => request.Method == "GET" &&
                              request.Path is "/exact" or "/outside")
            .ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal($"http://127.0.0.1:{harness.Upstream.Port}", requests[0].Headers["Referer"]);
        Assert.Equal(lookalike, requests[1].Headers["Referer"]);
    }

    // ------------------------------------------------------------------------------------------------
    // 请求目标解析回归（ADR-0036 真机 B1：Linux / Android 上 origin-form 被误判为 file:// URI）
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/__dshmobile/launch?nonce=0123abc", "/__dshmobile/launch")]
    [InlineData("/__dshmobile/launch?nonce=0123abc#frag", "/__dshmobile/launch")]
    [InlineData("/__dshmobile/launch#frag", "/__dshmobile/launch")]
    [InlineData("/__dshmobile/launch", "/__dshmobile/launch")]
    [InlineData("/", "/")]
    [InlineData("http://127.0.0.1:41257/__dshmobile/launch?nonce=0123abc", "/__dshmobile/launch")]
    [InlineData("https://127.0.0.1:41257/__dshmobile/launch?nonce=0123abc#frag", "/__dshmobile/launch")]
    [InlineData("file:///tmp/evil?nonce=0123abc", "file:///tmp/evil")]
    public void PathOf_ParsesOriginFormAndOnlyHttpAbsoluteTargets(string target, string expected)
    {
        Assert.Equal(expected, LoopbackPortalProxy.PathOf(target));
    }

    [Theory]
    [InlineData("/__dshmobile/launch?nonce=0123abc", "nonce", "0123abc")]
    [InlineData("/__dshmobile/launch?nonce=0123abc#frag", "nonce", "0123abc")]
    [InlineData("/__dshmobile/launch?nonce=0123abc&other=1", "other", "1")]
    [InlineData("http://127.0.0.1:41257/__dshmobile/launch?nonce=0123abc", "nonce", "0123abc")]
    [InlineData("https://127.0.0.1:41257/__dshmobile/launch?nonce=0123abc#frag", "nonce", "0123abc")]
    public void QueryValue_ParsesOriginFormAndHttpAbsoluteTargets(string target, string name, string expected)
    {
        Assert.Equal(expected, LoopbackPortalProxy.QueryValue(target, name));
    }

    [Fact]
    public void QueryValue_ReturnsNullWhenOriginFormHasNoQuery()
    {
        Assert.Null(LoopbackPortalProxy.QueryValue("/__dshmobile/launch", "nonce"));
        Assert.Null(LoopbackPortalProxy.QueryValue("/__dshmobile/launch#frag", "nonce"));
        Assert.Null(LoopbackPortalProxy.QueryValue("/__dshmobile/launch?nonce=0123abc", "missing"));
    }

    [Fact]
    public async Task OriginFormLaunchTarget_RedempsTicketEndToEnd()
    {
        await using var harness = new Harness();
        using var client = CreateHttpClient();
        var launch = harness.Proxy.TryIssueLaunchUri();
        Assert.NotNull(launch);

        // HttpClient 发送的是 origin-form（"/__dshmobile/launch?nonce=..."），正是真机上被误判为 file:// 的形态。
        using var response = await client.GetAsync(launch);
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Single(
            harness.Upstream.Requests,
            request => request.Path == "/" && QueryValue(request.Query, "token") is not null);
    }

    [Fact]
    public async Task RejectionLogs_AreObservableAndFreeOfSecrets()
    {
        await using var harness = new Harness();
        using var client = CreateHttpClient();
        var launch = harness.Proxy.TryIssueLaunchUri();
        Assert.NotNull(launch);
        var nonce = QueryValue(launch, "nonce");
        Assert.NotNull(nonce);

        using var redemption = await client.GetAsync(launch);
        Assert.Equal(HttpStatusCode.SeeOther, redemption.StatusCode);
        var secret = ExtractPortalSecret(redemption);

        using var unauthorized = await SendGetAsync(client, $"http://127.0.0.1:{launch.Port}/");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var forgedOrigin = await SendGetAsync(
            client,
            $"http://127.0.0.1:{launch.Port}/",
            PortalCookie(secret),
            origin: "http://attacker.example");
        Assert.Equal(HttpStatusCode.Forbidden, forgedOrigin.StatusCode);

        using var replay = await client.GetAsync(launch);
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);

        var logs = string.Join("\n", harness.Logs);
        Assert.Contains("portal: rejected request (host_or_origin)", logs, StringComparison.Ordinal);
        Assert.Contains("portal: rejected launch ticket", logs, StringComparison.Ordinal);
        Assert.Contains("portal: rejected unauthorized request", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(nonce, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(LaunchToken, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(UpstreamCookieValue, logs, StringComparison.Ordinal);
        Assert.DoesNotContain("nonce=", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("dsh-auth-", logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedeemWithoutUpstreamCookie_LogsMissingCookieWithoutSecrets()
    {
        await using var harness = new Harness(authCookie: null);
        using var client = CreateHttpClient();
        var launch = harness.Proxy.TryIssueLaunchUri();
        Assert.NotNull(launch);
        var nonce = QueryValue(launch, "nonce");
        Assert.NotNull(nonce);

        using var response = await client.GetAsync(launch);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        var logs = string.Join("\n", harness.Logs);
        Assert.Contains("portal: upstream cookie missing", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(nonce, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(LaunchToken, logs, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------
    // 测试基础设施
    // ------------------------------------------------------------------------------------------------

    private sealed class Harness : IAsyncDisposable
    {
        private readonly List<string> _logs = [];

        public Harness(string? token = LaunchToken, string? authCookie = UpstreamCookieValue)
        {
            Upstream = new FakeUpstream { AuthCookie = authCookie ?? string.Empty };
            Tokens = new TestTokenSource(token);
            Time = new FakeTimeProvider(StartTime);
            Proxy = new LoopbackPortalProxy(
                () => Upstream.Port,
                Tokens,
                () => Eligible,
                Time,
                message =>
                {
                    lock (_logs)
                    {
                        _logs.Add(message);
                    }
                });
        }

        public FakeUpstream Upstream { get; }

        public TestTokenSource Tokens { get; }

        public FakeTimeProvider Time { get; }

        public bool Eligible { get; set; } = true;

        public LoopbackPortalProxy Proxy { get; }

        public IReadOnlyList<string> Logs
        {
            get
            {
                lock (_logs)
                {
                    return _logs.ToArray();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            Proxy.Dispose();
            await Upstream.DisposeAsync();
        }
    }

    private sealed class TestTokenSource : IDshTokenSource
    {
        private string? _token;
        private long _revision;

        public TestTokenSource(string? token)
        {
            _token = token;
            _revision = token is null ? 0 : 1;
        }

        public string? Token
        {
            get
            {
                lock (this)
                {
                    return _token;
                }
            }
        }

        public long? TokenRevision
        {
            get
            {
                lock (this)
                {
                    return _token is null ? null : _revision;
                }
            }
        }

        public event EventHandler? TokenChanged;

        public void SetToken(string? token)
        {
            lock (this)
            {
                if (string.Equals(_token, token, StringComparison.Ordinal))
                {
                    return;
                }
                _token = token;
                _revision++;
            }
            TokenChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FakeUpstream : IAsyncDisposable
    {
        public sealed record CapturedRequest(
            string Method,
            string Path,
            string Query,
            IReadOnlyDictionary<string, string> Headers,
            string Body);

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentBag<Task> _clients = [];
        private readonly ConcurrentQueue<CapturedRequest> _requests = new();
        private readonly ConcurrentBag<WebSocket> _sockets = [];

        public FakeUpstream()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        public string LaunchToken { get; init; } = LoopbackPortalProxyTests.LaunchToken;

        public string AuthCookie { get; init; } = UpstreamCookieValue;

        public IReadOnlyList<CapturedRequest> Requests => _requests.ToArray();

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var tcp = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                    tcp.NoDelay = true;
                    _clients.Add(HandleClientAsync(tcp));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException)
            {
            }
        }

        private async Task HandleClientAsync(TcpClient tcp)
        {
            using (tcp)
            await using (var stream = tcp.GetStream())
            {
                try
                {
                    var head = await ReadHeadAsync(stream, _cts.Token).ConfigureAwait(false);
                    if (head is null)
                    {
                        return;
                    }
                    var request = ParseRequest(head);
                    if (request.Headers.TryGetValue("Upgrade", out var upgrade) &&
                        upgrade.Contains("websocket", StringComparison.OrdinalIgnoreCase))
                    {
                        _requests.Enqueue(new CapturedRequest(
                            request.Method,
                            request.Path,
                            request.Query,
                            request.Headers,
                            string.Empty));
                        await AcceptWebSocketAsync(stream, request, _cts.Token).ConfigureAwait(false);
                        return;
                    }

                    var body = await ReadBodyAsync(request, stream, _cts.Token).ConfigureAwait(false);
                    _requests.Enqueue(new CapturedRequest(
                        request.Method,
                        request.Path,
                        request.Query,
                        request.Headers,
                        body));

                    if (request.Method == "GET" &&
                        request.Path == "/" &&
                        QueryValue(request.Query, "token") is { } token)
                    {
                        if (!string.Equals(token, LaunchToken, StringComparison.Ordinal))
                        {
                            await WriteResponseAsync(stream, 401, "Unauthorized", [], string.Empty, _cts.Token)
                                .ConfigureAwait(false);
                            return;
                        }
                        await WriteResponseAsync(
                            stream,
                            303,
                            "See Other",
                            [("Location", "/"), ("Set-Cookie", $"{AuthCookie}; Path=/; HttpOnly")],
                            string.Empty,
                            _cts.Token).ConfigureAwait(false);
                        return;
                    }

                    if (request.Path == "/stream")
                    {
                        await WriteChunkedResponseAsync(stream, ["first", "second"], _cts.Token).ConfigureAwait(false);
                        return;
                    }

                    await WriteResponseAsync(
                        stream,
                        200,
                        "OK",
                        [("Content-Type", "text/plain; charset=utf-8")],
                        $"ok:{request.Method}:{request.Path}",
                        _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException)
                {
                }
                catch (SocketException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
                catch (WebSocketException)
                {
                }
            }
        }

        private async Task AcceptWebSocketAsync(NetworkStream stream, ParsedRequest request, CancellationToken ct)
        {
            var key = request.Headers.TryGetValue("Sec-WebSocket-Key", out var value) ? value : string.Empty;
#pragma warning disable CA5350 // RFC 6455 Sec-WebSocket-Accept mandates SHA-1
            var accept = Convert.ToBase64String(SHA1.HashData(
                Encoding.ASCII.GetBytes(key.Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
#pragma warning restore CA5350
            var handshake =
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(handshake), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
            {
                IsServer = true,
                KeepAliveInterval = Timeout.InfiniteTimeSpan,
            });
            _sockets.Add(socket);
            var buffer = new byte[8192];
            while (!ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                        .ConfigureAwait(false);
                    return;
                }
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }
                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                await socket.SendAsync(
                    Encoding.UTF8.GetBytes("echo:" + text),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    ct).ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _listener.Stop();
            foreach (var socket in _sockets)
            {
                socket.Dispose();
            }
            await Task.WhenAll(_clients).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _cts.Dispose();
        }
    }

    private sealed record ParsedRequest(
        string Method,
        string Path,
        string Query,
        Dictionary<string, string> Headers);

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    private static async Task<HttpResponseMessage> SendGetAsync(
        HttpClient client,
        string url,
        string? cookie = null,
        string? origin = null,
        string? referer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }
        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }
        if (referer is not null)
        {
            request.Headers.TryAddWithoutValidation("Referer", referer);
        }
        return await client.SendAsync(request);
    }

    private static async Task<(TcpClient Tcp, WebSocket Socket)> OpenWebSocketAsync(
        int proxyPort,
        string secret,
        string? origin = null)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(IPAddress.Loopback, proxyPort);
            var stream = tcp.GetStream();
            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var originLine = origin is null ? string.Empty : $"Origin: {origin}\r\n";
            var handshake = string.Create(
                CultureInfo.InvariantCulture,
                $"GET /api/remote.mux HTTP/1.1\r\nHost: 127.0.0.1:{proxyPort}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n{originLine}Cookie: __dshmobile_portal={secret}\r\n\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(handshake));
            await stream.FlushAsync();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var responseHead = await ReadHeadAsync(stream, timeout.Token);
            Assert.NotNull(responseHead);
            Assert.StartsWith("HTTP/1.1 101", responseHead, StringComparison.Ordinal);

            var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
            {
                IsServer = false,
                KeepAliveInterval = Timeout.InfiniteTimeSpan,
            });
            return (tcp, socket);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private static async Task<string> ReceiveTextAsync(WebSocket socket, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("websocket closed before a text message arrived");
            }
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.ToArray());
            }
        }
    }

    private static async Task<bool> WaitForCloseAsync(WebSocket socket, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[1024];
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (WebSocketException)
        {
            return true;
        }
        catch (IOException)
        {
            return true;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(25);
        }
        return condition();
    }

    private static async Task<string> SendRawAndReadAllAsync(int port, string raw)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        using var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(raw));
        await stream.FlushAsync();
        using var buffer = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await stream.CopyToAsync(buffer, timeout.Token);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task<string?> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count, 1), ct);
            if (read == 0)
            {
                return count == 0 ? null : throw new EndOfStreamException();
            }
            count++;
            if (count >= 4 &&
                buffer[count - 4] == (byte)'\r' &&
                buffer[count - 3] == (byte)'\n' &&
                buffer[count - 2] == (byte)'\r' &&
                buffer[count - 1] == (byte)'\n')
            {
                return Encoding.ASCII.GetString(buffer, 0, count - 4);
            }
        }
        throw new InvalidDataException("HTTP headers exceeded the size limit");
    }

    private static ParsedRequest ParseRequest(string head)
    {
        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ', 3);
        var target = parts.Length > 1 ? parts[1] : "/";
        var separator = target.IndexOf('?', StringComparison.Ordinal);
        var path = separator < 0 ? target : target[..separator];
        var query = separator < 0 ? string.Empty : target[separator..];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }
            var name = lines[i][..colon].Trim();
            var value = lines[i][(colon + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out var existing) ? $"{existing}; {value}" : value;
        }
        return new ParsedRequest(parts[0], path, query, headers);
    }

    private static async Task<string> ReadBodyAsync(
        ParsedRequest request,
        NetworkStream stream,
        CancellationToken ct)
    {
        if (request.Headers.TryGetValue("Transfer-Encoding", out var transferEncoding) &&
            transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            return await ReadChunkedBodyAsync(stream, ct);
        }
        if (request.Headers.TryGetValue("Content-Length", out var lengthText) &&
            int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var length) &&
            length > 0)
        {
            var body = new byte[length];
            await ReadExactAsync(stream, body, ct);
            return Encoding.UTF8.GetString(body);
        }
        return string.Empty;
    }

    private static async Task<string> ReadChunkedBodyAsync(NetworkStream stream, CancellationToken ct)
    {
        using var body = new MemoryStream();
        while (true)
        {
            var line = await ReadLineAsync(stream, ct);
            if (line is null)
            {
                throw new EndOfStreamException();
            }
            var separator = line.IndexOf(';');
            var sizeText = separator < 0 ? line : line[..separator];
            var size = int.Parse(sizeText.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (size == 0)
            {
                while (true)
                {
                    var trailer = await ReadLineAsync(stream, ct);
                    if (string.IsNullOrEmpty(trailer))
                    {
                        return Encoding.UTF8.GetString(body.ToArray());
                    }
                }
            }
            var chunk = new byte[size];
            await ReadExactAsync(stream, chunk, ct);
            body.Write(chunk);
            var crlf = new byte[2];
            await ReadExactAsync(stream, crlf, ct);
        }
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var bytes = new List<byte>(64);
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one.AsMemory(0, 1), ct);
            if (read == 0)
            {
                return bytes.Count == 0 ? null : throw new EndOfStreamException();
            }
            if (one[0] == (byte)'\n')
            {
                if (bytes.Count > 0 && bytes[^1] == (byte)'\r')
                {
                    bytes.RemoveAt(bytes.Count - 1);
                }
                return Encoding.ASCII.GetString(bytes.ToArray());
            }
            bytes.Add(one[0]);
        }
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }
            offset += read;
        }
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int statusCode,
        string reason,
        IReadOnlyList<(string Name, string Value)> headers,
        string body,
        CancellationToken ct)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var builder = new StringBuilder();
        builder.Append(FormattableString.Invariant($"HTTP/1.1 {statusCode} {reason}\r\n"));
        builder.Append(FormattableString.Invariant($"Content-Length: {bodyBytes.Length}\r\n"));
        builder.Append("Connection: close\r\n");
        foreach (var (name, value) in headers)
        {
            builder.Append(name).Append(": ").Append(value).Append("\r\n");
        }
        builder.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()), ct);
        if (bodyBytes.Length > 0)
        {
            await stream.WriteAsync(bodyBytes, ct);
        }
        await stream.FlushAsync(ct);
    }

    private static async Task WriteChunkedResponseAsync(
        NetworkStream stream,
        IReadOnlyList<string> parts,
        CancellationToken ct)
    {
        var builder = new StringBuilder(
            "HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n");
        foreach (var part in parts)
        {
            var bytes = Encoding.UTF8.GetBytes(part);
            builder.Append(bytes.Length.ToString("x", CultureInfo.InvariantCulture)).Append("\r\n");
            builder.Append(part).Append("\r\n");
        }
        builder.Append("0\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()), ct);
        await stream.FlushAsync(ct);
    }

    private static string PortalCookie(string secret) => $"__dshmobile_portal={secret}";

    private static string ExtractPortalSecret(HttpResponseMessage response)
    {
        var setCookie = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("__dshmobile_portal=", StringComparison.Ordinal));
        var pair = setCookie.Split(';', 2)[0];
        return pair["__dshmobile_portal=".Length..];
    }

    private static string Header(HttpResponseMessage response, string name) =>
        string.Join(",", response.Headers.TryGetValues(name, out var values) ? values : []);

    private static string? QueryValue(Uri uri, string name)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var key = separator < 0 ? part : part[..separator];
            if (!string.Equals(Uri.UnescapeDataString(key), name, StringComparison.Ordinal))
            {
                continue;
            }
            return separator < 0 ? string.Empty : Uri.UnescapeDataString(part[(separator + 1)..]);
        }
        return null;
    }

    private static string? QueryValue(string query, string name)
    {
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var key = separator < 0 ? part : part[..separator];
            if (!string.Equals(Uri.UnescapeDataString(key), name, StringComparison.Ordinal))
            {
                continue;
            }
            return separator < 0 ? string.Empty : Uri.UnescapeDataString(part[(separator + 1)..]);
        }
        return null;
    }
}
