using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DshMobile.Runtime.Hosting;

/// <summary>
/// 本机回环一次性票据代理（ADR-0036）：应用内 Custom Tabs 只拿到一个短时、单次、绑定本机回环 Host 的
/// <c>nonce</c> 地址；代理在进程内用该票据兑换 DSH 的启动令牌与上游 Cookie，再把浏览器的后续 HTTP / WebSocket
/// 流量透传到真实的 <c>127.0.0.1:&lt;upstreamPort&gt;</c>。启动令牌、一次性票据、会话密钥与 Cookie 均只驻留内存，
/// 绝不写入 URL（启动令牌除外，它只出现在进程内上游请求里）或日志。
/// </summary>
public sealed class LoopbackPortalProxy : IDisposable
{
    private const int NonceByteCount = 16;
    private const int SessionSecretByteCount = 32;
    private const int MaxPendingTickets = 16;
    private const int MaxHeadBytes = 64 * 1024;
    private const int MaxReadBufferBytes = MaxHeadBytes + 64 * 1024;
    private const string LaunchPath = "/__dshmobile/launch";
    private const string PortalCookieName = "__dshmobile_portal";
    private const string UpstreamCookiePrefix = "dsh-auth-";
    private const string TicketRejectedBody = "一次性访问票据已失效或已使用，请返回应用重新打开。";
    private const string UnauthorizedBody = "代理会话无效或已过期，请返回应用重新打开。";

    private static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 释放后短暂保留监听的时间：让与 <see cref="Dispose"/> 并发的请求得到明确的 <c>401</c>，
    /// 而不是被连接重置；窗口结束后释放回环端口。
    /// </summary>
    private static readonly TimeSpan DisposeDrainWindow = TimeSpan.FromSeconds(1);

    private readonly Func<int?> _getUpstreamPort;
    private readonly IDshTokenSource _tokenSource;
    private readonly Func<bool> _isUpstreamEligible;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;
    private readonly Lock _gate = new();
    private readonly List<PendingTicket> _tickets = [];
    private readonly List<PortalSession> _sessions = [];
    private readonly HashSet<ProxyConnection> _connections = [];
    private readonly CancellationTokenSource _shutdown = new();

    private TcpListener? _listener;
    private int _proxyPort;
    private int _disposed;
    private int _activeWebSockets;
    private long? _lastLegalRequestTimestamp;
    private long _revocationGeneration;

    /// <summary>
    /// 创建回环代理。代理按需在 <c>127.0.0.1:0</c> 上启动监听，只向上游回环端口转发。
    /// </summary>
    /// <param name="getUpstreamPort">返回当前 DSH 上游回环端口；返回 <c>null</c> 或非法端口时拒绝签发与代理。</param>
    /// <param name="tokenSource">内存中的启动令牌来源；令牌变化或清空会立即吊销全部票据与代理会话。</param>
    /// <param name="isUpstreamEligible">当前宿主是否允许代理（例如完整 WebUI 兼容模式且宿主就绪）。</param>
    /// <param name="timeProvider">票据 TTL 与最近活跃时间使用的时钟；默认使用系统时钟。</param>
    /// <param name="log">可选日志回调；代理保证传给它的文本不含令牌、nonce、会话密钥或 Cookie。</param>
    public LoopbackPortalProxy(
        Func<int?> getUpstreamPort,
        IDshTokenSource tokenSource,
        Func<bool> isUpstreamEligible,
        TimeProvider? timeProvider = null,
        Action<string>? log = null)
    {
        _getUpstreamPort = getUpstreamPort ?? throw new ArgumentNullException(nameof(getUpstreamPort));
        _tokenSource = tokenSource ?? throw new ArgumentNullException(nameof(tokenSource));
        _isUpstreamEligible = isUpstreamEligible ?? throw new ArgumentNullException(nameof(isUpstreamEligible));
        _time = timeProvider ?? TimeProvider.System;
        _log = log;
        _tokenSource.TokenChanged += OnTokenChanged;
    }

    /// <summary>
    /// 签发票据并返回浏览器可直接打开的地址：<c>http://127.0.0.1:&lt;proxyPort&gt;/__dshmobile/launch?nonce=&lt;nonce&gt;</c>。
    /// 未释放、上游可用、端口合法且令牌非空时才返回地址；否则返回 <c>null</c>。
    /// 票据一次性有效 60 秒，最多保留 16 个未消费票据（超出按最旧淘汰）。
    /// </summary>
    public Uri? TryIssueLaunchUri()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return null;
        }
        if (!_isUpstreamEligible())
        {
            return null;
        }
        var upstreamPort = _getUpstreamPort();
        if (upstreamPort is not (>= 1 and <= 65535))
        {
            return null;
        }
        var token = _tokenSource.Token;
        var revision = _tokenSource.TokenRevision;
        if (string.IsNullOrWhiteSpace(token) || revision is null)
        {
            return null;
        }
        if (!EnsureListener())
        {
            return null;
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceByteCount);
        var issuedAt = _time.GetUtcNow();
        int proxyPort;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return null;
            }
            RemoveExpiredTicketsLocked(issuedAt);
            while (_tickets.Count >= MaxPendingTickets)
            {
                _tickets.RemoveAt(0);
            }
            _tickets.Add(new PendingTicket(nonce, revision.Value, issuedAt));
            proxyPort = _proxyPort;
        }

        var nonceText = Convert.ToHexStringLower(nonce);
        var uri = new Uri(
            FormattableString.Invariant($"http://127.0.0.1:{proxyPort}{LaunchPath}?nonce={nonceText}"),
            UriKind.Absolute);
        _log?.Invoke("portal: issued one-time launch ticket");
        return uri;
    }

    /// <summary>
    /// 是否存在活跃的代理 WebSocket，或在 <paramref name="recentWindow"/> 内发生过合法的代理请求。
    /// 供上层判断“浏览器是否仍在使用本机 WebUI”，不暴露任何票据或会话内容。已释放时恒为 <c>false</c>。
    /// </summary>
    /// <param name="recentWindow">允许的最近合法请求时间窗口。</param>
    public bool HasActiveOrRecentClients(TimeSpan recentWindow)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }
        lock (_gate)
        {
            if (_activeWebSockets > 0)
            {
                return true;
            }
            return _lastLegalRequestTimestamp is { } last &&
                   _time.GetElapsedTime(last) <= recentWindow;
        }
    }

    /// <summary>
    /// 立即清空全部一次性票据与内存会话，复位最近活跃时间、递增吊销代次，并取消、关闭所有活跃代理连接
    /// （客户端与上游 socket）。监听器保留，以便宿主重新就绪后继续签发；只有 <see cref="Dispose"/> 才停止监听。
    /// </summary>
    public void RevokeAll()
    {
        List<ProxyConnection> connections;
        lock (_gate)
        {
            _tickets.Clear();
            _sessions.Clear();
            _lastLegalRequestTimestamp = null;
            _revocationGeneration++;
            connections = [.. _connections];
            _connections.Clear();
        }
        foreach (var connection in connections)
        {
            connection.Dispose();
        }
        if (connections.Count > 0)
        {
            _log?.Invoke($"portal: revoked {connections.Count} active connection(s)");
        }
    }

    /// <summary>
    /// 释放代理：原子置位已释放标志、取消令牌变更订阅，吊销全部票据、会话并断开所有活跃连接。
    /// 监听器保留一个短暂的排空窗口，使与释放并发的请求得到明确的 <c>401</c> 而不是连接重置；
    /// 窗口结束后停止监听并释放 accept 取消源。释放后 <see cref="TryIssueLaunchUri"/> 返回 <c>null</c>。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _tokenSource.TokenChanged -= OnTokenChanged;

        var listener = _listener;
        _listener = null;
        RevokeAll();

        if (listener is null)
        {
            try
            {
                _shutdown.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            _shutdown.Dispose();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DisposeDrainWindow).ConfigureAwait(false);
                listener.Stop();
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
            }
            finally
            {
                try
                {
                    _shutdown.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                _shutdown.Dispose();
            }
        });
    }

    // ------------------------------------------------------------------------------------------------
    // 监听与连接生命周期
    // ------------------------------------------------------------------------------------------------

    private void OnTokenChanged(object? sender, EventArgs e) => RevokeAll();

    private bool EnsureListener()
    {
        lock (_gate)
        {
            if (_listener is not null)
            {
                return true;
            }
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                _proxyPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                _listener = listener;
                var token = _shutdown.Token;
                _ = Task.Run(() => AcceptLoopAsync(listener, token));
                _log?.Invoke($"portal: proxy listening on 127.0.0.1:{_proxyPort}");
                return true;
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                _log?.Invoke($"portal: proxy listener failed ({e.GetType().Name})");
                return false;
            }
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }
                continue;
            }

            client.NoDelay = true;
            var connection = new ProxyConnection(client, CancellationTokenSource.CreateLinkedTokenSource(ct));
            lock (_gate)
            {
                // 释放排空窗口内也接受连接，交给请求处理路径返回明确的 401；不要在这里直接丢弃。
                _connections.Add(connection);
            }
#pragma warning disable CA2016 // 释放排空窗口内已接受的连接仍需处理并返回明确的 401，不能因取消令牌跳过执行。
            _ = Task.Run(() => HandleConnectionAsync(connection));
#pragma warning restore CA2016
        }
    }

    private async Task HandleConnectionAsync(ProxyConnection connection)
    {
        try
        {
            using (connection)
            await using (var stream = connection.Client.GetStream())
            {
                var reader = new ConnectionReader(stream);
                await ProcessRequestAsync(stream, reader, connection, connection.Token).ConfigureAwait(false);
            }
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
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // 连接的异常消息可能包含 URL 等敏感上下文，只记录类型。
            _log?.Invoke($"portal: connection failed ({e.GetType().Name})");
        }
        finally
        {
            lock (_gate)
            {
                _connections.Remove(connection);
            }
        }
    }

    // ------------------------------------------------------------------------------------------------
    // HTTP 入口与安全边界
    // ------------------------------------------------------------------------------------------------

    private async Task ProcessRequestAsync(
        NetworkStream client,
        ConnectionReader reader,
        ProxyConnection connection,
        CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            await WriteUnauthorizedAsync(client, ct).ConfigureAwait(false);
            return;
        }

        var headBytes = await reader.ReadHeadAsync(ct).ConfigureAwait(false);
        if (headBytes is null)
        {
            return;
        }
        var request = HttpHead.ParseRequest(Encoding.ASCII.GetString(headBytes));
        if (request is null)
        {
            await WriteErrorAsync(client, 400, "Bad Request", "请求格式无效。", clearCookie: false, ct).ConfigureAwait(false);
            return;
        }

        var expectedHost = FormattableString.Invariant($"127.0.0.1:{_proxyPort}");
        if (!request.TryGet("Host", out var host) || !string.Equals(host, expectedHost, StringComparison.Ordinal))
        {
            _log?.Invoke("portal: rejected request (host_or_origin)");
            await WriteErrorAsync(client, 403, "Forbidden", "拒绝非本机回环 Host 请求。", clearCookie: false, ct).ConfigureAwait(false);
            return;
        }
        if (request.TryGet("Origin", out var origin) &&
            !string.Equals(origin, FormattableString.Invariant($"http://{expectedHost}"), StringComparison.Ordinal))
        {
            _log?.Invoke("portal: rejected request (host_or_origin)");
            await WriteErrorAsync(client, 403, "Forbidden", "拒绝非本机回环 Origin 请求。", clearCookie: false, ct).ConfigureAwait(false);
            return;
        }

        if (string.Equals(PathOf(request.Target), LaunchPath, StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
            {
                await WriteErrorAsync(client, 405, "Method Not Allowed", "该地址只允许 GET 请求。", clearCookie: false, ct).ConfigureAwait(false);
                return;
            }
            await HandleLaunchAsync(client, request, ct).ConfigureAwait(false);
            return;
        }

        await HandleProxyAsync(client, reader, request, connection, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------------------
    // 一次性票据兑换
    // ------------------------------------------------------------------------------------------------

    private async Task HandleLaunchAsync(NetworkStream client, HttpHead request, CancellationToken ct)
    {
        var nonceText = QueryValue(request.Target, "nonce");
        var presentedNonce = new byte[NonceByteCount];
        var wellFormedNonce = false;
        if (nonceText is { Length: NonceByteCount * 2 })
        {
            try
            {
                var decoded = Convert.FromHexString(nonceText);
                if (decoded.Length == NonceByteCount)
                {
                    decoded.CopyTo(presentedNonce, 0);
                    wellFormedNonce = true;
                }
            }
            catch (FormatException)
            {
            }
        }

        PendingTicket? ticket = null;
        long revocationGeneration;
        lock (_gate)
        {
            // 即使 nonce 格式不合法也遍历全部条目完成常数时间比对，仅在接受时要求格式合法。
            for (var i = 0; i < _tickets.Count; i++)
            {
                var matches = CryptographicOperations.FixedTimeEquals(_tickets[i].Nonce, presentedNonce);
                if (matches && wellFormedNonce)
                {
                    ticket = _tickets[i];
                    _tickets.RemoveAt(i);
                    break;
                }
            }
            revocationGeneration = _revocationGeneration;
        }
        if (ticket is null)
        {
            await WriteTicketRejectedAsync(client, ct).ConfigureAwait(false);
            return;
        }

        var now = _time.GetUtcNow();
        var upstreamPort = _getUpstreamPort();
        var token = _tokenSource.Token;
        var revision = _tokenSource.TokenRevision;
        var stillEligible = Volatile.Read(ref _disposed) == 0 && _isUpstreamEligible();
        if (now - ticket.IssuedAt > TicketLifetime ||
            !stillEligible ||
            upstreamPort is not (>= 1 and <= 65535) ||
            string.IsNullOrWhiteSpace(token) ||
            revision != ticket.TokenRevision)
        {
            await WriteTicketRejectedAsync(client, ct).ConfigureAwait(false);
            return;
        }

        var upstreamCookie = await RedeemUpstreamAsync(upstreamPort.Value, token!, ct).ConfigureAwait(false);
        if (upstreamCookie is null)
        {
            await WriteErrorAsync(client, 502, "Bad Gateway", "上游服务暂时不可用，请稍后重试。", clearCookie: false, ct).ConfigureAwait(false);
            return;
        }

        // 兑换期间令牌、资格或吊销代次可能已经变化，签发会话前在同一把锁内再核对一次。
        var secret = RandomNumberGenerator.GetBytes(SessionSecretByteCount);
        var session = new PortalSession(secret, upstreamCookie, ticket.TokenRevision);
        var accepted = false;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) == 0 &&
                _revocationGeneration == revocationGeneration &&
                _isUpstreamEligible() &&
                _getUpstreamPort() is (>= 1 and <= 65535) &&
                !string.IsNullOrWhiteSpace(_tokenSource.Token) &&
                _tokenSource.TokenRevision == ticket.TokenRevision)
            {
                _sessions.Add(session);
                _lastLegalRequestTimestamp = _time.GetTimestamp();
                accepted = true;
            }
        }
        if (!accepted)
        {
            await WriteTicketRejectedAsync(client, ct).ConfigureAwait(false);
            return;
        }

        var secretText = Convert.ToHexStringLower(secret);
        var response = new StringBuilder();
        response.Append("HTTP/1.1 303 See Other\r\n");
        response.Append("Location: /\r\n");
        response.Append(FormattableString.Invariant(
            $"Set-Cookie: {PortalCookieName}={secretText}; Path=/; HttpOnly; SameSite=Strict\r\n"));
        response.Append("Cache-Control: no-store\r\n");
        response.Append("Referrer-Policy: no-referrer\r\n");
        response.Append("Content-Length: 0\r\n");
        response.Append("Connection: close\r\n\r\n");
        await WriteAsciiAsync(client, response.ToString(), ct).ConfigureAwait(false);
        _log?.Invoke("portal: one-time launch ticket redeemed");
    }

    private async Task<string?> RedeemUpstreamAsync(int upstreamPort, string launchToken, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };
        using var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

        // 启动令牌只进入这次进程内回环请求，绝不写入代理 URL 或日志。
        var requestUri = FormattableString.Invariant(
            $"http://127.0.0.1:{upstreamPort}/?token={Uri.EscapeDataString(launchToken)}");
        try
        {
            using var response = await http
                .GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!response.Headers.TryGetValues("Set-Cookie", out var values))
            {
                _log?.Invoke("portal: upstream cookie missing");
                return null;
            }

            var cookies = new List<string>();
            foreach (var value in values)
            {
                var pair = value.Split(';', 2)[0].Trim();
                var separator = pair.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }
                if (pair[..separator].StartsWith(UpstreamCookiePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    cookies.Add(pair);
                }
            }
            if (cookies.Count == 0)
            {
                _log?.Invoke("portal: upstream cookie missing");
                return null;
            }
            return string.Join("; ", cookies);
        }
        catch (HttpRequestException e)
        {
            _log?.Invoke($"portal: upstream redemption failed ({e.GetType().Name})");
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log?.Invoke("portal: upstream redemption timed out");
            return null;
        }
    }

    // ------------------------------------------------------------------------------------------------
    // 已认证代理请求
    // ------------------------------------------------------------------------------------------------

    private async Task HandleProxyAsync(
        NetworkStream client,
        ConnectionReader reader,
        HttpHead request,
        ProxyConnection connection,
        CancellationToken ct)
    {
        var presentedSecret = new byte[SessionSecretByteCount];
        var wellFormedSecret = false;
        if (TryGetCookie(request, PortalCookieName, out var cookieText) &&
            cookieText.Length == SessionSecretByteCount * 2)
        {
            try
            {
                var decoded = Convert.FromHexString(cookieText);
                if (decoded.Length == SessionSecretByteCount)
                {
                    decoded.CopyTo(presentedSecret, 0);
                    wellFormedSecret = true;
                }
            }
            catch (FormatException)
            {
            }
        }

        PortalSession? session = null;
        int? upstreamPort = null;
        lock (_gate)
        {
            // 格式不合法时仍用全 0 dummy 遍历全部会话完成常数时间比对，仅在接受时要求格式合法。
            PortalSession? matched = null;
            foreach (var candidate in _sessions)
            {
                if (CryptographicOperations.FixedTimeEquals(candidate.Secret, presentedSecret))
                {
                    matched = candidate;
                    break;
                }
            }
            if (matched is not null && wellFormedSecret)
            {
                var port = _getUpstreamPort();
                if (Volatile.Read(ref _disposed) == 0 &&
                    _isUpstreamEligible() &&
                    port is (>= 1 and <= 65535) &&
                    !string.IsNullOrWhiteSpace(_tokenSource.Token) &&
                    _tokenSource.TokenRevision == matched.TokenRevision)
                {
                    session = matched;
                    upstreamPort = port;
                }
            }
        }
        if (session is null || upstreamPort is null)
        {
            await WriteUnauthorizedAsync(client, ct).ConfigureAwait(false);
            return;
        }

        var nowTimestamp = _time.GetTimestamp();
        lock (_gate)
        {
            _lastLegalRequestTimestamp = nowTimestamp;
        }
        await ProxyToUpstreamAsync(client, reader, request, session, upstreamPort.Value, connection, ct)
            .ConfigureAwait(false);
    }

    private async Task ProxyToUpstreamAsync(
        NetworkStream client,
        ConnectionReader reader,
        HttpHead request,
        PortalSession session,
        int upstreamPort,
        ProxyConnection connection,
        CancellationToken ct)
    {
        using var upstream = new TcpClient();
        using var cancelUpstream = ct.Register(static state => ((TcpClient)state!).Dispose(), upstream);
        try
        {
            await upstream.ConnectAsync(IPAddress.Loopback, upstreamPort, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException)
        {
            if (e is OperationCanceledException)
            {
                return;
            }
            _log?.Invoke($"portal: upstream connect failed ({e.GetType().Name})");
            await WriteErrorAsync(client, 502, "Bad Gateway", "上游服务暂时不可用，请稍后重试。", clearCookie: false, ct).ConfigureAwait(false);
            return;
        }

        if (!connection.AttachUpstream(upstream))
        {
            // 连接在建立上游期间被吊销或释放：socket 已由 AttachUpstream 关闭，直接结束。
            return;
        }

        upstream.NoDelay = true;
        await using var upstreamStream = upstream.GetStream();
        var webSocket = IsWebSocketUpgrade(request);
        var requestHead = BuildUpstreamHead(request, upstreamPort, session.UpstreamCookie, webSocket);
        await WriteAsciiAsync(upstreamStream, requestHead, ct).ConfigureAwait(false);
        if (webSocket)
        {
            // WebSocket 升级请求：把请求头之后已预读的帧字节按原顺序交给上游。
            await reader.WritePendingToAsync(upstreamStream, ct).ConfigureAwait(false);
        }
        else
        {
            await ForwardRequestBodyAsync(reader, upstreamStream, request, ct).ConfigureAwait(false);
        }

        var responseReader = new ConnectionReader(upstreamStream);
        byte[] responseHeadBytes;
        HttpHead response;
        while (true)
        {
            var rawHead = await responseReader.ReadHeadAsync(ct).ConfigureAwait(false);
            if (rawHead is null)
            {
                await WriteErrorAsync(client, 502, "Bad Gateway", "上游响应无效。", clearCookie: false, ct).ConfigureAwait(false);
                return;
            }
            var parsed = HttpHead.ParseResponse(Encoding.ASCII.GetString(rawHead));
            if (parsed is null)
            {
                await WriteErrorAsync(client, 502, "Bad Gateway", "上游响应无效。", clearCookie: false, ct).ConfigureAwait(false);
                return;
            }
            responseHeadBytes = rawHead;
            response = parsed;
            if (response.StatusCode == 101 || !response.IsInformational)
            {
                break;
            }
            await WriteResponseHeadAsync(client, response, forceClose: false, ct).ConfigureAwait(false);
        }

        if (webSocket && response.StatusCode == 101)
        {
            // 101 响应头与紧随其后的余留字节原样写回客户端。
            await client.WriteAsync(responseHeadBytes, ct).ConfigureAwait(false);
            await WriteAsciiAsync(client, "\r\n\r\n", ct).ConfigureAwait(false);
            await responseReader.WritePendingToAsync(client, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _activeWebSockets++;
            }
            try
            {
                // 任一方向结束（对端关闭、取消或异常）都必须取消另一方向，避免半开连接把双向泵永久挂住。
                using var pump = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var clientToUpstream = client.CopyToAsync(upstreamStream, pump.Token);
                var upstreamToClient = upstreamStream.CopyToAsync(client, pump.Token);
                await Task.WhenAny(clientToUpstream, upstreamToClient).ConfigureAwait(false);
                await pump.CancelAsync().ConfigureAwait(false);
                try
                {
                    await Task.WhenAll(clientToUpstream, upstreamToClient).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
            finally
            {
                lock (_gate)
                {
                    _activeWebSockets--;
                }
            }
            return;
        }

        await WriteResponseHeadAsync(client, response, forceClose: true, ct).ConfigureAwait(false);
        await ForwardResponseBodyAsync(
            response,
            responseReader,
            client,
            string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase),
            ct).ConfigureAwait(false);
    }

    private string BuildUpstreamHead(HttpHead request, int upstreamPort, string upstreamCookie, bool webSocket)
    {
        var upstreamHost = FormattableString.Invariant($"127.0.0.1:{upstreamPort}");
        var proxyOrigin = FormattableString.Invariant($"http://127.0.0.1:{_proxyPort}");

        var builder = new StringBuilder();
        builder.Append(request.StartLine).Append("\r\n");
        var hostWritten = false;
        var originWritten = false;
        var refererWritten = false;
        var cookieWritten = false;
        foreach (var (name, value) in request.Headers)
        {
            if (string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase))
            {
                if (hostWritten)
                {
                    continue;
                }
                builder.Append("Host: ").Append(upstreamHost).Append("\r\n");
                hostWritten = true;
            }
            else if (string.Equals(name, "Origin", StringComparison.OrdinalIgnoreCase))
            {
                if (originWritten)
                {
                    continue;
                }
                builder.Append("Origin: http://").Append(upstreamHost).Append("\r\n");
                originWritten = true;
            }
            else if (string.Equals(name, "Referer", StringComparison.OrdinalIgnoreCase))
            {
                if (refererWritten)
                {
                    continue;
                }
                var rewritten = value.StartsWith(proxyOrigin, StringComparison.Ordinal) &&
                                IsOriginBoundary(value, proxyOrigin.Length)
                    ? FormattableString.Invariant($"http://{upstreamHost}{value[proxyOrigin.Length..]}")
                    : value;
                builder.Append("Referer: ").Append(rewritten).Append("\r\n");
                refererWritten = true;
            }
            else if (string.Equals(name, "Cookie", StringComparison.OrdinalIgnoreCase))
            {
                if (cookieWritten)
                {
                    continue;
                }
                builder.Append("Cookie: ").Append(upstreamCookie).Append("\r\n");
                cookieWritten = true;
            }
            else if (string.Equals(name, "Connection", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(name, "Proxy-Connection", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(name, "Expect", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            else
            {
                builder.Append(name).Append(": ").Append(value).Append("\r\n");
            }
        }

        if (!hostWritten)
        {
            builder.Append("Host: ").Append(upstreamHost).Append("\r\n");
        }
        if (!cookieWritten)
        {
            builder.Append("Cookie: ").Append(upstreamCookie).Append("\r\n");
        }
        builder.Append(webSocket ? "Connection: Upgrade\r\n" : "Connection: close\r\n");
        builder.Append("\r\n");
        return builder.ToString();
    }

    private static bool IsOriginBoundary(string value, int offset) =>
        value.Length == offset || (value.Length > offset && value[offset] is '/' or '?' or '#');

    private static bool IsWebSocketUpgrade(HttpHead request) =>
        request.TryGet("Upgrade", out var upgrade) &&
        upgrade.Contains("websocket", StringComparison.OrdinalIgnoreCase);

    private static async Task ForwardRequestBodyAsync(
        ConnectionReader reader,
        Stream upstream,
        HttpHead request,
        CancellationToken ct)
    {
        if (request.TryGet("Transfer-Encoding", out var transferEncoding) &&
            transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            await reader.CopyChunkedToAsync(upstream, ct).ConfigureAwait(false);
            return;
        }
        if (request.TryGet("Content-Length", out var lengthText) &&
            long.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var length) &&
            length > 0)
        {
            await reader.CopyExactToAsync(upstream, length, ct).ConfigureAwait(false);
            return;
        }
        // 无长度声明的请求也应把头部之后已预读的字节交给上游，避免丢失同段到达的 body。
        await reader.WritePendingToAsync(upstream, ct).ConfigureAwait(false);
    }

    private static async Task ForwardResponseBodyAsync(
        HttpHead response,
        ConnectionReader reader,
        Stream client,
        bool isHeadRequest,
        CancellationToken ct)
    {
        if (isHeadRequest || response.StatusCode is 204 or 304)
        {
            return;
        }
        if (response.TryGet("Transfer-Encoding", out var transferEncoding) &&
            transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            await reader.CopyChunkedToAsync(client, ct).ConfigureAwait(false);
            return;
        }
        if (response.TryGet("Content-Length", out var lengthText) &&
            long.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var length))
        {
            if (length > 0)
            {
                await reader.CopyExactToAsync(client, length, ct).ConfigureAwait(false);
            }
            return;
        }
        await reader.CopyToEndAsync(client, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------------------
    // 原始 HTTP 读写
    // ------------------------------------------------------------------------------------------------

    private static async Task WriteAsciiAsync(Stream stream, string text, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text), ct).ConfigureAwait(false);
    }

    private static async Task WriteResponseHeadAsync(
        NetworkStream client,
        HttpHead response,
        bool forceClose,
        CancellationToken ct)
    {
        var builder = new StringBuilder();
        builder.Append(response.StartLine).Append("\r\n");
        foreach (var (name, value) in response.Headers)
        {
            if (forceClose &&
                (string.Equals(name, "Connection", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(name, "Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(name, "Proxy-Connection", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            builder.Append(name).Append(": ").Append(value).Append("\r\n");
        }
        if (forceClose)
        {
            builder.Append("Connection: close\r\n");
        }
        builder.Append("\r\n");
        await WriteAsciiAsync(client, builder.ToString(), ct).ConfigureAwait(false);
    }

    private static async Task WriteErrorAsync(
        NetworkStream client,
        int statusCode,
        string reason,
        string body,
        bool clearCookie,
        CancellationToken ct)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var builder = new StringBuilder();
        builder.Append(FormattableString.Invariant($"HTTP/1.1 {statusCode} {reason}\r\n"));
        builder.Append("Content-Type: text/plain; charset=utf-8\r\n");
        builder.Append(FormattableString.Invariant($"Content-Length: {bodyBytes.Length}\r\n"));
        builder.Append("Cache-Control: no-store\r\n");
        builder.Append("Referrer-Policy: no-referrer\r\n");
        if (clearCookie)
        {
            builder.Append(FormattableString.Invariant(
                $"Set-Cookie: {PortalCookieName}=; Path=/; Max-Age=0; HttpOnly; SameSite=Strict\r\n"));
        }
        builder.Append("Connection: close\r\n\r\n");
        await WriteAsciiAsync(client, builder.ToString(), ct).ConfigureAwait(false);
        if (bodyBytes.Length > 0)
        {
            await client.WriteAsync(bodyBytes, ct).ConfigureAwait(false);
        }
    }

    private Task WriteTicketRejectedAsync(NetworkStream client, CancellationToken ct)
    {
        _log?.Invoke("portal: rejected launch ticket");
        return WriteErrorAsync(client, 403, "Forbidden", TicketRejectedBody, clearCookie: false, ct);
    }

    private Task WriteUnauthorizedAsync(NetworkStream client, CancellationToken ct)
    {
        _log?.Invoke("portal: rejected unauthorized request");
        return WriteErrorAsync(client, 401, "Unauthorized", UnauthorizedBody, clearCookie: true, ct);
    }

    // ------------------------------------------------------------------------------------------------
    // 解析与查询辅助
    // ------------------------------------------------------------------------------------------------

    private void RemoveExpiredTicketsLocked(DateTimeOffset now)
    {
        for (var i = _tickets.Count - 1; i >= 0; i--)
        {
            if (now - _tickets[i].IssuedAt > TicketLifetime)
            {
                _tickets.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// 解析 HTTP 请求目标中的路径。绝对形式（<c>http(s)://host/path?query</c>）交给 <see cref="Uri.AbsolutePath"/>；
    /// 以 <c>/</c> 开头的 origin-form（浏览器实际发送的形式）必须先按原始字符串剥离 <c>?</c> 与 <c>#</c>，
    /// 不能交给 <see cref="Uri.TryCreate(string?, UriKind, out Uri?)"/>：在 Linux / Android 上它会把
    /// <c>/__dshmobile/launch?nonce=…</c> 当作本地文件路径，生成
    /// <c>file:///__dshmobile/launch%3Fnonce=…</c>，使 <see cref="Uri.AbsolutePath"/> 与 <see cref="Uri.Query"/> 全部错位。
    /// </summary>
    internal static string PathOf(string target)
    {
        if (TryParseHttpAbsoluteTarget(target, out var absolute))
        {
            return absolute.AbsolutePath;
        }
        return StripQueryAndFragment(target);
    }

    /// <summary>
    /// 读取请求目标中的查询参数值。绝对形式使用 <see cref="Uri.Query"/>；origin-form 按原始字符串截取
    /// <c>?</c> 之后、<c>#</c> 之前的部分。
    /// </summary>
    internal static string? QueryValue(string target, string name)
    {
        string query;
        if (TryParseHttpAbsoluteTarget(target, out var absolute))
        {
            query = absolute.Query;
        }
        else
        {
            var separator = target.IndexOf('?', StringComparison.Ordinal);
            if (separator < 0)
            {
                return null;
            }
            query = target[separator..];
            var fragment = query.IndexOf('#', StringComparison.Ordinal);
            if (fragment >= 0)
            {
                query = query[..fragment];
            }
        }
        if (query.StartsWith('?'))
        {
            query = query[1..];
        }
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var key = separator < 0 ? part : part[..separator];
            try
            {
                if (!string.Equals(Uri.UnescapeDataString(key), name, StringComparison.Ordinal))
                {
                    continue;
                }
                return separator < 0 ? string.Empty : Uri.UnescapeDataString(part[(separator + 1)..]);
            }
            catch (UriFormatException)
            {
                continue;
            }
        }
        return null;
    }

    /// <summary>
    /// 仅当目标不是以 <c>/</c> 开头的 origin-form，且能被解析成 <c>http</c> / <c>https</c> 绝对 URI 时返回 <c>true</c>。
    /// Linux / Android 上的 <see cref="Uri.TryCreate(string?, UriKind, out Uri?)"/> 会把 <c>/</c> 开头的路径解释为
    /// 本地 <c>file://</c> URI，因此必须先排除 origin-form，再限定协议为 HTTP(S)。
    /// </summary>
    private static bool TryParseHttpAbsoluteTarget(
        string target,
        [NotNullWhen(true)] out Uri? absolute)
    {
        absolute = null;
        if (target.StartsWith('/'))
        {
            return false;
        }
        if (!Uri.TryCreate(target, UriKind.Absolute, out var parsed))
        {
            return false;
        }
        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        absolute = parsed;
        return true;
    }

    private static string StripQueryAndFragment(string target)
    {
        var end = target.IndexOf('?', StringComparison.Ordinal);
        var fragment = target.IndexOf('#', StringComparison.Ordinal);
        if (end < 0 || (fragment >= 0 && fragment < end))
        {
            end = fragment;
        }
        return end < 0 ? target : target[..end];
    }

    private static bool TryGetCookie(HttpHead request, string name, out string value)
    {
        value = string.Empty;
        if (!request.TryGet("Cookie", out var header))
        {
            return false;
        }
        foreach (var part in header.Split(';'))
        {
            var pair = part.Trim();
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }
            if (!string.Equals(pair[..separator], name, StringComparison.Ordinal))
            {
                continue;
            }
            value = pair[(separator + 1)..];
            return true;
        }
        return false;
    }

    private sealed record PendingTicket(byte[] Nonce, long TokenRevision, DateTimeOffset IssuedAt);

    private sealed record PortalSession(byte[] Secret, string UpstreamCookie, long TokenRevision);

    /// <summary>
    /// 单条代理连接：持有客户端与（可选）上游 socket 以及取消源，吊销时在锁外统一关闭。
    /// </summary>
    private sealed class ProxyConnection : IDisposable
    {
        private readonly Lock _sync = new();
        private TcpClient? _upstream;
        private int _disposed;

        public ProxyConnection(TcpClient client, CancellationTokenSource cancellation)
        {
            Client = client;
            Cancellation = cancellation;
            Token = cancellation.Token;
        }

        public TcpClient Client { get; }

        public CancellationTokenSource Cancellation { get; }

        public CancellationToken Token { get; }

        /// <summary>登记上游 socket；若连接已被吊销则立即关闭上游并返回 <c>false</c>。</summary>
        public bool AttachUpstream(TcpClient upstream)
        {
            lock (_sync)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    upstream.Dispose();
                    return false;
                }
                _upstream = upstream;
                return true;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            TcpClient? upstream;
            lock (_sync)
            {
                upstream = _upstream;
                _upstream = null;
            }
            Client.Dispose();
            upstream?.Dispose();
            Cancellation.Dispose();
        }
    }

    /// <summary>
    /// 带预读缓冲的 HTTP 流读取器：读取头部后保留缓冲区中紧随头部的 body 字节，供转发时按原顺序写出。
    /// </summary>
    private sealed class ConnectionReader
    {
        private readonly Stream _stream;
        private byte[] _buffer = new byte[16 * 1024];
        private int _start;
        private int _end;
        private bool _eof;

        public ConnectionReader(Stream stream)
        {
            _stream = stream;
        }

        public int BufferedCount => _end - _start;

        /// <summary>读取到 <c>\r\n\r\n</c> 为止的头部（不含终止符），并保留其后已预读的字节。</summary>
        public async Task<byte[]?> ReadHeadAsync(CancellationToken ct)
        {
            while (true)
            {
                for (var i = _start; i + 3 < _end; i++)
                {
                    if (_buffer[i] == (byte)'\r' &&
                        _buffer[i + 1] == (byte)'\n' &&
                        _buffer[i + 2] == (byte)'\r' &&
                        _buffer[i + 3] == (byte)'\n')
                    {
                        var head = new byte[i - _start];
                        Buffer.BlockCopy(_buffer, _start, head, 0, head.Length);
                        _start = i + 4;
                        return head;
                    }
                }

                if (_end - _start >= MaxHeadBytes)
                {
                    throw new InvalidDataException("HTTP headers exceeded the size limit");
                }
                if (_eof)
                {
                    return _start == _end ? null : throw new EndOfStreamException();
                }

                EnsureCapacityForRead();
                var read = await _stream.ReadAsync(_buffer.AsMemory(_end), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    _eof = true;
                }
                else
                {
                    _end += read;
                }
            }
        }

        /// <summary>把缓冲区中尚未消费的预读字节按原顺序写入目标流。</summary>
        public async Task WritePendingToAsync(Stream destination, CancellationToken ct)
        {
            if (_start == _end)
            {
                return;
            }
            var pending = new ReadOnlyMemory<byte>(_buffer, _start, _end - _start);
            _start = _end;
            await destination.WriteAsync(pending, ct).ConfigureAwait(false);
        }

        /// <summary>精确转发指定长度的字节（先消费预读字节，再从流中补足）。</summary>
        public async Task CopyExactToAsync(Stream destination, long count, CancellationToken ct)
        {
            while (count > 0)
            {
                if (!await EnsureBufferedAsync(ct).ConfigureAwait(false))
                {
                    throw new EndOfStreamException();
                }
                var take = (int)Math.Min(BufferedCount, count);
                var chunk = new ReadOnlyMemory<byte>(_buffer, _start, take);
                _start += take;
                count -= take;
                await destination.WriteAsync(chunk, ct).ConfigureAwait(false);
            }
        }

        /// <summary>按 chunked 编码转发请求体或响应体（含 trailer）。</summary>
        public async Task CopyChunkedToAsync(Stream destination, CancellationToken ct)
        {
            while (true)
            {
                var line = await ReadLineAsync(ct).ConfigureAwait(false) ?? throw new EndOfStreamException();
                var sizeText = line;
                var separator = sizeText.IndexOf(';');
                if (separator >= 0)
                {
                    sizeText = sizeText[..separator];
                }
                if (!long.TryParse(sizeText.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size < 0)
                {
                    throw new InvalidDataException("invalid chunk size");
                }

                await WriteAsciiAsync(destination, line + "\r\n", ct).ConfigureAwait(false);
                if (size == 0)
                {
                    while (true)
                    {
                        var trailer = await ReadLineAsync(ct).ConfigureAwait(false) ?? throw new EndOfStreamException();
                        await WriteAsciiAsync(destination, trailer + "\r\n", ct).ConfigureAwait(false);
                        if (trailer.Length == 0)
                        {
                            return;
                        }
                    }
                }

                await CopyExactToAsync(destination, size, ct).ConfigureAwait(false);
                var crlf = new byte[2];
                await ReadExactIntoAsync(crlf, ct).ConfigureAwait(false);
                await destination.WriteAsync(crlf, ct).ConfigureAwait(false);
            }
        }

        /// <summary>把剩余全部字节（含预读字节）流式写入目标流，直到上游关闭。</summary>
        public async Task CopyToEndAsync(Stream destination, CancellationToken ct)
        {
            await WritePendingToAsync(destination, ct).ConfigureAwait(false);
            await _stream.CopyToAsync(destination, ct).ConfigureAwait(false);
        }

        private async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            var bytes = new List<byte>(128);
            while (true)
            {
                if (!await EnsureBufferedAsync(ct).ConfigureAwait(false))
                {
                    return bytes.Count == 0 ? null : throw new EndOfStreamException();
                }
                var value = _buffer[_start++];
                if (value == (byte)'\n')
                {
                    if (bytes.Count > 0 && bytes[^1] == (byte)'\r')
                    {
                        bytes.RemoveAt(bytes.Count - 1);
                    }
                    return Encoding.ASCII.GetString(bytes.ToArray());
                }
                bytes.Add(value);
                if (bytes.Count > MaxHeadBytes)
                {
                    throw new InvalidDataException("HTTP line exceeded the size limit");
                }
            }
        }

        private async Task ReadExactIntoAsync(byte[] destination, CancellationToken ct)
        {
            var offset = 0;
            while (offset < destination.Length)
            {
                if (!await EnsureBufferedAsync(ct).ConfigureAwait(false))
                {
                    throw new EndOfStreamException();
                }
                var take = Math.Min(BufferedCount, destination.Length - offset);
                Buffer.BlockCopy(_buffer, _start, destination, offset, take);
                _start += take;
                offset += take;
            }
        }

        private async Task<bool> EnsureBufferedAsync(CancellationToken ct)
        {
            if (_start < _end)
            {
                return true;
            }
            if (_eof)
            {
                return false;
            }
            _start = 0;
            _end = 0;
            var read = await _stream.ReadAsync(_buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                _eof = true;
                return false;
            }
            _end = read;
            return true;
        }

        private void EnsureCapacityForRead()
        {
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            if (_end < _buffer.Length)
            {
                return;
            }
            var next = Math.Min(Math.Max(_buffer.Length * 2, 16 * 1024), MaxReadBufferBytes);
            if (next <= _buffer.Length)
            {
                throw new InvalidDataException("HTTP headers exceeded the size limit");
            }
            Array.Resize(ref _buffer, next);
        }
    }

    private sealed class HttpHead
    {
        private readonly Dictionary<string, string> _lookup;

        private HttpHead(
            string startLine,
            string method,
            string target,
            int statusCode,
            List<(string Name, string Value)> headers)
        {
            StartLine = startLine;
            Method = method;
            Target = target;
            StatusCode = statusCode;
            Headers = headers;
            _lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in headers)
            {
                _lookup[name] = _lookup.TryGetValue(name, out var existing)
                    ? $"{existing}; {value}"
                    : value;
            }
        }

        public string StartLine { get; }

        public string Method { get; }

        public string Target { get; }

        public int StatusCode { get; }

        public bool IsInformational => StatusCode is >= 100 and < 200 && StatusCode != 101;

        public IReadOnlyList<(string Name, string Value)> Headers { get; }

        public bool TryGet(string name, out string value) => _lookup.TryGetValue(name, out value!);

        public static HttpHead? ParseRequest(string head)
        {
            var lines = head.Split("\r\n");
            if (lines.Length == 0 || lines[0].Length == 0)
            {
                return null;
            }
            var parts = lines[0].Split(' ', 3);
            if (parts.Length < 3 || parts[0].Length == 0 || parts[1].Length == 0)
            {
                return null;
            }
            return new HttpHead(lines[0], parts[0], parts[1], 0, ParseHeaders(lines));
        }

        public static HttpHead? ParseResponse(string head)
        {
            var lines = head.Split("\r\n");
            if (lines.Length == 0 || lines[0].Length == 0)
            {
                return null;
            }
            var parts = lines[0].Split(' ', 3);
            if (parts.Length < 2 ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var statusCode))
            {
                return null;
            }
            return new HttpHead(lines[0], string.Empty, string.Empty, statusCode, ParseHeaders(lines));
        }

        private static List<(string Name, string Value)> ParseHeaders(string[] lines)
        {
            var headers = new List<(string Name, string Value)>(lines.Length);
            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                var separator = line.IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }
                var name = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();
                if (name.Length > 0)
                {
                    headers.Add((name, value));
                }
            }
            return headers;
        }
    }
}
