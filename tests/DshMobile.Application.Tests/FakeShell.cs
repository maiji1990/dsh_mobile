using System.Text;
using DshMobile.Native;

namespace DshMobile.Application.Tests;

/// <summary>A tiny line-discipline + bash imitation: echo, prompt, tty/stty, foreground sleep, ^C, exit.</summary>
internal sealed class FakeShell : IPtyHandle
{
    // Like the device's bash: bracketed-paste on before each prompt, off right after the command's newline.
    private const string Prompt = "\u001b[?2004hroot@localhost:~# ";
    private const string PasteOff = "\u001b[?2004l\r";
    private readonly Lock _lock = new();
    private readonly Queue<byte> _out = new();
    private readonly StringBuilder _line = new();
    private PtyExit? _exit;
    private bool _readerWaiting;
    private bool _sleeping;
    private int _status;

    public FakeShell(bool prompt = true, int pid = 4242)
    {
        Pid = pid;
        if (prompt)
        {
            Emit(Prompt);
        }
    }

    public bool IgnoreInterrupt { get; init; }

    public bool IgnoreHangup { get; init; }

    public int Rows { get; private set; } = 24;

    public int Columns { get; private set; } = 80;

    public List<int> Signals { get; } = [];

    public bool Disposed { get; private set; }

    public bool Exited
    {
        get
        {
            lock (_lock)
            {
                return _exit is not null;
            }
        }
    }

    public int Pid { get; }

    /// <summary>
    /// Nothing in flight: every byte the shell emitted has been read, and the reader is back inside <see cref="Read"/>
    /// waiting for more (a reader calls Read again only after it handled the previous chunk) or the shell has exited.
    /// The handshake that lets a test advance a fake clock without starving a step that should pass.
    /// </summary>
    public bool Quiescent
    {
        get
        {
            lock (_lock)
            {
                return _out.Count == 0 && (_readerWaiting || _exit is not null);
            }
        }
    }

    private void Emit(string s)
    {
        lock (_lock)
        {
            foreach (var b in Encoding.UTF8.GetBytes(s))
            {
                _out.Enqueue(b);
            }
        }
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            lock (_lock)
            {
                if (_out.Count > 0)
                {
                    var n = 0;
                    while (n < buffer.Length && _out.Count > 0)
                    {
                        buffer[n++] = _out.Dequeue();
                    }
                    _readerWaiting = false;
                    return n;
                }
                _readerWaiting = true;
                if (_exit is not null)
                {
                    return 0;
                }
            }
            if (Environment.TickCount64 >= deadline)
            {
                return -1;
            }
            Thread.Sleep(5);
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            if (b == 0x03)
            {
                if (_sleeping && IgnoreInterrupt)
                {
                    continue;
                }
                _sleeping = false;
                _status = 130;
                _line.Clear();
                Emit("^C\r\n" + Prompt);
            }
            else if (b == '\r')
            {
                Emit("\r\n");
                var cmd = _line.ToString();
                _line.Clear();
                if (!_sleeping)
                {
                    Emit(PasteOff);
                    Run(cmd);
                }
            }
            else
            {
                _line.Append((char)b);
                Emit(((char)b).ToString());
            }
        }
    }

    private void Run(string cmd)
    {
        switch (cmd)
        {
            case "tty; stty size; echo DSHM-$((6*7))":
                Emit($"/dev/pts/3\r\n{Rows} {Columns}\r\nDSHM-42\r\n{Prompt}");
                _status = 0;
                break;
            case "stty size":
                Emit($"{Rows} {Columns}\r\n{Prompt}");
                break;
            case var flood when flood.StartsWith("flood ", StringComparison.Ordinal):
                // flood N: N bytes of a repeating 0-9 pattern, then a marker line
                var n = int.Parse(flood[6..], System.Globalization.CultureInfo.InvariantCulture);
                var sb = new StringBuilder(n + 64);
                for (var i = 0; i < n; i++)
                {
                    sb.Append((char)('0' + (i % 10)));
                }
                Emit(sb.Append("\r\nFLOOD-END\r\n").Append(Prompt).ToString());
                break;
            case "sleep 30":
                _sleeping = true;
                break;
            case "echo DSHM-INT-$?":
                Emit($"DSHM-INT-{_status}\r\n{Prompt}");
                _status = 0;
                break;
            case "exit":
                Emit("logout\r\n");
                lock (_lock)
                {
                    _exit = new PtyExit(false, 0);
                }
                break;
            default:
                Emit($"bash: {cmd}: command not found\r\n{Prompt}");
                _status = 127;
                break;
        }
    }

    public void Resize(int rows, int columns)
    {
        Rows = rows;
        Columns = columns;
        if (!_sleeping)
        {
            Emit("\u001b[K\r" + Prompt); // readline redraws the prompt on SIGWINCH
        }
    }

    public PtyExit? TryWait()
    {
        lock (_lock)
        {
            return _exit;
        }
    }

    public void SignalGroup(int signal)
    {
        lock (_lock)
        {
            Signals.Add(signal);
            if (signal == NativePty.SigKill || (signal == NativePty.SigHup && !IgnoreHangup))
            {
                _exit ??= new PtyExit(true, signal);
            }
        }
    }

    public void Dispose() => Disposed = true;
}
