using System.Runtime.InteropServices;

namespace DshMobile.Native;

/// <summary>
/// Entry points of the in-repo <c>libdshmobile_native.so</c> (native/dshmobile-native, ADR-0007). The library is
/// intentionally tiny: process groups, PTY ioctls, and the exec of PRoot from nativeLibraryDir. It is NOT shipped in
/// PR-01; <see cref="Probe"/> therefore reports <see cref="NativeErrorCodes.NativeUnavailable"/> until a later PR adds
/// the NDK build, and nothing in the product depends on it yet.
/// </summary>
public static partial class DshNativeLibrary
{
    public const string LibraryName = "dshmobile_native";

    /// <summary>ABI version this managed code expects from <c>dshm_abi_version()</c>.</summary>
    public const int ExpectedAbiVersion = 1;

    [LibraryImport(LibraryName, EntryPoint = "dshm_abi_version")]
    private static partial int AbiVersion();

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
