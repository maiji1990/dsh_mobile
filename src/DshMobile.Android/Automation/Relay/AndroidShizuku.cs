// Android half of the Shizuku backend port. Translated from DSHBox pilot/interlock-relay-core (GPL-3.0,
// v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/exec/shizuku/ShizukuState.kt (ping, bindUserService / unbindUserService)
//   src/main/java/interlock/relay/core/exec/shizuku/ShizukuBackend.kt (the client side of every transaction and its
//     parcel layout, displayExists via DisplayManager, launchComponent, getPackageArchiveInfo, decodeFrame)
//   src/main/java/interlock/relay/core/exec/FrameEncoder.kt (MediaCodec AVC NV12 + MediaMuxer; the NV12 conversion
//     itself is DshMobile.Runtime Nv12.cs)
// Registered in docs/UPSTREAMS.md.
//
// Transitional (ADR-0009): the rikka.shizuku client (dev.rikka.shizuku:api 13.1.5, MIT) is the one already packaged
// with the legacy AAR and is called through standard JNI; its listener interfaces have no C# binding, so the C# host
// polls the server state instead of registering listeners. The remote half is this repository's own Java
// ShellService (src/DshMobile.Android/Java/dshmobile/relay/ShellService.java), bound under its own tag so it never
// shares a process with the legacy Relay's shell service.

using Android.Content;
using Android.Graphics;
using Android.Hardware.Display;
using Android.Media;
using Android.OS;
using Android.Runtime;
using DshMobile.Runtime.Automation.Relay.Exec.Shizuku;
using DshMobile.Runtime.Automation.Relay.Storage;

namespace DshMobile.App.Automation.Relay;

internal sealed class AndroidShizukuPlatform(Context context, RelayPaths paths) : IShizukuPlatform
{
    private const string ShizukuClass = "rikka/shizuku/Shizuku";
    private const string ArgsClass = "rikka/shizuku/Shizuku$UserServiceArgs";
    private const string ServiceClass = "dshmobile.relay.ShellService";
    private const string ProcessSuffix = "dshmobile-shell";
    private const string Tag = "dshmobile-relay";
    private const int ServiceVersion = 1;

    private readonly Context _app = context.ApplicationContext ?? context;
    private ShellConnection? _connection;

    /// <summary>The backend the connection reports to (set once the host is built).</summary>
    public ShizukuBackend? Backend { get; set; }

    public string AppPackage => _app.PackageName!;

    /// <summary>Upstream <c>ping</c>: not pre-v11 and the binder answers.</summary>
    public bool ServerAvailable
    {
        get
        {
            var cls = Class();
            if (cls == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                return !StaticBool(cls, "isPreV11") && StaticBool(cls, "pingBinder");
            }
            catch (Java.Lang.Throwable)
            {
                return false;
            }
        }
    }

    public bool Granted
    {
        get
        {
            var cls = Class();
            if (cls == IntPtr.Zero || !ServerAvailable)
            {
                return false;
            }
            try
            {
                return JNIEnv.CallStaticIntMethod(cls, JNIEnv.GetStaticMethodID(cls, "checkSelfPermission", "()I")) == 0;
            }
            catch (Java.Lang.Throwable)
            {
                return false;
            }
        }
    }

    public bool BindUserService()
    {
        if (!ServerAvailable || !Granted || Backend is not { } backend)
        {
            return false;
        }
        var cls = Class();
        _connection ??= new ShellConnection(backend);
        try
        {
            var args = NewArgs();
            try
            {
                var bind = JNIEnv.GetStaticMethodID(cls, "bindUserService", "(L" + ArgsClass + ";Landroid/content/ServiceConnection;)V");
                JNIEnv.CallStaticVoidMethod(cls, bind, new JValue(args), new JValue(_connection));
            }
            finally
            {
                JNIEnv.DeleteLocalRef(args);
            }
            return true;
        }
        catch (Java.Lang.Throwable)
        {
            return false;
        }
    }

    public void UnbindUserService()
    {
        var cls = Class();
        if (cls == IntPtr.Zero || _connection is null)
        {
            return;
        }
        try
        {
            var args = NewArgs();
            try
            {
                var unbind = JNIEnv.GetStaticMethodID(cls, "unbindUserService", "(L" + ArgsClass + ";Landroid/content/ServiceConnection;Z)V");
                JNIEnv.CallStaticVoidMethod(cls, unbind, new JValue(args), new JValue(_connection), new JValue(true));
            }
            finally
            {
                JNIEnv.DeleteLocalRef(args);
            }
        }
        catch (Java.Lang.Throwable)
        {
        }
    }

    /// <summary>Upstream <c>UserServiceArgs(component).daemon(false).processNameSuffix(..).tag(..).debuggable(false).version(1)</c>.</summary>
    private IntPtr NewArgs()
    {
        var argsClass = JNIEnv.FindClass(ArgsClass);
        using var component = new ComponentName(_app.PackageName!, ServiceClass);
        var ctor = JNIEnv.GetMethodID(argsClass, "<init>", "(Landroid/content/ComponentName;)V");
        var args = JNIEnv.NewObject(argsClass, ctor, new JValue(component));
        string sig = "L" + ArgsClass + ";";
        Chain(args, argsClass, "daemon", "(Z)" + sig, new JValue(false));
        using (var suffix = new Java.Lang.String(ProcessSuffix))
        {
            Chain(args, argsClass, "processNameSuffix", "(Ljava/lang/String;)" + sig, new JValue(suffix));
        }
        using (var tag = new Java.Lang.String(Tag))
        {
            Chain(args, argsClass, "tag", "(Ljava/lang/String;)" + sig, new JValue(tag));
        }
        Chain(args, argsClass, "debuggable", "(Z)" + sig, new JValue(false));
        Chain(args, argsClass, "version", "(I)" + sig, new JValue(ServiceVersion));
        return args;
    }

    private static void Chain(IntPtr target, IntPtr cls, string name, string signature, JValue arg)
    {
        var returned = JNIEnv.CallObjectMethod(target, JNIEnv.GetMethodID(cls, name, signature), arg);
        JNIEnv.DeleteLocalRef(returned); // the builder returns itself; drop the extra local reference
    }

    public bool DisplayExists(int displayId) =>
        _app.GetSystemService(Context.DisplayService) is not DisplayManager manager || manager.GetDisplay(displayId) is not null;

    public string? LaunchComponent(string packageName) =>
        _app.PackageManager!.GetLaunchIntentForPackage(packageName)?.Component?.FlattenToShortString();

    public IInstallUpload? OpenInstallUpload(string name, long maxBytes) => AndroidVettedUpload.Open(paths, name, maxBytes);

    public string? ArchivePackageName(string apkPath)
    {
        try
        {
#pragma warning disable CA1422 // the int-flags overload, as upstream
            return _app.PackageManager!.GetPackageArchiveInfo(apkPath, 0)?.PackageName;
#pragma warning restore CA1422
        }
        catch (Java.Lang.Throwable)
        {
            return null;
        }
    }

    /// <summary>Upstream <c>decodeFrame</c>: ARGB_8888 (a hardware bitmap cannot give its pixels).</summary>
    public DecodedFrame? DecodeFrame(byte[] encoded)
    {
        try
        {
            using var options = new BitmapFactory.Options { InPreferredConfig = Bitmap.Config.Argb8888 };
            using var bitmap = BitmapFactory.DecodeByteArray(encoded, 0, encoded.Length, options);
            if (bitmap is null)
            {
                return null;
            }
            var pixels = new int[bitmap.Width * bitmap.Height];
            bitmap.GetPixels(pixels, 0, bitmap.Width, 0, 0, bitmap.Width, bitmap.Height);
            var frame = new DecodedFrame(bitmap.Width, bitmap.Height, pixels);
            bitmap.Recycle();
            return frame;
        }
        catch (Java.Lang.Throwable)
        {
            return null;
        }
    }

    public IVideoEncoder? StartEncoder(string outputPath, int width, int height, int frameRate, int bitsPerSecond) =>
        AndroidVideoEncoder.Start(outputPath, width, height, frameRate, bitsPerSecond);

    private static IntPtr _class;

    private static IntPtr Class()
    {
        if (_class != IntPtr.Zero)
        {
            return _class;
        }
        try
        {
            _class = JNIEnv.FindClass(ShizukuClass); // a cached global reference: never deleted
        }
        catch (Java.Lang.Throwable)
        {
            _class = IntPtr.Zero;
        }
        return _class;
    }

    private static bool StaticBool(IntPtr cls, string name) =>
        JNIEnv.CallStaticBooleanMethod(cls, JNIEnv.GetStaticMethodID(cls, name, "()Z"));

    /// <summary>The user-service connection: reports to the backend, which owns all binding state.</summary>
    private sealed class ShellConnection(ShizukuBackend backend) : Java.Lang.Object, IServiceConnection
    {
        public void OnServiceConnected(ComponentName? name, IBinder? service)
        {
            if (service is not null)
            {
                backend.OnServiceConnected(new ShellServiceClient(service));
            }
        }

        public void OnServiceDisconnected(ComponentName? name) => backend.OnServiceDisconnected();

        public void OnBindingDied(ComponentName? name) => backend.OnBindingDied();
    }
}

/// <summary>Client side of ShellService's transactions (codes and parcel layout must match ShellService.java).</summary>
internal sealed class ShellServiceClient(IBinder binder) : IShellService
{
    private const int FirstCall = 1; // IBinder.FIRST_CALL_TRANSACTION
    private const int CodeRun = FirstCall;
    private const int CodeInstall = FirstCall + 1;
    private const int CodeDisplayCreate = FirstCall + 2;
    private const int CodeDisplayRelease = FirstCall + 3;
    private const int CodeDisplayCapture = FirstCall + 4;
    private const int CodeDisplayQuery = FirstCall + 5;
    private const int CodeScreenshot = FirstCall + 7;

    private T? Transact<T>(int code, Action<Parcel> write, Func<Parcel, T> read, T? refused, T? dead)
    {
        var data = Parcel.Obtain();
        var reply = Parcel.Obtain();
        try
        {
            write(data);
            if (!binder.Transact(code, data, reply, 0))
            {
                return refused;
            }
            return read(reply);
        }
        catch (Java.Lang.Throwable)
        {
            return dead;
        }
        finally
        {
            data.Recycle();
            reply.Recycle();
        }
    }

    private static ShellOutcome ReadOutcome(Parcel reply) => new(reply.ReadInt(), reply.ReadString() ?? "", reply.ReadString() ?? "");

    public ShellOutcome? Run(string command, long timeoutMs) =>
        Transact(CodeRun, d =>
        {
            d.WriteString(command);
            d.WriteLong(timeoutMs);
        }, ReadOutcome, null, null);

    public ShellOutcome? Install(IDisposable descriptor, string stagedName, long timeoutMs) =>
        Transact(CodeInstall, d =>
        {
            d.WriteFileDescriptor(((ParcelFileDescriptor)descriptor).FileDescriptor!);
            d.WriteString(stagedName);
            d.WriteLong(timeoutMs);
        }, ReadOutcome, null, null);

    private static DisplayReply ReadCode(Parcel reply) => new(reply.ReadInt(), reply.ReadString() ?? "");

    public DisplayReply CreateDisplay(int width, int height, int dpi) =>
        Transact(CodeDisplayCreate, d =>
        {
            d.WriteInt(width);
            d.WriteInt(height);
            d.WriteInt(dpi);
        }, ReadCode, new DisplayReply(-1, "service did not answer"), new DisplayReply(-1, "service dead"))!;

    public DisplayReply ReleaseDisplay() =>
        Transact(CodeDisplayRelease, _ => { }, ReadCode, new DisplayReply(-1, "service did not answer"), new DisplayReply(-1, "service dead"))!;

    public DisplayReply QueryDisplay() =>
        Transact(CodeDisplayQuery, _ => { }, ReadCode, new DisplayReply(-1, "service did not answer"), new DisplayReply(-1, "service dead"))!;

    public DisplayReply CaptureDisplay(long waitMs, int jpegQuality, int maxWidth) =>
        Transact(CodeDisplayCapture, d =>
        {
            d.WriteLong(waitMs);
            d.WriteInt(jpegQuality);
            d.WriteInt(maxWidth);
        }, reply =>
        {
            var code = reply.ReadInt();
            var reason = reply.ReadString() ?? "";
            return new DisplayReply(code, reason, code == 0 ? ReadDescriptor(reply) : null);
        }, new DisplayReply(-1, "service did not answer"), new DisplayReply(-1, "service dead"))!;

    public ScreenshotReply Screenshot() =>
        Transact(CodeScreenshot, _ => { }, reply =>
        {
            var code = reply.ReadInt();
            var reason = reply.ReadString() ?? "";
            return new ScreenshotReply(code == 0 ? ReadDescriptor(reply) : null, reason);
        }, new ScreenshotReply(null, "service did not answer"), new ScreenshotReply(null, "service dead"))!;

    /// <summary>The frame behind the returned descriptor; null when none came back or it was empty.</summary>
    private static byte[]? ReadDescriptor(Parcel reply)
    {
        using var descriptor = reply.ReadFileDescriptor();
        if (descriptor is null)
        {
            return null;
        }
        using var input = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(descriptor.DetachFd(), ownsHandle: true), FileAccess.Read);
        using var copy = new MemoryStream();
        input.CopyTo(copy);
        return copy.Length > 0 ? copy.ToArray() : null;
    }
}

/// <summary>Upstream <c>FrameEncoder</c>: an AVC encoder that takes NV12 (YUV420SemiPlanar) plus an MP4 muxer.</summary>
internal sealed class AndroidVideoEncoder : IVideoEncoder
{
    private const int IFrameSeconds = 2;
    private const long InputTimeoutUs = 10_000L;
    private const long FlushPollUs = 10_000L;
    private const long DrainBudgetUs = 2_000_000L;
    private const int DrainIdleStop = 20;
    private const int ColorFormatYuv420SemiPlanar = 21; // MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420SemiPlanar

    private MediaCodec? _codec;
    private MediaMuxer? _muxer;
    private int _track = -1;
    private int _frameIndex;
    private long _lastPtsUs;
    private bool _muxerRunning;
    private long _written;

    public int Stride { get; private set; }

    public int SliceHeight { get; private set; }

    public static AndroidVideoEncoder? Start(string output, int width, int height, int frameRate, int bitsPerSecond)
    {
        var encoder = new AndroidVideoEncoder();
        if (PickEncoder(width, height, frameRate, bitsPerSecond) is not { } codec)
        {
            return null;
        }
        encoder._codec = codec;
        try
        {
            codec.Start();
            var input = codec.InputFormat;
            encoder.Stride = OrDefault(input, MediaFormat.KeyStride, width);
            encoder.SliceHeight = OrDefault(input, MediaFormat.KeySliceHeight, height);
            encoder._muxer = new MediaMuxer(output, MuxerOutputType.Mpeg4);
            return encoder;
        }
        catch (Java.Lang.Throwable)
        {
            encoder.Release();
            return null;
        }
    }

    private static MediaCodec? PickEncoder(int width, int height, int frameRate, int bitsPerSecond)
    {
        var format = MediaFormat.CreateVideoFormat(MediaFormat.MimetypeVideoAvc, width, height);
        format.SetInteger(MediaFormat.KeyColorFormat, ColorFormatYuv420SemiPlanar);
        format.SetInteger(MediaFormat.KeyBitRate, bitsPerSecond);
        format.SetInteger(MediaFormat.KeyFrameRate, frameRate);
        format.SetInteger(MediaFormat.KeyIFrameInterval, IFrameSeconds);
        MediaCodecInfo[] infos;
        try
        {
            infos = new MediaCodecList(MediaCodecListKind.RegularCodecs).GetCodecInfos() ?? [];
        }
        catch (Java.Lang.Throwable)
        {
            infos = [];
        }
        foreach (var info in infos)
        {
            bool takesNv12;
            try
            {
                takesNv12 = info.IsEncoder && info.GetCapabilitiesForType(MediaFormat.MimetypeVideoAvc)?.ColorFormats?.Contains(ColorFormatYuv420SemiPlanar) == true;
            }
            catch (Java.Lang.Throwable)
            {
                takesNv12 = false;
            }
            if (takesNv12 && ConfigureOrRelease(TryCreate(() => MediaCodec.CreateByCodecName(info.Name)), format) is { } picked)
            {
                return picked;
            }
        }
        return ConfigureOrRelease(TryCreate(() => MediaCodec.CreateEncoderByType(MediaFormat.MimetypeVideoAvc)), format);
    }

    private static MediaCodec? TryCreate(Func<MediaCodec> create)
    {
        try
        {
            return create();
        }
        catch (Java.Lang.Throwable)
        {
            return null;
        }
    }

    private static MediaCodec? ConfigureOrRelease(MediaCodec? codec, MediaFormat format)
    {
        if (codec is null)
        {
            return null;
        }
        try
        {
            codec.Configure(format, null, null, MediaCodecConfigFlags.Encode);
            return codec;
        }
        catch (Java.Lang.Throwable)
        {
            try
            {
                codec.Release();
            }
            catch (Java.Lang.Throwable)
            {
            }
            return null;
        }
    }

    public bool Push(byte[] nv12, int length, long ptsUs)
    {
        if (_codec is not { } active)
        {
            return false;
        }
        int index;
        try
        {
            index = active.DequeueInputBuffer(InputTimeoutUs);
        }
        catch (Java.Lang.Throwable)
        {
            index = -1;
        }
        Drain(active, 0);
        if (index < 0)
        {
            return false;
        }
        Java.Nio.ByteBuffer? input;
        try
        {
            input = active.GetInputBuffer(index);
        }
        catch (Java.Lang.Throwable)
        {
            input = null;
        }
        if (input is null)
        {
            return false;
        }
        input.Clear();
        input.Put(nv12, 0, length);
        active.QueueInputBuffer(index, 0, length, Math.Max(ptsUs, _lastPtsUs), 0);
        _lastPtsUs = ptsUs;
        _frameIndex++;
        Drain(active, 0);
        return true;
    }

    public long Finish()
    {
        if (_codec is not { } active)
        {
            Release();
            return 0L;
        }
        try
        {
            var index = active.DequeueInputBuffer(InputTimeoutUs);
            if (index >= 0)
            {
                active.QueueInputBuffer(index, 0, 0, _lastPtsUs, MediaCodecBufferFlags.EndOfStream);
            }
        }
        catch (Java.Lang.Throwable)
        {
        }
        var waited = 0L;
        while (waited < DrainBudgetUs)
        {
            var before = _frameIndex;
            if (Drain(active, FlushPollUs))
            {
                break;
            }
            waited += FlushPollUs;
            if (before == _frameIndex && waited > FlushPollUs * DrainIdleStop)
            {
                break;
            }
        }
        Release();
        return _written;
    }

    /// <summary>True once the end-of-stream buffer came out.</summary>
    private bool Drain(MediaCodec active, long timeoutUs)
    {
        using var info = new MediaCodec.BufferInfo();
        while (true)
        {
            int code;
            try
            {
                code = active.DequeueOutputBuffer(info, timeoutUs);
            }
            catch (Java.Lang.Throwable)
            {
                code = (int)MediaCodecInfoState.TryAgainLater;
            }
            if (code == (int)MediaCodecInfoState.OutputFormatChanged)
            {
                if (_muxer is not { } opened)
                {
                    return true;
                }
                _track = opened.AddTrack(active.OutputFormat);
                opened.Start();
                _muxerRunning = true;
            }
            else if (code >= 0)
            {
                if (info.Size > 0 && _muxerRunning && _track >= 0 && active.GetOutputBuffer(code) is { } payload)
                {
                    payload.Position(info.Offset);
                    payload.Limit(info.Offset + info.Size);
                    try
                    {
                        _muxer!.WriteSampleData(_track, payload, info);
                        _written += info.Size;
                    }
                    catch (Java.Lang.Throwable)
                    {
                    }
                }
                active.ReleaseOutputBuffer(code, false);
                if ((info.Flags & MediaCodecBufferFlags.EndOfStream) != 0)
                {
                    return true;
                }
            }
            else
            {
                return false;
            }
        }
    }

    private void Release()
    {
        try
        {
            _codec?.Stop();
        }
        catch (Java.Lang.Throwable)
        {
        }
        try
        {
            _codec?.Release();
        }
        catch (Java.Lang.Throwable)
        {
        }
        try
        {
            if (_muxerRunning && _track >= 0)
            {
                _muxer?.Stop();
            }
        }
        catch (Java.Lang.Throwable)
        {
        }
        try
        {
            _muxer?.Release();
        }
        catch (Java.Lang.Throwable)
        {
        }
        _muxerRunning = false;
        _codec = null;
        _muxer = null;
        _track = -1;
    }

    private static int OrDefault(MediaFormat? format, string key, int fallback)
    {
        try
        {
            return format is not null && format.ContainsKey(key) ? format.GetInteger(key) : fallback;
        }
        catch (Java.Lang.Throwable)
        {
            return fallback;
        }
    }
}
