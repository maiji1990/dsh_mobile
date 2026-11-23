using DshMobile.Core.Contracts;
using DshMobile.Core.Files;

namespace DshMobile.Runtime.Guest;

/// <summary>
/// 基于 <see cref="IGuestProcess"/> 与 <see cref="GuestCommandLine"/> 的只读 Git 命令执行器（N-06-01）：
/// 所有参数通过 POSIX 单引号逐 token 转义后在 PRoot guest 中执行，绝不拼接原始 shell 字符串。
/// </summary>
public sealed class ProotGitCommandExecutor : IWorkspaceGitCommandRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    public const int DefaultMaxOutputLines = 4000;

    private readonly IGuestProcess _guestProcess;
    private readonly TimeSpan _timeout;
    private readonly int _maxOutputLines;

    public ProotGitCommandExecutor(
        IGuestProcess guestProcess,
        TimeSpan? timeout = null,
        int maxOutputLines = DefaultMaxOutputLines)
    {
        _guestProcess = guestProcess ?? throw new ArgumentNullException(nameof(guestProcess));
        _timeout = timeout ?? DefaultTimeout;
        _maxOutputLines = Math.Max(200, maxOutputLines);
    }

    /// <summary>生成经过 <see cref="GuestCommandLine"/> 逐 token 单引号转义的 guest shell 命令行。</summary>
    public static string BuildQuotedCommandLine(IReadOnlyList<string> gitArguments)
    {
        ArgumentNullException.ThrowIfNull(gitArguments);
        return GuestCommandLine.Join(["git", .. gitArguments]);
    }

    public async Task<GitCommandOutput> RunAsync(GitCommandRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        // 调用前预检 POSIX 单引号构造（包含 NUL 校验）
        _ = BuildQuotedCommandLine(request.Arguments);

        var options = new GuestExecutionOptions(
            WorkingDirectory: request.GuestWorkingDirectory,
            Timeout: _timeout,
            MaxOutputLines: _maxOutputLines);

        var result = await _guestProcess.ExecuteAsync("git", request.Arguments, options, ct).ConfigureAwait(false);
        if (result.Ok)
        {
            return new GitCommandOutput(true, result.ExitCode, result.Output, null);
        }

        var errorCode = ClassifyError(result);
        var output = string.IsNullOrWhiteSpace(result.Output) ? (result.Error ?? errorCode) : result.Output;
        return new GitCommandOutput(false, result.ExitCode, output, errorCode);
    }

    private static string ClassifyError(GuestProcessResult result)
    {
        if (result.Error is { } err && err.StartsWith(ProotGuestProcess.Timeout, StringComparison.Ordinal))
        {
            return GitErrors.Timeout;
        }
        if (result.ExitCode == 127 ||
            (result.Error?.Contains("GUEST_SPAWN_FAILED", StringComparison.Ordinal) == true) ||
            result.Output.Contains("git: not found", StringComparison.OrdinalIgnoreCase) ||
            result.Output.Contains("command not found", StringComparison.OrdinalIgnoreCase))
        {
            return GitErrors.GitUnavailable;
        }
        if (result.Output.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
        {
            return GitErrors.NotARepository;
        }
        return GitErrors.CommandFailed;
    }
}
