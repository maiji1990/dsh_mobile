using Android.Runtime;
using DshMobile.Runtime.Automation.Relay.Shizuku;

namespace DshMobile.App.Automation;

/// <summary>
/// The Shizuku client (<c>rikka.shizuku.Shizuku</c>, dev.rikka.shizuku:api 13.1.5, MIT, already packaged for Relay)
/// called through standard JNI. The decisions are <see cref="ShizukuConnection"/>; this class only reads the client
/// and, on the user's tap, asks Shizuku to show its own grant dialog. The user answers that dialog in Shizuku.
/// </summary>
internal static class ShizukuClient
{
    private const string ClassName = "rikka/shizuku/Shizuku";

    public static ShizukuProbe Probe()
    {
        var cls = Class();
        if (cls == IntPtr.Zero)
        {
            return new ShizukuProbe(false, false, false, false, -1, -1);
        }
        var preV11 = Try(() => StaticBool(cls, "isPreV11"), false);
        var alive = Try(() => StaticBool(cls, "pingBinder"), false);
        // Calling anything else without a live binder throws IllegalStateException (ShizukuState constraint 1).
        if (!alive || preV11)
        {
            return new ShizukuProbe(preV11, alive, false, false, -1, -1);
        }
        return new ShizukuProbe(false, true,
            Try(() => StaticInt(cls, "checkSelfPermission") == 0, false),
            Try(() => StaticBool(cls, "shouldShowRequestPermissionRationale"), false),
            Try(() => StaticInt(cls, "getVersion"), -1),
            Try(() => StaticInt(cls, "getUid"), -1));
    }

    /// <summary>Shows Shizuku's grant dialog when <see cref="ShizukuConnection.Decide"/> says so; returns the state it acted on.</summary>
    public static ShizukuConnectState RequestIfNeeded(out ShizukuProbe probe)
    {
        probe = Probe();
        var state = ShizukuConnection.Decide(probe);
        if (state == ShizukuConnectState.CanRequest)
        {
            var cls = Class();
            var m = JNIEnv.GetStaticMethodID(cls, "requestPermission", "(I)V");
            JNIEnv.CallStaticVoidMethod(cls, m, new JValue(ShizukuConnection.PermissionRequestCode));
        }
        return state;
    }

    private static IntPtr _class;

    private static IntPtr Class()
    {
        if (_class != IntPtr.Zero)
        {
            return _class;
        }
        try
        {
            // .NET for Android's JNIEnv.FindClass already returns a *global* reference (cached for the process);
            // releasing it as a local reference aborts the runtime ("delete global reference as local").
            _class = JNIEnv.FindClass(ClassName);
        }
        catch (Java.Lang.Throwable)
        {
            _class = IntPtr.Zero; // client library not packaged: reported as "not running", never as granted
        }
        return _class;
    }

    private static bool StaticBool(IntPtr cls, string name) =>
        JNIEnv.CallStaticBooleanMethod(cls, JNIEnv.GetStaticMethodID(cls, name, "()Z"));

    private static int StaticInt(IntPtr cls, string name) =>
        JNIEnv.CallStaticIntMethod(cls, JNIEnv.GetStaticMethodID(cls, name, "()I"));

    private static T Try<T>(Func<T> f, T fallback)
    {
        try
        {
            return f();
        }
        catch (Java.Lang.Throwable)
        {
            return fallback;
        }
    }
}
