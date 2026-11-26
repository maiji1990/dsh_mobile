using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DshMobile.Core.Files;

/// <summary>Git 只读服务稳定错误码（不伪造成功）。</summary>
public static class GitErrors
{
    public const string NotARepository = "GIT_NOT_A_REPOSITORY";
    public const string GitUnavailable = "GIT_UNAVAILABLE";
    public const string CommandFailed = "GIT_COMMAND_FAILED";
    public const string Timeout = "GIT_TIMEOUT";
    public const string BinaryFile = "GIT_BINARY_FILE";
}

/// <summary>传给只读 Git 命令执行器的单次调用请求。</summary>
public sealed record GitCommandRequest(
    string GuestWorkingDirectory,
    IReadOnlyList<string> Arguments);

/// <summary>Git 命令执行结果。</summary>
public sealed record GitCommandOutput(
    bool Ok,
    int? ExitCode,
    string StdoutAndError,
    string? ErrorCode = null);

/// <summary>可注入的只读 Git 命令执行抽象（生产环境走 PRoot + GuestCommandLine，桌面测试可离线注入假实现）。</summary>
public interface IWorkspaceGitCommandRunner
{
    Task<GitCommandOutput> RunAsync(GitCommandRequest request, CancellationToken ct);
}

/// <summary>工作区文件 Git 变更类型。</summary>
public enum GitChangeKind
{
    Modified,
    Added,
    Deleted,
    Renamed,
    Copied,
    Untracked,
    TypeChanged,
    Unmerged,
    Other,
}

/// <summary>Git 变更文件列表单条记录。</summary>
public sealed record GitChangeEntry(
    string RelativePath,
    string? PreviousRelativePath,
    GitChangeKind Kind,
    string PorcelainCode,
    bool IsStaged,
    bool IsUnstaged);

/// <summary>工作区 Git 仓库状态快照。</summary>
public sealed record GitStatusResult(
    bool IsRepository,
    string RepoRelativeRoot,
    string? BranchName,
    bool IsDetachedHead,
    IReadOnlyList<GitChangeEntry> Changes,
    bool Truncated,
    int TotalChangeCount,
    string? ErrorCode = null,
    string? ErrorMessage = null);

/// <summary>Unified Diff 单行类别。</summary>
public enum GitDiffLineKind
{
    Meta,
    HunkHeader,
    Context,
    Added,
    Removed,
    TruncationNotice,
}

/// <summary>预解析完成的 Unified Diff 行（UI 主线程零解析直接绑定）。</summary>
public sealed record GitDiffLine(
    GitDiffLineKind Kind,
    int? OldLineNumber,
    int? NewLineNumber,
    string Marker,
    string Content);

/// <summary>Diff 输出大小与行数上限。</summary>
public sealed record GitDiffLimits(
    int MaxBytes = 128 * 1024,
    int MaxLines = 1500,
    int MaxLineLength = 2000,
    int MaxStatusEntries = 500)
{
    public static readonly GitDiffLimits Default = new();
}

/// <summary>单文件 Unified Diff 解析结果（含截断状态与行号）。</summary>
public sealed record GitDiffDocument(
    string FileRelativePath,
    bool IsBinary,
    bool IsTruncated,
    string? TruncationReason,
    int TotalLinesObserved,
    int DisplayedLines,
    IReadOnlyList<GitDiffLine> Lines);

/// <summary>
/// 只读工作区 Git 状态与 Unified Diff 服务（N-06-01）：
/// 严格复用 <see cref="WorkspaceFileSystem"/> 的路径规范化、`.dsh/` 双向不可触与符号链接拒绝策略，
/// 校验仓库根不可逃逸工作区、强制限制 Diff 输出大小与行数并明确标注截断。
/// </summary>
public sealed partial class WorkspaceGitService
{
    private readonly WorkspaceFileSystem _fs;
    private readonly IWorkspaceGitCommandRunner _runner;
    private readonly string _guestWorkspaceRoot;
    private readonly GitDiffLimits _limits;

    public WorkspaceGitService(
        WorkspaceFileSystem fileSystem,
        IWorkspaceGitCommandRunner runner,
        string guestWorkspaceRoot = "/root/projects",
        GitDiffLimits? limits = null)
    {
        _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _guestWorkspaceRoot = NormalizeGuestRoot(guestWorkspaceRoot);
        _limits = limits ?? GitDiffLimits.Default;
    }

    /// <summary>查询当前目录所属 Git 仓库的分支与变更文件清单（只读）。</summary>
    public async Task<GitStatusResult> GetStatusAsync(string? repoRelativeDir = "", CancellationToken ct = default)
    {
        var (repoRelRoot, guestRepoDir, probeErrorCode, probeErrorMsg) = await ResolveRepoRootAsync(repoRelativeDir, ct).ConfigureAwait(false);
        if (probeErrorCode is not null)
        {
            return new GitStatusResult(
                IsRepository: false,
                RepoRelativeRoot: repoRelRoot,
                BranchName: null,
                IsDetachedHead: false,
                Changes: [],
                Truncated: false,
                TotalChangeCount: 0,
                ErrorCode: probeErrorCode,
                ErrorMessage: probeErrorMsg);
        }

        // 查询当前分支名（兼容无提交的初始仓库与 detached HEAD）
        var branchOutput = await _runner.RunAsync(
            new GitCommandRequest(guestRepoDir, BuildGitArgs(guestRepoDir, ["symbolic-ref", "--quiet", "--short", "HEAD"])),
            ct).ConfigureAwait(false);

        string? branchName = null;
        var isDetached = false;
        if (branchOutput.Ok)
        {
            var trimmed = FirstNonEmptyLine(branchOutput.StdoutAndError);
            if (!string.IsNullOrEmpty(trimmed))
            {
                branchName = trimmed;
            }
        }
        else
        {
            var revParseHead = await _runner.RunAsync(
                new GitCommandRequest(guestRepoDir, BuildGitArgs(guestRepoDir, ["rev-parse", "--short", "HEAD"])),
                ct).ConfigureAwait(false);
            if (revParseHead.Ok)
            {
                isDetached = true;
                branchName = FirstNonEmptyLine(revParseHead.StdoutAndError);
            }
            else
            {
                branchName = "HEAD (unborn)";
            }
        }

        var statusOutput = await _runner.RunAsync(
            new GitCommandRequest(
                guestRepoDir,
                BuildGitArgs(guestRepoDir, ["-c", "core.quotepath=false", "status", "--porcelain=v1", "-z", "-uall"])),
            ct).ConfigureAwait(false);

        if (!statusOutput.Ok)
        {
            var code = ClassifyGitFailure(statusOutput, GitErrors.CommandFailed);
            return new GitStatusResult(
                IsRepository: code != GitErrors.NotARepository && code != GitErrors.GitUnavailable,
                RepoRelativeRoot: repoRelRoot,
                BranchName: branchName,
                IsDetachedHead: isDetached,
                Changes: [],
                Truncated: false,
                TotalChangeCount: 0,
                ErrorCode: code,
                ErrorMessage: FirstNonEmptyLine(statusOutput.StdoutAndError));
        }

        var allChanges = ParsePorcelainStatus(statusOutput.StdoutAndError, repoRelRoot);
        var truncated = allChanges.Count > _limits.MaxStatusEntries;
        IReadOnlyList<GitChangeEntry> shown = truncated ? allChanges.Take(_limits.MaxStatusEntries).ToArray() : allChanges;
        return new GitStatusResult(
            IsRepository: true,
            RepoRelativeRoot: repoRelRoot,
            BranchName: branchName,
            IsDetachedHead: isDetached,
            Changes: shown,
            Truncated: truncated,
            TotalChangeCount: allChanges.Count);
    }

    /// <summary>查询单个工作区文件的 Unified Diff（只读，拒绝 `.dsh/`、越界与符号链接，强制大小与行数上限）。</summary>
    public async Task<GitDiffDocument> GetFileDiffAsync(
        string? repoRelativeDir,
        string fileRelativePath,
        CancellationToken ct = default)
    {
        var (repoRelRoot, guestRepoDir, probeErrorCode, probeErrorMsg) = await ResolveRepoRootAsync(repoRelativeDir, ct).ConfigureAwait(false);
        if (probeErrorCode is not null)
        {
            throw new WorkspaceException(probeErrorCode, probeErrorMsg ?? probeErrorCode);
        }

        var (fileNorm, wsRel, hostPath) = ValidateTargetFile(repoRelRoot, fileRelativePath);
        if (Directory.Exists(hostPath))
        {
            throw new WorkspaceException(WorkspaceErrors.NotAFile, wsRel);
        }

        // 先尝试对比 HEAD，覆盖已暂存 + 工作区修改
        var diffRes = await _runner.RunAsync(
            new GitCommandRequest(
                guestRepoDir,
                BuildGitArgs(guestRepoDir, ["-c", "core.quotepath=false", "diff", "HEAD", "--no-color", "--no-ext-diff", "--no-renames", "--unified=3", "--", fileNorm])),
            ct).ConfigureAwait(false);

        var rawDiff = diffRes.StdoutAndError;
        if (!diffRes.Ok)
        {
            // 若仓库尚无任何提交（unborn HEAD），回退为分别查询暂存区与工作区差异
            if (IsUnbornHeadError(diffRes.StdoutAndError))
            {
                var cached = await _runner.RunAsync(
                    new GitCommandRequest(
                        guestRepoDir,
                        BuildGitArgs(guestRepoDir, ["-c", "core.quotepath=false", "diff", "--cached", "--no-color", "--no-ext-diff", "--no-renames", "--unified=3", "--", fileNorm])),
                    ct).ConfigureAwait(false);
                var wt = await _runner.RunAsync(
                    new GitCommandRequest(
                        guestRepoDir,
                        BuildGitArgs(guestRepoDir, ["-c", "core.quotepath=false", "diff", "--no-color", "--no-ext-diff", "--no-renames", "--unified=3", "--", fileNorm])),
                    ct).ConfigureAwait(false);
                rawDiff = string.IsNullOrEmpty(cached.StdoutAndError) ? wt.StdoutAndError : cached.StdoutAndError + "\n" + wt.StdoutAndError;
            }
            else
            {
                var code = ClassifyGitFailure(diffRes, GitErrors.CommandFailed);
                throw new WorkspaceException(code, FirstNonEmptyLine(diffRes.StdoutAndError) ?? code);
            }
        }

        // 若 git diff 为空且文件存在于磁盘上，核查是否为未跟踪文件（??），若是则安全读取生成新增 Diff
        if (string.IsNullOrWhiteSpace(rawDiff) && File.Exists(hostPath))
        {
            var fileStatus = await _runner.RunAsync(
                new GitCommandRequest(
                    guestRepoDir,
                    BuildGitArgs(guestRepoDir, ["-c", "core.quotepath=false", "status", "--porcelain=v1", "-z", "--", fileNorm])),
                ct).ConfigureAwait(false);

            if (fileStatus.Ok && ParsePorcelainStatus(fileStatus.StdoutAndError, repoRelRoot).Any(c => c.Kind == GitChangeKind.Untracked))
            {
                return BuildUntrackedFileDiff(wsRel, fileNorm, _limits);
            }
        }

        return ParseUnifiedDiff(fileNorm, rawDiff, _limits);
    }

    /// <summary>在后台线程安全解析 Unified Diff 文本为带行号与截断标记的不可变行列表。</summary>
    public static GitDiffDocument ParseUnifiedDiff(string fileRelativePath, string rawDiff, GitDiffLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(fileRelativePath);
        var effective = limits ?? GitDiffLimits.Default;
        if (string.IsNullOrWhiteSpace(rawDiff))
        {
            return new GitDiffDocument(fileRelativePath, IsBinary: false, IsTruncated: false, TruncationReason: null, TotalLinesObserved: 0, DisplayedLines: 0, Lines: []);
        }

        var normalized = rawDiff.Replace("\r\n", "\n", StringComparison.Ordinal);
        var rawLines = normalized.Split('\n');
        // 去掉末尾仅由尾随换行产生的空字符串
        var totalObserved = rawLines.Length > 0 && rawLines[^1].Length == 0 ? rawLines.Length - 1 : rawLines.Length;

        var parsed = new List<GitDiffLine>();
        var bytesUsed = 0;
        var isTruncated = false;
        string? truncationReason = null;
        int? oldLine = null;
        int? newLine = null;

        for (var i = 0; i < totalObserved; i++)
        {
            var rawLine = rawLines[i];
            if (rawLine.StartsWith("Binary files ", StringComparison.Ordinal) ||
                rawLine.StartsWith("GIT binary patch", StringComparison.Ordinal))
            {
                return new GitDiffDocument(
                    fileRelativePath,
                    IsBinary: true,
                    IsTruncated: false,
                    TruncationReason: null,
                    TotalLinesObserved: totalObserved,
                    DisplayedLines: 0,
                    Lines: []);
            }

            if (parsed.Count >= effective.MaxLines)
            {
                isTruncated = true;
                truncationReason = $"超出最大显示行数上限（{effective.MaxLines} 行，总计 {totalObserved} 行）";
                break;
            }

            var lineBytes = Encoding.UTF8.GetByteCount(rawLine) + 1;
            if (bytesUsed + lineBytes > effective.MaxBytes && parsed.Count > 0)
            {
                isTruncated = true;
                truncationReason = $"超出最大输出大小上限（{effective.MaxBytes / 1024} KiB）";
                break;
            }
            bytesUsed += lineBytes;

            var lineEntry = ClassifyAndNumberLine(rawLine, ref oldLine, ref newLine, effective.MaxLineLength);
            parsed.Add(lineEntry);
        }

        if (isTruncated && truncationReason is not null)
        {
            parsed.Add(new GitDiffLine(GitDiffLineKind.TruncationNotice, null, null, "!", truncationReason));
        }

        return new GitDiffDocument(
            fileRelativePath,
            IsBinary: false,
            IsTruncated: isTruncated,
            TruncationReason: truncationReason,
            TotalLinesObserved: totalObserved,
            DisplayedLines: isTruncated ? parsed.Count - 1 : parsed.Count,
            Lines: parsed);
    }

    private GitDiffDocument BuildUntrackedFileDiff(string workspaceRelativePath, string displayPath, GitDiffLimits limits)
    {
        // 通过 WorkspaceFileSystem.OpenRead 复用全部安全门禁（拒绝 .dsh/、symlink 与超大文件直接截断读取）
        using var stream = _fs.OpenRead(workspaceRelativePath, maxBytes: null);
        var readBudget = Math.Min(limits.MaxBytes + 1, 512 * 1024);
        var buffer = new byte[readBudget];
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var n = stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (n <= 0)
            {
                break;
            }
            totalRead += n;
        }

        var probeLen = Math.Min(totalRead, 8192);
        for (var i = 0; i < probeLen; i++)
        {
            if (buffer[i] == 0)
            {
                return new GitDiffDocument(displayPath, IsBinary: true, IsTruncated: false, null, 0, 0, []);
            }
        }

        var exceededBytes = totalRead > limits.MaxBytes || stream.Length > limits.MaxBytes;
        var decodeLen = Math.Min(totalRead, limits.MaxBytes);
        var text = Encoding.UTF8.GetString(buffer, 0, decodeLen).Replace("\r\n", "\n", StringComparison.Ordinal);
        var rawLines = text.Split('\n');
        var lineCount = rawLines.Length > 0 && rawLines[^1].Length == 0 ? rawLines.Length - 1 : rawLines.Length;

        var lines = new List<GitDiffLine>(Math.Min(lineCount + 3, limits.MaxLines + 1))
        {
            new(GitDiffLineKind.Meta, null, null, " ", $"new file (untracked): {displayPath}"),
            // 标记列留空，避免 UI 把标记列与正文的 "@@" 拼成 "@@@@"
            new(GitDiffLineKind.HunkHeader, null, null, string.Empty, $"@@ -0,0 +1,{Math.Min(lineCount, limits.MaxLines)} @@"),
        };

        var truncated = exceededBytes;
        string? reason = exceededBytes ? $"未跟踪文件超出最大输出大小上限（{limits.MaxBytes / 1024} KiB）" : null;
        for (var i = 0; i < lineCount; i++)
        {
            if (lines.Count >= limits.MaxLines)
            {
                truncated = true;
                reason ??= $"超出最大显示行数上限（{limits.MaxLines} 行）";
                break;
            }
            var content = ClipLine(rawLines[i], limits.MaxLineLength);
            lines.Add(new GitDiffLine(GitDiffLineKind.Added, null, i + 1, "+", content));
        }

        if (truncated && reason is not null)
        {
            lines.Add(new GitDiffLine(GitDiffLineKind.TruncationNotice, null, null, "!", reason));
        }

        return new GitDiffDocument(
            displayPath,
            IsBinary: false,
            IsTruncated: truncated,
            TruncationReason: reason,
            TotalLinesObserved: lineCount,
            DisplayedLines: truncated ? lines.Count - 1 : lines.Count,
            Lines: lines);
    }

    private async Task<(string RepoRelRoot, string GuestRepoDir, string? ErrorCode, string? ErrorMessage)> ResolveRepoRootAsync(
        string? repoRelativeDir,
        CancellationToken ct)
    {
        var dirNorm = WorkspaceFileSystem.NormalizeRelative(repoRelativeDir);
        if (WorkspaceFileSystem.IsProtected(dirNorm))
        {
            throw new WorkspaceException(WorkspaceErrors.Protected, $"'{dirNorm}' is protected");
        }

        var hostDir = _fs.Resolve(dirNorm);
        if (!Directory.Exists(hostDir))
        {
            throw new WorkspaceException(File.Exists(hostDir) ? WorkspaceErrors.NotADirectory : WorkspaceErrors.NotFound, dirNorm);
        }

        ValidateDotGitEntry(hostDir);
        var guestDir = ToGuestPath(dirNorm);
        var topRes = await _runner.RunAsync(
            new GitCommandRequest(guestDir, BuildGitArgs(guestDir, ["rev-parse", "--show-toplevel"])),
            ct).ConfigureAwait(false);

        if (!topRes.Ok)
        {
            var code = ClassifyGitFailure(topRes, GitErrors.NotARepository);
            return (dirNorm, guestDir, code, FirstNonEmptyLine(topRes.StdoutAndError));
        }

        var topRaw = FirstNonEmptyLine(topRes.StdoutAndError)?.TrimEnd('/') ?? string.Empty;
        if (string.IsNullOrEmpty(topRaw))
        {
            return (dirNorm, guestDir, GitErrors.NotARepository, "empty rev-parse output");
        }

        string repoRelRoot;
        if (string.Equals(topRaw, _guestWorkspaceRoot, StringComparison.Ordinal))
        {
            repoRelRoot = string.Empty;
        }
        else if (topRaw.StartsWith(_guestWorkspaceRoot + "/", StringComparison.Ordinal))
        {
            repoRelRoot = WorkspaceFileSystem.NormalizeRelative(topRaw[(_guestWorkspaceRoot.Length + 1)..]);
        }
        else
        {
            throw new WorkspaceException(WorkspaceErrors.OutsideRoot, $"git repository root '{topRaw}' escapes workspace");
        }

        if (WorkspaceFileSystem.IsProtected(repoRelRoot))
        {
            throw new WorkspaceException(WorkspaceErrors.Protected, $"'{repoRelRoot}' is protected");
        }

        var resolvedRepoHost = _fs.Resolve(repoRelRoot);
        if (!Directory.Exists(resolvedRepoHost))
        {
            throw new WorkspaceException(WorkspaceErrors.NotADirectory, repoRelRoot);
        }
        ValidateDotGitEntry(resolvedRepoHost);

        return (repoRelRoot, ToGuestPath(repoRelRoot), null, null);
    }

    private void ValidateDotGitEntry(string hostDir)
    {
        var dotGit = Path.Combine(hostDir, ".git");
        if (WorkspaceFileSystem.IsLink(dotGit))
        {
            throw new WorkspaceException(WorkspaceErrors.ThroughSymlink, "'.git' is a symlink");
        }
        if (File.Exists(dotGit))
        {
            // 子模块或 worktree 的 gitdir 指针文件不得指向工作区之外
            var firstLine = File.ReadLines(dotGit).FirstOrDefault()?.Trim();
            if (firstLine is not null && firstLine.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
            {
                var target = firstLine["gitdir:".Length..].Trim().Replace('\\', '/');
                if (target.StartsWith('/') && !target.StartsWith(_guestWorkspaceRoot + "/", StringComparison.Ordinal) &&
                    !string.Equals(target, _guestWorkspaceRoot, StringComparison.Ordinal) &&
                    !target.StartsWith(_fs.Root.Replace('\\', '/') + "/", StringComparison.Ordinal))
                {
                    throw new WorkspaceException(WorkspaceErrors.OutsideRoot, "'.git' gitdir pointer escapes workspace");
                }
                if (!target.StartsWith('/') && target.Contains("..", StringComparison.Ordinal))
                {
                    var combined = Path.GetFullPath(Path.Combine(hostDir, target));
                    var rootWithSep = _fs.Root + Path.DirectorySeparatorChar;
                    if (!string.Equals(combined, _fs.Root, StringComparison.Ordinal) &&
                        !combined.StartsWith(rootWithSep, StringComparison.Ordinal))
                    {
                        throw new WorkspaceException(WorkspaceErrors.OutsideRoot, "'.git' relative gitdir escapes workspace");
                    }
                }
            }
        }
    }

    private (string FileNorm, string WorkspaceRelative, string HostPath) ValidateTargetFile(string repoRelRoot, string fileRelativePath)
    {
        var fileNorm = WorkspaceFileSystem.NormalizeRelative(fileRelativePath);
        if (fileNorm.Length == 0)
        {
            throw new WorkspaceException(WorkspaceErrors.InvalidName, "file path is required");
        }
        if (WorkspaceFileSystem.IsProtected(fileNorm))
        {
            throw new WorkspaceException(WorkspaceErrors.Protected, $"'{fileNorm}' is protected");
        }

        var wsRel = repoRelRoot.Length == 0 ? fileNorm : $"{repoRelRoot}/{fileNorm}";
        if (WorkspaceFileSystem.IsProtected(wsRel))
        {
            throw new WorkspaceException(WorkspaceErrors.Protected, $"'{wsRel}' is protected");
        }

        // 逐段检查既有目录与目标叶子均不得为符号链接（兼容已删除文件的场景）
        var parts = wsRel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = _fs.Root;
        for (var i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            if (WorkspaceFileSystem.IsLink(current))
            {
                throw new WorkspaceException(WorkspaceErrors.ThroughSymlink, $"'{wsRel}' passes through or targets a symlink");
            }
            if (i < parts.Length - 1 && File.Exists(current))
            {
                throw new WorkspaceException(WorkspaceErrors.NotADirectory, wsRel);
            }
        }

        var full = Path.GetFullPath(current);
        var rootPrefix = _fs.Root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new WorkspaceException(WorkspaceErrors.OutsideRoot, $"'{wsRel}' escapes workspace");
        }

        return (fileNorm, wsRel, full);
    }

    private static List<GitChangeEntry> ParsePorcelainStatus(string output, string repoRelRoot)
    {
        if (string.IsNullOrEmpty(output))
        {
            return [];
        }

        var entries = new List<GitChangeEntry>();
        if (output.Contains('\0', StringComparison.Ordinal))
        {
            var tokens = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < tokens.Length; i++)
            {
                var token = tokens[i];
                if (token.Length < 4 || token[2] != ' ')
                {
                    continue;
                }
                var xy = token[..2];
                var path = token[3..];
                string? previous = null;
                if (xy[0] is 'R' or 'C' && i + 1 < tokens.Length)
                {
                    previous = tokens[++i];
                }
                TryAddStatusEntry(entries, xy, path, previous, repoRelRoot);
            }
        }
        else
        {
            foreach (var rawLine in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (rawLine.Length < 4 || rawLine[2] != ' ')
                {
                    continue;
                }
                var xy = rawLine[..2];
                var path = rawLine[3..];
                string? previous = null;
                var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
                if (xy[0] is 'R' or 'C' && arrow > 0)
                {
                    previous = path[..arrow];
                    path = path[(arrow + 4)..];
                }
                TryAddStatusEntry(entries, xy, path, previous, repoRelRoot);
            }
        }

        return entries;
    }

    private static void TryAddStatusEntry(
        List<GitChangeEntry> entries,
        string xy,
        string rawPath,
        string? rawPrevious,
        string repoRelRoot)
    {
        string? normPath;
        try
        {
            normPath = WorkspaceFileSystem.NormalizeRelative(rawPath);
        }
        catch (WorkspaceException)
        {
            return;
        }
        if (normPath.Length == 0)
        {
            return;
        }

        var wsRel = repoRelRoot.Length == 0 ? normPath : $"{repoRelRoot}/{normPath}";
        if (WorkspaceFileSystem.IsProtected(normPath) || WorkspaceFileSystem.IsProtected(wsRel))
        {
            // 严格过滤 .dsh/：双向不可触，绝不在状态列表中暴露或打开
            return;
        }

        string? normPrev = null;
        if (!string.IsNullOrWhiteSpace(rawPrevious))
        {
            try
            {
                normPrev = WorkspaceFileSystem.NormalizeRelative(rawPrevious);
                var prevWs = repoRelRoot.Length == 0 ? normPrev : $"{repoRelRoot}/{normPrev}";
                if (WorkspaceFileSystem.IsProtected(normPrev) || WorkspaceFileSystem.IsProtected(prevWs))
                {
                    normPrev = null;
                }
            }
            catch (WorkspaceException)
            {
                normPrev = null;
            }
        }

        var x = xy[0];
        var y = xy[1];
        var kind = (x, y) switch
        {
            ('?', '?') => GitChangeKind.Untracked,
            ('U', _) or (_, 'U') or ('A', 'A') or ('D', 'D') => GitChangeKind.Unmerged,
            ('R', _) => GitChangeKind.Renamed,
            ('C', _) => GitChangeKind.Copied,
            ('A', _) => GitChangeKind.Added,
            ('D', _) or (_, 'D') => GitChangeKind.Deleted,
            ('T', _) or (_, 'T') => GitChangeKind.TypeChanged,
            ('M', _) or (_, 'M') => GitChangeKind.Modified,
            _ => GitChangeKind.Other,
        };
        var isStaged = x is not ' ' and not '?';
        var isUnstaged = y is not ' ' and not '?' || kind == GitChangeKind.Untracked;
        entries.Add(new GitChangeEntry(normPath, normPrev, kind, xy, isStaged, isUnstaged));
    }

    private static GitDiffLine ClassifyAndNumberLine(
        string rawLine,
        ref int? oldLine,
        ref int? newLine,
        int maxLineLength)
    {
        if (rawLine.StartsWith("@@", StringComparison.Ordinal))
        {
            var m = HunkHeaderRegex().Match(rawLine);
            if (m.Success &&
                int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var o) &&
                int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                oldLine = o;
                newLine = n;
            }
            // 根因修复（UI-S5）：旧实现把 Hunk 头的标记列写成 "@@"、正文又保留完整的 "@@ -1 +1 @@"
            // 原始行，DiffLineView 相邻渲染时拼成 "@@@@ -1 +1 @@"。标记列留空并在核心侧规范化为
            // 单一 "@@" 前缀，彻底杜绝重复前缀（同时兼容上游已经拼坏的 "@@@@" 输入）。
            return new GitDiffLine(
                GitDiffLineKind.HunkHeader,
                null,
                null,
                string.Empty,
                ClipLine(UnifiedDiffReview.NormalizeHunkHeader(rawLine), maxLineLength));
        }

        if (rawLine.StartsWith("diff ", StringComparison.Ordinal))
        {
            // 新的文件段落开始：重置行号状态，避免上一文件的 hunk 状态污染下一次解析。
            oldLine = null;
            newLine = null;
            return new GitDiffLine(GitDiffLineKind.Meta, null, null, " ", ClipLine(rawLine, maxLineLength));
        }

        if (rawLine.StartsWith("\\ No newline", StringComparison.Ordinal))
        {
            return new GitDiffLine(GitDiffLineKind.Meta, null, null, " ", ClipLine(rawLine, maxLineLength));
        }

        // 只有在 Hunk 之外才把 "--- "/"+++ " 等前缀当作 git 文件头元数据；Hunk 内以 "--- " / "+++ "
        // 开头的行是真实删除/新增正文（例如内容为 "-- foo" 的删除行），必须交由下面的 +/- 分支处理，
        // 否则会被误删，导致内容丢失且增删统计偏差。
        var insideHunk = oldLine is not null || newLine is not null;
        if (!insideHunk &&
            (rawLine.StartsWith("index ", StringComparison.Ordinal) ||
             rawLine.StartsWith("--- ", StringComparison.Ordinal) ||
             rawLine.StartsWith("+++ ", StringComparison.Ordinal) ||
             rawLine.StartsWith("new file mode ", StringComparison.Ordinal) ||
             rawLine.StartsWith("deleted file mode ", StringComparison.Ordinal) ||
             rawLine.StartsWith("old mode ", StringComparison.Ordinal) ||
             rawLine.StartsWith("new mode ", StringComparison.Ordinal) ||
             rawLine.StartsWith("similarity index ", StringComparison.Ordinal) ||
             rawLine.StartsWith("dissimilarity index ", StringComparison.Ordinal) ||
             rawLine.StartsWith("rename from ", StringComparison.Ordinal) ||
             rawLine.StartsWith("rename to ", StringComparison.Ordinal) ||
             rawLine.StartsWith("copy from ", StringComparison.Ordinal) ||
             rawLine.StartsWith("copy to ", StringComparison.Ordinal)))
        {
            return new GitDiffLine(GitDiffLineKind.Meta, null, null, " ", ClipLine(rawLine, maxLineLength));
        }

        if (rawLine.StartsWith('+') && oldLine is not null && newLine is not null)
        {
            var lineNo = newLine.Value;
            newLine = lineNo + 1;
            return new GitDiffLine(GitDiffLineKind.Added, null, lineNo, "+", ClipLine(rawLine[1..], maxLineLength));
        }

        if (rawLine.StartsWith('-') && oldLine is not null && newLine is not null)
        {
            var lineNo = oldLine.Value;
            oldLine = lineNo + 1;
            return new GitDiffLine(GitDiffLineKind.Removed, lineNo, null, "-", ClipLine(rawLine[1..], maxLineLength));
        }

        if (rawLine.StartsWith(' ') && oldLine is not null && newLine is not null)
        {
            var o = oldLine.Value;
            var n = newLine.Value;
            oldLine = o + 1;
            newLine = n + 1;
            return new GitDiffLine(GitDiffLineKind.Context, o, n, " ", ClipLine(rawLine[1..], maxLineLength));
        }

        return new GitDiffLine(GitDiffLineKind.Meta, null, null, " ", ClipLine(rawLine, maxLineLength));
    }

    private static string ClipLine(string line, int maxLineLength) =>
        line.Length <= maxLineLength ? line : string.Concat(line.AsSpan(0, maxLineLength), " …(行已截断)");

    private string ToGuestPath(string normalizedRelative) =>
        normalizedRelative.Length == 0 ? _guestWorkspaceRoot : $"{_guestWorkspaceRoot}/{normalizedRelative}";

    private static List<string> BuildGitArgs(string guestDir, IReadOnlyList<string> tailArgs)
    {
        var args = new List<string>(6 + tailArgs.Count)
        {
            "--no-pager",
            "-c",
            "safe.directory=*",
            "-C",
            guestDir,
        };
        args.AddRange(tailArgs);
        return args;
    }

    private static string NormalizeGuestRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var trimmed = root.Trim().Replace('\\', '/').TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    private static string? FirstNonEmptyLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0)
            {
                return t;
            }
        }
        return null;
    }

    private static bool IsUnbornHeadError(string? output) =>
        !string.IsNullOrEmpty(output) &&
        (output.Contains("ambiguous argument 'HEAD'", StringComparison.OrdinalIgnoreCase) ||
         output.Contains("unknown revision", StringComparison.OrdinalIgnoreCase) ||
         output.Contains("bad revision 'HEAD'", StringComparison.OrdinalIgnoreCase));

    private static string ClassifyGitFailure(GitCommandOutput output, string fallbackCode)
    {
        if (!string.IsNullOrEmpty(output.ErrorCode) &&
            output.ErrorCode is GitErrors.GitUnavailable or GitErrors.Timeout or GitErrors.NotARepository)
        {
            return output.ErrorCode;
        }
        var text = output.StdoutAndError ?? string.Empty;
        if (output.ExitCode == 127 ||
            text.Contains("git: not found", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("command not found", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase) && text.Contains("git", StringComparison.OrdinalIgnoreCase))
        {
            return GitErrors.GitUnavailable;
        }
        if (text.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
        {
            return GitErrors.NotARepository;
        }
        return fallbackCode;
    }

    [GeneratedRegex(@"^@@\s+-(\d+)(?:,\d+)?\s+\+(\d+)(?:,\d+)?\s+@@", RegexOptions.CultureInvariant)]
    private static partial Regex HunkHeaderRegex();
}
