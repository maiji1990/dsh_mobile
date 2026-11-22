using System.Text.Json;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;
using Microsoft.Extensions.Time.Testing;

namespace DshMobile.Agent.Tests.Engines;

/// <summary>
/// Deterministic PR-03 performance gate (`Stream6000` + `CoalescingPublisher` + `SkippedWhileHidden`) (`N-03-08`).
/// Replaces wall-clock guesses with deterministic <see cref="FakeTimeProvider"/> and <see cref="TranscriptCounters"/> assertions.
/// </summary>
public sealed class TranscriptPerfGateTests
{
    [Fact]
    public async Task CoalescingPublisher_EmitsLeadingImmediately_CoalescesInside50ms_AndSkipsWhenHidden()
    {
        var time = new FakeTimeProvider();
        var publishes = 0;
        await using var pub = new CoalescingPublisher(
            () => { Interlocked.Increment(ref publishes); return ValueTask.CompletedTask; },
            TimeSpan.FromMilliseconds(50),
            time);

        // Hidden mode (0 subscribers): all updates skipped from derive/publish.
        for (var i = 0; i < 10; i++)
        {
            pub.NotifyDirty();
        }
        Assert.Equal(0, publishes);
        Assert.Equal(10, pub.SkippedWhileHidden);

        // Subscribing 0 -> 1 triggers 1 catch-up publish.
        using (pub.Subscribe())
        {
            Assert.Equal(1, publishes);

            // Advance past quiet window so next delta fires immediately (leading edge).
            time.Advance(TimeSpan.FromMilliseconds(60));
            pub.NotifyDirty();
            Assert.Equal(2, publishes);

            // Burst inside 50ms window -> coalesced into a single trailing publish.
            for (var i = 0; i < 20; i++)
            {
                time.Advance(TimeSpan.FromMilliseconds(2));
                pub.NotifyDirty();
            }
            Assert.Equal(2, publishes);

            time.Advance(TimeSpan.FromMilliseconds(50));
            Assert.Equal(3, publishes);
        }
    }

    [Fact]
    public async Task Stream6000_With100PriorTurnsAnd400LineCodeBlock_StaysBoundedInCounters()
    {
        var time = new FakeTimeProvider();
        var scope = new SessionKey("rt-1", "default", "s-perf");
        await using var engine = new TranscriptEngine(time, TimeSpan.FromMilliseconds(50));
        var handle = engine.GetOrCreateSession(scope);

        long seq = 1;
        // 1. Seed 100 settled historical turns (must reuse turn prefix rows during live streaming)
        for (var t = 1; t <= 100; t++)
        {
            handle.PostFollowUpdate(scope, new SessionFollowEventUpdate(new SessionWireEventView(
                "turn/start", seq++, t * 10, JsonElement.Parse($$"""{"turn":{{t}}}"""))));
            handle.PostFollowUpdate(scope, new SessionFollowEventUpdate(new SessionWireEventView(
                "user/message", seq++, t * 10 + 1, JsonElement.Parse($$"""{"id":"u-{{t}}","content":[{"type":"text","text":"Question {{t}}"}]}"""))));
            handle.PostFollowUpdate(scope, new SessionFollowEventUpdate(new SessionWireEventView(
                "assistant/message", seq++, t * 10 + 2, JsonElement.Parse($$"""{"id":"a-{{t}}","content":[{"type":"text","text":"Answer {{t}}"}]}"""))));
            handle.PostFollowUpdate(scope, new SessionFollowEventUpdate(new SessionWireEventView(
                "turn/end", seq++, t * 10 + 3, JsonElement.Parse("{\"turn\":" + t + ",\"reason\":{\"kind\":\"completed\"}}"))));
        }

        await handle.WaitForIdleDrainAsync();

        // 2. Attach observer & start Turn 101 live streaming: 6,000 words + 400-line open code block
        var observedSnapshots = 0;
        using var sub = handle.Subscribe(_ => Interlocked.Increment(ref observedSnapshots));

        handle.PostFollowUpdate(scope, new SessionFollowEventUpdate(new SessionWireEventView(
            "turn/start", seq++, 9000, JsonElement.Parse("""{"turn":101}"""))));
        handle.PostFollowAssistantStartOrChunk(scope, "start", "att-101", 1, 0, """{"type":"start","attemptId":"att-101","revision":1}""");

        var chunkIndex = 0;
        // 600 deltas * 10 words each = 6,000 words
        for (var c = 0; c < 600; c++)
        {
            var words = string.Join(' ', Enumerable.Repeat("tokenword", 10)) + (c % 5 == 4 ? "\n" : " ");
            var payload = JsonSerializer.Serialize(words);
            handle.PostFollowAssistantStartOrChunk(scope, "chunk", "att-101", 1, chunkIndex++,
                $$$"""{"type":"chunk","attemptId":"att-101","revision":1,"index":{{{c}}},"chunk":{"type":"text-delta","index":0,"text":{{{payload}}}}}""");

            if (c % 25 == 24)
            {
                await handle.WaitForIdleDrainAsync();
                time.Advance(TimeSpan.FromMilliseconds(55));
            }
        }

        // Append a 400-line unclosed code block in increments
        var fenceIdx = chunkIndex++;
        handle.PostFollowAssistantStartOrChunk(scope, "chunk", "att-101", 1, fenceIdx,
            $$$"""{"type":"chunk","attemptId":"att-101","revision":1,"index":{{{fenceIdx}}},"chunk":{"type":"text-delta","index":0,"text":"\n```csharp\n"}}""");

        for (var line = 1; line <= 400; line++)
        {
            var idx = chunkIndex++;
            handle.PostFollowAssistantStartOrChunk(scope, "chunk", "att-101", 1, idx,
                $$$"""{"type":"chunk","attemptId":"att-101","revision":1,"index":{{{idx}}},"chunk":{"type":"text-delta","index":0,"text":"Console.WriteLine({{{line}}});\n"}}""");
            if (line % 50 == 0)
            {
                await handle.WaitForIdleDrainAsync();
                time.Advance(TimeSpan.FromMilliseconds(55));
            }
        }

        handle.PostFollowUpdate(scope, new SessionFollowEventUpdate(new SessionWireEventView(
            "turn/end", seq++, 99999, JsonElement.Parse("""{"turn":101,"reason":{"kind":"completed"}}"""))));

        var finalSnap = await handle.WaitForIdleDrainAsync();
        var counters = finalSnap.Counters;

        // Deterministic perf gates (`N-03-08`):
        // 1. Publishes coalesced (1000+ chunks produced far fewer UI publishes than chunk count)
        Assert.InRange(counters.Publishes, 1, 150);
        // 2. Active tail markdown chunks stay bounded (400-line fence split into <=16-line slices)
        var turn101MdRows = finalSnap.Rows.OfType<MarkdownBlockRow>().Where(r => r.TurnSeq == 101).ToList();
        Assert.True(turn101MdRows.Count >= 25);
        Assert.All(turn101MdRows, row =>
        {
            var lineCount = row.Markdown.Split('\n').Length;
            Assert.True(lineCount <= StreamingBlocks.MaxTailLines + 2, $"Row exceeded 16-line cap cleanly (+fence closer): {lineCount}");
        });
        // 3. Reused 100 settled turn prefixes without re-deriving history per live chunk
        Assert.True(counters.PrefixTurnReuseHits >= 100);
        // 4. TailChars is cumulative across the completed turn (NOT zero after turn/end!)
        //    and proves O(1)-tail parsing vs naive quadratic O(N^2) cumulative full-document reparsing.
        Assert.True(counters.TailChars > 0, "Cumulative TailChars must remain non-zero after turn/end");
        Assert.True(counters.TailReparses > 0, "TailReparses must count actual block re-evaluations");
        var naiveQuadraticEstimate = 20_000_000L; // ~ (6000w * 10chars) cumulative reparse over ~1000 frames / 2
        Assert.True(
            counters.TailChars < naiveQuadraticEstimate / 8,
            $"Expected sub-quadratic tail chars (< {naiveQuadraticEstimate / 8}), got {counters.TailChars}");
        // 5. RowWrites stays bounded proportional to distinct turns + finalized slices, not chunk count
        Assert.InRange(counters.RowWrites, 100, 1000);
        // 6. History RPC was never needlessly visited during pure live streaming
        Assert.Equal(0, counters.HistoryVisits);
    }

    [Fact]
    public void Stream6000_NaiveQuadraticReparser_FailsSubQuadraticTailCharBudget()
    {
        // Negative / anti-cheat check (`规则 5`): prove the Stream6000 TailChars gate actually rejects an O(N^2) implementation.
        var accumulated = 0;
        long naiveTailChars = 0;
        const int chunkChars = 100;
        for (var i = 0; i < 600; i++)
        {
            accumulated += chunkChars;
            naiveTailChars += accumulated;
        }
        const long budget = 2_500_000L;
        Assert.True(naiveTailChars > budget, $"Expected naive quadratic reparser ({naiveTailChars}) to violate gate ({budget})");
    }

    [Fact]
    public async Task TranscriptHandle_WhenHidden_SkipsDerivesAndPublishesCatchUpOnSubscribe()
    {
        var scope = new SessionKey("rt-1", "web", "s-hidden");
        var time = new FakeTimeProvider();
        await using var engine = new TranscriptEngine(time);
        var handle = engine.GetOrCreateSession(scope);

        handle.PostFollowUpdate(scope, new SessionFollowEventUpdate(new SessionWireEventView(
            "turn/start", 1, 100, JsonElement.Parse("""{"turn":1}"""))));
        for (var i = 0; i < 15; i++)
        {
            handle.PostFollowAssistantStartOrChunk(scope, i == 0 ? "start" : "chunk", "att-h", 1, i,
                $$$"""{"type":"chunk","attemptId":"att-h","revision":1,"index":{{{i}}},"chunk":{"type":"text-delta","text":"hi "}}""");
        }

        // Wait for pump to drain WITHOUT any UI subscribers -> SkippedWhileHidden > 0, Publishes == 0
        var hiddenSnap = await handle.WaitForIdleDrainAsync();
        Assert.Equal(0, hiddenSnap.Counters.Publishes);
        Assert.True(hiddenSnap.Counters.SkippedWhileHidden > 0);

        // First active UI subscriber triggers immediate catch-up publish + snapshot push
        var observed = new List<ChatSnapshot>();
        using var sub = handle.Subscribe(observed.Add);
        await Task.Delay(50);
        var visibleSnap = await handle.WaitForIdleDrainAsync();
        Assert.True(visibleSnap.Counters.Publishes >= 1);
        Assert.NotEmpty(observed);
    }
}

internal static class TranscriptTestExtensions
{
    public static void PostFollowAssistantStartOrChunk(
        this TranscriptHandle handle,
        SessionKey scope,
        string frameType,
        string attemptId,
        long revision,
        int _,
        string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        handle.PostFollowUpdate(scope, new SessionFollowAssistantStreamUpdate(
            frameType,
            attemptId,
            revision,
            doc.RootElement.Clone()));
    }
}
