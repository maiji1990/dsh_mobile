using System.Security.Cryptography;
using System.Text.Json;
using DshMobile.Runtime.Remote;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// PR-09 first segment: the C# remote-host port against tests/TestVectors/remote-ssh.json — the same cases the legacy
/// Kotlin RemoteHostProfile / PinnedHostKeys / SshWire / RemoteHosts companion run in native-bridge
/// (LegacyRemoteSshParityTest). csharpOnly cases follow Android libcore org.json (the device). The vectors come from
/// tooling/gen-remote-ssh-vectors.py, an independent model of the legacy rules.
/// </summary>
public sealed class RemoteSshParityTests
{
    private static readonly JsonElement V = Repo.Vectors("remote-ssh.json");

    private static IEnumerable<JsonElement> Cases(string section, int atLeast)
    {
        var cases = V.GetProperty(section).EnumerateArray().ToList();
        Assert.True(cases.Count >= atLeast, $"{section}: {cases.Count} cases");
        return cases;
    }

    private static string Name(JsonElement c) => c.TryGetProperty("name", out var n) ? n.GetString()! : c.GetRawText();

    private static bool IsError(JsonElement c) => c.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.True;

    [Fact]
    public void PublicBlob_LineAndFingerprint_MatchVectors()
    {
        foreach (var c in Cases("publicBlob", 5))
        {
            var x = Convert.FromHexString(c.GetProperty("x").GetString()!);
            var y = Convert.FromHexString(c.GetProperty("y").GetString()!);
            if (IsError(c))
            {
                Assert.Throws<FormatException>(() => SshWire.EcdsaPublicBlob(x, y));
                continue;
            }
            var blob = SshWire.EcdsaPublicBlob(x, y);
            Assert.True(Convert.ToBase64String(blob) == c.GetProperty("blob").GetString(), Name(c));
            Assert.Equal(c.GetProperty("fingerprint").GetString(), SshWire.Fingerprint(blob));
            Assert.Equal(c.GetProperty("line").GetString(), SshWire.OpenSshLine(blob, SshIdentity.Comment));
        }
    }

    [Fact]
    public void Fingerprint_MatchesOpenSsh()
    {
        foreach (var c in Cases("fingerprint", 3))
        {
            Assert.Equal(c.GetProperty("fingerprint").GetString(), SshWire.Fingerprint(Convert.FromBase64String(c.GetProperty("blob").GetString()!)));
        }
    }

    [Fact]
    public void DerToSsh_MatchesVectors()
    {
        foreach (var c in Cases("derToSsh", 15))
        {
            var der = Convert.FromHexString(c.GetProperty("der").GetString()!);
            if (IsError(c))
            {
                Assert.Throws<FormatException>(() => SshWire.DerToSshSignature(der));
                continue;
            }
            Assert.True(Convert.ToHexStringLower(SshWire.DerToSshSignature(der)) == c.GetProperty("ssh").GetString(), Name(c));
        }
    }

    [Fact]
    public void DerToSsh_RealSignaturesStillVerify()
    {
        // As the legacy SshWireTest: 20 fresh keys, DER from the platform, SSH form verified again.
        for (var i = 0; i < 20; i++)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var p = key.ExportParameters(false);
            var blob = SshWire.EcdsaPublicBlob(p.Q.X, p.Q.Y);
            Assert.Equal(4 + 19 + 4 + 8 + 4 + 65, blob.Length);
            var data = System.Text.Encoding.ASCII.GetBytes($"session-id-{i}");
            var der = key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            var ssh = SshWire.DerToSshSignature(der);
            Assert.True(SshWire.Verify(blob, data, ssh));
            Assert.False(SshWire.Verify(blob, [.. data, 0], ssh));
        }
    }

    [Fact]
    public void Probe_SignsThroughTheSourceAndVerifies()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var facts = SshIdentity.Probe(new FakeKeySource(key, hardware: true));
        var p = key.ExportParameters(false);
        var blob = SshWire.EcdsaPublicBlob(p.Q.X, p.Q.Y);
        Assert.True(facts.KeyPresent);
        Assert.Equal(SshWire.OpenSshLine(blob, "dsh-mobile"), facts.PublicKeyLine);
        Assert.Equal(SshWire.Fingerprint(blob), facts.Fingerprint);
        Assert.True(facts.HardwareBacked);
        Assert.True(facts.SelfTestVerified);
        Assert.Null(facts.Error);
    }

    [Fact]
    public void Probe_ReportsMissingKeyWrongSignatureAndFailures()
    {
        Assert.False(SshIdentity.Probe(new FakeKeySource(null, hardware: null)).KeyPresent);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var wrong = SshIdentity.Probe(new FakeKeySource(key, hardware: false) { Signer = other });
        Assert.False(wrong.SelfTestVerified);
        var failing = SshIdentity.Probe(new FakeKeySource(key, hardware: false) { FailSign = true });
        Assert.False(failing.SelfTestVerified);
        Assert.Contains("sign:", failing.Error, StringComparison.Ordinal);
        var unreadable = SshIdentity.Probe(new FakeKeySource(key, hardware: false) { FailRead = true });
        Assert.False(unreadable.KeyPresent);
        Assert.NotNull(unreadable.Error);
    }

    [Fact]
    public void SelfTestChallenge_CannotBeReadAsSshUserAuthData()
    {
        // SSH user-auth signed data starts with string(session id): a 4-byte length. The label's first 4 bytes as a
        // length are far larger than any message, so a self-test signature is useless to an SSH server.
        var len = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(SshIdentity.SelfTestLabel);
        Assert.True(len > 1_000_000_000u);
    }

    [Fact]
    public void ProfileRequest_MatchesVectors()
    {
        foreach (var c in Cases("profileRequest", 50))
        {
            var existing = c.GetProperty("existing").ValueKind == JsonValueKind.Null ? null : ToProfile(c.GetProperty("existing"));
            var request = c.GetProperty("request");
            if (c.TryGetProperty("error", out var err))
            {
                var ex = Assert.Throws<RemoteProfileException>(() => RemoteHostProfile.FromRequest(request, existing, () => "<new>"));
                Assert.True(err.GetString() == ex.Message, $"{Name(c)}: {ex.Message}");
                continue;
            }
            var got = RemoteHostProfile.FromRequest(request, existing, () => "<new>");
            Assert.True(ToProfile(c.GetProperty("profile")) == got, $"{Name(c)}: {got}");
        }
    }

    [Fact]
    public void RequestedId_MatchesVectors()
    {
        foreach (var c in Cases("requestedId", 8))
        {
            Assert.Equal(c.StringOrNull("id"), RemoteHostProfile.RequestedId(c.GetProperty("request")));
        }
    }

    [Fact]
    public void StoredList_MatchesVectors()
    {
        foreach (var c in Cases("stored", 10))
        {
            var expected = c.GetProperty("profiles").EnumerateArray().Select(ToProfile).ToList();
            var got = RemoteHostProfile.ParseStored(c.StringOrNull("stored"));
            Assert.True(expected.SequenceEqual(got), $"{Name(c)}: {string.Join("; ", got)}");
        }
    }

    [Fact]
    public void HostKey_MatchesVectors()
    {
        foreach (var c in Cases("hostKey", 8))
        {
            var key = c.StringOrNull("key") is { } k ? Convert.FromBase64String(k) : null;
            var got = HostKeyPin.Check(c.StringOrNull("pinned"), key);
            Assert.True(Enum.Parse<HostKeyDecision>(c.GetProperty("decision").GetString()!) == got.Decision, Name(c));
            Assert.Equal(c.StringOrNull("seenFingerprint"), got.SeenFingerprint);
            Assert.Equal(c.StringOrNull("seenType"), got.SeenType);
        }
    }

    [Fact]
    public void Trust_MatchesVectors()
    {
        foreach (var c in Cases("trust", 5))
        {
            var last = c.GetProperty("lastUnknown");
            (string, string)? seen = last.ValueKind == JsonValueKind.Null ? null : (last[0].GetString()!, last[1].GetString()!);
            Assert.True(c.GetProperty("allowed").GetBoolean() == HostKeyPin.TrustAllowed(c.GetProperty("hostId").GetString()!,
                c.GetProperty("fingerprint").GetString()!, seen), Name(c));
        }
    }

    [Fact]
    public void LaunchTokenAndRedaction_MatchVectors()
    {
        foreach (var c in Cases("launchToken", 10))
        {
            Assert.Equal(c.StringOrNull("token"), RemoteLaunchToken.TokenFrom(c.GetProperty("line").GetString()!));
        }
        foreach (var c in Cases("redact", 6))
        {
            Assert.Equal(c.GetProperty("out").GetString(), RemoteLaunchToken.Redact(c.GetProperty("in").GetString()!));
        }
    }

    [Fact]
    public void KotlinWhitespace_DiffersFromDotNetWhereTheVectorsSayItDoes()
    {
        Assert.True(KotlinText.IsWhitespace('\u001F'));
        Assert.False(char.IsWhiteSpace('\u001F'));
        Assert.False(KotlinText.IsWhitespace('\u0085'));
        Assert.True(char.IsWhiteSpace('\u0085'));
        Assert.True(KotlinText.IsWhitespace(' '));
        Assert.True(KotlinText.IsWhitespace('　'));
    }

    private static RemoteHostProfile ToProfile(JsonElement o) => new(
        o.GetProperty("id").GetString()!, o.GetProperty("label").GetString()!, o.GetProperty("host").GetString()!,
        o.GetProperty("port").GetInt32(), o.GetProperty("user").GetString()!, o.GetProperty("dshPort").GetInt32(),
        o.StringOrNull("pinnedFingerprint"));

    private sealed class FakeKeySource(ECDsa? key, bool? hardware) : ISshKeySource
    {
        public ECDsa? Signer { get; init; }
        public bool FailSign { get; init; }
        public bool FailRead { get; init; }

        public SshPublicKey? ReadPublicKey()
        {
            if (FailRead)
            {
                throw new InvalidOperationException("keystore locked");
            }
            if (key is null)
            {
                return null;
            }
            var p = key.ExportParameters(false);
            // Java BigInteger.toByteArray() style: a leading sign byte when the high bit is set.
            static byte[] Java(byte[] v) => (v[0] & 0x80) != 0 ? [0, .. v] : v;
            return new SshPublicKey(Java(p.Q.X!), Java(p.Q.Y!), hardware);
        }

        public byte[] SignDer(byte[] data) => FailSign
            ? throw new CryptographicException("user not authenticated")
            : (Signer ?? key!).SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }
}
