// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/exec/direct/DirectBackend.kt (supports, execute, settingsAction, handoffNote,
//     semanticIntent, argsError, limitOf, readNotifications, queryLaunchableApps, launchApp, awaitForeground,
//     readContacts, writeContact, readCalendar, writeCalendar, readLocation, writeMedia, headerMatchesKind,
//     readMedia, postNotification, readClipboard, writeClipboard; ARG_SPECS and the constants; appMatches)
// Registered in docs/UPSTREAMS.md. Policy, argument rules, response shapes and the launch verification live here;
// every Android call (PackageManager, ContentResolver, LocationManager, MediaStore, NotificationManager, clipboard,
// startActivity) is one IDirectPlatform member implemented in DshMobile.Android.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace DshMobile.Runtime.Automation.Relay.Exec.Direct;

/// <summary>How a <c>startActivity</c> for a launch intent went.</summary>
public enum LaunchStart
{
    Started,
    NotLaunchable,
    Threw,
}

public sealed record LocationFix(double Latitude, double Longitude, float AccuracyMeters, long TimeMs, string? Provider);

/// <summary>The reading of the clipboard: no service, no clip at all, or the first item's text (null when masked).</summary>
public abstract record ClipRead
{
    public sealed record NoService : ClipRead;

    public sealed record NoClip : ClipRead;

    public sealed record Clip(string? FirstItemText) : ClipRead;
}

/// <summary>An upload opened once and checked on the open handle (upstream <c>RelayPaths.VettedUpload</c>).</summary>
public interface IVettedUpload : IDisposable
{
    long Size { get; }

    /// <summary>Positioned reads on the vetted handle (the header check does not move the copy's start).</summary>
    int ReadAt(long position, Span<byte> buffer);

    Stream OpenRead();

    void DeleteSource();
}

/// <summary>Everything DirectBackend asks Android; each member is one platform call (or a short fixed sequence).</summary>
public interface IDirectPlatform
{
    string AppPackage { get; }

    /// <summary>Launcher activities as (package, label), in PackageManager order.</summary>
    IReadOnlyList<(string Package, string Label)> LaunchableApps();

    /// <summary>getLaunchIntentForPackage + NEW_TASK + startActivity; <paramref name="thrown"/> is the exception's simple name.</summary>
    LaunchStart StartLaunch(string packageName, out string? thrown);

    /// <summary>The focused window's root package (else the active window's), null without the accessibility service.</summary>
    string? ForegroundPackage();

    bool AccessibilityConnected { get; }

    bool OverlayGranted();

    /// <summary>The Intent for a validated template; returns its action, and the resolving package (null: no receiver).</summary>
    string? ResolveTemplate(IntentTemplates.Params template, out string action);

    /// <summary>startActivity for the template's Intent; null on success, else "SimpleName:message" as upstream prints it.</summary>
    string? StartTemplate(IntentTemplates.Params template);

    IEnumerable<(string? Name, string? Number)> QueryPhones();

    /// <summary>applyBatch of the raw contact; the number of results, or throws.</summary>
    int InsertContact(string name, string? number);

    IEnumerable<(long CalendarId, string? Title, long StartMs, long EndMs)> QueryEvents();

    long? FirstCalendarId();

    /// <summary>The new event id (-1 when the provider returned no uri), or throws.</summary>
    long InsertEvent(long calendarId, string title, long startMs, long endMs);

    /// <summary>Null when there is no location service; otherwise the enabled+permitted providers' last fixes.</summary>
    (int EnabledProviders, IReadOnlyList<LocationFix> Fixes)? LastKnownLocations();

    /// <summary>(uri, display name) of the image or video collection.</summary>
    IEnumerable<(string Uri, string? Name)> QueryMedia(string kind);

    IVettedUpload? OpenVettedUpload(string name, long maxBytes);

    long? TargetVolumeAvailableBytes();

    string? MimeFromExtension(string extension);

    /// <summary>MediaStore insert with IS_PENDING=1; the content uri, or null when refused.</summary>
    string? InsertPendingMedia(string kind, string displayName, string mime, string relativePath);

    /// <summary>Copies exactly <paramref name="bytes"/> from <paramref name="source"/>; false on a short read or any failure.</summary>
    bool WriteMedia(string uri, Stream source, long bytes);

    /// <summary>update IS_PENDING=0; the row count (0 on failure).</summary>
    int ReleasePendingMedia(string uri);

    void DeleteMedia(string uri);

    bool NotificationServiceAvailable { get; }

    void PostNotification(string title, string text, int id);

    ClipRead ReadClipboard();

    /// <summary>False when there is no clipboard service (upstream ignores that and answers ok).</summary>
    void WriteClipboard(string text);
}

public sealed class DirectBackend : IRelayBackend
{
    public const string KeyPackage = "package";
    public const string KeyFile = "file";
    public const string KeyLimit = "limit";
    public const string KeyIncludeOngoing = "includeOngoing";
    public const string KeyText = "text";
    public const string KeyTitle = "title";
    public const string KeyId = "id";
    public const string KeyKind = "kind";
    public const string KeyName = "name";
    public const string KeyNumber = "number";
    public const string KeyStartMs = "startMs";
    public const string KeyEndMs = "endMs";
    public const string KeySeconds = "seconds";
    public const int DefaultNoteLimit = 20;
    public const int DefaultLimit = 200;
    public const long DefaultEventMs = 3_600_000L;
    public const int ForegroundPollTries = 10;
    public const long ForegroundPollMs = 200;
    public const int ForegroundPollBlankLimit = 3;
    private const long MaxUploadBytes = 32L * 1024L * 1024L;
    private const long UploadHeadroomBytes = 8L * 1024L * 1024L;
    private const int HeaderBytes = 32;

    /// <summary>App-clone host containers whose windows hide the cloned package (confirmed ones only).</summary>
    public static readonly IReadOnlySet<string> CloneContainers = new HashSet<string>(StringComparer.Ordinal) { "com.vivo.doubleinstance" };

    private static readonly Dictionary<string, string> UploadMimes = new(StringComparer.Ordinal)
    {
        ["image"] = "image/png", ["video"] = "video/mp4", ["audio"] = "audio/m4a",
    };

    public sealed record ArgSpec(IReadOnlyList<string> Allowed, IReadOnlyList<string> Required);

    private static ArgSpec Spec(string[]? allowed = null, string[]? required = null) => new(allowed ?? [], required ?? []);

    /// <summary>Upstream <c>ARG_SPECS</c>: the keys each capability accepts and the ones that must not be blank.</summary>
    public static readonly IReadOnlyDictionary<string, ArgSpec> ArgSpecs = new Dictionary<string, ArgSpec>(StringComparer.Ordinal)
    {
        ["pkg.query"] = Spec(["keyword"]),
        ["app.launch"] = Spec([KeyPackage], [KeyPackage]),
        ["sys.intent"] = Spec(
            [
                IntentTemplates.KeyTemplate, IntentTemplates.KeyHour, IntentTemplates.KeyMinute, IntentTemplates.KeyLength,
                IntentTemplates.KeyMessage, IntentTemplates.KeyPage, IntentTemplates.KeyPackage, IntentTemplates.KeyNumber,
                IntentTemplates.KeyUrl,
            ],
            [IntentTemplates.KeyTemplate]),
        ["contact.read"] = Spec([KeyLimit]),
        ["contact.write"] = Spec([KeyName, KeyNumber], [KeyName]),
        ["cal.read"] = Spec([KeyLimit]),
        ["cal.write"] = Spec([KeyTitle, KeyStartMs, KeyEndMs], [KeyTitle]),
        ["loc.read"] = Spec(),
        ["media.read"] = Spec([KeyKind, KeyLimit]),
        ["media.write"] = Spec([KeyFile, KeyKind], [KeyFile]),
        ["notify.post"] = Spec([KeyTitle, KeyText, KeyId], [KeyTitle]),
        ["notify.read"] = Spec([KeyLimit, KeyPackage, KeyIncludeOngoing]),
        ["audio.capture"] = Spec([KeySeconds]),
        ["clip.read"] = Spec(),
        ["clip.write"] = Spec([KeyText], [KeyText]),
        ["screen.capture"] = Spec(),
        ["screen.observe"] = Spec(),
        ["screen.record"] = Spec([KeySeconds]),
    };

    private static readonly HashSet<string> AnySurface = new(StringComparer.Ordinal)
    {
        "pkg.query", "app.launch", "contact.read", "contact.write", "cal.read", "cal.write", "loc.read", "media.read",
        "media.write", "notify.post", "notify.read", "audio.capture", "sys.intent", "screen.capture", "screen.record",
    };

    private readonly IDirectPlatform _platform;
    private readonly NotificationWindow _notifications;
    private readonly AudioCapture _audio;
    private readonly ScreenProjection _projection;
    private readonly Func<bool> _clipboardUsable;
    private readonly Func<bool> _hostInFront;
    private readonly Func<long> _wallNowMs;
    private readonly Func<long, CancellationToken, Task> _delay;
    private readonly string _mediaAlbumDir;

    public DirectBackend(IDirectPlatform platform, NotificationWindow notifications, AudioCapture audio, ScreenProjection projection,
        Func<bool> clipboardUsable, Func<bool> hostInFront, Func<long> wallNowMs, Func<long, CancellationToken, Task> delay,
        string mediaAlbumDir)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _projection = projection ?? throw new ArgumentNullException(nameof(projection));
        _clipboardUsable = clipboardUsable ?? throw new ArgumentNullException(nameof(clipboardUsable));
        _hostInFront = hostInFront ?? throw new ArgumentNullException(nameof(hostInFront));
        _wallNowMs = wallNowMs ?? throw new ArgumentNullException(nameof(wallNowMs));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _mediaAlbumDir = mediaAlbumDir ?? throw new ArgumentNullException(nameof(mediaAlbumDir));
    }

    public bool Available() => true;

    /// <summary>
    /// The trusted display is never "supported" here: this backend only acts on the real screen, and claiming it
    /// would report background control as implemented while the action happens in front of the user.
    /// </summary>
    public bool Supports(string capability, SurfaceKind surface)
    {
        if (surface == SurfaceKind.BackendTrusted)
        {
            return false;
        }
        if (AnySurface.Contains(capability))
        {
            return true;
        }
        return capability switch
        {
            "clip.read" or "clip.write" => surface == SurfaceKind.Foreground,
            "screen.observe" => surface == SurfaceKind.ObserveOnly,
            _ => false,
        };
    }

    public async Task<BackendResult> ExecuteAsync(BackendCall backendCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backendCall);
        var call = backendCall;
        if (ArgsError(call.Capability, call.Args) is { } bad)
        {
            return new BackendResult.Failed(RelayError.TransportMalformed, bad);
        }
        var args = call.Args;
        return call.Capability switch
        {
            "pkg.query" => QueryLaunchableApps(args),
            "app.launch" => await LaunchAppAsync(OrgJson.OptString(args, KeyPackage), cancellationToken).ConfigureAwait(false),
            "contact.read" => ReadContacts(LimitOf(args)),
            "contact.write" => WriteContact(args),
            "cal.read" => ReadCalendar(LimitOf(args)),
            "cal.write" => WriteCalendar(args),
            "loc.read" => ReadLocation(),
            "media.read" => ReadMedia(args.TryGetProperty(KeyKind, out _) ? OrgJson.OptString(args, KeyKind) : "image", LimitOf(args)),
            "media.write" => WriteMedia(call),
            "notify.post" => PostNotification(args),
            "notify.read" => ReadNotifications(args),
            "audio.capture" => await _audio.CaptureAsync(call, cancellationToken).ConfigureAwait(false),
            "clip.read" => ReadClipboard(),
            "clip.write" => WriteClipboard(OrgJson.OptString(args, KeyText)),
            "sys.intent" => SemanticIntent(args),
            "screen.capture" or "screen.observe" => await _projection.CaptureFrameAsync(call, cancellationToken).ConfigureAwait(false),
            "screen.record" => await _projection.RecordAsync(call, cancellationToken).ConfigureAwait(false),
            _ => new BackendResult.Failed(RelayError.CapabilityNotImplemented, call.Capability),
        };
    }

    /// <summary>Upstream <c>argsError</c>: unknown keys first, then blank required ones.</summary>
    public static string? ArgsError(string capability, JsonElement args)
    {
        if (!ArgSpecs.TryGetValue(capability, out var spec))
        {
            return null;
        }
        foreach (var p in args.EnumerateObject())
        {
            if (!spec.Allowed.Contains(p.Name))
            {
                return ArgErrors.Unknown(p.Name, spec.Allowed);
            }
        }
        foreach (var key in spec.Required)
        {
            if (OrgJson.OptString(args, key).All(OrgJson.KotlinIsWhitespace))
            {
                return "missing arg: " + key;
            }
        }
        return null;
    }

    /// <summary>Reads are clamped to [1, 200]: an unbounded limit builds tens of MB of JSON in the host first.</summary>
    public static int LimitOf(JsonElement args) => Math.Clamp(OrgJson.OptInt(args, KeyLimit, DefaultLimit), 1, DefaultLimit);

    /// <summary>Upstream <c>appMatches</c>: case-insensitive substring of the package or the label; blank keyword matches all.</summary>
    public static bool AppMatches(string keyword, string pkg, string label)
    {
        if (keyword.All(OrgJson.KotlinIsWhitespace))
        {
            return true;
        }
        var needle = OrgJson.KotlinTrim(keyword).ToLowerInvariant();
        return pkg.ToLowerInvariant().Contains(needle, StringComparison.Ordinal) || label.ToLowerInvariant().Contains(needle, StringComparison.Ordinal);
    }

    public static string SettingsAction(string? page) => page switch
    {
        "wifi" => "android.settings.WIFI_SETTINGS",
        "bluetooth" => "android.settings.BLUETOOTH_SETTINGS",
        "display" => "android.settings.DISPLAY_SETTINGS",
        "location" => "android.settings.LOCATION_SOURCE_SETTINGS",
        "sound" => "android.settings.SOUND_SETTINGS",
        "apn" => "android.settings.APN_SETTINGS",
        "developer" => "android.settings.APPLICATION_DEVELOPMENT_SETTINGS",
        _ => "android.settings.NFC_SETTINGS",
    };

    public static string HandoffNote(string template) => template switch
    {
        IntentTemplates.AlarmSet or IntentTemplates.TimerSet =>
            "the clock app took the request without a screen (SKIP_UI); it does not report back, " +
            "so this says the hand-off happened, not that a specific alarm now exists",
        IntentTemplates.Dial => "the dialer opened with this number; nothing was called - ACTION_DIAL never places the call",
        _ => "the system page or app opened; this says the hand-off happened, not what the user did next",
    };

    private static BackendResult.Ok Ok(JsonObject data, string? reason = null) => new(data, [], 0, reason);

    /// <summary>
    /// Upstream <c>semanticIntent</c>. A background activity start is dropped silently since Android 10, so without
    /// the foreground or the overlay exemption the call answers E_GATE_NO_FOREGROUND instead of a false success.
    /// </summary>
    private BackendResult SemanticIntent(JsonElement args)
    {
        IntentTemplates.Params p;
        switch (IntentTemplates.Validate(args))
        {
            case IntentTemplates.Outcome.Bad bad:
                return new BackendResult.Failed(RelayError.TransportMalformed, bad.Reason);
            case IntentTemplates.Outcome.Ok ok:
                p = ok.Params;
                break;
            default:
                throw new InvalidOperationException();
        }
        string? resolved;
        string action;
        try
        {
            resolved = _platform.ResolveTemplate(p, out action);
        }
#pragma warning disable CA1031 // upstream runCatching: a resolver failure is "no receiver"
        catch (Exception)
#pragma warning restore CA1031
        {
            resolved = null;
            action = "";
        }
        if (resolved is null)
        {
            return new BackendResult.Failed(RelayError.BackendUnavailable,
                $"no app on this device handles {action}: a template that is in the table can still " +
                "have no receiver here, which is a property of this device, not of the argument");
        }
        var top = _platform.ForegroundPackage();
        if (top != _platform.AppPackage && !_platform.OverlayGranted())
        {
            return new BackendResult.Failed(RelayError.GateNoForeground,
                $"would be blocked as a background activity start (foreground is {top ?? "unknown"}): " +
                "grant the overlay permission, or send this while the assistant page is open");
        }
        if (_platform.StartTemplate(p) is { } failure)
        {
            return new BackendResult.Failed(RelayError.BackendUnavailable, $"startActivity refused for {resolved}: {failure}");
        }
        var data = new JsonObject { ["template"] = p.Template, ["resolvedPackage"] = resolved };
        if (p.Hour >= 0)
        {
            data["skipUi"] = true;
            data["hour"] = p.Hour;
            data["minute"] = p.Minute;
        }
        if (p.LengthSeconds >= 0)
        {
            data["skipUi"] = true;
            data["lengthSeconds"] = p.LengthSeconds;
        }
        if (p.Page is not null)
        {
            data["page"] = p.Page;
        }
        if (p.PackageName is not null)
        {
            data["package"] = p.PackageName;
        }
        if (p.Number is not null)
        {
            data["numberLength"] = p.Number.Length;
        }
        if (p.Url is not null)
        {
            data["url"] = p.Url;
        }
        if (p.Message is not null)
        {
            data["message"] = p.Message;
        }
        data["note"] = HandoffNote(p.Template);
        return Ok(data);
    }

    /// <summary>The listener's in-memory window; "not bound" when the setting is on but the service is not connected yet.</summary>
    private BackendResult ReadNotifications(JsonElement args)
    {
        if (!_notifications.IsConnected)
        {
            return new BackendResult.Failed(RelayError.BackendUnavailable, "notification listener not bound");
        }
        var limit = Math.Clamp(OrgJson.OptInt(args, KeyLimit, DefaultNoteLimit), 1, DefaultLimit);
        var filterRaw = OrgJson.OptString(args, KeyPackage);
        var filter = filterRaw.All(OrgJson.KotlinIsWhitespace) ? null : filterRaw;
        var entries = _notifications.Snapshot(limit, filter, OrgJson.OptBoolean(args, KeyIncludeOngoing));
        var items = new JsonArray();
        foreach (var e in entries)
        {
            items.Add(new JsonObject { ["package"] = e.PackageName, ["title"] = e.Title, ["text"] = e.Text, ["postedAtMs"] = e.PostedAtMs });
        }
        return Ok(new JsonObject { ["count"] = entries.Count, ["notifications"] = items });
    }

    private BackendResult QueryLaunchableApps(JsonElement args)
    {
        var keyword = OrgJson.KotlinTrim(OrgJson.OptString(args, "keyword"));
        var resolved = _platform.LaunchableApps();
        var items = new JsonArray();
        var matched = 0;
        foreach (var (pkg, label) in resolved)
        {
            if (AppMatches(keyword, pkg, label))
            {
                matched++;
                items.Add(new JsonObject { ["package"] = pkg, ["name"] = label });
            }
        }
        var data = new JsonObject { ["count"] = matched, ["apps"] = items };
        if (keyword.Length > 0)
        {
            data["keyword"] = keyword;
            data["total"] = resolved.Count;
        }
        return Ok(data);
    }

    /// <summary>
    /// Upstream <c>launchApp</c>: start, then verify who holds the front. "No exception" is not "launched" (a cold
    /// start lands a second later), and "not verified" never passes for "verified".
    /// </summary>
    private async Task<BackendResult> LaunchAppAsync(string packageName, CancellationToken ct)
    {
        var app = _platform.AppPackage;
        if (packageName == app && _hostInFront())
        {
            var seen = _platform.ForegroundPackage();
            var self = new JsonObject { [KeyPackage] = packageName, ["verified"] = seen == app, ["alreadyInFront"] = true };
            if (seen is not null)
            {
                self["observed"] = seen;
            }
            return Ok(self, "this app was already in the foreground; nothing was started" +
                (seen == app ? "" : " (no foreground observation: verified is false)"));
        }
        switch (_platform.StartLaunch(packageName, out var thrown))
        {
            case LaunchStart.NotLaunchable:
                return new BackendResult.Failed(RelayError.TransportMalformed,
                    $"not launchable: {packageName} (expected a package name from pkg.query, not a display name)");
            case LaunchStart.Threw:
                return new BackendResult.Failed(RelayError.InternalError, $"startActivity threw {thrown ?? "null"}");
        }
        var observed = await AwaitForegroundAsync(packageName, ct).ConfigureAwait(false);
        var inClone = observed is not null && CloneContainers.Contains(observed);
        var data = new JsonObject { [KeyPackage] = packageName };
        if (observed == packageName)
        {
            data["verified"] = true;
            return Ok(data);
        }
        if (observed is not null && !inClone)
        {
            return new BackendResult.Failed(RelayError.LaunchNotLanded,
                $"the launch was accepted but another app is confirmed to hold the front: asked for {packageName}, " +
                $"foreground is {observed}. This is not a cold start still running (that case answers verified:false); " +
                "read ui.snapshot to see what is up, or ask the user to leave that screen, before sending this again");
        }
        if (observed is not null)
        {
            data["observed"] = observed;
        }
        data["verified"] = false;
        var reason = inClone
            ? $"foreground is clone container {observed}, cloned app not identifiable; a system chooser may be waiting " +
              "for an icon pick. Read ui.snapshot first - this ROM does expose that box's tree - and use screen.capture " +
              "to tell the original icon from the clone, then answer with ui.tap (no display argument: the surface is " +
              "the user's choice) and launch again"
            : !_platform.AccessibilityConnected
                ? "no observation: accessibility service not connected"
                : "no foreign window seen yet: still launching or blocked by this app";
        return Ok(data, reason);
    }

    /// <summary>Upstream <c>awaitForeground</c>: up to 10 samples 200 ms apart, giving up after 3 blank ones.</summary>
    internal async Task<string?> AwaitForegroundAsync(string target, CancellationToken ct)
    {
        string? observed = null;
        var samples = 0;
        var blank = 0;
        while (samples < ForegroundPollTries && blank < ForegroundPollBlankLimit)
        {
            samples++;
            var seen = _platform.ForegroundPackage();
            if (seen is null)
            {
                blank++;
            }
            else if (seen == target)
            {
                return seen;
            }
            else if (CloneContainers.Contains(seen))
            {
                observed = seen;
            }
            else if (seen != _platform.AppPackage)
            {
                observed = seen;
            }
            if (samples < ForegroundPollTries && blank < ForegroundPollBlankLimit)
            {
                await _delay(ForegroundPollMs, ct).ConfigureAwait(false);
            }
        }
        return observed;
    }

    private BackendResult ReadContacts(int limit)
    {
        var items = new JsonArray();
        foreach (var (name, number) in _platform.QueryPhones())
        {
            if (items.Count >= limit)
            {
                break;
            }
            var o = new JsonObject();
            PutString(o, "name", name);
            PutString(o, "number", number);
            items.Add(o);
        }
        return Ok(new JsonObject { ["count"] = items.Count, ["contacts"] = items });
    }

    /// <summary>org.json <c>put(name, null)</c> removes the key: a null column is simply absent.</summary>
    private static void PutString(JsonObject o, string key, string? value)
    {
        if (value is not null)
        {
            o[key] = value;
        }
    }

    private BackendResult WriteContact(JsonElement args)
    {
        var name = OrgJson.OptString(args, KeyName);
        var number = OrgJson.OptString(args, KeyNumber);
        try
        {
            var ops = _platform.InsertContact(name, number.All(OrgJson.KotlinIsWhitespace) ? null : number);
            return Ok(new JsonObject { ["operations"] = ops });
        }
#pragma warning disable CA1031 // the provider refusing is a host fault, reported by its class name
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new BackendResult.Failed(RelayError.InternalError, $"contact write rejected: {SimpleName(ex)}");
        }
    }

    private BackendResult ReadCalendar(int limit)
    {
        var items = new JsonArray();
        foreach (var (calendarId, title, start, end) in _platform.QueryEvents())
        {
            if (items.Count >= limit)
            {
                break;
            }
            var o = new JsonObject { ["calendarId"] = calendarId };
            PutString(o, "title", title);
            o["startMs"] = start;
            o["endMs"] = end;
            items.Add(o);
        }
        return Ok(new JsonObject { ["count"] = items.Count, ["events"] = items });
    }

    private BackendResult WriteCalendar(JsonElement args)
    {
        var title = OrgJson.OptString(args, KeyTitle);
        if (_platform.FirstCalendarId() is not { } calendarId)
        {
            return new BackendResult.Failed(RelayError.GateSystemMissing, "no calendar account");
        }
        var start = OrgJson.OptLong(args, KeyStartMs, _wallNowMs());
        try
        {
            var id = _platform.InsertEvent(calendarId, title, start, OrgJson.OptLong(args, KeyEndMs, start + DefaultEventMs));
            return Ok(new JsonObject { ["eventId"] = id });
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new BackendResult.Failed(RelayError.InternalError, $"calendar write rejected: {SimpleName(ex)}");
        }
    }

    private BackendResult ReadLocation()
    {
        if (_platform.LastKnownLocations() is not { } read)
        {
            return new BackendResult.Failed(RelayError.CapabilityUnavailableOnDevice, "no location service");
        }
        var latest = read.Fixes.Count == 0 ? null : read.Fixes.MaxBy(f => f.TimeMs);
        if (latest is null)
        {
            return new BackendResult.Failed(RelayError.BackendUnavailable, $"no location fix yet (enabled providers: {read.EnabledProviders})");
        }
        var data = new JsonObject
        {
            ["latitude"] = latest.Latitude,
            ["longitude"] = latest.Longitude,
            ["accuracyMeters"] = A11y.NodeReader.JavaFloat(latest.AccuracyMeters),
            ["fixAgeMs"] = _wallNowMs() - latest.TimeMs,
        };
        PutString(data, "provider", latest.Provider);
        return Ok(data);
    }

    /// <summary>
    /// Upstream <c>writeMedia</c>: MediaStore, never a path; the target directory and MIME are fixed by kind; the entry
    /// is pending until fully written and removed when anything fails, so no zero-byte or half rows remain.
    /// </summary>
    private BackendResult WriteMedia(BackendCall call)
    {
        var name = OrgJson.OptString(call.Args, KeyFile);
        if (_platform.OpenVettedUpload(name, MaxUploadBytes) is not { } upload)
        {
            return new BackendResult.Failed(RelayError.TransportMalformed, $"no such upload: {name}");
        }
        using (upload)
        {
            var bytes = upload.Size;
            var kind = (call.Args.TryGetProperty(KeyKind, out _) ? OrgJson.OptString(call.Args, KeyKind) : "image").ToLowerInvariant();
            if (kind is not ("video" or "audio" or "image"))
            {
                upload.DeleteSource();
                return new BackendResult.Failed(RelayError.TransportMalformed, "kind must be image/video/audio");
            }
            var dot = name.LastIndexOf('.');
            var extension = (dot < 0 ? "" : name[(dot + 1)..]).ToLowerInvariant();
            var guessed = _platform.MimeFromExtension(extension);
            var mime = guessed is not null && guessed.StartsWith(kind + "/", StringComparison.Ordinal) ? guessed : UploadMimes[kind];
            if (_platform.TargetVolumeAvailableBytes() is { } available && available < bytes + UploadHeadroomBytes)
            {
                upload.DeleteSource();
                return new BackendResult.Failed(RelayError.StorageFull, "target volume has no room");
            }
            if (!HeaderMatchesKind(upload, kind))
            {
                upload.DeleteSource();
                return new BackendResult.Failed(RelayError.TransportMalformed, $"content does not match kind={kind}");
            }
            string? target;
            try
            {
                target = _platform.InsertPendingMedia(kind, name, mime, UploadDir(kind));
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                target = null;
            }
            if (target is null)
            {
                upload.DeleteSource();
                return new BackendResult.Failed(RelayError.InternalError, "media store insert refused");
            }
            bool written;
            try
            {
                using var source = upload.OpenRead();
                written = _platform.WriteMedia(target, source, bytes);
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                written = false;
            }
            var released = 0;
            if (written)
            {
                try
                {
                    released = _platform.ReleasePendingMedia(target);
                }
#pragma warning disable CA1031
                catch (Exception)
#pragma warning restore CA1031
                {
                    released = 0;
                }
            }
            if (released != 1)
            {
                try
                {
                    _platform.DeleteMedia(target);
                }
#pragma warning disable CA1031
                catch (Exception)
#pragma warning restore CA1031
                {
                    // the pending row is invisible to media apps either way
                }
                upload.DeleteSource();
                return new BackendResult.Failed(RelayError.InternalError, "media publish failed");
            }
            upload.DeleteSource();
            return Ok(new JsonObject { ["uri"] = target, ["bytes"] = bytes });
        }
    }

    public string UploadDir(string kind) => kind switch
    {
        "video" => $"Movies/{_mediaAlbumDir}/",
        "audio" => $"Music/{_mediaAlbumDir}/",
        _ => $"Pictures/{_mediaAlbumDir}/",
    };

    /// <summary>Upstream <c>headerMatchesKind</c> over the first 32 bytes (a misuse guard, not an adversary boundary).</summary>
    public static bool HeaderMatchesKind(IVettedUpload upload, string kind)
    {
        ArgumentNullException.ThrowIfNull(upload);
        Span<byte> header = stackalloc byte[HeaderBytes];
        var filled = 0;
        try
        {
            while (filled < HeaderBytes)
            {
                var read = upload.ReadAt(filled, header[filled..]);
                if (read <= 0)
                {
                    break;
                }
                filled += read;
            }
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
        return HeaderMatchesKind(header[..filled], kind);
    }

    public static bool HeaderMatchesKind(ReadOnlySpan<byte> header, string kind)
    {
        if (header.Length < 4)
        {
            return false;
        }
        var value = Convert.ToHexStringLower(header);
        var majorBrand = value.Length >= 24 && value.Substring(8, 8) == "66747970" ? value.Substring(16, 8) : null;
        var riffForm = value.Length >= 32 && value.StartsWith("52494646", StringComparison.Ordinal) ? value.Substring(24, 8) : null;
        bool Starts(string prefix) => value.StartsWith(prefix, StringComparison.Ordinal);
        return kind switch
        {
            "image" => Starts("89504e47") || Starts("ffd8ff") || Starts("47494638") || Starts("424d") || Starts("49492a00") ||
                Starts("4d4d002a") || riffForm == "57454250" ||
                (majorBrand is not null && (majorBrand.StartsWith("6865", StringComparison.Ordinal) || majorBrand == "6d696631")),
            "audio" => majorBrand is not null || Starts("494433") || riffForm == "57415645" || Starts("4f676753") ||
                (value.Length >= 4 && Starts("ff") && (Convert.ToInt32(value.Substring(2, 2), 16) & 0xE0) == 0xE0),
            "video" => majorBrand is not null || riffForm == "41564920" || Starts("1a45dfa3"),
            _ => false,
        };
    }

    private BackendResult ReadMedia(string kind, int limit)
    {
        var k = kind.ToLowerInvariant();
        if (k is not ("video" or "image"))
        {
            // An unknown kind must not fall back to photos: a plausible wrong answer is worse than a failure.
            return new BackendResult.Failed(RelayError.TransportMalformed, "kind must be image/video");
        }
        var items = new JsonArray();
        foreach (var (uri, name) in _platform.QueryMedia(k))
        {
            if (items.Count >= limit)
            {
                break;
            }
            var o = new JsonObject { ["uri"] = uri };
            PutString(o, "name", name);
            items.Add(o);
        }
        return Ok(new JsonObject { ["count"] = items.Count, ["items"] = items });
    }

    private BackendResult PostNotification(JsonElement args)
    {
        if (!_platform.NotificationServiceAvailable)
        {
            return new BackendResult.Failed(RelayError.CapabilityUnavailableOnDevice, "no notification service");
        }
        _platform.PostNotification(OrgJson.OptString(args, KeyTitle), OrgJson.OptString(args, KeyText),
            OrgJson.OptInt(args, KeyId, unchecked((int)_wallNowMs())));
        return Ok(new JsonObject { ["posted"] = true });
    }

    /// <summary>"Empty clipboard" (no clip) and "masked without focus" (a clip whose text cannot be read) are different answers.</summary>
    private BackendResult ReadClipboard()
    {
        if (!_clipboardUsable())
        {
            return new BackendResult.Failed(RelayError.ClipboardNoFocus, "clipboard needs app focus");
        }
        switch (_platform.ReadClipboard())
        {
            case ClipRead.NoService:
                return new BackendResult.Failed(RelayError.CapabilityUnavailableOnDevice, "no clipboard service");
            case ClipRead.NoClip:
                return Ok(new JsonObject { ["text"] = "", ["length"] = 0 });
            case ClipRead.Clip { FirstItemText: { Length: > 0 } text }:
                return Ok(new JsonObject { ["text"] = text, ["length"] = text.Length });
            default:
                return new BackendResult.Failed(RelayError.ClipboardNoFocus, "clip content masked without focus");
        }
    }

    private BackendResult WriteClipboard(string text)
    {
        if (!_clipboardUsable())
        {
            return new BackendResult.Failed(RelayError.ClipboardNoFocus, "clipboard needs app focus");
        }
        _platform.WriteClipboard(text);
        return Ok(new JsonObject { ["written"] = text.Length });
    }

    /// <summary>Java <c>Class.getSimpleName</c> of a platform exception (Java exceptions carry their own name).</summary>
    public static string SimpleName(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.Data["JavaSimpleName"] as string ?? ex.GetType().Name;
    }
}
