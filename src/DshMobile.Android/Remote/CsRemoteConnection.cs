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

    /// <summary>True while a C# SSH session is open.</summary>
    public static bool Connected
    {
        get
        {
            lock (Gate)
            {
                return _last?.Session is { IsConnected: true };
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

    /// <summary>
    /// The current attempt as a legacy-shaped status, after the legacy liveness rule: a session that dropped (keepalive
    /// failed, network gone) is reported DISCONNECTED / UNREACHABLE and its probe forgotten. Null when nothing is current.
    /// </summary>
    public static RemoteStatusInfo? Status()
    {
        RemoteConnectAttempt? a;
        RemoteHostProfile? p;
        RemoteSshSession? dropped = null;
        lock (Gate)
        {
            if (_last?.Session is { IsConnected: false } s)
            {
                dropped = s;
                _last = _last with { State = "DISCONNECTED", Code = RemoteCodes.Unreachable, Message = "connection lost; connect again", Probe = null, Session = null };
            }
            a = _last;
            p = _profile;
        }
        dropped?.Dispose();
        return a is null
            ? null
            : new RemoteStatusInfo(a.State, a.Code, a.Message, p?.Id, p?.Label, a.Probe, null, p?.DshPort, false, a.Fingerprint, a.KeyType);
    }
}
