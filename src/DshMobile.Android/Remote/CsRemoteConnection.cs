using DshMobile.Core.Bridge;
using DshMobile.Runtime.Remote;

namespace DshMobile.App.Remote;

/// <summary>
/// The opt-in C# SSH connection of the remote page (PR-09 second segment, ADR-0012): at most one, process-wide, so it
/// survives the activity being recreated. The legacy connection and this one are never up at the same time (V1: one
/// remote at a time) — the page disconnects the other side before connecting either.
/// </summary>
internal static class CsRemoteConnection
{
    private static readonly Lock Gate = new();
    private static RemoteHostProfile? _profile;
    private static RemoteConnectAttempt? _last;

    public static RemoteSshConnector Connector() => new(new AndroidSshKeySource());

    private static volatile bool _legacyFallbackActive;

    /// <summary>
    /// Set when the legacy (JSch) connect was used on the fallback in this process, cleared after a legacy disconnect
    /// succeeded. The legacy connection only lives in this process, so without it the page never calls the legacy
    /// remote.* methods (ADR-0016: nothing on the default path goes through the bridge).
    /// </summary>
    public static bool LegacyFallbackActive
    {
        get => _legacyFallbackActive;
        set => _legacyFallbackActive = value;
    }

    /// <summary>True while a C# attempt is the page's current connection state (also after it failed, until cleared).</summary>
    public static bool Current
    {
        get
        {
            lock (Gate)
            {
                return _last is not null;
            }
        }
    }


    /// <summary>Makes <paramref name="attempt"/> (to <paramref name="profile"/>) current; the previous session is closed.</summary>
    public static void Set(RemoteHostProfile profile, RemoteConnectAttempt attempt)
    {
        RemoteSshSession? old;
        lock (Gate)
        {
            old = _last?.Session;
            _profile = profile;
            _last = attempt;
        }
        if (!ReferenceEquals(old, attempt.Session))
        {
            old?.Dispose();
        }
    }

    /// <summary>Closes the C# session (if any) and forgets the attempt.</summary>
    public static void Clear()
    {
        RemoteSshSession? old;
        lock (Gate)
        {
            old = _last?.Session;
            _last = null;
            _profile = null;
        }
        old?.Dispose();
    }

    /// <summary>The open C# session, if any (start / stop of the remote DSH run on it).</summary>
    public static RemoteSshSession? Session
    {
        get
        {
            lock (Gate)
            {
                return _last?.Session is { IsConnected: true } s ? s : null;
            }
        }
    }

    /// <summary>
    /// The current attempt as a legacy-shaped status, after the legacy liveness rule (<see cref="RemoteSshSession.Refresh"/>):
    /// a dropped session is DISCONNECTED / UNREACHABLE with its probe forgotten; a READY remote that exited loses its token.
    /// <paramref name="op"/> is the result of a start / stop just made (it carries the HTTP check / stop verdict).
    /// Null when nothing is current.
    /// </summary>
    public static RemoteStatusInfo? Status(RemoteDshStatus? op = null)
    {
        RemoteConnectAttempt? a;
        RemoteHostProfile? p;
        lock (Gate)
        {
            a = _last;
            p = _profile;
        }
        if (a is null)
        {
            return null;
        }
        if (a.Session is not { } s)
        {
            return new RemoteStatusInfo(a.State, a.Code, a.Message, p?.Id, p?.Label, a.Probe, null, p?.DshPort, false, a.Fingerprint, a.KeyType);
        }
        var d = s.Refresh();
        if (op is not null && d.State == op.State && d.Code == op.Code)
        {
            d = d with { HttpUnauthenticated = op.HttpUnauthenticated, RemoteDshStillRunning = op.RemoteDshStillRunning };
        }
        var probe = d.State == "DISCONNECTED" ? null : a.Probe;
        return new RemoteStatusInfo(d.State, d.Code, d.Message, p?.Id, p?.Label, probe, d.LocalPort, p?.DshPort, d.HasLaunchToken,
            null, null, d.HttpUnauthenticated, d.RemoteDshStillRunning);
    }
}
