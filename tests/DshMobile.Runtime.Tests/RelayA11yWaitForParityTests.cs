using System.Text.Json;
using DshMobile.Runtime.Automation.Relay;
using DshMobile.Runtime.Automation.Relay.A11y;
using DshMobile.Runtime.Automation.Relay.Exec;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// PR-07 segment 8: ui.waitFor over tests/TestVectors/relay-a11y-waitfor.json. The clock moves only in the poll delay
/// and poll k reads timeline[min(k,len)-1], so polls and waitedMs are exact. C#-only like the read vectors; the live
/// legacy Relay is compared on the device (automation.relay-a11y-waitfor).
/// </summary>
public sealed class RelayA11yWaitForParityTests
{
    private static readonly JsonElement V = Repo.Vectors("relay-a11y-waitfor.json");

    private static async Task<(BackendResult Result, int Delays)> Run(JsonElement c)
    {
        var timeline = c.GetProperty("timeline").EnumerateArray()
            .Select(t => t.ValueKind == JsonValueKind.Null ? null : new RelayA11yReadParityTests.FakeNode(V.GetProperty("trees").GetProperty(t.GetString()!), 0))
            .ToList();
        var frame = V.GetProperty("frames").GetProperty(c.GetProperty("frame").GetString()!);
        var s = frame.GetProperty("screen");
        long now = 0;
        var delays = 0;
        var tree = new A11yTree(frame.GetProperty("display").GetInt32(), new Bounds(s[0].GetInt32(), s[1].GetInt32(), s[2].GetInt32(), s[3].GetInt32()),
            frame.GetProperty("dpi").GetInt32(), () => timeline[Math.Min(delays, timeline.Count - 1)]);
        var args = JsonDocument.Parse(c.GetProperty("args").GetString()!).RootElement;
        var result = await new NodeReader(tree).WaitForAsync(args, c.GetProperty("budgetMs").GetInt64(), () => now, (ms, _) =>
        {
            Assert.Equal(NodeReader.WaitPollMs, ms);
            now += ms;
            delays++;
            return Task.CompletedTask;
        }, CancellationToken.None);
        return (result, delays);
    }

    [Fact]
    public async Task WaitFor_MatchesTheVectorTextByteForByte()
    {
        var n = 0;
        foreach (var c in V.GetProperty("cases").EnumerateArray())
        {
            var label = $"waitFor {c.GetProperty("args").GetString()} on {c.GetProperty("timeline").GetRawText()}";
            var expect = c.GetProperty("expect");
            var (actual, _) = await Run(c);
            if (expect.GetProperty("ok").GetBoolean())
            {
                var ok = Assert.IsType<BackendResult.Ok>(actual);
                Assert.True(expect.GetProperty("data").GetString() == RelayEncoding.EncodeData(ok.Data),
                    $"{label}\n want {expect.GetProperty("data").GetString()}\n got  {RelayEncoding.EncodeData(ok.Data)}");
            }
            else
            {
                var failed = Assert.IsType<BackendResult.Failed>(actual);
                Assert.True(expect.GetProperty("code").GetString() == failed.Error.Code && expect.GetProperty("reason").GetString() == failed.Reason,
                    $"{label}\n want {expect.GetProperty("code").GetString()}: {expect.GetProperty("reason").GetString()}\n got  {failed.Error.Code}: {failed.Reason}");
            }
            n++;
        }
        Assert.Equal(43, n);
    }

    [Fact]
    public async Task ARejectedSpec_AnswersAtOnce_WithoutSpendingTheBudget()
    {
        // An ambiguous selector can never become satisfied: it must not sit out the timeout first.
        var c = V.GetProperty("cases").EnumerateArray().First(x => x.GetProperty("args").GetString() == """{"selector":{"text":"OK"},"timeoutMs":3000}""");
        var (result, delays) = await Run(c);
        Assert.Equal(RelayError.NodeAmbiguous, Assert.IsType<BackendResult.Failed>(result).Error);
        Assert.Equal(0, delays);
    }

    [Fact]
    public async Task OnTheRealClock_ATimeoutOfZero_PollsOnce()
    {
        var root = new RelayA11yReadParityTests.FakeNode(V.GetProperty("trees").GetProperty("blank"), 0);
        var tree = new A11yTree(0, new Bounds(0, 0, 1080, 2400), 480, () => root);
        var args = JsonDocument.Parse("""{"selector":{"text":"nothing here"},"timeoutMs":0}""").RootElement;
        var result = await new NodeReader(tree).WaitForAsync(args, 60_000, TimeProvider.System, CancellationToken.None);
        Assert.Equal("condition still false after 0ms of the requested 0ms and 1 polls: selector matched no node",
            Assert.IsType<BackendResult.Failed>(result).Reason);
    }
}
