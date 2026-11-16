using System.Diagnostics;
using System.Text;
using DshMobile.Core.Files;
using Microsoft.Extensions.Time.Testing;

namespace DshMobile.Core.Tests;

/// <summary>
/// vNext PR-09: the workspace as an <see cref="IFileProvider"/> — every mutation keeps the WorkspaceFileSystem rules
/// (no symlink traversal, <c>.dsh</c> untouchable in both directions, no escape), writes are atomic and conflict-checked,
/// deletes go to a restorable trash outside the workspace, copies / moves never overwrite.
/// </summary>
public sealed class WorkspaceProviderTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "dshm-wp-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _outside;
    private readonly string _trash;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));
    private readonly WorkspaceProvider _p;

    public WorkspaceProviderTests()
    {
        _root = Directory.CreateDirectory(Path.Combine(_base, "user-data")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_base, "outside")).FullName;
        _trash = Path.Combine(_base, "trash");
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "host secret");
        Directory.CreateDirectory(Path.Combine(_root, "proj", "src"));
        File.WriteAllText(Path.Combine(_root, "proj", "readme.md"), "# hi");
        File.WriteAllText(Path.Combine(_root, "proj", "src", "main.py"), "print(1)\n");
        Directory.CreateDirectory(Path.Combine(_root, ".dsh", "profiles"));
        File.WriteAllText(Path.Combine(_root, ".dsh", "credentials.json"), "{\"key\":\"sk-xxx\"}");
        _p = new WorkspaceProvider(new WorkspaceFileSystem(_root), _trash, time: _time);
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

    private static string Code(Action a) => Assert.Throws<WorkspaceException>(a).Code;

    internal static bool TryLinkDirectory(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        using var proc = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
        proc!.WaitForExit();
        return proc.ExitCode == 0 && new DirectoryInfo(link).LinkTarget is not null;
    }

    internal static bool TryLinkFile(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false; // Windows without Developer Mode: no file symlinks
        }
    }

    internal static void NoLeftovers(string dir) =>
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories),
            p => p.EndsWith(".part", StringComparison.Ordinal) || p.EndsWith(".rename", StringComparison.Ordinal) || p.EndsWith(".extract", StringComparison.Ordinal));

    // ── capabilities ──────────────────────────────────────────────────────

    [Fact]
    public void Capabilities_AreWhatTheEntryReallyAllows()
    {
        Assert.True(_p.Capabilities.HasFlag(FileCapabilities.Trash));
        Assert.False(new WorkspaceProvider(new WorkspaceFileSystem(_root), null).Capabilities.HasFlag(FileCapabilities.Trash));
        var entries = _p.FileSystem.List("");
        var dsh = entries.Single(e => e.Name == ".dsh");
        Assert.Equal(FileCapabilities.List, _p.CapabilitiesOf(dsh));
        var cred = _p.FileSystem.List(".dsh").Single(e => e.Name == "credentials.json");
        Assert.Equal(FileCapabilities.None, _p.CapabilitiesOf(cred));
        var proj = entries.Single(e => e.Name == "proj");
        Assert.True(_p.CapabilitiesOf(proj).HasFlag(FileCapabilities.Delete | FileCapabilities.Copy | FileCapabilities.Create));
        Assert.False(_p.CapabilitiesOf(proj).HasFlag(FileCapabilities.Read));
        var readme = _p.FileSystem.List("proj").Single(e => e.Name == "readme.md");
        Assert.True(_p.CapabilitiesOf(readme).HasFlag(FileCapabilities.Read | FileCapabilities.Write));
        if (TryLinkDirectory(Host("link"), _outside))
        {
            var link = _p.FileSystem.List("").Single(e => e.Name == "link");
            Assert.Equal(FileCapabilities.Delete | FileCapabilities.Rename | FileCapabilities.Move, _p.CapabilitiesOf(link));
        }
    }

    [Fact]
    public void TheTrash_MustBeOutsideTheWorkspace() =>
        Assert.Throws<ArgumentException>(() => new WorkspaceProvider(new WorkspaceFileSystem(_root), Path.Combine(_root, "trash")));

    // ── create / rename ───────────────────────────────────────────────────

    [Fact]
    public void Create_ChineseNames_EmptyFiles_AndRefusals()
    {
        Assert.Equal("proj/笔记", _p.CreateDirectory("proj", "笔记"));
        Assert.Equal("proj/笔记/空文件.txt", _p.CreateFile("proj/笔记", "空文件.txt"));
        Assert.Equal(0, new FileInfo(Host("proj/笔记/空文件.txt")).Length);
        Assert.Equal(WorkspaceErrors.Exists, Code(() => _p.CreateFile("proj/笔记", "空文件.txt")));
        Assert.Equal(WorkspaceErrors.Exists, Code(() => _p.CreateDirectory("proj", "readme.md")));
        foreach (var bad in new[] { "a/b", "..", ".", "", " padded", "x\u0001" })
        {
            Assert.Equal(WorkspaceErrors.InvalidName, Code(() => _p.CreateFile("proj", bad)));
        }
        Assert.Equal(WorkspaceErrors.Protected, Code(() => _p.CreateFile(".dsh", "planted.json")));
        Assert.Equal(WorkspaceErrors.Protected, Code(() => _p.CreateDirectory("", ".dsh")));
        Assert.Equal(WorkspaceErrors.OutsideRoot, Code(() => _p.CreateFile("../outside", "x")));
        Assert.Equal(WorkspaceErrors.NotFound, Code(() => _p.CreateFile("nope", "x")));
    }

    [Fact]
    public void Rename_Collision_CaseOnly_AndProtected()
    {
        Assert.Equal("proj/README.md", _p.Rename("proj/readme.md", "README.md")); // case only: works on any filesystem
        Assert.Equal(["README.md"], Directory.GetFiles(Host("proj")).Select(Path.GetFileName));
        _p.CreateFile("proj", "other.md");
        Assert.Equal(WorkspaceErrors.Exists, Code(() => _p.Rename("proj/other.md", "README.md")));
        Assert.Equal("proj/说明.md", _p.Rename("proj/other.md", "说明.md"));
        Assert.Equal(WorkspaceErrors.Protected, Code(() => _p.Rename(".dsh", "dsh-old")));
        Assert.Equal(WorkspaceErrors.Protected, Code(() => _p.Rename(".dsh/credentials.json", "c.json")));
        Assert.Equal(WorkspaceErrors.Protected, Code(() => _p.Rename("proj", ".dsh")));
        Assert.Equal(WorkspaceErrors.NotSupported, Code(() => _p.Rename("", "x")));
        Assert.Equal(WorkspaceErrors.InvalidName, Code(() => _p.Rename("proj/说明.md", "../escape.md")));
        Assert.Equal(WorkspaceErrors.NotFound, Code(() => _p.Rename("proj/nope", "x")));
        NoLeftovers(_root);
    }

    // ── write (editor saves) ──────────────────────────────────────────────

    private async Task<FileVersion> Save(string path, string text, FileVersion? expected) =>
        await _p.WriteAsync(path, new MemoryStream(Encoding.UTF8.GetBytes(text)), expected, CancellationToken.None);

    [Fact]
    public async Task Write_CreatesNew_ReplacesTheExpectedVersion_AndRefusesAChangedFile()
    {
        var v1 = await Save("proj/new.txt", "one", null);
        Assert.Equal(WorkspaceErrors.Exists, (await Assert.ThrowsAsync<WorkspaceException>(() => Save("proj/new.txt", "x", null))).Code);
        Assert.Equal(_p.Version("proj/new.txt"), v1);
        var v2 = await Save("proj/new.txt", "two", v1);
        Assert.Equal("two", File.ReadAllText(Host("proj/new.txt")));
        Assert.NotEqual(v1.Sha256, v2.Sha256);

        // Someone else writes the same length (mtime may not even move): the hash still catches it.
        File.WriteAllText(Host("proj/new.txt"), "TWO");
        var conflict = await Assert.ThrowsAsync<WorkspaceException>(() => Save("proj/new.txt", "mine", v2));
        Assert.Equal(WorkspaceErrors.Conflict, conflict.Code);
        Assert.Equal("TWO", File.ReadAllText(Host("proj/new.txt"))); // not overwritten

        File.Delete(Host("proj/new.txt"));
        Assert.Equal(WorkspaceErrors.Conflict, (await Assert.ThrowsAsync<WorkspaceException>(() => Save("proj/new.txt", "mine", v2))).Code);
        Assert.False(File.Exists(Host("proj/new.txt")));
        NoLeftovers(_root);
    }

    [Fact]
    public async Task Write_KeepsTheUnixMode_AndRefusesProtectedOrLinkedTargets()
    {
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode exec = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
            File.SetUnixFileMode(Host("proj/src/main.py"), exec);
            await Save("proj/src/main.py", "print(2)\n", _p.Version("proj/src/main.py"));
            Assert.Equal(exec, File.GetUnixFileMode(Host("proj/src/main.py")));
        }
        Assert.Equal(WorkspaceErrors.Protected, (await Assert.ThrowsAsync<WorkspaceException>(() => Save(".dsh/credentials.json", "{}", null))).Code);
        Assert.Equal(WorkspaceErrors.Protected, Code(() => _p.Version(".dsh/credentials.json")));
        Assert.Equal(WorkspaceErrors.NotAFile, (await Assert.ThrowsAsync<WorkspaceException>(() => Save("proj/src", "x", null))).Code);
        if (TryLinkFile(Host("proj/link.txt"), Path.Combine(_outside, "secret.txt")))
        {
            Assert.Equal(WorkspaceErrors.ThroughSymlink, (await Assert.ThrowsAsync<WorkspaceException>(() => Save("proj/link.txt", "pwned", null))).Code);
            Assert.Equal("host secret", File.ReadAllText(Path.Combine(_outside, "secret.txt")));
        }
        if (TryLinkDirectory(Host("out"), _outside))
        {
            Assert.Equal(WorkspaceErrors.ThroughSymlink, (await Assert.ThrowsAsync<WorkspaceException>(() => Save("out/secret.txt", "pwned", null))).Code);
            Assert.Equal("host secret", File.ReadAllText(Path.Combine(_outside, "secret.txt")));
        }
    }

    // ── copy / move ───────────────────────────────────────────────────────

    [Fact]
    public void Copy_Trees_LeavesLinksOut_NeverOverwrites_AndReportsPerItem()
    {
        var linked = TryLinkDirectory(Host("proj/src/escape"), _outside);
        var r = _p.Transfer(["proj/src", "proj/readme.md", "proj/missing", ".dsh/credentials.json"], "", copy: true);
        Assert.Equal(["src", "readme.md"], r.Created);
        Assert.Equal([("proj/missing", WorkspaceErrors.NotFound), (".dsh/credentials.json", WorkspaceErrors.Protected)], r.Failed.Select(f => (f.Path, f.Code)));
        Assert.Equal(linked ? 1 : 0, r.SkippedLinks);
        Assert.Equal("print(1)\n", File.ReadAllText(Host("src/main.py")));
        Assert.False(Directory.Exists(Host("src/escape")));
        Assert.Equal("host secret", File.ReadAllText(Path.Combine(_outside, "secret.txt")));

        var again = _p.Transfer(["proj/readme.md", "proj/readme.md"], "", copy: true);
        Assert.Equal(["readme (1).md", "readme (2).md"], again.Created);
        Assert.Equal("# hi", File.ReadAllText(Host("readme.md"))); // original untouched
        NoLeftovers(_root);
    }

    [Fact]
    public void Copy_AndMove_RefuseIntoItself_IntoDsh_AndThroughLinks()
    {
        Assert.Equal(WorkspaceErrors.IntoItself, _p.Transfer(["proj"], "proj/src", copy: true).Failed.Single().Code);
        Assert.Equal(WorkspaceErrors.IntoItself, _p.Transfer(["proj"], "proj", copy: false).Failed.Single().Code);
        Assert.Equal(WorkspaceErrors.Protected, Code(() => _p.Transfer(["proj/readme.md"], ".dsh", copy: true)));
        Assert.Equal(WorkspaceErrors.Protected, Code(() => _p.Transfer(["proj/readme.md"], ".dsh/profiles", copy: false)));
        if (TryLinkDirectory(Host("out"), _outside))
        {
            Assert.Equal(WorkspaceErrors.ThroughSymlink, _p.Transfer(["out/secret.txt"], "", copy: true).Failed.Single().Code);
            Assert.Equal(WorkspaceErrors.ThroughSymlink, Code(() => _p.Transfer(["proj/readme.md"], "out", copy: false)));
            Assert.False(File.Exists(Path.Combine(_outside, "readme.md")));
        }
    }

    [Fact]
    public void Move_RenamesAtomically_SameDirectoryIsANoOp_CollisionsGetASuffix()
    {
        _p.CreateDirectory("", "dest");
        File.WriteAllText(Host("dest/readme.md"), "theirs");
        var r = _p.Transfer(["proj/readme.md", "proj/src"], "dest", copy: false);
        Assert.Equal(["dest/readme (1).md", "dest/src"], r.Created);
        Assert.Equal("theirs", File.ReadAllText(Host("dest/readme.md")));
        Assert.False(File.Exists(Host("proj/readme.md")));
        Assert.Equal(["dest/src"], _p.Transfer(["dest/src"], "dest", copy: false).Created);
        if (TryLinkDirectory(Host("dest/link"), _outside))
        {
            Assert.Equal(["link"], _p.Transfer(["dest/link"], "", copy: false).Created); // the link moves, not its target
            Assert.NotNull(new DirectoryInfo(Host("link")).LinkTarget);
            Assert.True(File.Exists(Path.Combine(_outside, "secret.txt")));
        }
    }

    // ── delete / trash / restore ──────────────────────────────────────────

    [Fact]
    public void Delete_GoesToTheTrash_AndRestoresWhereItWas()
    {
        var receipt = _p.Delete(["proj/readme.md", "proj/src", "proj/nope"]);
        Assert.True(receipt.Restorable);
        Assert.Equal(["proj/readme.md", "proj/src"], receipt.Deleted);
        Assert.Equal(WorkspaceErrors.NotFound, receipt.Failed.Single().Code);
        Assert.False(File.Exists(Host("proj/readme.md")));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories),
            p => p.Contains("trash", StringComparison.OrdinalIgnoreCase)); // the trash is not in the workspace

        File.WriteAllText(Host("proj/readme.md"), "new one"); // the name was taken meanwhile
        var restored = _p.Restore(receipt);
        Assert.Equal(["proj/readme (1).md", "proj/src"], restored);
        Assert.Equal("# hi", File.ReadAllText(Host("proj/readme (1).md")));
        Assert.Equal("print(1)\n", File.ReadAllText(Host("proj/src/main.py")));
        Assert.Equal(WorkspaceErrors.TrashGone, Code(() => _p.Restore(receipt)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_trash));
    }

    [Fact]
    public void Restore_IntoAVanishedFolder_LandsAtTheRoot()
    {
        var receipt = _p.Delete(["proj/src/main.py"]);
        _p.Delete(["proj"]);
        Assert.Equal(["main.py"], _p.Restore(receipt));
        Assert.Equal("print(1)\n", File.ReadAllText(Host("main.py")));
    }

    [Fact]
    public void Delete_RefusesTheRootAndDsh_RemovesLinksNotTargets_AndIsPermanentWithoutATrash()
    {
        Assert.Equal(WorkspaceErrors.NotSupported, _p.Delete([""]).Failed.Single().Code);
        Assert.Equal(WorkspaceErrors.Protected, _p.Delete([".dsh"]).Failed.Single().Code);
        Assert.Equal(WorkspaceErrors.Protected, _p.Delete([".dsh/credentials.json"]).Failed.Single().Code);
        Assert.True(File.Exists(Host(".dsh/credentials.json")));
        var noTrash = new WorkspaceProvider(new WorkspaceFileSystem(_root), null);
        if (TryLinkDirectory(Host("proj/escape"), _outside))
        {
            var r = noTrash.Delete(["proj/escape"]);
            Assert.False(r.Restorable);
            Assert.Empty(r.Failed);
            Assert.False(Directory.Exists(Host("proj/escape")));
            Assert.Equal("host secret", File.ReadAllText(Path.Combine(_outside, "secret.txt")));
            // a link inside a deleted tree is unlinked, never descended into
            TryLinkDirectory(Host("proj/src/escape"), _outside);
            noTrash.Delete(["proj/src"]);
            Assert.Equal("host secret", File.ReadAllText(Path.Combine(_outside, "secret.txt")));
        }
        var gone = noTrash.Delete(["proj/readme.md"]);
        Assert.False(gone.Restorable);
        Assert.False(File.Exists(Host("proj/readme.md")));
        Assert.Equal(WorkspaceErrors.NotSupported, Code(() => noTrash.Restore(gone)));
    }

    [Fact]
    public void Restore_RefusesForgedBatchIds_AndUnreadableManifests()
    {
        foreach (var id in new[] { "../outside", "a/b", "..\\x" })
        {
            Assert.Equal(WorkspaceErrors.InvalidName, Code(() => _p.Restore(new DeleteReceipt(id, [], [], true))));
        }
        var receipt = _p.Delete(["proj/readme.md"]);
        File.WriteAllText(Path.Combine(_trash, receipt.Id, "manifest.json"), "{ not json");
        Assert.Equal(WorkspaceErrors.TrashGone, Code(() => _p.Restore(receipt)));
    }

    // ── many files ────────────────────────────────────────────────────────

    [Fact]
    public void TensOfThousandsOfFiles_ListSortFilterSearch_InReasonableTime()
    {
        var big = Directory.CreateDirectory(Host("big")).FullName;
        for (var i = 0; i < 30_000; i++)
        {
            File.WriteAllBytes(Path.Combine(big, $"f{i}.txt"), []);
        }
        var sw = Stopwatch.StartNew();
        var list = _p.FileSystem.List("big");
        var sorted = FileListing.Sort(list, FileSort.Name, descending: false);
        var filtered = FileListing.Filter(sorted, "F2999");
        var found = FileListing.Search(_p.FileSystem, "", "f29999");
        sw.Stop();
        Assert.Equal(30_000, list.Count);
        Assert.Equal(["f0.txt", "f1.txt", "f2.txt"], sorted.Take(3).Select(e => e.Name)); // natural order
        Assert.Equal("f29999.txt", sorted[^1].Name);
        Assert.Equal(11, filtered.Count); // f2999, f29990..f29999
        Assert.Equal("big/f29999.txt", found.Matches.Single().RelativePath);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"took {sw.Elapsed}");
    }
}
