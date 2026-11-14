// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/exec/a11y/RelayAccessibilityService.kt
//   src/main/res/xml/relay_accessibility_service_config.xml (same flags, own description)
// Registered in docs/UPSTREAMS.md.

using Android.AccessibilityServices;
using Android.Runtime;
using Android.Views.Accessibility;

namespace DshMobile.App.Automation.Relay;

/// <summary>
/// This app's own accessibility service (PR-07 step 4, service ownership). It only publishes "I am bound" and lends its
/// instance to the C# Relay's backends; every decision stays in the Runtime. The user enables it in system Settings
/// (the app never does). While both this one and the legacy service are listed, the C# host prefers this one.
/// Declared in AndroidManifest.xml (BIND_ACCESSIBILITY_SERVICE, exported for the system only), hence [Register].
/// </summary>
[Register("dshmobile.relay.DshAccessibilityService")]
public sealed class DshAccessibilityService : AccessibilityService
{
    private static WeakReference<DshAccessibilityService>? _bound;

    /// <summary>The bound instance, or null when the user has not enabled it.</summary>
    public static DshAccessibilityService? Instance =>
        Volatile.Read(ref _bound) is { } weak && weak.TryGetTarget(out var service) ? service : null;

    /// <summary>Bound / unbound (upstream <c>onBoundChanged</c>): the host re-reads the system facts.</summary>
    public static event System.Action? BoundChanged;

    protected override void OnServiceConnected()
    {
        base.OnServiceConnected();
        Volatile.Write(ref _bound, new WeakReference<DshAccessibilityService>(this));
        BoundChanged?.Invoke();
    }

    public override void OnAccessibilityEvent(AccessibilityEvent? e)
    {
    }

    public override void OnInterrupt()
    {
    }

    public override void OnDestroy()
    {
        Volatile.Write(ref _bound, null);
        BoundChanged?.Invoke();
        base.OnDestroy();
    }
}
