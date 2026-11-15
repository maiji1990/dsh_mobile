// C# SSH session for the remote host slice (PR-09 second / third segments, ADR-0012 / ADR-0013) on SSH.NET 2026.0.0.
// Replaces the JSch session handling of this repo's native/native-bridge/.../RemoteHosts.kt (exec, startDsh,
// readTokenLine, httpStatus, stopDsh, stopDshChannel, refreshLiveness, disconnect); the decisions are the ported
// RemoteConnectRules / RemoteDshRules. Registered in docs/UPSTREAMS.md.

using System.Net;
using System.Net.Sockets;
using System.Text;
using DshMobile.Core.Contracts;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace DshMobile.Runtime.Remote;

/// <summary>
/// The session part of the legacy status (state / code / message / forward / token presence). The launch token itself
/// never appears here — only <see cref="HasLaunchToken"/>.
/// </summary>
public sealed record RemoteDshStatus(
    string State,
    string Code,
    string? Message,
    int? LocalPort,
    bool HasLaunchToken,
    int? HttpUnauthenticated = null,
    bool? RemoteDshStillRunning = null);

/// <summary>
/// A live C# SSH session to one remote host profile, and the remote <c>dsh web</c> it may run (one at a time). Operations
/// are serialised; <see cref="Refresh"/> may be called from any thread. Dispose to stop everything and disconnect.
/// </summary>
public sealed class RemoteSshSession : IDisposable
{
    private readonly SshClient _client;
    private readonly SemaphoreSlim _op = new(1, 1);
    private readonly Lock _gate = new();
    private int _disposed;

    // Guarded by _gate.
    private string _state = "CONNECTED";
    private string _code = RemoteCodes.Ok;
    private string? _message;
    private SshCommand? _dsh;
    private Task? _dshRun;
    private string? _launchToken; // only ever in memory (ADR-0006)
    private ForwardedPortLocal? _forward;

    internal RemoteSshSession(SshClient client, RemoteHostProfile profile)
    {
        _client = client;
        Profile = profile;
    }

    public RemoteHostProfile Profile { get; }

    /// <summary>False after a keepalive / socket failure or <see cref="Dispose"/>.</summary>
    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && _client.IsConnected;

    /// <summary>The local forward port while the remote DSH is reachable through it.</summary>
    public int? LocalPort
    {
        get
        {
            lock (_gate)
            {
                return _forward is { IsStarted: true } f ? (int)f.BoundPort : null;
            }
        }
    }

    internal void SetConnected(string code, string? message) => Set("CONNECTED", code, message);

    /// <summary>
    /// Legacy <c>refreshLiveness</c> + status: a dropped session is reported DISCONNECTED / UNREACHABLE (forward and
    /// token voided); a READY remote whose process ended becomes CONNECTED / DSH_START_FAILED and loses its token.
    /// </summary>
    public RemoteDshStatus Refresh()
    {
        if (!IsConnected)
        {
            lock (_gate)
            {
                if (_state is not "DISCONNECTED")
                {
                    _launchToken = null;
                    _forward = null;
                    _dsh = null;
                    _dshRun = null;
                    SetLocked("DISCONNECTED", RemoteCodes.Unreachable, RemoteDshRules.ConnectionLostMessage);
                }
            }
        }
        else
        {
            lock (_gate)
            {
                if (_state == "READY" && _dshRun is { IsCompleted: true })
                {
                    _launchToken = null;
                    SetLocked("CONNECTED", RemoteCodes.DshStartFailed, RemoteDshRules.ExitedMessage);
                }
            }
        }
        return Snapshot();
    }

    /// <summary>
    /// Legacy <c>startDsh</c>: starts <c>dsh --profile web --port &lt;dshPort&gt;</c>, waits for the launch line (token kept in
    /// memory only), opens the local forward 127.0.0.1:random → remote 127.0.0.1:dshPort and checks that DSH answers 401
    /// without the token.
    /// </summary>
    public async Task<RemoteDshStatus> StartDshAsync(CancellationToken ct = default)
    {
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsConnected)
            {
                return Fail(RemoteCodes.NotConnected, RemoteDshRules.NotConnectedMessage);
            }
            StopDshChannel();
            RemoveForward(); // legacy leaves the previous forward until disconnect; C# closes it here
            Set("STARTING", RemoteCodes.Ok, null);

            var reader = new LaunchOutputReader();
            SshCommand cmd;
            Task run;
            try
            {
                cmd = _client.CreateCommand(RemoteDshRules.StartCommand(Profile.DshPort));
                run = cmd.ExecuteAsync(CancellationToken.None);
            }
            catch (Exception e) when (e is SshException or SocketException or InvalidOperationException or ObjectDisposedException)
            {
                return Fail(RemoteCodes.DshStartFailed, $"could not start remote dsh web: {e.Message}");
            }
            lock (_gate)
            {
                _dsh = cmd;
                _dshRun = run;
            }
            var found = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var readers = Task.WhenAll(Drain(cmd.OutputStream, 0, reader, found), Drain(cmd.ExtendedOutputStream, 1, reader, found));
            var deadline = Task.Delay(RemoteDshRules.StartTimeout, ct);
            var first = await Task.WhenAny(found.Task, readers, deadline).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (first != found.Task && !found.Task.IsCompleted)
            {
                StopDshChannel();
                return Fail(RemoteCodes.DshStartFailed, reader.NoLaunchUrlMessage());
            }
            lock (_gate)
            {
                _launchToken = found.Task.Result;
            }

            int port;
            try
            {
                var forward = new ForwardedPortLocal("127.0.0.1", 0, "127.0.0.1", (uint)Profile.DshPort);
                _client.AddForwardedPort(forward);
                forward.Start();
                lock (_gate)
                {
                    _forward = forward;
                }
                port = (int)forward.BoundPort;
            }
            catch (Exception e) when (e is SshException or SocketException or InvalidOperationException or ObjectDisposedException)
            {
                StopDshChannel();
                RemoveForward();
                return Fail(RemoteCodes.ForwardFailed, e.Message);
            }
            var http = await HttpStatusAsync(port, ct).ConfigureAwait(false);
            var after = RemoteDshRules.AfterStart(http);
            Set("READY", after.Code, after.Message);
            return Snapshot() with { HttpUnauthenticated = http };
        }
        finally
        {
            _op.Release();
        }
    }

    /// <summary>
    /// Legacy <c>stopDsh</c>: TERM, close the channel, wait, then check through the forward whether the remote DSH still
    /// answers — reported, never assumed (Windows OpenSSH ignores the signal but ends the process with the channel).
    /// </summary>
    public async Task<RemoteDshStatus> StopDshAsync(CancellationToken ct = default)
    {
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var port = LocalPort;
            StopDshChannel();
            await Task.Delay(RemoteDshRules.StopSettle, ct).ConfigureAwait(false);
            int? http = port is { } p ? await HttpStatusAsync(p, ct).ConfigureAwait(false) : null;
            var still = RemoteDshRules.StillRunning(port is not null, http, IsConnected);
            RemoveForward();
            Set(IsConnected ? "CONNECTED" : "DISCONNECTED", RemoteCodes.Ok, null);
            return Snapshot() with { RemoteDshStillRunning = still };
        }
        finally
        {
            _op.Release();
        }
    }

    /// <summary>
    /// PR-09 shadow check (ADR-0013): through a forward of this session's own, the unauthenticated status of whatever
    /// listens at the remote 127.0.0.1:<paramref name="dshPort"/>. Starts nothing on the remote; null when nothing answers.
    /// </summary>
    public async Task<int?> ProbeHttpAsync(int dshPort, CancellationToken ct = default)
    {
        await _op.WaitAsync(ct).ConfigureAwait(false);
        ForwardedPortLocal? forward = null;
        try
        {
            forward = new ForwardedPortLocal("127.0.0.1", 0, "127.0.0.1", (uint)dshPort);
            _client.AddForwardedPort(forward);
            forward.Start();
            return await HttpStatusAsync((int)forward.BoundPort, ct).ConfigureAwait(false);
        }
        finally
        {
            if (forward is not null)
            {
                StopForward(forward);
            }
            _op.Release();
        }
    }

    /// <summary>Legacy <c>httpStatus</c>: GET / on the loopback forward, no redirects, no proxy; null when no answer.</summary>
    internal static async Task<int?> HttpStatusAsync(int port, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = RemoteDshRules.HttpConnectTimeout,
        };
        using var http = new HttpClient(handler) { Timeout = RemoteDshRules.HttpConnectTimeout + RemoteDshRules.HttpReadTimeout };
        try
        {
            using var resp = await http.GetAsync(new Uri($"http://127.0.0.1:{port}/"), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return (int)resp.StatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads one output stream of the remote dsh web to the end: lines go to <paramref name="reader"/> until the launch
    /// line; afterwards output is read and dropped so a chatty remote never stalls on flow control (ADR-0013).
    /// </summary>
    private static async Task Drain(Stream stream, int index, LaunchOutputReader reader, TaskCompletionSource<string> found)
    {
        var decoder = new UTF8Encoding(false).GetDecoder();
        var bytes = new byte[2048];
        var chars = new char[2049];
        try
        {
            int n;
            while ((n = await stream.ReadAsync(bytes).ConfigureAwait(false)) > 0)
            {
                if (found.Task.IsCompleted)
                {
                    continue;
                }
                var c = decoder.GetChars(bytes, 0, n, chars, 0);
                if (reader.Feed(index, new string(chars, 0, c)) is { } token)
                {
                    found.TrySetResult(token);
                }
            }
        }
        catch (Exception e) when (e is ObjectDisposedException or IOException or SshException)
        {
            // Channel closed under us: the stream simply ended.
        }
    }

    /// <summary>Legacy <c>stopDshChannel</c>: TERM, then close the channel; the token is voided.</summary>
    private void StopDshChannel()
    {
        SshCommand? cmd;
        lock (_gate)
        {
            cmd = _dsh;
            _dsh = null;
            _dshRun = null;
            _launchToken = null;
        }
        if (cmd is null)
        {
            return;
        }
        try
        {
            cmd.CancelAsync(forceKill: false, millisecondsTimeout: 500);
        }
        catch (Exception e) when (e is SshException or SocketException or InvalidOperationException or ObjectDisposedException)
        {
            // Not started or already gone.
        }
        cmd.Dispose();
    }

    private void RemoveForward()
    {
        ForwardedPortLocal? f;
        lock (_gate)
        {
            f = _forward;
            _forward = null;
        }
        if (f is not null)
        {
            StopForward(f);
        }
    }

    private void StopForward(ForwardedPortLocal f)
    {
        try
        {
            f.Stop();
            _client.RemoveForwardedPort(f);
        }
        catch (Exception e) when (e is SshException or SocketException or InvalidOperationException or ObjectDisposedException)
        {
            // Session already gone.
        }
        f.Dispose();
    }

    /// <summary>Legacy <c>fail</c>: DSH start / forward failures keep the session; everything else drops it.</summary>
    private RemoteDshStatus Fail(string code, string? message)
    {
        if (!RemoteDshRules.KeepsSession(code))
        {
            DisconnectCore();
        }
        Set(IsConnected ? "CONNECTED" : "DISCONNECTED", code, message);
        return Snapshot();
    }

    private void Set(string state, string code, string? message)
    {
        lock (_gate)
        {
            SetLocked(state, code, message);
        }
    }

    private void SetLocked(string state, string code, string? message)
    {
        _state = state;
        _code = code;
        _message = RemoteDshRules.Cap(message);
    }

    private RemoteDshStatus Snapshot()
    {
        lock (_gate)
        {
            return new RemoteDshStatus(_state, _code, _message,
                _forward is { IsStarted: true } f ? (int)f.BoundPort : null, _launchToken is not null);
        }
    }

    /// <summary>Runs one fixed command (never caller text) with the legacy exec rules.</summary>
    internal async Task<ProbeRun> ExecAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        using var cmd = _client.CreateCommand(command);
        cmd.CommandTimeout = timeout;
        try
        {
            await cmd.ExecuteAsync(ct).ConfigureAwait(false);
        }
        catch (SshOperationTimeoutException)
        {
            // Legacy would keep what was read so far; reading SSH.NET's output after a timeout can block, so C# keeps none.
            return new ProbeRun(null, "", true);
        }
        catch (Exception e) when (e is SshException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            return new ProbeRun(null, "", false);
        }
        return new ProbeRun(cmd.ExitStatus, RemoteConnectRules.CombineOutput(cmd.Result, cmd.Error), false);
    }

    private void DisconnectCore()
    {
        StopDshChannel();
        RemoveForward();
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        try
        {
            _client.Disconnect();
        }
        catch (Exception e) when (e is SshException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            // Already gone: nothing to release beyond Dispose.
        }
        _client.Dispose();
    }

    public void Dispose()
    {
        DisconnectCore();
        _op.Dispose();
    }
}
