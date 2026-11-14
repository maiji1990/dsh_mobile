// Ported from this repo's native/native-bridge/src/main/java/dshmobile/nativebridge/RemoteHosts.kt (startDsh,
// readTokenLine, stopDsh, refreshLiveness, set / fail; transition code for ADR-0006 — DSHBox has no remote-host
// feature). Registered in docs/UPSTREAMS.md. Behaviour locked by tests/TestVectors/remote-ssh.json (launchOutput,
// afterStart, stillRunning) and LegacySourceContractTests.Remote.

using DshMobile.Core.Bridge;

namespace DshMobile.Runtime.Remote;

/// <summary>
/// Line reader of the remote <c>dsh web</c> output (legacy <c>readTokenLine</c>): each stream keeps its own partial
/// line; complete lines (split on '\n', one trailing '\r' removed) are checked for the launch URL; every other line goes,
/// redacted and cut to 160 chars, into one shared tail of the last 6 lines (for the failure message). Only the token is
/// kept from the launch line; the line itself is never stored.
/// </summary>
public sealed class LaunchOutputReader
{
    private readonly System.Text.StringBuilder[] _partial;
    private readonly Queue<string> _tail = new();
    private readonly Lock _gate = new();

    public LaunchOutputReader(int streams = 2)
    {
        _partial = [.. Enumerable.Range(0, streams).Select(_ => new System.Text.StringBuilder())];
    }

    /// <summary>The token, once a launch line was seen on any stream.</summary>
    public string? Token { get; private set; }

    /// <summary>Feeds decoded text of stream <paramref name="stream"/>; returns the token when this chunk completed the launch line.</summary>
    public string? Feed(int stream, string chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        lock (_gate)
        {
            if (Token is not null)
            {
                return null; // legacy stops reading at the launch line; later output is drained and dropped
            }
            var sb = _partial[stream];
            sb.Append(chunk);
            while (true)
            {
                var text = sb.ToString();
                var nl = text.IndexOf('\n', StringComparison.Ordinal);
                if (nl < 0)
                {
                    return null;
                }
                var line = text[..nl];
                if (line.EndsWith('\r'))
                {
                    line = line.TrimEnd('\r');
                }
                sb.Remove(0, nl + 1);
                if (RemoteLaunchToken.TokenFrom(line) is { } token)
                {
                    Token = token;
                    return token;
                }
                var red = RemoteLaunchToken.Redact(line);
                _tail.Enqueue(red.Length > 160 ? red[..160] : red);
                while (_tail.Count > 6)
                {
                    _tail.Dequeue();
                }
            }
        }
    }

    /// <summary>Legacy failure message when no launch URL appeared.</summary>
    public string NoLaunchUrlMessage()
    {
        lock (_gate)
        {
            var joined = string.Join(" | ", _tail);
            return RemoteDshRules.NoLaunchUrlPrefix + (joined.Length > 400 ? joined[..400] : joined);
        }
    }
}

/// <summary>The library-independent rules of the legacy remote <c>dsh web</c> start / stop / liveness.</summary>
public static class RemoteDshRules
{
    public const string NotConnectedMessage = "connect first";
    public const string NoLaunchUrlPrefix = "remote dsh web printed no launch URL; tail: ";
    public const string ExitedMessage = "remote dsh web exited";
    public const string ConnectionLostMessage = "connection lost; connect again";

    /// <summary>Legacy <c>DSH_START_TIMEOUT_MS</c>.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Legacy <c>stopDsh</c> wait between closing the channel and checking the forward.</summary>
    public static readonly TimeSpan StopSettle = TimeSpan.FromMilliseconds(1500);

    /// <summary>Legacy <c>httpStatus</c> timeouts.</summary>
    public static readonly TimeSpan HttpConnectTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan HttpReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The fixed remote command. <paramref name="dshPort"/> is a validated integer (1024..65535), never caller text.</summary>
    public static string StartCommand(int dshPort)
    {
        if (dshPort is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(dshPort), dshPort, "dshPort must be 1024..65535");
        }
        return $"dsh --profile web --port {dshPort.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Legacy end of <c>startDsh</c>: state READY either way; OK only when the forwarded unauthenticated GET answered 401
    /// (auth enforced). Kotlin prints a null status as "null".
    /// </summary>
    public static RemoteOutcome AfterStart(int? http) =>
        http == 401
            ? new(RemoteCodes.Ok, null)
            : new(RemoteCodes.DshStartFailed, $"forwarded DSH answered HTTP {(http is { } h ? h.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null")} instead of 401");

    /// <summary>
    /// Legacy <c>stopDsh</c> verdict: no forward → unknown; an answer in 200..499 → still running; no answer while the
    /// session is up → not running (nothing listens remotely any more); no answer and no session → unknown.
    /// </summary>
    public static bool? StillRunning(bool hadForward, int? http, bool sessionConnected)
    {
        if (!hadForward)
        {
            return null;
        }
        if (http is { } h)
        {
            return h is >= 200 and <= 499;
        }
        return sessionConnected ? false : null;
    }

    /// <summary>Legacy <c>set</c>: messages are kept to 500 characters.</summary>
    public static string? Cap(string? message) => message is { Length: > 500 } m ? m[..500] : message;

    /// <summary>Legacy <c>fail</c>: DSH start / forward failures keep the session; every other failure drops it.</summary>
    public static bool KeepsSession(string code) => code is RemoteCodes.DshStartFailed or RemoteCodes.ForwardFailed;
}
