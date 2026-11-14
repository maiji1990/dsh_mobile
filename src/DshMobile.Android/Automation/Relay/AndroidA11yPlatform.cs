// Android half of the A11y backend port. Translated from DSHBox pilot/interlock-relay-core (GPL-3.0,
// v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/exec/a11y/A11yBackend.kt (dispatchStroke / dispatchAwaited, screenshot,
//     mapScreenshotError, the host-window scan over service.windows)
//   src/main/java/interlock/relay/core/exec/a11y/DisplayTree.kt (ofDisplay, rootNow, pickRoot, displayMetrics)
// Registered in docs/UPSTREAMS.md. Decisions live in DshMobile.Runtime (A11yBackend.cs); this file is platform calls.

using Android.AccessibilityServices;
using Android.Content;
using Android.Graphics;
using Android.Hardware.Display;
using Android.Runtime;
using Android.Views;
using Android.Views.Accessibility;
using DshMobile.Runtime.Automation.Relay;
using DshMobile.Runtime.Automation.Relay.A11y;
using Path = Android.Graphics.Path;

namespace DshMobile.App.Automation.Relay;

internal sealed class AndroidA11yPlatform(Context context) : IA11yPlatform
{
    private readonly Context _app = context.ApplicationContext ?? context;

    private static AccessibilityService? Service => RelayA11yTreeSource.Current();

    public bool Connected => Service is not null;

    public string SelfPackage => _app.PackageName!;

    public int Sdk => (int)Android.OS.Build.VERSION.SdkInt;

    public Bounds DisplayBounds() => RelayA11yTreeSource.DisplayBounds(_app);

    public int DensityDpi => (int)_app.Resources!.DisplayMetrics!.DensityDpi;

    public IA11yNode? RootInActiveWindow() => Service?.RootInActiveWindow is { } root ? new A11yNodeAdapter(root, 0) : null;

    public IReadOnlyList<string?> WindowRootPackages()
    {
        if (Service is not { } service)
        {
            return [];
        }
        var packages = new List<string?>();
        foreach (var window in service.Windows ?? [])
        {
            packages.Add(window.Root?.PackageName);
        }
        // ADR-0011: our floating approval card counts as one of this app's windows even if the system leaves it out of
        // the list (or the list lags its removal), so the agent can never press it.
        if (AndroidOverlayApproval.OnScreen)
        {
            packages.Add(SelfPackage);
        }
        return packages;
    }

    public TreeLook TreeOfDisplay(int displayId)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            return new TreeLook.QueryFailed("below-api-33");
        }
        if (Service is not { } service)
        {
            return new TreeLook.QueryFailed("NullPointerException:no service");
        }
        IList<AccessibilityWindowInfo>? windows;
        try
        {
            windows = WindowsOn(service, displayId);
        }
        catch (Java.Lang.Throwable t)
        {
            return new TreeLook.QueryFailed(Describe(t));
        }
        if (windows is null || PickRoot(windows) is not { } root)
        {
            return new TreeLook.NoWindowOnDisplay();
        }
        Bounds screen;
        var density = 0;
        if (DisplayMetricsOf(displayId) is { } metrics)
        {
            (screen, density) = metrics;
        }
        else
        {
            var r = new Rect();
            root.GetBoundsInScreen(r);
            screen = new Bounds(r.Left, r.Top, r.Right, r.Bottom);
        }
        return new TreeLook.Has(new A11yTree(displayId, screen, density, () => RootNow(displayId)));
    }

    private static IA11yNode? RootNow(int displayId)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33) || Service is not { } service)
        {
            return null;
        }
        try
        {
            return WindowsOn(service, displayId) is { } windows && PickRoot(windows) is { } root ? new A11yNodeAdapter(root, 0) : null;
        }
        catch (Java.Lang.Throwable)
        {
            return null;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android33.0")]
    private static IList<AccessibilityWindowInfo>? WindowsOn(AccessibilityService service, int displayId)
    {
        var all = service.WindowsOnAllDisplays;
        if (all?.Get(displayId) is not { } list)
        {
            return null;
        }
        var javaList = list.JavaCast<Java.Util.IList>()!;
        var windows = new List<AccessibilityWindowInfo>(javaList.Size());
        for (var i = 0; i < javaList.Size(); i++)
        {
            if (javaList.Get(i)?.JavaCast<AccessibilityWindowInfo>() is { } w)
            {
                windows.Add(w);
            }
        }
        return windows;
    }

    /// <summary>Upstream <c>pickRoot</c>: the focused window, else the first application window, else any root.</summary>
    private static AccessibilityNodeInfo? PickRoot(IList<AccessibilityWindowInfo> windows) =>
        windows.FirstOrDefault(w => w.IsFocused)?.Root
        ?? windows.FirstOrDefault(w => w.Type == AccessibilityWindowType.Application)?.Root
        ?? windows.Select(w => w.Root).FirstOrDefault(r => r is not null);

    private (Bounds, int)? DisplayMetricsOf(int displayId)
    {
        if (_app.GetSystemService(Context.DisplayService) is not DisplayManager manager || manager.GetDisplay(displayId) is not { } display)
        {
            return null;
        }
        var metrics = new Android.Util.DisplayMetrics();
#pragma warning disable CA1422 // per-display real size has no newer public replacement (upstream suppresses the same)
        display.GetRealMetrics(metrics);
#pragma warning restore CA1422
        if (metrics.WidthPixels <= 0 || metrics.HeightPixels <= 0)
        {
            return null;
        }
        return (new Bounds(0, 0, metrics.WidthPixels, metrics.HeightPixels), (int)metrics.DensityDpi);
    }

    private static string Describe(Java.Lang.Throwable t)
    {
        var message = t.Message is { } m ? (m.Length > 120 ? m[..120] : m).Replace('\n', ' ') : "no-message";
        return t.Class.SimpleName + ":" + message;
    }

    /// <summary>
    /// Upstream <c>dispatchAwaited</c>: only onCompleted counts as done; onCancelled and an outright refusal by
    /// <c>dispatchGesture</c> are failures (otherwise the agent would carry on as if it worked).
    /// </summary>
    public Task<string?> DispatchStrokeAsync(float fromX, float fromY, float toX, float toY, long durationMs, CancellationToken ct)
    {
        if (Service is not { } service)
        {
            return Task.FromResult<string?>("rejected");
        }
        var path = new Path();
        path.MoveTo(fromX, fromY);
        if (fromX != toX || fromY != toY)
        {
            path.LineTo(toX, toY);
        }
        var description = new GestureDescription.Builder()
            .AddStroke(new GestureDescription.StrokeDescription(path, 0L, durationMs))
            .Build();
        var settled = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callback = new StrokeCallback(settled);
        bool accepted;
        try
        {
            accepted = service.DispatchGesture(description, callback, null);
        }
        catch (Java.Lang.Throwable)
        {
            accepted = false;
        }
        if (!accepted)
        {
            settled.TrySetResult("rejected");
        }
        return settled.Task.WaitAsync(ct);
    }

    private sealed class StrokeCallback(TaskCompletionSource<string?> settled) : AccessibilityService.GestureResultCallback
    {
        public override void OnCompleted(GestureDescription? gestureDescription) => settled.TrySetResult(null);

        public override void OnCancelled(GestureDescription? gestureDescription) => settled.TrySetResult("cancelled");
    }

    public bool PerformGlobalAction(int action)
    {
        try
        {
            return Service?.PerformGlobalAction((GlobalAction)action) == true;
        }
        catch (Java.Lang.Throwable)
        {
            return false;
        }
    }

    /// <summary>Upstream <c>screenshot</c> + the hardware → software copy before compressing (API 30+).</summary>
    public Task<A11yShot> TakeScreenshotAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30) || Service is not { } service)
        {
            return Task.FromResult<A11yShot>(new A11yShot.Refused(RelayError.GateSystemMissing, "no service"));
        }
        var settled = new TaskCompletionSource<A11yShot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = Java.Util.Concurrent.Executors.NewSingleThreadExecutor()!;
        try
        {
            service.TakeScreenshot(Display.DefaultDisplay, executor, new ShotCallback(settled));
        }
        catch (Java.Lang.Throwable t)
        {
            settled.TrySetResult(new A11yShot.Refused(RelayError.GateSystemMissing, Describe(t)));
        }
        return settled.Task.WaitAsync(ct).ContinueWith(t =>
        {
            executor.Shutdown();
            return t;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android30.0")]
    private sealed class ShotCallback(TaskCompletionSource<A11yShot> settled) : Java.Lang.Object, AccessibilityService.ITakeScreenshotCallback
    {
        public void OnSuccess(AccessibilityService.ScreenshotResult result)
        {
            Bitmap? hardware = null;
            try
            {
                hardware = Bitmap.WrapHardwareBuffer(result.HardwareBuffer, result.ColorSpace);
            }
            catch (Java.Lang.Throwable)
            {
            }
            // The gralloc buffer is ours: wrapHardwareBuffer only references it.
            try
            {
                result.HardwareBuffer.Close();
            }
            catch (Java.Lang.Throwable)
            {
            }
            if (hardware is null)
            {
                settled.TrySetResult(new A11yShot.Refused(RelayError.SurfaceSecureWindow, "wrap failed"));
                return;
            }
            var width = hardware.Width;
            var height = hardware.Height;
            // Hardware bitmaps cannot be compressed: copy to software when writing, recycle both afterwards.
            var shot = new A11yShot.Taken(width, height, path =>
            {
                Bitmap? software = null;
                try
                {
                    software = hardware.Copy(Bitmap.Config.Argb8888!, false);
                }
                catch (Java.Lang.Throwable)
                {
                }
                if (software is null)
                {
                    return -1;
                }
                try
                {
                    using (var stream = File.Create(path))
                    {
                        if (!software.Compress(Bitmap.CompressFormat.Png!, 100, stream))
                        {
                            return 0;
                        }
                    }
                    return new FileInfo(path).Length;
                }
                catch (IOException)
                {
                    return 0;
                }
                finally
                {
                    software.Recycle();
                }
            }, hardware.Recycle);
            if (!settled.TrySetResult(shot))
            {
                hardware.Recycle();
            }
        }

        public void OnFailure(int errorCode) =>
            settled.TrySetResult(new A11yShot.Refused(MapScreenshotError(errorCode), "code=" + errorCode.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>Upstream <c>mapScreenshotError</c>: retry later vs never possible.</summary>
    private static RelayError MapScreenshotError(int code) => (Android.Accessibilityservice.AccessibilityService.TakeScreenshotError)code switch
    {
        Android.Accessibilityservice.AccessibilityService.TakeScreenshotError.SecureWindow => RelayError.SurfaceSecureWindow,
        Android.Accessibilityservice.AccessibilityService.TakeScreenshotError.IntervalTimeShort => RelayError.RateLimited,
        _ => RelayError.GateSystemMissing,
    };
}
