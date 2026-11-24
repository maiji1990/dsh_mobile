using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using DshMobile.Core.Files;

namespace DshMobile.Core.Tests;

/// <summary>vNext PR-09: text documents (encoding / line endings / read-only / binary), listing helpers, archives.</summary>
public sealed class FileWorkbenchTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "dshm-fw-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _outside;
    private readonly WorkspaceProvider _p;

    public FileWorkbenchTests()
    {
        _root = Directory.CreateDirectory(Path.Combine(_base, "user-data")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_base, "outside")).FullName;
        Directory.CreateDirectory(Path.Combine(_root, ".dsh"));
        File.WriteAllText(Path.Combine(_root, ".dsh", "credentials.json"), "{}");
        _p = new WorkspaceProvider(new WorkspaceFileSystem(_root), Path.Combine(_base, "trash"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Host(string rel) => Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));

    private static readonly FileVersion V = new(0, DateTimeOffset.UnixEpoch, new string('0', 64));

    // ── text documents ────────────────────────────────────────────────────

    [Theory]
    [InlineData(new byte[] { 0x68, 0x69, 0x0D, 0x0A, 0x79, 0x6F, 0x0D, 0x0A }, TextEncodingKind.Utf8, "\r\n")]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0xE4, 0xB8, 0xAD, 0x0A }, TextEncodingKind.Utf8Bom, "\n")]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x2D, 0x4E, 0x0A, 0x00 }, TextEncodingKind.Utf16Le, "\n")]
    [InlineData(new byte[] { 0xFE, 0xFF, 0x4E, 0x2D, 0x00, 0x0D }, TextEncodingKind.Utf16Be, "\r")]
    public void Text_RoundTripsByteForByte_InItsOwnEncodingAndLineEndings(byte[] bytes, TextEncodingKind kind, string eol)
    {
        var doc = TextDocument.Decode(bytes, V)!;
        Assert.Equal((kind, eol, false), (doc.Encoding, doc.LineEnding, doc.ReadOnly));
        Assert.Equal(bytes, doc.Encode(doc.Text));
        // An Android EditText hands back "\n": the save restores the file's own line ending.
        Assert.Equal(bytes, doc.Encode(doc.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')));
    }

    [Fact]
    public void Text_EditKeepsTheEncodingAndBom()
    {
        var doc = TextDocument.Decode([0xEF, 0xBB, 0xBF, (byte)'a', 0x0D, 0x0A], V)!;
        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("中文\r\n😀\r\n")], doc.Encode("中文\n😀\n"));
    }

    [Fact]
    public void Text_NonUtf8IsReadOnly_BinaryIsNotText_OversizeIsRefused()
    {
        var gbk = new byte[] { 0xD6, 0xD0, 0xCE, 0xC4, 0x0A }; // "中文" in GBK
        var doc = TextDocument.Decode(gbk, V)!;
        Assert.True(doc.ReadOnly);
        Assert.Contains("not UTF-8", doc.ReadOnlyReason, StringComparison.Ordinal);
        Assert.Equal(WorkspaceErrors.NotSupported, Assert.Throws<WorkspaceException>(() => doc.Encode("x")).Code);
        Assert.Null(TextDocument.Decode([0x7F, 0x45, 0x4C, 0x46, 0x00, 0x01], V)); // ELF
        Assert.Equal(WorkspaceErrors.TooLarge, Assert.Throws<WorkspaceException>(() => TextDocument.Decode(new byte[TextDocument.EditLimit + 1], V)).Code);
        var empty = TextDocument.Decode([], V)!;
        Assert.Equal(("", "\n", false), (empty.Text, empty.LineEnding, empty.ReadOnly));
        Assert.Empty(empty.Encode(""));
        Assert.Equal(WorkspaceErrors.TooLarge, Assert.Throws<WorkspaceException>(() => empty.Encode(new string('x', (int)TextDocument.EditLimit + 1))).Code);
    }

    [Theory]
    [InlineData("README.md", TextKind.Markdown)]
    [InlineData("package.json", TextKind.Json)]
    [InlineData("events.jsonl", TextKind.Code)]
    [InlineData("ci.YML", TextKind.Yaml)]
    [InlineData("main.py", TextKind.Code)]
    [InlineData("notes", TextKind.Plain)]
    public void Text_KindFromName(string name, TextKind kind) => Assert.Equal(kind, TextDocument.KindOf(name));

    [Fact]
    public void Text_JsonIsValidatedAsAWarning_OthersNot()
    {
        Assert.Null(TextDocument.Validate(TextKind.Json, "{\"a\": 1, // ok\n}"));
        Assert.StartsWith("JSON:", TextDocument.Validate(TextKind.Json, "{\"a\": }"), StringComparison.Ordinal);
        Assert.Null(TextDocument.Validate(TextKind.Yaml, "a: : :"));
    }

    // ── listing ───────────────────────────────────────────────────────────

    private static WorkspaceEntry E(string name, WorkspaceEntryKind kind = WorkspaceEntryKind.File, long size = 0, int minute = 0) =>
        new(name, name, kind, size, new DateTimeOffset(2026, 1, 1, 0, minute, 0, TimeSpan.Zero), false, null);

    [Fact]
    public void Sort_NaturalDirectoriesFirst_ByEveryKey()
    {
        var entries = new[] { E("file10.txt", size: 5, minute: 1), E("File2.txt", size: 50, minute: 3), E("zdir", WorkspaceEntryKind.Directory), E("adir", WorkspaceEntryKind.Directory), E("b.md", size: 7, minute: 2) };
        Assert.Equal(["adir", "zdir", "b.md", "File2.txt", "file10.txt"], FileListing.Sort(entries, FileSort.Name, false).Select(e => e.Name));
        Assert.Equal(["zdir", "adir", "file10.txt", "File2.txt", "b.md"], FileListing.Sort(entries, FileSort.Name, true).Select(e => e.Name));
        Assert.Equal(["adir", "zdir", "file10.txt", "b.md", "File2.txt"], FileListing.Sort(entries, FileSort.Size, false).Select(e => e.Name));
        // Descending reverses the name tie-break too (both folders have the same time).
        Assert.Equal(["zdir", "adir", "File2.txt", "b.md", "file10.txt"], FileListing.Sort(entries, FileSort.Modified, true).Select(e => e.Name));
        Assert.Equal(["adir", "zdir", "b.md", "File2.txt", "file10.txt"], FileListing.Sort(entries, FileSort.Type, false).Select(e => e.Name));
    }

    [Theory]
    [InlineData("a2", "a10", -1)]
    [InlineData("a010", "a10", -1)] // same number: ordinal tie-break, so the order is still total
    [InlineData("A", "a", -1)]
    [InlineData("v1.10", "v1.9", 1)]
    [InlineData("中文2", "中文10", -1)]
    public void NaturalCompare(string a, string b, int sign) => Assert.Equal(sign, Math.Sign(FileListing.NaturalCompare(a, b)));

    [Fact]
    public void Filter_AndBreadcrumbs()
    {
        var entries = new[] { E("Report.md"), E("报告.md"), E("notes.txt") };
        Assert.Equal(["Report.md"], FileListing.Filter(entries, " report ").Select(e => e.Name));
        Assert.Equal(["报告.md"], FileListing.Filter(entries, "报告").Select(e => e.Name));
        Assert.Equal(3, FileListing.Filter(entries, "").Count);
        Assert.Equal([("/root/projects", ""), ("a", "a"), ("中文", "a/中文")], FileListing.Breadcrumbs("/root/projects", "a/中文/").Select(c => (c.Label, c.Path)));
        Assert.Throws<WorkspaceException>(() => FileListing.Breadcrumbs("/", "../x"));
    }

    [Fact]
    public void Search_SkipsDshContentAndLinks_AndStopsAtTheLimits()
    {
        Directory.CreateDirectory(Host("a/b"));
        File.WriteAllText(Host("a/b/target.txt"), "");
        File.WriteAllText(Host("a/target-2.txt"), "");
        File.WriteAllText(Host(".dsh/target-secret.txt"), "");
        File.WriteAllText(Path.Combine(_outside, "target-outside.txt"), "");
        WorkspaceProviderTests.TryLinkDirectory(Host("a/out"), _outside);
        var r = FileListing.Search(_p.FileSystem, "", "TARGET");
        Assert.Equal(["a/target-2.txt", "a/b/target.txt"], r.Matches.Select(m => m.RelativePath));
        Assert.False(r.Truncated);
        Assert.True(FileListing.Search(_p.FileSystem, "", "target", maxResults: 1).Truncated);
        Assert.True(FileListing.Search(_p.FileSystem, "", "target", maxVisited: 2).Truncated);
        Assert.Equal([".dsh"], FileListing.Search(_p.FileSystem, "", ".dsh").Matches.Select(m => m.RelativePath)); // the name still matches
    }

    [Fact]
    public void DiscoverGitRepositories_FindsImmediateNonProtectedGitChildren_SkipsSymlinksAndDsh()
    {
        Directory.CreateDirectory(Host("pr06-repo/.git"));
        Directory.CreateDirectory(Host("worktree-repo"));
        File.WriteAllText(Host("worktree-repo/.git"), "gitdir: ../.git");
        Directory.CreateDirectory(Host("plain-dir/nested/.git"));
        Directory.CreateDirectory(Host(".dsh/.git"));
        Directory.CreateDirectory(Path.Combine(_outside, "outside-repo/.git"));
        WorkspaceProviderTests.TryLinkDirectory(Host("linked-repo"), Path.Combine(_outside, "outside-repo"));

        var found = FileListing.DiscoverGitRepositories(_p.FileSystem, "");
        Assert.Equal([("pr06-repo", "pr06-repo"), ("worktree-repo", "worktree-repo")], found.Select(r => (r.Label, r.Path)));
        Assert.Equal([("nested", "plain-dir/nested")], FileListing.DiscoverGitRepositories(_p.FileSystem, "plain-dir", maxCount: 4).Select(r => (r.Label, r.Path)));
        Assert.Empty(FileListing.DiscoverGitRepositories(_p.FileSystem, "plain-dir/nested", maxCount: 4));
    }

    // ── archives ──────────────────────────────────────────────────────────

    private string Zip(string name, Action<ZipArchive> fill)
    {
        var path = Host(name);
        using (var fs = File.Create(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            fill(zip);
        }
        return name;
    }

    private static void Add(ZipArchive zip, string name, string text, int unixMode = 0)
    {
        var e = zip.CreateEntry(name);
        if (unixMode != 0)
        {
            e.ExternalAttributes = unixMode << 16;
        }
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(text);
    }

    private void AssertNothingExtracted()
    {
        string[] expected = ["outside", "trash", "user-data"];
        Assert.Equal(expected, Directory.GetDirectories(_base).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal([".dsh"], Directory.GetDirectories(_root).Select(Path.GetFileName));
        Assert.Empty(Directory.GetFiles(_outside));
    }

    [Fact]
    public void Zip_ExtractsIntoAFreshFolder_WithChineseNamesAndTheExecutableBit()
    {
        Zip("pkg.zip", z =>
        {
            Add(z, "pkg/readme.md", "# 你好");
            Add(z, "pkg/bin/run.sh", "#!/bin/sh\n", unixMode: 0x81ED); // -rwxr-xr-x
            Add(z, "pkg/中文/空.txt", "");
            z.CreateEntry("pkg/empty-dir/");
        });
        var r = _p.Extract("pkg.zip", "");
        Assert.Equal(("pkg", 3), (r.Directory, r.Files));
        Assert.Equal("# 你好", File.ReadAllText(Host("pkg/pkg/readme.md")));
        Assert.True(Directory.Exists(Host("pkg/pkg/empty-dir")));
        Assert.Equal(0, new FileInfo(Host("pkg/pkg/中文/空.txt")).Length);
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(Host("pkg/pkg/bin/run.sh")).HasFlag(UnixFileMode.UserExecute));
            Assert.False(File.GetUnixFileMode(Host("pkg/pkg/readme.md")).HasFlag(UnixFileMode.UserExecute));
        }
        Assert.Equal("pkg (1)", _p.Extract("pkg.zip", "").Directory); // never merged into an existing folder
        WorkspaceProviderTests.NoLeftovers(_root);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("ok/../../evil.txt")]
    [InlineData("/etc/evil.txt")]
    [InlineData("C:/evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("dir/\u0001bad.txt")]
    public void Zip_AnEscapingEntry_RefusesTheWholeArchive_AndLeavesNothing(string evil)
    {
        Zip("evil.zip", z =>
        {
            Add(z, "good/first.txt", "fine");
            Add(z, evil, "pwned");
        });
        Assert.Equal(WorkspaceErrors.ArchiveUnsafe, Assert.Throws<WorkspaceException>(() => _p.Extract("evil.zip", "")).Code);
        AssertNothingExtracted();
        Assert.False(File.Exists(Path.Combine(_base, "evil.txt")));
        WorkspaceProviderTests.NoLeftovers(_root);
    }

    [Fact]
    public void Zip_LinksAndDevicesAreNeverCreated_DuplicatesAndClashesAbort()
    {
        Zip("links.zip", z =>
        {
            Add(z, "a/link", "/etc/passwd", unixMode: 0xA1FF); // lrwxrwxrwx
            Add(z, "a/real.txt", "ok");
        });
        var r = _p.Extract("links.zip", "");
        Assert.Equal(["a/link (symlink)"], r.Skipped);
        Assert.False(File.Exists(Host("links/a/link")));
        Assert.Equal("ok", File.ReadAllText(Host("links/a/real.txt")));

        Zip("dup.zip", z =>
        {
            Add(z, "x.txt", "1");
            Add(z, "X.txt", "2");
        });
        Assert.Equal(WorkspaceErrors.ArchiveUnsafe, Assert.Throws<WorkspaceException>(() => _p.Extract("dup.zip", "")).Code);
        Zip("clash.zip", z =>
        {
            Add(z, "x", "file");
            Add(z, "x/y.txt", "under a file");
        });
        Assert.Equal(WorkspaceErrors.ArchiveUnsafe, Assert.Throws<WorkspaceException>(() => _p.Extract("clash.zip", "")).Code);
        Assert.False(Directory.Exists(Host("dup")));
        Assert.False(Directory.Exists(Host("clash")));
        WorkspaceProviderTests.NoLeftovers(_root);
    }

    [Fact]
    public void Zip_LimitsCountRealBytes_AndDamagedArchivesAreUnsupported()
    {
        Zip("bomb.zip", z => Add(z, "big.txt", new string('a', 100_000)));
        Assert.Equal(WorkspaceErrors.ArchiveTooLarge, Assert.Throws<WorkspaceException>(() => _p.Extract("bomb.zip", "", new ArchiveLimits(MaxTotalBytes: 10_000))).Code);
        Zip("many.zip", z =>
        {
            for (var i = 0; i < 20; i++)
            {
                Add(z, $"f{i}.txt", "");
            }
        });
        Assert.Equal(WorkspaceErrors.ArchiveTooLarge, Assert.Throws<WorkspaceException>(() => _p.Extract("many.zip", "", new ArchiveLimits(MaxEntries: 10))).Code);
        File.WriteAllText(Host("broken.zip"), "PK\u0003\u0004 not really a zip");
        Assert.Equal(WorkspaceErrors.ArchiveUnsupported, Assert.Throws<WorkspaceException>(() => _p.Extract("broken.zip", "")).Code);
        File.WriteAllText(Host("notes.rar"), "");
        Assert.Equal(WorkspaceErrors.ArchiveUnsupported, Assert.Throws<WorkspaceException>(() => _p.Extract("notes.rar", "")).Code);
        Assert.False(Directory.Exists(Host("bomb")));
        Assert.False(Directory.Exists(Host("many")));
        WorkspaceProviderTests.NoLeftovers(_root);
    }

    [Fact]
    public void Extract_RefusesDshAsTargetOrSource()
    {
        Zip("ok.zip", z => Add(z, "a.txt", "a"));
        Assert.Equal(WorkspaceErrors.Protected, Assert.Throws<WorkspaceException>(() => _p.Extract("ok.zip", ".dsh")).Code);
        File.Copy(Host("ok.zip"), Host(".dsh/ok.zip"));
        Assert.Equal(WorkspaceErrors.Protected, Assert.Throws<WorkspaceException>(() => _p.Extract(".dsh/ok.zip", "")).Code);
    }

    private string Tar(string name, bool gzip, Action<TarWriter> fill)
    {
        var path = Host(name);
        using var fs = File.Create(path);
        using Stream s = gzip ? new GZipStream(fs, CompressionLevel.Fastest) : fs;
        using (var tar = new TarWriter(s, TarEntryFormat.Pax, leaveOpen: true))
        {
            fill(tar);
        }
        return name;
    }

    private static void TarFile(TarWriter tar, string name, string text, UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite)
    {
        var e = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)), Mode = mode };
        tar.WriteEntry(e);
    }

    [Fact]
    public void Tar_AndTarGz_Extract_LinksAndDevicesSkipped_EscapesRefused()
    {
        Tar("src.tar.gz", gzip: true, t =>
        {
            t.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "src/"));
            TarFile(t, "src/main.c", "int main(){}", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            t.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "src/etc") { LinkName = "/etc" });
            t.WriteEntry(new PaxTarEntry(TarEntryType.HardLink, "src/hard") { LinkName = "/etc/shadow" });
            t.WriteEntry(new PaxTarEntry(TarEntryType.Fifo, "src/pipe"));
        });
        var r = _p.Extract("src.tar.gz", "");
        Assert.Equal("src", r.Directory);
        Assert.Equal("int main(){}", File.ReadAllText(Host("src/src/main.c")));
        Assert.Equal(3, r.Skipped.Count);
        Assert.False(File.Exists(Host("src/src/etc")) || Directory.Exists(Host("src/src/etc")));
        Assert.False(File.Exists(Host("src/src/hard")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(Host("src/src/main.c")).HasFlag(UnixFileMode.UserExecute));
        }

        Tar("evil.tar", gzip: false, t =>
        {
            TarFile(t, "ok.txt", "ok");
            TarFile(t, "../../evil.txt", "pwned");
        });
        Assert.Equal(WorkspaceErrors.ArchiveUnsafe, Assert.Throws<WorkspaceException>(() => _p.Extract("evil.tar", "")).Code);
        Assert.False(Directory.Exists(Host("evil")));
        Assert.False(File.Exists(Path.Combine(_base, "evil.txt")));
        WorkspaceProviderTests.NoLeftovers(_root);
    }

    [Fact]
    public void CreateZip_LeavesLinksOut_ExcludesItself_AndRoundTrips()
    {
        Directory.CreateDirectory(Host("proj/src"));
        File.WriteAllText(Host("proj/src/a.txt"), "A");
        File.WriteAllText(Host("proj/中文.md"), "文");
        var linked = WorkspaceProviderTests.TryLinkDirectory(Host("proj/out"), _outside);
        var z = _p.CreateZip(["proj"], "proj", "proj"); // the zip lands inside the folder being zipped
        Assert.Equal(("proj/proj.zip", 2, linked ? 1 : 0), (z.Path, z.Files, z.SkippedLinks));
        using (var zip = ZipFile.OpenRead(Host(z.Path)))
        {
            Assert.Equal(["proj/", "proj/src/", "proj/src/a.txt", "proj/中文.md"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        }
        var back = _p.Extract(z.Path, "");
        Assert.Equal("A", File.ReadAllText(Host(back.Directory + "/proj/src/a.txt")));
        Assert.Equal(WorkspaceErrors.Protected, Assert.Throws<WorkspaceException>(() => _p.CreateZip([".dsh"], "", "leak")).Code);
        Assert.Equal(WorkspaceErrors.Protected, Assert.Throws<WorkspaceException>(() => _p.CreateZip(["proj/中文.md"], ".dsh", "x")).Code);
        WorkspaceProviderTests.NoLeftovers(_root);
    }
}
