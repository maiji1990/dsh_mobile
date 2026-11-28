using DshMobile.Core.Contracts;
using DshMobile.Runtime.Hosting;
using DshMobile.Runtime.Remote;

namespace DshMobile.App.Remote;

/// <summary>
/// The C# SSH connection of the remote page (PR-09 second segment, ADR-0012): at most one, process-wide, so it survives
/// the activity being recreated (V1: one remote at a time).
/// 同时成对维护该会话的一次性回环票据代理（ADR-0036）：代理随会话建立、随替换 / 清除立即释放。
/// </summary>
internal static class CsRemoteConnection
{
    private static readonly Lock Gate = new();
    private static RemoteHostProfile? _profile;
    private static RemoteConnectAttempt? _last;
    private static LoopbackPortalProxy? _portalProxy;

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


    /// <summary>Makes <paramref name="attempt"/> (to <paramref name="profile"/>) current; the previous session is closed.</summary>
    public static void Set(RemoteHostProfile profile, RemoteConnectAttempt attempt)
    {
        RemoteSshSession? old;
        LoopbackPortalProxy? oldProxy;
        lock (Gate)
        {
            old = _last?.Session;
            oldProxy = _portalProxy;
            _profile = profile;
            _last = attempt;
            // 新代理只绑定当前会话：会话被替换或清除时旧代理随即释放，避免票据跨会话残留。
            _portalProxy = CreatePortalProxy(attempt.Session);
        }
        oldProxy?.Dispose();
        if (!ReferenceEquals(old, attempt.Session))
        {
            old?.Dispose();
        }
    }

    /// <summary>Closes the C# session (if any) and forgets the attempt; its one-time portal proxy is released first.</summary>
    public static void Clear()
    {
        RemoteSshSession? old;
        LoopbackPortalProxy? oldProxy;
        lock (Gate)
        {
            old = _last?.Session;
            oldProxy = _portalProxy;
            _portalProxy = null;
            _last = null;
            _profile = null;
        }
        oldProxy?.Dispose();
        old?.Dispose();
    }

    /// <summary>
    /// 当前远程会话是否具备安全打开完整 WebUI 的条件（ADR-0036）：会话仍是活跃会话、连接正常、
    /// 本地转发端口合法且内存中持有远程启动令牌。
    /// </summary>
    public static bool CanOpenRemoteWebUiPortal
    {
        get
        {
            RemoteSshSession? session;
            lock (Gate)
            {
                session = _last?.Session;
            }
            return session is not null && IsPortalEligible(session);
        }
    }

    /// <summary>签发远程会话的一次性回环票据地址；不可用时返回 <c>null</c>。严禁记录或持久化返回值。</summary>
    public static Uri? TryIssueRemoteWebUiPortalUri()
    {
        LoopbackPortalProxy? proxy;
        lock (Gate)
        {
            proxy = _portalProxy;
        }
        return proxy is not null && CanOpenRemoteWebUiPortal ? proxy.TryIssueLaunchUri() : null;
    }

    /// <summary>立即吊销远程会话的全部一次性票据与代理会话（停止远端 DSH 时调用）。</summary>
    public static void RevokeRemoteWebUiPortal()
    {
        LoopbackPortalProxy? proxy;
        lock (Gate)
        {
            proxy = _portalProxy;
        }
        proxy?.RevokeAll();
    }

    /// <summary>为当前会话创建票据代理；无会话时返回 <c>null</c>。</summary>
    private static LoopbackPortalProxy? CreatePortalProxy(RemoteSshSession? session) =>
        session is null
            ? null
            : new LoopbackPortalProxy(
                () => session.LocalPort,
                session,
                () => IsPortalEligible(session));

    /// <summary>
    /// 代理资格检查：必须是当前活跃会话、SSH 仍连接、本地转发端口合法且启动令牌非空；
    /// 任一条不满足都会让代理拒绝签发与兑换。
    /// </summary>
    private static bool IsPortalEligible(RemoteSshSession session)
    {
        lock (Gate)
        {
            if (!ReferenceEquals(_last?.Session, session))
            {
                return false;
            }
        }
        return session.IsConnected &&
               session.LocalPort is >= 1 and <= 65535 &&
               !string.IsNullOrWhiteSpace(session.Token);
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
    /// The current attempt as a status, after the DSHBox liveness rule (<see cref="RemoteSshSession.Refresh"/>):
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
