// Ported from this repo's native/native-bridge/src/main/java/dshmobile/nativebridge/SshKeys.kt (SshKeys: alias, comment,
// public blob / OpenSSH line / fingerprint, hardwareBacked, signSsh; transition code for ADR-0006). Registered in
// docs/UPSTREAMS.md.

using System.Security.Cryptography;
using System.Text;

namespace DshMobile.Runtime.Remote;

/// <summary>An EC P-256 public key: affine coordinates as unsigned big-endian bytes (any length, leading zeros ok).</summary>
public sealed record SshPublicKey(byte[] X, byte[] Y, bool? HardwareBacked);

/// <summary>
/// The phone's SSH key in Android Keystore, as the platform exposes it. Implementations never export the private key
/// and never create the key: in this migration segment the legacy <c>SshKeys.ensure()</c> owns creation.
/// </summary>
public interface ISshKeySource
{
    /// <summary>The public half of <see cref="SshIdentity.KeystoreAlias"/>, or null when the alias does not exist.</summary>
    SshPublicKey? ReadPublicKey();

    /// <summary>SHA256withECDSA inside Keystore; returns the DER signature Keystore produces.</summary>
    byte[] SignDer(byte[] data);
}

/// <summary>What C# established about the identity on its own.</summary>
public sealed record SshIdentityFacts(
    bool KeyPresent,
    string? PublicKeyLine,
    string? Fingerprint,
    bool? HardwareBacked,
    bool? SelfTestVerified,
    string? Error);

/// <summary>
/// The phone's SSH identity (ADR-0006): a non-exportable EC P-256 signing key under a fixed Keystore alias. The
/// alias and comment are the legacy ones so the same key — and the line the user already put in
/// <c>authorized_keys</c> — keeps working when C# takes over.
/// </summary>
public static class SshIdentity
{
    public const string KeystoreAlias = "dshmobile-ssh-ecdsa-p256";
    public const string Comment = "dsh-mobile";

    /// <summary>
    /// Prefix of the self-test challenge. It cannot be mistaken for SSH user-auth signed data, which starts with
    /// string(session id): read as a length, "dshm" is 1.6 GB.
    /// </summary>
    internal static readonly byte[] SelfTestLabel = Encoding.ASCII.GetBytes("dshmobile-ssh-selftest-v1\0");

    public static byte[] PublicBlob(SshPublicKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return SshWire.EcdsaPublicBlob(key.X, key.Y);
    }

    /// <summary>Legacy <c>signSsh</c>: Keystore DER signature converted to the SSH signature blob.</summary>
    public static byte[] SignSsh(ISshKeySource source, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(source);
        return SshWire.DerToSshSignature(source.SignDer(data));
    }

    /// <summary>
    /// Reads the public key and signs one random, labelled challenge through Keystore, then verifies the SSH-form
    /// signature with the public key in managed code. Proves the C# path can authenticate with the key without it
    /// ever leaving Keystore. Never throws for Keystore failures: they become <see cref="SshIdentityFacts.Error"/>.
    /// </summary>
    public static SshIdentityFacts Probe(ISshKeySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        SshPublicKey? key;
        try
        {
            key = source.ReadPublicKey();
        }
        catch (Exception ex)
        {
            return new SshIdentityFacts(false, null, null, null, null, $"read public key: {ex.GetType().Name}: {ex.Message}");
        }
        if (key is null)
        {
            return new SshIdentityFacts(false, null, null, null, null, null);
        }
        byte[] blob;
        try
        {
            blob = PublicBlob(key);
        }
        catch (FormatException ex)
        {
            return new SshIdentityFacts(true, null, null, key.HardwareBacked, null, $"public key: {ex.Message}");
        }
        var line = SshWire.OpenSshLine(blob, Comment);
        var fp = SshWire.Fingerprint(blob);
        var challenge = new byte[SelfTestLabel.Length + 32];
        SelfTestLabel.CopyTo(challenge, 0);
        RandomNumberGenerator.Fill(challenge.AsSpan(SelfTestLabel.Length));
        try
        {
            var sig = SignSsh(source, challenge);
            return new SshIdentityFacts(true, line, fp, key.HardwareBacked, SshWire.Verify(blob, challenge, sig), null);
        }
        catch (Exception ex)
        {
            return new SshIdentityFacts(true, line, fp, key.HardwareBacked, false, $"sign: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
