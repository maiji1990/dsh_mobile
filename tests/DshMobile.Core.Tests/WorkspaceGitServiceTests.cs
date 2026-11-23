using System.Diagnostics;
using DshMobile.Core.Files;

namespace DshMobile.Core.Tests;

public sealed class WorkspaceGitServiceTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "dshm-git-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _outside;
    private readonly WorkspaceFileSystem _fs;

    public WorkspaceGitServiceTests()
    {
        _root = Directory.CreateDirectory(Path.Combine(_base, "workspace")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_base, "outside")).FullName;
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "outside-secret");
        Directory.CreateDirectory(Path.Combine(_root, "repo", "src"));
        File.WriteAllText(Path.Combine(_root, "repo", "src", "app.cs"), "Console.WriteLine(1);\n");
        Directory.CreateDirectory(Path.Combine(_root, ".dsh", "profiles"));
        File.WriteAllText(Path.Combine(_root, ".dsh", "credentials.yaml"), "DEEPSEEK_API_KEY: sk-test\n");
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

    private static bool TryLinkDirectory(string link, string target)
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
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        });
        p!.WaitForExit();
        return p.ExitCode == 0 && new DirectoryInfo(link).LinkTarget is not null;
    }

    private sealed class FakeGitRunner(Func<GitCommandRequest, GitCommandOutput> handler) : IWorkspaceGitCommandRunner
    {
        public List<GitCommandRequest> Requests { get; } = [];

        public Task<GitCommandOutput> RunAsync(GitCommandRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(handler(request));
        }
    }

    [Fact]
    public async Task GetStatusAsync_ReturnsBranchAndChanges_AndStrictlyFiltersDotDsh()
    {
        var runner = new FakeGitRunner(req =>
        {
            if (req.Arguments.Contains("rev-parse"))
            {
                return new GitCommandOutput(true, 0, "/root/projects/repo\n");
            }
            if (req.Arguments.Contains("symbolic-ref"))
            {
                return new GitCommandOutput(true, 0, "feat/pr06\n");
            }
            if (req.Arguments.Contains("status"))
            {
                // 混合 .dsh/ 保护条目、修改、重命名与未跟踪文件
                var payload = string.Join('\0',
                    " M .dsh/credentials.yaml",
                    " M src/app.cs",
                    "R  new.cs",
                    "old.cs",
                    "?? README.md") + "\0";
                return new GitCommandOutput(true, 0, payload);
            }
            return new GitCommandOutput(false, 1, "unexpected");
        });

        var svc = new WorkspaceGitService(_fs, runner);
        var status = await svc.GetStatusAsync("repo");

        Assert.True(status.IsRepository);
        Assert.Equal("repo", status.RepoRelativeRoot);
        Assert.Equal("feat/pr06", status.BranchName);
        Assert.False(status.IsDetachedHead);
        Assert.Equal(3, status.Changes.Count);
        Assert.DoesNotContain(status.Changes, c => c.RelativePath.StartsWith(".dsh", StringComparison.Ordinal));
        Assert.Equal("src/app.cs", status.Changes[0].RelativePath);
        Assert.Equal(GitChangeKind.Modified, status.Changes[0].Kind);
        Assert.Equal("new.cs", status.Changes[1].RelativePath);
        Assert.Equal("old.cs", status.Changes[1].PreviousRelativePath);
        Assert.Equal(GitChangeKind.Renamed, status.Changes[1].Kind);
        Assert.Equal("README.md", status.Changes[2].RelativePath);
        Assert.Equal(GitChangeKind.Untracked, status.Changes[2].Kind);
    }

    [Theory]
    [InlineData(".dsh", WorkspaceErrors.Protected)]
    [InlineData(".dsh/profiles", WorkspaceErrors.Protected)]
    [InlineData("../outside", WorkspaceErrors.OutsideRoot)]
    [InlineData("repo/../../outside", WorkspaceErrors.OutsideRoot)]
    [InlineData("/etc", WorkspaceErrors.OutsideRoot)]
    public async Task GetStatusAsync_RejectsProtectedOrEscapingDirectory_BeforeRunningCommand(string dir, string expectedCode)
    {
        var runner = new FakeGitRunner(_ => throw new InvalidOperationException("runner must not be called"));
        var svc = new WorkspaceGitService(_fs, runner);

        var ex = await Assert.ThrowsAsync<WorkspaceException>(() => svc.GetStatusAsync(dir));
        Assert.Equal(expectedCode, ex.Code);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task GetStatusAsync_RejectsRepoRootEscapingWorkspace()
    {
        var runner = new FakeGitRunner(_ => new GitCommandOutput(true, 0, "/root\n"));
        var svc = new WorkspaceGitService(_fs, runner);

        var ex = await Assert.ThrowsAsync<WorkspaceException>(() => svc.GetStatusAsync("repo"));
        Assert.Equal(WorkspaceErrors.OutsideRoot, ex.Code);
    }

    [Fact]
    public async Task GetStatusAsync_RejectsSymlinkDirectory_AndEscapingGitdirFile()
    {
        var linkPath = Path.Combine(_root, "repo", "escape_link");
        Assert.True(TryLinkDirectory(linkPath, _outside));

        var runner = new FakeGitRunner(_ => throw new InvalidOperationException("runner must not be called"));
        var svc = new WorkspaceGitService(_fs, runner);

        var linkEx = await Assert.ThrowsAsync<WorkspaceException>(() => svc.GetStatusAsync("repo/escape_link"));
        Assert.Equal(WorkspaceErrors.ThroughSymlink, linkEx.Code);

        // .git 指针文件指向工作区外也必须拒绝
        var badSub = Directory.CreateDirectory(Path.Combine(_root, "repo", "sub")).FullName;
        File.WriteAllText(Path.Combine(badSub, ".git"), "gitdir: /etc/evil-git\n");
        var gitdirEx = await Assert.ThrowsAsync<WorkspaceException>(() => svc.GetStatusAsync("repo/sub"));
        Assert.Equal(WorkspaceErrors.OutsideRoot, gitdirEx.Code);
    }

    [Theory]
    [InlineData(".dsh/credentials.yaml", WorkspaceErrors.Protected)]
    [InlineData(".dsh", WorkspaceErrors.Protected)]
    [InlineData("../outside/secret.txt", WorkspaceErrors.OutsideRoot)]
    [InlineData("/etc/passwd", WorkspaceErrors.OutsideRoot)]
    public async Task GetFileDiffAsync_RejectsProtectedAndEscapingPaths(string file, string expectedCode)
    {
        var runner = new FakeGitRunner(req =>
            req.Arguments.Contains("rev-parse")
                ? new GitCommandOutput(true, 0, "/root/projects/repo\n")
                : throw new InvalidOperationException("diff must not be called"));
        var svc = new WorkspaceGitService(_fs, runner);

        var ex = await Assert.ThrowsAsync<WorkspaceException>(() => svc.GetFileDiffAsync("repo", file));
        Assert.Equal(expectedCode, ex.Code);
    }

    [Fact]
    public async Task GetFileDiffAsync_RejectsSymlinkTarget()
    {
        var linkDir = Path.Combine(_root, "repo", "linked");
        Assert.True(TryLinkDirectory(linkDir, _outside));

        var runner = new FakeGitRunner(req =>
            req.Arguments.Contains("rev-parse")
                ? new GitCommandOutput(true, 0, "/root/projects/repo\n")
                : throw new InvalidOperationException("diff must not be called"));
        var svc = new WorkspaceGitService(_fs, runner);

        var ex = await Assert.ThrowsAsync<WorkspaceException>(() => svc.GetFileDiffAsync("repo", "linked/secret.txt"));
        Assert.Equal(WorkspaceErrors.ThroughSymlink, ex.Code);
    }

    [Fact]
    public void ParseUnifiedDiff_ComputesLineNumbers_AndDetectsBinary()
    {
        const string diff = """
            diff --git a/src/app.cs b/src/app.cs
            index 1111111..2222222 100644
            --- a/src/app.cs
            +++ b/src/app.cs
            @@ -10,3 +10,4 @@
             context
            -removed
            +added1
            +added2
            """;

        var doc = WorkspaceGitService.ParseUnifiedDiff("src/app.cs", diff);
        Assert.False(doc.IsBinary);
        Assert.False(doc.IsTruncated);
        Assert.Equal(9, doc.Lines.Count);

        var ctxLine = doc.Lines[5];
        Assert.Equal(GitDiffLineKind.Context, ctxLine.Kind);
        Assert.Equal(10, ctxLine.OldLineNumber);
        Assert.Equal(10, ctxLine.NewLineNumber);

        var delLine = doc.Lines[6];
        Assert.Equal(GitDiffLineKind.Removed, delLine.Kind);
        Assert.Equal(11, delLine.OldLineNumber);
        Assert.Null(delLine.NewLineNumber);

        var addLine1 = doc.Lines[7];
        Assert.Equal(GitDiffLineKind.Added, addLine1.Kind);
        Assert.Null(addLine1.OldLineNumber);
        Assert.Equal(11, addLine1.NewLineNumber);

        var addLine2 = doc.Lines[8];
        Assert.Equal(GitDiffLineKind.Added, addLine2.Kind);
        Assert.Null(addLine2.OldLineNumber);
        Assert.Equal(12, addLine2.NewLineNumber);

        var binDoc = WorkspaceGitService.ParseUnifiedDiff("a.png", "Binary files a/a.png and b/a.png differ\n");
        Assert.True(binDoc.IsBinary);
        Assert.Empty(binDoc.Lines);
    }

    [Fact]
    public void ParseUnifiedDiff_EnforcesMaxLinesAndMaxBytes_WithTruncationNotice()
    {
        var manyLines = string.Join('\n', Enumerable.Range(1, 50).Select(i => $"+line {i}"));
        var raw = "@@ -0,0 +1,50 @@\n" + manyLines + "\n";

        var lineLimited = WorkspaceGitService.ParseUnifiedDiff("big.txt", raw, new GitDiffLimits(MaxBytes: 1024 * 1024, MaxLines: 10));
        Assert.True(lineLimited.IsTruncated);
        Assert.Equal(10, lineLimited.DisplayedLines);
        Assert.Equal(GitDiffLineKind.TruncationNotice, lineLimited.Lines[^1].Kind);

        var byteLimited = WorkspaceGitService.ParseUnifiedDiff("big.txt", raw, new GitDiffLimits(MaxBytes: 64, MaxLines: 500));
        Assert.True(byteLimited.IsTruncated);
        Assert.True(byteLimited.DisplayedLines < 51);
        Assert.Equal(GitDiffLineKind.TruncationNotice, byteLimited.Lines[^1].Kind);
    }

    [Fact]
    public async Task GetFileDiffAsync_UntrackedFile_ReadsSafelyAndTruncatesOversized()
    {
        var bigPath = Path.Combine(_root, "repo", "notes.txt");
        File.WriteAllLines(bigPath, Enumerable.Range(1, 30).Select(i => $"note line {i}"));

        var runner = new FakeGitRunner(req =>
        {
            if (req.Arguments.Contains("rev-parse"))
            {
                return new GitCommandOutput(true, 0, "/root/projects/repo\n");
            }
            if (req.Arguments.Contains("diff"))
            {
                return new GitCommandOutput(true, 0, string.Empty);
            }
            if (req.Arguments.Contains("status"))
            {
                return new GitCommandOutput(true, 0, "?? notes.txt\0");
            }
            return new GitCommandOutput(false, 1, "unexpected");
        });

        var svc = new WorkspaceGitService(_fs, runner, limits: new GitDiffLimits(MaxBytes: 64 * 1024, MaxLines: 12));
        var doc = await svc.GetFileDiffAsync("repo", "notes.txt");

        Assert.False(doc.IsBinary);
        Assert.True(doc.IsTruncated);
        Assert.Equal(GitDiffLineKind.TruncationNotice, doc.Lines[^1].Kind);
        Assert.Contains(doc.Lines, l => l.Kind == GitDiffLineKind.Added && l.Content == "note line 1" && l.NewLineNumber == 1);
    }
}
