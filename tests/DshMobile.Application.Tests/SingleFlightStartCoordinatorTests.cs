using DshMobile.Application.Runtime;

namespace DshMobile.Application.Tests;

/// <summary>5B：启动单飞汇流组件——并发复用、失败上报、完成后可重新启动、取消只影响等待方。</summary>
public sealed class SingleFlightStartCoordinatorTests
{
    [Fact]
    public async Task ConcurrentStarts_AttachToSameFlight_AndReuseResult()
    {
        var coordinator = new SingleFlightStartCoordinator<string>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        Task<string> Start(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            return WaitAsync();
        }

        async Task<string> WaitAsync()
        {
            await gate.Task.ConfigureAwait(false);
            return "ok";
        }

        var first = coordinator.Start(Start);
        var second = coordinator.Start(Start);

        Assert.True(first.IsOwner);
        Assert.False(second.IsOwner);
        Assert.Same(first.Task, second.Task);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.True(coordinator.IsInFlight);

        gate.SetResult();
        Assert.Equal("ok", await first.Task);
        Assert.Equal("ok", await second.Task);
        Assert.Equal(1, Volatile.Read(ref calls));

        // 完成后的新请求必须创建新的启动，而不是复用已完成的旧任务。
        var third = coordinator.Start(Start);
        Assert.True(third.IsOwner);
        Assert.Equal("ok", await third.Task);
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task FailedFlight_IsObservedByEveryWaiter_NotSwallowed()
    {
        var coordinator = new SingleFlightStartCoordinator<string>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task<string> FailingStart(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            await gate.Task.ConfigureAwait(false);
            throw new InvalidOperationException("start failed");
        }

        var owner = coordinator.Start(FailingStart);
        var attached = coordinator.Start(FailingStart);
        Assert.Equal(1, Volatile.Read(ref calls));

        gate.SetResult();
        var ownerError = await Assert.ThrowsAsync<InvalidOperationException>(() => owner.Task);
        var attachedError = await Assert.ThrowsAsync<InvalidOperationException>(() => attached.Task);
        Assert.Same(ownerError, attachedError);
        Assert.Equal("start failed", attachedError.Message);
    }

    [Fact]
    public async Task CancellingAnAttachedWaiter_DoesNotCancelTheSharedFlight()
    {
        var coordinator = new SingleFlightStartCoordinator<string>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string> Start(CancellationToken _)
        {
            await gate.Task.ConfigureAwait(false);
            return "ok";
        }

        var owner = coordinator.Start(Start);
        using var cts = new CancellationTokenSource();
        var attached = coordinator.Start(Start, cts.Token);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attached.Task.WaitAsync(cts.Token));

        gate.SetResult();
        Assert.Equal("ok", await owner.Task);
    }

    [Fact]
    public async Task OwnerCancellation_CancelsTheFlight_AndANewStartStillRuns()
    {
        var coordinator = new SingleFlightStartCoordinator<string>();
        var calls = 0;
        using var cts = new CancellationTokenSource();

        Task<string> HangingStart(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            return Task.Delay(Timeout.InfiniteTimeSpan, ct).ContinueWith(_ => "unreachable", ct);
        }

        var hanging = coordinator.Start(HangingStart, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hanging.Task);

        var fresh = coordinator.Start(_ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult("fresh");
        });
        Assert.True(fresh.IsOwner);
        Assert.Equal("fresh", await fresh.Task);
        Assert.Equal(2, Volatile.Read(ref calls));
    }
}
