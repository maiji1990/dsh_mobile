// Ported from this repo's native/native-bridge/src/main/java/dshmobile/nativebridge/SshKeys.kt (keyStore, publicKey,
// hardwareBacked, signSsh; transition code for ADR-0006). Registered in docs/UPSTREAMS.md.

using Android.Runtime;
using Android.Security.Keystore;
using DshMobile.Runtime.Remote;
using Java.Security;
using Java.Security.Interfaces;

namespace DshMobile.App.Remote;

/// <summary>
/// The phone's SSH key in Android Keystore through standard .NET for Android interop (ADR-0007: platform API, not a
/// bridge). Same alias as the legacy <c>SshKeys</c>. Read-only towards the key store: it never generates, imports,
/// deletes or exports a key — the private key is used only through <see cref="Signature"/> inside Keystore.
/// </summary>
internal sealed class AndroidSshKeySource : ISshKeySource
{
    private const string Provider = "AndroidKeyStore";

    private static KeyStore Open()
    {
        var ks = KeyStore.GetInstance(Provider)!;
        ks.Load(null);
        return ks;
    }

    public SshPublicKey? ReadPublicKey()
    {
        var ks = Open();
        if (!ks.ContainsAlias(SshIdentity.KeystoreAlias))
        {
            return null;
        }
        var cert = ks.GetCertificate(SshIdentity.KeystoreAlias) ?? throw new InvalidOperationException("alias has no certificate");
        var pub = cert.PublicKey.JavaCast<IECPublicKey>() ?? throw new InvalidOperationException("not an EC public key");
        var w = pub.GetW() ?? throw new InvalidOperationException("EC public key without a point");
        return new SshPublicKey(w.AffineX!.ToByteArray()!, w.AffineY!.ToByteArray()!, HardwareBacked(ks));
    }

    public byte[] SignDer(byte[] data)
    {
        var key = PrivateKey(Open());
        var sig = Signature.GetInstance("SHA256withECDSA")!;
        sig.InitSign(key);
        sig.Update(data);
        return sig.Sign()!;
    }

    private static IPrivateKey PrivateKey(KeyStore ks) =>
        ks.GetKey(SshIdentity.KeystoreAlias, null).JavaCast<IPrivateKey>() ?? throw new InvalidOperationException("alias has no private key");

    /// <summary>Whether Keystore reports the key as hardware-backed (TEE / StrongBox); null when it cannot tell.</summary>
    private static bool? HardwareBacked(KeyStore ks)
    {
        try
        {
            var key = PrivateKey(ks);
            var factory = KeyFactory.GetInstance(key.Algorithm, Provider)!;
            var info = factory.GetKeySpec(key, Java.Lang.Class.FromType(typeof(KeyInfo))).JavaCast<KeyInfo>()!;
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                return info.SecurityLevel != (int)KeyStoreSecurityLevel.Software;
            }
#pragma warning disable CA1422 // legacy API below API 31, as in the legacy SshKeys
            return info.IsInsideSecureHardware;
#pragma warning restore CA1422
        }
        catch (Java.Lang.Exception)
        {
            return null;
        }
    }
}
