using System.Runtime.InteropServices;

namespace DshMobile.Native;

/// <summary>
/// The two bionic / glibc calls .NET has no managed API for. Only libc (no repo native code); returns -1 where libc
/// is not loadable (Windows desktop tests), so callers degrade instead of crashing.
/// </summary>
public static partial class Posix
{
    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint GetUidRaw();

    [LibraryImport("libc", EntryPoint = "getgid")]
    private static partial uint GetGidRaw();

    /// <summary>The real uid of this process (the Android app uid), or -1 when unavailable.</summary>
    public static int GetUid() => Call(GetUidRaw);

    /// <summary>The real gid of this process, or -1 when unavailable.</summary>
    public static int GetGid() => Call(GetGidRaw);

    private static int Call(Func<uint> f)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())
        {
            return -1;
        }
        try
        {
            return (int)f();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return -1;
        }
    }
}
