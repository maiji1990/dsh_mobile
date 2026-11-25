using System.Globalization;
using DshMobile.Runtime.Hosting;
using DshMobile.Runtime.Supervision;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// ADR-0031 停机目标选择：SIGTERM 必须发给外层 PRoot 下的 Node tracee，而不是只发给 libproot.so；
/// /proc 不可用或没有 Node 后代时才回退到原 dsh.Id。
/// </summary>
public sealed class DshHostGracefulStopTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dshm-graceful-stop-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Proc(int pid, int ppid, params string[] argv)
    {
        var d = Path.Combine(_dir, pid.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "stat"), $"{pid} ({Path.GetFileName(argv[0])}) S {ppid} {pid} 0 0 -1 4194560 0 0");
        File.WriteAllText(Path.Combine(d, "cmdline"), string.Join('\0', argv) + "\0");
    }

    [Fact]
    public void SelectGracefulStopPid_PrefersTheInnerNodeTracee()
    {
        Proc(300, 100, "libproot.so", "--rootfs=/x/base", "/usr/local/bin/node", "/opt/dshapp/runtime/node_modules/@deepseek-ai/dsh/lib/bin.js");
        Proc(301, 300, "node", "--expose-internals", "/opt/dshapp/runtime/node_modules/@deepseek-ai/dsh/lib/bin.js");

        Assert.Equal(301, DshHost.SelectGracefulStopPid(ProcessTable.Read(_dir), 300));
    }

    [Fact]
    public void SelectGracefulStopPid_UsesChildrenFirstOrder()
    {
        Proc(300, 100, "libproot.so");
        Proc(301, 300, "node", "parent-worker.js");
        Proc(302, 301, "node", "child-worker.js");

        // 子进程优先：最深层的 node（302）先收到 SIGTERM。
        Assert.Equal(302, DshHost.SelectGracefulStopPid(ProcessTable.Read(_dir), 300));
    }

    [Fact]
    public void SelectGracefulStopPid_ReachesNodeUnderNonNodeIntermediate_AndIgnoresNodeInArgs()
    {
        // 真实形态：libproot.so -> 启停脚本（bash）-> node。bash 的 argv 里也含 node 路径，
        // 但判定只看 argv[0] 文件名，因此不能把 bash 当成 node，必须继续下钻到真正的 tracee。
        Proc(300, 100, "libproot.so", "--rootfs=/x/base", "/opt/dshapp/runtime/start.sh");
        Proc(301, 300, "bash", "-c", "/usr/local/bin/node /opt/dshapp/runtime/node_modules/@deepseek-ai/dsh/lib/bin.js");
        Proc(302, 301, "node", "/opt/dshapp/runtime/node_modules/@deepseek-ai/dsh/lib/bin.js");

        Assert.Equal(302, DshHost.SelectGracefulStopPid(ProcessTable.Read(_dir), 300));
    }

    [Fact]
    public void SelectGracefulStopPid_FallsBackWhenNoNodeDescendantExists()
    {
        Proc(300, 100, "libproot.so", "/usr/bin/bash", "-c", "echo no node here");
        Proc(301, 300, "bash", "-c", "echo no node here");

        Assert.Null(DshHost.SelectGracefulStopPid(ProcessTable.Read(_dir), 300));
    }

    [Fact]
    public void SelectGracefulStopPid_FallsBackWhenProcIsUnavailable()
    {
        // Windows 或 /proc 不可用时 Read 返回 null，调用方据此回退到 dsh.Id。
        Assert.Null(ProcessTable.Read(Path.Combine(_dir, "missing-proc")));
        Assert.Null(DshHost.SelectGracefulStopPid(null, 300));
    }

    [Fact]
    public void SelectGracefulStopPid_EmptyTableFallsBack()
    {
        // 临时 /proc 目录存在但没有任何 PID 条目：Read 返回非 null 空表。
        var table = ProcessTable.Read(_dir);
        Assert.NotNull(table);
        Assert.Empty(table.DescendantPidsChildrenFirst(300));
        Assert.Null(table.FindNodeDescendant(300));
        Assert.Null(DshHost.SelectGracefulStopPid(table, 300));
    }

    [Fact]
    public void SelectGracefulStopPid_RejectsInvalidRootPid()
    {
        Proc(300, 100, "libproot.so");
        Proc(301, 300, "node", "bin.js");

        var table = ProcessTable.Read(_dir);
        Assert.Null(DshHost.SelectGracefulStopPid(table, 0));
        Assert.Null(DshHost.SelectGracefulStopPid(table, 1));
    }
}
