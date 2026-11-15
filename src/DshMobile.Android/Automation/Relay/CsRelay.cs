// Android composition root of the C# Relay (PR-07 step 4). The container logic is DshMobile.Runtime RelayHost.cs
// (ported from DSHBox RelayContainer.kt / RelayRuntime.kt); this file only builds the platform objects and wires the
// Android-side callbacks. Upstream reference for the device facts: DSHBox pilot/interlock-relay-core (GPL-3.0,
// v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4) src/main/java/interlock/relay/core/interlock/SystemStateProbe.kt
// (settingsKeyNamesSelf, microphoneBlockedByPolicy). Registered in docs/UPSTREAMS.md.
//
// Two opt-in modes, one C# host at a time (ADR-0009 / ADR-0010):
//   Shadow: serves its own mailbox (files/pilot-cs, never bound into the guest) next to the legacy Relay, for the
//           dual-relay comparison.
//   Guest:  serves the guest's mailbox (files/pilot, guest /opt/pilot) and publishes the guest files there, in place
//           of the legacy Relay, which the caller must have stopped first (never two relays on one mailbox).
// Both read the same Relay preferences (tiers, surface preference, shell opt-ins) and ask for approval on the C# card
// (ADR-0011: inside the 手机操控 page while it is in front, else the floating card when the user granted "display over
// other apps", else the notification card), so every C# write still needs the user's own answer. Nothing here enables
// a system permission.

using Android.App;
using Android.Content;
using Android.Content.PM;
using DshMobile.Runtime;
using DshMobile.Runtime.Automation;
using DshMobile.Runtime.Automation.Relay;
using DshMobile.Runtime.Automation.Relay.Exec.Direct;
using DshMobile.Runtime.Automation.Relay.Logging;
using DshMobile.Runtime.Automation.Relay.Shizuku;
using DshMobile.Runtime.Automation.Relay.Storage;
using DshMobile.Runtime.Automation.Relay.Transport;

namespace DshMobile.App.Automation.Relay;

/// <summary>Which mailbox the C# host serves.</summary>
public enum CsRelayMode
{
    /// <summary>Its own mailbox (files/pilot-cs), next to the legacy Relay: the dual-relay shadow.</summary>
    Shadow,

    /// <summary>The guest's mailbox (files/pilot, guest /opt/pilot), in place of the legacy Relay.</summary>
    Guest,
}

public static class CsRelay
{
    /// <summary>The shadow host's own directories: never the legacy Relay's mailbox, never bound into the guest.</summary>
    public static RelayPathPolicy ShadowPathPolicy { get; } = RelayPathPolicy.DshMobile with { GuestEntry = "/opt/pilot-cs", HostDirName = "pilot-cs" };

    public static RelayPathPolicy PathPolicyOf(CsRelayMode mode) => mode == CsRelayMode.Guest ? RelayPathPolicy.DshMobile : ShadowPathPolicy;

    private static readonly Lock Gate = new();
    private static Timer? _ticker;
    private static (bool Running, bool Granted)? _lastShizuku;
    private static AndroidShizukuPlatform? _shizuku;
    private static Action? _unhook;
    private static AndroidOverlayApproval? _overlay;
    private static volatile bool _pageCardShown;

    /// <summary>
    /// Upstream <c>surfaces.uiInForeground()</c>: the 手机操控 page is resumed and renders the in-page approval card
    /// (ADR-0011). Set by that page; every change re-decides where a pending question is shown.
    /// </summary>
    public static bool PageCardShown
    {
        get => _pageCardShown;
        set
        {
            if (_pageCardShown == value)
            {
                return;
            }
            _pageCardShown = value;
            // Rerouting reads the trusted-display state (a Shizuku binder call): never on the page's main thread.
            if (Host is { } host)
            {
                _ = Task.Run(host.OnForegroundChanged);
            }
        }
    }

    /// <summary>The process-wide window the C# notification listener feeds.</summary>
    public static NotificationWindow NotificationFeed { get; } = new(() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public static RelayHost? Host { get; private set; }

    public static RelayRuntimePhase Phase => Host?.Phase ?? RelayRuntimePhase.Stopped;

    /// <summary>The mode of <see cref="Host"/> (meaningful only while there is one).</summary>
    public static CsRelayMode Mode { get; private set; }

    /// <summary>The C# host is up on the guest's mailbox: the legacy Relay must stay stopped.</summary>
    public static bool GuestFacing => Mode == CsRelayMode.Guest && Phase is not RelayRuntimePhase.Stopped;

    /// <summary>
    /// Starts the host in <paramref name="mode"/>. A host in the other mode is stopped and dropped first (one C# host at
    /// a time: they share the approval receiver, the accessibility callback and the Relay preferences).
    /// </summary>
    public static void Start(Context context, CsRelayMode mode = CsRelayMode.Shadow)
    {
        ArgumentNullException.ThrowIfNull(context);
        var app = context.ApplicationContext ?? context;
        lock (Gate)
        {
            if (Host is { } other && Mode != mode)
            {
                StopLocked();
                _unhook?.Invoke();
                _unhook = null;
                other.Dispose();
                Host = null;
            }
            Mode = mode;
            Host ??= Build(app, PathPolicyOf(mode));
            Host.Start();
            _ticker ??= new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(3));
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            StopLocked();
        }
    }

    private static void StopLocked()
    {
        _ticker?.Dispose();
        _ticker = null;
        _lastShizuku = null;
        // No floating card left behind for a channel that no longer answers.
        _overlay?.Withdraw();
        Host?.Stop();
    }

    /// <summary>
    /// ADR-0010: the C# host takes the guest's mailbox, then the <c>ui_*</c> layer is written (as the legacy
    /// <c>relay.start</c> does after <c>MobileAutomation.start</c>). The caller has stopped the legacy Relay. False when
    /// the layer path holds someone else's file (nothing overwritten; the host still serves the CLI).
    /// </summary>
    /// The ui_* guest scripts are staged by C# first (they used to appear as a side effect of the legacy bridge's first
    /// call); if they cannot be written the layer is not registered (DSH would load a facade that is not there).
    public static (bool Registered, string? StageError) StartGuest(Context context, RuntimeLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Start(context, CsRelayMode.Guest);
        try
        {
            UiFacadeAssets.Stage(layout.GuestCacheDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (false, $"{UiFacadeAssets.NotStaged}: {e.Message}");
        }
        return (UiToolsRegistration.Register(layout.UserDataDir), null);
    }

    /// <summary>
    /// The legacy Relay was started from this process (long-press fallback) and not stopped since. Process-wide: the legacy
    /// Relay lives in this process and does not come back on its own after the process dies (its sticky service stops
    /// itself when no channel runs), so false means only a C# Relay can be serving the guest mailbox.
    /// </summary>
    public static bool LegacyFallbackActive { get; set; }

    /// <summary>The legacy <c>relay.stop</c> / <c>sandbox.stop</c> pair for the guest-facing host: stop, then remove our layer.</summary>
    public static bool StopGuest(string userDataDir)
    {
        lock (Gate)
        {
            if (Mode != CsRelayMode.Guest || Host is null)
            {
                return true;
            }
            StopLocked();
        }
        return UiToolsRegistration.Unregister(userDataDir);
    }

    /// <summary>
    /// Stands in for upstream's Shizuku listeners (no C# binding for them): a change of (running, granted) is reported
    /// to the host, which binds the shell service once both hold.
    /// </summary>
    private static void Tick()
    {
        if (Host is not { } host || _shizuku is not { } shizuku)
        {
            return;
        }
        try
        {
            var now = (shizuku.ServerAvailable, shizuku.Granted);
            if (_lastShizuku != now)
            {
                _lastShizuku = now;
                host.OnShizukuStateChanged(now.Item1, now.Item2);
            }
            host.OnHeartbeat();
        }
#pragma warning disable CA1031 // a timer callback must not throw into the thread pool
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static RelayHost Build(Context app, RelayPathPolicy policy)
    {
        var paths = new RelayPaths(app.FilesDir!.AbsolutePath, policy);
        var shizuku = new AndroidShizukuPlatform(app, paths);
        _shizuku = shizuku;
        var notifications = new RelayApprovalNotifications(app);
        _overlay ??= new AndroidOverlayApproval(app, (cap, cause, at) =>
            Host?.RunLog.Warn(LogSubsystem.Gate, LogEvent.OverlayUnavailable, ("cap", cap), ("cause", cause), ("at", at)));
        var host = new RelayHost(new RelayHostPlatform
        {
            FilesDir = app.FilesDir!.AbsolutePath,
            PathPolicy = policy,
            SelfPackage = app.PackageName!,
            SdkInt = (int)Android.OS.Build.VERSION.SdkInt,
            Prefs = new AndroidRelayPrefs(app),
            A11y = new AndroidA11yPlatform(app),
            Direct = new AndroidDirectPlatform(app, paths),
            Audio = new AndroidAudioRecorder(),
            Projection = new AndroidProjectionPlatform(app, () => AppInForeground(app)),
            Shizuku = shizuku,
            DeviceFacts = () => DeviceFacts(app),
            // ADR-0011: the in-page card while the 手机操控 page is in front (the queue routes PAGE first), then the
            // floating card, then the notification card (whose allow goes through the in-app confirmation page).
            UiInForeground = () => _pageCardShown,
            Overlay = _overlay,
            AppInForeground = () => AppInForeground(app),
            DeviceInteractive = () => (app.GetSystemService(Context.PowerService) as Android.OS.PowerManager)?.IsInteractive != false,
            Notifications = notifications,
            NotificationFeed = NotificationFeed,
            CharCount = n => app.GetString(Resource.String.relay_cs_char_count, n),
            UidResolver = pkg =>
            {
                try
                {
                    return app.PackageManager!.GetPackageInfo(pkg, 0)?.ApplicationInfo?.Uid;
                }
                catch (Java.Lang.Throwable)
                {
                    return null;
                }
            },
            StartChannelService = () => RelayCsChannelService.Start(app),
            StopChannelService = () => RelayCsChannelService.Stop(app),
            ShizukuState = () =>
            {
                var probe = ShizukuClient.Probe();
                var alive = ShizukuConnection.Alive(probe);
                return new ShizukuSnapshot(alive, alive && probe.PermissionGranted, alive ? probe.Uid : -1, alive ? probe.Version : -1);
            },
        });
        shizuku.Backend = host.Shizuku;
        RelayApprovalReceiver.Resolve = host.ResolveApproval;
        Action bound = host.OnAccessibilityBoundChanged;
        DshAccessibilityService.BoundChanged += bound;
        _unhook = () => DshAccessibilityService.BoundChanged -= bound;
        return host;
    }

    private static readonly string[] Permissions =
    [
        "android.permission.READ_CONTACTS", "android.permission.WRITE_CONTACTS", "android.permission.READ_CALENDAR",
        "android.permission.WRITE_CALENDAR", "android.permission.ACCESS_FINE_LOCATION", "android.permission.ACCESS_COARSE_LOCATION",
        "android.permission.READ_MEDIA_IMAGES", "android.permission.READ_MEDIA_VIDEO", "android.permission.READ_MEDIA_AUDIO",
        "android.permission.RECORD_AUDIO", "android.permission.REQUEST_INSTALL_PACKAGES", "android.permission.POST_NOTIFICATIONS",
    ];

    /// <summary>Upstream <c>SystemStateProbe</c>'s facts: bound service first, the Settings key only as a fallback.</summary>
    private static RelaySystemFacts DeviceFacts(Context app)
    {
        var granted = Permissions.Where(p => app.CheckSelfPermission(p) == Permission.Granted).ToHashSet(StringComparer.Ordinal);
        return new RelaySystemFacts(
            (int)Android.OS.Build.VERSION.SdkInt,
            A11yBound: RelayA11yTreeSource.Current() is not null,
            A11yListed: SettingsKeyNamesSelf(app, "enabled_accessibility_services"),
            ShellAvailable: false,
            TrustedDisplayAvailable: false,
            NotificationListener: NotificationFeed.IsConnected || SettingsKeyNamesSelf(app, "enabled_notification_listeners"),
            granted,
            MicrophoneBlocked: MicrophoneBlocked(app));
    }

    private static bool SettingsKeyNamesSelf(Context app, string key)
    {
        try
        {
            var value = Android.Provider.Settings.Secure.GetString(app.ContentResolver, key);
            return (value ?? "").Split(':').Any(c => c.Contains(app.PackageName!, StringComparison.OrdinalIgnoreCase));
        }
        catch (Java.Lang.Throwable)
        {
            return false;
        }
    }

    private static bool MicrophoneBlocked(Context app)
    {
        try
        {
            var ops = (AppOpsManager?)app.GetSystemService(Context.AppOpsService);
#pragma warning disable CA1422 // upstream uses the same deprecated form on every level
            return ops?.UnsafeCheckOpNoThrow(AppOpsManager.OpstrRecordAudio, Android.OS.Process.MyUid(), app.PackageName!) == AppOpsManagerMode.Ignored;
#pragma warning restore CA1422
        }
        catch (Java.Lang.Throwable)
        {
            return false;
        }
    }

    private static bool AppInForeground(Context app)
    {
        try
        {
            var am = (ActivityManager?)app.GetSystemService(Context.ActivityService);
            return am?.RunningAppProcesses?.Any(p => p.ProcessName == app.PackageName
                && p.Importance <= Importance.Foreground) == true;
        }
        catch (Java.Lang.Throwable)
        {
            return false;
        }
    }
}

/// <summary>A page's subscription to the host's phase changes, dropped when the page pauses.</summary>
internal sealed class RelayHostWatch : IDisposable
{
    private readonly RelayHost _host;
    private readonly Action<RelayRuntimePhase> _handler;

    public RelayHostWatch(RelayHost host, Action changed)
    {
        _host = host;
        _handler = _ => changed();
        host.PhaseChanged += _handler;
    }

    public void Dispose() => _host.PhaseChanged -= _handler;
}

/// <summary>Upstream <c>RelayPrefs.Default</c>: SharedPreferences by file name (the same files the legacy Relay uses).</summary>
internal sealed class AndroidRelayPrefs(Context context) : IRelayPrefs
{
    private readonly Context _app = context.ApplicationContext ?? context;

    private ISharedPreferences Prefs(string file) => _app.GetSharedPreferences(file, FileCreationMode.Private)!;

    public string? GetString(string file, string key)
    {
        try
        {
            return Prefs(file).GetString(key, null);
        }
        catch (Java.Lang.Throwable)
        {
            return null;
        }
    }

    public void PutString(string file, string key, string value)
    {
        try
        {
            Prefs(file).Edit()!.PutString(key, value)!.Apply();
        }
        catch (Java.Lang.Throwable)
        {
        }
    }

    public bool GetBoolean(string file, string key, bool defValue)
    {
        try
        {
            return Prefs(file).GetBoolean(key, defValue);
        }
        catch (Java.Lang.Throwable)
        {
            return defValue;
        }
    }
}
