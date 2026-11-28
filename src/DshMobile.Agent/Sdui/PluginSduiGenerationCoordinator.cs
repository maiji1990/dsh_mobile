using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;

namespace DshMobile.Agent.Sdui;

/// <summary>
/// 某个插件 SDUI 生成任务的不可变快照。生成尚未开始时 <see cref="Result"/> 为 <see langword="null"/>；
/// 生成结束后 <see cref="Result"/> 为本次生成的稳定结果。
/// </summary>
public sealed record PluginSduiGenerationSnapshot(
    string PackageName,
    bool IsGenerating,
    PluginSduiLoadResult? Result,
    DateTimeOffset? UpdatedAtUtc);

/// <summary>
/// 插件 SDUI 生成协调器（第 4 项精修）：以包名为粒度合并并复用后台生成任务。
/// <list type="bullet">
/// <item>生成使用协调器自身的生命周期令牌，页面销毁不会取消后台生成；</item>
/// <item>同包名正在生成时直接返回同一个在途 <see cref="Task{TResult}"/>，绝不重复创建辅助会话；</item>
/// <item>快照与事件在锁外发布，订阅方异常不会中断生成；</item>
/// <item>未预期异常收敛为稳定诊断 <see cref="PluginSduiGeneratorService.WaitFailedDiagnostic"/>，绝不暴露异常消息。</item>
/// </list>
/// </summary>
public sealed class PluginSduiGenerationCoordinator : IDisposable
{
    private readonly PluginSduiGeneratorService _generator;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Task<PluginSduiGenerationSnapshot>> _inFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginSduiGenerationSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _disposeCts = new();
    private int _disposed;

    /// <summary>创建协调器；内部持有并复用同一个 <see cref="PluginSduiGeneratorService"/>。</summary>
    /// <param name="gateway">DSH 网关。</param>
    /// <param name="store">SDUI 版本化缓存存储。</param>
    /// <param name="hiddenSessionStore">可选的隐藏会话存储，用于避免辅助会话出现在用户会话列表。</param>
    /// <param name="timeout">单次生成总超时；为空时沿用生成服务默认值。</param>
    public PluginSduiGenerationCoordinator(
        IDshGateway gateway,
        PluginSduiStore store,
        HiddenSessionStore? hiddenSessionStore = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(store);
        _generator = new PluginSduiGeneratorService(gateway, store, hiddenSessionStore, timeout);
    }

    /// <summary>快照变化通知（在锁外同步触发；订阅方不得阻塞，异常会被隔离）。</summary>
    public event Action<PluginSduiGenerationSnapshot>? StateChanged;

    /// <summary>
    /// 读取某个包的当前快照；没有记录时返回 <c>IsGenerating = false</c> 且 <c>Result = null</c>。
    /// </summary>
    public PluginSduiGenerationSnapshot GetSnapshot(string packageName)
    {
        var key = NormalizePackageName(packageName);
        lock (_lock)
        {
            return _snapshots.TryGetValue(key, out var snapshot)
                ? snapshot
                : new PluginSduiGenerationSnapshot(key, IsGenerating: false, Result: null, UpdatedAtUtc: null);
        }
    }

    /// <summary>
    /// 启动或加入某个包的生成任务：同包名生成中时直接返回同一个在途任务；
    /// 否则立即发布 <c>IsGenerating = true</c> 快照并在协调器生命周期内后台生成。
    /// </summary>
    public Task<PluginSduiGenerationSnapshot> StartOrJoinAsync(
        string packageName,
        string? expectedVersion = null,
        string? settingsNamespaceHint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        var key = NormalizePackageName(packageName);

        PluginSduiGenerationSnapshot started;
        TaskCompletionSource<PluginSduiGenerationSnapshot> completion;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);

            if (_inFlight.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var previous = _snapshots.TryGetValue(key, out var snapshot) ? snapshot.Result : null;
            started = new PluginSduiGenerationSnapshot(
                key,
                IsGenerating: true,
                Result: previous,
                UpdatedAtUtc: DateTimeOffset.UtcNow);
            _snapshots[key] = started;

            completion = new TaskCompletionSource<PluginSduiGenerationSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlight[key] = completion.Task;
        }

        // 发布开始快照后再真正进入异步生成，保证订阅方先看到 IsGenerating = true。
        Publish(started);
        _ = GenerateAndCompleteAsync(key, expectedVersion, settingsNamespaceHint, completion);
        return completion.Task;
    }

    /// <summary>
    /// 清除某个包的旧生成结果并通知订阅者；该包仍在生成中时保持不变。
    /// </summary>
    public void ClearSnapshot(string packageName)
    {
        var key = NormalizePackageName(packageName);
        PluginSduiGenerationSnapshot cleared;
        lock (_lock)
        {
            if (_inFlight.ContainsKey(key) ||
                !_snapshots.TryGetValue(key, out var current) ||
                current.Result is null)
            {
                // 正在生成、没有记录或已经是空结果：无需清空或重复通知。
                return;
            }

            cleared = new PluginSduiGenerationSnapshot(
                key,
                IsGenerating: false,
                Result: null,
                UpdatedAtUtc: DateTimeOffset.UtcNow);
            _snapshots[key] = cleared;
        }

        Publish(cleared);
    }

    /// <summary>取消协调器生命周期内的全部生成并安全清理；已完成或取消的任务不会再发布结束快照。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Task<PluginSduiGenerationSnapshot>[] pending;
        lock (_lock)
        {
            pending = _inFlight.Values.ToArray();
        }

        // 取消会让生成服务以 OperationCanceledException 结束；先收集在途任务再取消，避免漏掉清理。
        _disposeCts.Cancel();

        if (pending.Length == 0)
        {
            _disposeCts.Dispose();
            return;
        }

        // 仍在生成的调用方可能继续使用令牌，因此等任务全部收敛后再释放 CTS。
        _ = Task.WhenAll(pending).ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            _disposeCts,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task GenerateAndCompleteAsync(
        string packageName,
        string? expectedVersion,
        string? settingsNamespaceHint,
        TaskCompletionSource<PluginSduiGenerationSnapshot> completion)
    {
        PluginSduiLoadResult? result = null;
        var canceled = false;
        try
        {
            result = await _generator
                .GenerateDetailedAsync(
                    packageName,
                    expectedVersion,
                    settingsNamespaceHint,
                    _disposeCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 协调器释放导致的取消属于正常停止：不向调用方暴露任何异常消息。
            canceled = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 未预期异常收敛为稳定诊断，绝不把异常消息带入快照或 UI。
            result = new PluginSduiLoadResult(
                false,
                DiagnosticKey: PluginSduiGeneratorService.WaitFailedDiagnostic);
        }

        PluginSduiGenerationSnapshot final;
        lock (_lock)
        {
            _inFlight.Remove(packageName);
            final = new PluginSduiGenerationSnapshot(
                packageName,
                IsGenerating: false,
                Result: result,
                UpdatedAtUtc: DateTimeOffset.UtcNow);
            _snapshots[packageName] = final;
        }

        Publish(final);

        if (canceled)
        {
            completion.TrySetCanceled(_disposeCts.Token);
        }
        else
        {
            completion.TrySetResult(final);
        }
    }

    private void Publish(PluginSduiGenerationSnapshot snapshot)
    {
        var handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }

        // 锁外触发；逐个隔离订阅方异常，避免单个订阅者拖垮生成或释放路径。
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<PluginSduiGenerationSnapshot>)handler)(snapshot);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 订阅方异常不得中断生成或释放流程；取消属于控制流，必须透传而不是被吞掉。
            }
        }
    }

    private static string NormalizePackageName(string? packageName) =>
        string.IsNullOrWhiteSpace(packageName) ? string.Empty : packageName.Trim();
}
