using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;

namespace DshMobile.App;

/// <summary>
/// Foreground service that keeps the process (and its PRoot/DSH children) alive while the sandbox runs.
/// It does no work itself: the bridge owns the runtime. Declared in AndroidManifest.xml (specialUse + subtype),
/// hence [Register] with a fixed Java name instead of a generated [Service] entry.
/// </summary>
[Register("dshmobile.app.HarnessForegroundService")]
public sealed class HarnessForegroundService : Service
{
    private const string ChannelId = "harness";
    private const int NotificationId = 1001;

    public static void Start(Context context) =>
        context.StartForegroundService(new Intent(context, typeof(HarnessForegroundService)));

    public static void Stop(Context context) =>
        context.StopService(new Intent(context, typeof(HarnessForegroundService)));

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(ChannelId, GetString(Resource.String.fgs_channel), NotificationImportance.Low));

        var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)), PendingIntentFlags.Immutable);
        var notification = new Notification.Builder(this, ChannelId)
            .SetContentTitle(GetString(Resource.String.app_name))
            .SetContentText(GetString(Resource.String.fgs_text))
            .SetSmallIcon(Resource.Mipmap.appicon)
            .SetContentIntent(open)
            .SetOngoing(true)
            .Build();

        if (OperatingSystem.IsAndroidVersionAtLeast(34))
        {
            StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
        }
        else
        {
            StartForeground(NotificationId, notification);
        }
        // Not sticky: if the system kills us, the runtime state is gone too; the user restarts explicitly.
        return StartCommandResult.NotSticky;
    }
}
