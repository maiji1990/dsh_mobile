using Android.Runtime;
using DshMobile.App.Bridge;

namespace DshMobile.App;

/// <summary>
/// Owns process-lifetime services. Must stay cheap: no root, no network, no runtime start here (plan 6.3).
/// </summary>
[global::Android.App.Application]
public sealed class DshMobileApplication : global::Android.App.Application
{
    private BridgeHost? _bridge;

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
        _bridge = new BridgeHost(this);
    }
}
