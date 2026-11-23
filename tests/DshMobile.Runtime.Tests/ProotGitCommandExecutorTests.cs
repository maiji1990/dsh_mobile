using DshMobile.Core.Contracts;
using DshMobile.Core.Files;
using DshMobile.Runtime.Guest;

namespace DshMobile.Runtime.Tests;

public sealed class ProotGitCommandExecutorTests
{
    private sealed class FakeGuestProcess(Func<string, IReadOnlyList<string>, GuestExecutionOptions, GuestProcessResult> handler) : IGuestProcess
    {
        public (string Executable, IReadOnlyList<string> Arguments, GuestExecutionOptions Options)? LastCall { get; private set; }

        public Task<GuestProcessResult> ExecuteAsync(string executable, IReadOnlyList<string> arguments, GuestExecutionOptions options, CancellationToken ct)
        {
            LastCall = (executable, arguments, options);
            return Task.FromResult(handler(executable, arguments, options));
        }

        public Task<GuestPtySession> OpenPtyAsync(GuestExecutionOptions options, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    [Fact]
    public void BuildQuotedCommandLine_SingleQuotesAllTokensSafely()
    {
        var cmd = ProotGitCommandExecutor.BuildQuotedCommandLine([
            "--no-pager",
            "-C",
            "/root/projects/my repo",
            "diff",
            "--",
            "evil'file;rm -rf /;$(id).txt",
        ]);

        Assert.Equal(
            "'git' '--no-pager' '-C' '/root/projects/my repo' 'diff' '--' 'evil'\\''file;rm -rf /;$(id).txt'",
            cmd);
    }

    [Fact]
    public async Task RunAsync_InvokesGuestProcessWithGitArgv_AndMapsTimeoutAndMissingGit()
    {
        var timeoutGuest = new FakeGuestProcess((_, _, _) =>
            new GuestProcessResult(false, -1, "", $"{ProotGuestProcess.Timeout}: exceeded"));
        var executor = new ProotGitCommandExecutor(timeoutGuest);

        var timeoutRes = await executor.RunAsync(
            new GitCommandRequest("/root/projects", ["status", "--porcelain=v1"]),
            CancellationToken.None);

        Assert.False(timeoutRes.Ok);
        Assert.Equal(GitErrors.Timeout, timeoutRes.ErrorCode);
        Assert.NotNull(timeoutGuest.LastCall);
        Assert.Equal("git", timeoutGuest.LastCall!.Value.Executable);
        Assert.Equal("/root/projects", timeoutGuest.LastCall.Value.Options.WorkingDirectory);

        var missingGuest = new FakeGuestProcess((_, _, _) =>
            new GuestProcessResult(false, 127, "bash: git: command not found", "GUEST_CMD_FAILED"));
        var missingExecutor = new ProotGitCommandExecutor(missingGuest);

        var missingRes = await missingExecutor.RunAsync(
            new GitCommandRequest("/root/projects", ["rev-parse", "--show-toplevel"]),
            CancellationToken.None);

        Assert.False(missingRes.Ok);
        Assert.Equal(GitErrors.GitUnavailable, missingRes.ErrorCode);
    }
}
