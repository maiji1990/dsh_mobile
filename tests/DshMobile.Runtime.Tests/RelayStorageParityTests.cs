using System.Text.Json;
using DshMobile.Runtime.Automation.Relay.Logging;
using DshMobile.Runtime.Automation.Relay.Storage;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// PR-07 segment 6: the C# storage and log layer over tests/TestVectors/relay-storage.json. The legacy Kotlin
/// LegacyRelayStorageParityTest runs the same cases through the real upstream RelayPaths, QuotaLedger, RunLog,
/// AuditLog and StorageReaper.
/// </summary>
public sealed class RelayStorageParityTests : IDisposable
{
    private static readonly JsonElement V = Repo.Vectors("relay-storage.json");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dshm-relay-storage-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static List<JsonElement> List(string name) => [.. V.GetProperty(name).EnumerateArray()];

    private static string Rel(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>
    /// Device finding (PR-07 relay-host shadow): the default free-space probe must measure the directory's own volume
    /// (upstream StatFs(dir)), not "/" on Unix, which is the read-only system partition on Android.
    /// </summary>
    [Fact]
    public void Default_free_space_probe_measures_the_directorys_volume()
    {
        Directory.CreateDirectory(_root);
        var expected = new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(_root)! : _root).AvailableFreeSpace;
        var actual = StorageReaper.VolumeAvailableBytes(_root);
        Assert.True(actual > QuotaLedger.ReservedFloorBytes, $"probe reported {actual} bytes free");
        Assert.InRange(actual, expected / 2, expected * 2);
    }

    [Fact]
    public void Paths_ForThisAppsPolicy()
    {
        var p = V.GetProperty("paths");
        var pol = p.GetProperty("policy");
        var policy = new RelayPathPolicy(pol.GetProperty("guestEntry").GetString()!, pol.GetProperty("hostDirName").GetString()!,
            pol.GetProperty("cliName").GetString()!, pol.GetProperty("artifactPrefix").GetString()!, pol.GetProperty("assetClient").GetString()!,
            pol.GetProperty("mediaAlbumDir").GetString()!);
        Assert.Equal(RelayPathPolicy.DshMobile, policy);
        var paths = new RelayPaths(_root, policy);
        foreach (var h in p.GetProperty("host").EnumerateObject())
        {
            var prop = typeof(RelayPaths).GetProperty(char.ToUpperInvariant(h.Name[0]) + h.Name[1..])!;
            Assert.Equal(h.Value.GetString(), Rel(_root, (string)prop.GetValue(paths)!));
        }
        foreach (var g in p.GetProperty("guest").EnumerateObject())
        {
            var prop = typeof(RelayPaths).GetProperty(char.ToUpperInvariant(g.Name[0]) + g.Name[1..])!;
            Assert.Equal(g.Value.GetString(), (string)prop.GetValue(paths)!);
        }
    }

    [Fact]
    public void UploadNames_AndResolve()
    {
        foreach (var c in List("uploadNames"))
        {
            Assert.Equal(c.GetProperty("ok").GetBoolean(), RelayStorageRules.IsUploadName(c.GetProperty("name").GetString()!));
        }
        var paths = new RelayPaths(_root, RelayPathPolicy.DshMobile);
        Directory.CreateDirectory(paths.UploadsDir);
        foreach (var n in V.GetProperty("resolveRejectsDotDot").EnumerateArray())
        {
            File.WriteAllText(Path.Combine(paths.UploadsDir, n.GetString()!), "x");
            Assert.Null(paths.ResolveUpload(n.GetString()!));
        }
        File.WriteAllText(Path.Combine(paths.UploadsDir, "ok.png"), "x");
        Assert.Equal(Path.Combine(paths.UploadsDir, "ok.png"), paths.ResolveUpload("ok.png"));
        Assert.Null(paths.ResolveUpload("missing.png"));
    }

    [Fact]
    public void SymlinkFree_PathPrefixRules()
    {
        var never = (string _) => false;
        Assert.True(RelayStorageRules.SymlinkFreeFrom("/a/b", "/a/b/c/d", never));
        Assert.True(RelayStorageRules.SymlinkFreeFrom("/a/b", "/a/b", never));
        Assert.False(RelayStorageRules.SymlinkFreeFrom("/a/b", "/a/bc/d", never));
        Assert.False(RelayStorageRules.SymlinkFreeFrom("/a/b", "/a/x", never));
        var linked = new HashSet<string>(StringComparer.Ordinal) { Path.GetFullPath("/a/b/c").Replace('\\', '/') };
        Assert.False(RelayStorageRules.SymlinkFreeFrom("/a/b", "/a/b/c/d", linked.Contains));
        Assert.True(RelayStorageRules.SymlinkFreeFrom("/a/b", "/a/b/x/d", linked.Contains));
    }

    [Fact]
    public void ArtifactOut_AndStems()
    {
        foreach (var e in V.GetProperty("subdirFor").EnumerateObject())
        {
            Assert.Equal(e.Value.GetString(), ArtifactOut.SubdirFor(e.Name));
        }
        foreach (var c in List("artifactStem"))
        {
            Assert.Equal(c[1].GetString(), StorageReaper.ArtifactStem(c[0].GetString()!));
        }
        foreach (var c in List("pinOwner"))
        {
            Assert.Equal(c[1].GetString(), StorageReaper.PinOwner(c[0].GetString()!));
        }
    }

    [Fact]
    public void Quota_ChargingAndScenario()
    {
        foreach (var c in List("charged"))
        {
            Assert.Equal(c.GetProperty("charged").GetInt64(), QuotaLedger.Charged(c.GetProperty("bytes").GetInt64()));
        }
        var q = V.GetProperty("quota");
        var file = Path.Combine(_root, "q", "quota.properties");
        var ledger = new QuotaLedger(file, q.GetProperty("maxBytes").GetInt64(), () => 42);
        var reservations = new List<QuotaLedger.Reservation?>();
        var i = 0;
        foreach (var s in q.GetProperty("steps").EnumerateArray())
        {
            var where = $"step {i++} {s}";
            switch (s.GetProperty("op").GetString())
            {
                case "reserve":
                    var r = ledger.Reserve(s.GetProperty("bytes").GetInt64());
                    reservations.Add(r);
                    Assert.True(s.GetProperty("expect").GetBoolean() == (r is not null), where);
                    break;
                case "commit":
                    ledger.Commit(reservations[s.GetProperty("which").GetInt32()]!, s.GetProperty("actual").GetInt64());
                    break;
                case "release":
                    ledger.Release(reservations[s.GetProperty("which").GetInt32()]!);
                    break;
                case "written":
                    ledger.RecordWritten(s.GetProperty("bytes").GetInt64());
                    break;
                case "freed":
                    ledger.RecordFreed(s.GetProperty("bytes").GetInt64());
                    break;
            }
            Assert.True(s.GetProperty("usable").GetInt64() == ledger.UsableBytes(), where);
            Assert.True(s.GetProperty("total").GetInt64() == ledger.TotalBytes, where);
            Assert.True(s.GetProperty("count").GetInt32() == ledger.ArtifactCount, where);
            if (s.TryGetProperty("over", out var over))
            {
                Assert.True(over.GetBoolean() == ledger.OverQuota(), where);
            }
        }
        Assert.Equal(QuotaLedger.Snapshot(0, 0, 42), File.ReadAllText(file));
        Assert.True(ledger.PersistHealthy);
    }

    [Fact]
    public void Quota_Load()
    {
        var file = Path.Combine(_root, "load.properties");
        Directory.CreateDirectory(_root);
        foreach (var c in List("quotaLoad"))
        {
            File.WriteAllText(file, c.GetProperty("text").GetString());
            var ledger = new QuotaLedger(file);
            string? corrupted = null;
            Assert.Equal(c.GetProperty("ok").GetBoolean(), ledger.Load(d => corrupted = d));
            if (c.GetProperty("ok").GetBoolean())
            {
                Assert.Null(corrupted);
                Assert.Equal(c.GetProperty("total").GetInt64(), ledger.TotalBytes);
                Assert.Equal(c.GetProperty("count").GetInt32(), ledger.ArtifactCount);
            }
            else
            {
                Assert.Equal("quota ledger malformed, reconcile from disk required", corrupted);
            }
        }
        Assert.True(new QuotaLedger(Path.Combine(_root, "absent")).Load());
    }

    [Fact]
    public void RunLog_RendersLikeUpstream()
    {
        var events = LogEvent.All.ToDictionary(e => e.Name, StringComparer.Ordinal);
        Assert.Equal(73, events.Count);
        Assert.Equal(List("logEvents").Select(e => $"{e[0].GetString()} {e[1].GetString()} {e[2].GetString()}"),
            LogEvent.All.Select(e => $"{e.Name} {e.Level.ToString().ToUpperInvariant()} {e.Template}"));
        var log = new RunLog(Path.Combine(_root, "logs"));
        foreach (var c in List("runLog"))
        {
            var ev = events[c.GetProperty("event").GetString()!];
            var sub = Enum.Parse<LogSubsystem>(c.GetProperty("sub").GetString()!, ignoreCase: true);
            var fields = c.GetProperty("fields").EnumerateArray().Select(f => (f[0].GetString()!, f[1].GetString()!)).ToArray();
            log.Event(sub, ev, fields);
            Assert.Equal(c.GetProperty("text").GetString(), log.Snapshot()[^1].Text);
            Assert.Equal(c.GetProperty("line").GetString(), File.ReadLines(Path.Combine(_root, "logs", RunLog.FileName)).Last());
        }
    }

    [Fact]
    public void AuditLog_LinesSegmentsAndDigest()
    {
        var a = V.GetProperty("audit");
        var wall = a.GetProperty("wallMs").GetInt64();
        var dir = Path.Combine(_root, "audit");
        var audit = new AuditLog(dir, a.GetProperty("salt").GetString()!, now: () => wall, zone: TimeZoneInfo.Utc);
        foreach (var e in a.GetProperty("entries").EnumerateArray())
        {
            string? Opt(string n) => e.GetProperty(n).ValueKind == JsonValueKind.Null ? null : e.GetProperty(n).GetString();
            var entry = new AuditEntry(e.GetProperty("requestId").GetString()!, e.GetProperty("cap").GetString()!, Opt("target"),
                e.GetProperty("sys").GetString()!, e.GetProperty("tier").GetString()!, e.GetProperty("decision").GetString()!,
                e.GetProperty("source").GetString()!, e.GetProperty("uid").GetString()!, e.GetProperty("outcome").GetString()!, Opt("surface"),
                Opt("from"), Opt("reason"), e.GetProperty("ms").GetInt64(), e.GetProperty("bytes").GetInt64(), Opt("args"));
            audit.Record(entry);
            Assert.Equal(e.GetProperty("line").GetString(), File.ReadLines(Path.Combine(dir, "audit-20261112.jsonl")).Last());
        }
        Assert.Equal(2, audit.Recent(10).Count);
        foreach (var s in a.GetProperty("segments").EnumerateArray())
        {
            Assert.Equal(s[2].GetString(), AuditLog.SegmentName(s[0].GetString()!, s[1].GetInt32()));
        }
    }

    [Fact]
    public void Sweep_DeliveryTree()
    {
        var s = V.GetProperty("outSweep");
        var now = s.GetProperty("nowWallMs").GetInt64();
        var paths = new RelayPaths(_root, RelayPathPolicy.DshMobile);
        var reaper = new StorageReaper(paths, new QuotaLedger(paths.QuotaFile), new RunLog(paths.LogsDir), () => 0, () => now, _ => long.MaxValue);
        var all = new List<string>();
        foreach (var f in s.GetProperty("files").EnumerateArray())
        {
            var file = Path.Combine(paths.OutDir, f.GetProperty("kind").GetString()!, f.GetProperty("name").GetString()!);
            using (var fs = File.Create(file))
            {
                fs.SetLength(f.GetProperty("bytes").GetInt64());
            }
            File.SetLastWriteTimeUtc(file, DateTimeOffset.FromUnixTimeMilliseconds(now - f.GetProperty("ageMs").GetInt64()).UtcDateTime);
            all.Add(f.GetProperty("name").GetString()!);
        }
        var result = reaper.Sweep();
        var left = ArtifactOut.Subdirs.SelectMany(k => Directory.GetFiles(Path.Combine(paths.OutDir, k))).Select(Path.GetFileName).ToHashSet();
        var deleted = all.Where(n => !left.Contains(n)).ToList();
        Assert.Equal(s.GetProperty("deleted").EnumerateArray().Select(x => x.GetString()).Order(), deleted.Order());
        Assert.Equal(deleted.Count, result.DeletedArtifacts);
    }

    [Fact]
    public void Reaper_ArtifactLifecycle_AndAtomicWrite()
    {
        var paths = new RelayPaths(_root, RelayPathPolicy.DshMobile);
        var ledger = new QuotaLedger(paths.QuotaFile, 64 * 1024);
        var reaper = new StorageReaper(paths, ledger, new RunLog(paths.LogsDir), () => 0, () => 0, _ => long.MaxValue);
        var staged = reaper.NewArtifactFile("req/1", "png", 5000);
        Assert.NotNull(staged);
        Assert.StartsWith("req1-", Path.GetFileName(staged), StringComparison.Ordinal);
        Assert.Equal(56 * 1024, ledger.UsableBytes());
        File.WriteAllBytes(staged!, new byte[5000]);
        var published = reaper.PublishArtifact(staged!, "req/1");
        Assert.NotNull(published);
        Assert.Equal(8192, ledger.TotalBytes);
        Assert.Equal(56 * 1024, ledger.UsableBytes());
        Assert.Throws<ArgumentException>(() => reaper.NewArtifactFile("x", "exe", 1));
        Assert.Null(reaper.NewArtifactFile("x", "png", 1L << 40));
        Assert.True(reaper.WriteTextAtomic(Path.Combine(paths.OutboxDir, "a.json"), "{}"));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(paths.OutboxDir, "a.json")));
        Assert.Empty(Directory.GetFiles(paths.OutboxDir, "*.tmp"));
        Assert.True(reaper.BeginCall());
        Assert.Null(reaper.ClearAllArtifacts());
        reaper.EndCall();
        Assert.Equal(5000, reaper.ClearAllArtifacts());
        Assert.Equal(0, ledger.TotalBytes);
    }

    [Fact]
    public void MediaSelection()
    {
        var g = StorageReaper.GalleryPrefixes("DSH Mobile");
        Assert.Equal(["Pictures/DSH Mobile/", "Movies/DSH Mobile/"], g);
        Assert.Equal("(relative_path LIKE ? OR relative_path LIKE ?)", StorageReaper.MediaSelection(g));
        Assert.Equal(["Music/DSH Mobile/%"], StorageReaper.MediaSelectionArgs(StorageReaper.AudioPrefixes("DSH Mobile")));
    }
}
