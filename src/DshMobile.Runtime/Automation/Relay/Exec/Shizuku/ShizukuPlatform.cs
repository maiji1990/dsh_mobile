// Interfaces for the Shizuku backend port (see ShizukuBackend.cs for the upstream reference).
// Every member is one Android call implemented in DshMobile.Android; the remote half is the in-repo Java
// ShellService (src/DshMobile.Android/Java/dshmobile/relay/ShellService.java, ADR-0009).

using DshMobile.Runtime.Automation.Relay.Exec.Direct;

namespace DshMobile.Runtime.Automation.Relay.Exec.Shizuku;

/// <summary>Upstream <c>RelayShellService.Outcome</c>.</summary>
public sealed record ShellOutcome(int ExitCode, string Stdout, string Stderr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>A display transaction's reply: (code, reason) plus, for a capture, the frame read from the returned descriptor.</summary>
public sealed record DisplayReply(int Code, string Reason, byte[]? Frame = null);

/// <summary>CODE_SCREENSHOT's answer: the frame (null when none) and the service's own reason line.</summary>
public sealed record ScreenshotReply(byte[]? Frame, string Note);

/// <summary>
/// The bound user-service binder. Blocking <c>IBinder.transact</c> calls with ShellService's codes and parcel layout;
/// ShizukuBackend only calls them on its single remote-transaction thread.
/// </summary>
public interface IShellService
{
    /// <summary>CODE_RUN. Null when the transaction was refused or the binder died (RemoteException).</summary>
    ShellOutcome? Run(string command, long timeoutMs);

    /// <summary>CODE_INSTALL with the vetted upload's descriptor (sent by reference). Null when refused / dead.</summary>
    ShellOutcome? Install(IDisposable descriptor, string stagedName, long timeoutMs);

    /// <summary>CODE_DISPLAY_CREATE: (displayId or &lt;0, reason). Refused → (-1, "service did not answer"), dead → (-1, "service dead").</summary>
    DisplayReply CreateDisplay(int width, int height, int dpi);

    /// <summary>CODE_DISPLAY_RELEASE: (1 released / 0 none / &lt;0 failed, reason).</summary>
    DisplayReply ReleaseDisplay();

    /// <summary>CODE_DISPLAY_QUERY: (displayId or -1, "id=N WxH").</summary>
    DisplayReply QueryDisplay();

    /// <summary>CODE_DISPLAY_CAPTURE (long wait, int jpegQuality, int maxWidth): Frame is the descriptor's bytes, null when none or empty.</summary>
    DisplayReply CaptureDisplay(long waitMs, int jpegQuality, int maxWidth);

    /// <summary>CODE_SCREENSHOT of the user's real screen.</summary>
    ScreenshotReply Screenshot();
}

/// <summary>An upload that can also be handed to the shell service as a descriptor and parsed as an archive.</summary>
public interface IInstallUpload : IVettedUpload
{
    /// <summary>The vetted file's path (for <c>getPackageArchiveInfo</c>).</summary>
    string FilePath { get; }

    /// <summary>A ParcelFileDescriptor dup of the vetted handle; null when it cannot be opened.</summary>
    IDisposable? OpenDescriptor();
}

/// <summary>A decoded frame in <c>Bitmap.getPixels</c> layout (ARGB_8888 ints, row length = width).</summary>
public sealed record DecodedFrame(int Width, int Height, int[] Argb);

/// <summary>A started MediaCodec (AVC, NV12 / YUV420SemiPlanar) + MediaMuxer pair (upstream <c>FrameEncoder</c>).</summary>
public interface IVideoEncoder
{
    /// <summary>The encoder's own KEY_STRIDE (else the width).</summary>
    int Stride { get; }

    /// <summary>The encoder's own KEY_SLICE_HEIGHT (else the height).</summary>
    int SliceHeight { get; }

    /// <summary>Queues one NV12 frame; false when no input buffer was free (the frame is skipped, not the recording).</summary>
    bool Push(byte[] nv12, int length, long ptsUs);

    /// <summary>EOS, drain, stop the muxer; bytes written to the container (0 = nothing usable).</summary>
    long Finish();
}

/// <summary>Everything ShizukuBackend asks Android outside the binder.</summary>
public interface IShizukuPlatform
{
    string AppPackage { get; }

    /// <summary>Upstream <c>ShizukuState.available</c>: the Shizuku server binder is alive (v11+).</summary>
    bool ServerAvailable { get; }

    /// <summary>
    /// <c>ShizukuState.bindUserService(connection)</c> for ShellService. The connection's callbacks call
    /// <see cref="ShizukuBackend.OnServiceConnected"/>, <see cref="ShizukuBackend.OnServiceDisconnected"/> and
    /// <see cref="ShizukuBackend.OnBindingDied"/>. False when not yet bindable (not running / not granted) or it threw.
    /// </summary>
    bool BindUserService();

    void UnbindUserService();

    /// <summary><c>DisplayManager.getDisplay(id) != null</c>; true when there is no display service to ask.</summary>
    bool DisplayExists(int displayId);

    /// <summary><c>getLaunchIntentForPackage(pkg)?.component?.flattenToShortString()</c>.</summary>
    string? LaunchComponent(string packageName);

    /// <summary>The upload opened once and checked on the open handle; null when there is no such upload.</summary>
    IInstallUpload? OpenInstallUpload(string name, long maxBytes);

    /// <summary><c>getPackageArchiveInfo(path, 0)?.packageName</c>; null when it is not an archive.</summary>
    string? ArchivePackageName(string apkPath);

    /// <summary><c>BitmapFactory.decodeByteArray</c> as ARGB_8888 pixels; null when it is not an image.</summary>
    DecodedFrame? DecodeFrame(byte[] encoded);

    /// <summary>Picks an AVC encoder that takes NV12 and opens the muxer; null when none configures.</summary>
    IVideoEncoder? StartEncoder(string outputPath, int width, int height, int frameRate, int bitsPerSecond);
}
