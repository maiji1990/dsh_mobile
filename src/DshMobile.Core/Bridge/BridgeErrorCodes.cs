namespace DshMobile.Core.Bridge;

/// <summary>
/// Stable protocol v1 error codes (docs/PROVIDER_CONTRACTS.md 1.5).
/// Every code the Java facade emits must exist here; a contract test scans the Java source to enforce it.
/// </summary>
public static class BridgeErrorCodes
{
    public const string Ok = "OK";
    public const string BadRequest = "BAD_REQUEST";
    public const string ProtocolMismatch = "PROTOCOL_MISMATCH";
    public const string MethodNotFound = "METHOD_NOT_FOUND";
    public const string NotInitialized = "NOT_INITIALIZED";
    public const string DuplicateRequest = "DUPLICATE_REQUEST";
    public const string DeadlineExceeded = "DEADLINE_EXCEEDED";
    public const string Cancelled = "CANCELLED";
    public const string Closed = "CLOSED";
    public const string Unavailable = "UNAVAILABLE";
    public const string Internal = "INTERNAL";

    public static IReadOnlyList<string> All { get; } =
    [
        Ok, BadRequest, ProtocolMismatch, MethodNotFound, NotInitialized, DuplicateRequest,
        DeadlineExceeded, Cancelled, Closed, Unavailable, Internal,
    ];

    /// <summary>Codes after which retrying the same request may succeed.</summary>
    public static bool IsTransient(string code) => code is DeadlineExceeded;
}
