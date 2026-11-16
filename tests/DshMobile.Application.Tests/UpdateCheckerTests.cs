using System.Text;
using DshMobile.Application.Updates;
using DshMobile.Runtime;
using DshMobile.Runtime.Updates;
using Microsoft.Extensions.Time.Testing;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace DshMobile.Application.Tests;

/// <summary>
/// vNext PR-02: the read-only update check end to end (installed versions → anti-replay floor → fetch → verify →
/// catalog → record acceptance), with a throwaway Ed25519 key. Replays are refused across restarts; every failure keeps
/// its stable code; nothing outside the host state directory is written.
/// </summary>
public sealed class UpdateCheckerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dshm-update-").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));
    private readonly Ed25519PrivateKeyParameters _key;

    public UpdateCheckerTests()
    {
        var g = new Ed25519KeyPairGenerator();
        g.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        _key = (Ed25519PrivateKeyParameters)g.GenerateKeyPair().Private;
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private RuntimeLayout Layout => new(Path.Combine(_dir, "files"), null, "/lib");

    private ReleaseTrust Trust => new([_key.GeneratePublicKey().GetEncoded()]);

    private sealed class FakeSource(Func<ReleaseFiles> files) : IReleaseSource
    {
        public int Fetches { get; private set; }

        public Task<ReleaseFiles> FetchAsync(CancellationToken ct)
        {
            Fetches++;
            return Task.FromResult(files());
        }
    }

    private static byte[] Manifest(string channel, string generatedAt, string dshVersion = "0.2.2") => Encoding.UTF8.GetBytes(
        $$"""
        {"schema":1,"channel":"{{channel}}","generatedAt":"{{generatedAt}}","expiresAt":"2026-12-16T00:00:00Z","artifacts":[
          {"kind":"dsh","version":"{{dshVersion}}","abi":"arm64","archive":"dsh-{{dshVersion}}.tar.zst",
           "url":"https://github.com/maiji1990/dsh_mobile/releases/download/dsh-{{dshVersion}}/dsh-{{dshVersion}}.tar.zst",
           "sha256":"{{new string('c', 64)}}","size":1048576,"requiresNode":"24.x",
           "sourceRepo":"deepseek-ai/deepseek-harness","sourceTag":"dsh-v{{dshVersion}}","sourceCommit":"{{new string('d', 40)}}"}]}
        """);

    private ReleaseFiles Signed(byte[] manifest)
    {
        var message = Encoding.ASCII.GetBytes(ReleaseManifestVerifier.Domain).Concat(manifest).ToArray();
        var s = new Ed25519Signer();
        s.Init(true, _key);
        s.BlockUpdate(message, 0, message.Length);
        var keyId = ReleaseTrust.KeyId(_key.GeneratePublicKey().GetEncoded());
        var envelope = $$"""{"schema":1,"signatures":[{"alg":"ed25519","keyId":"{{keyId}}","sig":"{{Convert.ToBase64String(s.GenerateSignature())}}"}]}""";
        return new ReleaseFiles(manifest, Encoding.UTF8.GetBytes(envelope));
    }

    private UpdateChecker Checker(Func<string, IReleaseSource> sources, ReleaseTrust? trust = null) =>
        new(Layout, trust ?? Trust, sources, new UpdateCheckOptions("0.1.0", "arm64"), _time);

    private void InstallNode24AndDsh(string dsh)
    {
        Directory.CreateDirectory(Layout.RuntimeCurrentDir);
        File.WriteAllText(Path.Combine(Layout.RuntimeCurrentDir, "runtime-profile.json"),
            """{"bundle":{"version":"0.2.0"},"layers":[{"name":"node","version":"24.19.0"}]}""");
        Directory.CreateDirectory(Path.Combine(Layout.LayerDir("dsh"), ".dshmobile"));
        File.WriteAllText(Path.Combine(Layout.LayerDir("dsh"), ".dshmobile", "version"), dsh);
    }

    [Fact]
    public async Task VerifiedManifest_IsEvaluatedAgainstWhatIsInstalled_AndAccepted()
    {
        InstallNode24AndDsh("0.2.1-rc.5");
        var source = new FakeSource(() => Signed(Manifest("stable", "2026-11-16T00:00:00Z")));
        var r = await Checker(_ => source).CheckAsync("stable", CancellationToken.None);
        Assert.True(r.Ok, $"{r.Code} {r.Message}");
        var dsh = r.Choices.Single(c => c.Kind == ReleaseKinds.Dsh);
        Assert.Equal(("0.2.1-rc.5", "0.2.2"), (dsh.Installed, dsh.Update?.Version));
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero), new ReleaseAcceptanceStore(Layout).Read("stable").GeneratedAt);
        var lines = UpdateChecker.Describe("stable", r).ToList();
        Assert.Contains(lines, l => l.Contains("dsh: installed 0.2.1-rc.5; 1 offer(s); update to 0.2.2", StringComparison.Ordinal));
        // Only the host state directory was written.
        Assert.Equal([Path.Combine(Layout.HostStateDir, ReleaseAcceptanceStore.FileName)],
            Directory.EnumerateFiles(Layout.FilesDir, "*", SearchOption.AllDirectories).Where(f => !f.StartsWith(Layout.RuntimeCurrentDir, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task OlderManifest_IsARollback_EvenAfterARestart()
    {
        var current = Manifest("stable", "2026-11-16T00:00:00Z", "0.2.2");
        var old = Manifest("stable", "2026-11-10T00:00:00Z", "0.2.1");
        var serve = current;
        var source = new FakeSource(() => Signed(serve));
        Assert.True((await Checker(_ => source).CheckAsync("stable", CancellationToken.None)).Ok);

        serve = old; // a validly signed but older manifest replayed by the host or a man in the middle
        var r = await Checker(_ => source).CheckAsync("stable", CancellationToken.None); // a new checker = an app restart
        Assert.Equal(UpdateErrorCodes.Rollback, r.Code);
        Assert.Empty(r.Choices);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero), new ReleaseAcceptanceStore(Layout).Read("stable").GeneratedAt);

        serve = current; // the same manifest again is fine
        Assert.True((await Checker(_ => source).CheckAsync("stable", CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task ChannelsHaveIndependentFloors_AndAPreviewManifestCannotPassAsStable()
    {
        var preview = new FakeSource(() => Signed(Manifest("preview", "2026-11-16T06:00:00Z")));
        var checker = Checker(channel => preview);
        Assert.True((await checker.CheckAsync("preview", CancellationToken.None)).Ok);
        Assert.Equal(UpdateErrorCodes.ChannelMismatch, (await checker.CheckAsync("stable", CancellationToken.None)).Code);
        Assert.Null(new ReleaseAcceptanceStore(Layout).Read("stable").GeneratedAt);
    }

    [Fact]
    public async Task NoTrustedKey_IsReported_WithoutGoingOnline()
    {
        var source = new FakeSource(() => throw new InvalidOperationException("must not fetch"));
        var r = await Checker(_ => source, ReleaseTrust.BuiltIn).CheckAsync("stable", CancellationToken.None);
        Assert.Equal(UpdateErrorCodes.NoTrustedKeys, r.Code);
        Assert.Equal(0, source.Fetches);
        Assert.Equal("update check (stable) FAILED UPDATE_NO_TRUSTED_KEYS: this build pins no release public key; online updates are unavailable",
            Assert.Single(UpdateChecker.Describe("stable", r)));
    }

    [Fact]
    public async Task UnreadableState_RefusesBeforeFetching_AndUnrecordableAcceptance_WithholdsTheOffers()
    {
        var source = new FakeSource(() => Signed(Manifest("stable", "2026-11-16T00:00:00Z")));
        Directory.CreateDirectory(Layout.HostStateDir);
        var state = Path.Combine(Layout.HostStateDir, ReleaseAcceptanceStore.FileName);
        File.WriteAllText(state, "garbage");
        var r = await Checker(_ => source).CheckAsync("stable", CancellationToken.None);
        Assert.Equal(UpdateErrorCodes.StateUnreadable, r.Code);
        Assert.Equal(0, source.Fetches);

        // A state path that cannot be written (a directory where the file should be).
        File.Delete(state);
        Directory.CreateDirectory(state);
        r = await Checker(_ => source).CheckAsync("stable", CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains(r.Code, (string[])[UpdateErrorCodes.StateWriteFailed, UpdateErrorCodes.StateUnreadable]);
        Assert.Empty(r.Choices);
    }

    [Fact]
    public async Task TamperedOrExpiredManifests_KeepTheirCodes_AndRecordNothing()
    {
        var good = Signed(Manifest("stable", "2026-11-16T00:00:00Z"));
        var tampered = new ReleaseFiles(Manifest("stable", "2026-11-16T00:00:00Z", "6.6.6"), good.Signature);
        Assert.Equal(UpdateErrorCodes.SignatureInvalid, (await Checker(_ => new FakeSource(() => tampered)).CheckAsync("stable", CancellationToken.None)).Code);

        _time.SetUtcNow(new DateTimeOffset(2026, 12, 17, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(UpdateErrorCodes.Expired, (await Checker(_ => new FakeSource(() => good)).CheckAsync("stable", CancellationToken.None)).Code);
        Assert.False(File.Exists(Path.Combine(Layout.HostStateDir, ReleaseAcceptanceStore.FileName)));
    }

    [Fact]
    public async Task UnknownChannel_IsABadRequest()
    {
        var r = await Checker(_ => throw new InvalidOperationException()).CheckAsync("latest", CancellationToken.None);
        Assert.Equal(RuntimeErrorCodes.BadRequest, r.Code);
    }
}
