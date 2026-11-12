using System.Runtime.InteropServices;

namespace DshMobile.Native;

/// <summary>How a PTY child ended.</summary>
public readonly record struct PtyExit(bool Signaled, int Value)
{
    public int? ExitCode => Signaled ? null : Value;

    public int? Signal => Signaled ? Value : null;

    public override string ToString() => Signaled ? $"signal {Value}" : $"exit {Value}";
}

/// <summary>The PTY operations a terminal session needs (the native one in production, a fake in tests).</summary>
public interface IPtyHandle : IDisposable
{
    int Pid { get; }

    /// <summary>Bytes read; 0 at end of stream; -1 when nothing arrived within <paramref name="timeoutMs"/>.</summary>
    int Read(Span<byte> buffer, int timeoutMs);

    void Write(ReadOnlySpan<byte> data);

    void Resize(int rows, int columns);

    /// <summary>Reaps the child if it has ended (never blocks).</summary>
    PtyExit? TryWait();

    /// <summary>Sends <paramref name="signal"/> to the child's process group (it is a session leader).</summary>
    void SignalGroup(int signal);
}

/// <summary>
/// A child process on its own PTY, started by <c>dshm_pty_spawn</c> in libdshmobile_native.so: fork + setsid +
/// controlling terminal + execve happen in C, because managed code must not run between fork and exec.
/// Errors are <see cref="NativePtyException"/> carrying errno; nothing is reported as success that did not happen.
/// </summary>
public sealed partial class NativePty : IPtyHandle
{
    public const int SigHup = 1;
    public const int SigInt = 2;
    public const int SigKill = 9;
    public const int SigTerm = 15;

    private const int EAGAIN = 11;
    private const int ECHILD = 10;

    private int _fd;
    private PtyExit? _exit;

    private NativePty(int fd, int pid)
    {
        _fd = fd;
        Pid = pid;
    }

    public int Pid { get; }

    /// <summary>
    /// Starts <paramref name="path"/> with exactly <paramref name="argv"/> (argv[0] included) and the complete
    /// environment <paramref name="environment"/> (<c>KEY=value</c>) on a new <paramref name="rows"/>×<paramref name="columns"/> PTY.
    /// </summary>
    public static NativeResult<NativePty> Spawn(string path, IReadOnlyList<string> argv, IReadOnlyList<string> environment, string? workingDirectory, int rows, int columns)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentNullException.ThrowIfNull(environment);
        if (argv.Count == 0)
        {
            return NativeResult<NativePty>.Fail("BAD_REQUEST", "argv must contain argv[0]");
        }
        var probe = DshNativeLibrary.Probe();
        if (!probe.Ok)
        {
            return NativeResult<NativePty>.Fail(probe.Code, probe.Message ?? probe.Code);
        }
        using var argBlock = new Utf8Block(argv);
        using var envBlock = new Utf8Block(environment);
        var fd = DshNativeLibrary.PtySpawn(path, argBlock.Pointer, envBlock.Pointer, workingDirectory, rows, columns, out var pid, out var errno);
        return fd < 0
            ? NativeResult<NativePty>.Fail(NativeErrorCodes.SpawnFailed, $"{path}: errno {errno} ({Errno.Describe(errno)})")
            : NativeResult<NativePty>.Success(new NativePty(fd, pid));
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        var fd = Volatile.Read(ref _fd);
        if (fd < 0)
        {
            return 0;
        }
        var n = DshNativeLibrary.Read(fd, buffer, timeoutMs);
        return n switch
        {
            >= 0 => n,
            -EAGAIN => -1,
            _ => throw new NativePtyException("read", -n),
        };
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        var fd = Volatile.Read(ref _fd);
        ObjectDisposedException.ThrowIf(fd < 0, this);
        var n = DshNativeLibrary.Write(fd, data);
        if (n < 0)
        {
            throw new NativePtyException("write", -n);
        }
    }

    public void Resize(int rows, int columns)
    {
        var fd = Volatile.Read(ref _fd);
        ObjectDisposedException.ThrowIf(fd < 0, this);
        var r = DshNativeLibrary.PtyResize(fd, rows, columns);
        if (r < 0)
        {
            throw new NativePtyException("resize", -r);
        }
    }

    public PtyExit? TryWait()
    {
        if (_exit is { } done)
        {
            return done;
        }
        var r = DshNativeLibrary.Wait(Pid, 1, out var kind, out var value);
        if (r == 1)
        {
            _exit = new PtyExit(kind == 2, value);
        }
        else if (r == -ECHILD)
        {
            // Someone else reaped it (should not happen: .NET only reaps the children it started itself).
            _exit = new PtyExit(false, -1);
        }
        else if (r < 0)
        {
            throw new NativePtyException("waitpid", -r);
        }
        return _exit;
    }

    public void SignalGroup(int signal)
    {
        if (_exit is not null)
        {
            return;
        }
        var r = DshNativeLibrary.Kill(Pid, signal, 1);
        if (r < 0 && r != -3 /* ESRCH: already gone */)
        {
            throw new NativePtyException("kill", -r);
        }
    }

    /// <summary>Closes the master. Callers end the child first (<see cref="SignalGroup"/>); closing alone sends SIGHUP.</summary>
    public void Dispose()
    {
        var fd = Interlocked.Exchange(ref _fd, -1);
        if (fd >= 0)
        {
            _ = DshNativeLibrary.Close(fd);
        }
    }

    /// <summary>A NULL-terminated <c>char*[]</c> of UTF-8 strings in unmanaged memory.</summary>
    private sealed class Utf8Block : IDisposable
    {
        private readonly nint[] _strings;

        public Utf8Block(IReadOnlyList<string> values)
        {
            _strings = new nint[values.Count];
            Pointer = Marshal.AllocHGlobal(nint.Size * (values.Count + 1));
            for (var i = 0; i < values.Count; i++)
            {
                if (values[i].Contains('\0', StringComparison.Ordinal))
                {
                    throw new ArgumentException("argv/env entries must not contain NUL", nameof(values));
                }
                _strings[i] = Marshal.StringToCoTaskMemUTF8(values[i]);
                Marshal.WriteIntPtr(Pointer, i * nint.Size, _strings[i]);
            }
            Marshal.WriteIntPtr(Pointer, values.Count * nint.Size, 0);
        }

        public nint Pointer { get; }

        public void Dispose()
        {
            foreach (var s in _strings)
            {
                if (s != 0)
                {
                    Marshal.FreeCoTaskMem(s);
                }
            }
            Marshal.FreeHGlobal(Pointer);
        }
    }
}

public sealed class NativePtyException(string operation, int errno)
    : IOException($"{operation} failed: errno {errno} ({DshMobile.Native.Errno.Describe(errno)})")
{
    public int Errno { get; } = errno;
}

/// <summary>Linux errno names for messages (Android bionic uses the Linux numbering).</summary>
public static class Errno
{
    public static string Describe(int errno) => errno switch
    {
        1 => "EPERM",
        2 => "ENOENT",
        3 => "ESRCH",
        4 => "EINTR",
        5 => "EIO",
        9 => "EBADF",
        10 => "ECHILD",
        11 => "EAGAIN",
        12 => "ENOMEM",
        13 => "EACCES",
        20 => "ENOTDIR",
        22 => "EINVAL",
        24 => "EMFILE",
        25 => "ENOTTY",
        _ => "errno",
    };
}
