using System.Globalization;
using DshMobile.Runtime.Guest;
using DshMobile.Runtime.Hosting;
using DshMobile.Runtime.Remote;
using DshMobile.Runtime.Supervision;

namespace DshMobile.Runtime.Tests;

/// <summary>PR-08 Runtime 层测试：32 幻影进程预算监测、exit 137 归类、ProbeHealthAsync 前置校验与 RemoteSshSession 令牌接口契约。</summary>
public sealed class GovernanceRuntimeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dshm-gov-rt-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void WriteFakeProc(int pid, int ppid, int uid)
    {
        var dir = Path.Combine(_dir, pid.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "stat"), $"{pid} (proc{pid}) S {ppid} {pid} 0 0 -1 4194560 0 0");
        File.WriteAllText(Path.Combine(dir, "status"), $"Name:\tproc{pid}\nUid:\t{uid}\t{uid}\t{uid}\t{uid}\nTracerPid:\t0\n");
    }

    [Fact]
    public void PhantomProcessMonitor_CountsOnlySameUidChildren_AndWarnsAt28()
    {
        var selfDir = Path.Combine(_dir, "self");
        Directory.CreateDirectory(selfDir);
        File.WriteAllText(Path.Combine(selfDir, "status"), "Name:\tdshmobile\nUid:\t10245\t10245\t10245\t10245\n");

        WriteFakeProc(100, 1, 10245); // App 主进程，应排除
        for (var i = 0; i < 27; i++)
        {
            WriteFakeProc(200 + i, 100, 10245);
        }
        WriteFakeProc(900, 1, 2000); // 其他 UID，不能计入

        var below = PhantomProcessMonitor.Observe(100, _dir);
        Assert.False(below.Unsupported);
        Assert.Equal(10245, below.SelfUid);
        Assert.Equal(27, below.ChildProcessCount);
        Assert.Equal(32, below.Limit);
        Assert.False(below.NearLimit);

        WriteFakeProc(227, 100, 10245); // 第 28 个同 UID 子进程触发预警
        var warning = PhantomProcessMonitor.Observe(100, _dir);
        Assert.Equal(28, warning.ChildProcessCount);
        Assert.True(warning.NearLimit);
    }

    [Fact]
    public void PhantomProcessMonitor_MissingProcRoot_ReportsUnsupported()
    {
        var report = PhantomProcessMonitor.Observe(100, Path.Combine(_dir, "missing"));
        Assert.True(report.Unsupported);
        Assert.False(report.NearLimit);
        Assert.Equal(0, report.ChildProcessCount);
    }

    [Fact]
    public async Task ProotGuestProcess_Exit137_IsClassifiedAsPhantomProcessLimit()
    {
        IReadOnlyList<string> argv = OperatingSystem.IsWindows()
            ? ["cmd.exe", "/c", "exit 137"]
            : ["/bin/sh", "-c", "exit 137"];

        var result = await ProotGuestProcess.RunProcessAsync(
            argv,
            new Dictionary<string, string>(StringComparer.Ordinal),
            new GuestExecutionOptions(Timeout: TimeSpan.FromSeconds(10)),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(137, result.ExitCode);
        Assert.StartsWith(RuntimeErrorCodes.PhantomProcessLimit, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DshHost_ProbeHealthAsync_ReturnsPreconditionFailureWithoutStartingProcesses()
    {
        var layout = new RuntimeLayout(Path.Combine(_dir, "files"), null, Path.Combine(_dir, "nolib"));
        var logs = new List<string>();
        await using var host = new DshHost(layout, logs.Add);

        var probe = await host.ProbeHealthAsync(CancellationToken.None);
        Assert.False(probe.Ok);
        Assert.Equal(Native.NativeErrorCodes.ExecutableNotAllowed, probe.Code);
        Assert.False(host.OwnsProcesses);
        Assert.Equal(DshHostState.Stopped, host.State);
    }

    [Fact]
    public void RemoteSshSession_ImplementsIDshTokenSource_AndDropsTokenOnDisconnectRefresh()
    {
        using var client = new Renci.SshNet.SshClient("127.0.0.1", "user", "pw");
        var profile = new RemoteHostProfile("h1", "lab", "127.0.0.1", 22, "user", 3080, null);
        using var session = new RemoteSshSession(client, profile);

        Assert.Null(session.Token);
        Assert.Null(session.LoopbackBaseUri);

        var status = session.Refresh();
        Assert.Equal("DISCONNECTED", status.State);
        Assert.Equal(DshMobile.Core.Contracts.RemoteCodes.Unreachable, status.Code);
        Assert.Null(session.Token);
        Assert.Null(session.LoopbackBaseUri);
    }
}
