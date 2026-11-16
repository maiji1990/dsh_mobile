using System.Diagnostics;
using System.Text;
using DshMobile.Application.Terminal;
using DshMobile.Native;
using DshMobile.Runtime.Guest;
using Microsoft.Extensions.Time.Testing;

namespace DshMobile.Application.Tests;

/// <summary>
/// PR-06: the PTY device probe and the C# PTY session lifecycle, driven by a scripted shell on a fake
/// <see cref="IPtyHandle"/>. The real PTY (libdshmobile_native) is only exercised on the device; these tests pin
/// what the probe concludes from what it sees, and that teardown hangs up → kills → closes.
/// <para>No wall-clock timeouts: the probe runs on a <see cref="FakeTimeProvider"/> that is advanced only while the
/// fake shell is quiescent (all output read, reader waiting) and the session is not mid-reap. A step that should pass
/// therefore cannot time out on a busy runner (CI run 43784510013 lost the 1.5 s prompt wait that way); a step that
/// should fail times out as soon as nothing else can happen.</para>
/// </summary>
public class PtyProbeTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private static async Task<PtyProbeReport> Probe(FakeShell shell)
    {
        var clock = new FakeTimeProvider();
        ProotPtySession? session = null;
        var run = PtyProbe.RunAsync(
            _ => Task.FromResult(session = new ProotPtySession(shell, "test-pty", ["proot", "/usr/bin/bash"])),
            _ => [4243],
            _ => !shell.Exited,
            CancellationToken.None,
            clock);
        var guard = Stopwatch.StartNew();
        while (!run.IsCompleted)
        {
            // The real session reaps on its own 100 ms poll: an exited shell is not quiescent until that was observed.
            var reaping = shell.Exited && session is { Completion.IsCompleted: false };
            if (shell.Quiescent && !reaping)
            {
                clock.Advance(Tick);
            }
            await Task.WhenAny(run, Task.Delay(5));
            Assert.True(guard.Elapsed < TimeSpan.FromMinutes(5), "probe did not finish (hang guard, not a timeout of the probe)");
        }
        return await run;
    }

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
            CancellationToken.None);
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
}
