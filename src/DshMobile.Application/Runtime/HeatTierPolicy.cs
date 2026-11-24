namespace DshMobile.Application.Runtime;

/// <summary>设备温控阶梯（N-08-04）：中度及以上温控抑制后台预热与非必要并发，但绝不强杀活跃前台会话。</summary>
public enum HeatTier
{
    Nominal = 0,
    Moderate = 1,
    Severe = 2,
    Critical = 3,
}

/// <summary>温控与电源协同治理建议快照。</summary>
public sealed record HeatGovernanceSnapshot(
    HeatTier Tier,
    bool AllowBackgroundWarmup,
    int MaxBackgroundConcurrency,
    bool LowBattery,
    bool PowerSave,
    float? ThermalHeadroom);

/// <summary>
/// <c>PowerManager.GetThermalHeadroom(30)</c> 的限频采样器（N-08-04）：
/// 严格保证任意两次真实系统查询的间隔不少于 30 秒，两次窗口之间返回上次缓存值。
/// </summary>
public sealed class ThermalHeadroomSampler
{
    public static readonly TimeSpan DefaultMinSampleInterval = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time;
    private readonly TimeSpan _minInterval;
    private readonly Lock _gate = new();

    private long? _lastSampleTimestamp;
    private float? _cachedHeadroom;

    public ThermalHeadroomSampler(TimeProvider? time = null, TimeSpan? minInterval = null)
    {
        _time = time ?? TimeProvider.System;
        _minInterval = minInterval ?? DefaultMinSampleInterval;
        if (_minInterval < DefaultMinSampleInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(minInterval), "GetThermalHeadroom 采样间隔不得低于 30 秒。");
        }
    }

    public float? CachedHeadroom
    {
        get
        {
            lock (_gate)
            {
                return _cachedHeadroom;
            }
        }
    }

    /// <summary>在满足至少 30 秒采样周期时调用 <paramref name="probe"/>，否则复用缓存读数。</summary>
    public float? Sample(Func<float?> probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            if (_lastSampleTimestamp is { } prev)
            {
                var elapsed = _time.GetElapsedTime(prev, now);
                if (now < prev || elapsed < TimeSpan.Zero)
                {
                    // 单调时钟发生回退时重新锚定，避免高频触发系统查询或沿用旧差值。
                    _lastSampleTimestamp = now;
                    return _cachedHeadroom;
                }
                if (elapsed < _minInterval)
                {
                    return _cachedHeadroom;
                }
            }

            var value = probe();
            _lastSampleTimestamp = now;
            if (value is { } v && !float.IsNaN(v))
            {
                _cachedHeadroom = v;
            }
            return _cachedHeadroom;
        }
    }
}

/// <summary>
/// 设备发热阶梯策略（N-08-04）：综合系统 ThermalStatus、限频 ThermalHeadroom 与电量/省电状态，
/// 决定是否允许后台预热及后台并发度；无论处于何种温控阶梯均不强杀用户活跃对话。
/// </summary>
public static class HeatTierPolicy
{
    /// <summary>
    /// 根据 Android <c>PowerManager.CurrentThermalStatus</c>（0=None, 1=Light, 2=Moderate, 3=Severe, 4+=Critical/Emergency/Shutdown）
    /// 与 <c>GetThermalHeadroom(30)</c> 评估当前温控阶梯与后台并发预算。
    /// </summary>
    public static HeatGovernanceSnapshot Evaluate(
        int androidThermalStatus,
        float? thermalHeadroom,
        bool lowBattery,
        bool powerSave)
    {
        var statusTier = androidThermalStatus switch
        {
            <= 1 => HeatTier.Nominal,
            2 => HeatTier.Moderate,
            3 => HeatTier.Severe,
            _ => HeatTier.Critical,
        };

        var headroomTier = thermalHeadroom switch
        {
            null => HeatTier.Nominal,
            >= 0.98f => HeatTier.Critical,
            >= 0.92f => HeatTier.Severe,
            >= 0.85f => HeatTier.Moderate,
            _ => HeatTier.Nominal,
        };

        var tier = (HeatTier)Math.Max((int)statusTier, (int)headroomTier);
        var allowWarmup = tier == HeatTier.Nominal && !lowBattery && !powerSave;
        var concurrency = tier switch
        {
            HeatTier.Nominal => (lowBattery || powerSave) ? 2 : 4,
            HeatTier.Moderate => 2,
            HeatTier.Severe => 1,
            HeatTier.Critical => 1,
            _ => 1,
        };

        return new HeatGovernanceSnapshot(tier, allowWarmup, concurrency, lowBattery, powerSave, thermalHeadroom);
    }
}
