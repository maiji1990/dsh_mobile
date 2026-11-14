// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/surface/SurfacePolicy.kt       (SurfacePreference, SurfaceDecision, decide, rejectReason)
//   src/main/java/interlock/relay/core/surface/SurfacePreferences.kt  ("relay" prefs, key surface_preference, FOREGROUND default)
//   src/main/java/interlock/relay/core/protocol/CapabilityRegistry.kt (per-capability surfaces and backend order, NODE_SURFACES)
//   src/main/java/interlock/relay/core/surface/BackendDispatcher.kt   (canServe, isImplemented)
//   src/main/java/interlock/relay/core/exec/a11y/A11yBackend.kt       (supports, TREE_ / COORDINATE_CAPABILITIES)
//   src/main/java/interlock/relay/core/exec/direct/DirectBackend.kt   (supports)
//   src/main/java/interlock/relay/core/exec/shizuku/ShizukuBackend.kt (supports)
//   src/main/java/interlock/relay/core/runtime/RelayContainer.kt      (policy wiring, trustedDisplayReady)
// Registered in docs/UPSTREAMS.md. Locked by tests/TestVectors/relay-surface.json.

namespace DshMobile.Runtime.Automation.Relay;

/// <summary>The user's execution-mode preference (upstream <c>SurfacePreference</c>).</summary>
public enum SurfacePreference
{
    Foreground,
    BackgroundPreferred,
}

/// <summary>
/// Where one call runs (upstream <c>SurfaceDecision</c>). <see cref="RequiresMode"/> is set only when the preference
/// chain cannot reach any of the capability's surfaces and none of them is the foreground: the call is refused with
/// <c>E_SURFACE_MODE_REQUIRED</c> before the gate, not faked onto the foreground.
/// </summary>
public sealed record SurfaceDecision(
    SurfaceKind Surface,
    SurfaceKind? DegradedFrom = null,
    string? Reason = null,
    SurfaceKind? RequiresMode = null);

/// <summary>Relay's three built-in backends (upstream <c>BackendId</c>).</summary>
public enum RelayBackendId
{
    Direct,
    A11y,
    Shizuku,
}

/// <summary>The registry facts the surface policy and the dispatcher read: allowed surfaces and backend order.</summary>
public static class RelaySurfaces
{
    /// <summary>Upstream <c>SurfacePolicy.MIN_BACKEND_SDK</c> (Android 13): the trusted display needs it.</summary>
    public const int MinBackendSdk = 33;

    /// <summary>Upstream SharedPreferences file <c>RelayPrefs.FILE_MAIN</c> and the preference key.</summary>
    public const string PrefsFile = "relay";
    public const string PreferenceKey = "surface_preference";

    private static readonly SurfaceKind[] None = [];
    private static readonly SurfaceKind[] Node = [SurfaceKind.Foreground, SurfaceKind.BackendTrusted];
    private static readonly SurfaceKind[] Fg = [SurfaceKind.Foreground];

    private static readonly RelayBackendId[] Direct = [RelayBackendId.Direct];
    private static readonly RelayBackendId[] A11y = [RelayBackendId.A11y];
    private static readonly RelayBackendId[] Shizuku = [RelayBackendId.Shizuku];
    private static readonly RelayBackendId[] A11yShizuku = [RelayBackendId.A11y, RelayBackendId.Shizuku];
    private static readonly RelayBackendId[] DirectShizuku = [RelayBackendId.Direct, RelayBackendId.Shizuku];

    /// <summary>Declaration order matters: upstream sets are insertion-ordered and <c>requiresMode</c> is the first.</summary>
    private static readonly Dictionary<string, (SurfaceKind[] Surfaces, RelayBackendId[] Backends)> Table = new(StringComparer.Ordinal)
    {
        ["ui.snapshot"] = (Node, A11y),
        ["screen.capture"] = ([SurfaceKind.Foreground, SurfaceKind.BackendTrusted, SurfaceKind.ObserveOnly], [RelayBackendId.A11y, RelayBackendId.Direct, RelayBackendId.Shizuku]),
        ["screen.observe"] = ([SurfaceKind.ObserveOnly], Direct),
        ["screen.record"] = ([SurfaceKind.ObserveOnly, SurfaceKind.Foreground, SurfaceKind.BackendTrusted], DirectShizuku),
        ["notify.read"] = (None, Direct),
        ["ui.tap"] = (Node, A11yShizuku),
        ["ui.swipe"] = (Node, A11yShizuku),
        ["ui.click"] = (Node, A11y),
        ["ui.longClick"] = (Node, A11y),
        ["ui.select"] = (Node, A11y),
        ["ui.dismiss"] = (Node, A11y),
        ["ui.scroll"] = (Node, A11y),
        ["ui.setValue"] = (Node, A11y),
        ["ui.waitFor"] = (Node, A11y),
        ["ui.setProgress"] = (Node, A11y),
        ["ui.imeAction"] = (Node, A11y),
        ["ui.node"] = (Node, A11y),
        ["ui.text"] = (Node, A11yShizuku),
        ["ui.key"] = (Node, A11yShizuku),
        ["app.launch"] = (Node, DirectShizuku),
        ["sys.intent"] = (Fg, Direct),
        ["sys.shell"] = (None, Shizuku),
        ["app.stop"] = (None, Shizuku),
        ["surface.virtual"] = ([SurfaceKind.BackendTrusted], Shizuku),
        ["clip.read"] = (Fg, Direct),
        ["clip.write"] = (Fg, Direct),
        ["contact.read"] = (None, Direct),
        ["contact.write"] = (None, Direct),
        ["cal.read"] = (None, Direct),
        ["cal.write"] = (None, Direct),
        ["loc.read"] = (None, Direct),
        ["media.read"] = (None, Direct),
        ["media.write"] = (None, Direct),
        ["audio.capture"] = (None, Direct),
        ["pkg.query"] = (None, Direct),
        ["app.install"] = (None, Shizuku),
        ["sys.settings.write"] = (None, Shizuku),
        ["appops.set"] = (None, Shizuku),
        ["notify.post"] = (None, Direct),
    };

    /// <summary>Every registered capability wire, in registry order (upstream <c>RelayCapabilities.BuiltIn.all()</c>).</summary>
    public static IReadOnlyCollection<string> Wires => Table.Keys;

    /// <summary>The capability's allowed surfaces in registry order; empty for mode-independent capabilities.</summary>
    public static IReadOnlyList<SurfaceKind> SurfacesOf(string wire) =>
        Table.TryGetValue(wire, out var e) ? e.Surfaces : throw new ArgumentException($"unknown capability {wire}", nameof(wire));

    /// <summary>The capability's backends in preference order.</summary>
    public static IReadOnlyList<RelayBackendId> BackendsOf(string wire) =>
        Table.TryGetValue(wire, out var e) ? e.Backends : throw new ArgumentException($"unknown capability {wire}", nameof(wire));

    public static SurfaceKind? FromWire(string? wire) => wire switch
    {
        "foreground" => SurfaceKind.Foreground,
        "trusted-display" => SurfaceKind.BackendTrusted,
        "observe-only" => SurfaceKind.ObserveOnly,
        _ => null,
    };

    /// <summary>
    /// Upstream <c>SurfacePreferences.preference()</c>: <c>SurfacePreference.valueOf(raw)</c> (the exact enum name),
    /// anything else or nothing stored is the foreground.
    /// </summary>
    public static SurfacePreference ParsePreference(string? raw) =>
        raw == "BACKGROUND_PREFERRED" ? SurfacePreference.BackgroundPreferred : SurfacePreference.Foreground;

    public static string Name(this SurfacePreference preference) =>
        preference == SurfacePreference.BackgroundPreferred ? "BACKGROUND_PREFERRED" : "FOREGROUND";
}

/// <summary>
/// The environment facts the backend support matrix reads, as the assembly root wires them upstream:
/// <see cref="ShellAvailable"/> is <c>ShizukuState.available</c>; <see cref="TrustedDisplayAlive"/> is
/// <c>ShizukuBackend.displayAlive()</c>; the accessibility backend sees the display only when both hold
/// (<c>trustedDisplayReady()</c>), and reads its tree there only from Android 13.
/// </summary>
public sealed record RelayBackendFacts(int SdkInt, bool ShellAvailable, bool TrustedDisplayAlive)
{
    public bool A11yTrustedDisplayReachable => ShellAvailable && TrustedDisplayAlive && SdkInt >= RelaySurfaces.MinBackendSdk;
}

/// <summary>
/// Which built-in backend implements which capability on which surface (each upstream backend's <c>supports()</c>),
/// and the dispatcher's two questions built on it. Implementation facts only: whether a backend is reachable right
/// now (accessibility enabled, Shizuku bound) is not asked here, upstream keeps that out of the decision on purpose.
/// </summary>
public static class RelayBackendSupport
{
    private static readonly HashSet<string> Tree = new(StringComparer.Ordinal)
    {
        "ui.snapshot", "ui.node", "ui.waitFor",
        "ui.click", "ui.longClick", "ui.select", "ui.dismiss", "ui.scroll", "ui.setValue", "ui.setProgress", "ui.imeAction",
    };

    private static readonly HashSet<string> Coordinate = new(StringComparer.Ordinal) { "ui.tap", "ui.swipe", "ui.text", "ui.key" };

    private static readonly HashSet<string> DirectAnySurface = new(StringComparer.Ordinal)
    {
        "pkg.query", "app.launch", "contact.read", "contact.write", "cal.read", "cal.write", "loc.read",
        "media.read", "media.write", "notify.post", "notify.read", "audio.capture", "sys.intent", "screen.capture", "screen.record",
    };

    private static readonly HashSet<string> ShizukuAnySurface = new(StringComparer.Ordinal)
    {
        "sys.settings.write", "appops.set", "app.stop", "sys.shell", "app.install",
    };

    private static readonly HashSet<string> ShizukuOnDisplay = new(StringComparer.Ordinal)
    {
        "app.launch", "ui.tap", "ui.swipe", "ui.text", "ui.key", "screen.capture", "screen.record",
    };

    public static bool Supports(RelayBackendId backend, string wire, SurfaceKind surface, RelayBackendFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return backend switch
        {
            RelayBackendId.A11y =>
                Tree.Contains(wire) ? surface == SurfaceKind.Foreground || (surface == SurfaceKind.BackendTrusted && facts.A11yTrustedDisplayReachable)
                : Coordinate.Contains(wire) ? surface == SurfaceKind.Foreground
                : wire == "screen.capture" && surface == SurfaceKind.Foreground && facts.SdkInt >= 30,
            RelayBackendId.Direct =>
                surface != SurfaceKind.BackendTrusted && (DirectAnySurface.Contains(wire)
                    || (wire is "clip.read" or "clip.write" && surface == SurfaceKind.Foreground)
                    || (wire == "screen.observe" && surface == SurfaceKind.ObserveOnly)),
            RelayBackendId.Shizuku =>
                ShizukuAnySurface.Contains(wire)
                || (wire == "surface.virtual" && surface == SurfaceKind.BackendTrusted)
                || (ShizukuOnDisplay.Contains(wire) && surface == SurfaceKind.BackendTrusted && facts.TrustedDisplayAlive),
            _ => false,
        };
    }

    /// <summary>Upstream <c>BackendDispatcher.canServe</c>: some backend of this capability implements it on this surface.</summary>
    public static bool CanServe(string wire, SurfaceKind surface, RelayBackendFacts facts) =>
        RelaySurfaces.BackendsOf(wire).Any(b => Supports(b, wire, surface, facts));

    /// <summary>Upstream <c>BackendDispatcher.isImplemented</c>: implemented on any allowed surface (none = foreground).</summary>
    public static bool IsImplemented(string wire, RelayBackendFacts facts)
    {
        var surfaces = RelaySurfaces.SurfacesOf(wire);
        return (surfaces.Count == 0 ? [SurfaceKind.Foreground] : surfaces).Any(s => CanServe(wire, s, facts));
    }
}

/// <summary>
/// Upstream <c>SurfacePolicy</c>: picks the execution surface from the preference chain, falling back to the
/// foreground (and saying so in <see cref="SurfaceDecision.DegradedFrom"/> / <see cref="SurfaceDecision.Reason"/>)
/// when the background route is unavailable, and naming the required mode when only a non-foreground surface exists.
/// </summary>
public sealed class SurfacePolicy
{
    private readonly Func<SurfacePreference> _preference;
    private readonly Func<bool> _shellAvailable;
    private readonly Func<string, SurfaceKind, bool> _backendCanServe;
    private readonly int _sdkInt;

    public SurfacePolicy(Func<SurfacePreference> preference, Func<bool> shellAvailable, Func<string, SurfaceKind, bool>? backendCanServe, int sdkInt)
    {
        _preference = preference;
        _shellAvailable = shellAvailable;
        _backendCanServe = backendCanServe ?? ((_, _) => true);
        _sdkInt = sdkInt;
    }

    /// <summary>The policy as Relay's assembly root wires it: <c>backendCanServe = dispatcher::canServe</c>.</summary>
    public static SurfacePolicy For(SurfacePreference preference, RelayBackendFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return new(() => preference, () => facts.ShellAvailable, (w, s) => RelayBackendSupport.CanServe(w, s, facts), facts.SdkInt);
    }

    public SurfaceDecision Decide(string wire) => Decide(wire, RelaySurfaces.SurfacesOf(wire));

    /// <summary>Decides for an explicit allowed-surface list (registry order), e.g. a descriptor variant in a test vector.</summary>
    public SurfaceDecision Decide(string wire, IReadOnlyList<SurfaceKind> allowed)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        if (allowed.Count == 0)
        {
            return new(SurfaceKind.Foreground);
        }
        SurfaceKind[] preferred = _preference() == SurfacePreference.BackgroundPreferred
            ? [SurfaceKind.BackendTrusted, SurfaceKind.Foreground, SurfaceKind.ObserveOnly]
            : [SurfaceKind.Foreground, SurfaceKind.ObserveOnly];

        SurfaceKind? blockedFrom = null;
        string? reason = null;
        foreach (var candidate in preferred)
        {
            if (!allowed.Contains(candidate))
            {
                // Wanted the background but the capability declares none: that is itself a degrade.
                if (blockedFrom is null && candidate == SurfaceKind.BackendTrusted)
                {
                    blockedFrom = candidate;
                    reason = RelayError.SurfaceUnavailable.Code;
                }
                continue;
            }
            var rejection = RejectReason(candidate) ?? (_backendCanServe(wire, candidate) ? null : RelayError.SurfaceUnavailable.Code);
            if (rejection is null)
            {
                // Only landing on the foreground counts as degraded (observe-only touches no UI).
                var fellBack = candidate == SurfaceKind.Foreground;
                return new(candidate, fellBack ? blockedFrom : null, fellBack ? reason : null);
            }
            if (blockedFrom is null)
            {
                blockedFrom = candidate;
                reason = rejection;
            }
        }

        // A single-surface capability that is not the foreground: name the mode, never fake a foreground landing.
        if (!allowed.Contains(SurfaceKind.Foreground))
        {
            var required = allowed[0];
            return new(required, RequiresMode: required, Reason: RelayError.SurfaceModeRequired.Code);
        }
        // "Degraded from foreground to foreground" would contradict itself.
        var fallbackFrom = blockedFrom ?? preferred[0];
        return fallbackFrom == SurfaceKind.Foreground
            ? new(SurfaceKind.Foreground)
            : new(SurfaceKind.Foreground, fallbackFrom, reason ?? RelayError.SurfaceUnavailable.Code);
    }

    private string? RejectReason(SurfaceKind candidate)
    {
        if (candidate != SurfaceKind.BackendTrusted)
        {
            return null;
        }
        if (_sdkInt < RelaySurfaces.MinBackendSdk)
        {
            return RelayError.SurfaceApiLevel.Code;
        }
        return _shellAvailable() ? null : RelayError.SurfaceNoShell.Code;
    }
}
