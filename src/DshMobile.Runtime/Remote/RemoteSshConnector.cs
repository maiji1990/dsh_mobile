// C# SSH connection for the remote host slice (PR-09 second segment, ADR-0012) on SSH.NET 2026.0.0 (MIT). Replaces the
// JSch session of this repo's native/native-bridge/.../RemoteHosts.kt connect / runProbe / exec; the outcome rules are
// the ported RemoteConnectRules. Registered in docs/UPSTREAMS.md.

using System.Net.Sockets;
using DshMobile.Core.Bridge;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Security;

namespace DshMobile.Runtime.Remote;

/// <summary>
/// SSH user-auth with the phone's Keystore key: <c>ecdsa-sha2-nistp256</c>, public blob from <see cref="SshWire"/>, every
/// signature made inside Keystore (DER) and converted to the SSH signature blob SSH.NET puts in the auth request.
/// </summary>
internal sealed class KeystoreHostAlgorithm(ISshKeySource keys, byte[] publicBlob) : HostAlgorithm(SshWire.Algorithm)
{
    public override byte[] Data => publicBlob;

    public override byte[] Sign(byte[] data) => SshWire.DerToSshSignature(keys.SignDer(data));

    public override bool VerifySignature(byte[] data, byte[] signature) => SshWire.Verify(publicBlob, data, signature);
}

internal sealed class KeystoreKeySource(HostAlgorithm algorithm) : IPrivateKeySource
{
    public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms { get; } = [algorithm];
}

/// <summary>One probe command as C# ran it. <see cref="TimedOut"/> has no legacy counterpart (legacy keeps partial output).</summary>
public sealed record ProbeRun(int? ExitStatus, string Output, bool TimedOut);

/// <summary>
/// Result of one C# connect attempt. <see cref="Session"/> is non-null only for CONNECTED (code OK or DSH_MISSING); the
/// caller owns it. <see cref="Fingerprint"/> / <see cref="KeyType"/> follow the legacy status: fingerprint for
/// HOST_KEY_UNKNOWN / HOST_KEY_CHANGED, key type for HOST_KEY_UNKNOWN only.
/// </summary>
public sealed record RemoteConnectAttempt(
    string State,
    string Code,
    string? Message,
    string? Fingerprint,
    string? KeyType,
    RemoteProbe? Probe,
    bool ProbeTimedOut,
    RemoteSshSession? Session)
{
    /// <summary>The fingerprint of whatever key the server presented (also when it matched the pin).</summary>
    public string? SeenFingerprint { get; init; }
}

/// <summary>
/// Connects to a remote host profile over SSH.NET with the ADR-0006 policy: public key only (Keystore), only the pinned
/// host key is accepted (an unknown or different key ends the attempt during key exchange, before auth), the host-key
/// algorithm list is JSch's, then the fixed capability probe. Never pins, never starts anything on the remote.
/// </summary>
public sealed class RemoteSshConnector(ISshKeySource keys)
{
    /// <summary>Test hook: overrides <see cref="RemoteConnectRules.ConnectTimeout"/>.</summary>
    internal TimeSpan ConnectTimeout { get; init; } = RemoteConnectRules.ConnectTimeout;

    public Task<RemoteConnectAttempt> ConnectAsync(RemoteHostProfile profile, CancellationToken ct = default) => Connect(profile, true, ct);

    /// <summary>Connect without the node / dsh probe: for the PR-09 shadow checks that only need a session (ADR-0013).</summary>
    public Task<RemoteConnectAttempt> ConnectSessionAsync(RemoteHostProfile profile, CancellationToken ct = default) => Connect(profile, false, ct);

    private async Task<RemoteConnectAttempt> Connect(RemoteHostProfile profile, bool probe, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var pub = keys.ReadPublicKey();
        if (pub is null)
        {
            return Failed(new RemoteOutcome(RemoteConnectRules.IdentityMissing, RemoteConnectRules.IdentityMissingMessage), null, null, null);
        }
        var algorithm = new KeystoreHostAlgorithm(keys, SshIdentity.PublicBlob(pub));
        var info = new ConnectionInfo(profile.Host, profile.Port, profile.User,
            new PrivateKeyAuthenticationMethod(profile.User, new KeystoreKeySource(algorithm)))
        {
            Timeout = ConnectTimeout,
        };
        RestrictHostKeyAlgorithms(info);

        HostKeyCheck? seen = null;
        var client = new SshClient(info) { KeepAliveInterval = RemoteConnectRules.KeepAlive };
        client.HostKeyReceived += (_, e) =>
        {
            var check = HostKeyPin.Check(profile.PinnedFingerprint, e.HostKey);
            seen = check;
            e.CanTrust = check.Decision == HostKeyDecision.Ok;
        };
        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (Kind(e, ct) is { } kind)
        {
            client.Dispose();
            var outcome = RemoteConnectRules.Classify(profile.PinnedFingerprint, seen?.SeenFingerprint, kind, e.Message);
            return Failed(outcome, seen, outcome.Code is RemoteCodes.HostKeyUnknown or RemoteCodes.HostKeyChanged ? seen?.SeenFingerprint : null,
                outcome.Code == RemoteCodes.HostKeyUnknown ? seen?.SeenType : null);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        var session = new RemoteSshSession(client, profile);
        try
        {
            if (!probe)
            {
                session.SetConnected(RemoteCodes.Ok, null);
                return new RemoteConnectAttempt("CONNECTED", RemoteCodes.Ok, null, null, null, null, false, session) { SeenFingerprint = seen?.SeenFingerprint };
            }
            var node = await session.ExecAsync(RemoteConnectRules.NodeProbe.Command, RemoteConnectRules.NodeProbe.Timeout, ct).ConfigureAwait(false);
            var dsh = await session.ExecAsync(RemoteConnectRules.DshProbe.Command, RemoteConnectRules.DshProbe.Timeout, ct).ConfigureAwait(false);
            var result = RemoteConnectRules.ToProbe(
                RemoteConnectRules.Summarize(node.ExitStatus, node.Output), RemoteConnectRules.Summarize(dsh.ExitStatus, dsh.Output));
            var after = RemoteConnectRules.AfterProbe(result);
            session.SetConnected(after.Code, after.Message);
            return new RemoteConnectAttempt("CONNECTED", after.Code, after.Message, null, null, result, node.TimedOut || dsh.TimedOut, session)
            {
                SeenFingerprint = seen?.SeenFingerprint,
            };
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>SSH.NET's default list minus the two entries JSch does not offer (<c>ssh-rsa-cert-v01</c>, <c>ssh-rsa</c>).</summary>
    internal static void RestrictHostKeyAlgorithms(ConnectionInfo info)
    {
        foreach (var name in info.HostKeyAlgorithms.Keys.ToList())
        {
            if (!RemoteConnectRules.HostKeyAlgorithms.Contains(name))
            {
                info.HostKeyAlgorithms.Remove(name);
            }
        }
    }

    /// <summary>Null for the caller's own cancellation (rethrown) and for programming errors.</summary>
    internal static SshFailureKind? Kind(Exception e, CancellationToken ct)
    {
        if (ct.IsCancellationRequested && e is OperationCanceledException)
        {
            return null;
        }
        return e switch
        {
            SshAuthenticationException => SshFailureKind.AuthRejected,
            SshOperationTimeoutException => SshFailureKind.Timeout,
            SocketException { SocketErrorCode: SocketError.TimedOut } => SshFailureKind.Timeout,
            OperationCanceledException => SshFailureKind.Timeout,
            SshException or SocketException or ProxyException or IOException => SshFailureKind.Other,
            _ => null,
        };
    }

    private static RemoteConnectAttempt Failed(RemoteOutcome outcome, HostKeyCheck? seen, string? fingerprint, string? keyType) =>
        new("DISCONNECTED", outcome.Code, outcome.Message is { Length: > 500 } m ? m[..500] : outcome.Message, fingerprint, keyType, null, false, null)
        {
            SeenFingerprint = seen?.SeenFingerprint,
        };
}
