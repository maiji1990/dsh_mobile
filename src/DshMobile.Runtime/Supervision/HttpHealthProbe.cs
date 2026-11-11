// Ported from DSHBox v1.5.1 (7c9d33c) sandbox-manager/.../HttpHealthChecker.kt (GPL-3.0-or-later).

using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace DshMobile.Runtime.Supervision;

/// <summary>Legacy <c>SandboxHealth</c> as the C# probe sees it (<c>dshProcessRunning</c> is the port check there too).</summary>
public sealed record DshHealthSnapshot(bool PortOpen, bool WebUiReady, int? HttpStatus, string? Error);

/// <summary>
/// The DSH WebUI health probe: TCP connect (port open), then a minimal HTTP/1.1 <c>GET /</c> on the same connection,
/// reading only the status line. ANY HTTP status 200-599 means alive - DSH answers 401 without the launch token, and
/// treating that as "not ready" once left a live DSH holding port 3080 while every restart hit EADDRINUSE. Redirects
/// are not followed and the token is never sent. Loopback HTTP only, so a raw socket is enough (HttpClient would add
/// ~3 MB of trimmed networking assemblies to the APK).
/// </summary>
public static class HttpHealthProbe
{
    public static async Task<DshHealthSnapshot> CheckAsync(
        string host = RuntimeConstants.DshHost,
        int port = RuntimeConstants.DshPort,
        string path = "/",
        TimeSpan? connectTimeout = null,
        TimeSpan? readTimeout = null,
        CancellationToken ct = default)
    {
        using var tcp = new TcpClient();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(connectTimeout ?? TimeSpan.FromSeconds(2));
            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new DshHealthSnapshot(false, false, null, e.GetType().Name);
        }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(readTimeout ?? TimeSpan.FromSeconds(3));
            var stream = tcp.GetStream();
            var request = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: {host}:{port}\r\nUser-Agent: dshmobile-health\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(request, cts.Token).ConfigureAwait(false);
            var status = await ReadStatusLineAsync(stream, cts.Token).ConfigureAwait(false);
            var code = ParseStatusCode(status);
            return code is { } c
                ? new DshHealthSnapshot(true, c is >= 200 and <= 599, c, null)
                : new DshHealthSnapshot(true, false, null, "no HTTP status line");
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new DshHealthSnapshot(true, false, null, e.GetType().Name);
        }
    }

    /// <summary>"HTTP/1.x NNN reason" → NNN; null for anything else.</summary>
    internal static int? ParseStatusCode(string? statusLine)
    {
        if (statusLine is null || !statusLine.StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            return null;
        }
        var parts = statusLine.Split(' ', 3);
        return parts.Length >= 2 && parts[1].Length == 3 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var code) ? code : null;
    }

    private static async Task<string?> ReadStatusLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[256];
        var sb = new StringBuilder();
        while (sb.Length < 1024)
        {
            var n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }
            sb.Append(Encoding.ASCII.GetString(buffer, 0, n));
            var text = sb.ToString();
            var eol = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (eol >= 0)
            {
                return text[..eol];
            }
        }
        return sb.Length == 0 ? null : sb.ToString();
    }
}
