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
        // DSHBox's browser runtime (ADR-0005) runs in its own `:browser` process. There we only give WebView a
        // separate data directory (cookies/storage isolated from the DSH WebUI in the main process) and stop:
        // no bridge, no runtime, no services. Must run before anything in that process touches WebView.
        if (IsBrowserProcess())
        {
            Android.Webkit.WebView.SetDataDirectorySuffix("browser");
            return;
        }
        _bridge = new BridgeHost(this);
    }

    private bool IsBrowserProcess() =>
        OperatingSystem.IsAndroidVersionAtLeast(28) && global::Android.App.Application.ProcessName == PackageName + ":browser";
}
