// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/protocol/RelayWire.kt  (protocol versions, RelayRequest, RelayError, RelayExitCode)
//   src/main/java/interlock/relay/core/protocol/Capability.kt (CapabilityId wire names: the closed capability set)
// Registered in docs/UPSTREAMS.md. Tables are checked against the shared vectors (tests/TestVectors/relay-envelope.json).

namespace DshMobile.Runtime.Automation.Relay;

/// <summary>Relay mailbox protocol constants (v1 capability channel, v2 control channel).</summary>
public static class RelayProtocol
{
    public const int V1 = 1;
    public const int V2 = 2;

    /// <summary>Default request lifetime; must exceed the approval wait or the request is reaped while the user reads.</summary>
    public const long DefaultTimeoutMs = 90_000;

    /// <summary>
    /// The closed set of capabilities a guest may name (upstream <c>CapabilityId.wire</c>, all 39, declaration order). Being in
    /// this set only means the envelope is well-formed: what DSH Mobile allows is a much smaller allowlist.
    /// </summary>
    public static readonly IReadOnlyList<string> Capabilities =
    [
        "ui.snapshot", "screen.capture", "screen.observe", "screen.record", "notify.read",
        "ui.tap", "ui.swipe", "ui.text", "ui.key", "ui.click", "ui.longClick", "ui.select", "ui.dismiss", "ui.scroll",
        "ui.setValue", "ui.waitFor", "ui.node", "ui.setProgress", "ui.imeAction",
        "app.launch", "app.stop", "surface.virtual",
        "clip.read", "clip.write", "contact.read", "contact.write", "cal.read", "cal.write", "loc.read",
        "media.read", "media.write", "audio.capture",
        "pkg.query", "app.install", "sys.settings.write", "appops.set", "sys.intent", "sys.shell", "notify.post",
    ];

    private static readonly HashSet<string> CapabilitySet = new(Capabilities, StringComparer.Ordinal);

    /// <summary>Upstream <c>CapabilityId.fromWire</c>: exact, case-sensitive.</summary>
    public static bool IsCapability(string wire) => CapabilitySet.Contains(wire);
}

/// <summary>Exit codes of the guest CLI (upstream <c>RelayExitCode</c>): each class of failure has its own value.</summary>
public static class RelayExitCode
{
    public const int Success = 0;
    public const int Usage = 1;
    public const int Denied = 2;
    public const int Timeout = 3;
    public const int NeedsUserAction = 4;
    public const int RetryLater = 5;
    public const int Internal = 6;
}

/// <summary>One Relay error (upstream enum <c>RelayError</c>): wire code, whether a retry can help, CLI exit code.</summary>
public sealed record RelayError(string Name, string Code, bool Retryable, int ExitCode)
{
    public static readonly RelayError GateHostDenied = new("GATE_HOST_DENIED", "E_GATE_HOST_DENIED", false, RelayExitCode.Denied);
    public static readonly RelayError GateSystemMissing = new("GATE_SYSTEM_MISSING", "E_GATE_SYSTEM_MISSING", false, RelayExitCode.NeedsUserAction);
    public static readonly RelayError GateApprovalTimeout = new("GATE_APPROVAL_TIMEOUT", "E_GATE_APPROVAL_TIMEOUT", false, RelayExitCode.Timeout);
    public static readonly RelayError GateAwaitingConsent = new("GATE_AWAITING_CONSENT", "E_AWAITING_CONSENT", true, RelayExitCode.RetryLater);
    public static readonly RelayError GateWaitingTurn = new("GATE_WAITING_TURN", "E_GATE_WAITING_TURN", true, RelayExitCode.RetryLater);
    public static readonly RelayError GateCancelled = new("GATE_CANCELLED", "E_GATE_CANCELLED", false, RelayExitCode.RetryLater);
    public static readonly RelayError GateSuspendedByHost = new("GATE_SUSPENDED_BY_HOST", "E_TASK_SUSPENDED_BY_HOST", true, RelayExitCode.RetryLater);
    public static readonly RelayError NoEditableTarget = new("NO_EDITABLE_TARGET", "E_NO_EDITABLE_TARGET", false, RelayExitCode.Usage);
    public static readonly RelayError NodeNotFound = new("NODE_NOT_FOUND", "E_NODE_NOT_FOUND", false, RelayExitCode.Usage);
    public static readonly RelayError NodeAmbiguous = new("NODE_AMBIGUOUS", "E_NODE_AMBIGUOUS", false, RelayExitCode.Usage);
    public static readonly RelayError NodeNotActionable = new("NODE_NOT_ACTIONABLE", "E_NODE_NOT_ACTIONABLE", false, RelayExitCode.Usage);
    public static readonly RelayError NodeActionRejected = new("NODE_ACTION_REJECTED", "E_ACTION_REJECTED", true, RelayExitCode.RetryLater);
    public static readonly RelayError WaitTimeout = new("WAIT_TIMEOUT", "E_WAIT_TIMEOUT", true, RelayExitCode.Timeout);
    public static readonly RelayError GateNoForeground = new("GATE_NO_FOREGROUND", "E_GATE_NO_FOREGROUND", true, RelayExitCode.NeedsUserAction);
    public static readonly RelayError ClipboardNoFocus = new("CLIPBOARD_NO_FOCUS", "E_CLIPBOARD_NO_FOCUS", true, RelayExitCode.NeedsUserAction);
    public static readonly RelayError CapabilityUnavailableOnDevice = new("CAPABILITY_UNAVAILABLE_ON_DEVICE", "E_CAPABILITY_UNAVAILABLE_ON_DEVICE", false, RelayExitCode.NeedsUserAction);
    public static readonly RelayError CapabilityNotImplemented = new("CAPABILITY_NOT_IMPLEMENTED", "E_NOT_IMPLEMENTED", false, RelayExitCode.Usage);
    public static readonly RelayError NodeNoImeAction = new("NODE_NO_IME_ACTION", "E_NODE_NO_IME_ACTION", false, RelayExitCode.Usage);
    public static readonly RelayError NodeSystemConsentTarget = new("NODE_SYSTEM_CONSENT_TARGET", "E_NODE_SYSTEM_CONSENT_TARGET", false, RelayExitCode.NeedsUserAction);
    public static readonly RelayError LaunchNotLanded = new("LAUNCH_NOT_LANDED", "E_LAUNCH_NOT_LANDED", true, RelayExitCode.RetryLater);
    public static readonly RelayError SurfaceApiLevel = new("SURFACE_API_LEVEL", "E_SURFACE_API_LEVEL", false, RelayExitCode.NeedsUserAction);
    public static readonly RelayError SurfaceNoShell = new("SURFACE_NO_SHELL", "E_SURFACE_NO_SHELL", true, RelayExitCode.NeedsUserAction);
    public static readonly RelayError SurfaceTrustedDenied = new("SURFACE_TRUSTED_DENIED", "E_SURFACE_TRUSTED_DENIED", false, RelayExitCode.NeedsUserAction);
    public static readonly RelayError SurfaceUnavailable = new("SURFACE_UNAVAILABLE", "E_SURFACE_UNAVAILABLE", false, RelayExitCode.NeedsUserAction);
    public static readonly RelayError SurfaceModeRequired = new("SURFACE_MODE_REQUIRED", "E_SURFACE_MODE_REQUIRED", false, RelayExitCode.NeedsUserAction);
    public static readonly RelayError SurfaceSystemConsentRequired = new("SURFACE_SYSTEM_CONSENT_REQUIRED", "E_SURFACE_SYSTEM_CONSENT_REQUIRED", true, RelayExitCode.NeedsUserAction);
    public static readonly RelayError SurfaceSecureWindow = new("SURFACE_SECURE_WINDOW", "E_SURFACE_SECURE_WINDOW", false, RelayExitCode.Denied);
    public static readonly RelayError BackendShizukuDead = new("BACKEND_SHIZUKU_DEAD", "E_BACKEND_SHIZUKU_DEAD", true, RelayExitCode.RetryLater);
    public static readonly RelayError BackendUnavailable = new("BACKEND_UNAVAILABLE", "E_BACKEND_UNAVAILABLE", true, RelayExitCode.RetryLater);
    public static readonly RelayError StorageFull = new("STORAGE_FULL", "E_STORAGE_FULL", true, RelayExitCode.RetryLater);
    public static readonly RelayError RateLimited = new("RATE_LIMITED", "E_RATE_LIMITED", true, RelayExitCode.RetryLater);
    public static readonly RelayError QueueFull = new("QUEUE_FULL", "E_QUEUE_FULL", true, RelayExitCode.RetryLater);
    public static readonly RelayError RequestTooLarge = new("REQUEST_TOO_LARGE", "E_REQUEST_TOO_LARGE", false, RelayExitCode.Usage);
    public static readonly RelayError TransportMalformed = new("TRANSPORT_MALFORMED", "E_TRANSPORT_MALFORMED", false, RelayExitCode.Usage);
    public static readonly RelayError TransportTimeout = new("TRANSPORT_TIMEOUT", "E_TRANSPORT_TIMEOUT", true, RelayExitCode.Timeout);
    public static readonly RelayError InternalError = new("INTERNAL", "E_INTERNAL", false, RelayExitCode.Internal);

    /// <summary>Upstream declaration order.</summary>
    public static readonly IReadOnlyList<RelayError> All =
    [
        GateHostDenied, GateSystemMissing, GateApprovalTimeout, GateAwaitingConsent, GateWaitingTurn, GateCancelled,
        GateSuspendedByHost, NoEditableTarget, NodeNotFound, NodeAmbiguous, NodeNotActionable, NodeActionRejected,
        WaitTimeout, GateNoForeground, ClipboardNoFocus, CapabilityUnavailableOnDevice, CapabilityNotImplemented,
        NodeNoImeAction, NodeSystemConsentTarget, LaunchNotLanded, SurfaceApiLevel, SurfaceNoShell,
        SurfaceTrustedDenied, SurfaceUnavailable, SurfaceModeRequired, SurfaceSystemConsentRequired,
        SurfaceSecureWindow, BackendShizukuDead, BackendUnavailable, StorageFull, RateLimited, QueueFull,
        RequestTooLarge, TransportMalformed, TransportTimeout, InternalError,
    ];

    /// <summary>Upstream <c>RelayError.fromCode</c>.</summary>
    public static RelayError? FromCode(string code) => All.FirstOrDefault(e => e.Code == code);
}
