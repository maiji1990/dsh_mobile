using System.Net;
using System.Security.Cryptography;
using System.Text;
using DshMobile.Application.Runtime;
using DshMobile.Application.Shadow;
using DshMobile.Application.Updates;
using DshMobile.Runtime;
using DshMobile.Runtime.Hosting;
using DshMobile.Runtime.Journal;
using DshMobile.Runtime.Layers;
using DshMobile.Runtime.Updates;
using Microsoft.Extensions.Time.Testing;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace DshMobile.Application.Tests;

/// <summary>
/// vNext PR-05: the online DSH layer update end to end — a really signed manifest, the real downloader, the real DSH
/// layer installer on a temp app directory, the coordinator with a fake host — including an unhealthy new layer, a running
/// DSH without / with consent, a process killed during the health check, a failed download and a tampered file.
/// </summary>
public sealed class UpdateCoordinatorTests : IDisposable
{
    private sealed class Foreground : IForegroundServiceController
    {
        public void Acquire() { }

        public void Release() { }
    }

    private sealed class ListLog : IDiagnosticsLog, IShadowReportSink
    {
        public List<string> Lines { get; } = [];

        public void Note(string line) => Lines.Add(line);

        public void Report(ShadowReport report) { }
    }

    private sealed class Server(byte[] body) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body), RequestMessage = request });
        }
    }

    private sealed class Source(ReleaseFiles files) : IReleaseSource
    {
        public Task<ReleaseFiles> FetchAsync(CancellationToken ct) => Task.FromResult(files);
    }

    private const string Target = "1.3.0";
    private readonly string _dir = Directory.CreateTempSubdirectory("dshm-upd-").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));
    private readonly List<string> _calls = [];
    private readonly ListLog _log = new();
    private readonly InMemoryRuntimeJournal _journal = new();
    private readonly Ed25519PrivateKeyParameters _key;
    private readonly byte[] _layer = File.ReadAllBytes(TestRuntime.RepoPath("tests", "TestVectors", "bundles", "dsh-good.tar"));
    private FakeDshHost _host;

    public UpdateCoordinatorTests()
    {
        var g = new Ed25519KeyPairGenerator();
        g.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        _key = (Ed25519PrivateKeyParameters)g.GenerateKeyPair().Private;
        _host = new FakeDshHost(_calls);
        TestRuntime.Install(Layout.FilesDir);
        InstallOldDsh();
        Directory.CreateDirectory(Path.Combine(Layout.UserDataDir, ".dsh"));
        File.WriteAllText(Path.Combine(Layout.UserDataDir, ".dsh", "credentials.yaml"), "secret");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private RuntimeLayout Layout => new(Path.Combine(_dir, "files"), null, Path.Combine(_dir, "nolib"));

    private DshLayerInstaller Dsh => new(Layout, LayerExtractor.AndroidLikeHost);

    private void InstallOldDsh()
    {
        var dsh = Layout.LayerDir("dsh");
        Directory.CreateDirectory(Path.Combine(dsh, "node_modules", "@deepseek-ai", "dsh", "lib"));
        File.WriteAllText(Path.Combine(dsh, DshLayerInstaller.BinJs), "old-bin");
        Directory.CreateDirectory(Path.Combine(dsh, ".dshmobile"));
        File.WriteAllText(Path.Combine(dsh, DshLayerInstaller.VersionFile), "1.0.0");
        File.WriteAllText(Path.Combine(dsh, "old.txt"), "old");
    }

    private RuntimeCoordinator Coordinator() => new(Layout, new RuntimeStateMachine(_journal, RuntimeCoordinator.Implementation), new Foreground(), _log, _log,
        new RuntimeOptions { HostPathExists = LayerExtractor.AndroidLikeHost }, hostFactory: (_, _, _) => _host);

    /// <summary>A verified discovery offering DSH <see cref="Target"/> (signed with a throwaway key, verified for real).</summary>
    private async Task<ReleaseDiscoveryResult> Discovery(byte[]? servedSha = null)
    {
        var sha = Convert.ToHexStringLower(servedSha ?? SHA256.HashData(_layer));
        var manifest = Encoding.UTF8.GetBytes($$"""
            {"schema":1,"channel":"preview","generatedAt":"2026-11-16T00:00:00Z","expiresAt":"2026-12-16T00:00:00Z","artifacts":[
              {"kind":"dsh","version":"{{Target}}","abi":"arm64","archive":"dsh-{{Target}}.tar.zst",
               "url":"https://github.com/maiji1990/dsh_mobile/releases/download/dsh-{{Target}}/dsh-{{Target}}.tar.zst",
               "sha256":"{{sha}}","size":{{_layer.Length}},"sourceRepo":"deepseek-ai/deepseek-harness","sourceTag":"dsh-v{{Target}}",
               "sourceCommit":"{{new string('b', 40)}}"}]}
            """);
        var message = Encoding.ASCII.GetBytes(ReleaseManifestVerifier.Domain).Concat(manifest).ToArray();
        var signer = new Ed25519Signer();
        signer.Init(true, _key);
        signer.BlockUpdate(message, 0, message.Length);
        var pub = _key.GeneratePublicKey().GetEncoded();
        var envelope = Encoding.UTF8.GetBytes($$"""{"schema":1,"signatures":[{"alg":"ed25519","keyId":"{{ReleaseTrust.KeyId(pub)}}","sig":"{{Convert.ToBase64String(signer.GenerateSignature())}}"}]}""");
        var r = await ReleaseDiscovery.CheckAsync(new Source(new ReleaseFiles(manifest, envelope)), new ReleaseTrust([pub]), "preview",
            InstalledVersionsReader.Read(Layout, "0.1.0") with { Node = "24.19.0" }, "arm64", null, _time, CancellationToken.None);
        Assert.True(r.Ok, $"{r.Code} {r.Message}");
        return r;
    }

    private (UpdateCoordinator U, Server S, HttpClient H, ArtifactDownloader D) Updates(RuntimeCoordinator runtime, byte[]? body = null)
    {
        var server = new Server(body ?? _layer);
        var http = new HttpClient(server);
        var downloader = new ArtifactDownloader(http, new ArtifactStore(Layout), _ => long.MaxValue);
        return (new UpdateCoordinator(runtime, downloader, new UpdateStateStore(Layout), _time), server, http, downloader);
    }

    private void AssertUserDataUntouched() =>
        Assert.Equal("secret", File.ReadAllText(Path.Combine(Layout.UserDataDir, ".dsh", "credentials.yaml")));

    [Fact]
    public async Task HealthyUpdate_IsDownloaded_SwappedHeld_Probed_AndCommitted()
    {
        using var runtime = Coordinator();
        var (u, _, http, d) = Updates(runtime);
        using (http)
        using (d)
        using (u)
        {
            var phases = new List<string>();
            var r = await u.InstallDshAsync(await Discovery(), "preview", stopConfirmed: false, new SyncProgress(p => phases.Add(p.Phase)), CancellationToken.None);
            Assert.True(r.Ok, $"{r.Code} {r.Message}");
            Assert.Equal((UpdatePhases.Committed, Target), (r.Phase, r.InstalledVersion));
            Assert.Equal([UpdatePhases.Downloading, UpdatePhases.Verified, UpdatePhases.Swapping, UpdatePhases.Probing, UpdatePhases.Committed], phases.Distinct());
            Assert.Equal(["host.start", "host.stop"], _calls); // the probe; DSH was not running before, so no restart
            Assert.Equal(Target, Dsh.InstalledVersion());
            Assert.False(Dsh.IsHeldForProbe);
            Assert.False(Directory.Exists(Dsh.AsideDir));
            Assert.Equal(UpdatePhases.Committed, u.LastState!.Phase);
            AssertUserDataUntouched();
        }
    }

    [Fact]
    public async Task UnhealthyNewLayer_IsRolledBack_ToTheOldOne()
    {
        using var runtime = Coordinator();
        _host.OnStart = () => new DshHostResult(false, "DSH_START_TIMEOUT", "no READY within 60 s");
        var (u, _, http, d) = Updates(runtime);
        using (http)
        using (d)
        using (u)
        {
            var r = await u.InstallDshAsync(await Discovery(), "preview", false, null, CancellationToken.None);
            Assert.False(r.Ok);
            Assert.Equal(UpdateTransactionCodes.HealthFailed, r.Code);
            Assert.Contains("previous layer was restored", r.Message, StringComparison.Ordinal);
            Assert.Equal("1.0.0", Dsh.InstalledVersion());
            Assert.True(File.Exists(Path.Combine(Layout.LayerDir("dsh"), "old.txt")));
            Assert.False(Dsh.IsHeldForProbe);
            Assert.Equal((UpdatePhases.Failed, UpdateTransactionCodes.HealthFailed), (u.LastState!.Phase, u.LastState.Code));
            AssertUserDataUntouched();
        }
    }

    [Fact]
    public async Task RunningDsh_IsNotStoppedWithoutConsent_AndIsRestartedAfterAConsentedUpdate()
    {
        using var runtime = Coordinator();
        Assert.True((await runtime.StartAsync(DshProfile.Web, CancellationToken.None)).Ok);
        var (u, _, http, d) = Updates(runtime);
        using (http)
        using (d)
        using (u)
        {
            var check = await Discovery();
            var refused = await u.InstallDshAsync(check, "preview", stopConfirmed: false, null, CancellationToken.None);
            Assert.Equal(RuntimeErrorCodes.InvalidTransition, refused.Code);
            Assert.Equal("1.0.0", Dsh.InstalledVersion());
            Assert.Equal(["host.start"], _calls);

            var r = await u.InstallDshAsync(check, "preview", stopConfirmed: true, null, CancellationToken.None);
            Assert.True(r.Ok, $"{r.Code} {r.Message}");
            Assert.True(r.Restarted);
            Assert.Equal(["host.start", "host.stop", "host.start", "host.stop", "host.start"], _calls);
            Assert.Equal(Target, Dsh.InstalledVersion());
        }
    }

    [Fact]
    public async Task ProcessKilledDuringTheHealthCheck_ComesBackWithTheOldLayer()
    {
        using (var runtime = Coordinator())
        {
            var (u, _, http, d) = Updates(runtime);
            using (http)
            using (d)
            using (u)
            {
                var check = await Discovery();
                var dl = await d.DownloadAsync(check.Release!, check.Manifest!.Artifacts[0], null, CancellationToken.None);
                Assert.True(dl.Ok);
                var held = await runtime.InstallDshUpdateAsync(dl.File!, CancellationToken.None);
                Assert.True(held.Ok && held.Changed);
                Assert.True(Dsh.IsHeldForProbe);
                // ...the probe starts DSH: the coordinator's own start does not undo the held layer...
                Assert.True((await runtime.StartAsync(DshProfile.Web, CancellationToken.None)).Ok);
                Assert.Equal(Target, runtime.State.Current.DshVersion);
                new UpdateStateStore(Layout).Write(new UpdateState(UpdatePhases.Probing, "dsh", Target, check.Manifest.Artifacts[0].Sha256, "preview",
                    _time.GetUtcNow(), _time.GetUtcNow(), null, null));
            }
        }
        // ...and the process dies here. The next process: the first sync restores the old layer.
        _host = new FakeDshHost(_calls);
        using var next = Coordinator();
        await next.RefreshStateAsync(CancellationToken.None);
        Assert.Equal("1.0.0", Dsh.InstalledVersion());
        Assert.False(Dsh.IsHeldForProbe);
        Assert.True(File.Exists(Path.Combine(Layout.LayerDir("dsh"), "old.txt")));
        Assert.Contains(_log.Lines, l => l.StartsWith("dsh layer: interrupted install restored", StringComparison.Ordinal));
        var (u2, _, http2, d2) = Updates(next);
        using (http2)
        using (d2)
        using (u2)
        {
            var settled = u2.SettleInterrupted();
            Assert.Equal((UpdatePhases.Failed, UpdateTransactionCodes.Interrupted), (settled!.Phase, settled.Code));
            Assert.Null(u2.SettleInterrupted()); // once
        }
        AssertUserDataUntouched();
    }

    [Fact]
    public async Task FailedDownload_TouchesNothing_AndStartsNothing()
    {
        using var runtime = Coordinator();
        var (u, server, http, d) = Updates(runtime, body: _layer.Select(b => (byte)(b ^ 1)).ToArray());
        using (http)
        using (d)
        using (u)
        {
            var r = await u.InstallDshAsync(await Discovery(), "preview", false, null, CancellationToken.None);
            Assert.Equal(DownloadErrorCodes.Sha256Mismatch, r.Code);
            Assert.Equal(1, server.Requests);
            Assert.Empty(_calls);
            Assert.Equal("1.0.0", Dsh.InstalledVersion());
            Assert.False(Dsh.IsHeldForProbe);
        }
    }

    [Fact]
    public async Task FileChangedAfterVerification_IsRefusedAtTheSwap()
    {
        using var runtime = Coordinator();
        var (_, _, http, d) = Updates(runtime);
        using (http)
        using (d)
        {
            var check = await Discovery();
            var dl = await d.DownloadAsync(check.Release!, check.Manifest!.Artifacts[0], null, CancellationToken.None);
            var bytes = await File.ReadAllBytesAsync(dl.File!.FilePath);
            bytes[100] ^= 1;
            await File.WriteAllBytesAsync(dl.File.FilePath, bytes);
            var r = await runtime.InstallDshUpdateAsync(dl.File, CancellationToken.None);
            Assert.Equal(DownloadErrorCodes.Sha256Mismatch, r.Code);
            Assert.Equal("1.0.0", Dsh.InstalledVersion());
        }
    }

    [Fact]
    public async Task NothingNewer_IsReportedAsNothingToInstall()
    {
        File.WriteAllText(Path.Combine(Layout.LayerDir("dsh"), DshLayerInstaller.VersionFile), "9.0.0");
        using var runtime = Coordinator();
        var (u, server, http, d) = Updates(runtime);
        using (http)
        using (d)
        using (u)
        {
            var sha = SHA256.HashData(_layer);
            var check = await Discovery(sha);
            var r = await u.InstallDshAsync(check, "preview", false, null, CancellationToken.None);
            Assert.Equal(UpdateTransactionCodes.NothingToInstall, r.Code);
            Assert.Equal(0, server.Requests);
        }
    }

    private sealed class SyncProgress(Action<UpdateProgress> on) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => on(value);
    }
}
