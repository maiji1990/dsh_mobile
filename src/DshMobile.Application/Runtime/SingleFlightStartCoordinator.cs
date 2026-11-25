namespace DshMobile.Application.Runtime;

/// <summary>一次单飞启动的句柄：<see cref="Task"/> 是共享的进行中启动，<see cref="IsOwner"/> 表示本次调用是否创建了该启动。</summary>
public readonly record struct SingleFlightStart<T>(Task<T> Task, bool IsOwner);

/// <summary>
/// 启动请求的单飞汇流器（5B）：同一时刻只有一个启动任务在跑，后到的并发启动附着并复用同一个 <see cref="Task"/>，
/// 因此用户在后台上门预热进行中点击「启动 DSH」时不会被静默丢弃，而是等待并复用这次启动的结果。
///
/// 只用于 <c>start</c>：<c>stop</c>/<c>import</c>/<c>update</c> 等其它操作仍由调用方保持严格单写者互斥，不接入本组件。
/// 启动任务完成后句柄立即失效，下一次请求会创建新的启动；附着方的取消只终止自己的等待，不影响共享启动，
/// 发起者自己的令牌则由启动委托按约定传播。
/// </summary>
public sealed class SingleFlightStartCoordinator<T>
{
    private readonly Lock _gate = new();
    private Task<T>? _inFlight;

    /// <summary>当前是否有进行中的启动。用于 UI/日志判断“正在预热/启动”。</summary>
    public bool IsInFlight
    {
        get
        {
            lock (_gate)
            {
                return _inFlight is { IsCompleted: false };
            }
        }
    }

    /// <summary>
    /// 发起或附着一次启动。返回的 <see cref="SingleFlightStart{T}.IsOwner"/> 为 true 时，启动委托由本次调用执行；
    /// 否则本次调用只等待已有启动。调用方对返回任务的取消只终止自己的等待。
    /// </summary>
    public SingleFlightStart<T> Start(Func<CancellationToken, Task<T>> start, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(start);

        TaskCompletionSource<T> completion;
        bool owner;
        lock (_gate)
        {
            if (_inFlight is { IsCompleted: false } existing)
            {
                return new SingleFlightStart<T>(existing, false);
            }

            completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlight = completion.Task;
            owner = true;
        }

        if (owner)
        {
            _ = ExecuteAsync(start, completion, completion.Task, ct);
        }
        return new SingleFlightStart<T>(completion.Task, owner);
    }

    private async Task ExecuteAsync(
        Func<CancellationToken, Task<T>> start,
        TaskCompletionSource<T> completion,
        Task<T> flight,
        CancellationToken ct)
    {
        try
        {
            completion.TrySetResult(await start(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException e)
        {
            completion.TrySetCanceled(e.CancellationToken);
        }
        catch (Exception e)
        {
            completion.TrySetException(e);
        }
        finally
        {
            lock (_gate)
            {
                // 只清理自己那次启动：若期间已有更新的启动（旧任务完成得晚），不能误清。
                if (ReferenceEquals(_inFlight, flight))
                {
                    _inFlight = null;
                }
            }
        }
    }
}
