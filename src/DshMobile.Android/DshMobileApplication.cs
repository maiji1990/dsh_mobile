using Android.App;
using Android.Runtime;
using DshMobile.App.Bridge;

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
        // ADR-0021: Material You colours from the wallpaper on Android 12+; elsewhere the brand scheme in colors.xml.
        Google.Android.Material.Color.DynamicColors.ApplyToActivitiesIfAvailable(this);
        _bridge = new BridgeHost(this);
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
        // 记录可见 Activity 计数，精准驱动后台审批/问答系统通知通道（N-06-04）
        if (Interlocked.Increment(ref _startedActivities) == 1)
        {
            _bridge?.Agent.RequestNotifications.SetAppInBackground(false);
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
            _bridge?.Agent.RequestNotifications.SetAppInBackground(true);
        }
    }
}
