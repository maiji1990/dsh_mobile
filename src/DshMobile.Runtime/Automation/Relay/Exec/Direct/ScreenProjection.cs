// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/exec/direct/ScreenProjection.kt (idleTimeLeftMs, Consent, obtain,
//     createConsentGate, resetConsent, consentFailed, onConsentResult, onMintTimeout, mintPendingToken,
//     finishWaiting, armIdleExpiry, onIdleExpiry, release, captureFrame, record, endSession, publish; constants)
// Registered in docs/UPSTREAMS.md. The MediaProjection consent state machine and the capture / record flows. The
// system dialog, the projection foreground service, the token, the virtual display, the ImageReader and the
// MediaRecorder are behind IProjectionPlatform (DshMobile.Android). Every capture ends the session (one dialog per use).

using System.Text.Json.Nodes;
using DshMobile.Runtime.Automation.Relay.Logging;
using DshMobile.Runtime.Automation.Relay.Storage;

namespace DshMobile.Runtime.Automation.Relay.Exec.Direct;

/// <summary>A granted MediaProjection.</summary>
public interface IProjectionToken
{
    void Stop();
}

/// <summary>One frame from a mirroring virtual display, or why there was none.</summary>
public abstract record FrameGrab
{
    /// <summary><paramref name="WritePng"/> compresses to PNG at the path; false when nothing was written.</summary>
    public sealed record Frame(int Width, int Height, Func<string, bool> WritePng, Action Recycle) : FrameGrab;

    /// <summary>createVirtualDisplay threw (<paramref name="Cause"/> is its simple name; SecurityException ends the session).</summary>
    public sealed record NoDisplay(string? Cause) : FrameGrab;

    public sealed record NoImage : FrameGrab;
}

/// <summary>A started screen recording (MediaRecorder on a mirroring virtual display).</summary>
public interface IScreenRecording
{
    /// <summary>recorder.stop(); the exception's simple name when it threw, else null.</summary>
    string? Stop();

    /// <summary>The last onError ("media error what=… extra=…"), if any.</summary>
    string? RecorderError { get; }

    void Release();
}

public interface IProjectionPlatform
{
    /// <summary>createScreenCaptureIntent (the default-display config on API 34+); null without a manager or an intent.</summary>
    object? CreateConsentIntent();

    /// <summary>Brings up the activity that presents the system dialog; false when no surface can.</summary>
    bool StartConsentHost();

    /// <summary>startForegroundService for the projection type; <paramref name="onReady"/> runs once it is seated.</summary>
    bool StartProjectionService(Action onReady);

    void StopProjectionService();

    /// <summary>getMediaProjection + registerCallback(onStop); null with <paramref name="failure"/> = "Class: message".</summary>
    IProjectionToken? Mint(int resultCode, object data, Action onStop, out string? failure);

    int MetricsWidth { get; }

    int MetricsHeight { get; }

    Task<FrameGrab> GrabFrameAsync(IProjectionToken token, int width, int height, CancellationToken ct);

    /// <summary>Prepare + start the recorder, then the display. Null with <paramref name="failure"/> "recorder" or "display:Cause".</summary>
    IScreenRecording? StartRecording(IProjectionToken token, string path, int width, int height, int frameRate, int bitsPerSecond,
        out string? failure);
}

public sealed class ScreenProjection
{
    public const long ConsentGraceMs = 10_000;
    public const long ConsentLifetimeMs = 90_000;
    public const long MintTimeoutMs = 5_000;
    public const long IdleExpiryMs = 60_000;
    public const float FrameScale = 0.5f;
    public const int MinFramePx = 64;
    public const long BytesPerPixel = 4;
    public const long MaxEstimateBytes = 32L * 1024 * 1024;
    public const int DefaultSeconds = 5;
    public const int MaxSeconds = 30;
    public const int VideoFrameRate = 15;
    public const long VideoBitsPerSecond = 6_000_000;
    public const int ResultOk = -1;

    private enum Consent
    {
        Granted,
        Awaiting,
        Declined,
        TokenFailed,
        NoUi,
    }

    private readonly IProjectionPlatform _platform;
    private readonly StorageReaper _reaper;
    private readonly RunLog _runLog;
    private readonly TimeProvider _time;
    private readonly long _origin;
    private readonly Lock _lock = new();
    private TaskCompletionSource<Consent>? _waiting;
    private long _consentStartedAt;
    private IProjectionToken? _projection;
    private (int Code, object Data)? _pendingConsent;
    private long _idleSinceMs;
    private ITimer? _mintTimer;
    private ITimer? _idleTimer;
    private bool _selfStopping;
    private object? _consentRequest;

    public ScreenProjection(IProjectionPlatform platform, StorageReaper reaper, RunLog runLog, TimeProvider time)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _reaper = reaper ?? throw new ArgumentNullException(nameof(reaper));
        _runLog = runLog ?? throw new ArgumentNullException(nameof(runLog));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _origin = time.GetTimestamp();
    }

    /// <summary>Upstream <c>idleTimeLeftMs</c>.</summary>
    public static long IdleTimeLeftMs(long idleSinceMs, long nowMs, long idleExpiryMs) => idleSinceMs + idleExpiryMs - nowMs;

    private long Now() => (long)_time.GetElapsedTime(_origin).TotalMilliseconds;

    /// <summary>The system dialog's intent while one is waiting (the consent activity starts it), else null.</summary>
    public object? ConsentRequest => Volatile.Read(ref _consentRequest);

    public event EventHandler? ConsentRequestChanged;

    private void SetConsentRequest(object? value)
    {
        Volatile.Write(ref _consentRequest, value);
        ConsentRequestChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<(Consent Kind, IProjectionToken? Token)> ObtainAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _projection) is { } held)
        {
            return (Consent.Granted, held);
        }
        var gate = Volatile.Read(ref _waiting) ?? CreateConsentGate();
        if (gate is null)
        {
            return (Consent.NoUi, null);
        }
        var remaining = Math.Max(ConsentLifetimeMs - (Now() - Volatile.Read(ref _consentStartedAt)), 0);
        var wait = Math.Min(ConsentGraceMs, remaining);
        var delay = Task.Delay(TimeSpan.FromMilliseconds(wait), _time, ct);
        if (await Task.WhenAny(gate.Task, delay).ConfigureAwait(false) == gate.Task)
        {
            var result = await gate.Task.ConfigureAwait(false);
            return (result, result == Consent.Granted ? Volatile.Read(ref _projection) : null);
        }
        ct.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _projection) is { } late)
        {
            return (Consent.Granted, late);
        }
        if (Now() - Volatile.Read(ref _consentStartedAt) >= ConsentLifetimeMs)
        {
            _runLog.Warn(LogSubsystem.Gate, LogEvent.ProjectionConsentTimeout);
            ResetConsent();
        }
        else
        {
            _runLog.Info(LogSubsystem.Gate, LogEvent.ProjectionConsentPending);
        }
        return (Consent.Awaiting, null);
    }

    private TaskCompletionSource<Consent>? CreateConsentGate()
    {
        if (_platform.CreateConsentIntent() is not { } intent)
        {
            return null;
        }
        var created = new TaskCompletionSource<Consent>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _waiting, created, null) is { } existing)
        {
            return existing;
        }
        SetConsentRequest(intent);
        Volatile.Write(ref _consentStartedAt, Now());
        if (!_platform.StartConsentHost())
        {
            _runLog.Warn(LogSubsystem.Gate, LogEvent.ProjectionNoUi);
            ResetConsent();
            return null;
        }
        return created;
    }

    private void ResetConsent()
    {
        Volatile.Write(ref _waiting, null);
        SetConsentRequest(null);
    }

    private static BackendResult.Failed ConsentFailed(Consent consent) => consent switch
    {
        Consent.Declined => new(RelayError.SurfaceSystemConsentRequired, "user declined the system capture dialog", RelayResponse.ConsentKindAndroidProjection),
        Consent.TokenFailed => new(RelayError.BackendUnavailable, "system refused the capture token for this session", RelayResponse.ConsentKindAndroidProjection),
        Consent.NoUi => new(RelayError.GateNoForeground, "no foreground surface can present the system capture dialog", RelayResponse.ConsentKindAndroidProjection),
        _ => new(RelayError.GateAwaitingConsent, "system capture dialog is waiting for the user", RelayResponse.ConsentKindAndroidProjection),
    };

    /// <summary>The consent activity's result. The token is minted only once the projection service is in the foreground.</summary>
    public void OnConsentResult(int resultCode, object? data)
    {
        if (resultCode != ResultOk || data is null)
        {
            _runLog.Info(LogSubsystem.Gate, LogEvent.ProjectionConsentDenied);
            FinishWaiting(Consent.Declined);
            return;
        }
        lock (_lock)
        {
            _pendingConsent = (resultCode, data);
            _mintTimer?.Dispose();
            _mintTimer = _time.CreateTimer(_ => OnMintTimeout(), null, TimeSpan.FromMilliseconds(MintTimeoutMs), Timeout.InfiniteTimeSpan);
        }
        if (!_platform.StartProjectionService(MintPendingToken))
        {
            _runLog.Warn(LogSubsystem.Gate, LogEvent.ProjectionTokenFailed, ("cause", "fgs-start-failed"));
            CancelMintTimeout();
            _platform.StopProjectionService();
            FinishWaiting(Consent.TokenFailed);
        }
    }

    private void OnMintTimeout()
    {
        lock (_lock)
        {
            if (_pendingConsent is null)
            {
                return;
            }
            _pendingConsent = null;
        }
        _runLog.Warn(LogSubsystem.Gate, LogEvent.ProjectionTokenFailed, ("cause", "fgs-ready-timeout"));
        _platform.StopProjectionService();
        FinishWaiting(Consent.TokenFailed);
    }

    private void CancelMintTimeout()
    {
        lock (_lock)
        {
            _mintTimer?.Dispose();
            _mintTimer = null;
        }
    }

    private void MintPendingToken()
    {
        (int Code, object Data) pending;
        lock (_lock)
        {
            if (_pendingConsent is not { } p)
            {
                return;
            }
            pending = p;
            _pendingConsent = null;
        }
        CancelMintTimeout();
        IProjectionToken? obtained;
        string? failure;
        try
        {
            obtained = _platform.Mint(pending.Code, pending.Data, OnProjectionStopped, out failure);
        }
#pragma warning disable CA1031 // upstream runCatching around getMediaProjection
        catch (Exception ex)
#pragma warning restore CA1031
        {
            obtained = null;
            failure = $"{ex.GetType().Name}: {ex.Message}";
        }
        if (obtained is null)
        {
            if (failure is not null)
            {
                _runLog.Warn(LogSubsystem.Gate, LogEvent.ProjectionTokenFailed, ("cause", failure));
            }
            _platform.StopProjectionService();
            FinishWaiting(Consent.TokenFailed);
            return;
        }
        Volatile.Write(ref _projection, obtained);
        ArmIdleExpiry();
        _runLog.Info(LogSubsystem.Gate, LogEvent.ProjectionConsentGranted);
        FinishWaiting(Consent.Granted);
    }

    private void OnProjectionStopped()
    {
        Volatile.Write(ref _projection, null);
        DisarmIdleExpiry();
        if (!Volatile.Read(ref _selfStopping))
        {
            _runLog.Info(LogSubsystem.Gate, LogEvent.ProjectionRevoked);
        }
    }

    private void FinishWaiting(Consent result)
    {
        Interlocked.Exchange(ref _waiting, null)?.TrySetResult(result);
        SetConsentRequest(null);
    }

    private void ArmIdleExpiry()
    {
        lock (_lock)
        {
            _idleSinceMs = Now();
            _idleTimer?.Dispose();
            _idleTimer = _time.CreateTimer(_ => OnIdleExpiry(), null, TimeSpan.FromMilliseconds(IdleExpiryMs), Timeout.InfiniteTimeSpan);
        }
    }

    private void DisarmIdleExpiry()
    {
        lock (_lock)
        {
            _idleSinceMs = 0;
            _idleTimer?.Dispose();
            _idleTimer = null;
        }
    }

    private void OnIdleExpiry()
    {
        lock (_lock)
        {
            if (Volatile.Read(ref _projection) is null || _idleSinceMs == 0)
            {
                return;
            }
            var left = IdleTimeLeftMs(_idleSinceMs, Now(), IdleExpiryMs);
            if (left > 0)
            {
                _idleTimer?.Dispose();
                _idleTimer = _time.CreateTimer(_ => OnIdleExpiry(), null, TimeSpan.FromMilliseconds(left), Timeout.InfiniteTimeSpan);
                return;
            }
        }
        _runLog.Warn(LogSubsystem.Gate, LogEvent.ProjectionTokenFailed, ("cause", "idle-expired"));
        EndSession();
    }

    public void Release()
    {
        EndSession();
        ResetConsent();
    }

    public async Task<BackendResult> CaptureFrameAsync(BackendCall call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        var (consent, token) = await ObtainAsync(ct).ConfigureAwait(false);
        if (consent != Consent.Granted || token is null)
        {
            return ConsentFailed(consent == Consent.Granted ? Consent.TokenFailed : consent);
        }
        DisarmIdleExpiry();
        FrameGrab grab;
        var width = Math.Max((int)(_platform.MetricsWidth * FrameScale), MinFramePx);
        var height = Math.Max((int)(_platform.MetricsHeight * FrameScale), MinFramePx);
        try
        {
            grab = await _platform.GrabFrameAsync(token, width, height, ct).ConfigureAwait(false);
        }
        finally
        {
            EndSession();
        }
        if (grab is FrameGrab.NoDisplay noDisplay)
        {
            _runLog.Warn(LogSubsystem.Gate, LogEvent.ProjectionFrameFailed, ("cause", noDisplay.Cause ?? "null"));
        }
        if (grab is not FrameGrab.Frame frame)
        {
            return new BackendResult.Failed(RelayError.BackendUnavailable, "no frame from projection");
        }
        var estimate = Math.Min((long)frame.Width * frame.Height * BytesPerPixel, MaxEstimateBytes);
        if (_reaper.NewArtifactFile(call.RequestId, "png", estimate) is not { } staged)
        {
            frame.Recycle();
            return new BackendResult.Failed(RelayError.StorageFull, "quota exhausted");
        }
        bool written;
        try
        {
            written = frame.WritePng(staged) && File.Exists(staged) && new FileInfo(staged).Length > 0;
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            written = false;
        }
        frame.Recycle();
        if (!written)
        {
            _reaper.Discard(staged);
            return new BackendResult.Failed(RelayError.InternalError, "frame encode failed");
        }
        return Publish(staged, call, new JsonObject { ["width"] = frame.Width, ["height"] = frame.Height });
    }

    public async Task<BackendResult> RecordAsync(BackendCall call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        var (consent, token) = await ObtainAsync(ct).ConfigureAwait(false);
        if (consent != Consent.Granted || token is null)
        {
            return ConsentFailed(consent == Consent.Granted ? Consent.TokenFailed : consent);
        }
        DisarmIdleExpiry();
        var seconds = Math.Clamp(OrgJson.OptInt(call.Args, DirectBackend.KeySeconds, DefaultSeconds), 1, MaxSeconds);
        var estimate = Math.Min(seconds * VideoBitsPerSecond / 8L, MaxEstimateBytes);
        if (_reaper.NewArtifactFile(call.RequestId, "mp4", estimate) is not { } staged)
        {
            EndSession();
            return new BackendResult.Failed(RelayError.StorageFull, "quota exhausted");
        }
        // H.264 wants even dimensions.
        var width = _platform.MetricsWidth & ~1;
        var height = _platform.MetricsHeight & ~1;
        IScreenRecording? recording = null;
        try
        {
            recording = _platform.StartRecording(token, staged, width, height, VideoFrameRate, (int)VideoBitsPerSecond, out var failure);
            if (recording is null)
            {
                _reaper.Discard(staged);
                if (failure is not null && failure.StartsWith("display", StringComparison.Ordinal))
                {
                    var cut = failure.IndexOf(':', StringComparison.Ordinal);
                    _runLog.Warn(LogSubsystem.Gate, LogEvent.ProjectionFrameFailed, ("cause", cut < 0 ? "null" : failure[(cut + 1)..]));
                    return new BackendResult.Failed(RelayError.BackendUnavailable, "no display from projection");
                }
                return new BackendResult.Failed(RelayError.BackendUnavailable, "recorder rejected");
            }
            await DelayAsync(seconds * 1000L, ct).ConfigureAwait(false);
            var thrown = recording.Stop();
            var bytes = File.Exists(staged) ? new FileInfo(staged).Length : 0L;
            if (thrown is not null || bytes <= 0)
            {
                _reaper.Discard(staged);
                var cause = thrown is not null
                    ? $"stop failed: {thrown}" + (recording.RecorderError is { } e ? $" ({e})" : "") +
                      " — the encoder got no frame; if the system dialog is set to a single app, pick whole screen and retry"
                    : "encoder wrote no bytes";
                return new BackendResult.Failed(RelayError.BackendUnavailable, cause);
            }
            return Publish(staged, call, new JsonObject { ["seconds"] = seconds, ["bytes"] = bytes });
        }
        finally
        {
            try
            {
                recording?.Release();
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
            }
            EndSession();
            if (File.Exists(staged))
            {
                _reaper.Discard(staged);
            }
        }
    }

    private Task DelayAsync(long ms, CancellationToken ct) => Task.Delay(TimeSpan.FromMilliseconds(ms), _time, ct);

    private void EndSession()
    {
        var current = Interlocked.Exchange(ref _projection, null);
        lock (_lock)
        {
            _pendingConsent = null;
        }
        DisarmIdleExpiry();
        CancelMintTimeout();
        if (current is not null)
        {
            Volatile.Write(ref _selfStopping, true);
            try
            {
                current.Stop();
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
            }
            Volatile.Write(ref _selfStopping, false);
        }
        _platform.StopProjectionService();
    }

    private BackendResult Publish(string staged, BackendCall call, JsonObject data)
    {
        var bytes = new FileInfo(staged).Length;
        if (_reaper.PublishArtifact(staged, call.RequestId) is not { } artifact)
        {
            return new BackendResult.Failed(RelayError.StorageFull, "artifact publish failed");
        }
        if (call.Paths.ToGuestPath(artifact) is not { } guestPath)
        {
            _reaper.Discard(artifact);
            return new BackendResult.Failed(RelayError.InternalError, "artifact path unmappable");
        }
        return new BackendResult.Ok(data, [guestPath], bytes);
    }
}
