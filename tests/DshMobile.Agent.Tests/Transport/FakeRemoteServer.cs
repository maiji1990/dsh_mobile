using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace DshMobile.Agent.Tests.Transport;

/// <summary>
/// Lightweight in-process loopback HTTP/1.1 + RFC 6455 WebSocket fake Route B server for
/// <see cref="DshRemoteConnectionTests"/> (N-01B-03).
/// </summary>
internal sealed class FakeRemoteServer : IAsyncDisposable
{
    public sealed record HttpRequestContext(
        string Method,
        string Path,
        string Query,
        IReadOnlyDictionary<string, string> Headers,
        byte[] Body);

    public sealed record HttpResponseData(
        int StatusCode,
        string ReasonPhrase,
        IReadOnlyList<(string Name, string Value)> Headers,
        byte[] Body)
    {
        public static HttpResponseData Json(int statusCode, string json, params (string Name, string Value)[] headers) =>
            new(statusCode, statusCode == 200 ? "OK" : "Error",
                [("Content-Type", "application/json; charset=utf-8"), .. headers],
                Encoding.UTF8.GetBytes(json));

        public static HttpResponseData Bytes(int statusCode, string contentType, byte[] body, params (string Name, string Value)[] headers) =>
            new(statusCode, statusCode == 200 ? "OK" : "Error",
                [("Content-Type", contentType), .. headers],
                body);
    }

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentBag<Task> _clients = [];
    private readonly ConcurrentBag<WebSocket> _openSockets = [];

    public FakeRemoteServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUri = new Uri($"http://127.0.0.1:{Port}/");
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public int Port { get; }
    public Uri BaseUri { get; }

    public Func<HttpRequestContext, Task<HttpResponseData>>? OnHttpRequest { get; set; }
    public Func<HttpRequestContext, WebSocket, CancellationToken, Task>? OnWebSocketConnected { get; set; }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcp = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                tcp.NoDelay = true;
                var task = HandleClientAsync(tcp, ct);
                _clients.Add(task);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task HandleClientAsync(TcpClient tcp, CancellationToken ct)
    {
        using (tcp)
        await using (var stream = tcp.GetStream())
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var req = await ReadHttpRequestAsync(stream, ct).ConfigureAwait(false);
                    if (req is null)
                    {
                        return;
                    }

                    if (req.Headers.TryGetValue("Upgrade", out var upgrade) &&
                        string.Equals(upgrade, "websocket", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!req.Headers.TryGetValue("Sec-WebSocket-Key", out var wsKey))
                        {
                            await WriteHttpResponseAsync(stream,
                                new HttpResponseData(400, "Bad Request", [], []), keepAlive: false, ct).ConfigureAwait(false);
                            return;
                        }

                        var accept = ComputeWebSocketAccept(wsKey);
                        var handshake =
                            "HTTP/1.1 101 Switching Protocols\r\n" +
                            "Upgrade: websocket\r\n" +
                            "Connection: Upgrade\r\n" +
                            $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(handshake), ct).ConfigureAwait(false);
                        await stream.FlushAsync(ct).ConfigureAwait(false);

                        using var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
                        {
                            IsServer = true,
                            KeepAliveInterval = Timeout.InfiniteTimeSpan,
                        });
                        _openSockets.Add(ws);
                        if (OnWebSocketConnected is { } handler)
                        {
                            await handler(req, ws, ct).ConfigureAwait(false);
                        }
                        return;
                    }

                    var response = OnHttpRequest is not null
                        ? await OnHttpRequest(req).ConfigureAwait(false)
                        : HttpResponseData.Json(404, "{\"ok\":false}");

                    var closeRequested = req.Headers.TryGetValue("Connection", out var conn) &&
                                         string.Equals(conn, "close", StringComparison.OrdinalIgnoreCase);
                    await WriteHttpResponseAsync(stream, response, keepAlive: !closeRequested, ct).ConfigureAwait(false);
                    if (closeRequested)
                    {
                        return;
                    }
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
            catch (WebSocketException)
            {
            }
        }
    }

    private static async Task<HttpRequestContext?> ReadHttpRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        using var headerBuf = new MemoryStream();
        int headerEnd = -1;
        while (true)
        {
            var chunk = new byte[1];
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return headerBuf.Length == 0 ? null : throw new EndOfStreamException();
            }
            headerBuf.WriteByte(chunk[0]);
            var current = headerBuf.GetBuffer().AsSpan(0, (int)headerBuf.Length);
            headerEnd = current.IndexOf("\r\n\r\n"u8);
            if (headerEnd >= 0)
            {
                break;
            }
            if (headerBuf.Length > 64 * 1024)
            {
                throw new InvalidDataException("HTTP headers exceeded 64 KiB");
            }
        }

        var rawHeader = Encoding.ASCII.GetString(headerBuf.GetBuffer(), 0, headerEnd);
        var lines = rawHeader.Split("\r\n");
        var requestLine = lines[0].Split(' ', 3);
        var method = requestLine[0];
        var target = requestLine[1];
        var qIdx = target.IndexOf('?', StringComparison.Ordinal);
        var path = qIdx >= 0 ? target[..qIdx] : target;
        var query = qIdx >= 0 ? target[qIdx..] : string.Empty;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }
            var name = lines[i][..colon].Trim();
            var val = lines[i][(colon + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out var existing) ? $"{existing}; {val}" : val;
        }

        var contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var lenStr))
        {
            contentLength = int.Parse(lenStr, CultureInfo.InvariantCulture);
        }

        var body = new byte[contentLength];
        var offset = 0;
        while (offset < contentLength)
        {
            var n = await stream.ReadAsync(body.AsMemory(offset, contentLength - offset), ct).ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException();
            }
            offset += n;
        }

        return new HttpRequestContext(method, path, query, headers, body);
    }

    private static async Task WriteHttpResponseAsync(NetworkStream stream, HttpResponseData res, bool keepAlive, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {res.StatusCode} {res.ReasonPhrase}\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"Content-Length: {res.Body.Length}\r\n");
        sb.Append(keepAlive ? "Connection: keep-alive\r\n" : "Connection: close\r\n");
        foreach (var (name, value) in res.Headers)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{name}: {value}\r\n");
        }
        sb.Append("\r\n");
        var headBytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(headBytes, ct).ConfigureAwait(false);
        if (res.Body.Length > 0)
        {
            await stream.WriteAsync(res.Body, ct).ConfigureAwait(false);
        }
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string ComputeWebSocketAccept(string key)
    {
        var combined = Encoding.ASCII.GetBytes(key.Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11");
#pragma warning disable CA5350 // RFC 6455 Sec-WebSocket-Accept mandates SHA-1
        return Convert.ToBase64String(SHA1.HashData(combined));
#pragma warning restore CA5350
    }

    public static async Task<string?> ReceiveTextAsync(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var res = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
            if (res.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            ms.Write(buf, 0, res.Count);
            if (res.EndOfMessage)
            {
                return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            }
        }
    }

    public static ValueTask SendTextAsync(WebSocket ws, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        foreach (var ws in _openSockets)
        {
            ws.Dispose();
        }
        await Task.WhenAll(_clients).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _cts.Dispose();
    }
}
