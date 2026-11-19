using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DshMobile.Runtime.Updates;
using Microsoft.Extensions.Time.Testing;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// P0-03 / ADR-0022: signed release manifests and read-only discovery. The fixture in tests/TestVectors/release-manifest
/// was signed by tooling/release-manifest-sign.mjs (Node's Ed25519, an independent implementation) with a throwaway
/// key whose private half was never committed; its public key is pinned below.
/// </summary>
public sealed class ReleaseManifestTests
{
    private const string FixtureKey = "0/7Vy+xzt4e19iEbU63oDxGycevISE5500hV1Q8yu3o=";
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static byte[] Fixture(string name) => File.ReadAllBytes(Repo.PathOf("tests", "TestVectors", "release-manifest", name));

    private sealed class Signer
    {
        private readonly Ed25519PrivateKeyParameters _key;

        public Signer()
        {
            var g = new Ed25519KeyPairGenerator();
            g.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
            _key = (Ed25519PrivateKeyParameters)g.GenerateKeyPair().Private;
        }

        public byte[] PublicKey => _key.GeneratePublicKey().GetEncoded();

        public ReleaseTrust Trust => new([PublicKey]);

        public string KeyId => ReleaseTrust.KeyId(PublicKey);

        public byte[] Sign(byte[] manifest, string? domain = null)
        {
            var message = Encoding.ASCII.GetBytes(domain ?? ReleaseManifestVerifier.Domain).Concat(manifest).ToArray();
            var s = new Ed25519Signer();
            s.Init(true, _key);
            s.BlockUpdate(message, 0, message.Length);
            return s.GenerateSignature();
        }

        public byte[] Envelope(byte[] manifest, string? keyId = null) => EnvelopeOf((keyId ?? KeyId, Sign(manifest)));
    }

    private static byte[] EnvelopeOf(params (string KeyId, byte[] Sig)[] sigs) =>
        Encoding.UTF8.GetBytes(new JsonObject
        {
            ["schema"] = 1,
            ["signatures"] = new JsonArray(sigs.Select(s => (JsonNode)new JsonObject { ["alg"] = "ed25519", ["keyId"] = s.KeyId, ["sig"] = Convert.ToBase64String(s.Sig) }).ToArray()),
        }.ToJsonString());

    private static JsonObject Artifact(string kind = "runtime", string version = "1.0.0", string abi = "arm64") => new()
    {
        ["kind"] = kind,
        ["version"] = version,
        ["abi"] = abi,
        ["archive"] = $"{kind}-{version}.tar.zst",
        ["url"] = $"https://releases.example.invalid/{kind}-{version}.tar.zst",
        ["sha256"] = new string('a', 64),
        ["size"] = 1234,
    };

    private static JsonObject DshArtifact(string version = "0.2.2-rc.3")
    {
        var a = Artifact("dsh", version);
        a["sourceRepo"] = "deepseek-ai/deepseek-harness";
        a["sourceTag"] = "dsh-v" + version;
        a["sourceCommit"] = new string('b', 40);
        return a;
    }

    private static byte[] Manifest(Action<JsonObject>? edit = null, params JsonObject[] artifacts)
    {
        var m = new JsonObject
        {
            ["schema"] = 1,
            ["channel"] = "stable",
            ["generatedAt"] = "2026-11-16T00:00:00Z",
            ["expiresAt"] = "2026-12-16T00:00:00Z",
            ["artifacts"] = new JsonArray(artifacts.Length == 0 ? [Artifact()] : artifacts.Select(a => (JsonNode)a).ToArray()),
        };
        edit?.Invoke(m);
        return Encoding.UTF8.GetBytes(m.ToJsonString());
    }

    private static ReleaseVerification Verify(byte[] manifest, Signer signer, string channel = "stable", DateTimeOffset? floor = null) =>
        ReleaseManifestVerifier.Verify(manifest, signer.Envelope(manifest), signer.Trust, channel, Now, floor);

    // ------------------------------------------------------------------ the primitive and the independent fixture

    [Fact]
    public void Ed25519_Rfc8032Test1_Verifies_AndAFlippedBitDoesNot()
    {
        var pub = Convert.FromHexString("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
        var sig = Convert.FromHexString("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");
        Assert.True(ReleaseManifestVerifier.Ed25519Verify(pub, [], sig));
        sig[10] ^= 1;
        Assert.False(ReleaseManifestVerifier.Ed25519Verify(pub, [], sig));
    }

    [Fact]
    public void NodeSignedFixture_Verifies_AndParses()
    {
        var v = ReleaseManifestVerifier.Verify(Fixture("release-manifest.json"), Fixture("release-manifest.json.sig"), ReleaseTrust.FromBase64(FixtureKey), "preview", Now);
        Assert.True(v.Ok, $"{v.Code} {v.Message}");
        Assert.Equal("ed25519:c0c914c1caa1fa90", v.KeyId);
        var m = v.Manifest!;
        Assert.Equal((1, "preview"), (m.Schema, m.Channel));
        var dsh = Assert.Single(m.Artifacts, a => a.Kind == ReleaseKinds.Dsh);
        Assert.Equal(("0.2.2-rc.3", "dsh-v0.2.2-rc.3", "24.x"), (dsh.Version, dsh.SourceTag, dsh.RequiresNode));
        Assert.Equal(new Uri("https://releases.example.invalid/dsh/dsh-arm64-0.2.2-rc.3.tar.zst"), dsh.Url);
    }

    [Fact]
    public void NodeSignedFixture_OneChangedByte_IsRefused()
    {
        var manifest = Fixture("release-manifest.json");
        var i = Array.IndexOf(manifest, (byte)'3'); // the first '3': the dsh version rc.3 becomes rc.4
        manifest[i] = (byte)'4';
        var v = ReleaseManifestVerifier.Verify(manifest, Fixture("release-manifest.json.sig"), ReleaseTrust.FromBase64(FixtureKey), "preview", Now);
        Assert.Equal(UpdateErrorCodes.SignatureInvalid, v.Code);
        Assert.Null(v.Manifest);
    }

    // ------------------------------------------------------------------ trust

    [Fact]
    public void EmptyTrust_OffersNothing_EvenForAValidManifest()
    {
        // Independent of which keys this build pins: zero trusted keys is a refusal, not an unverified offer.
        var v = ReleaseManifestVerifier.Verify(Fixture("release-manifest.json"), Fixture("release-manifest.json.sig"), ReleaseTrust.FromBase64(), "preview", Now);
        Assert.Equal(UpdateErrorCodes.NoTrustedKeys, v.Code);
    }

    [Fact]
    public void UnknownKey_WrongDomain_ForgedKeyId_AndGarbageEnvelopes_AreRefused()
    {
        var signer = new Signer();
        var other = new Signer();
        var manifest = Manifest();
        Assert.True(Verify(manifest, signer).Ok);

        Assert.Equal(UpdateErrorCodes.KeyUnknown, ReleaseManifestVerifier.Verify(manifest, other.Envelope(manifest), signer.Trust, "stable", Now).Code);
        // Signed by another key but labelled with the trusted key's id: the id is derived from the key, so it fails.
        Assert.Equal(UpdateErrorCodes.SignatureInvalid, ReleaseManifestVerifier.Verify(manifest, other.Envelope(manifest, signer.KeyId), signer.Trust, "stable", Now).Code);
        // Without the domain prefix the same key's signature is worthless here.
        Assert.Equal(UpdateErrorCodes.SignatureInvalid,
            ReleaseManifestVerifier.Verify(manifest, EnvelopeOf((signer.KeyId, signer.Sign(manifest, domain: ""))), signer.Trust, "stable", Now).Code);
        foreach (var junk in (string[])["", "{}", "[]", "{\"schema\":1,\"signatures\":[]}", "{\"schema\":1,\"signatures\":[{\"alg\":\"ed25519\",\"keyId\":\"x\",\"sig\":\"AAAA\"}]}", "not json"])
        {
            Assert.Equal(UpdateErrorCodes.SignatureMissing, ReleaseManifestVerifier.Verify(manifest, Encoding.UTF8.GetBytes(junk), signer.Trust, "stable", Now).Code);
        }
    }

    [Fact]
    public void Rotation_AnyTrustedSignatureVerifies_UnknownAlgorithmsAreIgnored()
    {
        var old = new Signer();
        var next = new Signer();
        var manifest = Manifest();
        var envelope = Encoding.UTF8.GetBytes(
            $"{{\"schema\":1,\"signatures\":[{{\"alg\":\"rsa\",\"keyId\":\"x\",\"sig\":\"AA==\"}},{{\"alg\":\"ed25519\",\"keyId\":\"{old.KeyId}\",\"sig\":\"{Convert.ToBase64String(old.Sign(manifest))}\"}},{{\"alg\":\"ed25519\",\"keyId\":\"{next.KeyId}\",\"sig\":\"{Convert.ToBase64String(next.Sign(manifest))}\"}}]}}");
        Assert.Equal(next.KeyId, ReleaseManifestVerifier.Verify(manifest, envelope, next.Trust, "stable", Now).KeyId);
        Assert.Equal(old.KeyId, ReleaseManifestVerifier.Verify(manifest, envelope, old.Trust, "stable", Now).KeyId);
    }

    [Fact]
    public void Trust_RefusesKeysThatAreNotEd25519PublicKeys()
    {
        Assert.Throws<ArgumentException>(() => new ReleaseTrust([new byte[31]]));
        Assert.Equal("ed25519:c0c914c1caa1fa90", ReleaseTrust.KeyId(Convert.FromBase64String(FixtureKey)));
    }

    // ------------------------------------------------------------------ validity window / channel / anti-rollback

    [Fact]
    public void Channel_Window_AndRollback_AreEnforcedAfterTheSignature()
    {
        var s = new Signer();
        Assert.Equal(UpdateErrorCodes.ChannelMismatch, Verify(Manifest(), s, channel: "preview").Code);
        Assert.Equal(UpdateErrorCodes.Expired, Verify(Manifest(m => m["expiresAt"] = "2026-11-16T11:59:59Z"), s).Code);
        Assert.Equal(UpdateErrorCodes.NotYetValid, Verify(Manifest(m => m["generatedAt"] = "2026-11-16T12:30:00Z"), s).Code);
        Assert.True(Verify(Manifest(m => m["generatedAt"] = "2026-11-16T12:05:00Z"), s).Ok); // within clock skew
        Assert.Equal(UpdateErrorCodes.Rollback, Verify(Manifest(), s, floor: new DateTimeOffset(2026, 11, 16, 0, 0, 1, TimeSpan.Zero)).Code);
        Assert.True(Verify(Manifest(), s, floor: new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero)).Ok); // same manifest again
    }

    // ------------------------------------------------------------------ strict content (all of these are validly signed)

    public static TheoryData<string, string> Malformed() => new()
    {
        { "schema 2", """{"schema":2,"channel":"stable","generatedAt":"2026-11-16T00:00:00Z","expiresAt":"2026-12-16T00:00:00Z","artifacts":[]}""" },
        { "channel", """{"schema":1,"channel":"beta","generatedAt":"2026-11-16T00:00:00Z","expiresAt":"2026-12-16T00:00:00Z","artifacts":[]}""" },
        { "local time", """{"schema":1,"channel":"stable","generatedAt":"2026-11-16T00:00:00","expiresAt":"2026-12-16T00:00:00Z","artifacts":[]}""" },
        { "window", """{"schema":1,"channel":"stable","generatedAt":"2026-11-16T00:00:00Z","expiresAt":"2026-11-16T00:00:00Z","artifacts":[]}""" },
        { "duplicate key", """{"schema":1,"channel":"stable","channel":"preview","generatedAt":"2026-11-16T00:00:00Z","expiresAt":"2026-12-16T00:00:00Z","artifacts":[]}""" },
        { "trailing comma", """{"schema":1,"channel":"stable","generatedAt":"2026-11-16T00:00:00Z","expiresAt":"2026-12-16T00:00:00Z","artifacts":[],}""" },
        { "not an object", "[]" },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void MalformedTopLevel_IsSchemaInvalid(string label, string json)
    {
        var s = new Signer();
        var v = Verify(Encoding.UTF8.GetBytes(json), s);
        Assert.True(v.Code == UpdateErrorCodes.SchemaInvalid, $"{label}: {v.Code} {v.Message}");
    }

    public static TheoryData<string, string, string?> BadArtifacts() => new()
    {
        { "kind", "kind", "plugin" },
        { "version", "version", "../1" },
        { "abi", "abi", "ARM 64" },
        { "archive path", "archive", "../dsh.tar.zst" },
        { "archive dotfile", "archive", ".hidden.tar.zst" },
        { "archive type", "archive", "dsh.sh" },
        { "http url", "url", "http://releases.example.invalid/x.tar.zst" },
        { "credentials in url", "url", "https://user:token@releases.example.invalid/x.tar.zst" },
        { "fragment", "url", "https://releases.example.invalid/x.tar.zst#y" },
        { "relative url", "url", "/x.tar.zst" },
        { "uppercase digest", "sha256", new string('A', 64) },
        { "short digest", "sha256", new string('a', 63) },
        { "missing digest", "sha256", null },
        { "min app", "minAppVersion", "latest" },
    };

    [Theory]
    [MemberData(nameof(BadArtifacts))]
    public void MalformedArtifact_IsSchemaInvalid(string label, string field, string? value)
    {
        var a = Artifact();
        if (value is null)
        {
            a.Remove(field);
        }
        else
        {
            a[field] = value;
        }
        var v = Verify(Manifest(null, a), new Signer());
        Assert.True(v.Code == UpdateErrorCodes.SchemaInvalid, $"{label}: {v.Code} {v.Message}");
    }

    [Fact]
    public void Size_Duplicates_AndUnpinnedDshSources_AreSchemaInvalid()
    {
        var s = new Signer();
        foreach (var size in (JsonNode?[])[0, -1, ReleaseManifestParser.MaxArtifactBytes + 1, "1", null])
        {
            var a = Artifact();
            a["size"] = size?.DeepClone();
            Assert.Equal(UpdateErrorCodes.SchemaInvalid, Verify(Manifest(null, a), s).Code);
        }
        Assert.Equal(UpdateErrorCodes.SchemaInvalid, Verify(Manifest(null, Artifact(), Artifact()), s).Code);
        Assert.True(Verify(Manifest(null, Artifact(), Artifact(abi: "x86_64")), s).Ok); // same version, other ABI: fine
        foreach (var field in (string[])["sourceRepo", "sourceTag", "sourceCommit"])
        {
            var d = DshArtifact();
            d.Remove(field);
            Assert.Equal(UpdateErrorCodes.SchemaInvalid, Verify(Manifest(null, d), s).Code);
        }
        var shortCommit = DshArtifact();
        shortCommit["sourceCommit"] = "abc1234";
        Assert.Equal(UpdateErrorCodes.SchemaInvalid, Verify(Manifest(null, shortCommit), s).Code);
        Assert.True(Verify(Manifest(null, DshArtifact()), s).Ok);
    }

    [Fact]
    public void UnknownProperties_AreIgnored()
    {
        var a = Artifact();
        a["notes"] = "covered by the signature";
        Assert.True(Verify(Manifest(m => m["publisher"] = "ci", a), new Signer()).Ok);
    }

    // ------------------------------------------------------------------ read-only catalog

    private static ReleaseManifest Parsed(params JsonObject[] artifacts) => ReleaseManifestParser.Parse(Manifest(null, artifacts));

    [Fact]
    public void Catalog_PicksTheNewestApplicableOfferPerKind_ForThisAbi()
    {
        var newer = DshArtifact("0.2.2-rc.3");
        var stableDsh = DshArtifact("0.2.1");
        var x86 = Artifact("runtime", "9.0.0", abi: "x86_64");
        var rows = ReleaseCatalog.Evaluate(Parsed(stableDsh, newer, Artifact("runtime", "2026.11.16"), x86), new InstalledVersions("0.1.0", "2026.11.10", "24.11.0", "0.2.1-rc.5"), "arm64");
        Assert.Equal(ReleaseKinds.All, rows.Select(r => r.Kind));
        var dsh = rows.Single(r => r.Kind == ReleaseKinds.Dsh);
        Assert.Equal(["0.2.2-rc.3", "0.2.1"], dsh.Offers.Select(o => o.Artifact.Version)); // newest first (Versions.Compare)
        Assert.Equal("0.2.2-rc.3", dsh.Update?.Version);
        var runtime = rows.Single(r => r.Kind == ReleaseKinds.Runtime);
        Assert.Equal("2026.11.16", runtime.Update?.Version);
        Assert.DoesNotContain(runtime.Offers, o => o.Artifact.Abi != "arm64");
        Assert.Null(rows.Single(r => r.Kind == ReleaseKinds.Node).Update);
        Assert.Empty(rows.Single(r => r.Kind == ReleaseKinds.App).Offers);
    }

    [Fact]
    public void Catalog_InstalledSameOrNewer_IsUpToDate_AndNotInstalledTakesTheNewest()
    {
        var rows = ReleaseCatalog.Evaluate(Parsed(DshArtifact("0.2.1")), new InstalledVersions("0.1.0", null, null, "0.2.1"), "arm64");
        Assert.Null(rows.Single(r => r.Kind == ReleaseKinds.Dsh).Update);
        rows = ReleaseCatalog.Evaluate(Parsed(DshArtifact("0.2.1")), new InstalledVersions("0.1.0", null, null, null), "arm64");
        Assert.Equal("0.2.1", rows.Single(r => r.Kind == ReleaseKinds.Dsh).Update?.Version);
    }

    [Fact]
    public void Catalog_ConstraintsBlockAnOffer_AndFallBackToAnOlderApplicableOne()
    {
        var needsApp = DshArtifact("0.3.0");
        needsApp["minAppVersion"] = "0.2.0";
        var needsNode24 = DshArtifact("0.2.2");
        needsNode24["requiresNode"] = "24.x";
        var plain = DshArtifact("0.2.1");
        var rows = ReleaseCatalog.Evaluate(Parsed(needsApp, needsNode24, plain), new InstalledVersions("0.1.5", null, "22.11.0", "0.2.0"), "arm64");
        var dsh = rows.Single(r => r.Kind == ReleaseKinds.Dsh);
        Assert.Equal("needs app 0.2.0 or newer (installed 0.1.5)", dsh.Offers[0].BlockedBy);
        Assert.Equal("needs Node 24.x (installed 22.11.0)", dsh.Offers[1].BlockedBy);
        Assert.Equal("0.2.1", dsh.Update?.Version);
    }

    [Fact]
    public void Catalog_UnblocksDshOffer_WhenPendingRuntimeUpdateProvidesRequiredNode_ADR0029()
    {
        var rt26 = Artifact("runtime", "0.2.1");
        rt26["providesNode"] = "26.13.1";
        var dsh26 = DshArtifact("0.2.2-rc.4");
        dsh26["requiresNode"] = "26.x";
        var manifest = Parsed(rt26, dsh26);
        Assert.Equal("26.13.1", manifest.Artifacts.Single(a => a.Kind == ReleaseKinds.Runtime).ProvidesNode);

        // 1. 已安装 Node 24 / Runtime 0.2.0：由于存在带 providesNode=26.13.1 的 Runtime 0.2.1 更新，DSH 0.2.2-rc.4 可联合升级。
        var rows = ReleaseCatalog.Evaluate(manifest, new InstalledVersions("0.1.0", "0.2.0", "24.19.0", "0.2.1-rc.5"), "arm64");
        Assert.Equal("0.2.1", rows.Single(r => r.Kind == ReleaseKinds.Runtime).Update?.Version);
        Assert.Equal("0.2.2-rc.4", rows.Single(r => r.Kind == ReleaseKinds.Dsh).Update?.Version);

        // 2. 全新安装（Runtime 与 Node 均为 null）：Runtime 0.2.1 与 DSH 0.2.2-rc.4 可一次性联合在线初始化。
        var fresh = ReleaseCatalog.Evaluate(manifest, new InstalledVersions("0.1.0", null, null, null), "arm64");
        var plan = UpdatePlanner.Plan(fresh);
        Assert.Equal(("0.2.1", "0.2.2-rc.4"), (plan.Runtime?.Version, plan.Dsh?.Version));

        // 3. providesNode 只能出现在 runtime 制品上。
        var badDsh = DshArtifact("0.2.2-rc.4");
        badDsh["providesNode"] = "26.13.1";
        Assert.Equal(UpdateErrorCodes.SchemaInvalid, Verify(Manifest(null, badDsh), new Signer()).Code);
    }

    [Theory]
    [InlineData("24.x", "24.11.0", true)]
    [InlineData("24.x", "v24.1.0", true)]
    [InlineData("24.x", "24", true)]
    [InlineData("24.x", "240.1.0", false)]
    [InlineData("24.x", "22.11.0", false)]
    [InlineData("24.11.0", "24.11.0", true)]
    [InlineData("24.11.0", "24.11.1", false)]
    [InlineData("24.x", null, false)]
    public void NodeRequirement(string requirement, string? installed, bool ok) => Assert.Equal(ok, ReleaseCatalog.NodeMatches(requirement, installed));

    // ------------------------------------------------------------------ source + discovery

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var r = respond(request);
            r.RequestMessage ??= request;
            return Task.FromResult(r);
        }
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static readonly Uri ManifestUrl = new("https://releases.example.invalid/preview/release-manifest.json");

    [Fact]
    public async Task Discovery_FetchesAnonymously_VerifiesAndEvaluates()
    {
        var handler = new FakeHandler(r => Ok(r.RequestUri!.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal) ? Fixture("release-manifest.json.sig") : Fixture("release-manifest.json")));
        using var http = new HttpClient(handler);
        var r = await ReleaseDiscovery.CheckAsync(new HttpReleaseSource(http, ManifestUrl), ReleaseTrust.FromBase64(FixtureKey), "preview",
            new InstalledVersions("0.1.0", null, "24.11.0", "0.2.1-rc.5"), "arm64", null, new FakeTimeProvider(Now), CancellationToken.None);
        Assert.True(r.Ok, $"{r.Code} {r.Message}");
        Assert.Equal("0.2.2-rc.3", r.Choices.Single(c => c.Kind == ReleaseKinds.Dsh).Update?.Version);
        Assert.Equal([ManifestUrl.AbsoluteUri, ManifestUrl.AbsoluteUri + ".sig"], handler.Requests.Select(q => q.RequestUri!.AbsoluteUri));
        Assert.All(handler.Requests, q => Assert.Null(q.Headers.Authorization));
    }

    [Fact]
    public async Task Discovery_WithoutTrustedKeys_DoesNotEvenFetch()
    {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("must not fetch"));
        using var http = new HttpClient(handler);
        var r = await ReleaseDiscovery.CheckAsync(new HttpReleaseSource(http, ManifestUrl), ReleaseTrust.FromBase64(), "stable",
            new InstalledVersions("0.1.0", null, null, null), "arm64", null, TimeProvider.System, CancellationToken.None);
        Assert.Equal(UpdateErrorCodes.NoTrustedKeys, r.Code);
        Assert.Empty(handler.Requests);
        Assert.Empty(r.Choices);
    }

    [Fact]
    public async Task Source_HttpErrors_OversizeBodies_AndInsecureUrls_AreStableFailures()
    {
        using var notFound = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var e = await Assert.ThrowsAsync<ReleaseSourceException>(() => new HttpReleaseSource(notFound, ManifestUrl).FetchAsync(CancellationToken.None));
        Assert.Equal(UpdateErrorCodes.FetchFailed, e.Code);

        using var huge = new HttpClient(new FakeHandler(_ => Ok(new byte[HttpReleaseSource.MaxBytes + 1])));
        e = await Assert.ThrowsAsync<ReleaseSourceException>(() => new HttpReleaseSource(huge, ManifestUrl).FetchAsync(CancellationToken.None));
        Assert.Equal(UpdateErrorCodes.TooLarge, e.Code);

        using var downgraded = new HttpClient(new FakeHandler(r => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1]),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://releases.example.invalid/release-manifest.json"),
        }));
        e = await Assert.ThrowsAsync<ReleaseSourceException>(() => new HttpReleaseSource(downgraded, ManifestUrl).FetchAsync(CancellationToken.None));
        Assert.Equal(UpdateErrorCodes.FetchFailed, e.Code);

        using var any = new HttpClient();
        Assert.Throws<ArgumentException>(() => new HttpReleaseSource(any, new Uri("http://releases.example.invalid/m.json")));
        Assert.Throws<ArgumentException>(() => new HttpReleaseSource(any, new Uri("https://u:p@releases.example.invalid/m.json")));
    }
}
