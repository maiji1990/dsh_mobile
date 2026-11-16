using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace DshMobile.Runtime.Updates;

/// <summary>
/// The release public keys the app trusts (Ed25519, 32 raw bytes each). A key's id is derived from the key itself
/// (<see cref="KeyId"/>), so a signature envelope cannot attribute a signature to a different key than the one that
/// verifies it. Several keys may be trusted at once (rotation: sign with old and new, then drop the old one).
/// </summary>
public sealed class ReleaseTrust
{
    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

    public ReleaseTrust(IEnumerable<byte[]> publicKeys)
    {
        ArgumentNullException.ThrowIfNull(publicKeys);
        foreach (var k in publicKeys)
        {
            if (k is not { Length: 32 })
            {
                throw new ArgumentException("an Ed25519 public key is 32 bytes", nameof(publicKeys));
            }
            _ = new Ed25519PublicKeyParameters(k, 0); // refuses bytes that are not a curve point
            _keys[KeyId(k)] = (byte[])k.Clone();
        }
    }

    /// <summary>
    /// The keys built into this app, from the embedded <c>Updates/release-trusted-keys.txt</c> (one base64 raw key per
    /// line, <c>#</c> comments) — the same file <c>release-publish.yml</c> verifies against. <b>Empty today</b>: the
    /// release signing key is generated and held by the project owner (tooling/release-manifest-sign.mjs keygen; the
    /// private key only in the protected release environment), and its public half is added in its own reviewed commit.
    /// Until then every discovery ends in <see cref="UpdateErrorCodes.NoTrustedKeys"/> — never in an unverified offer.
    /// A malformed file yields an empty trust (fail closed); <c>ReleaseTrustFileTests</c> keeps it well-formed.
    /// </summary>
    public static ReleaseTrust BuiltIn { get; } = LoadBuiltIn();

    public const string BuiltInResource = "DshMobile.Updates.release-trusted-keys.txt";

    /// <summary>Parses a trusted-keys file; throws <see cref="FormatException"/> / <see cref="ArgumentException"/> on any bad line.</summary>
    public static ReleaseTrust Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var keys = new List<byte[]>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            keys.Add(Convert.FromBase64String(line));
        }
        return new ReleaseTrust(keys);
    }

    private static ReleaseTrust LoadBuiltIn()
    {
        try
        {
            using var s = typeof(ReleaseTrust).Assembly.GetManifestResourceStream(BuiltInResource);
            if (s is null)
            {
                return new ReleaseTrust([]);
            }
            using var reader = new StreamReader(s, Encoding.UTF8);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception e) when (e is FormatException or ArgumentException or IOException)
        {
            return new ReleaseTrust([]);
        }
    }

    public static ReleaseTrust FromBase64(params string[] keys) => new(keys.Select(Convert.FromBase64String));

    public bool IsEmpty => _keys.Count == 0;

    public IReadOnlyCollection<string> KeyIds => _keys.Keys;

    /// <summary><c>ed25519:</c> + the first 16 hex digits of SHA-256 over the raw public key.</summary>
    public static string KeyId(ReadOnlySpan<byte> publicKey) => "ed25519:" + Convert.ToHexStringLower(SHA256.HashData(publicKey))[..16];

    internal bool TryGet(string keyId, out byte[] key) => _keys.TryGetValue(keyId, out key!);
}

/// <summary>Outcome of verifying a manifest; <see cref="Manifest"/> is set only when <see cref="Ok"/>.</summary>
public sealed record ReleaseVerification(bool Ok, string Code, string? Message, ReleaseManifest? Manifest, string? KeyId)
{
    internal static ReleaseVerification Fail(string code, string message) => new(false, code, message, null, null);
}

/// <summary>
/// Verifies a release manifest (ADR-0022) inside the C# trust boundary — no plugin, agent or guest code takes part or
/// can skip it. Order: trusted keys exist → the detached envelope (<c>release-manifest.json.sig</c>) parses → one of its
/// signatures is by a trusted key and verifies over <see cref="Domain"/> + the exact manifest bytes → the content
/// parses strictly → channel, validity window and anti-rollback hold. SHA-256 of each artifact is checked later, by
/// the downloader, against the digest in this (now trusted) manifest.
/// </summary>
public static class ReleaseManifestVerifier
{
    /// <summary>Domain separation: a release signature can never be replayed as a signature over anything else.</summary>
    public const string Domain = "DSHM-RELEASE-MANIFEST-V1\n";

    /// <summary>A generatedAt this far in the future is accepted (device clocks drift).</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(10);

    /// <param name="expectedChannel">The channel the user follows; a manifest for another channel is refused (no swapping preview in for stable).</param>
    /// <param name="notOlderThan">generatedAt of the last manifest accepted on this device, if any: an older one is a rollback (a replayed manifest).</param>
    public static ReleaseVerification Verify(
        ReadOnlySpan<byte> manifest,
        ReadOnlySpan<byte> envelope,
        ReleaseTrust trust,
        string expectedChannel,
        DateTimeOffset now,
        DateTimeOffset? notOlderThan = null)
    {
        ArgumentNullException.ThrowIfNull(trust);
        if (trust.IsEmpty)
        {
            return ReleaseVerification.Fail(UpdateErrorCodes.NoTrustedKeys, "this build pins no release public key; online updates are unavailable");
        }
        List<(string KeyId, byte[] Sig)> signatures;
        try
        {
            signatures = ParseEnvelope(envelope);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidDataException)
        {
            return ReleaseVerification.Fail(UpdateErrorCodes.SignatureMissing, "signature envelope unreadable: " + e.Message);
        }
        var message = new byte[Encoding.ASCII.GetByteCount(Domain) + manifest.Length];
        var prefix = Encoding.ASCII.GetBytes(Domain, message);
        manifest.CopyTo(message.AsSpan(prefix));
        string? verifiedBy = null;
        var trustedSeen = false;
        foreach (var (keyId, sig) in signatures)
        {
            if (!trust.TryGet(keyId, out var key))
            {
                continue;
            }
            trustedSeen = true;
            if (Ed25519Verify(key, message, sig))
            {
                verifiedBy = keyId;
                break;
            }
        }
        if (verifiedBy is null)
        {
            return trustedSeen
                ? ReleaseVerification.Fail(UpdateErrorCodes.SignatureInvalid, "the manifest does not match its signature")
                : ReleaseVerification.Fail(UpdateErrorCodes.KeyUnknown, "signed by " + string.Join(", ", signatures.Select(s => s.KeyId)) + "; trusted: " + string.Join(", ", trust.KeyIds));
        }
        ReleaseManifest parsed;
        try
        {
            parsed = ReleaseManifestParser.Parse(manifest);
        }
        catch (ReleaseManifestException e)
        {
            return ReleaseVerification.Fail(UpdateErrorCodes.SchemaInvalid, e.Message);
        }
        if (parsed.Channel != expectedChannel)
        {
            return ReleaseVerification.Fail(UpdateErrorCodes.ChannelMismatch, $"manifest is for '{parsed.Channel}', expected '{expectedChannel}'");
        }
        if (parsed.GeneratedAt > now + ClockSkew)
        {
            return ReleaseVerification.Fail(UpdateErrorCodes.NotYetValid, $"generatedAt {parsed.GeneratedAt:O} is in the future (device time {now:O})");
        }
        if (parsed.ExpiresAt <= now)
        {
            return ReleaseVerification.Fail(UpdateErrorCodes.Expired, $"expired at {parsed.ExpiresAt:O}");
        }
        if (notOlderThan is { } floor && parsed.GeneratedAt < floor)
        {
            return ReleaseVerification.Fail(UpdateErrorCodes.Rollback, $"generatedAt {parsed.GeneratedAt:O} is older than the last accepted manifest ({floor:O})");
        }
        return new ReleaseVerification(true, RuntimeErrorCodes.Ok, null, parsed, verifiedBy);
    }

    /// <summary><c>{"schema":1,"signatures":[{"alg":"ed25519","keyId":"ed25519:…","sig":"&lt;base64, 64 bytes&gt;"}]}</c>.</summary>
    private static List<(string, byte[])> ParseEnvelope(ReadOnlySpan<byte> envelope)
    {
        var reader = new Utf8JsonReader(envelope);
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var s) || s.ValueKind != JsonValueKind.Number || s.GetInt32() != 1
            || !root.TryGetProperty("signatures", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("expected {\"schema\":1,\"signatures\":[...]}");
        }
        var list = new List<(string, byte[])>();
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object
                || !e.TryGetProperty("alg", out var alg) || alg.ValueKind != JsonValueKind.String
                || !e.TryGetProperty("keyId", out var id) || id.ValueKind != JsonValueKind.String
                || !e.TryGetProperty("sig", out var sig) || sig.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("each signature needs alg, keyId and sig");
            }
            if (alg.GetString() != "ed25519")
            {
                continue; // unknown algorithms are not trusted, but do not hide a valid ed25519 signature next to them
            }
            var bytes = Convert.FromBase64String(sig.GetString()!);
            if (bytes.Length != Ed25519.SignatureSize)
            {
                throw new InvalidDataException("an Ed25519 signature is 64 bytes");
            }
            list.Add((id.GetString()!, bytes));
        }
        if (list.Count == 0)
        {
            throw new InvalidDataException("no ed25519 signature");
        }
        return list;
    }

    /// <summary>Plain Ed25519 (RFC 8032, no context) over <paramref name="message"/>.</summary>
    internal static bool Ed25519Verify(byte[] publicKey, byte[] message, byte[] signature)
    {
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
        verifier.BlockUpdate(message, 0, message.Length);
        return verifier.VerifySignature(signature);
    }

    private static class Ed25519
    {
        public const int SignatureSize = 64;
    }
}
