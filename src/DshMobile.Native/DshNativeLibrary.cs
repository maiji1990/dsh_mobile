using System.Runtime.InteropServices;

namespace DshMobile.Native;

/// <summary>
/// Entry points of the in-repo <c>libdshmobile_native.so</c> (native/dshmobile-native/src/dshm_native.c, ADR-0007).
/// The library is intentionally tiny: PTY spawn / resize / read / write, waitpid and process-group kill. Built by
/// native/build-native.* and packaged by src/DshMobile.Android; <see cref="Probe"/> reports
/// <see cref="NativeErrorCodes.NativeUnavailable"/> where it is not loadable (desktop tests, a build without it).
/// Signatures must match the C exports exactly (a source-contract test scans the C file).
/// </summary>
public static partial class DshNativeLibrary
{
    public const string LibraryName = "dshmobile_native";

    /// <summary>ABI version this managed code expects from <c>dshm_abi_version()</c>.</summary>
    public const int ExpectedAbiVersion = 1;

    [LibraryImport(LibraryName, EntryPoint = "dshm_abi_version")]
    private static partial int AbiVersion();

    [LibraryImport(LibraryName, EntryPoint = "dshm_pty_spawn", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int PtySpawn(string path, nint argv, nint envp, string? cwd, int rows, int cols, out int pid, out int errno);

    [LibraryImport(LibraryName, EntryPoint = "dshm_pty_resize")]
    internal static partial int PtyResize(int fd, int rows, int cols);

    [LibraryImport(LibraryName, EntryPoint = "dshm_read")]
    private static unsafe partial int ReadRaw(int fd, byte* buf, int len, int timeoutMs);

    [LibraryImport(LibraryName, EntryPoint = "dshm_write")]
    private static unsafe partial int WriteRaw(int fd, byte* buf, int len);

    [LibraryImport(LibraryName, EntryPoint = "dshm_close")]
    internal static partial int Close(int fd);

    [LibraryImport(LibraryName, EntryPoint = "dshm_wait")]
    internal static partial int Wait(int pid, int nohang, out int kind, out int value);

    [LibraryImport(LibraryName, EntryPoint = "dshm_kill")]
    internal static partial int Kill(int pid, int sig, int group);

    internal static unsafe int Read(int fd, Span<byte> buffer, int timeoutMs)
    {
        fixed (byte* p = buffer)
        {
            return ReadRaw(fd, p, buffer.Length, timeoutMs);
        }
    }

    internal static unsafe int Write(int fd, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return 0;
        }
        fixed (byte* p = data)
        {
            return WriteRaw(fd, p, data.Length);
        }
    }

    /// <summary>Loads the library and checks its ABI version. Never throws for a missing library.</summary>
    public static NativeResult<int> Probe()
    {
        try
        {
            var v = AbiVersion();
            return v == ExpectedAbiVersion
                ? NativeResult<int>.Success(v)
                : NativeResult<int>.Fail(NativeErrorCodes.AbiMismatch, $"native ABI {v}, managed expects {ExpectedAbiVersion}");
        }
        catch (DllNotFoundException e)
        {
            return NativeResult<int>.Fail(NativeErrorCodes.NativeUnavailable, e.Message);
        }
        catch (EntryPointNotFoundException e)
        {
            return NativeResult<int>.Fail(NativeErrorCodes.AbiMismatch, e.Message);
        }
    }
}
