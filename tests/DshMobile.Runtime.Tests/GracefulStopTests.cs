using DshMobile.Runtime.Hosting;

namespace DshMobile.Runtime.Tests;

/// <summary>ADR-0031: the bounded SIGTERM wait before the SIGKILL sweep.</summary>
public sealed class GracefulStopTests
{
    private sealed class FakeTerminator(bool ok) : IGuestTerminator
    {
        public int Calls { get; private set; }

        public int? LastPid { get; private set; }

        public bool TryTerminate(int pid)
        {
            Calls++;
            LastPid = pid;
            return ok;
        }
    }

    /// <summary>在收到 SIGTERM 时执行回调，用于模拟“外层 PRoot 随内部 node 退出”的联动。</summary>
    private sealed class CallbackTerminator(Func<int, bool> terminate) : IGuestTerminator
    {
        public int? LastPid { get; private set; }

        public bool TryTerminate(int pid)
        {
            LastPid = pid;
            return terminate(pid);
        }
    }

    [Fact]
    public async Task AlreadyExited_DoesNotSignal()
    {
        var terminator = new FakeTerminator(ok: true);
        var log = new List<string>();
        var r = await GracefulStop.RunAsync(42, terminator, () => true, TimeSpan.FromSeconds(1), log.Add);
        Assert.True(r.Graceful);
        Assert.False(r.Signalled);
        Assert.Equal(0, terminator.Calls);
        Assert.Empty(log);
    }

    [Fact]
    public async Task NoSigtermFallsThroughToKillWithoutWaiting()
    {
        var log = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await GracefulStop.RunAsync(42, new FakeTerminator(ok: false), () => false, TimeSpan.FromSeconds(5), log.Add);
        sw.Stop();
        Assert.False(r.Graceful);
        Assert.False(r.Signalled);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), "it must not wait for a signal it never sent");
        Assert.Contains("SIGKILL", Assert.Single(log));
    }

    [Fact]
    public async Task ProcessThatLeavesAfterSigterm_IsGraceful()
    {
        var exited = false;
        _ = Task.Run(async () =>
        {
            await Task.Delay(150);
            exited = true;
        });
        var log = new List<string>();
        var r = await GracefulStop.RunAsync(42, new FakeTerminator(ok: true), () => exited, TimeSpan.FromSeconds(2), log.Add);
        Assert.True(r.Graceful);
        Assert.True(r.Signalled);
        Assert.InRange(r.Elapsed.TotalMilliseconds, 100, 1500);
        Assert.Contains("graceful stop", Assert.Single(log));
    }

    [Fact]
    public async Task SignalsTheGivenInnerPid_AndWaitsForTheOuterProcessToLeave()
    {
        var outerExited = false;
        var terminator = new CallbackTerminator(pid =>
        {
            // 只对内部 node PID 发信号；node 退出后外层 PRoot 随之退出。
            if (pid != 301)
            {
                return false;
            }
            outerExited = true;
            return true;
        });
        var log = new List<string>();
        var r = await GracefulStop.RunAsync(301, terminator, () => outerExited, TimeSpan.FromSeconds(2), log.Add);
        Assert.True(r.Graceful);
        Assert.True(r.Signalled);
        Assert.Equal(301, terminator.LastPid);
        Assert.Contains("graceful stop", Assert.Single(log));
    }

    [Fact]
    public async Task ProcessThatIgnoresSigterm_EscalatesAfterTheTimeout()
    {
        var log = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await GracefulStop.RunAsync(42, new FakeTerminator(ok: true), () => false, TimeSpan.FromMilliseconds(300), log.Add);
        sw.Stop();
        Assert.False(r.Graceful);
        Assert.True(r.Signalled);
        Assert.InRange(sw.Elapsed.TotalMilliseconds, 250, 3000);
        Assert.Contains("escalating to SIGKILL", Assert.Single(log));
    }

    [Fact]
    public void WindowsTerminator_RefusesInsteadOfThrowing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        Assert.False(PosixGuestTerminator.Instance.TryTerminate(Environment.ProcessId));
    }
}
