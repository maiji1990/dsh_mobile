namespace DshMobile.Application.Runtime;

/// <summary>工作租约类别（N-08-02）：覆盖正在进行的聊天、审批、环境准备、在线更新与历史校准。</summary>
public enum WorkLeaseKind
{
    Chat,
    Approval,
    Setup,
    Update,
    Reconcile,
}

/// <summary>工作租约时间上限配置（单租约上限与连续忙碌总上限）。</summary>
public sealed record WorkLeaseOptions(
    TimeSpan SingleLeaseCap,
    TimeSpan ContinuousTotalCap)
{
    public static WorkLeaseOptions Default { get; } = new(
        SingleLeaseCap: TimeSpan.FromMinutes(10),
        ContinuousTotalCap: TimeSpan.FromMinutes(30));
}

/// <summary>活跃租约的只读快照。</summary>
public sealed record WorkLeaseInfo(
    long Id,
    WorkLeaseKind Kind,
    string? Label,
    TimeSpan Elapsed);

/// <summary>
/// 后台保活与空闲停机仲裁器使用的工作租约管理器（N-08-02）：
/// 为 <see cref="WorkLeaseKind"/> 各阶段提供单租约上限与连续活跃总上限；若单调时钟出现倒退或负差值，立即清空全部活跃租约并归零计数。
/// </summary>
public sealed class WorkLeaseManager
{
    private sealed record ActiveLease(long Id, WorkLeaseKind Kind, string? Label, long AcquiredTimestamp);

    private readonly TimeProvider _time;
    private readonly WorkLeaseOptions _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<long, ActiveLease> _active = [];

    private long _nextId;
    private long _lastTimestamp;
    private long? _continuousStartTimestamp;
    private int _clockRegressionResets;

    public WorkLeaseManager(TimeProvider? time = null, WorkLeaseOptions? options = null)
    {
        _time = time ?? TimeProvider.System;
        _options = options ?? WorkLeaseOptions.Default;
        if (_options.SingleLeaseCap <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SingleLeaseCap must be positive.");
        }
        if (_options.ContinuousTotalCap <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ContinuousTotalCap must be positive.");
        }
        _lastTimestamp = _time.GetTimestamp();
    }

    public event EventHandler? LeasesChanged;

    /// <summary>累计触发单调时钟倒退/负值归零的次数（便于诊断与测试验证）。</summary>
    public int ClockRegressionResetCount
    {
        get
        {
            lock (_gate)
            {
                return _clockRegressionResets;
            }
        }
    }

    /// <summary>在修剪过期/超限租约后，当前是否仍有活跃租约。</summary>
    public bool HasActiveLease
    {
        get
        {
            bool changed;
            bool active;
            lock (_gate)
            {
                changed = PruneLocked();
                active = _active.Count > 0;
            }
            if (changed)
            {
                LeasesChanged?.Invoke(this, EventArgs.Empty);
            }
            return active;
        }
    }

    /// <summary>在修剪过期/超限租约后，返回当前活跃租约数量。</summary>
    public int ActiveCount
    {
        get
        {
            bool changed;
            int count;
            lock (_gate)
            {
                changed = PruneLocked();
                count = _active.Count;
            }
            if (changed)
            {
                LeasesChanged?.Invoke(this, EventArgs.Empty);
            }
            return count;
        }
    }

    /// <summary>返回当前仍有效的活跃租约只读快照。</summary>
    public IReadOnlyList<WorkLeaseInfo> Snapshot()
    {
        bool changed;
        List<WorkLeaseInfo> items;
        lock (_gate)
        {
            changed = PruneLocked();
            var now = _time.GetTimestamp();
            items = new List<WorkLeaseInfo>(_active.Count);
            foreach (var lease in _active.Values)
            {
                var elapsed = _time.GetElapsedTime(lease.AcquiredTimestamp, now);
                items.Add(new WorkLeaseInfo(lease.Id, lease.Kind, lease.Label, elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed));
            }
        }
        if (changed)
        {
            LeasesChanged?.Invoke(this, EventArgs.Empty);
        }
        return items;
    }

    /// <summary>申请指定类型的临时工作租约；调用方必须在工作结束或取消时释放返回的句柄。</summary>
    public IDisposable Acquire(WorkLeaseKind kind, string? label = null)
    {
        long leaseId;
        lock (_gate)
        {
            PruneLocked();
            var now = _time.GetTimestamp();
            leaseId = ++_nextId;
            if (_active.Count == 0 || _continuousStartTimestamp is null)
            {
                _continuousStartTimestamp = now;
            }
            _active[leaseId] = new ActiveLease(leaseId, kind, label, now);
        }
        LeasesChanged?.Invoke(this, EventArgs.Empty);
        return new LeaseHandle(this, leaseId);
    }

    /// <summary>主动清理已到期或超限的租约，并返回清理后的活跃计数。</summary>
    public int Prune()
    {
        bool changed;
        int count;
        lock (_gate)
        {
            changed = PruneLocked();
            count = _active.Count;
        }
        if (changed)
        {
            LeasesChanged?.Invoke(this, EventArgs.Empty);
        }
        return count;
    }

    /// <summary>主动释放全部租约并重置连续忙碌计时器（如宿主停止或断线清理时调用）。</summary>
    public void ClearAll()
    {
        bool changed;
        lock (_gate)
        {
            changed = _active.Count > 0 || _continuousStartTimestamp is not null;
            _active.Clear();
            _continuousStartTimestamp = null;
            _lastTimestamp = _time.GetTimestamp();
        }
        if (changed)
        {
            LeasesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void Release(long leaseId)
    {
        bool changed;
        lock (_gate)
        {
            changed = _active.Remove(leaseId);
            if (_active.Count == 0)
            {
                _continuousStartTimestamp = null;
            }
            PruneLocked();
        }
        if (changed)
        {
            LeasesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool PruneLocked()
    {
        var now = _time.GetTimestamp();
        var stepElapsed = _time.GetElapsedTime(_lastTimestamp, now);
        // 若单调时钟出现倒退或负间隔，立即清空并归零以防止无限租约阻止后台空闲停机。
        if (now < _lastTimestamp || stepElapsed < TimeSpan.Zero)
        {
            var hadAny = _active.Count > 0 || _continuousStartTimestamp is not null;
            _active.Clear();
            _continuousStartTimestamp = null;
            _lastTimestamp = now;
            _clockRegressionResets++;
            return hadAny;
        }
        _lastTimestamp = now;

        if (_active.Count == 0)
        {
            _continuousStartTimestamp = null;
            return false;
        }

        if (_continuousStartTimestamp is { } continuousStart)
        {
            var continuousElapsed = _time.GetElapsedTime(continuousStart, now);
            if (continuousElapsed < TimeSpan.Zero)
            {
                _active.Clear();
                _continuousStartTimestamp = null;
                _clockRegressionResets++;
                return true;
            }
            if (continuousElapsed >= _options.ContinuousTotalCap)
            {
                _active.Clear();
                _continuousStartTimestamp = null;
                return true;
            }
        }

        List<long>? expired = null;
        foreach (var (id, lease) in _active)
        {
            var elapsed = _time.GetElapsedTime(lease.AcquiredTimestamp, now);
            if (elapsed < TimeSpan.Zero || elapsed >= _options.SingleLeaseCap)
            {
                expired ??= [];
                expired.Add(id);
            }
        }

        if (expired is null)
        {
            return false;
        }

        foreach (var id in expired)
        {
            _active.Remove(id);
        }
        if (_active.Count == 0)
        {
            _continuousStartTimestamp = null;
        }
        return true;
    }

    private sealed class LeaseHandle(WorkLeaseManager owner, long id) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(id);
            }
        }
    }
}
