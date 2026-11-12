// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/protocol/RelayWire.kt      (RelayResponse, consent kinds)
//   src/main/java/interlock/relay/core/protocol/EffectSemantics.kt (RetryPolicy, EffectState, retryPolicyOf, effectStateFor)
//   src/main/java/interlock/relay/core/protocol/Capability.kt     (SurfaceKind, CapabilityCategory per CapabilityId)
//   src/main/java/interlock/relay/core/runtime/RelayCoordinator.kt (consentKindFor)
// Registered in docs/UPSTREAMS.md. Locked by tests/TestVectors/relay-admission.json (legacy Kotlin runs the same cases).

using System.Text.Json;

namespace DshMobile.Runtime.Automation.Relay;

/// <summary>Execution surface (upstream <c>SurfaceKind</c>); only the wire names go on the channel.</summary>
public enum SurfaceKind
{
    Foreground,
    BackendTrusted,
    ObserveOnly,
}

/// <summary>What a caller may do with the same arguments again (upstream <c>RetryPolicy</c>).</summary>
public enum RetryPolicy
{
    SafeRetry,
    QueryFirst,
    UserDecide,
    Never,
}

/// <summary>Conservative mark of whether a side effect happened (upstream <c>EffectState</c>).</summary>
public enum EffectState
{
    NotStarted,
    Dispatched,
    Verified,
    Unknown,
}

public enum CapabilityCategory
{
    Observe,
    Control,
    Data,
    System,
    Dangerous,
}

/// <summary>Response envelope (upstream <c>RelayResponse</c>). <see cref="Error"/> set implies <see cref="Ok"/> false.</summary>
public sealed record RelayResponse(
    string Id,
    bool Ok,
    JsonElement? Data,
    IReadOnlyList<string> Artifacts,
    SurfaceKind? Surface,
    SurfaceKind? DegradedFrom,
    string? Reason,
    RelayError? Error,
    long ElapsedMs,
    string? DegradeReason = null,
    RetryPolicy? RetryPolicy = null,
    EffectState? EffectState = null,
    string? ConsentKind = null)
{
    public const string ConsentKindRelayPolicy = "relay_policy";
    public const string ConsentKindAndroidProjection = "android_projection";

    /// <summary>Upstream <c>MailboxServer.failure</c>: the bare failure the mailbox answers on its own.</summary>
    public static RelayResponse Failure(string id, RelayError error, long elapsedMs, string? reason = null) =>
        new(id, false, null, [], null, null, reason, error, elapsedMs);
}

/// <summary>Static per-capability semantics.</summary>
public static class RelaySemantics
{
    public static string Wire(this SurfaceKind kind) => kind switch
    {
        SurfaceKind.Foreground => "foreground",
        SurfaceKind.BackendTrusted => "trusted-display",
        SurfaceKind.ObserveOnly => "observe-only",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Upstream <c>RetryPolicy.name.lowercase()</c> / <c>EffectState.name.lowercase()</c>.</summary>
    public static string Wire(this RetryPolicy p) => p switch
    {
        RetryPolicy.SafeRetry => "safe_retry",
        RetryPolicy.QueryFirst => "query_first",
        RetryPolicy.UserDecide => "user_decide",
        RetryPolicy.Never => "never",
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    public static string Wire(this EffectState s) => s switch
    {
        EffectState.NotStarted => "not_started",
        EffectState.Dispatched => "dispatched",
        EffectState.Verified => "verified",
        EffectState.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    private static readonly Dictionary<string, CapabilityCategory> Categories = new(StringComparer.Ordinal)
    {
        ["ui.snapshot"] = CapabilityCategory.Observe, ["screen.capture"] = CapabilityCategory.Observe,
        ["screen.observe"] = CapabilityCategory.Observe, ["screen.record"] = CapabilityCategory.Observe,
        ["notify.read"] = CapabilityCategory.Observe, ["ui.node"] = CapabilityCategory.Observe,
        ["ui.tap"] = CapabilityCategory.Control, ["ui.swipe"] = CapabilityCategory.Control, ["ui.text"] = CapabilityCategory.Control,
        ["ui.key"] = CapabilityCategory.Control, ["ui.click"] = CapabilityCategory.Control, ["ui.longClick"] = CapabilityCategory.Control,
        ["ui.select"] = CapabilityCategory.Control, ["ui.dismiss"] = CapabilityCategory.Control, ["ui.scroll"] = CapabilityCategory.Control,
        ["ui.setValue"] = CapabilityCategory.Control, ["ui.waitFor"] = CapabilityCategory.Control,
        ["ui.setProgress"] = CapabilityCategory.Control, ["ui.imeAction"] = CapabilityCategory.Control,
        ["app.launch"] = CapabilityCategory.Control, ["app.stop"] = CapabilityCategory.Control,
        ["surface.virtual"] = CapabilityCategory.Control,
        ["clip.read"] = CapabilityCategory.Data, ["clip.write"] = CapabilityCategory.Data, ["contact.read"] = CapabilityCategory.Data,
        ["contact.write"] = CapabilityCategory.Data, ["cal.read"] = CapabilityCategory.Data, ["cal.write"] = CapabilityCategory.Data,
        ["loc.read"] = CapabilityCategory.Data, ["media.read"] = CapabilityCategory.Data, ["media.write"] = CapabilityCategory.Data,
        ["audio.capture"] = CapabilityCategory.Data,
        ["pkg.query"] = CapabilityCategory.System, ["app.install"] = CapabilityCategory.System,
        ["sys.settings.write"] = CapabilityCategory.System, ["appops.set"] = CapabilityCategory.System,
        ["sys.intent"] = CapabilityCategory.System, ["sys.shell"] = CapabilityCategory.Dangerous,
        ["notify.post"] = CapabilityCategory.System,
    };

    public static CapabilityCategory CategoryOf(string wire) =>
        Categories.TryGetValue(wire, out var c) ? c : throw new ArgumentException($"unknown capability {wire}", nameof(wire));

    /// <summary>Upstream <c>retryPolicyOf</c>.</summary>
    public static RetryPolicy RetryPolicyOf(string wire) => wire switch
    {
        "pkg.query" or "ui.snapshot" or "ui.node" or "ui.waitFor" => RetryPolicy.SafeRetry,
        "screen.capture" or "screen.record" or "screen.observe" or "audio.capture" or "notify.read" or "loc.read" or "media.read"
            or "ui.setValue" or "sys.settings.write" or "appops.set" or "clip.write" => RetryPolicy.QueryFirst,
        _ => RetryPolicy.UserDecide, // every write / action, and everything not argued for replay
    };

    /// <summary>Upstream <c>effectStateFor</c>.</summary>
    public static EffectState EffectStateFor(bool ok, RelayError? error) =>
        ok ? EffectState.Verified
        : error == RelayError.TransportTimeout || error == RelayError.WaitTimeout || error == RelayError.GateApprovalTimeout ? EffectState.Unknown
        : EffectState.NotStarted;

    /// <summary>Upstream <c>consentKindFor</c>.</summary>
    public static string? ConsentKindFor(RelayError? error) =>
        error == RelayError.GateAwaitingConsent || error == RelayError.GateNoForeground
        || error == RelayError.GateWaitingTurn || error == RelayError.GateCancelled
            ? RelayResponse.ConsentKindRelayPolicy
            : null;
}
