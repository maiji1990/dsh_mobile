namespace DshMobile.Native;

/// <summary>Stable error codes of the native layer (never a fake success, AGENTS.md rule 5).</summary>
public static class NativeErrorCodes
{
    /// <summary>libdshmobile_native.so is not packaged / not loadable on this build.</summary>
    public const string NativeUnavailable = "NATIVE_UNAVAILABLE";
    /// <summary>The packaged native library speaks a different ABI version than this managed code.</summary>
    public const string AbiMismatch = "NATIVE_ABI_MISMATCH";
    /// <summary>The requested executable violates the W^X policy.</summary>
    public const string ExecutableNotAllowed = "EXECUTABLE_NOT_ALLOWED";
    /// <summary>fork / exec of a PTY child failed (message carries errno; an exec failure is reported, never hidden).</summary>
    public const string SpawnFailed = "NATIVE_SPAWN_FAILED";
    /// <summary>The operation is declared but not implemented in this slice.</summary>
    public const string NotImplemented = "NOT_IMPLEMENTED";
}

public readonly record struct NativeResult<T>(bool Ok, T? Value, string Code, string? Message)
{
    public static NativeResult<T> Success(T value) => new(true, value, "OK", null);

    public static NativeResult<T> Fail(string code, string message) => new(false, default, code, message);
}
