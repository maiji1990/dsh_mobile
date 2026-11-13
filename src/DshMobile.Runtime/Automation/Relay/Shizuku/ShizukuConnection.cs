// Decision rules ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/exec/shizuku/ShizukuState.kt (ping / sync / requestAuthorization)
// Registered in docs/UPSTREAMS.md.
//
// Security: this only decides whether to *ask*. Shizuku v11+ keeps the grant inside the Shizuku server; the user
// confirms it in Shizuku's own dialog, `pm grant` cannot replace it, and nothing here ever answers that dialog.

namespace DshMobile.Runtime.Automation.Relay.Shizuku;

/// <summary>What the "connect Shizuku" action can do right now.</summary>
public enum ShizukuConnectState
{
    /// <summary>No live binder: Shizuku is not running (or not installed). The app does not start it.</summary>
    NotRunning,

    /// <summary>A pre-v11 server: upstream treats it as unavailable.</summary>
    Unsupported,

    /// <summary>Already authorized inside Shizuku: no dialog is shown again.</summary>
    Granted,

    /// <summary>The user chose "deny and don't ask again": the system will not show the dialog; they must grant it in Shizuku.</summary>
    PermanentlyDenied,

    /// <summary>Asking shows Shizuku's own grant dialog, which the user answers.</summary>
    CanRequest,
}

/// <summary>A probe of the Shizuku client (all false / -1 when the binder is absent).</summary>
public sealed record ShizukuProbe(bool PreV11, bool BinderAlive, bool PermissionGranted, bool ShouldShowRationale, int Version, int Uid);

public static class ShizukuConnection
{
    /// <summary>Upstream <c>ShizukuState.PERMISSION_REQUEST_CODE</c>: Relay's own result listener only acts on this code.</summary>
    public const int PermissionRequestCode = 41;

    /// <summary>Upstream <c>ping()</c>: a pre-v11 server never counts as alive.</summary>
    public static bool Alive(ShizukuProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        return !probe.PreV11 && probe.BinderAlive;
    }

    /// <summary>Upstream <c>requestAuthorization()</c>'s guards, in its order.</summary>
    public static ShizukuConnectState Decide(ShizukuProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (probe.BinderAlive && probe.PreV11)
        {
            return ShizukuConnectState.Unsupported;
        }
        if (!Alive(probe))
        {
            return ShizukuConnectState.NotRunning;
        }
        if (probe.PermissionGranted)
        {
            return ShizukuConnectState.Granted;
        }
        return probe.ShouldShowRationale ? ShizukuConnectState.PermanentlyDenied : ShizukuConnectState.CanRequest;
    }
}
