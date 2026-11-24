using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using DshMobile.App.Ui;
using DshMobile.Application.Runtime;
using DshMobile.Runtime;
using DshMobile.Runtime.Supervision;

namespace DshMobile.App.Governance;

/// <summary>
/// Android 系统治理与可靠性桥接（PR-08: N-08-02 / N-08-03 / N-08-04）：
/// 1. 维护 <see cref="WorkLeaseManager"/> 与后台空闲优雅停机检测；
/// 2. 经 <see cref="PhantomProcessMonitor"/> 监测 Android 12+ 32 幻影进程预算并在 API 34+ 提供系统设置引导（只引导不篡改设置）；
/// 3. 启动读取 <see cref="ActivityManager.GetHistoricalProcessExitReasons"/> 识别 Android 17 <c>MemoryLimiter:AnonSwap</c>（绝不存储原始系统描述）；
/// 4. 注册温控监听并限频采样 <c>PowerManager.GetThermalHeadroom(30)</c>（≥30s），经 <see cref="HeatTierPolicy"/> 约束后台预热。
/// </summary>
public sealed class SystemGovernanceMonitor : Java.Lang.Object, PowerManager.IOnThermalStatusChangedListener, IDisposable
{
    private readonly Context _context;
    private readonly Action<string> _log;
    private readonly TimeProvider _time;
    private readonly ThermalHeadroomSampler _headroomSampler;
    private readonly Lock _gate = new();

    private PowerManager? _powerManager;
    private bool _thermalRegistered;
    private int _androidThermalStatus;
    private bool _inBackground;
    private long? _backgroundEnteredTimestamp;
    private HistoricalExitAssessment _lastExit = new(HistoricalExitKind.None, null, null);
    private int _disposed;

    public SystemGovernanceMonitor(Context context, Action<string> log, TimeProvider? time = null)
    {
        _context = context.ApplicationContext ?? context;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _time = time ?? TimeProvider.System;
        _headroomSampler = new ThermalHeadroomSampler(_time);
        Leases = new WorkLeaseManager(_time);
        RegisterThermalListener();
    }

    public WorkLeaseManager Leases { get; }

    public HistoricalExitAssessment LastExitAssessment
    {
        get
        {
            lock (_gate)
            {
                return _lastExit;
            }
        }
    }

    /// <summary>记录 App 前后台切换以驱动 <see cref="IdleStopPolicy"/> 单调时钟统计。</summary>
    public void SetAppInBackground(bool inBackground)
    {
        lock (_gate)
        {
            if (_inBackground == inBackground)
            {
                return;
            }
            _inBackground = inBackground;
            _backgroundEnteredTimestamp = inBackground ? _time.GetTimestamp() : null;
        }
    }

    /// <summary>返回当前后台持续空闲时长；若单调时钟回退则清零重锚定，避免误触发停机。</summary>
    public TimeSpan GetBackgroundDuration()
    {
        lock (_gate)
        {
            if (!_inBackground || _backgroundEnteredTimestamp is not { } entered)
            {
                return TimeSpan.Zero;
            }
            var now = _time.GetTimestamp();
            var elapsed = _time.GetElapsedTime(entered, now);
            if (now < entered || elapsed < TimeSpan.Zero)
            {
                _backgroundEnteredTimestamp = now;
                return TimeSpan.Zero;
            }
            return elapsed;
        }
    }

    /// <summary>按 <see cref="IdleStopPolicy"/> 判定当前是否满足后台空闲优雅停机全部前置条件。</summary>
    public IdleStopDecision EvaluateIdleStop(bool hostRunning, bool agentIdle, TimeSpan? threshold = null)
    {
        bool inBackground;
        lock (_gate)
        {
            inBackground = _inBackground;
        }
        return IdleStopPolicy.Decide(
            hostRunning: hostRunning,
            inBackground: inBackground,
            hasActiveLease: Leases.HasActiveLease,
            agentIdle: agentIdle,
            backgroundIdleDuration: GetBackgroundDuration(),
            threshold: threshold);
    }

    /// <summary>读取当前 UID 子进程数与 32 幻影进程预算水位（只读观察）。</summary>
    public static PhantomProcessReport ObservePhantomProcessBudget() =>
        PhantomProcessMonitor.Observe(Android.OS.Process.MyPid());

    /// <summary>
    /// 在 API 34+ 上一键跳转开发者选项引导用户自行核对幻影进程限制（绝不修改任何系统设置）。
    /// </summary>
    public static bool OpenDeveloperOptionsForPhantomLimit(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var intent = Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake
                ? new Intent(Settings.ActionApplicationDevelopmentSettings)
                : new Intent(Settings.ActionSettings);
            intent.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(intent);
            return true;
        }
        catch (Exception e) when (e is ActivityNotFoundException or Java.Lang.SecurityException)
        {
            try
            {
                var fallback = new Intent(Settings.ActionSettings);
                fallback.AddFlags(ActivityFlags.NewTask);
                context.StartActivity(fallback);
                return true;
            }
            catch (Exception ex) when (ex is ActivityNotFoundException or Java.Lang.SecurityException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 启动恢复时检查最近历史进程退出原因，识别 <c>MemoryLimiter:AnonSwap</c> 并仅存安全枚举/键（绝不保存原始描述，N-08-04）。
    /// </summary>
    public void InspectStartupExitReason()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            return;
        }
        try
        {
            var am = _context.GetSystemService(Context.ActivityService) as ActivityManager;
            var reasons = am?.GetHistoricalProcessExitReasons(_context.PackageName, 0, 5);
            if (reasons is null || reasons.Count == 0)
            {
                return;
            }

            HistoricalExitAssessment? detected = null;
            foreach (var info in reasons)
            {
                var ts = DateTimeOffset.FromUnixTimeMilliseconds(info.Timestamp);
                var assessment = HistoricalExitClassifier.Classify((int)info.Reason, info.Description, ts);
                if (assessment.Kind == HistoricalExitKind.MemoryLimited)
                {
                    detected = assessment;
                    break;
                }
                detected ??= assessment;
            }

            if (detected is not null)
            {
                lock (_gate)
                {
                    _lastExit = detected;
                }
                if (detected.Kind == HistoricalExitKind.MemoryLimited)
                {
                    _log($"governance: previous process terminated ({RuntimeErrorCodes.MemoryLimited} / {detected.SafeDiagnosticKey})");
                }
            }
        }
        catch (Exception e) when (e is Java.Lang.Throwable or InvalidOperationException or UnauthorizedAccessException)
        {
            _log($"governance: exit reason inspection skipped ({e.GetType().Name})");
        }
    }

    /// <summary>采样并计算当前温控与电源治理阶梯（<c>GetThermalHeadroom</c> 受 ≥30s 限频保护）。</summary>
    public HeatGovernanceSnapshot CurrentHeatGovernance()
    {
        var (lowBattery, powerSave) = UiPrefs.WarmupPower(_context);
        int status;
        lock (_gate)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(29) && _powerManager is not null)
            {
                try
                {
                    _androidThermalStatus = (int)_powerManager.CurrentThermalStatus;
                }
                catch (Java.Lang.Throwable)
                {
                }
            }
            status = _androidThermalStatus;
        }

        var headroom = _headroomSampler.Sample(ProbeThermalHeadroom);
        return HeatTierPolicy.Evaluate(status, headroom, lowBattery, powerSave);
    }

    public void OnThermalStatusChanged(ThermalStatus status)
    {
        lock (_gate)
        {
            _androidThermalStatus = (int)status;
        }
    }

    private float? ProbeThermalHeadroom()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30) || _powerManager is null)
        {
            return null;
        }
        try
        {
            var value = _powerManager.GetThermalHeadroom(30);
            return float.IsNaN(value) ? null : value;
        }
        catch (Java.Lang.Throwable)
        {
            return null;
        }
    }

    private void RegisterThermalListener()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            return;
        }
        try
        {
            _powerManager = _context.GetSystemService(Context.PowerService) as PowerManager;
            if (_powerManager is not null)
            {
                _androidThermalStatus = (int)_powerManager.CurrentThermalStatus;
                _powerManager.AddThermalStatusListener(this);
                _thermalRegistered = true;
            }
        }
        catch (Exception e) when (e is Java.Lang.Throwable or InvalidOperationException)
        {
            _log($"governance: thermal listener unavailable ({e.GetType().Name})");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            if (_thermalRegistered && OperatingSystem.IsAndroidVersionAtLeast(29) && _powerManager is not null)
            {
                try
                {
                    _powerManager.RemoveThermalStatusListener(this);
                }
                catch (Java.Lang.Throwable)
                {
                }
            }
            Leases.ClearAll();
        }
        base.Dispose(disposing);
    }
}
