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

    // ------------------------------------------------------------ PR-09 second segment (ADR-0012)

    [Fact]
    public void HostKeyAlgorithms_SshNetOffersExactlyTheJschList()
    {
        var want = V.GetProperty("hostKeyAlgorithms").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Equal(12, want.Count);
        Assert.Equal(want, RemoteConnectRules.HostKeyAlgorithms);
        var info = new Renci.SshNet.ConnectionInfo("h", "u", new Renci.SshNet.NoneAuthenticationMethod("u"));
        Assert.Contains("ssh-rsa", info.HostKeyAlgorithms.Keys); // SSH.NET's default carries SHA-1 RSA; JSch's does not
        RemoteSshConnector.RestrictHostKeyAlgorithms(info);
        Assert.Equal(want, info.HostKeyAlgorithms.Keys.ToList()); // same entries, same order → same key presented
    }

    [Fact]
    public void ConnectOutcome_MatchesVectors()
    {
        foreach (var c in Cases("connectOutcome", 18))
        {
            var kind = Enum.Parse<SshFailureKind>(c.GetProperty("kind").GetString()!);
            var got = RemoteConnectRules.Classify(c.StringOrNull("pinned"), c.StringOrNull("seen"), kind, c.GetProperty("libraryMessage").GetString());
            Assert.True(c.GetProperty("code").GetString() == got.Code, Name(c));
            Assert.True(c.StringOrNull("message") == got.Message, Name(c));
        }
        foreach (var c in Cases("jschKind", 8))
        {
            Assert.Equal(c.GetProperty("kind").GetString(), RemoteConnectRules.KindFromJschMessage(c.StringOrNull("message")).ToString());
        }
    }

    [Fact]
    public void Probe_MatchesVectors()
    {
        foreach (var c in Cases("probe", 15))
        {
            int? exit = c.GetProperty("exit").ValueKind == JsonValueKind.Null ? null : c.GetProperty("exit").GetInt32();
            var got = RemoteConnectRules.Summarize(exit, RemoteConnectRules.CombineOutput(c.StringOrNull("stdout"), c.StringOrNull("stderr")));
            Assert.True(c.GetProperty("present").GetBoolean() == got.Present, Name(c));
            Assert.True(c.StringOrNull("version") == got.Version, $"{Name(c)}: {got.Version}");
        }
    }

    [Fact]
    public void Probe_DshDecidesTheConnectedCode()
    {
        Assert.Equal(new RemoteOutcome("OK", null), RemoteConnectRules.AfterProbe(new(true, "v1", true, "0.2")));
        Assert.Equal(new RemoteOutcome("DSH_MISSING", RemoteConnectRules.DshMissingMessage),
            RemoteConnectRules.AfterProbe(new(true, "v1", false, "'dsh' is not recognized")));
        Assert.Equal(("node --version", TimeSpan.FromSeconds(20)), RemoteConnectRules.NodeProbe);
        Assert.Equal(("dsh --version", TimeSpan.FromSeconds(60)), RemoteConnectRules.DshProbe);
    }

    [Fact]
    public void FailureKind_FromSshNetExceptions()
    {
        var none = CancellationToken.None;
        Assert.Equal(SshFailureKind.AuthRejected, RemoteSshConnector.Kind(new Renci.SshNet.Common.SshAuthenticationException("Permission denied (publickey)."), none));
        Assert.Equal(SshFailureKind.Timeout, RemoteSshConnector.Kind(new Renci.SshNet.Common.SshOperationTimeoutException("slow"), none));
        Assert.Equal(SshFailureKind.Timeout, RemoteSshConnector.Kind(new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.TimedOut), none));
        Assert.Equal(SshFailureKind.Other, RemoteSshConnector.Kind(new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused), none));
        Assert.Equal(SshFailureKind.Other, RemoteSshConnector.Kind(
            new Renci.SshNet.Common.SshConnectionException("Host key could not be verified.", Renci.SshNet.Messages.Transport.DisconnectReason.HostKeyNotVerifiable), none));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Null(RemoteSshConnector.Kind(new OperationCanceledException(cancelled.Token), cancelled.Token)); // caller's cancel is rethrown
        Assert.Null(RemoteSshConnector.Kind(new NullReferenceException(), none)); // bugs are not network outcomes
    }

    [Fact]
    public void KeystoreHostAlgorithm_SignsOnlyThroughTheSource_AndTheSignatureVerifies()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var source = new FakeKeySource(key, true);
        var blob = SshIdentity.PublicBlob(source.ReadPublicKey()!);
        var alg = new KeystoreHostAlgorithm(source, blob);
        Assert.Equal(SshWire.Algorithm, alg.Name);
        Assert.Equal(blob, alg.Data);
        var data = RandomNumberGenerator.GetBytes(200);
        var sig = alg.Sign(data);
        Assert.True(alg.VerifySignature(data, sig));
        Assert.True(SshWire.Verify(blob, data, sig));
        data[0] ^= 1;
        Assert.False(alg.VerifySignature(data, sig));
        Assert.Throws<CryptographicException>(() => new KeystoreHostAlgorithm(new FakeKeySource(key, true) { FailSign = true }, blob).Sign(data));
    }

    [Fact]
    public async Task Connector_WithoutKeystoreKey_ReportsIdentityMissing_AndNeverConnects()
    {
        var profile = new RemoteHostProfile("id", "pc", "192.0.2.1", 22, "u", 3091, null);
        var a = await new RemoteSshConnector(new FakeKeySource(null, null)).ConnectAsync(profile);
        Assert.Equal(("DISCONNECTED", RemoteConnectRules.IdentityMissing), (a.State, a.Code));
        Assert.Null(a.Session);
    }

    [Fact]
    public async Task Connector_ClosedPort_IsUnreachable()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop(); // nothing listens there any more
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var profile = new RemoteHostProfile("id", "pc", "127.0.0.1", port, "u", 3091, null);
        var a = await new RemoteSshConnector(new FakeKeySource(key, true)) { ConnectTimeout = TimeSpan.FromSeconds(5) }.ConnectAsync(profile);
        Assert.Equal(("DISCONNECTED", "UNREACHABLE"), (a.State, a.Code));
        Assert.Null(a.Fingerprint);
        Assert.Null(a.Session);
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
