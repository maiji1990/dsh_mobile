// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/exec/direct/RelayNotificationListener.kt (the service half; the window is
//     DshMobile.Runtime NotificationWindow.cs)
//   src/main/java/interlock/relay/core/exec/direct/RelayService.kt (the channel's persistent foreground service)
// Registered in docs/UPSTREAMS.md. Both are declared in AndroidManifest.xml (hence [Register] with fixed names).

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Service.Notification;
using DshMobile.Runtime.Automation.Relay.Exec.Direct;

namespace DshMobile.App.Automation.Relay;

/// <summary>
/// Feeds <see cref="CsRelay.NotificationFeed"/> (in memory only; bodies never reach disk). The user grants notification
/// access in system Settings; until then notify.read is refused by the gate as a missing system grant.
/// </summary>
[Register("dshmobile.relay.DshNotificationListener")]
public sealed class DshNotificationListener : NotificationListenerService
{
    public override void OnListenerConnected()
    {
        base.OnListenerConnected();
        List<NotificationEntry> active;
        try
        {
            active = [.. (GetActiveNotifications() ?? []).Select(EntryOf)];
        }
        catch (Java.Lang.Throwable)
        {
            active = [];
        }
        CsRelay.NotificationFeed.Attach(this, active);
    }

    public override void OnListenerDisconnected()
    {
        CsRelay.NotificationFeed.Detach(this);
        base.OnListenerDisconnected();
    }

    public override void OnNotificationPosted(StatusBarNotification? sbn)
    {
        if (sbn is not null)
        {
            CsRelay.NotificationFeed.Record(EntryOf(sbn));
        }
    }

    public override void OnNotificationRemoved(StatusBarNotification? sbn)
    {
        if (sbn is not null)
        {
            CsRelay.NotificationFeed.Retire(NotificationEntry.KeyOf(sbn.PackageName, sbn.Id, sbn.Tag));
        }
    }

    /// <summary>Upstream <c>Entry.of</c>.</summary>
    private static NotificationEntry EntryOf(StatusBarNotification sbn)
    {
        var notification = sbn.Notification!;
        var extras = notification.Extras;
        return new NotificationEntry(
            NotificationEntry.KeyOf(sbn.PackageName, sbn.Id, sbn.Tag),
            sbn.PackageName ?? "",
            extras?.GetCharSequence(Notification.ExtraTitle) ?? "",
            extras?.GetCharSequence(Notification.ExtraText) ?? "",
            sbn.PostTime,
            (notification.Flags & NotificationFlags.OngoingEvent) != 0);
    }
}

/// <summary>
/// Upstream <c>RelayService</c>: the C# channel's persistent notification while the host takes work (vendor background
/// cleaners would otherwise sweep the process and unbind the accessibility service with it). Not exported.
/// </summary>
[Register("dshmobile.relay.RelayCsChannelService")]
public sealed class RelayCsChannelService : Service
{
    private const string ChannelId = "relay_cs_channel";
    private const int NotificationId = 0x5051;

    public static void Start(Context context)
    {
        var manager = (NotificationManager)context.GetSystemService(NotificationService)!;
        if (manager.GetNotificationChannel(ChannelId) is null)
        {
            manager.CreateNotificationChannel(new NotificationChannel(ChannelId,
                context.GetString(Resource.String.relay_cs_channel_name), NotificationImportance.Low));
        }
        try
        {
            context.StartForegroundService(new Intent(context, typeof(RelayCsChannelService)));
        }
        catch (Java.Lang.Throwable)
        {
            // Background-start limits: the maintenance tick tries again; the channel itself runs regardless.
        }
    }

    public static void Stop(Context context)
    {
        try
        {
            context.StopService(new Intent(context, typeof(RelayCsChannelService)));
        }
        catch (Java.Lang.Throwable)
        {
        }
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var notification = new Notification.Builder(this, ChannelId)
            .SetSmallIcon(Resource.Drawable.ic_notification)
            .SetContentTitle(GetString(Resource.String.app_name))
            .SetContentText(GetString(Resource.String.relay_cs_channel_text))
            .SetOngoing(true)
            .Build();
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(34))
            {
                StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
            }
            else
            {
                StartForeground(NotificationId, notification);
            }
        }
        catch (Java.Lang.Throwable)
        {
            StopSelf();
        }
        return StartCommandResult.NotSticky;
    }
}
