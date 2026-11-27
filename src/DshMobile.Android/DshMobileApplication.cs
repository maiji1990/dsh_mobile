using Android.App;
using Android.Runtime;
using DshMobile.App.Bridge;
using DshMobile.App.Ui;

namespace DshMobile.App;

/// <summary>
/// Owns process-lifetime services. Must stay cheap: no root, no network, no runtime start here (plan 6.3).
/// </summary>
[global::Android.App.Application]
public sealed class DshMobileApplication : global::Android.App.Application, global::Android.App.Application.IActivityLifecycleCallbacks
{
    private BridgeHost? _bridge;
    private int _startedActivities;

    public DshMobileApplication(IntPtr handle, JniHandleOwnership transfer)
        : base(handle, transfer)
    {
    }

    public static DshMobileApplication Current { get; private set; } = null!;

    public BridgeHost Bridge => _bridge ?? throw new InvalidOperationException("Application not created yet");

    public override void OnCreate()
    {
        base.OnCreate();
        Current = this;
        // Polish-P1：先应用用户选择的外观模式（跟随系统 / 浅色 / 深色），再决定是否启用壁纸动态取色。
        UiPrefs.ApplyThemeMode(this);
        // 壁纸动态取色默认关闭，避免医院灰绿等壁纸污染品牌色盘；仅在用户显式开启时应用。
        if (UiPrefs.DynamicColorEnabled(this))
        {
            Google.Android.Material.Color.DynamicColors.ApplyToActivitiesIfAvailable(this);
        }
        _bridge = new BridgeHost(this);
        _ = Task.Run(() => _bridge.Governance.InspectStartupExitReason());
        RegisterActivityLifecycleCallbacks(this);
    }

    public void OnActivityCreated(Activity activity, Bundle? savedInstanceState)
    {
    }

    public void OnActivityDestroyed(Activity activity)
    {
    }

    public void OnActivityPaused(Activity activity)
    {
    }

    public void OnActivityResumed(Activity activity)
    {
    }

    public void OnActivitySaveInstanceState(Activity activity, Bundle outState)
    {
    }

    public void OnActivityStarted(Activity activity)
    {
        // 记录可见 Activity 计数，精准驱动后台审批/问答系统通知通道与空闲停机检测（N-06-04 / N-08-02）
        if (Interlocked.Increment(ref _startedActivities) == 1)
        {
            _bridge?.OnAppBackgroundChanged(false);
        }
    }

    public void OnActivityStopped(Activity activity)
    {
        if (activity.IsChangingConfigurations)
        {
            return;
        }
        if (Interlocked.Decrement(ref _startedActivities) <= 0)
        {
            Volatile.Write(ref _startedActivities, 0);
            _bridge?.OnAppBackgroundChanged(true);
        }
    }
}
