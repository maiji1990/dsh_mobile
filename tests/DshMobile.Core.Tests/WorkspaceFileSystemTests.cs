using System.Diagnostics;
using System.Text;
using DshMobile.Core.Files;

namespace DshMobile.Core.Tests;

public sealed class WorkspaceFileSystemTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "dshm-ws-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _outside;
    private readonly WorkspaceFileSystem _fs;

    public WorkspaceFileSystemTests()
    {
        _root = Directory.CreateDirectory(Path.Combine(_base, "root")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_base, "outside")).FullName;
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "host secret");
        Directory.CreateDirectory(Path.Combine(_root, "proj", "src"));
        File.WriteAllText(Path.Combine(_root, "proj", "readme.md"), "# hi");
        Directory.CreateDirectory(Path.Combine(_root, ".dsh", "profiles"));
        File.WriteAllText(Path.Combine(_root, ".dsh", "credentials.json"), "{\"key\":\"sk-xxx\"}");
        _fs = new WorkspaceFileSystem(_root);
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

    /// <summary>Directory symlink, or a junction when symlinks need privileges (Windows without Developer Mode).</summary>
    private bool TryLinkDirectory(string link, string target)
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
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
        p!.WaitForExit();
        return p.ExitCode == 0 && new DirectoryInfo(link).LinkTarget is not null;
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("proj", "proj")]
    [InlineData("proj/./src/", "proj/src")]
    [InlineData("proj\\src", "proj/src")]
    public void Normalize_Accepts(string input, string expected) =>
        Assert.Equal(expected, WorkspaceFileSystem.NormalizeRelative(input));

    [Theory]
    [InlineData("../x", WorkspaceErrors.OutsideRoot)]
    [InlineData("proj/../../x", WorkspaceErrors.OutsideRoot)]
    [InlineData("/etc/passwd", WorkspaceErrors.OutsideRoot)]
    [InlineData("C:/Windows", WorkspaceErrors.OutsideRoot)]
    [InlineData("a\0b", WorkspaceErrors.InvalidName)]
    public void Normalize_Rejects(string input, string code) =>
        Assert.Equal(code, Assert.Throws<WorkspaceException>(() => WorkspaceFileSystem.NormalizeRelative(input)).Code);

    [Fact]
    public void List_DirectoriesFirst_AndMarksProtected()
    {
        var root = _fs.List("");
        Assert.Equal([".dsh", "proj"], root.Select(e => e.Name));
        Assert.True(root.Single(e => e.Name == ".dsh").Protected);
        Assert.False(root.Single(e => e.Name == "proj").Protected);

        var proj = _fs.List("proj");
        Assert.Equal(WorkspaceEntryKind.Directory, proj[0].Kind);
        Assert.Equal("proj/src", proj[0].RelativePath);
        Assert.Equal(4, proj.Single(e => e.Name == "readme.md").Size);
    }

    [Fact]
    public void OpenRead_ProtectedTooLargeAndMissing()
    {
        Assert.Equal(WorkspaceErrors.Protected, Assert.Throws<WorkspaceException>(() => _fs.OpenRead(".dsh/credentials.json")).Code);
        Assert.Equal(WorkspaceErrors.TooLarge, Assert.Throws<WorkspaceException>(() => _fs.OpenRead("proj/readme.md", maxBytes: 2)).Code);
        Assert.Equal(WorkspaceErrors.NotFound, Assert.Throws<WorkspaceException>(() => _fs.OpenRead("proj/nope.txt")).Code);
        Assert.Equal(WorkspaceErrors.NotAFile, Assert.Throws<WorkspaceException>(() => _fs.OpenRead("proj/src")).Code);
        using var s = _fs.OpenRead("proj/readme.md");
        Assert.Equal("# hi", new StreamReader(s).ReadToEnd());
    }

    [Fact]
    public void Symlinks_AreListedButNeverTraversed()
    {
        var link = Path.Combine(_root, "proj", "escape");
        Assert.True(TryLinkDirectory(link, _outside), "could not create a symlink or junction on this host");

        var entry = _fs.List("proj").Single(e => e.Name == "escape");
        Assert.Equal(WorkspaceEntryKind.Symlink, entry.Kind);
        Assert.NotNull(entry.LinkTarget);

        Assert.Equal(WorkspaceErrors.ThroughSymlink, Assert.Throws<WorkspaceException>(() => _fs.List("proj/escape")).Code);
        Assert.Equal(WorkspaceErrors.ThroughSymlink, Assert.Throws<WorkspaceException>(() => _fs.OpenRead("proj/escape/secret.txt")).Code);
    }

    [Fact]
    public async Task Import_WritesAtomically_NeverOverwrites_RejectsProtectedAndLinks()
    {
        var rel = await _fs.ImportAsync("proj", "notes.txt", new MemoryStream(Encoding.UTF8.GetBytes("hello")));
        Assert.Equal("proj/notes.txt", rel);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(_root, "proj", "notes.txt")));
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(_root, "proj")), f => f.EndsWith(".part", StringComparison.Ordinal));

        Assert.Equal(WorkspaceErrors.Exists, (await Assert.ThrowsAsync<WorkspaceException>(
            () => _fs.ImportAsync("proj", "notes.txt", new MemoryStream([1])))).Code);
        Assert.Equal(WorkspaceErrors.Protected, (await Assert.ThrowsAsync<WorkspaceException>(
            () => _fs.ImportAsync(".dsh", "x.json", new MemoryStream([1])))).Code);
        // Display names from the picker are reduced to one safe component.
        Assert.Equal("proj/evil.sh", await _fs.ImportAsync("proj", "../../evil.sh", new MemoryStream([1])));

        Assert.True(TryLinkDirectory(Path.Combine(_root, "proj", "lnk"), _outside), "could not create a symlink or junction on this host");
        Assert.Equal(WorkspaceErrors.ThroughSymlink, (await Assert.ThrowsAsync<WorkspaceException>(
            () => _fs.ImportAsync("proj/lnk", "x.txt", new MemoryStream([1])))).Code);
        Assert.False(File.Exists(Path.Combine(_outside, "x.txt")));
    }

    [Fact]
    public async Task Import_FailedCopy_LeavesNoPartialFile()
    {
        await Assert.ThrowsAsync<IOException>(() => _fs.ImportAsync("proj", "broken.bin", new FailingStream()));
        Assert.False(File.Exists(Path.Combine(_root, "proj", "broken.bin")));
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(_root, "proj")), f => f.EndsWith(".part", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("a.txt", "a.txt")]
    [InlineData("dir/b.txt", "b.txt")]
    [InlineData("  c.txt ", "c.txt")]
    [InlineData("d\u0001e.txt", "de.txt")]
    public void SanitizeFileName_Works(string input, string expected) =>
        Assert.Equal(expected, WorkspaceFileSystem.SanitizeFileName(input));

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("dir/")]
    public void SanitizeFileName_RejectsEmptyAndDots(string input) =>
        Assert.Equal(WorkspaceErrors.InvalidName, Assert.Throws<WorkspaceException>(() => WorkspaceFileSystem.SanitizeFileName(input)).Code);

    private sealed class FailingStream : Stream
    {
        private int _reads;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            _reads++ == 0 ? Fill(buffer, offset, count) : throw new IOException("source went away");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _reads++ == 0 ? ValueTask.FromResult(Fill(buffer.Span)) : throw new IOException("source went away");
        private static int Fill(byte[] b, int o, int c) { b.AsSpan(o, Math.Min(c, 16)).Fill(1); return Math.Min(c, 16); }
        private static int Fill(Span<byte> b) { var n = Math.Min(b.Length, 16); b[..n].Fill(1); return n; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
