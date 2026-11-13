using Android.AccessibilityServices;
using Android.Content;
using Android.Graphics;
using Android.Runtime;
using Android.Views;
using Android.Views.Accessibility;
using DshMobile.Runtime.Automation.Relay.A11y;

namespace DshMobile.App.Automation;

/// <summary>
/// PR-07 segment 7, read-only and transitional: the foreground accessibility tree as the C# reader sees it, read from
/// the accessibility service that is running in this process. While the slice is LegacyWithShadow that service is still
/// the legacy Relay's <c>interlock.relay.core.exec.a11y.RelayAccessibilityService</c> (the one the user enabled), reached
/// through standard JNI (<c>RelayAccessibilityService.Companion.current()</c>); this class only reads its tree. When C#
/// owns the service this source reads its own service instead and the JNI lookup goes away.
/// </summary>
internal static class RelayA11yTreeSource
{
    private const string ServiceClass = "interlock/relay/core/exec/a11y/RelayAccessibilityService";
    private const string CompanionClass = ServiceClass + "$Companion";

    /// <summary>The running service, or null when the user has not enabled it (or the class is not packaged).</summary>
    public static AccessibilityService? Current()
    {
        try
        {
            var cls = JNIEnv.FindClass(ServiceClass); // a global ref, cached by the runtime: never deleted here
            var field = JNIEnv.GetStaticFieldID(cls, "Companion", "L" + CompanionClass + ";");
            var companion = JNIEnv.GetStaticObjectField(cls, field);
            try
            {
                var companionCls = JNIEnv.FindClass(CompanionClass);
                var current = JNIEnv.GetMethodID(companionCls, "current", "()L" + ServiceClass + ";");
                var handle = JNIEnv.CallObjectMethod(companion, current);
                return handle == IntPtr.Zero ? null : Java.Lang.Object.GetObject<AccessibilityService>(handle, JniHandleOwnership.TransferLocalRef);
            }
            finally
            {
                JNIEnv.DeleteLocalRef(companion);
            }
        }
        catch (Java.Lang.Throwable)
        {
            return null;
        }
    }

    /// <summary>Upstream <c>A11yBackend.hostTree</c>: the default display, its real pixel frame, the active window's root.</summary>
    public static A11yTree? HostTree(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Current() is not { } service)
        {
            return null;
        }
        var app = context.ApplicationContext!;
        var screen = DisplayBounds(app);
        var density = (int)app.Resources!.DisplayMetrics!.DensityDpi;
        return new A11yTree(A11yConstants.DefaultDisplay, screen, density,
            A11yTree.RetryOnce(() => service.RootInActiveWindow is { } root ? new A11yNodeAdapter(root, 0) : null));
    }

    /// <summary>Upstream <c>displayBounds</c>: maximum window metrics (not the app window, which shrinks in split screen).</summary>
    private static Bounds DisplayBounds(Context app)
    {
        var wm = app.GetSystemService(Context.WindowService).JavaCast<IWindowManager>()!;
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            var b = wm.MaximumWindowMetrics.Bounds;
            return new Bounds(b.Left, b.Top, b.Right, b.Bottom);
        }
        var metrics = new Android.Util.DisplayMetrics();
#pragma warning disable CA1422 // the pre-30 way to read the real size
        wm.DefaultDisplay!.GetRealMetrics(metrics);
#pragma warning restore CA1422
        return new Bounds(0, 0, metrics.WidthPixels, metrics.HeightPixels);
    }
}

/// <summary>One <see cref="AccessibilityNodeInfo"/> as the C# reader's node. Children are fetched once per node.</summary>
internal sealed class A11yNodeAdapter(AccessibilityNodeInfo info, int siblingIndex) : IA11yNode
{
    private A11yNodeAdapter?[]? _kids;

    private A11yNodeAdapter?[] Kids
    {
        get
        {
            if (_kids is null)
            {
                var kids = new A11yNodeAdapter?[info.ChildCount];
                for (var i = 0; i < kids.Length; i++)
                {
                    kids[i] = info.GetChild(i) is { } child ? new A11yNodeAdapter(child, i) : null;
                }
                _kids = kids;
            }
            return _kids;
        }
    }

    public string? Text => info.Text;

    public string? ContentDescription => info.ContentDescription;

    public string? ViewId => info.ViewIdResourceName;

    public string? ClassName => info.ClassName;

    public string? PackageName => info.PackageName;

    public int SiblingIndex { get; } = siblingIndex;

    public IReadOnlyList<INodeView> Children => [.. Kids.OfType<A11yNodeAdapter>()];

    public Bounds BoundsInScreen
    {
        get
        {
            var r = new Rect();
            info.GetBoundsInScreen(r);
            return new Bounds(r.Left, r.Top, r.Right, r.Bottom);
        }
    }

    public int ChildCount => Kids.Length;

    public IA11yNode? GetChild(int index) => Kids[index];

    public bool IsScrollable => info.Scrollable;

    public bool IsClickable => info.Clickable;

    public bool IsEditable => info.Editable;

#pragma warning disable CS0618, CA1422 // upstream reads the deprecated isChecked getter on every version; so does this
    public bool IsChecked => info.Checked;
#pragma warning restore CS0618, CA1422

    public bool IsCheckable => info.Checkable;

    public bool IsEnabled => info.Enabled;

    public bool IsSelected => info.Selected;

    public bool IsVisibleToUser => info.VisibleToUser;

    public bool IsFocused => info.Focused;

    public IReadOnlyList<int> ActionIds => [.. (info.ActionList ?? []).Select(a => a.Id)];

    public A11yRange? Range => info.GetRangeInfo() is { } r ? new A11yRange((int)r.Type, r.Min, r.Max, r.Current) : null;
}
