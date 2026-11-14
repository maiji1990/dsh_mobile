// Ported from this repo's native/native-bridge/src/main/java/dshmobile/nativebridge/RemoteHosts.kt (PinnedHostKeys,
// RemoteHosts.trust; transition code for ADR-0006 — DSHBox has no remote-host feature). Registered in
// docs/UPSTREAMS.md. Behaviour locked by tests/TestVectors/remote-ssh.json.

using System.Buffers.Binary;
using System.Text;

namespace DshMobile.Runtime.Remote;

/// <summary>Host-key decision (JSch <c>HostKeyRepository</c> result the legacy repository returns).</summary>
public enum HostKeyDecision
{
    /// <summary>The key is the pinned one.</summary>
    Ok,
    /// <summary>No pin yet (or no key): the connection is refused and the fingerprint is shown to the user.</summary>
    NotIncluded,
    /// <summary>A pin exists and the key differs: refused; only removing the host and re-confirming helps.</summary>
    Changed,
}

/// <summary>What one host-key check saw. <see cref="SeenFingerprint"/> is null only when no key was presented.</summary>
public sealed record HostKeyCheck(HostKeyDecision Decision, string? SeenFingerprint, string? SeenType);

/// <summary>
/// Host-key policy (ADR-0006): only the fingerprint the user confirmed is accepted. Unknown keys are reported and
/// rejected (no session is ever opened on an unconfirmed key); a different key is <see cref="HostKeyDecision.Changed"/>.
/// Pins are set only by the user, through <see cref="TrustAllowed"/>.
/// </summary>
public static class HostKeyPin
{
    /// <summary>Legacy <c>PinnedHostKeys.check</c> for the server host-key blob <paramref name="key"/>.</summary>
    public static HostKeyCheck Check(string? pinned, byte[]? key)
    {
        if (key is null)
        {
            return new HostKeyCheck(HostKeyDecision.NotIncluded, null, null);
        }
        var fp = SshWire.Fingerprint(key);
        return new HostKeyCheck(Decide(pinned, fp), fp, KeyType(key));
    }

    /// <summary>The decision for a key with fingerprint <paramref name="fingerprint"/> against <paramref name="pinned"/>.</summary>
    public static HostKeyDecision Decide(string? pinned, string fingerprint) =>
        pinned is null ? HostKeyDecision.NotIncluded : pinned == fingerprint ? HostKeyDecision.Ok : HostKeyDecision.Changed;

    /// <summary>
    /// The key type the legacy check records: the blob's first SSH string as UTF-8, or null when the length prefix is
    /// missing or runs past the blob (Java <c>String(bytes, 4, len)</c> throws).
    /// </summary>
    public static string? KeyType(ReadOnlySpan<byte> key)
    {
        if (key.Length < 4)
        {
            return null;
        }
        var len = BinaryPrimitives.ReadInt32BigEndian(key);
        if (len < 0 || len > key.Length - 4)
        {
            return null;
        }
        return Encoding.UTF8.GetString(key.Slice(4, len));
    }

    /// <summary>
    /// Legacy <c>trust</c>: a pin is accepted only if <paramref name="fingerprint"/> is well formed and is exactly the
    /// key this app last saw for host <paramref name="hostId"/> on an unconfirmed connection attempt — a caller cannot
    /// pin an arbitrary value. Otherwise the bridge answers <c>BAD_REQUEST</c> with <see cref="TrustRejected"/>.
    /// </summary>
    public static bool TrustAllowed(string hostId, string fingerprint, (string HostId, string Fingerprint)? lastUnknown) =>
        RemoteHostProfile.IsFingerprint(fingerprint)
        && lastUnknown is { } seen && seen.HostId == hostId && seen.Fingerprint == fingerprint;

    public const string TrustRejected = "fingerprint does not match the key last presented by this host";
}
