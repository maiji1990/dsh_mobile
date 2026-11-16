using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using DshMobile.Runtime.Layers;
using DshMobile.Runtime.Updates;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// vNext PR-04: the artifact downloader against a scripted server — short and oversized bodies, interrupted transfers
/// and resume (206 / 200 / changed entity), a tampered partial file, insecure redirects, disk budget, cache cap,
/// duplicates, a killed process, unsafe names — and it never touches runtime-current or user-data.
/// </summary>
public sealed class ArtifactDownloaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dshm-download-").FullName;

    public void Dispose() => LayerInstaller.DeleteTree(_dir);

    private RuntimeLayout Layout => new(Path.Combine(_dir, "files"), null, "/lib");

    private static readonly byte[] Content = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7 % 251)).ToArray();

    private static ReleaseArtifact Artifact(byte[]? content = null, string archive = "dsh-0.2.2.tar.zst", string? sha = null) => new(
        "dsh", "0.2.2", "arm64", archive, new Uri($"https://github.com/maiji1990/dsh_mobile/releases/download/dsh-0.2.2/{archive}"),
        sha ?? Convert.ToHexStringLower(SHA256.HashData(content ?? Content)), (content ?? Content).Length,
        null, "24.x", "deepseek-ai/deepseek-harness", "dsh-v0.2.2", new string('b', 40));

    private static VerifiedRelease Release(params ReleaseArtifact[] artifacts) => new(
        new ReleaseManifest(1, "preview", new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 12, 16, 0, 0, 0, TimeSpan.Zero), artifacts),
        "ed25519:0000000000000000");

    /// <summary>A scripted HTTPS file server for one entity.</summary>
    private sealed class Server : HttpMessageHandler
    {
        public byte[] Body { get; set; } = Content;
        public string ETag { get; set; } = "\"v1\"";
        public bool HonorRange { get; set; } = true;
        public long? FailAfter { get; set; }
        public byte[] Extra { get; set; } = [];
        public HttpStatusCode? Status { get; set; }
        public Uri? FinalUri { get; set; }
        public long? AnnouncedLength { get; set; }
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            if (Status is { } s)
            {
                return Task.FromResult(new HttpResponseMessage(s) { RequestMessage = request });
            }
            var from = 0L;
            var partial = false;
            var ifRange = request.Headers.TryGetValues("If-Range", out var v) ? v.Single() : null;
            if (HonorRange && request.Headers.Range?.Ranges.SingleOrDefault()?.From is { } f && (ifRange is null || ifRange == ETag))
            {
                from = f;
                partial = true;
            }
            var bytes = Body.Skip((int)from).Concat(Extra).ToArray();
            var content = new StreamContent(new FailingStream(bytes, FailAfter is { } n ? Math.Max(0, n - from) : null));
            content.Headers.ContentLength = AnnouncedLength ?? bytes.Length;
            var response = new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = content, RequestMessage = request };
            response.Headers.ETag = new EntityTagHeaderValue(ETag);
            if (partial)
            {
                content.Headers.ContentRange = new ContentRangeHeaderValue(from, Body.Length - 1, Body.Length);
            }
            if (FinalUri is not null)
            {
                response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, FinalUri);
            }
            return Task.FromResult(response);
        }
    }

    /// <summary>A body that throws a connection error after <c>failAfter</c> bytes.</summary>
    private sealed class FailingStream(byte[] bytes, long? failAfter) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (failAfter is { } n && Position >= n)
            {
                throw new IOException("connection reset by peer");
            }
            var max = failAfter is { } m ? (int)Math.Min(count, m - Position) : count;
            return base.Read(buffer, offset, max);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Task.FromResult(Read(buffer, offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var tmp = new byte[buffer.Length];
            var n = Read(tmp, 0, tmp.Length);
            tmp.AsSpan(0, n).CopyTo(buffer.Span);
            return ValueTask.FromResult(n);
        }
    }

    private (ArtifactDownloader D, ArtifactStore S, HttpClient H) Downloader(Server server, long free = long.MaxValue, long? cache = null)
    {
        var store = cache is { } c ? new ArtifactStore(Layout) { MaxCacheBytes = c } : new ArtifactStore(Layout);
        var http = new HttpClient(server);
        return (new ArtifactDownloader(http, store, _ => free), store, http);
    }

    private static Task<DownloadResult> Get(ArtifactDownloader d, ReleaseArtifact a, VerifiedRelease? r = null) =>
        d.DownloadAsync(r ?? Release(a), a, null, CancellationToken.None);

    [Fact]
    public async Task FullDownload_IsVerified_StoredByDigest_AndRecordsItsOrigin()
    {
        var server = new Server();
        var (d, store, http) = Downloader(server);
        using (http)
        using (d)
        {
            var a = Artifact();
            var r = await Get(d, a);
            Assert.True(r.Ok, $"{r.Code} {r.Message}");
            Assert.Equal(Path.Combine(store.VerifiedDir, a.Sha256, a.Archive), r.File!.FilePath);
            Assert.Equal(Content, await File.ReadAllBytesAsync(r.File.FilePath));
            Assert.Equal("ed25519:0000000000000000", r.File.ManifestKeyId);
            Assert.Contains("\"keyId\": \"ed25519:0000000000000000\"", await File.ReadAllTextAsync(Path.Combine(store.VerifiedDir, a.Sha256, "verified.json")));
            Assert.False(File.Exists(store.PartFile(a)));
            Assert.False(File.Exists(store.JournalFile(a)));
            Assert.Null(server.Requests[0].Headers.Range);
            Assert.Null(server.Requests[0].Headers.Authorization);
        }
    }

    [Fact]
    public async Task ArtifactsNotInTheVerifiedManifest_OrWithUnsafeNames_AreNeverFetched()
    {
        var server = new Server();
        var (d, store, http) = Downloader(server);
        using (http)
        using (d)
        {
            var a = Artifact();
            var other = a with { Url = new Uri("https://evil.example.invalid/x.tar.zst") };
            Assert.Equal(DownloadErrorCodes.NotOffered, (await Get(d, other, Release(a))).Code); // a free URL, not the manifest's
            foreach (var bad in (ReleaseArtifact[])[a with { Archive = "../escape.tar.zst" }, a with { Archive = ".hidden.tar.zst" },
                         a with { Sha256 = "../../user-data" }, a with { Url = new Uri("http://github.com/x.tar.zst") }])
            {
                Assert.Equal(DownloadErrorCodes.NotOffered, (await Get(d, bad, Release(bad))).Code);
            }
            Assert.Empty(server.Requests);
            Assert.False(Directory.Exists(store.Root));
        }
    }

    [Fact]
    public async Task InterruptedDownload_ResumesWithRangeAndIfRange_AndTheWholeFileIsHashed()
    {
        var server = new Server { FailAfter = 120_000 };
        var (d, store, http) = Downloader(server);
        using (http)
        using (d)
        {
            var a = Artifact();
            var first = await Get(d, a);
            Assert.Equal(DownloadErrorCodes.Incomplete, first.Code);
            Assert.True(new FileInfo(store.PartFile(a)).Length is > 0 and <= 120_000);
            Assert.True(File.Exists(store.JournalFile(a)));

            server.FailAfter = null;
            // A new downloader = the app restarted after being killed; only the journal and the part survive.
            var (d2, _, http2) = Downloader(server);
            using (http2)
            using (d2)
            {
                var second = await Get(d2, a);
                Assert.True(second.Ok, $"{second.Code} {second.Message}");
                Assert.True(second.Resumed);
                var req = server.Requests[^1];
                Assert.Equal(new FileInfo(second.File!.FilePath).Length, a.Size);
                Assert.NotNull(req.Headers.Range);
                Assert.Equal("\"v1\"", req.Headers.GetValues("If-Range").Single());
                Assert.Equal(Content, await File.ReadAllBytesAsync(second.File.FilePath));
            }
        }
    }

    [Fact]
    public async Task ChangedEntity_OrRangeIgnored_StartsOverFromByteZero()
    {
        foreach (var (label, mutate) in (ValueTuple<string, Action<Server>>[])[("etag changed", s => s.ETag = "\"v2\""), ("200 instead of 206", s => s.HonorRange = false)])
        {
            var server = new Server { FailAfter = 100_000 };
            var (d, store, http) = Downloader(server);
            using (http)
            using (d)
            {
                var a = Artifact();
                Assert.Equal(DownloadErrorCodes.Incomplete, (await Get(d, a)).Code);
                server.FailAfter = null;
                mutate(server);
                var r = await Get(d, a);
                Assert.True(r.Ok, $"{label}: {r.Code} {r.Message}");
                Assert.False(r.Resumed, label);
                Assert.Equal(Content, await File.ReadAllBytesAsync(r.File!.FilePath));
            }
            LayerInstaller.DeleteTree(Path.Combine(_dir, "files"));
        }
    }

    [Fact]
    public async Task TamperedPartialFile_IsCaughtByTheWholeFileHash_AndDeleted()
    {
        var server = new Server { FailAfter = 150_000 };
        var (d, store, http) = Downloader(server);
        using (http)
        using (d)
        {
            var a = Artifact();
            Assert.Equal(DownloadErrorCodes.Incomplete, (await Get(d, a)).Code);
            // Something else on the device flips a byte in the part while the app is away.
            var bytes = await File.ReadAllBytesAsync(store.PartFile(a));
            bytes[10] ^= 0xFF;
            await File.WriteAllBytesAsync(store.PartFile(a), bytes);

            server.FailAfter = null;
            var r = await Get(d, a);
            Assert.Equal(DownloadErrorCodes.Sha256Mismatch, r.Code);
            Assert.False(File.Exists(store.PartFile(a)));
            Assert.False(Directory.Exists(Path.Combine(store.VerifiedDir, a.Sha256)));

            Assert.True((await Get(d, a)).Ok); // and a clean retry works
        }
    }

    [Fact]
    public async Task WrongContent_ShortBody_LongBody_AndContradictingHeaders_AreNeverVerified()
    {
        var a = Artifact();
        var cases = new (string Label, Action<Server> Setup, string Code)[]
        {
            ("different bytes, same length", s => s.Body = Content.Select(b => (byte)(b ^ 1)).ToArray(), DownloadErrorCodes.Sha256Mismatch),
            ("body longer than the manifest", s => { s.Extra = [1, 2, 3]; s.AnnouncedLength = Content.Length; }, DownloadErrorCodes.SizeMismatch),
            ("Content-Length contradicts the manifest", s => s.AnnouncedLength = Content.Length + 5, DownloadErrorCodes.SizeMismatch),
            ("connection closes early", s => { s.Body = Content[..1000]; s.AnnouncedLength = Content.Length; }, DownloadErrorCodes.Incomplete),
            ("HTTP 404", s => s.Status = HttpStatusCode.NotFound, DownloadErrorCodes.HttpFailed),
            ("redirected to http", s => s.FinalUri = new Uri("http://downgrade.example.invalid/x"), DownloadErrorCodes.InsecureRedirect),
        };
        foreach (var (label, setup, code) in cases)
        {
            var server = new Server();
            setup(server);
            var (d, store, http) = Downloader(server);
            using (http)
            using (d)
            {
                var r = await Get(d, a);
                Assert.True(r.Code == code, $"{label}: {r.Code} {r.Message}");
                Assert.Null(store.TryGetVerified(a, CancellationToken.None));
            }
            LayerInstaller.DeleteTree(Path.Combine(_dir, "files"));
        }
    }

    [Fact]
    public async Task NotEnoughSpace_OrTooBigForTheCache_IsRefusedBeforeAnyByte()
    {
        var a = Artifact();
        var server = new Server();
        var (d, _, http) = Downloader(server, free: a.Size);
        using (http)
        using (d)
        {
            Assert.Equal(DownloadErrorCodes.InsufficientSpace, (await Get(d, a)).Code);
        }
        var (d2, _, http2) = Downloader(server, cache: a.Size - 1);
        using (http2)
        using (d2)
        {
            Assert.Equal(DownloadErrorCodes.CacheFull, (await Get(d2, a)).Code);
        }
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task CacheCap_EvictsTheLeastRecentlyUsedArtifact()
    {
        var small = Content[..100_000];
        var older = Artifact(small, "old-0.1.tar.zst");
        var newer = Artifact(Content);
        var server = new Server { Body = small };
        var (d, store, http) = Downloader(server, cache: Content.Length + 50_000);
        using (http)
        using (d)
        {
            Assert.True((await Get(d, older)).Ok);
            server.Body = Content;
            Assert.True((await Get(d, newer)).Ok);
            Assert.Null(store.TryGetVerified(older, CancellationToken.None)); // evicted to make room
            Assert.NotNull(store.TryGetVerified(newer, CancellationToken.None));
            Assert.True(store.VerifiedBytes() <= store.MaxCacheBytes);
        }
    }

    [Fact]
    public async Task SecondRequest_IsServedFromTheVerifiedCache_AfterReHashing()
    {
        var server = new Server();
        var (d, store, http) = Downloader(server);
        using (http)
        using (d)
        {
            var a = Artifact();
            Assert.True((await Get(d, a)).Ok);
            var again = await Get(d, a);
            Assert.True(again.FromCache);
            Assert.Single(server.Requests);

            // A cached file changed on disk is not handed out: it is dropped and downloaded again.
            var file = store.VerifiedFile(a);
            var bytes = await File.ReadAllBytesAsync(file);
            bytes[^1] ^= 1;
            await File.WriteAllBytesAsync(file, bytes);
            var refetched = await Get(d, a);
            Assert.True(refetched.Ok);
            Assert.False(refetched.FromCache);
            Assert.Equal(2, server.Requests.Count);
        }
    }

    [Fact]
    public async Task Cancellation_KeepsThePart_ForTheNextAttempt()
    {
        var server = new Server();
        var (d, store, http) = Downloader(server);
        using (http)
        using (d)
        {
            var a = Artifact();
            using var cts = new CancellationTokenSource();
            var p = new SyncProgress(r => { if (r.Received >= 65_536) cts.Cancel(); });
            var r = await d.DownloadAsync(Release(a), a, p, cts.Token);
            Assert.Equal(DownloadErrorCodes.Cancelled, r.Code);
            Assert.True(File.Exists(store.JournalFile(a)));
            var resumed = await Get(d, a);
            Assert.True(resumed.Ok, $"{resumed.Code} {resumed.Message}");
            Assert.True(resumed.Resumed);
        }
    }

    private sealed class SyncProgress(Action<DownloadProgress> on) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => on(value);
    }

    [Fact]
    public async Task NothingOutsideTheUpdateStore_IsEverWritten()
    {
        var layout = Layout;
        Directory.CreateDirectory(Path.Combine(layout.RuntimeCurrentDir, "dsh"));
        await File.WriteAllTextAsync(Path.Combine(layout.RuntimeCurrentDir, "dsh", "old.txt"), "layer");
        Directory.CreateDirectory(Path.Combine(layout.UserDataDir, ".dsh"));
        await File.WriteAllTextAsync(Path.Combine(layout.UserDataDir, ".dsh", "credentials.yaml"), "secret");
        string[] Outside() => Directory.EnumerateFileSystemEntries(layout.FilesDir, "*", SearchOption.AllDirectories)
            .Where(p => !p.StartsWith(layout.UpdatesDir, StringComparison.Ordinal)).Order().ToArray();
        var before = Outside();

        var server = new Server { FailAfter = 50_000 };
        var (d, _, http) = Downloader(server);
        using (http)
        using (d)
        {
            var a = Artifact();
            await Get(d, a); // interrupted
            server.FailAfter = null;
            Assert.True((await Get(d, a)).Ok);
        }
        Assert.Equal(before, Outside());
        Assert.Equal("secret", await File.ReadAllTextAsync(Path.Combine(layout.UserDataDir, ".dsh", "credentials.yaml")));
    }

    [Fact]
    public async Task OnlyTheVerifierMintsReleases_DiscoveryCarriesTheToken()
    {
        // The public surface cannot build a VerifiedRelease; a verified discovery result carries one.
        Assert.DoesNotContain(typeof(VerifiedRelease).GetConstructors(), c => c.IsPublic);
        var key = Convert.FromBase64String("0/7Vy+xzt4e19iEbU63oDxGycevISE5500hV1Q8yu3o=");
        var manifest = await File.ReadAllBytesAsync(Repo.PathOf("tests", "TestVectors", "release-manifest", "release-manifest.json"));
        var envelope = await File.ReadAllBytesAsync(Repo.PathOf("tests", "TestVectors", "release-manifest", "release-manifest.json.sig"));
        var v = ReleaseManifestVerifier.Verify(manifest, envelope, new ReleaseTrust([key]), "preview", new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));
        Assert.True(v.Ok);
        Assert.NotNull(v.Release);
        Assert.All(v.Manifest!.Artifacts, a => Assert.True(v.Release!.Offers(a)));
        Assert.False(v.Release!.Offers(v.Manifest.Artifacts[0] with { Sha256 = new string('0', 64) }));
    }
}
