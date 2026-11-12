using DshMobile.Native;

namespace DshMobile.Runtime.Guest;

/// <summary>
/// An interactive guest login shell on a PTY owned by C# (PR-06): bytes flow through <see cref="Stream"/> (never the
/// JSON control plane); ^C is the byte 0x03 written to it (the PTY line discipline turns it into SIGINT for the
/// foreground job, exactly like a real terminal). Disposing hangs up the session (SIGHUP to the process group), then
/// kills it, then closes the master.
/// </summary>
public sealed class ProotPtySession : GuestPtySession
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan HangupGrace = TimeSpan.FromSeconds(2);

    private readonly IPtyHandle _pty;
    private readonly PtyStream _stream;
    private readonly Task<PtyExit> _completion;
    private int _disposed;

    public ProotPtySession(IPtyHandle pty, string id, IReadOnlyList<string> argv)
    {
        _pty = pty ?? throw new ArgumentNullException(nameof(pty));
        Id = id;
        Argv = argv;
        _stream = new PtyStream(pty);
        _completion = WatchAsync();
    }

    public override string Id { get; }

    public override Stream Stream => _stream;

    public int Pid => _pty.Pid;

    /// <summary>The argv the session was started with (for logs / shadow).</summary>
    public IReadOnlyList<string> Argv { get; }

    /// <summary>Completes when the shell (the PRoot session leader) has been reaped.</summary>
    public Task<PtyExit> Completion => _completion;

    public override Task ResizeAsync(int columns, int rows, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        _pty.Resize(rows, columns);
        return Task.CompletedTask;
    }

    private async Task<PtyExit> WatchAsync()
    {
        // Keeps reaping after dispose was requested too (the child is being killed), so it never lingers as a zombie.
        while (true)
        {
            if (_pty.TryWait() is { } exit)
            {
                return exit;
            }
            await Task.Delay(PollInterval).ConfigureAwait(false);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        if (!_completion.IsCompleted)
        {
            _pty.SignalGroup(NativePty.SigHup);
            if (!await CompletesWithin(HangupGrace).ConfigureAwait(false))
            {
                _pty.SignalGroup(NativePty.SigKill);
                await CompletesWithin(HangupGrace).ConfigureAwait(false);
            }
        }
        _stream.MarkClosed();
        _pty.Dispose();
    }

    private async Task<bool> CompletesWithin(TimeSpan t)
    {
        try
        {
            await _completion.WaitAsync(t).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (NativePtyException)
        {
            return true; // waitpid itself failed: nothing left to wait for
        }
    }

    /// <summary>Blocking reads poll in short slices so a closed session never strands a reader thread.</summary>
    private sealed class PtyStream(IPtyHandle pty) : Stream
    {
        private const int SliceMs = 200;
        private volatile bool _closed;

        public void MarkClosed() => _closed = true;

        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            while (!_closed)
            {
                var n = pty.Read(buffer, SliceMs);
                if (n >= 0)
                {
                    return n;
                }
            }
            return 0;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Task.Factory.StartNew(() =>
            {
                while (!_closed)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var n = pty.Read(buffer.Span, SliceMs);
                    if (n >= 0)
                    {
                        return n;
                    }
                }
                return 0;
            }, cancellationToken, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            pty.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>Opening a PTY session failed; <see cref="Code"/> is a stable error code (RUNTIME_NOT_INSTALLED, NATIVE_*, …).</summary>
public sealed class GuestPtyException(string code, string message) : Exception($"{code}: {message}")
{
    public string Code { get; } = code;
}
