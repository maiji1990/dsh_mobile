using System.Text;
using DshMobile.Application.Shadow;
using DshMobile.Application.Terminal;
using DshMobile.Native;
using DshMobile.Runtime.Guest;

namespace DshMobile.Application.Tests;

/// <summary>
/// PR-06: the PTY device probe and the C# PTY session lifecycle, driven by a scripted shell on a fake
/// <see cref="IPtyHandle"/>. The real PTY (libdshmobile_native) is only exercised on the device; these tests pin
/// what the probe concludes from what it sees, and that teardown hangs up → kills → closes.
/// </summary>
public class PtyProbeTests
{
    private const double Fast = 0.05;

    /// <summary>A tiny line-discipline + bash imitation: echo, prompt, tty/stty, foreground sleep, ^C, exit.</summary>
    private sealed class FakeShell : IPtyHandle
    {
        // Like the device's bash: bracketed-paste on before each prompt, off right after the command's newline.
        private const string Prompt = "\u001b[?2004hroot@localhost:~# ";
        private const string PasteOff = "\u001b[?2004l\r";
        private readonly Lock _lock = new();
        private readonly Queue<byte> _out = new();
        private readonly StringBuilder _line = new();
        private PtyExit? _exit;
        private bool _sleeping;
        private int _status;

        public FakeShell(bool prompt = true)
        {
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

        public int Pid => 4242;

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
                        return n;
                    }
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

    private static Task<PtyProbeReport> Probe(FakeShell shell) =>
        PtyProbe.RunAsync(
            _ => Task.FromResult(new ProotPtySession(shell, "test-pty", ["proot", "/usr/bin/bash"])),
            _ => [4243],
            _ => !shell.Exited,
            CancellationToken.None,
            Fast);

    [Fact]
    public async Task WellBehavedShell_PassesEveryStep_AndExitsByItself()
    {
        var shell = new FakeShell();
        var r = await Probe(shell);
        Assert.True(r.Ok, r.Summary);
        Assert.Equal(["open", "prompt", "tty", "resize", "ctrl-c", "exit", "no-orphans"], r.Steps.Select(s => s.Name));
        Assert.All(r.Steps, s => Assert.True(s.Ok, s.Name));
        Assert.Equal((30, 100), (shell.Rows, shell.Columns));
        Assert.Contains("130", r.Steps.Single(s => s.Name == "ctrl-c").Detail, StringComparison.Ordinal);
        Assert.Equal("exit 0", r.Steps.Single(s => s.Name == "exit").Detail);
        Assert.Empty(shell.Signals); // exit ended it; no hangup needed
        Assert.True(shell.Disposed);
        Assert.StartsWith("PASS: open ok", r.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IgnoredCtrlC_FailsThatStep_AndTheSessionIsHungUp()
    {
        var shell = new FakeShell { IgnoreInterrupt = true };
        var r = await Probe(shell);
        Assert.False(r.Ok);
        var step = r.Steps.Single(s => s.Name == "ctrl-c");
        Assert.False(step.Ok);
        Assert.Equal("sleep not interrupted", step.Detail);
        Assert.False(r.Steps.Single(s => s.Name == "exit").Ok);
        Assert.False(r.Steps.Single(s => s.Name == "no-orphans").Ok);
        Assert.Equal([NativePty.SigHup], shell.Signals);
        Assert.True(shell.Disposed);
        Assert.StartsWith("FAIL", r.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoPrompt_StopsAtPrompt_WithTheTailAsEvidence()
    {
        var shell = new FakeShell(prompt: false);
        var r = await Probe(shell);
        Assert.False(r.Ok);
        Assert.Equal(["open", "prompt"], r.Steps.Select(s => s.Name));
        Assert.StartsWith("no prompt", r.Steps[1].Detail, StringComparison.Ordinal);
        Assert.Equal([NativePty.SigHup], shell.Signals);
    }

    [Fact]
    public async Task OpenFailure_IsReportedWithItsStableCode()
    {
        var r = await PtyProbe.RunAsync(
            _ => throw new GuestPtyException("RUNTIME_NOT_INSTALLED", "no runtime rootfs"),
            _ => [],
            _ => false,
            CancellationToken.None,
            Fast);
        Assert.False(r.Ok);
        var step = Assert.Single(r.Steps);
        Assert.Equal("open", step.Name);
        Assert.Contains("RUNTIME_NOT_INSTALLED", step.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispose_WhenHangupIsIgnored_EscalatesToKill_ThenCloses()
    {
        var shell = new FakeShell { IgnoreHangup = true };
        var session = new ProotPtySession(shell, "t", []);
        await session.DisposeAsync();
        Assert.Equal([NativePty.SigHup, NativePty.SigKill], shell.Signals);
        Assert.True(shell.Disposed);
        Assert.Equal(new PtyExit(true, NativePty.SigKill), await session.Completion);
        Assert.Throws<ObjectDisposedException>(() => session.Stream.Write([0x61]));
        Assert.Equal(0, session.Stream.Read(new byte[8]));
        await session.DisposeAsync(); // idempotent
        Assert.Equal(2, shell.Signals.Count);
    }

    [Fact]
    public async Task Stream_ReturnsEndOfStream_AfterTheShellExits()
    {
        var shell = new FakeShell();
        await using var session = new ProotPtySession(shell, "t", []);
        session.Stream.Write("exit\r"u8);
        var all = new MemoryStream();
        await session.Stream.CopyToAsync(all);
        Assert.Contains("logout", Encoding.UTF8.GetString(all.ToArray()), StringComparison.Ordinal);
        Assert.Equal(new PtyExit(false, 0), await session.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ResizeAsync(0, 24, CancellationToken.None));
    }

    [Fact]
    public void Plain_RemovesBracketedPasteAndCsi_SoDeviceOutputMatches()
    {
        // Verbatim from the first device run (2026-11-12, PTP-AN10): "30 100" was there, behind ESC[?2004l.
        const string Seen = "\u001b[K\rroot@localhost:~# stty size\r\n\u001b[?2004l\r30 100\r\n\u001b[?2004hroot@localhost:~# ";
        Assert.DoesNotContain("\n30 100", Seen, StringComparison.Ordinal);
        Assert.Contains("\n30 100", PtyProbe.Plain(Seen), StringComparison.Ordinal);
        Assert.Equal("root@localhost:~# stty size ⏎ 30 100 ⏎ root@localhost:~#", PtyProbe.Printable(Seen));
        Assert.Equal("ab", PtyProbe.Plain("a\u001b]0;title\u0007b"));
    }

    // ── terminal argv shadow ─────────────────────────────────────────────

    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;
    private static readonly string[] Shell = ["/data/app/x/lib/arm64/libproot.so", "--kill-on-exit", "-0", "--rootfs=/r", "--cwd=/root", "/usr/bin/bash", "--noprofile", "--login"];

    [Fact]
    public void IsTerminalShell_OnlyMatchesTheLoginShellPRoot()
    {
        Assert.True(TerminalArgvShadowCheck.IsTerminalShell(Shell));
        Assert.False(TerminalArgvShadowCheck.IsTerminalShell(["/system/bin/sh", "/usr/bin/bash", "--noprofile", "--login"]));
        Assert.False(TerminalArgvShadowCheck.IsTerminalShell(["/x/libproot.so", "--rootfs=/r", "/usr/bin/bash", "-c", "echo keepalive"]));
        Assert.False(TerminalArgvShadowCheck.IsTerminalShell(["/x/libproot.so", "--login"]));
    }

    [Fact]
    public void NoLegacySession_IsInconclusive()
    {
        var r = TerminalArgvShadowCheck.Compare([], Shell, 1, At);
        Assert.Equal(ShadowVerdict.Inconclusive, r.Verdict);
    }

    [Fact]
    public void CSharpWithoutRootfs_WhileLegacyRuns_IsDiverge()
    {
        var r = TerminalArgvShadowCheck.Compare([Shell], null, 1, At);
        Assert.Equal(ShadowVerdict.Diverge, r.Verdict);
        Assert.Contains("no runtime rootfs", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void EverySessionIdentical_IsAgree_AndOneDifferentSessionIsNamed()
    {
        Assert.Equal(ShadowVerdict.Agree, TerminalArgvShadowCheck.Compare([Shell, Shell], Shell, 1, At).Verdict);
        string[] other = [.. Shell];
        other[3] = "--rootfs=/other";
        var r = TerminalArgvShadowCheck.Compare([Shell, other], Shell, 1, At);
        Assert.Equal(ShadowVerdict.Diverge, r.Verdict);
        Assert.StartsWith("session 2: arg 3", r.Detail, StringComparison.Ordinal);
    }
}
