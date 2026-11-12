using System.Collections.Concurrent;
using System.Text;
using DshMobile.Application.Runtime;
using DshMobile.Application.Terminal;
using DshMobile.Native;
using DshMobile.Runtime;
using DshMobile.Runtime.Guest;
using DshMobile.Runtime.Journal;

namespace DshMobile.Application.Tests;

/// <summary>
/// PR-06 C# terminal sessions: the legacy DshTerminalManager rules (numbering, active session, close, natural exit,
/// stopAll), output kept for re-attaching, and the extra-keys / CTRL mapping. Shells are <see cref="FakeShell"/>s.
/// </summary>
public sealed class TerminalSessionsTests
{
    private readonly List<FakeShell> _shells = [];
    private readonly List<string> _log = [];
    private int _nextPid = 5000;

    private Task<ProotPtySession> Open(int rows, int columns, CancellationToken ct)
    {
        var shell = new FakeShell(pid: _nextPid++);
        shell.Resize(rows, columns);
        _shells.Add(shell);
        return Task.FromResult(new ProotPtySession(shell, "t" + shell.Pid, ["proot"]));
    }

    private TerminalSessions Sessions() => new(Open, _log.Add);

    private static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }
        Assert.True(condition(), "timed out waiting for " + what);
    }

    [Fact]
    public async Task Create_NumbersFromOne_AndTheNewSessionIsActive()
    {
        await using var s = Sessions();
        var changed = 0;
        s.Changed += () => Interlocked.Increment(ref changed);
        await s.CreateAsync(24, 80, CancellationToken.None);
        await s.CreateAsync(24, 80, CancellationToken.None);
        var third = await s.CreateAsync(30, 100, CancellationToken.None);
        Assert.Equal(["1", "2", "3"], s.Snapshot().Select(i => i.Id));
        Assert.Equal(["终端 1", "终端 2", "终端 3"], s.Snapshot().Select(i => i.Title));
        Assert.Equal("3", s.ActiveId);
        Assert.True(third.Active && third.Running);
        Assert.Equal((30, 100), (_shells[2].Rows, _shells[2].Columns));
        Assert.Equal(3, changed);
        Assert.Equal([5000, 5001, 5002], s.Pids);
    }

    [Fact]
    public async Task Close_ActivatesFirstSurvivor_IdsNeverReused_OrderRestartsOnlyWhenEmpty()
    {
        await using var s = Sessions();
        await s.CreateAsync(24, 80, CancellationToken.None);
        await s.CreateAsync(24, 80, CancellationToken.None);
        await s.CreateAsync(24, 80, CancellationToken.None);
        Assert.True(await s.CloseAsync("3"));
        Assert.Equal("1", s.ActiveId); // first survivor, as upstream
        Assert.Equal([NativePty.SigHup], _shells[2].Signals);
        Assert.True(_shells[2].Disposed);
        var fourth = await s.CreateAsync(24, 80, CancellationToken.None);
        Assert.Equal(("4", "终端 4"), (fourth.Id, fourth.Title)); // order keeps counting while windows remain
        Assert.False(await s.CloseAsync("3"));
        Assert.Equal(3, await s.CloseAllAsync());
        Assert.Null(s.ActiveId);
        Assert.Empty(s.Pids);
        var fresh = await s.CreateAsync(24, 80, CancellationToken.None);
        Assert.Equal(("5", "终端 1"), (fresh.Id, fresh.Title)); // 序号归一, id still unique
        Assert.Contains(_log, l => l == "terminal.csharp: session 3 closed (shell signal 1)");
    }

    [Fact]
    public async Task ShellExitingByItself_StaysListedWithItsStatus_AndTakesNoMoreInput()
    {
        await using var s = Sessions();
        await s.CreateAsync(24, 80, CancellationToken.None);
        Assert.True(s.Write("1", "exit\r"u8));
        await Until(() => s.Snapshot()[0].Exited, "exit");
        var info = s.Snapshot().Single();
        Assert.Equal((false, true, 0, (int?)null), (info.Running, info.Exited, info.ExitCode, info.Signal));
        Assert.Empty(s.Pids);
        Assert.False(s.Write("1", "ls\r"u8));
        Assert.False(await s.ResizeAsync("1", 100, 30, CancellationToken.None));
        await Until(() => _shells[0].Disposed, "master released");
        Assert.Empty(_shells[0].Signals); // it ended by itself: no hangup was needed
        Assert.True(await s.CloseAsync("1"));
        Assert.Empty(s.Snapshot());
    }

    [Fact]
    public async Task Attach_ReplaysKeptOutputFirst_ThenLiveOutputInOrder_UntilDetached()
    {
        await using var s = Sessions();
        await s.CreateAsync(24, 80, CancellationToken.None);
        s.Write("1", "stty size\r"u8);
        await Task.Delay(300);
        var seen = new ConcurrentQueue<byte[]>();
        var attachment = s.Attach("1", seen.Enqueue)!;
        Assert.Contains("24 80", Encoding.UTF8.GetString(seen.First()), StringComparison.Ordinal); // replay
        s.Write("1", "echo DSHM-INT-$?\r"u8);
        await Until(() => Encoding.UTF8.GetString(seen.SelectMany(b => b).ToArray()).Contains("DSHM-INT-0", StringComparison.Ordinal), "live output");
        var text = Encoding.UTF8.GetString(seen.SelectMany(b => b).ToArray());
        Assert.True(text.IndexOf("24 80", StringComparison.Ordinal) < text.IndexOf("DSHM-INT-0", StringComparison.Ordinal));
        attachment.Dispose();
        var before = seen.Count;
        s.Write("1", "stty size\r"u8);
        await Task.Delay(300);
        Assert.Equal(before, seen.Count);
        Assert.Null(s.Attach("9", _ => { }));
    }

    [Fact]
    public async Task KeptOutput_IsTheLast256KiB()
    {
        await using var s = Sessions();
        await s.CreateAsync(24, 80, CancellationToken.None);
        s.Write("1", "flood 300000\r"u8);
        var replay = Array.Empty<byte>();
        await Until(() =>
        {
            using var a = s.Attach("1", b => replay = b);
            return Encoding.ASCII.GetString(replay).Contains("FLOOD-END", StringComparison.Ordinal);
        }, "flood output");
        Assert.Equal(TerminalSessions.ScrollbackBytes, replay.Length);
        Assert.EndsWith("FLOOD-END\r\n\u001b[?2004hroot@localhost:~# ", Encoding.ASCII.GetString(replay), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resize_ReachesTheShell_AndOpenFailure_LeavesTheListUntouched()
    {
        await using var s = Sessions();
        await s.CreateAsync(24, 80, CancellationToken.None);
        Assert.True(await s.ResizeAsync("1", 100, 30, CancellationToken.None));
        Assert.Equal((30, 100), (_shells[0].Rows, _shells[0].Columns));
        Assert.False(await s.ResizeAsync("1", 0, 30, CancellationToken.None));

        await using var failing = new TerminalSessions((_, _, _) => throw new GuestPtyException(RuntimeErrorCodes.NotInstalled, "no rootfs"));
        var e = await Assert.ThrowsAsync<GuestPtyException>(() => failing.CreateAsync(24, 80, CancellationToken.None));
        Assert.Equal(RuntimeErrorCodes.NotInstalled, e.Code);
        Assert.Empty(failing.Snapshot());
    }

    [Fact]
    public async Task Activate_IgnoresUnknownIds()
    {
        await using var s = Sessions();
        await s.CreateAsync(24, 80, CancellationToken.None);
        await s.CreateAsync(24, 80, CancellationToken.None);
        s.Activate("1");
        Assert.Equal("1", s.ActiveId);
        s.Activate("7");
        Assert.Equal("1", s.ActiveId);
        Assert.Equal([true, false], s.Snapshot().Select(i => i.Active));
    }

    // ── coordinator: runtime stop closes the C# sessions (legacy sandbox.stop → stopAll) ──

    private sealed class Log : IDiagnosticsLog, Shadow.IShadowReportSink
    {
        public readonly List<string> Lines = [];

        public void Note(string line) => Lines.Add(line);

        public void Report(Shadow.ShadowReport report)
        {
        }
    }

    private sealed class Foreground : IForegroundServiceController
    {
        public void Acquire()
        {
        }

        public void Release()
        {
        }
    }

    [Fact]
    public async Task CoordinatorStop_ClosesCSharpSessions_BeforeTheSandboxStops()
    {
        var runtime = new StoppableRuntime();
        var log = new Log();
        using var coordinator = new RuntimeCoordinator(runtime, new RuntimeStateMachine(new InMemoryRuntimeJournal(), "fake"), new Foreground(), log, log,
            new MigrationOptions(), terminalOpener: Open);
        await coordinator.StartAsync(DshProfile.Web, CancellationToken.None);
        await coordinator.Terminals!.CreateAsync(24, 80, CancellationToken.None);
        await coordinator.Terminals.CreateAsync(24, 80, CancellationToken.None);
        runtime.OnSandboxStop = () => runtime.SessionsAtSandboxStop = coordinator.Terminals.Snapshot().Count;
        var r = await coordinator.StopAsync(CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal(0, runtime.SessionsAtSandboxStop);
        Assert.All(_shells, sh => Assert.Equal([NativePty.SigHup], sh.Signals));
        Assert.Contains("terminal.csharp: closed 2 session(s) on stop", log.Lines);
    }

    [Fact]
    public async Task CoordinatorStop_WithoutAStartedSandbox_StillClosesCSharpSessions()
    {
        // The C# terminal does not need a started sandbox; "stop: nothing running" must not leave its shells alive.
        var runtime = new StoppableRuntime();
        var log = new Log();
        using var coordinator = new RuntimeCoordinator(runtime, new RuntimeStateMachine(new InMemoryRuntimeJournal(), "fake"), new Foreground(), log, log,
            new MigrationOptions(), terminalOpener: Open);
        await coordinator.Terminals!.CreateAsync(24, 80, CancellationToken.None);
        await coordinator.Terminals.CreateAsync(24, 80, CancellationToken.None);
        var r = await coordinator.StopAsync(CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Empty(coordinator.Terminals.Snapshot());
        Assert.Equal(2, _shells.Count);
        Assert.All(_shells, sh => Assert.Equal([NativePty.SigHup], sh.Signals));
        Assert.Equal(["terminal.csharp: closed 2 session(s) on stop", "stop: nothing running"],
            log.Lines.Where(l => l.Contains("on stop", StringComparison.Ordinal) || l.StartsWith("stop:", StringComparison.Ordinal)));
    }

    private sealed class StoppableRuntime : IRuntimeManager
    {
        private RuntimeState _state = RuntimeState.Ready;

        public Action? OnSandboxStop { get; set; }

        public int? SessionsAtSandboxStop { get; set; }

        public string Implementation => "fake";

        private RuntimeStartResult Ok() => new(true, "OK", null, new RuntimeSnapshot(_state, HarnessState.Stopped, 0, null, null, null, DateTimeOffset.UnixEpoch));

        public Task<RuntimeSnapshot> GetStateAsync(CancellationToken ct = default) => Task.FromResult(Ok().Snapshot);

        public Task<RuntimeInstallResult> InstallAsync(RuntimePackage package, IProgress<RuntimeProgress>? progress, CancellationToken ct) =>
            Task.FromResult(new RuntimeInstallResult(true, "OK", null));

        public Task<RuntimeInstallResult> PrepareAsync(CancellationToken ct) => Task.FromResult(new RuntimeInstallResult(true, "OK", null));

        public Task<RuntimeStartResult> StartSandboxAsync(CancellationToken ct)
        {
            _state = RuntimeState.Running;
            return Task.FromResult(Ok());
        }

        public Task<RuntimeStartResult> StartHarnessAsync(DshProfile profile, CancellationToken ct) => Task.FromResult(Ok());

        public Task<RuntimeStartResult> StopHarnessAsync(CancellationToken ct) => Task.FromResult(Ok());

        public Task<RuntimeStartResult> StopSandboxAsync(CancellationToken ct)
        {
            OnSandboxStop?.Invoke();
            _state = RuntimeState.Ready;
            return Task.FromResult(Ok());
        }
    }
}

public class TerminalKeysTests
{
    [Theory]
    [InlineData("ESC", false, "\u001b")]
    [InlineData("TAB", false, "\t")]
    [InlineData("UP", false, "\u001b[A")]
    [InlineData("UP", true, "\u001bOA")]
    [InlineData("DOWN", true, "\u001bOB")]
    [InlineData("RIGHT", false, "\u001b[C")]
    [InlineData("LEFT", true, "\u001bOD")]
    [InlineData("HOME", false, "\u001b[H")]
    [InlineData("HOME", true, "\u001bOH")]
    [InlineData("END", false, "\u001b[F")]
    [InlineData("END", true, "\u001bOF")]
    [InlineData("PGUP", true, "\u001b[5~")]
    [InlineData("PGDN", false, "\u001b[6~")]
    [InlineData("CTRL_C", false, "\u0003")]
    [InlineData("ENTER", false, "\r")]
    [InlineData("BACKSPACE", false, "\u007f")]
    [InlineData("DELETE", true, "\u001b[3~")]
    [InlineData("INSERT", false, "\u001b[2~")]
    public void BarKey_IsTheXtermSequence(string key, bool applicationCursor, string expected) =>
        Assert.Equal(expected, TerminalKeys.Sequence(key, applicationCursor));

    [Fact]
    public void EveryBarKey_HasASequence_AndUnknownKeysHaveNone()
    {
        Assert.All(TerminalKeys.Bar, k => Assert.NotNull(TerminalKeys.Sequence(k.Key, false)));
        Assert.Null(TerminalKeys.Sequence("F13", false));
    }

    [Theory]
    [InlineData("c", "\u0003", true)]
    [InlineData("C", "\u0003", true)]
    [InlineData("z", "\u001a", true)]
    [InlineData("1", "1", false)]
    [InlineData("1a", "1\u0001", true)]
    [InlineData("ab", "\u0001b", true)]
    [InlineData("中", "中", false)]
    public void LatchedCtrl_TurnsTheNextLetterIntoAControlCode(string typed, string output, bool released) =>
        Assert.Equal((output, released), TerminalKeys.ApplyCtrl(typed));
}
