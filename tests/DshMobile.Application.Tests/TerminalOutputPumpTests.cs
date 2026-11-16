using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using DshMobile.Application.Terminal;
using DshMobile.Runtime.Guest;
using Microsoft.Extensions.Time.Testing;

namespace DshMobile.Application.Tests;

/// <summary>
/// vNext PR-08: PTY bytes → view. Bytes arrive in order and undecoded (a UTF-8 character split by a read or a batch is
/// rejoined by the emulator, never decoded twice); the view's acknowledgements bound what is in flight, and that bound
/// reaches the PTY reader; a view that stops acknowledging cannot freeze the shell.
/// </summary>
public sealed class TerminalOutputPumpTests
{
    private sealed class Page
    {
        public readonly ConcurrentQueue<string> Posts = new();

        public void Post(string json) => Posts.Enqueue(json);

        /// <summary>Decoded "out" payloads in order; "reset" as null.</summary>
        public List<byte[]?> Messages() => Posts.Select(p =>
        {
            using var d = JsonDocument.Parse(p);
            return d.RootElement.GetProperty("t").GetString() switch
            {
                "out" => Convert.FromBase64String(d.RootElement.GetProperty("b").GetString()!),
                "reset" => (byte[]?)null,
                var t => throw new InvalidDataException(t),
            };
        }).ToList();

        public byte[] Bytes() => Messages().Where(m => m is not null).SelectMany(m => m!).ToArray();
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(10);
        }
        Assert.True(condition(), "timed out waiting for " + what);
    }

    [Fact]
    public async Task Bytes_ArriveInOrder_Batched_AndNeverDecoded()
    {
        var page = new Page();
        using var pump = new TerminalOutputPump(page.Post, batchBytes: 5, windowBytes: 1 << 20);
        // "中文 émoji 😀" split inside every multi-byte sequence.
        var text = Encoding.UTF8.GetBytes("中文 émoji 😀 end");
        foreach (var chunk in text.Chunk(1))
        {
            pump.Write(chunk);
        }
        await Until(() => page.Bytes().Length == text.Length, "all bytes posted");
        Assert.Equal(text, page.Bytes());
        Assert.All(page.Messages(), m => Assert.True(m!.Length <= 5));
        Assert.Contains(page.Messages(), m => !IsCompleteUtf8(m!)); // a batch does end mid-character…
        Assert.Equal("中文 émoji 😀 end", Encoding.UTF8.GetString(page.Bytes())); // …and the stream is still exact
    }

    private static bool IsCompleteUtf8(byte[] b)
    {
        try
        {
            _ = new UTF8Encoding(false, true).GetString(b);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    [Fact]
    public async Task Reset_IsOrderedBetweenTheBytes()
    {
        var page = new Page();
        using var pump = new TerminalOutputPump(page.Post);
        pump.Write("a"u8.ToArray());
        pump.Reset();
        pump.Write("b"u8.ToArray());
        await Until(() => page.Posts.Count >= 3, "three posts");
        var m = page.Messages();
        Assert.Equal("a", Encoding.ASCII.GetString(m[0]!));
        Assert.Null(m[1]);
        Assert.Equal("b", Encoding.ASCII.GetString(m[2]!));
    }

    [Fact]
    public async Task Window_HoldsTheReaderUntilTheViewAcks()
    {
        var time = new FakeTimeProvider();
        var page = new Page();
        using var pump = new TerminalOutputPump(page.Post, batchBytes: 100, windowBytes: 300, time: time);
        Assert.True(pump.WaitForRoomAsync(CancellationToken.None).IsCompleted);
        pump.Write(new byte[300]);
        await Until(() => page.Bytes().Length == 300, "posted");
        Assert.Equal(300, pump.Pending); // posted but not rendered still counts
        var wait = pump.WaitForRoomAsync(CancellationToken.None).AsTask();
        await Task.Delay(50);
        Assert.False(wait.IsCompleted);
        pump.Ack(100);
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(200, pump.Pending);
        pump.Ack(10_000); // excess is clamped
        Assert.Equal(0, pump.Pending);
        Assert.Equal(0, pump.Stalls);
    }

    [Fact]
    public async Task AViewThatStopsAcking_CannotFreezeTheShell_AndAnAckThrottlesAgain()
    {
        var time = new FakeTimeProvider();
        var page = new Page();
        using var pump = new TerminalOutputPump(page.Post, batchBytes: 100, windowBytes: 200, stallTimeout: TimeSpan.FromSeconds(5), time: time);
        pump.Write(new byte[200]);
        var wait = pump.WaitForRoomAsync(CancellationToken.None).AsTask();
        await Task.Delay(50);
        time.Advance(TimeSpan.FromSeconds(4));
        await Task.Delay(50);
        Assert.False(wait.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, pump.Stalls);
        Assert.True(pump.WaitForRoomAsync(CancellationToken.None).IsCompleted); // unthrottled while stalled

        pump.Ack(1); // the view is back: throttle again once the window is full
        pump.Write(new byte[10]);
        var again = pump.WaitForRoomAsync(CancellationToken.None).AsTask();
        await Task.Delay(50);
        Assert.False(again.IsCompleted);
        pump.Ack(1000);
        await again.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AFullWindowAfterAQuietSpell_IsNotAStall()
    {
        var time = new FakeTimeProvider();
        var page = new Page();
        using var pump = new TerminalOutputPump(page.Post, batchBytes: 100, windowBytes: 100, time: time);
        time.Advance(TimeSpan.FromMinutes(10)); // idle long before the window fills
        pump.Write(new byte[100]);
        var wait = pump.WaitForRoomAsync(CancellationToken.None).AsTask();
        await Task.Delay(50);
        Assert.False(wait.IsCompleted);
        Assert.Equal(0, pump.Stalls);
        pump.Dispose(); // detaching releases the reader
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pump.WaitForRoomAsync(CancellationToken.None).IsCompleted);
    }

    [Fact]
    public async Task Sessions_ReaderStopsReading_WhileTheViewIsBehind()
    {
        FakeShell? shell = null;
        Task<ProotPtySession> Open(int rows, int columns, CancellationToken ct)
        {
            shell = new FakeShell(pid: 7000);
            return Task.FromResult(new ProotPtySession(shell, "t7000", ["proot"]));
        }
        await using var sessions = new TerminalSessions(Open);
        var info = await sessions.CreateAsync(24, 80, CancellationToken.None);
        var page = new Page();
        var time = new FakeTimeProvider();
        using var pump = new TerminalOutputPump(page.Post, batchBytes: 64 * 1024, windowBytes: 256 * 1024, time: time);
        using var attachment = sessions.Attach(info.Id, pump)!;
        Assert.True(sessions.Write(info.Id, "flood 4000000\r"u8));

        // The reader fills the window plus at most one read buffer, then waits.
        await Until(() => pump.Pending >= 256 * 1024, "window filled");
        await Task.Delay(200);
        var delivered = page.Bytes().Length;
        Assert.InRange(delivered, 256 * 1024 - 64 * 1024, 256 * 1024 + 16 * 1024);
        Assert.False(shell!.Quiescent); // the rest is still in the "PTY"

        // A view that keeps up drains everything, in order.
        var acked = 0L;
        await Until(() =>
        {
            var total = page.Bytes().LongLength;
            pump.Ack(total - acked);
            acked = total;
            return Encoding.ASCII.GetString(page.Bytes()).Contains("FLOOD-END", StringComparison.Ordinal);
        }, "flood drained");
        var all = Encoding.ASCII.GetString(page.Bytes());
        var start = all.IndexOf("0123456789", StringComparison.Ordinal);
        Assert.Equal(4_000_000, all.IndexOf("\r\nFLOOD-END", start, StringComparison.Ordinal) - start);
        Assert.Equal(0, pump.Stalls);
    }

    [Fact]
    public async Task Sessions_UnthrottledAttach_StillWorks_AndANonAckingViewOnlyDelaysTheShell()
    {
        FakeShell? shell = null;
        Task<ProotPtySession> Open(int rows, int columns, CancellationToken ct)
        {
            shell = new FakeShell(pid: 7001);
            return Task.FromResult(new ProotPtySession(shell, "t7001", ["proot"]));
        }
        await using var sessions = new TerminalSessions(Open);
        var info = await sessions.CreateAsync(24, 80, CancellationToken.None);
        var plain = new ConcurrentQueue<byte[]>();
        using var legacy = sessions.Attach(info.Id, plain.Enqueue)!; // the Action overload never throttles
        var page = new Page();
        using var pump = new TerminalOutputPump(page.Post, batchBytes: 1024, windowBytes: 4096, stallTimeout: TimeSpan.FromMilliseconds(200));
        using var attachment = sessions.Attach(info.Id, pump)!;
        Assert.True(sessions.Write(info.Id, "flood 100000\r"u8));
        // Nobody acks: after the (real-clock) stall timeout the reader proceeds and everything is delivered.
        await Until(() => Encoding.ASCII.GetString(page.Bytes()).Contains("FLOOD-END", StringComparison.Ordinal), "delivered despite no acks");
        Assert.True(pump.Stalls >= 1);
        await Until(() => Encoding.ASCII.GetString(plain.SelectMany(b => b).ToArray()).Contains("FLOOD-END", StringComparison.Ordinal), "legacy listener");
    }
}
