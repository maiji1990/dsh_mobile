// Android half of the Direct backend port. Translated from DSHBox pilot/interlock-relay-core (GPL-3.0,
// v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/exec/direct/DirectBackend.kt (semanticIntent's Intent table and
//     settingsAction, overlayGranted, queryLaunchableApps, launchApp's startActivity, foregroundPackage,
//     readContacts, writeContact, readCalendar, writeCalendar, firstCalendarId, readLocation, writeMedia's
//     MediaStore calls, readMedia, postNotification, readClipboard, writeClipboard)
// Registered in docs/UPSTREAMS.md. Every decision (argument rules, refusal codes, response shapes) is in
// DshMobile.Runtime (DirectBackend.cs); each member here is one platform call.

using Android;
using Android.Content;
using Android.Content.PM;
using Android.Locations;
using Android.OS;
using Android.Provider;
using Android.Webkit;
using DshMobile.Runtime.Automation.Relay.Exec.Direct;
using DshMobile.Runtime.Automation.Relay.Storage;
using Uri = Android.Net.Uri;

namespace DshMobile.App.Automation.Relay;

internal sealed class AndroidDirectPlatform(Context context, RelayPaths paths) : IDirectPlatform
{
    /// <summary>Upstream <c>RelaySurfaces.DEFAULT_NOTIFY_POST_ID</c>; the same app-wide channel as the legacy Relay.</summary>
    public const string NotifyChannelId = "relay_notify";
    private const string ClipLabel = "relay";
    private const string IdColumn = "_id"; // MediaStore.MediaColumns._ID (BaseColumns._ID)

    private readonly Context _app = context.ApplicationContext ?? context;

    private ContentResolver Resolver => _app.ContentResolver!;

    public string AppPackage => _app.PackageName!;

    public IReadOnlyList<(string Package, string Label)> LaunchableApps()
    {
        var pm = _app.PackageManager!;
        var intent = new Intent(Intent.ActionMain).AddCategory(Intent.CategoryLauncher);
#pragma warning disable CA1422 // the int-flags overload is what upstream calls on every level
        var resolved = pm.QueryIntentActivities(intent, 0);
#pragma warning restore CA1422
        return [.. resolved.Select(info => (info.ActivityInfo!.PackageName!, info.LoadLabel(pm)))];
    }

    public LaunchStart StartLaunch(string packageName, out string? thrown)
    {
        thrown = null;
        if (_app.PackageManager!.GetLaunchIntentForPackage(packageName) is not { } intent)
        {
            return LaunchStart.NotLaunchable;
        }
        try
        {
            intent.AddFlags(ActivityFlags.NewTask);
            _app.StartActivity(intent);
            return LaunchStart.Started;
        }
        catch (Java.Lang.Throwable t)
        {
            thrown = t.Class.SimpleName;
            return LaunchStart.Threw;
        }
    }

    /// <summary>Upstream <c>foregroundPackage</c>: the focused window's root, else the active window's root.</summary>
    public string? ForegroundPackage()
    {
        if (RelayA11yTreeSource.Current() is not { } service)
        {
            return null;
        }
        try
        {
            var focused = (service.Windows ?? []).FirstOrDefault(w => w.IsFocused);
            return focused?.Root?.PackageName ?? service.RootInActiveWindow?.PackageName;
        }
        catch (Java.Lang.Throwable)
        {
            return null;
        }
    }

    public bool AccessibilityConnected => RelayA11yTreeSource.Current() is not null;

    public bool OverlayGranted()
    {
        try
        {
            return Settings.CanDrawOverlays(_app);
        }
        catch (Java.Lang.Throwable)
        {
            return false;
        }
    }

    private static Intent IntentOf(IntentTemplates.Params p)
    {
        Intent intent;
        switch (p.Template)
        {
            case IntentTemplates.AlarmSet:
                intent = new Intent(AlarmClock.ActionSetAlarm).PutExtra(AlarmClock.ExtraHour, p.Hour).PutExtra(AlarmClock.ExtraMinutes, p.Minute);
                if (p.Message is { } alarmMessage)
                {
                    intent.PutExtra(AlarmClock.ExtraMessage, alarmMessage);
                }
                intent.PutExtra(AlarmClock.ExtraSkipUi, true);
                break;
            case IntentTemplates.TimerSet:
                intent = new Intent(AlarmClock.ActionSetTimer).PutExtra(AlarmClock.ExtraLength, p.LengthSeconds);
                if (p.Message is { } timerMessage)
                {
                    intent.PutExtra(AlarmClock.ExtraMessage, timerMessage);
                }
                intent.PutExtra(AlarmClock.ExtraSkipUi, true);
                break;
            case IntentTemplates.AlarmShow:
                intent = new Intent(AlarmClock.ActionShowAlarms);
                break;
            case IntentTemplates.TimerShow:
                intent = new Intent(AlarmClock.ActionShowTimers);
                break;
            case IntentTemplates.AppInfo:
                intent = new Intent(Settings.ActionApplicationDetailsSettings, Uri.FromParts("package", p.PackageName, null));
                break;
            case IntentTemplates.Dial:
                intent = new Intent(Intent.ActionDial, Uri.FromParts("tel", p.Number, null));
                break;
            case IntentTemplates.WebOpen:
                intent = new Intent(Intent.ActionView, Uri.Parse(p.Url));
                break;
            default:
                intent = new Intent(SettingsAction(p.Page));
                break;
        }
        return intent.AddFlags(ActivityFlags.NewTask);
    }

    /// <summary>Upstream <c>settingsAction</c>: public constants, no permission, only brings the page in front.</summary>
    private static string SettingsAction(string? page) => page switch
    {
        "wifi" => Settings.ActionWifiSettings,
        "bluetooth" => Settings.ActionBluetoothSettings,
        "display" => Settings.ActionDisplaySettings,
        "location" => Settings.ActionLocationSourceSettings,
        "sound" => Settings.ActionSoundSettings,
        "apn" => Settings.ActionApnSettings,
        "developer" => Settings.ActionApplicationDevelopmentSettings,
        _ => Settings.ActionNfcSettings,
    };

    public string? ResolveTemplate(IntentTemplates.Params template, out string action)
    {
        var intent = IntentOf(template);
        action = intent.Action ?? "";
#pragma warning disable CA1422 // upstream calls the int-flags overload on every level
        return _app.PackageManager!.ResolveActivity(intent, PackageInfoFlags.MatchDefaultOnly)?.ActivityInfo?.PackageName;
#pragma warning restore CA1422
    }

    public string? StartTemplate(IntentTemplates.Params template)
    {
        try
        {
            _app.StartActivity(IntentOf(template));
            return null;
        }
        catch (Java.Lang.Throwable t)
        {
            var message = t.Message is { } m ? (m.Length > 160 ? m[..160] : m).Replace('\n', ' ') : "null";
            return t.Class.SimpleName + ":" + message;
        }
    }

    public IEnumerable<(string? Name, string? Number)> QueryPhones()
    {
        using var cursor = Resolver.Query(ContactsContract.CommonDataKinds.Phone.ContentUri!,
            [ContactsContract.CommonDataKinds.Phone.InterfaceConsts.DisplayName, ContactsContract.CommonDataKinds.Phone.Number], null, null, null);
        var rows = new List<(string?, string?)>();
        while (cursor is not null && cursor.MoveToNext())
        {
            rows.Add((cursor.GetString(0), cursor.GetString(1)));
        }
        return rows;
    }

    public int InsertContact(string name, string? number)
    {
        var ops = new List<ContentProviderOperation>
        {
            ContentProviderOperation.NewInsert(ContactsContract.RawContacts.ContentUri!)!
                .WithValue(ContactsContract.RawContacts.InterfaceConsts.AccountType, null)!
                .WithValue(ContactsContract.RawContacts.InterfaceConsts.AccountName, null)!
                .Build()!,
            ContentProviderOperation.NewInsert(ContactsContract.Data.ContentUri!)!
                .WithValueBackReference(ContactsContract.Data.InterfaceConsts.RawContactId, 0)!
                .WithValue(ContactsContract.Data.InterfaceConsts.Mimetype, ContactsContract.CommonDataKinds.StructuredName.ContentItemType)!
                .WithValue(ContactsContract.CommonDataKinds.StructuredName.DisplayName, name)!
                .Build()!,
        };
        if (!string.IsNullOrWhiteSpace(number))
        {
            ops.Add(ContentProviderOperation.NewInsert(ContactsContract.Data.ContentUri!)!
                .WithValueBackReference(ContactsContract.Data.InterfaceConsts.RawContactId, 0)!
                .WithValue(ContactsContract.Data.InterfaceConsts.Mimetype, ContactsContract.CommonDataKinds.Phone.ContentItemType)!
                .WithValue(ContactsContract.CommonDataKinds.Phone.Number, number)!
                .WithValue(ContactsContract.CommonDataKinds.Phone.InterfaceConsts.Type, (int)PhoneDataKind.Mobile)!
                .Build()!);
        }
        return Resolver.ApplyBatch(ContactsContract.Authority, ops)!.Length;
    }

    public IEnumerable<(long CalendarId, string? Title, long StartMs, long EndMs)> QueryEvents()
    {
        using var cursor = Resolver.Query(CalendarContract.Events.ContentUri!,
            [CalendarContract.EventsColumns.CalendarId, CalendarContract.EventsColumns.Title, CalendarContract.EventsColumns.Dtstart,
                CalendarContract.EventsColumns.Dtend], null, null, CalendarContract.EventsColumns.Dtstart + " ASC");
        var rows = new List<(long, string?, long, long)>();
        while (cursor is not null && cursor.MoveToNext())
        {
            rows.Add((cursor.GetLong(0), cursor.GetString(1), cursor.GetLong(2), cursor.GetLong(3)));
        }
        return rows;
    }

    public long? FirstCalendarId()
    {
        using var cursor = Resolver.Query(CalendarContract.Calendars.ContentUri!, [CalendarContract.Calendars.InterfaceConsts.Id], null, null, null);
        return cursor is not null && cursor.MoveToFirst() ? cursor.GetLong(0) : null;
    }

    public long InsertEvent(long calendarId, string title, long startMs, long endMs)
    {
        using var values = new ContentValues();
        values.Put(CalendarContract.EventsColumns.CalendarId, calendarId);
        values.Put(CalendarContract.EventsColumns.Title, title);
        values.Put(CalendarContract.EventsColumns.Dtstart, startMs);
        values.Put(CalendarContract.EventsColumns.Dtend, endMs);
        values.Put(CalendarContract.EventsColumns.EventTimezone, Java.Util.TimeZone.Default!.ID);
        var uri = Resolver.Insert(CalendarContract.Events.ContentUri!, values);
        return uri is null ? -1L : ContentUris.ParseId(uri);
    }

    public (int EnabledProviders, IReadOnlyList<LocationFix> Fixes)? LastKnownLocations()
    {
        if (_app.GetSystemService(Context.LocationService) is not LocationManager manager)
        {
            return null;
        }
        var finePermitted = _app.CheckPermission(Manifest.Permission.AccessFineLocation, Process.MyPid(), Process.MyUid()) == Permission.Granted;
        var providers = manager.AllProviders.Where(p => manager.IsProviderEnabled(p) && finePermitted).ToList();
        var fixes = new List<LocationFix>();
        foreach (var provider in providers)
        {
            if (manager.GetLastKnownLocation(provider) is { } l)
            {
                fixes.Add(new LocationFix(l.Latitude, l.Longitude, l.Accuracy, l.Time, l.Provider));
            }
        }
        return (providers.Count, fixes);
    }

    private static Uri? Collection(string kind) => kind switch
    {
        "video" => MediaStore.Video.Media.ExternalContentUri,
        "audio" => MediaStore.Audio.Media.ExternalContentUri,
        "image" => MediaStore.Images.Media.ExternalContentUri,
        _ => null,
    };

    public IEnumerable<(string Uri, string? Name)> QueryMedia(string kind)
    {
        var collection = Collection(kind) ?? throw new ArgumentOutOfRangeException(nameof(kind));
        using var cursor = Resolver.Query(collection,
            [IdColumn, MediaStore.IMediaColumns.DisplayName, MediaStore.IMediaColumns.DateModified], null, null, null);
        var rows = new List<(string, string?)>();
        if (cursor is null)
        {
            return rows;
        }
        var idColumn = cursor.GetColumnIndexOrThrow(IdColumn);
        var nameColumn = cursor.GetColumnIndexOrThrow(MediaStore.IMediaColumns.DisplayName);
        while (cursor.MoveToNext())
        {
            rows.Add((Uri.WithAppendedPath(collection, cursor.GetString(idColumn))!.ToString()!, cursor.GetString(nameColumn)));
        }
        return rows;
    }

    public IVettedUpload? OpenVettedUpload(string name, long maxBytes) => AndroidVettedUpload.Open(paths, name, maxBytes);

    public long? TargetVolumeAvailableBytes()
    {
        try
        {
            return _app.GetExternalFilesDir(null)?.Path is { } path ? new StatFs(path).AvailableBytes : null;
        }
        catch (Java.Lang.Throwable)
        {
            return null;
        }
    }

    public string? MimeFromExtension(string extension) => MimeTypeMap.Singleton?.GetMimeTypeFromExtension(extension);

    public string? InsertPendingMedia(string kind, string displayName, string mime, string relativePath)
    {
        if (Collection(kind) is not { } collection)
        {
            return null;
        }
        using var values = new ContentValues();
        values.Put(MediaStore.IMediaColumns.DisplayName, displayName);
        values.Put(MediaStore.IMediaColumns.MimeType, mime);
        values.Put(MediaStore.IMediaColumns.RelativePath, relativePath);
        values.Put(MediaStore.IMediaColumns.IsPending, 1);
        try
        {
            return Resolver.Insert(collection, values)?.ToString();
        }
        catch (Java.Lang.Throwable)
        {
            return null;
        }
    }

    public bool WriteMedia(string uri, Stream source, long bytes)
    {
        try
        {
            using var output = Resolver.OpenOutputStream(Uri.Parse(uri)!, "w");
            if (output is null)
            {
                return false;
            }
            var buffer = new byte[64 * 1024];
            var left = bytes;
            while (left > 0)
            {
                var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (read <= 0)
                {
                    break;
                }
                output.Write(buffer, 0, read);
                left -= read;
            }
            return left == 0;
        }
        catch (Exception e) when (e is Java.Lang.Throwable or IOException)
        {
            return false;
        }
    }

    public int ReleasePendingMedia(string uri)
    {
        using var values = new ContentValues();
        values.Put(MediaStore.IMediaColumns.IsPending, 0);
        try
        {
            return Resolver.Update(Uri.Parse(uri)!, values, null, null);
        }
        catch (Java.Lang.Throwable)
        {
            return 0;
        }
    }

    public void DeleteMedia(string uri)
    {
        try
        {
            Resolver.Delete(Uri.Parse(uri)!, null, null);
        }
        catch (Java.Lang.Throwable)
        {
        }
    }

    private NotificationManager? Notifications => _app.GetSystemService(Context.NotificationService) as NotificationManager;

    public bool NotificationServiceAvailable => Notifications is not null;

    /// <summary>Title and text are the agent's data, not this app's UI text.</summary>
    public void PostNotification(string title, string text, int id)
    {
        var manager = Notifications!;
        if (manager.GetNotificationChannel(NotifyChannelId) is null)
        {
            manager.CreateNotificationChannel(new NotificationChannel(NotifyChannelId,
                _app.GetString(Resource.String.relay_cs_notify_channel), NotificationImportance.Default));
        }
        var notification = new Notification.Builder(_app, NotifyChannelId)
            .SetSmallIcon(Android.Resource.Drawable.StatNotifyChat)
            .SetContentTitle(title)
            .SetContentText(text)
            .SetAutoCancel(true)
            .Build();
        manager.Notify(id, notification);
    }

    public ClipRead ReadClipboard()
    {
        if (_app.GetSystemService(Context.ClipboardService) is not ClipboardManager manager)
        {
            return new ClipRead.NoService();
        }
        if (manager.PrimaryClip is not { } clip)
        {
            return new ClipRead.NoClip();
        }
        return new ClipRead.Clip(clip.ItemCount > 0 ? clip.GetItemAt(0)?.CoerceToText(_app) : null);
    }

    public void WriteClipboard(string text) =>
        (_app.GetSystemService(Context.ClipboardService) as ClipboardManager)?.PrimaryClip = ClipData.NewPlainText(ClipLabel, text);
}
