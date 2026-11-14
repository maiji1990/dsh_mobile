// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/exec/shizuku/LastFrameCache.kt
// Registered in docs/UPSTREAMS.md. The trusted display's last encoded frame: served again (marked stale) when the
// display stops redrawing, never across displays, never after a process restart (the timestamp lives in memory).

using DshMobile.Runtime.Automation.Relay.Logging;

namespace DshMobile.Runtime.Automation.Relay.Exec.Shizuku;

public sealed class LastFrameCache(string file, Func<long>? now = null, long maxBytes = LastFrameCache.MaxBytes)
{
    /// <summary>Same order as one lossless PNG; anything larger should not stay on local storage.</summary>
    public const long MaxBytes = 8L * 1024 * 1024;

    private readonly string _file = file ?? throw new ArgumentNullException(nameof(file));
    private readonly Func<long> _now = now ?? RelayClock.MonotonicNow;
    private readonly object _gate = new();
    private int _displayId = -1;
    private long _takenAtMs;

    /// <summary>One successful grab. A failed write does not fail the call; the next idle capture just has nothing to serve.</summary>
    public bool Remember(int display, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        lock (_gate)
        {
            if (bytes.LongLength > maxBytes)
            {
                ClearLocked();
                return false;
            }
            try
            {
                if (Path.GetDirectoryName(_file) is { Length: > 0 } dir)
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllBytes(_file, bytes);
            }
#pragma warning disable CA1031 // upstream runCatching { writeBytes }
            catch (Exception)
#pragma warning restore CA1031
            {
                ClearLocked();
                return false;
            }
            _displayId = display;
            _takenAtMs = _now();
            return true;
        }
    }

    /// <summary>(bytes, age) of the cached frame for this display; null when there is nothing to serve.</summary>
    public (byte[] Bytes, long AgeMs)? Peek(int display)
    {
        lock (_gate)
        {
            if (_displayId != display || _takenAtMs == 0L || !File.Exists(_file))
            {
                return null;
            }
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(_file);
            }
#pragma warning disable CA1031 // upstream runCatching { readBytes }.getOrNull()
            catch (Exception)
#pragma warning restore CA1031
            {
                return null;
            }
            if (bytes.Length == 0)
            {
                return null;
            }
            // A negative age means the path crossed a clock reset: serve nothing rather than a wrong age.
            var age = _now() - _takenAtMs;
            return age < 0L ? null : (bytes, age);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            ClearLocked();
        }
    }

    private void ClearLocked()
    {
        _displayId = -1;
        _takenAtMs = 0L;
        try
        {
            File.Delete(_file);
        }
#pragma warning disable CA1031 // upstream runCatching { file.delete() }
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
