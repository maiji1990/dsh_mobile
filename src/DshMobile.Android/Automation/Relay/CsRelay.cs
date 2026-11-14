// Android composition root of the C# Relay (PR-07 step 4). The container logic is DshMobile.Runtime RelayHost.cs
// (ported from DSHBox RelayContainer.kt / RelayRuntime.kt); this file only builds the platform objects and wires the
// Android-side callbacks. Upstream reference for the device facts: DSHBox pilot/interlock-relay-core (GPL-3.0,
// v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4) src/main/java/interlock/relay/core/interlock/SystemStateProbe.kt
// (settingsKeyNamesSelf, microphoneBlockedByPolicy). Registered in docs/UPSTREAMS.md.
//
// LegacyWithShadow (ADR-0007): the legacy Relay keeps deciding for the guest. This host is opt-in, serves its own
// mailbox (files/pilot-cs/run, never the guest's), reads the same Relay preferences (tiers, surface preference,
// shell opt-ins) read-only, and asks for approval on its own notification card, so every C# write still needs the
// user's own answer. Nothing here enables a system permission.

using Android.App;
using Android.Content;
using Android.Content.PM;
using DshMobile.Runtime.Automation.Relay;
using DshMobile.Runtime.Automation.Relay.Exec.Direct;
using DshMobile.Runtime.Automation.Relay.Storage;
using DshMobile.Runtime.Automation.Relay.Transport;

namespace DshMobile.App.Automation.Relay;

public static class CsRelay
{
    /// <summary>The C# host's own directories: never the legacy Relay's mailbox, never bound into the guest.</summary>
    public static RelayPathPolicy PathPolicy { get; } = RelayPathPolicy.DshMobile with { GuestEntry = "/opt/pilot-cs", HostDirName = "pilot-cs" };

    private static readonly Lock Gate = new();
    private static Timer? _ticker;
    private static (bool Running, bool Granted)? _lastShizuku;
    private static AndroidShizukuPlatform? _shizuku;

    /// <summary>The process-wide window the C# notification listener feeds.</summary>
    public static NotificationWindow NotificationFeed { get; } = new(() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public static RelayHost? Host { get; private set; }

    public static RelayRuntimePhase Phase => Host?.Phase ?? RelayRuntimePhase.Stopped;

    public static void Start(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var app = context.ApplicationContext ?? context;
        lock (Gate)
        {
            Host ??= Build(app);
            Host.Start();
            _ticker ??= new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(3));
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _ticker?.Dispose();
            _ticker = null;
            _lastShizuku = null;
            Host?.Stop();
        }
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

    private static RelayHost Build(Context app)
    {
        var paths = new RelayPaths(app.FilesDir!.AbsolutePath, PathPolicy);
        var shizuku = new AndroidShizukuPlatform(app, paths);
        _shizuku = shizuku;
        var notifications = new RelayApprovalNotifications(app);
        var host = new RelayHost(new RelayHostPlatform
        {
            FilesDir = app.FilesDir!.AbsolutePath,
            PathPolicy = PathPolicy,
            SelfPackage = app.PackageName!,
            SdkInt = (int)Android.OS.Build.VERSION.SdkInt,
            Prefs = new AndroidRelayPrefs(app),
            A11y = new AndroidA11yPlatform(app),
            Direct = new AndroidDirectPlatform(app, paths),
            Audio = new AndroidAudioRecorder(),
            Projection = new AndroidProjectionPlatform(app, () => AppInForeground(app)),
            Shizuku = shizuku,
            DeviceFacts = () => DeviceFacts(app),
            // No in-app approval card is wired for the C# host yet: the queue must route to the notification card,
            // which the user answers (allow goes through the in-app confirmation page).
            UiInForeground = () => false,
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
        });
        shizuku.Backend = host.Shizuku;
        RelayApprovalReceiver.Resolve = host.ResolveApproval;
        DshAccessibilityService.BoundChanged += host.OnForegroundChanged;
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
