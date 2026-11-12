// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/interlock/InterlockGate.kt     (GateResult, authorize, verdict, requiresApproval,
//     systemBlocks, approvalLabel, the approval-frequency merge, SESSION_GRANT_MS, APPROVAL_IDENTITY_CAPACITY)
//   src/main/java/interlock/relay/core/interlock/InterlockBroker.kt   (InterlockChoice, ApprovalOutcome, request's window
//     rules, errorFor, DEFAULT_APPROVAL_WINDOW_MS, MIN_PROMPT_WINDOW_MS)
//   src/main/java/interlock/relay/core/interlock/TierStore.kt         (tier keys, defaults, session grants)
//   src/main/java/interlock/relay/core/interlock/SystemStateProbe.kt  (route and all-route probes, accessibility gap)
//   src/main/java/interlock/relay/core/protocol/Capability.kt         (AccessTier, TierCeiling, SystemGrantState, GrantGap)
//   src/main/java/interlock/relay/core/protocol/CapabilityRegistry.kt (ceilings, runtime permissions, SCREENSHOT_MIN_SDK)
// Registered in docs/UPSTREAMS.md. Locked by tests/TestVectors/relay-gate.json.
//
// Security: this is the decision half of Relay's approval gate. It never shows a prompt itself; the prompt is a
// caller-supplied presenter (the legacy Relay keeps presenting and deciding until this slice is accepted).

namespace DshMobile.Runtime.Automation.Relay;

/// <summary>The assistant's access tier for one capability (upstream <c>AccessTier</c>).</summary>
public enum AccessTier
{
    Denied,
    Ask,
    Always,
}

/// <summary>How far a tier may relax the prompt (upstream <c>TierCeiling</c>).</summary>
public enum TierCeiling
{
    Any,
    SessionOnly,
    AskOnly,
}

/// <summary>What the system side allows right now (upstream <c>SystemGrantState</c>).</summary>
public enum SystemGrantState
{
    Granted,
    RuntimeAskable,
    ManualOnly,
    SessionConsent,
    ForegroundFocus,
    UnavailableOnDevice,
}

/// <summary>The presenter's answer (upstream <c>InterlockChoice</c>).</summary>
public enum InterlockChoice
{
    AllowOnce,
    AllowSession,
    Deny,
    UnableToShow,
    Expired,
    Awaiting,
    WaitingTurn,
}

/// <summary>The broker's outcome (upstream <c>ApprovalOutcome</c>).</summary>
public enum ApprovalOutcome
{
    AllowedOnce,
    AllowedForSession,
    Denied,
    TimedOut,
    AwaitingConsent,
    WaitingTurn,
    CannotPresent,
}

/// <summary>The system-side snapshot plus what the user would have to turn on (upstream <c>ProbeResult</c>).</summary>
/// <param name="Gaps">"accessibility", "notificationListener", "shizuku" or "permission:&lt;name&gt;".</param>
public sealed record ProbeResult(SystemGrantState State, IReadOnlyList<string> Gaps)
{
    public static ProbeResult GrantedResult { get; } = new(SystemGrantState.Granted, []);
}

/// <summary>Upstream <c>GateResult</c>; <see cref="DecisionLabel"/> is what the audit record and the reason carry.</summary>
public sealed record GateResult(
    bool Allowed,
    bool RequiresApproval = false,
    RelayError? Error = null,
    SystemGrantState SystemState = SystemGrantState.Granted,
    AccessTier Tier = AccessTier.Denied,
    string DecisionLabel = "");

/// <summary>What the presenter is asked (the parts of upstream <c>ApprovalPrompt</c> the gate decides).</summary>
public sealed record GatePrompt(string Capability, string ArgsIdentity, bool AllowsSessionGrant, int CallIndexInSession);

public static class RelayGatePolicy
{
    /// <summary>Upstream <c>InterlockGate.SESSION_GRANT_MS</c>: "allow for this session" lasts 30 minutes.</summary>
    public const long SessionGrantMs = 30L * 60L * 1000L;

    public const long DefaultApprovalWindowMs = 60_000;
    public const long MinPromptWindowMs = 5_000;
    public const int ScreenshotMinSdk = 30;
    internal const int ApprovalIdentityCapacity = 64;

    private static readonly Dictionary<string, (TierCeiling Ceiling, string[] Permissions)> Table = new(StringComparer.Ordinal)
    {
        ["ui.snapshot"] = (TierCeiling.Any, []),
        ["screen.capture"] = (TierCeiling.SessionOnly, []),
        ["screen.observe"] = (TierCeiling.SessionOnly, []),
        ["screen.record"] = (TierCeiling.SessionOnly, []),
        ["notify.read"] = (TierCeiling.SessionOnly, []),
        ["ui.tap"] = (TierCeiling.Any, []),
        ["ui.swipe"] = (TierCeiling.Any, []),
        ["ui.click"] = (TierCeiling.Any, []),
        ["ui.longClick"] = (TierCeiling.Any, []),
        ["ui.select"] = (TierCeiling.Any, []),
        ["ui.dismiss"] = (TierCeiling.Any, []),
        ["ui.scroll"] = (TierCeiling.Any, []),
        ["ui.setValue"] = (TierCeiling.Any, []),
        ["ui.waitFor"] = (TierCeiling.Any, []),
        ["ui.setProgress"] = (TierCeiling.Any, []),
        ["ui.imeAction"] = (TierCeiling.Any, []),
        ["ui.node"] = (TierCeiling.Any, []),
        ["ui.text"] = (TierCeiling.Any, []),
        ["ui.key"] = (TierCeiling.Any, []),
        ["app.launch"] = (TierCeiling.Any, []),
        ["sys.intent"] = (TierCeiling.Any, []),
        ["sys.shell"] = (TierCeiling.AskOnly, []),
        ["app.stop"] = (TierCeiling.AskOnly, []),
        ["surface.virtual"] = (TierCeiling.Any, []),
        ["clip.read"] = (TierCeiling.AskOnly, []),
        ["clip.write"] = (TierCeiling.AskOnly, []),
        ["contact.read"] = (TierCeiling.Any, ["android.permission.READ_CONTACTS"]),
        ["contact.write"] = (TierCeiling.AskOnly, ["android.permission.WRITE_CONTACTS"]),
        ["cal.read"] = (TierCeiling.Any, ["android.permission.READ_CALENDAR"]),
        ["cal.write"] = (TierCeiling.AskOnly, ["android.permission.WRITE_CALENDAR"]),
        ["loc.read"] = (TierCeiling.AskOnly, ["android.permission.ACCESS_FINE_LOCATION", "android.permission.ACCESS_COARSE_LOCATION"]),
        ["media.read"] = (TierCeiling.SessionOnly, ["android.permission.READ_MEDIA_IMAGES", "android.permission.READ_MEDIA_VIDEO", "android.permission.READ_MEDIA_AUDIO"]),
        ["media.write"] = (TierCeiling.AskOnly, []),
        ["audio.capture"] = (TierCeiling.AskOnly, ["android.permission.RECORD_AUDIO"]),
        ["pkg.query"] = (TierCeiling.Any, []),
        ["app.install"] = (TierCeiling.AskOnly, ["android.permission.REQUEST_INSTALL_PACKAGES"]),
        ["sys.settings.write"] = (TierCeiling.AskOnly, []),
        ["appops.set"] = (TierCeiling.AskOnly, []),
        ["notify.post"] = (TierCeiling.Any, ["android.permission.POST_NOTIFICATIONS"]),
    };

    public static TierCeiling CeilingOf(string wire) =>
        Table.TryGetValue(wire, out var e) ? e.Ceiling : throw new ArgumentException($"unknown capability {wire}", nameof(wire));

    public static IReadOnlyList<string> RuntimePermissionsOf(string wire) =>
        Table.TryGetValue(wire, out var e) ? e.Permissions : throw new ArgumentException($"unknown capability {wire}", nameof(wire));

    /// <summary>
    /// Upstream <c>systemBlocks</c>, shared by the gate and the capability list: the ones the user must go and grant.
    /// Session consent and foreground focus do not block (the call is reachable).
    /// </summary>
    public static bool SystemBlocks(SystemGrantState state) =>
        state is SystemGrantState.RuntimeAskable or SystemGrantState.ManualOnly or SystemGrantState.UnavailableOnDevice;

    /// <summary>Upstream <c>verdict</c>: system first, then the tier; null = go on. Rate limits are not in this table.</summary>
    public static GateResult? Verdict(SystemGrantState system, AccessTier tier, TierCeiling ceiling)
    {
        _ = ceiling;
        if (SystemBlocks(system))
        {
            return system == SystemGrantState.UnavailableOnDevice
                ? new(false, Error: RelayError.CapabilityUnavailableOnDevice, SystemState: system, Tier: tier, DecisionLabel: "unavailable_on_device")
                : new(false, Error: RelayError.GateSystemMissing, SystemState: system, Tier: tier, DecisionLabel: "system_missing");
        }
        return tier == AccessTier.Denied
            ? new(false, Error: RelayError.GateHostDenied, SystemState: system, Tier: tier, DecisionLabel: "tier_denied")
            : null;
    }

    /// <summary>
    /// Upstream <c>requiresApproval</c>. A consent screen always asks; ask-only never skips; a session grant skips;
    /// otherwise ask unless the tier is "always" under an "any" ceiling. The order is the security property.
    /// </summary>
    public static bool RequiresApproval(AccessTier tier, TierCeiling ceiling, bool sessionGranted, bool consentScreen = false)
    {
        if (consentScreen || ceiling == TierCeiling.AskOnly)
        {
            return true;
        }
        if (sessionGranted)
        {
            return false;
        }
        return tier == AccessTier.Ask || ceiling == TierCeiling.SessionOnly;
    }

    /// <summary>Upstream <c>approvalLabel</c>: four kinds of "not allowed" that must not read as a user refusal.</summary>
    public static string ApprovalLabel(ApprovalOutcome outcome, RelayError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return outcome switch
        {
            ApprovalOutcome.AwaitingConsent => "awaiting_consent",
            ApprovalOutcome.WaitingTurn => "waiting_turn",
            ApprovalOutcome.TimedOut => "expired_" + error.Code,
            ApprovalOutcome.CannotPresent => "no_surface",
            ApprovalOutcome.Denied => "denied_" + error.Code,
            _ => "approved",
        };
    }

    /// <summary>Upstream <c>InterlockBroker.errorFor</c>.</summary>
    public static RelayError? ErrorFor(ApprovalOutcome outcome) => outcome switch
    {
        ApprovalOutcome.Denied => RelayError.GateHostDenied,
        ApprovalOutcome.TimedOut => RelayError.GateApprovalTimeout,
        ApprovalOutcome.AwaitingConsent => RelayError.GateAwaitingConsent,
        ApprovalOutcome.WaitingTurn => RelayError.GateWaitingTurn,
        ApprovalOutcome.CannotPresent => RelayError.GateNoForeground,
        _ => null,
    };

    /// <summary>Upstream broker mapping of a presenter choice.</summary>
    public static ApprovalOutcome OutcomeOf(InterlockChoice choice) => choice switch
    {
        InterlockChoice.AllowOnce => ApprovalOutcome.AllowedOnce,
        InterlockChoice.AllowSession => ApprovalOutcome.AllowedForSession,
        InterlockChoice.Deny => ApprovalOutcome.Denied,
        InterlockChoice.UnableToShow => ApprovalOutcome.CannotPresent,
        InterlockChoice.Awaiting => ApprovalOutcome.AwaitingConsent,
        InterlockChoice.WaitingTurn => ApprovalOutcome.WaitingTurn,
        _ => ApprovalOutcome.TimedOut,
    };
}

/// <summary>
/// The device facts the system-state probe reads (upstream <c>SystemStateProbe</c> asks Android for each):
/// whether the accessibility service is bound right now and whether Settings lists it, Shizuku, the trusted display
/// (<c>trustedDisplayReady()</c>), the notification listener (bound or listed), granted runtime permissions, and
/// whether the microphone app-op is ignored.
/// </summary>
public sealed record RelaySystemFacts(
    int SdkInt,
    bool A11yBound,
    bool A11yListed,
    bool ShellAvailable,
    bool TrustedDisplayAvailable,
    bool NotificationListener,
    IReadOnlySet<string> GrantedPermissions,
    bool MicrophoneBlocked);

/// <summary>Upstream <c>AccessibilityGap</c>.</summary>
public enum AccessibilityGap
{
    Revoked,
    NotBound,
}

/// <summary>Upstream <c>SystemStateProbe</c> as a pure function of <see cref="RelaySystemFacts"/>.</summary>
public static class RelaySystemProbe
{
    private static readonly HashSet<string> ShellCaps = new(StringComparer.Ordinal)
    {
        "sys.settings.write", "appops.set", "app.install", "app.stop", "surface.virtual", "sys.shell",
    };

    private static readonly HashSet<string> TreeCaps = new(StringComparer.Ordinal)
    {
        "ui.snapshot", "ui.node", "ui.waitFor", "ui.setProgress", "ui.imeAction",
        "ui.click", "ui.longClick", "ui.select", "ui.dismiss", "ui.scroll", "ui.setValue",
    };

    private static readonly HashSet<string> CoordinateCaps = new(StringComparer.Ordinal) { "ui.tap", "ui.swipe", "ui.text", "ui.key" };

    private static readonly ProbeResult SessionConsent = new(SystemGrantState.SessionConsent, []);

    /// <summary>Upstream <c>accessibilityGap()</c>: null when bound.</summary>
    public static AccessibilityGap? Gap(RelaySystemFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return facts.A11yBound ? null : facts.A11yListed ? AccessibilityGap.NotBound : AccessibilityGap.Revoked;
    }

    /// <summary>Upstream <c>probe(descriptor, route)</c>: the route decides for a few capabilities, else all routes.</summary>
    public static ProbeResult Probe(string wire, SurfaceKind? route, RelaySystemFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (route is { } r && RouteState(wire, r, facts) is { } routed)
        {
            return routed;
        }
        return AllRoutes(wire, facts);
    }

    private static ProbeResult? RouteState(string wire, SurfaceKind route, RelaySystemFacts f)
    {
        if (CoordinateCaps.Contains(wire))
        {
            return route == SurfaceKind.BackendTrusted ? ProbeResult.GrantedResult : Accessibility(f);
        }
        return wire switch
        {
            "screen.capture" => route switch
            {
                SurfaceKind.BackendTrusted => ProbeResult.GrantedResult,
                SurfaceKind.Foreground => f.A11yBound && f.SdkInt >= RelayGatePolicy.ScreenshotMinSdk ? ProbeResult.GrantedResult : SessionConsent,
                _ => SessionConsent,
            },
            "screen.record" => route == SurfaceKind.BackendTrusted ? ProbeResult.GrantedResult : SessionConsent,
            _ => null,
        };
    }

    private static ProbeResult AllRoutes(string wire, RelaySystemFacts f)
    {
        if (ShellCaps.Contains(wire))
        {
            return f.ShellAvailable ? ProbeResult.GrantedResult : new(SystemGrantState.ManualOnly, ["shizuku"]);
        }
        if (TreeCaps.Contains(wire))
        {
            return Accessibility(f);
        }
        if (CoordinateCaps.Contains(wire))
        {
            return f.TrustedDisplayAvailable ? ProbeResult.GrantedResult : Accessibility(f);
        }
        return wire switch
        {
            "screen.capture" => f.A11yBound || f.A11yListed || f.TrustedDisplayAvailable ? ProbeResult.GrantedResult : SessionConsent,
            "screen.record" => f.TrustedDisplayAvailable ? ProbeResult.GrantedResult : SessionConsent,
            "screen.observe" => SessionConsent,
            "clip.read" or "clip.write" => new(SystemGrantState.ForegroundFocus, []),
            "notify.read" => f.NotificationListener ? ProbeResult.GrantedResult : new(SystemGrantState.ManualOnly, ["notificationListener"]),
            "app.launch" or "pkg.query" or "media.write" or "sys.intent" => ProbeResult.GrantedResult,
            "audio.capture" => f.MicrophoneBlocked ? new(SystemGrantState.UnavailableOnDevice, []) : Runtime(wire, f),
            "contact.read" or "contact.write" or "cal.read" or "cal.write" or "loc.read" or "media.read" or "notify.post" => Runtime(wire, f),
            _ => throw new ArgumentException($"unknown capability {wire}", nameof(wire)),
        };
    }

    private static ProbeResult Runtime(string wire, RelaySystemFacts f)
    {
        var missing = RelayGatePolicy.RuntimePermissionsOf(wire).Where(p => !f.GrantedPermissions.Contains(p)).ToList();
        return missing.Count == 0 ? ProbeResult.GrantedResult : new(SystemGrantState.RuntimeAskable, [.. missing.Select(p => "permission:" + p)]);
    }

    /// <summary>Listed but not bound counts as granted (the app was stopped, the permission is still there).</summary>
    private static ProbeResult Accessibility(RelaySystemFacts f) =>
        Gap(f) == AccessibilityGap.Revoked ? new(SystemGrantState.ManualOnly, ["accessibility"]) : ProbeResult.GrantedResult;
}

/// <summary>
/// Upstream <c>TierStore</c>: tiers live in the host's private "relay" preferences (key <c>tier_&lt;wire with . → _&gt;</c>,
/// the exact enum name), default "ask" except <c>sys.shell</c> (denied until the user turns it on); session grants are
/// in memory only and expire.
/// </summary>
public sealed class RelayTierStore
{
    private readonly Func<string, string?> _readPref;
    private readonly Dictionary<string, long> _sessionGrants = new(StringComparer.Ordinal);

    public RelayTierStore(Func<string, string?> readPref) => _readPref = readPref;

    public static string Key(string wire) => "tier_" + wire.Replace('.', '_');

    public AccessTier TierOf(string wire) => _readPref(Key(wire)) switch
    {
        "DENIED" => AccessTier.Denied,
        "ASK" => AccessTier.Ask,
        "ALWAYS" => AccessTier.Always,
        _ => wire == "sys.shell" ? AccessTier.Denied : AccessTier.Ask,
    };

    public void GrantForSession(string wire, long expiresAtMs)
    {
        lock (_sessionGrants)
        {
            _sessionGrants[wire] = expiresAtMs;
        }
    }

    public bool SessionGranted(string wire, long nowMs)
    {
        lock (_sessionGrants)
        {
            if (!_sessionGrants.TryGetValue(wire, out var expires))
            {
                return false;
            }
            if (expires <= nowMs)
            {
                _sessionGrants.Remove(wire);
                return false;
            }
            return true;
        }
    }

    public void ClearSession()
    {
        lock (_sessionGrants)
        {
            _sessionGrants.Clear();
        }
    }
}

/// <summary>One call as the gate sees it (the inputs of upstream <c>authorize</c> that decide).</summary>
public sealed record GateCall(
    string Capability,
    string ArgsIdentity,
    ProbeResult System,
    long BudgetMs,
    bool ConsentScreen = false,
    bool HostBringToFront = false);

/// <summary>
/// Upstream <c>InterlockGate.authorize</c> with the broker's window rules. Order: host bring-to-front → verdict
/// (system, then tier) → session grant / requiresApproval → approval frequency (merged per capability + args
/// identity within the window) → the prompt, which is shown only when the window is at least
/// <see cref="RelayGatePolicy.MinPromptWindowMs"/> and is supplied by the caller.
/// </summary>
public sealed class RelayGate
{
    private readonly RelayTierStore _tiers;
    private readonly Func<long> _now;
    private readonly RelayRateLimiter _approvalRate;
    private readonly LinkedList<(string Key, long At)> _seenOrder = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, long At)>> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _callCounts = new(StringComparer.Ordinal);
    private readonly long _windowMs;

    public RelayGate(RelayTierStore tiers, Func<long> monotonicNowMs, long approvalWindowMs = RelayGatePolicy.DefaultApprovalWindowMs)
    {
        _tiers = tiers;
        _now = monotonicNowMs;
        _windowMs = approvalWindowMs;
        _approvalRate = new RelayRateLimiter(monotonicNowMs, RelayRateLimiter.ExecutionLimit);
    }

    /// <summary>
    /// The decision when it needs no prompt, or null when the call would be put to the user. Nothing is counted
    /// toward the approval frequency for a null answer from here: <see cref="AuthorizeAsync"/> does that.
    /// </summary>
    public GateResult? DecideWithoutPrompt(GateCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var tier = _tiers.TierOf(call.Capability);
        var system = call.System.State;
        if (call.HostBringToFront)
        {
            return new(true, false, null, system, tier, "host_bring_to_front");
        }
        var ceiling = RelayGatePolicy.CeilingOf(call.Capability);
        if (RelayGatePolicy.Verdict(system, tier, ceiling) is { } verdict)
        {
            return verdict;
        }
        var sessionGranted = !call.ConsentScreen && _tiers.SessionGranted(call.Capability, _now());
        return RelayGatePolicy.RequiresApproval(tier, ceiling, sessionGranted, call.ConsentScreen)
            ? null
            : new(true, false, null, system, tier, "auto");
    }

    /// <summary>The whole gate. <paramref name="present"/> is called only when a prompt can actually be shown.</summary>
    public async Task<GateResult> AuthorizeAsync(GateCall call, Func<GatePrompt, CancellationToken, Task<InterlockChoice>> present, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(present);
        if (DecideWithoutPrompt(call) is { } decided)
        {
            return decided;
        }
        var tier = _tiers.TierOf(call.Capability);
        var system = call.System.State;
        if (CountsTowardApprovalFrequency(call.Capability, call.ArgsIdentity) && !_approvalRate.Accepted(call.Capability))
        {
            return new(false, true, RelayError.RateLimited, system, tier, "rate_limited");
        }
        int index;
        lock (_callCounts)
        {
            index = _callCounts[call.Capability] = _callCounts.GetValueOrDefault(call.Capability) + 1;
        }
        var prompt = new GatePrompt(call.Capability, call.ArgsIdentity,
            RelayGatePolicy.CeilingOf(call.Capability) != TierCeiling.AskOnly && !call.ConsentScreen, index);
        var outcome = await RequestAsync(prompt, call.BudgetMs, present, ct).ConfigureAwait(false);
        switch (outcome)
        {
            case ApprovalOutcome.AllowedOnce:
                return new(true, true, null, system, tier, "approved_once");
            case ApprovalOutcome.AllowedForSession:
                _tiers.GrantForSession(call.Capability, _now() + RelayGatePolicy.SessionGrantMs);
                return new(true, true, null, system, tier, "approved_session");
            default:
                var error = RelayGatePolicy.ErrorFor(outcome) ?? RelayError.GateHostDenied;
                return new(false, true, error, system, tier, RelayGatePolicy.ApprovalLabel(outcome, error));
        }
    }

    /// <summary>Upstream <c>InterlockBroker.request</c>: too short a window is "still awaiting", never a prompt.</summary>
    private async Task<ApprovalOutcome> RequestAsync(GatePrompt prompt, long budgetMs, Func<GatePrompt, CancellationToken, Task<InterlockChoice>> present, CancellationToken ct)
    {
        var window = Math.Min(_windowMs, budgetMs);
        if (window < RelayGatePolicy.MinPromptWindowMs)
        {
            return ApprovalOutcome.AwaitingConsent;
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(window));
        try
        {
            var choice = await present(prompt, cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
            return RelayGatePolicy.OutcomeOf(choice);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Cut short by the call's budget = retry; the full window ran out = nobody answered.
            return window < _windowMs ? ApprovalOutcome.AwaitingConsent : ApprovalOutcome.TimedOut;
        }
    }

    /// <summary>Upstream <c>countsTowardApprovalFrequency</c>: the same question within the window counts once.</summary>
    private bool CountsTowardApprovalFrequency(string wire, string argsIdentity)
    {
        var key = wire + ":" + argsIdentity;
        var windowStart = _now() - RelayRateLimiter.WindowMs;
        lock (_seen)
        {
            if (_seen.TryGetValue(key, out var node))
            {
                if (node.Value.At >= windowStart)
                {
                    return false;
                }
                _seenOrder.Remove(node);
            }
            _seen[key] = _seenOrder.AddLast((key, _now()));
            while (_seen.Count > RelayGatePolicy.ApprovalIdentityCapacity)
            {
                var eldest = _seenOrder.First!;
                _seenOrder.RemoveFirst();
                _seen.Remove(eldest.Value.Key);
            }
            return true;
        }
    }
}
