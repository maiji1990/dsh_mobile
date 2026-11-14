// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/protocol/LaunchMediators.kt
// Registered in docs/UPSTREAMS.md. Packages that hold the front while a launch waits for the user to pick an icon.

namespace DshMobile.Runtime.Automation.Relay;

public static class LaunchMediators
{
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "com.vivo.doubleinstance",
        "com.android.intentresolver",
    };

    public static bool IsMediator(string? packageName) => packageName is not null && Known.Contains(packageName);
}
