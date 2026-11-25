using System.Diagnostics;
using DshMobile.Runtime;
using DshMobile.Runtime.Hosting;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// 宿主侧握手闸门（PR-08 真机日志：HTTP 404 比 stdout token 早 83–114 ms）：
/// HTTP 就绪后必须等 <see cref="IDshTokenSource.TokenChanged"/> 事件送入非空 token 才进入协议握手；
/// 等待仍受剩余预算、取消和 DSH 进程提前退出（含 exit 137）约束，不伪造成功。
/// </summary>
public sealed class DshHostProtocolHandshakeTests
{
    private sealed class FakeTokenSource : IDshTokenSource
    {
        private string? _token;

        public string? Token => Volatile.Read(ref _token);

        public long? TokenRevision => Token is null ? null : 1;

        public int SubscriberCount => TokenChanged is { } handlers ? handlers.GetInvocationList().Length : 0;

        public event EventHandler? TokenChanged;

        public void SetToken(string? token)
        {
            Volatile.Write(ref _token, token);
            TokenChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static Process StartLongRunningProcess() =>
        OperatingSystem.IsWindows()
            ? Process.Start(new ProcessStartInfo("cmd.exe", "/d /c ping -n 30 127.0.0.1 >nul")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!
            : Process.Start(new ProcessStartInfo("/bin/sh", "-c \"sleep 30\"")
            {
                UseShellExecute = false,
            })!;

    private static Process StartExitedProcess(int exitCode)
    {
        var process = OperatingSystem.IsWindows()
            ? Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c exit {exitCode}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!
            : Process.Start(new ProcessStartInfo("/bin/sh", $"-c \"exit {exitCode}\"")
            {
                UseShellExecute = false,
            })!;
        process.WaitForExit();
        Assert.Equal(exitCode, process.ExitCode);
        return process;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    [Fact]
    public async Task AwaitProtocolHandshake_WhenHttpReadyBeforeToken_WaitsThenCallsHandshakeWithNonNullToken()
    {
        using var dsh = StartLongRunningProcess();
        var source = new FakeTokenSource();
        var handshakeToken = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handshakeCalled = 0;

        var waiting = DshHost.AwaitProtocolHandshakeAsync(
            source,
            dsh,
            TimeSpan.FromSeconds(5),
            TimeProvider.System,
            (tokenSource, _) =>
            {
                Interlocked.Increment(ref handshakeCalled);
                handshakeToken.TrySetResult(tokenSource.Token);
                return Task.FromResult(new DshHostResult(true, "OK", null));
            },
            CancellationToken.None);

        // HTTP 已就绪但 token 尚未打印：握手不得被调用，也不得靠轮询提前放行。
        await Task.Delay(60);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(0, Volatile.Read(ref handshakeCalled));

        var availableAt = Stopwatch.StartNew();
        source.SetToken("launch-token-test");
        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        availableAt.Stop();

        Assert.True(result.Ok);
        Assert.Equal(1, Volatile.Read(ref handshakeCalled));
        Assert.Equal("launch-token-test", await handshakeToken.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.True(availableAt.Elapsed < TimeSpan.FromSeconds(1), $"token 更新应立即唤醒握手，实际 {availableAt.Elapsed}");
        Assert.Equal(0, source.SubscriberCount);
    }

    [Fact]
    public async Task AwaitProtocolHandshake_WhenTokenAlreadyAvailable_CallsHandshakeWithoutWaiting()
    {
        using var dsh = StartLongRunningProcess();
        var source = new FakeTokenSource();
        source.SetToken("already-there");

        var startedAt = Stopwatch.StartNew();
        var result = await DshHost.AwaitProtocolHandshakeAsync(
            source,
            dsh,
            TimeSpan.FromSeconds(5),
            TimeProvider.System,
            (tokenSource, _) => Task.FromResult(new DshHostResult(true, tokenSource.Token ?? "missing", null)),
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("already-there", result.Code);
        Assert.True(startedAt.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(137, "PHANTOM_PROCESS_LIMIT")]
    [InlineData(7, "DSH_EXITED")]
    public async Task AwaitProtocolHandshake_WhenProcessExitsBeforeToken_ReturnsFailureWithoutHandshake(int exitCode, string expectedCode)
    {
        using var dsh = StartExitedProcess(exitCode);
        var source = new FakeTokenSource();
        var handshakeCalled = 0;

        var result = await DshHost.AwaitProtocolHandshakeAsync(
            source,
            dsh,
            TimeSpan.FromSeconds(5),
            TimeProvider.System,
            (_, _) =>
            {
                Interlocked.Increment(ref handshakeCalled);
                return Task.FromResult(new DshHostResult(true, "OK", null));
            },
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(expectedCode, result.Code);
        Assert.Equal(0, Volatile.Read(ref handshakeCalled));
    }

    [Fact]
    public async Task AwaitProtocolHandshake_WhenTokenNeverArrives_ReturnsNotReadyAfterBudgetWithoutHandshake()
    {
        using var dsh = StartLongRunningProcess();
        var source = new FakeTokenSource();
        var handshakeCalled = 0;
        var startedAt = Stopwatch.StartNew();

        var result = await DshHost.AwaitProtocolHandshakeAsync(
            source,
            dsh,
            TimeSpan.FromMilliseconds(150),
            TimeProvider.System,
            (_, _) =>
            {
                Interlocked.Increment(ref handshakeCalled);
                return Task.FromResult(new DshHostResult(true, "OK", null));
            },
            CancellationToken.None);

        startedAt.Stop();
        Assert.False(result.Ok);
        Assert.Equal(DshHost.NotReady, result.Code);
        Assert.Equal(0, Volatile.Read(ref handshakeCalled));
        Assert.True(startedAt.Elapsed >= TimeSpan.FromMilliseconds(100), $"预算未到不应提前失败，实际 {startedAt.Elapsed}");
        Assert.True(startedAt.Elapsed < TimeSpan.FromSeconds(3), $"应在预算附近失败，实际 {startedAt.Elapsed}");
        Assert.Equal(0, source.SubscriberCount);
    }

    [Fact]
    public async Task AwaitProtocolHandshake_WhenCanceled_PropagatesCancellationWithoutHandshake()
    {
        using var dsh = StartLongRunningProcess();
        var source = new FakeTokenSource();
        using var cts = new CancellationTokenSource();

        var waiting = DshHost.AwaitProtocolHandshakeAsync(
            source,
            dsh,
            TimeSpan.FromSeconds(5),
            TimeProvider.System,
            (_, _) => Task.FromResult(new DshHostResult(true, "OK", null)),
            cts.Token);
        await Task.Delay(30);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(0, source.SubscriberCount);
    }

    [Fact]
    public async Task AwaitProtocolHandshake_WhenTokenArrivesWhileSubscribing_StillCallsHandshake()
    {
        using var dsh = StartLongRunningProcess();
        var source = new TokenArrivesWhenSubscribingSource();
        var handshakeCalled = 0;
        var observedToken = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await DshHost.AwaitProtocolHandshakeAsync(
            source,
            dsh,
            TimeSpan.FromMilliseconds(500),
            TimeProvider.System,
            (tokenSource, _) =>
            {
                Interlocked.Increment(ref handshakeCalled);
                observedToken.TrySetResult(tokenSource.Token);
                return Task.FromResult(new DshHostResult(true, "OK", null));
            },
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(1, Volatile.Read(ref handshakeCalled));
        Assert.Equal("arrived-while-subscribing", await observedToken.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, source.SubscriberCount);
    }

    [Fact]
    public async Task AwaitProtocolHandshake_WhenTokenClearedEventsArrive_DoesNotHandshakeWithNull()
    {
        using var dsh = StartLongRunningProcess();
        var source = new FakeTokenSource();
        var handshakeCalled = 0;
        var waiting = DshHost.AwaitProtocolHandshakeAsync(
            source,
            dsh,
            TimeSpan.FromSeconds(5),
            TimeProvider.System,
            (tokenSource, _) =>
            {
                Interlocked.Increment(ref handshakeCalled);
                return Task.FromResult(new DshHostResult(true, tokenSource.Token ?? "NULL", null));
            },
            CancellationToken.None);

        await Task.Delay(30);
        // 空令牌事件（令牌被清空）不得触发握手，也不得让等待提前结束。
        source.SetToken(null);
        await Task.Delay(30);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(0, Volatile.Read(ref handshakeCalled));

        source.SetToken("late-token");
        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Ok);
        Assert.Equal(1, Volatile.Read(ref handshakeCalled));
        Assert.Equal(0, source.SubscriberCount);
    }

    [Fact]
    public async Task AwaitProtocolHandshake_WhenProcessExitsWhileWaiting_ReturnsFailureWithoutHandshake()
    {
        var dsh = StartLongRunningProcess();
        try
        {
            var source = new FakeTokenSource();
            var handshakeCalled = 0;
            var waiting = DshHost.AwaitProtocolHandshakeAsync(
                source,
                dsh,
                TimeSpan.FromSeconds(5),
                TimeProvider.System,
                (_, _) =>
                {
                    Interlocked.Increment(ref handshakeCalled);
                    return Task.FromResult(new DshHostResult(true, "OK", null));
                },
                CancellationToken.None);

            await Task.Delay(50);
            try
            {
                dsh.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 进程可能已被外部清理；继续等待结果即可暴露真实问题。
            }

            var result = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(result.Ok);
            // Windows 强杀退出码与 POSIX 的 137 不同：两者都必须映射为稳定错误码，且绝不进入握手。
            Assert.True(
                result.Code is DshHost.Exited or RuntimeErrorCodes.PhantomProcessLimit,
                $"waiting 期间进程退出应返回稳定错误码，实际 {result.Code}");
            Assert.Equal(0, Volatile.Read(ref handshakeCalled));
            Assert.Equal(0, source.SubscriberCount);
        }
        finally
        {
            Kill(dsh);
        }
    }

    /// <summary>
    /// 令牌恰好在“检查为空”与“订阅 TokenChanged”之间就绪：add 访问器先写入令牌、再挂处理器，
    /// 该事件因此不会送达新订阅者。实现只有在订阅后复查 Token 才能通过本用例。
    /// </summary>
    private sealed class TokenArrivesWhenSubscribingSource : IDshTokenSource
    {
        private readonly FakeTokenSource _inner = new();
        private int _armed;

        public string? Token => _inner.Token;

        public long? TokenRevision => _inner.TokenRevision;

        public int SubscriberCount => _inner.SubscriberCount;

        public event EventHandler? TokenChanged
        {
            add
            {
                if (Interlocked.Exchange(ref _armed, 1) == 0)
                {
                    _inner.SetToken("arrived-while-subscribing");
                }
                _inner.TokenChanged += value;
            }
            remove => _inner.TokenChanged -= value;
        }
    }
}
