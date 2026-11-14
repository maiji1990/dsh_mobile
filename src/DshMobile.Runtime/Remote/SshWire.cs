// Ported from this repo's native/native-bridge/src/main/java/dshmobile/nativebridge/SshKeys.kt (object SshWire,
// transition code for ADR-0006; DSHBox has no remote-host feature). Registered in docs/UPSTREAMS.md. Behaviour locked by
// tests/TestVectors/remote-ssh.json (legacy Kotlin LegacyRemoteSshParityTest runs the same cases).

using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DshMobile.Runtime.Remote;

/// <summary>
/// SSH wire encodings for <c>ecdsa-sha2-nistp256</c> (RFC 5656 / RFC 4253), as the legacy <c>SshWire</c> writes them.
/// Pure functions; the private key is never an input (signing happens inside Android Keystore).
/// </summary>
public static class SshWire
{
    public const string Algorithm = "ecdsa-sha2-nistp256";
    private const string Curve = "nistp256";

    /// <summary>
    /// Public key blob: string(alg) || string("nistp256") || string(0x04 || X || Y). Coordinates are unsigned
    /// big-endian of any length (Java <c>BigInteger.toByteArray()</c> may carry a leading 0x00 sign byte); each is
    /// left-padded to 32 bytes. More than 32 significant bytes is an error, as in the legacy <c>fixed32</c>.
    /// </summary>
    public static byte[] EcdsaPublicBlob(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
    {
        var point = new byte[65];
        point[0] = 0x04;
        Fixed32(x).CopyTo(point.AsSpan(1));
        Fixed32(y).CopyTo(point.AsSpan(33));
        var w = new SshWriter();
        w.String(Encoding.ASCII.GetBytes(Algorithm));
        w.String(Encoding.ASCII.GetBytes(Curve));
        w.String(point);
        return w.ToArray();
    }

    /// <summary>
    /// DER ECDSA signature (SEQUENCE { INTEGER r, INTEGER s }, what Android Keystore returns) → SSH signature blob:
    /// string(alg) || string(mpint(r) || mpint(s)). Parsing is as lenient as the legacy one: the SEQUENCE length is
    /// skipped, not checked, and bytes after <c>s</c> are ignored. Malformed input throws <see cref="FormatException"/>.
    /// </summary>
    public static byte[] DerToSshSignature(ReadOnlySpan<byte> der)
    {
        var (r, s) = ParseDerSignature(der);
        var inner = new SshWriter();
        inner.String(JavaPositiveBigIntegerBytes(r));
        inner.String(JavaPositiveBigIntegerBytes(s));
        var w = new SshWriter();
        w.String(Encoding.ASCII.GetBytes(Algorithm));
        w.String(inner.ToArray());
        return w.ToArray();
    }

    /// <summary><c>ecdsa-sha2-nistp256 AAAA… comment</c>, the line users put in <c>authorized_keys</c>.</summary>
    public static string OpenSshLine(ReadOnlySpan<byte> blob, string comment) =>
        $"{Algorithm} {Convert.ToBase64String(blob)} {comment}";

    /// <summary>OpenSSH-style fingerprint of a key blob: <c>SHA256:</c> + unpadded base64 of SHA-256(blob).</summary>
    public static string Fingerprint(ReadOnlySpan<byte> blob) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');

    /// <summary>
    /// The two DER INTEGERs as unsigned big-endian magnitudes without leading zeros (Java <c>BigInteger(1, bytes)</c>).
    /// </summary>
    public static (byte[] R, byte[] S) ParseDerSignature(ReadOnlySpan<byte> der)
    {
        var i = 0;
        if (Byte(der, i++) != 0x30)
        {
            throw new FormatException("not a DER sequence");
        }
        i += DerLengthSize(der, i);
        var r = Integer(der, ref i);
        var s = Integer(der, ref i);
        return (r, s);
    }

    /// <summary>
    /// Verifies an SSH <c>ecdsa-sha2-nistp256</c> signature blob over <paramref name="data"/> with the public key
    /// <paramref name="publicBlob"/>. Used by the identity self-test to prove that a Keystore signature survives the
    /// DER → SSH conversion intact; never used to authenticate a peer.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> publicBlob, ReadOnlySpan<byte> data, ReadOnlySpan<byte> sshSignature)
    {
        try
        {
            var key = new SshReader(publicBlob);
            if (Encoding.ASCII.GetString(key.String()) != Algorithm || Encoding.ASCII.GetString(key.String()) != Curve)
            {
                return false;
            }
            var point = key.String();
            if (point.Length != 65 || point[0] != 0x04 || !key.AtEnd)
            {
                return false;
            }
            var sig = new SshReader(sshSignature);
            if (Encoding.ASCII.GetString(sig.String()) != Algorithm)
            {
                return false;
            }
            var inner = new SshReader(sig.String());
            if (!sig.AtEnd)
            {
                return false;
            }
            var r = inner.String();
            var s = inner.String();
            if (!inner.AtEnd)
            {
                return false;
            }
            var p1363 = new byte[64];
            if (!TryUnsigned32(r, p1363.AsSpan(0, 32)) || !TryUnsigned32(s, p1363.AsSpan(32, 32)))
            {
                return false;
            }
            using var ecdsa = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = point[1..33].ToArray(), Y = point[33..65].ToArray() },
            });
            return ecdsa.VerifyData(data, p1363, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Unsigned big-endian of an SSH mpint that must be non-negative and fit 32 bytes.</summary>
    private static bool TryUnsigned32(ReadOnlySpan<byte> mpint, Span<byte> into)
    {
        if (mpint.Length > 0 && (mpint[0] & 0x80) != 0)
        {
            return false;
        }
        var start = 0;
        while (start < mpint.Length && mpint[start] == 0)
        {
            start++;
        }
        var mag = mpint[start..];
        if (mag.Length > 32)
        {
            return false;
        }
        into.Clear();
        mag.CopyTo(into[(32 - mag.Length)..]);
        return true;
    }

    private static byte[] Integer(ReadOnlySpan<byte> der, ref int i)
    {
        if (Byte(der, i++) != 0x02)
        {
            throw new FormatException("not a DER integer");
        }
        var len = DerLength(der, i);
        i += DerLengthSize(der, i);
        // Legacy: Kotlin copyOfRange(i, i + len) throws when the integer runs past the end or len wrapped negative.
        if (len < 0 || len > der.Length - i)
        {
            throw new FormatException("truncated DER integer");
        }
        var v = StripLeadingZeros(der.Slice(i, len));
        i += len;
        return v;
    }

    /// <summary>Legacy <c>derLength</c>: short form, or long form with (first &amp; 0x7f) length bytes (0 → length 0).</summary>
    private static int DerLength(ReadOnlySpan<byte> b, int at)
    {
        var first = Byte(b, at);
        if (first < 0x80)
        {
            return first;
        }
        var len = 0;
        for (var k = 1; k <= (first & 0x7f); k++)
        {
            len = unchecked((len << 8) | Byte(b, at + k)); // Kotlin Int shl: wraps like this
        }
        return len;
    }

    private static int DerLengthSize(ReadOnlySpan<byte> b, int at)
    {
        var first = Byte(b, at);
        return first < 0x80 ? 1 : 1 + (first & 0x7f);
    }

    private static int Byte(ReadOnlySpan<byte> b, int at) =>
        at >= 0 && at < b.Length ? b[at] : throw new FormatException("truncated DER");

    private static byte[] StripLeadingZeros(ReadOnlySpan<byte> v)
    {
        var start = 0;
        while (start < v.Length && v[start] == 0)
        {
            start++;
        }
        return v[start..].ToArray();
    }

    /// <summary>
    /// Java <c>BigInteger.toByteArray()</c> of a non-negative magnitude: minimal two's complement, a leading 0x00 when
    /// the high bit is set, and <c>{0x00}</c> for zero (the legacy <c>writeMpint</c>; an RFC 4251 mpint of zero would be
    /// empty, but r = 0 / s = 0 is not a valid ECDSA signature anyway).
    /// </summary>
    internal static byte[] JavaPositiveBigIntegerBytes(ReadOnlySpan<byte> magnitude)
    {
        var mag = StripLeadingZeros(magnitude);
        if (mag.Length == 0)
        {
            return [0];
        }
        if ((mag[0] & 0x80) == 0)
        {
            return mag;
        }
        var withSign = new byte[mag.Length + 1];
        mag.CopyTo(withSign.AsSpan(1));
        return withSign;
    }

    private static byte[] Fixed32(ReadOnlySpan<byte> v)
    {
        var raw = StripLeadingZeros(v);
        if (raw.Length > 32)
        {
            throw new FormatException("coordinate too large");
        }
        var out32 = new byte[32];
        raw.CopyTo(out32.AsSpan(32 - raw.Length));
        return out32;
    }

    private sealed class SshWriter
    {
        private readonly ArrayBufferWriter<byte> _out = new();

        public void String(ReadOnlySpan<byte> b)
        {
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)b.Length);
            _out.Write((ReadOnlySpan<byte>)len);
            _out.Write(b);
        }

        public byte[] ToArray() => _out.WrittenSpan.ToArray();
    }

    private ref struct SshReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _at;

        public readonly bool AtEnd => _at == _data.Length;

        public ReadOnlySpan<byte> String()
        {
            if (_data.Length - _at < 4)
            {
                throw new FormatException("truncated SSH string");
            }
            var len = BinaryPrimitives.ReadUInt32BigEndian(_data[_at..]);
            _at += 4;
            if (len > (uint)(_data.Length - _at))
            {
                throw new FormatException("truncated SSH string");
            }
            var s = _data.Slice(_at, (int)len);
            _at += (int)len;
            return s;
        }
    }
}
