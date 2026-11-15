// Values ported from DSHBox v1.5.1 (7c9d33c) common/.../Constants.kt and sandbox-manager/.../SandboxConfig.kt
// (GPL-3.0-or-later). Only facts the guest / existing on-device data depend on are carried over; DSHBox brand
// preferences and UI keys are deliberately not ported. Changing any value breaks existing installs.

namespace DshMobile.Runtime;

/// <summary>Guest-visible contract of the runtime (paths and endpoints that existing installs rely on).</summary>
public static class RuntimeConstants
{
    public const string DshHost = "127.0.0.1";
    public const int DshPort = 3080;
    public const string DshBaseUrl = "http://127.0.0.1:3080";

    public const int MinSupportedSdk = 29;
    public const int MaxAutoRestartAttempts = 3;
    public static readonly TimeSpan HealthcheckTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DshReadyTimeout = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan DshUnresponsiveGrace = TimeSpan.FromSeconds(120);

    /// <summary>Minimum free bytes before a guest npm install starts.</summary>
    public const long DshInstallMinFreeBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>Default Linux workspace inside the guest (host <c>user-data</c>).</summary>
    public const string SandboxWorkspace = "/root/projects";

    /// <summary>Marker in the sandbox keepalive command line, used to find the sandbox PRoot process.</summary>
    public const string SandboxKeepaliveMarker = "dshapp-sandbox-keepalive";

    /// <summary>DSH-only entry token; matches the DSH PRoot cmdline but NOT the sandbox keepalive.</summary>
    public const string DshStartScript = "@deepseek-ai/dsh/lib/bin.js";

    /// <summary>Guest path of the DSH entry (DSH layer bound at /opt/dshapp/runtime).</summary>
    public const string GuestDshBin = "/opt/dshapp/runtime/node_modules/@deepseek-ai/dsh/lib/bin.js";

    /// <summary>Profile the host starts DSH with (<c>dsh --profile web</c>).</summary>
    public const string DshWebProfile = "web";

    /// <summary>Guest dir of the Android hard-link compat shims (host <c>filesDir/dshmobile</c>).</summary>
    public const string LinkShimGuestDir = "/opt/dshmobile";
    public const string LinkShimGuestPath = "/opt/dshmobile/link-shim.mjs";
    public const string LinkShimSoGuestPath = "/opt/dshmobile/libdshmobile-link.so";

    /// <summary>Guest toolchain cache mount (host <c>filesDir/guest-cache</c>): pnpm store, corepack.</summary>
    public const string GuestCacheDir = "/opt/dshmobile-cache";
    public const string GuestCacheDirLeaf = "guest-cache";

    /// <summary>App-managed assets inside the workspace (relative to user-data / guest <c>/root/projects</c>).</summary>
    public const string AssetsRelativeDir = ".dsh/dshmobile";
    public const string AssetsGuestDir = "/root/projects/.dsh/dshmobile";

    /// <summary>The <c>dsh</c> wrapper written into the workspace (GuestToolchain).</summary>
    public const string GuestDshWrapper = "/root/projects/.dsh/dshmobile/bin/dsh";

    /// <summary>
    /// App-written state inside each installed layer (<c>layer-&lt;name&gt;.sha256</c> sentinels, the DSH layer's
    /// <c>version</c>). DSHBox used <c>.dshbox</c> (ADR-0019; the bundle's own <c>.dshbox/env.d</c> is layer content).
    /// </summary>
    public const string LayerStateDir = ".dshmobile";

    /// <summary>Runtime layer names, in assembly order.</summary>
    public static IReadOnlyList<string> LayerNames { get; } = ["base", "node", "android-side"];
}
