using System.Threading.Channels;

namespace DshMobile.Application.Terminal;

/// <summary>
/// One attached view's end of a session's output (vNext PR-08). <see cref="Write"/> is called on the session's reader
/// thread under its lock and must never block; <see cref="WaitForRoomAsync"/> is awaited by that reader before each PTY
/// read, so a slow view slows the shell (the PTY fills and the shell's writes block, as on any real terminal) instead of
/// queueing output in memory without bound.
/// </summary>
public interface ITerminalOutputSink
{
    void Write(byte[] chunk);

    ValueTask WaitForRoomAsync(CancellationToken ct);
}

/// <summary>
/// PTY bytes → view, with flow control (vNext PR-08). Bytes are batched (≤ <see cref="BatchBytes"/>) and handed to
/// <c>post</c> as <c>{"t":"out","b":"&lt;base64&gt;"}</c> — raw bytes, never decoded here: the emulator decodes UTF-8
/// itself across batch boundaries, so a multi-byte character split by the PTY read or the batching arrives intact.
/// The view acknowledges each batch after rendering it (<see cref="Ack"/>); while queued + unacknowledged bytes reach
/// <see cref="WindowBytes"/>, the session reader waits. A view that stops acknowledging (page frozen, renderer gone)
/// is not allowed to freeze the shell: after <see cref="StallTimeout"/> without progress the pump stops throttling
/// until the next acknowledgement (<see cref="Stalls"/> counts how often).
/// </summary>
public sealed class TerminalOutputPump : ITerminalOutputSink, IDisposable
{
    public const int DefaultBatchBytes = 64 * 1024;
    public const int DefaultWindowBytes = 1024 * 1024;
    public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(5);

    private readonly Action<string> _post;
    private readonly TimeProvider _time;
    private readonly Channel<byte[]?> _queue = Channel.CreateUnbounded<byte[]?>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _lock = new();
    private long _queued;
    private long _unacked;
    private long _lastProgress;
    private bool _unthrottled;
    private bool _disposed;
    private TaskCompletionSource? _room;

    /// <param name="post">Called on the pump's own task, in order; the caller marshals to its UI thread.</param>
    public TerminalOutputPump(Action<string> post, int batchBytes = DefaultBatchBytes, int windowBytes = DefaultWindowBytes,
        TimeSpan? stallTimeout = null, TimeProvider? time = null)
    {
        _post = post ?? throw new ArgumentNullException(nameof(post));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowBytes, batchBytes);
        BatchBytes = batchBytes;
        WindowBytes = windowBytes;
        StallTimeout = stallTimeout ?? DefaultStallTimeout;
        _time = time ?? TimeProvider.System;
        _lastProgress = _time.GetTimestamp();
        Completion = Task.Run(RunAsync);
    }

    public int BatchBytes { get; }

    public int WindowBytes { get; }

    public TimeSpan StallTimeout { get; }

    /// <summary>The pump's loop; ends after <see cref="Dispose"/> once everything queued before it was posted.</summary>
    public Task Completion { get; }

    /// <summary>Times the view stopped acknowledging for <see cref="StallTimeout"/> and throttling was lifted.</summary>
    public int Stalls { get; private set; }

    /// <summary>Queued + posted-but-unacknowledged bytes.</summary>
    public long Pending
    {
        get
        {
            lock (_lock)
            {
                return _queued + _unacked;
            }
        }
    }

    public void Write(byte[] chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Length == 0)
        {
            return;
        }
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _queued += chunk.Length;
        }
        _queue.Writer.TryWrite(chunk);
    }

    /// <summary>Asks the view to clear the screen before the bytes that follow (attach / replay).</summary>
    public void Reset() => _queue.Writer.TryWrite(null);

    /// <summary>The view rendered <paramref name="bytes"/> more bytes. Unknown / excess acknowledgements are clamped.</summary>
    public void Ack(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }
        TaskCompletionSource? release = null;
        lock (_lock)
        {
            _unacked = Math.Max(0, _unacked - bytes);
            _lastProgress = _time.GetTimestamp();
            _unthrottled = false;
            if (_queued + _unacked < WindowBytes)
            {
                release = _room;
                _room = null;
            }
        }
        release?.TrySetResult();
    }

    public async ValueTask WaitForRoomAsync(CancellationToken ct)
    {
        while (true)
        {
            TaskCompletionSource room;
            long since;
            lock (_lock)
            {
                if (_disposed || _unthrottled || _queued + _unacked < WindowBytes)
                {
                    return;
                }
                if (_room is null)
                {
                    _room = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    // A window that fills after a quiet spell is not a stall: count from the start of this wait.
                    _lastProgress = Math.Max(_lastProgress, _time.GetTimestamp());
                }
                room = _room;
                since = _lastProgress;
            }
            var left = StallTimeout - _time.GetElapsedTime(since);
            if (left > TimeSpan.Zero)
            {
                var delay = Task.Delay(left, _time, ct);
                await Task.WhenAny(room.Task, delay).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                continue;
            }
            lock (_lock)
            {
                if (!_unthrottled && _time.GetElapsedTime(_lastProgress) >= StallTimeout && _queued + _unacked >= WindowBytes)
                {
                    _unthrottled = true;
                    Stalls++;
                }
            }
        }
    }

    public void Dispose()
    {
        TaskCompletionSource? release;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            release = _room;
            _room = null;
        }
        _queue.Writer.TryComplete();
        release?.TrySetResult();
    }

    private async Task RunAsync()
    {
        var batch = new MemoryStream();
        var reader = _queue.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var item))
            {
                if (item is null)
                {
                    Flush(batch);
                    _post("{\"t\":\"reset\"}");
                    continue;
                }
                var offset = 0;
                while (offset < item.Length)
                {
                    var take = Math.Min(item.Length - offset, BatchBytes - (int)batch.Length);
                    batch.Write(item, offset, take);
                    offset += take;
                    if (batch.Length >= BatchBytes)
                    {
                        Flush(batch);
                    }
                }
            }
            Flush(batch);
        }
    }

    private void Flush(MemoryStream batch)
    {
        var n = (int)batch.Length;
        if (n == 0)
        {
            return;
        }
        var b64 = Convert.ToBase64String(batch.GetBuffer(), 0, n);
        batch.SetLength(0);
        lock (_lock)
        {
            _queued -= n;
            _unacked += n;
        }
        _post("{\"t\":\"out\",\"b\":\"" + b64 + "\"}");
    }
}
