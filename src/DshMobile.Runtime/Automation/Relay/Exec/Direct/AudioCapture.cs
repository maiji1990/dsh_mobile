// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/exec/direct/AudioCapture.kt
// Registered in docs/UPSTREAMS.md. One microphone capture at a time into a staged m4a, published as one artifact.
// The MediaRecorder itself is behind IAudioRecorder (DshMobile.Android).

using System.Text.Json.Nodes;
using DshMobile.Runtime.Automation.Relay.Storage;

namespace DshMobile.Runtime.Automation.Relay.Exec.Direct;

/// <summary>A started MediaRecorder (MIC, MPEG_4/AAC, max duration set).</summary>
public interface IAudioRecording
{
    /// <summary>recorder.stop(); false when it threw (the capture ended early).</summary>
    bool Stop();

    void Release();
}

public interface IAudioRecorder
{
    /// <summary>
    /// setAudioSource … prepare, start. Null with <paramref name="failure"/> set when start was refused:
    /// <c>"security"</c> for a SecurityException, otherwise the exception's simple name.
    /// </summary>
    IAudioRecording? Start(string outputPath, int seconds, out string? failure);
}

public sealed class AudioCapture(StorageReaper reaper, IAudioRecorder recorder, Func<long, CancellationToken, Task> delay)
{
    public const int DefaultSeconds = 5;
    public const int MaxSeconds = 30;
    public const long BytesPerSecond = 32L * 1024L;

    private readonly StorageReaper _reaper = reaper ?? throw new ArgumentNullException(nameof(reaper));
    private readonly IAudioRecorder _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
    private readonly Func<long, CancellationToken, Task> _delay = delay ?? throw new ArgumentNullException(nameof(delay));
    private int _busy;

    public async Task<BackendResult> CaptureAsync(BackendCall call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return new BackendResult.Failed(RelayError.RateLimited, "capture already in flight");
        }
        string? staged = null;
        IAudioRecording? recording = null;
        try
        {
            var seconds = Math.Clamp(OrgJson.OptInt(call.Args, DirectBackend.KeySeconds, DefaultSeconds), 1, MaxSeconds);
            staged = _reaper.NewArtifactFile(call.RequestId, "m4a", seconds * BytesPerSecond);
            if (staged is null)
            {
                return new BackendResult.Failed(RelayError.StorageFull, "quota exhausted");
            }
            recording = _recorder.Start(staged, seconds, out var failure);
            if (recording is null)
            {
                // SecurityException: the microphone grant was revoked (a settings action); anything else is a busy recorder.
                return failure == "security"
                    ? new BackendResult.Failed(RelayError.GateSystemMissing, "RECORD_AUDIO not granted: recorder start refused")
                    : new BackendResult.Failed(RelayError.BackendUnavailable, $"recorder rejected: {failure}");
            }
            await _delay(seconds * 1000L, ct).ConfigureAwait(false);
            if (!recording.Stop())
            {
                return new BackendResult.Failed(RelayError.BackendUnavailable, "capture stopped early");
            }
            var bytes = File.Exists(staged) ? new FileInfo(staged).Length : 0L;
            if (bytes <= 0)
            {
                return new BackendResult.Failed(RelayError.StorageFull, "empty capture");
            }
            if (_reaper.PublishArtifact(staged, call.RequestId) is not { } artifact)
            {
                return new BackendResult.Failed(RelayError.StorageFull, "artifact publish failed");
            }
            if (call.Paths.ToGuestPath(artifact) is not { } guestPath)
            {
                _reaper.Discard(artifact);
                return new BackendResult.Failed(RelayError.InternalError, "artifact path unmappable");
            }
            return new BackendResult.Ok(new JsonObject { ["seconds"] = seconds, ["bytes"] = bytes }, [guestPath], bytes);
        }
        finally
        {
            try
            {
                recording?.Release();
            }
#pragma warning disable CA1031 // upstream runCatching { recorder.release() }
            catch (Exception)
#pragma warning restore CA1031
            {
            }
            if (staged is not null && File.Exists(staged))
            {
                File.Delete(staged);
            }
            Volatile.Write(ref _busy, 0);
        }
    }
}
