using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DshMobile.Runtime.Guest;

namespace DshMobile.Application.Runtime;

public sealed record PtyProbeStep(string Name, bool Ok, string Detail, long Ms);

public sealed record PtyProbeReport(bool Ok, IReadOnlyList<PtyProbeStep> Steps, string Transcript)
{
    public string Summary => (Ok ? "PASS" : "FAIL") + ": " + string.Join("; ", Steps.Select(s => $"{s.Name} {(s.Ok ? "ok" : "FAILED")} ({s.Detail}, {s.Ms} ms)"));
}

/// <summary>
/// PR-06 device acceptance for the C#-owned PTY (no UI): drives a real guest login shell through the PTY the way a
/// terminal does — prompt, <c>tty</c> / <c>stty size</c>, window resize, ^C (byte 0x03 → SIGINT via the line
/// discipline, exit status 130), <c>exit</c> → exit code 0 — then checks that no process of the session survived.
/// Every step reports what it saw; nothing is assumed.
/// <para>Every delay and timeout runs on the given <see cref="TimeProvider"/> (default: the system clock). Timeouts are
/// upper bounds; tests drive a fake clock and advance it only once the fake shell is quiescent, so a busy CI runner can
/// no longer make a step that should pass time out.</para>
/// </summary>
public static partial class PtyProbe
{
    public const string Slice = "runtime.pty-probe";

    [GeneratedRegex(@"[#$] $")]
    private static partial Regex PromptAtEnd();

    [GeneratedRegex(@"\x1b(?:\[[0-9;?]*[ -/]*[@-~]|\][^\x07\x1b]*(?:\x07|\x1b\\)|[@-Z\\-_])")]
    private static partial Regex EscapeSequence();

    /// <summary>
    /// What a terminal would show as text: escape sequences and CRs removed. Real bash wraps every command in
    /// bracketed-paste toggles (<c>ESC[?2004l</c> right after the newline) and redraws the prompt on SIGWINCH, so
    /// matching raw bytes against "\n30 100" misses output that is really there (first device run, 2026-11-12).
    /// </summary>
    public static string Plain(string raw) => EscapeSequence().Replace(raw, "").Replace("\r", "", StringComparison.Ordinal);

    /// <summary>One log line: plain text with line breaks shown as ⏎ (logcat splits multi-line messages).</summary>
    public static string Printable(string raw) => Plain(raw).Trim().Replace("\n", " ⏎ ", StringComparison.Ordinal);

    public static async Task<PtyProbeReport> RunAsync(
        Func<CancellationToken, Task<ProotPtySession>> open,
        Func<int, IReadOnlyList<int>> descendantsOf,
        Func<int, bool> isAlive,
        CancellationToken ct,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(descendantsOf);
        ArgumentNullException.ThrowIfNull(isAlive);
        var clock = time ?? TimeProvider.System;
        static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);
        var steps = new List<PtyProbeStep>();
        var sw = Stopwatch.StartNew();
        void Step(string name, bool ok, string detail)
        {
            steps.Add(new PtyProbeStep(name, ok, detail, sw.ElapsedMilliseconds));
            sw.Restart();
        }

        ProotPtySession session;
        try
        {
            session = await open(ct).ConfigureAwait(false);
        }
        catch (GuestPtyException e)
        {
            Step("open", false, e.Message);
            return new PtyProbeReport(false, steps, "");
        }
        Step("open", true, $"pid {session.Pid}");
        var output = new Transcript(clock);
        using var readerStop = new CancellationTokenSource();
        var reader = Task.Run(async () =>
        {
            var buf = new byte[4096];
            try
            {
                while (!readerStop.IsCancellationRequested)
                {
                    var n = await session.Stream.ReadAsync(buf, readerStop.Token).ConfigureAwait(false);
                    if (n == 0)
                    {
                        break;
                    }
                    output.Append(buf.AsSpan(0, n));
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }, CancellationToken.None);

        void Send(string text) => session.Stream.Write(Encoding.UTF8.GetBytes(text));

        try
        {
            // 1. The login shell prints its prompt.
            var ok = await output.WaitAsync(t => PromptAtEnd().IsMatch(t), 0, S(30), ct).ConfigureAwait(false);
            Step("prompt", ok, ok ? "prompt seen" : "no prompt: " + Printable(output.Tail(120)));
            if (!ok)
            {
                return await FinishAsync(false).ConfigureAwait(false);
            }

            // 2. A real controlling terminal of the requested size.
            var mark = output.Length;
            Send("tty; stty size; echo DSHM-$((6*7))\r");
            ok = await output.WaitAsync(t => t.Contains("DSHM-42", StringComparison.Ordinal), mark, S(15), ct).ConfigureAwait(false);
            var text = Plain(output.Since(mark));
            var isPts = text.Contains("/dev/pts/", StringComparison.Ordinal);
            var size = text.Contains("\n24 80", StringComparison.Ordinal);
            Step("tty", ok && isPts && size, $"pts={isPts} size24x80={size} echo={ok}");

            // 3. Resize reaches the guest (TIOCSWINSZ on the master).
            await output.WaitAsync(t => PromptAtEnd().IsMatch(t), mark, S(5), ct).ConfigureAwait(false);
            await session.ResizeAsync(100, 30, ct).ConfigureAwait(false);
            mark = output.Length;
            Send("stty size\r");
            ok = await output.WaitAsync(t => Plain(t).Contains("\n30 100", StringComparison.Ordinal), mark, S(10), ct).ConfigureAwait(false);
            Step("resize", ok, ok ? "30x100 seen" : "got: " + Printable(output.Since(mark)));

            // 4. ^C interrupts the foreground job; the shell survives and reports 130.
            await output.WaitAsync(t => PromptAtEnd().IsMatch(t), mark, S(5), ct).ConfigureAwait(false);
            Send("sleep 30\r");
            await Task.Delay(S(1.5), clock, ct).ConfigureAwait(false);
            mark = output.Length;
            var interrupt = Stopwatch.StartNew();
            session.Stream.Write([0x03]);
            var back = await output.WaitAsync(t => PromptAtEnd().IsMatch(t), mark, S(8), ct).ConfigureAwait(false);
            var interruptMs = interrupt.ElapsedMilliseconds;
            mark = output.Length;
            Send("echo DSHM-INT-$?\r");
            ok = back && await output.WaitAsync(t => t.Contains("DSHM-INT-130", StringComparison.Ordinal), mark, S(8), ct).ConfigureAwait(false);
            Step("ctrl-c", ok, back ? $"prompt back after {interruptMs} ms, status={(ok ? "130" : Printable(output.Since(mark)))}" : "sleep not interrupted");

            // 5. exit ends the session cleanly and leaves nothing behind.
            await output.WaitAsync(t => PromptAtEnd().IsMatch(t), mark, S(5), ct).ConfigureAwait(false);
            var tree = descendantsOf(session.Pid);
            Send("exit\r");
            var exited = await CompletesWithin(session.Completion, S(15), clock).ConfigureAwait(false);
            var exit = exited ? session.Completion.Result.ToString() : "still running";
            Step("exit", exited && session.Completion.Result.ExitCode == 0, exit);

            await Task.Delay(S(0.5), clock, ct).ConfigureAwait(false);
            var left = tree.Append(session.Pid).Where(isAlive).ToList();
            Step("no-orphans", left.Count == 0, left.Count == 0 ? $"{tree.Count + 1} process(es) gone" : "alive: " + string.Join(",", left));
            return await FinishAsync(steps.All(s => s.Ok)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or InvalidOperationException)
        {
            Step("error", false, e.GetType().Name + ": " + e.Message);
            return await FinishAsync(false).ConfigureAwait(false);
        }

        async Task<PtyProbeReport> FinishAsync(bool ok)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            await readerStop.CancelAsync().ConfigureAwait(false);
            await reader.ConfigureAwait(false);
            return new PtyProbeReport(ok, steps, output.Tail(4000));
        }
    }

    private static async Task<bool> CompletesWithin(Task t, TimeSpan timeout, TimeProvider clock)
    {
        try
        {
            await t.WaitAsync(timeout, clock).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Thread-safe accumulated PTY output with "wait until it contains X" (UTF-8, partial sequences kept).</summary>
    private sealed class Transcript(TimeProvider clock)
    {
        private readonly StringBuilder _text = new();
        private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();
        private readonly Lock _lock = new();
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Length
        {
            get
            {
                lock (_lock)
                {
                    return _text.Length;
                }
            }
        }

        public void Append(ReadOnlySpan<byte> bytes)
        {
            TaskCompletionSource changed;
            lock (_lock)
            {
                var chars = new char[_utf8.GetCharCount(bytes, flush: false)];
                _utf8.GetChars(bytes, chars, flush: false);
                _text.Append(chars);
                changed = _changed;
                _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            changed.TrySetResult();
        }

        public string Since(int mark)
        {
            lock (_lock)
            {
                return mark >= _text.Length ? "" : _text.ToString(mark, _text.Length - mark);
            }
        }

        public string Tail(int max)
        {
            lock (_lock)
            {
                var n = Math.Min(max, _text.Length);
                return _text.ToString(_text.Length - n, n);
            }
        }

        /// <summary>
        /// True once the output since <paramref name="mark"/> satisfies <paramref name="predicate"/>. On timeout the
        /// output is checked one last time: text that arrived before the deadline counts even when the continuation
        /// that would have seen it had not run yet (thread-pool starvation must not turn a pass into a fail).
        /// </summary>
        public async Task<bool> WaitAsync(Func<string, bool> predicate, int mark, TimeSpan timeout, CancellationToken ct)
        {
            using var deadline = new CancellationTokenSource(timeout, clock);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            while (true)
            {
                Task changed;
                lock (_lock)
                {
                    if (Matches(predicate, mark))
                    {
                        return true;
                    }
                    changed = _changed.Task;
                }
                try
                {
                    await changed.WaitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    lock (_lock)
                    {
                        return Matches(predicate, mark);
                    }
                }
            }
        }

        private bool Matches(Func<string, bool> predicate, int mark) =>
            predicate(mark >= _text.Length ? "" : _text.ToString(mark, _text.Length - mark));
    }
}
