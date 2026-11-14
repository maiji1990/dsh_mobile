// Android half of the screen-projection and audio ports. Translated from DSHBox pilot/interlock-relay-core (GPL-3.0,
// v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/exec/direct/ScreenProjection.kt (consentIntent, startConsentHost,
//     getMediaProjection + registerCallback, grabFrame / createDisplay / awaitImage / bitmapOf, record's MediaRecorder)
//   src/main/java/interlock/relay/core/exec/direct/RelayProjectionService.kt
//   src/main/java/interlock/relay/core/runtime/RelayConsentActivity.kt
//   src/main/java/interlock/relay/core/exec/direct/AudioCapture.kt (the MediaRecorder sequence)
// Registered in docs/UPSTREAMS.md. Session rules (one display per token, idle expiry, consent lifetime) are in
// DshMobile.Runtime (ScreenProjection.cs / AudioCapture.cs).

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Display;
using Android.Media;
using Android.Media.Projection;
using Android.OS;
using Android.Runtime;
using DshMobile.Runtime.Automation.Relay.Exec.Direct;

namespace DshMobile.App.Automation.Relay;

internal sealed class AndroidAudioRecorder : IAudioRecorder
{
    public IAudioRecording? Start(string outputPath, int seconds, out string? failure)
    {
        failure = null;
#pragma warning disable CA1422 // the parameterless constructor (API 29 floor), as upstream
        var recorder = new MediaRecorder();
#pragma warning restore CA1422
        try
        {
            recorder.SetAudioSource(AudioSource.Mic);
            recorder.SetOutputFormat(OutputFormat.Mpeg4);
            recorder.SetAudioEncoder(AudioEncoder.Aac);
            recorder.SetMaxDuration(seconds * 1000);
            recorder.SetOutputFile(outputPath);
            recorder.Prepare();
            recorder.Start();
            return new Recording(recorder);
        }
        catch (Java.Lang.SecurityException)
        {
            failure = "security";
        }
        catch (Java.Lang.Throwable t)
        {
            failure = t.Class.SimpleName;
        }
        try
        {
            recorder.Release();
        }
        catch (Java.Lang.Throwable)
        {
        }
        return null;
    }

    private sealed class Recording(MediaRecorder recorder) : IAudioRecording
    {
        public bool Stop()
        {
            try
            {
                recorder.Stop();
                return true;
            }
            catch (Java.Lang.Throwable)
            {
                return false;
            }
        }

        public void Release()
        {
            try
            {
                recorder.Release();
            }
            catch (Java.Lang.Throwable)
            {
            }
        }
    }
}

internal sealed class AndroidProjectionPlatform(Context context, Func<bool> uiInForeground) : IProjectionPlatform
{
    private const string DisplayName = "relay-projection";
    private const int MaxImages = 2;
    private const int FramePollTries = 20;
    private const int FramePollMs = 100;

    private readonly Context _app = context.ApplicationContext ?? context;
    private readonly Handler _main = new(Looper.MainLooper!);

    private MediaProjectionManager? Manager => _app.GetSystemService(Context.MediaProjectionService) as MediaProjectionManager;

    /// <summary>Upstream <c>consentIntent</c>: the whole default display on API 34+, else the plain intent.</summary>
    public object? CreateConsentIntent()
    {
        if (Manager is not { } manager)
        {
            return null;
        }
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
        {
            try
            {
                return manager.CreateScreenCaptureIntent(MediaProjectionConfig.CreateConfigForDefaultDisplay());
            }
            catch (Java.Lang.Throwable)
            {
            }
        }
        try
        {
            return manager.CreateScreenCaptureIntent();
        }
        catch (Java.Lang.Throwable)
        {
            return null;
        }
    }

    /// <summary>Upstream <c>startConsentHost</c>: only from the foreground (a background activity start would be blocked).</summary>
    public bool StartConsentHost()
    {
        if (!uiInForeground())
        {
            return false;
        }
        try
        {
            _app.StartActivity(new Intent(_app, typeof(RelayCsConsentActivity)).AddFlags(ActivityFlags.NewTask));
            return true;
        }
        catch (Java.Lang.Throwable)
        {
            return false;
        }
    }

    public bool StartProjectionService(Action onReady)
    {
        RelayCsProjectionService.OnReady = onReady;
        return RelayCsProjectionService.Start(_app);
    }

    public void StopProjectionService() => RelayCsProjectionService.Stop(_app);

    public IProjectionToken? Mint(int resultCode, object data, Action onStop, out string? failure)
    {
        failure = null;
        MediaProjection? obtained;
        try
        {
            obtained = Manager?.GetMediaProjection(resultCode, (Intent)data);
        }
        catch (Java.Lang.Throwable t)
        {
            failure = t.Class.SimpleName + ": " + (t.Message ?? "no message");
            return null;
        }
        if (obtained is null)
        {
            failure = "getMediaProjection returned null";
            return null;
        }
        obtained.RegisterCallback(new StopCallback(onStop), _main);
        return new Token(obtained);
    }

    private sealed class StopCallback(Action onStop) : MediaProjection.Callback
    {
        public override void OnStop() => onStop();
    }

    private sealed class Token(MediaProjection projection) : IProjectionToken
    {
        public MediaProjection Projection { get; } = projection;

        public void Stop()
        {
            try
            {
                Projection.Stop();
            }
            catch (Java.Lang.Throwable)
            {
            }
        }
    }

    public int MetricsWidth => _app.Resources!.DisplayMetrics!.WidthPixels;

    public int MetricsHeight => _app.Resources!.DisplayMetrics!.HeightPixels;

    private (VirtualDisplay? Display, string? Cause) CreateDisplay(MediaProjection source, Android.Views.Surface surface, int width, int height)
    {
        try
        {
            var display = source.CreateVirtualDisplay(DisplayName, width, height, (int)_app.Resources!.DisplayMetrics!.DensityDpi,
                (Android.Views.DisplayFlags)(int)VirtualDisplayFlags.AutoMirror, surface, null, _main);
            return (display, null);
        }
        catch (Java.Lang.Throwable t)
        {
            return (null, t.Class.SimpleName);
        }
    }

    /// <summary>Upstream <c>grabFrame</c>: one display per call, first frame within the poll budget, then released.</summary>
    public async Task<FrameGrab> GrabFrameAsync(IProjectionToken token, int width, int height, CancellationToken ct)
    {
        var source = ((Token)token).Projection;
        using var reader = ImageReader.NewInstance(width, height, (ImageFormatType)Format.Rgba8888, MaxImages);
        var (display, cause) = CreateDisplay(source, reader.Surface!, width, height);
        if (display is null)
        {
            return new FrameGrab.NoDisplay(cause);
        }
        try
        {
            for (var i = 0; i < FramePollTries; i++)
            {
                if (reader.AcquireLatestImage() is { } image)
                {
                    try
                    {
                        var bitmap = BitmapOf(image);
                        return new FrameGrab.Frame(image.Width, image.Height, path =>
                        {
                            try
                            {
                                using var stream = File.Create(path);
                                return bitmap.Compress(Bitmap.CompressFormat.Png!, 100, stream);
                            }
                            catch (IOException)
                            {
                                return false;
                            }
                        }, bitmap.Recycle);
                    }
                    finally
                    {
                        image.Close();
                    }
                }
                await Task.Delay(FramePollMs, ct).ConfigureAwait(false);
            }
            return new FrameGrab.NoImage();
        }
        finally
        {
            display.Release();
        }
    }

    /// <summary>Upstream <c>bitmapOf</c>: copy the padded rows, then crop the padding columns away.</summary>
    private static Bitmap BitmapOf(Image image)
    {
        var plane = image.GetPlanes()![0];
        var pixelStride = plane.PixelStride;
        var rowPadding = plane.RowStride - pixelStride * image.Width;
        var padded = Bitmap.CreateBitmap(image.Width + rowPadding / pixelStride, image.Height, Bitmap.Config.Argb8888!)!;
        padded.CopyPixelsFromBuffer(plane.Buffer!);
        if (rowPadding == 0)
        {
            return padded;
        }
        var cropped = Bitmap.CreateBitmap(padded, 0, 0, image.Width, image.Height)!;
        padded.Recycle();
        return cropped;
    }

    public IScreenRecording? StartRecording(IProjectionToken token, string path, int width, int height, int frameRate, int bitsPerSecond,
        out string? failure)
    {
        failure = null;
        var source = ((Token)token).Projection;
#pragma warning disable CA1422 // the parameterless constructor (API 29 floor), as upstream
        var recorder = new MediaRecorder();
#pragma warning restore CA1422
        var recording = new Recording(recorder);
        try
        {
            recorder.SetVideoSource(VideoSource.Surface);
            recorder.SetOutputFormat(OutputFormat.Mpeg4);
            recorder.SetVideoEncoder(VideoEncoder.H264);
            recorder.SetVideoSize(width, height);
            recorder.SetVideoFrameRate(frameRate);
            recorder.SetVideoEncodingBitRate(bitsPerSecond);
            recorder.SetOutputFile(path);
            recorder.SetOnErrorListener(recording);
            recorder.Prepare();
            recorder.Start();
        }
        catch (Java.Lang.Throwable)
        {
            failure = "recorder";
            recording.Release();
            return null;
        }
        var (display, cause) = CreateDisplay(source, recorder.Surface!, width, height);
        if (display is null)
        {
            failure = "display:" + cause;
            recording.Release();
            return null;
        }
        recording.Display = display;
        return recording;
    }

    private sealed class Recording(MediaRecorder recorder) : Java.Lang.Object, IScreenRecording, MediaRecorder.IOnErrorListener
    {
        public VirtualDisplay? Display { get; set; }

        public string? RecorderError { get; private set; }

        public void OnError(MediaRecorder? mr, MediaRecorderError what, int extra) =>
            RecorderError = $"media error what={(int)what} extra={extra}";

        public string? Stop()
        {
            try
            {
                recorder.Stop();
                return null;
            }
            catch (Java.Lang.Throwable t)
            {
                return t.Class.SimpleName;
            }
        }

        public void Release()
        {
            try
            {
                recorder.Release();
            }
            catch (Java.Lang.Throwable)
            {
            }
            Display?.Release();
            Display = null;
        }
    }
}

/// <summary>
/// Upstream <c>RelayProjectionService</c>: exists only so the token may be minted (Android 14+ requires a running
/// mediaProjection-type foreground service before <c>getMediaProjection</c>). Declared in AndroidManifest.xml.
/// </summary>
[Register("dshmobile.relay.RelayCsProjectionService")]
public sealed class RelayCsProjectionService : Service
{
    private const string ChannelId = "relay_cs_projection";
    private const int NotificationId = 0x5050;

    public static Action? OnReady { get; set; }

    public static bool Start(Context context)
    {
        var manager = (NotificationManager)context.GetSystemService(NotificationService)!;
        if (manager.GetNotificationChannel(ChannelId) is null)
        {
            manager.CreateNotificationChannel(new NotificationChannel(ChannelId,
                context.GetString(Resource.String.relay_cs_projection_channel), NotificationImportance.Low));
        }
        try
        {
            context.StartForegroundService(new Intent(context, typeof(RelayCsProjectionService)));
            return true;
        }
        catch (Java.Lang.Throwable)
        {
            return false;
        }
    }

    public static void Stop(Context context)
    {
        OnReady = null;
        try
        {
            context.StopService(new Intent(context, typeof(RelayCsProjectionService)));
        }
        catch (Java.Lang.Throwable)
        {
        }
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var notification = new Notification.Builder(this, ChannelId)
            .SetSmallIcon(Android.Resource.Drawable.IcMenuCamera)
            .SetContentTitle(GetString(Resource.String.app_name))
            .SetContentText(GetString(Resource.String.relay_cs_projection_ongoing))
            .SetOngoing(true)
            .Build();
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
            {
                StartForeground(NotificationId, notification, ForegroundService.TypeMediaProjection);
            }
        }
        catch (Java.Lang.Throwable)
        {
            StopSelf();
        }
        try
        {
            OnReady?.Invoke();
        }
#pragma warning disable CA1031 // upstream runCatching: a failing mint is reported by ScreenProjection itself
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        return StartCommandResult.NotSticky;
    }
}

/// <summary>
/// Upstream <c>RelayConsentActivity</c>: presents the system capture dialog for the C# host's pending consent and hands
/// the result back (a destroyed-while-finishing activity reports a cancel). Not exported.
/// </summary>
[Activity(Name = "dshmobile.relay.RelayCsConsentActivity", Exported = false, Theme = "@android:style/Theme.Translucent.NoTitleBar",
    ExcludeFromRecents = true)]
public sealed class RelayCsConsentActivity : Activity
{
    private const int RequestConsent = 1;
    private bool _delivered;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (CsRelay.Host?.ScreenProjection.ConsentRequest is not Intent pending)
        {
            Finish();
            return;
        }
        if (savedInstanceState is not null)
        {
            return;
        }
        StartActivityForResult(pending, RequestConsent);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == RequestConsent)
        {
            Deliver((int)resultCode, data);
        }
    }

    private void Deliver(int resultCode, Intent? data)
    {
        if (_delivered)
        {
            return;
        }
        _delivered = true;
        CsRelay.Host?.ScreenProjection.OnConsentResult(resultCode, data);
        Finish();
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (IsFinishing && !IsChangingConfigurations)
        {
            Deliver((int)Result.Canceled, null);
        }
    }
}
