using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DshMobile.Runtime.Automation.Relay;
using DshMobile.Runtime.Automation.Relay.Interlock;
using DshMobile.Runtime.Automation.Relay.Logging;
using DshMobile.Runtime.Automation.Relay.Storage;
using DshMobile.Runtime.Automation.Relay.Transport;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// PR-07 segment 8: the C# control channel over tests/TestVectors/relay-control.json. The legacy Kotlin
/// LegacyRelayControlParityTest drives the real upstream ControlChannelServer / ControlChannelLogic / PromptCodes.
/// </summary>
public sealed class RelayControlParityTests : IDisposable
{
    private static readonly JsonElement V = Repo.Vectors("relay-control.json");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dshm-relay-control-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string[] Strings(JsonElement a) => [.. a.EnumerateArray().Select(e => e.GetString()!)];

    private static bool Same(JsonElement want, string actual) => JsonNode.DeepEquals(JsonNode.Parse(want.GetRawText()), JsonNode.Parse(actual));

    /// <summary>A control payload as the object EncodeControl would write after the four header fields.</summary>
    private static string PayloadObject(IEnumerable<KeyValuePair<string, object?>> payload)
    {
        var frame = JsonNode.Parse(RelayEncoding.EncodeControl("x", "p", true, payload))!.AsObject();
        foreach (var k in new[] { "v", "id", "op", "ok" })
        {
            frame.Remove(k);
        }
        return frame.ToJsonString();
    }

    private static RequestState? State(JsonElement s) => s.ValueKind == JsonValueKind.Null ? null : new RequestState(
        s.GetProperty("id").GetString()!, s.GetProperty("digest").GetString()!, s.GetProperty("capability").GetString()!,
        RequestPhases.Parse(s.GetProperty("phase").GetString())!.Value, s.GetProperty("createdAtMs").GetInt64(), null,
        s.GetProperty("ttlMs").GetInt64(),
        MayHaveDispatched: s.TryGetProperty("mayHaveDispatched", out var m) && m.GetBoolean(),
        ResultJson: s.TryGetProperty("resultJson", out var r) ? r.GetString() : null,
        CancelRequested: s.TryGetProperty("cancelRequested", out var cr) && cr.GetBoolean());

    [Fact]
    public void Logic_MatchesSharedVectors()
    {
        foreach (var c in V.GetProperty("submitEnvelope").EnumerateArray())
        {
            var s = c.GetProperty("submit");
            var submit = new ControlRequest.Submit(s.GetProperty("id").GetString()!, s.GetProperty("capability").GetString()!,
                s.GetProperty("args"), s.GetProperty("hostTtlMs").GetInt64(), s.GetProperty("digest").GetString()!);
            Assert.Equal(c.GetProperty("text").GetString(), ControlChannelLogic.SubmitEnvelopeV1(submit, c.GetProperty("ts").GetInt64()));
        }
        foreach (var c in V.GetProperty("statusPayload").EnumerateArray())
        {
            var got = PayloadObject(ControlChannelLogic.StatusPayload(State(c.GetProperty("state")), c.GetProperty("target").GetString()!));
            Assert.True(Same(c.GetProperty("payload"), got), got);
        }
        foreach (var c in V.GetProperty("healthPayload").EnumerateArray())
        {
            var a = c.GetProperty("args");
            var got = PayloadObject(ControlChannelLogic.HealthPayload(a[0].GetBoolean(), a[1].GetInt32(),
                a[2].ValueKind == JsonValueKind.Null ? null : a[2].GetInt64(), a[3].GetInt32(), a[4].GetBoolean()));
            Assert.True(Same(c.GetProperty("payload"), got), got);
        }
        foreach (var c in V.GetProperty("cancelPlan").EnumerateArray())
        {
            var state = c[0].ValueKind == JsonValueKind.Null ? null
                : new RequestState("t", "d", "ui.node", RequestPhases.Parse(c[0].GetString())!.Value, 0, null, 5000);
            var plan = ControlChannelLogic.CancelPlanOf(state, c[1].GetBoolean()) switch
            {
                CancelPlan.AlreadySettled s => "AlreadySettled:" + s.Phase.Wire(),
                var p => p.GetType().Name,
            };
            Assert.Equal(c[2].GetString(), plan);
        }
        foreach (var c in V.GetProperty("askStale").EnumerateArray())
        {
            Assert.Equal(c[3].GetInt64(), ControlChannelLogic.AskStaleMs(c[0].GetInt64(), c[1].GetInt64(), c[2].GetInt64()));
        }
        foreach (var c in V.GetProperty("promptCodes").EnumerateArray())
        {
            var cause = c[0].GetString()!;
            Assert.Equal(c[1].GetString(), PromptCodes.CodeOf(cause));
            Assert.Equal(c[2].GetInt32(), PromptCodes.ExitCodeFor(cause));
            Assert.Equal(c[3].GetBoolean(), PromptCodes.Retryable(cause));
        }
        var constants = typeof(PromptCodes).GetFields().Where(f => f.IsLiteral && f.Name.Contains('/', StringComparison.Ordinal) is false)
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);
        foreach (var p in V.GetProperty("promptConstants").EnumerateObject())
        {
            var name = string.Concat(p.Name.Split('_').Select(w => w[0] + w[1..].ToLowerInvariant()));
            Assert.True(constants.TryGetValue(name, out var value), name);
            Assert.Equal(p.Value.GetString(), value);
        }
        var ask = V.GetProperty("askPayloads");
        var chosen = ask.GetProperty("chosen");
        var chosenPayload = ControlChannelLogic.AskChosenPayload(chosen.GetProperty("args")[0].GetInt32(), chosen.GetProperty("args")[1].GetString()!);
        Assert.True(Same(chosen.GetProperty("payload"), PayloadObject(chosenPayload)));
        Assert.EndsWith("," + chosen.GetProperty("text").GetString()![1..], RelayEncoding.EncodeControl("x", "ask", true, chosenPayload));
        Assert.True(Same(ask.GetProperty("rejectAll").GetProperty("payload"), PayloadObject(ControlChannelLogic.AskRejectAllPayload())));
        Assert.True(Same(ask.GetProperty("reask").GetProperty("payload"), PayloadObject(ControlChannelLogic.AskReaskPayload())));
        var failure = ask.GetProperty("failure");
        Assert.True(Same(failure.GetProperty("payload"), PayloadObject(ControlChannelLogic.AskFailurePayload(failure.GetProperty("cause").GetString()!))));
    }

    private sealed class ScriptedFailure : Exception;

    private sealed class ScriptedPresenter : IInterlockPrompt
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PromptOutcome> AskAsync(string question, IReadOnlyList<string> options, long timeoutMs, CancellationToken cancellationToken) =>
            question switch
            {
                "pick" => new PromptOutcome.Chosen(1, "B"),
                "none" => new PromptOutcome.RejectedAll(),
                "again" => new PromptOutcome.Reasked(),
                "slow" => new PromptOutcome.TimedOut(true),
                "nope" => new PromptOutcome.Unavailable(PromptCodes.NoSurfaceHidden),
                "boom" => throw new ScriptedFailure(),
                "hold" => await Hold(),
                _ => throw new InvalidOperationException(question),
            };

        private async Task<PromptOutcome> Hold()
        {
            await Release.Task;
            return new PromptOutcome.Chosen(0, "X");
        }
    }

    public static IEnumerable<object[]> Scenarios() => V.GetProperty("scenarios").EnumerateArray().Select(s => new object[] { s.GetProperty("name").GetString()! });

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Control_ScenarioMatchesSharedVectors(string name)
    {
        var s = V.GetProperty("scenarios").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        var clock = Stopwatch.StartNew();
        long Now() => 1_000 + clock.ElapsedMilliseconds;
        var paths = new RelayPaths(_root, RelayPathPolicy.DshMobile);
        paths.EnsureWritableDirs();
        paths.EnsureMailboxDirs();
        var runLog = new RunLog(paths.LogsDir);
        var reaper = new StorageReaper(paths, new QuotaLedger(paths.QuotaFile), runLog, Now, RelayClock.WallNow, _ => long.MaxValue);
        var store = new RequestStateStore(paths.RequestStateDir, Now, RelayClock.WallNow, runLog);
        var h = V.GetProperty("health");
        var health = new WorkLoopHealth(h.GetProperty("channelLive").GetBoolean(), h.GetProperty("queueDepth").GetInt32(),
            h.GetProperty("oldestQueuedAgeMs").GetInt64(), h.GetProperty("inboxDepth").GetInt32(), h.GetProperty("stalled").GetBoolean());
        var presenter = new ScriptedPresenter();
        var server = new ControlChannelServer(paths, reaper, store, runLog, () => health,
            s.GetProperty("presenter").GetBoolean() ? presenter : null, Now);
        if (s.TryGetProperty("preState", out var pre))
        {
            foreach (var op in pre.EnumerateArray())
            {
                var id = op.GetProperty("id").GetString()!;
                _ = op.GetProperty("op").GetString() switch
                {
                    "create" => (object)store.Create(id, op.GetProperty("digest").GetString()!, "ui.node", 30_000),
                    "terminal" => store.RecordTerminal(id, RequestPhases.Parse(op.GetProperty("phase").GetString())!.Value, op.GetProperty("result").GetString())!,
                    "admit" => store.MarkAdmitted(id)!,
                    "guard" => store.ArmExecDispatchGuard(id),
                    var other => throw new InvalidOperationException(other),
                };
            }
        }
        if (s.TryGetProperty("inboxFiles", out var inboxFiles))
        {
            foreach (var p in inboxFiles.EnumerateObject())
            {
                File.WriteAllText(Path.Combine(paths.InboxDir, p.Name), p.Value.GetString(), new UTF8Encoding(false));
            }
        }
        var staging = Path.Combine(_root, "drop");
        Directory.CreateDirectory(staging);
        void Drop(JsonElement f)
        {
            var fileName = f.GetProperty("name").GetString()!;
            var temp = Path.Combine(staging, fileName);
            File.WriteAllText(temp, f.GetProperty("text").GetString(), new UTF8Encoding(false));
            if (f.TryGetProperty("ageMs", out var age))
            {
                File.SetLastWriteTimeUtc(temp, DateTime.UtcNow.AddMilliseconds(-age.GetInt64()));
            }
            File.Move(temp, Path.Combine(paths.ControlInboxDir, fileName));
        }
        string Log() => SharedRead.Logs(paths.LogsDir); // the server is still appending while the test polls
        async Task Until(Func<bool> condition, string what)
        {
            var sw = Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"{name}: timed out waiting for {what}");
                await Task.Delay(50);
            }
        }
        var wallAtStart = RelayClock.WallNow();
        using var scope = new CancellationTokenSource();
        server.Start(scope.Token);
        try
        {
            Assert.True(server.IsRunning);
            foreach (var f in s.GetProperty("files").EnumerateArray())
            {
                Drop(f);
            }
            if (s.TryGetProperty("redeliver", out var again))
            {
                await Until(() => Log().Contains("control op=ask result=accepted", StringComparison.Ordinal), "the first ask");
                Drop(again);
                var dup = s.GetProperty("duplicateLog").GetString()!;
                await Until(() => Log().Contains(dup, StringComparison.Ordinal), "the duplicate refusal");
                Assert.False(File.Exists(Path.Combine(paths.ControlOutboxDir, again.GetProperty("name").GetString()!)), "duplicate must not reply");
                presenter.Release.SetResult();
            }
            var control = s.GetProperty("controlOutbox");
            var left = Strings(s.GetProperty("controlInboxLeft"));
            await Until(() => control.EnumerateObject().All(p => File.Exists(Path.Combine(paths.ControlOutboxDir, p.Name)))
                && Directory.GetFiles(paths.ControlInboxDir).Length == left.Length, "all replies");
            await Task.Delay(200);
            Assert.Equal(control.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal),
                Directory.GetFiles(paths.ControlOutboxDir).Select(Path.GetFileName).Order(StringComparer.Ordinal)!);
            foreach (var p in control.EnumerateObject())
            {
                var text = File.ReadAllText(Path.Combine(paths.ControlOutboxDir, p.Name), Encoding.UTF8);
                Assert.True(Same(p.Value, text), $"{name} control-outbox/{p.Name}: {text}");
            }
            Assert.Equal(left.Order(StringComparer.Ordinal), Directory.GetFiles(paths.ControlInboxDir).Select(Path.GetFileName).Order(StringComparer.Ordinal)!);
            var inbox = s.GetProperty("inbox");
            Assert.Equal(inbox.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal),
                Directory.GetFiles(paths.InboxDir).Select(Path.GetFileName).Order(StringComparer.Ordinal)!);
            foreach (var p in inbox.EnumerateObject())
            {
                var envelope = JsonNode.Parse(File.ReadAllText(Path.Combine(paths.InboxDir, p.Name), Encoding.UTF8))!.AsObject();
                Assert.InRange(envelope["ts"]!.GetValue<long>(), wallAtStart, RelayClock.WallNow());
                envelope.Remove("ts");
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(p.Value.GetRawText()), envelope), envelope.ToJsonString());
            }
            if (s.TryGetProperty("phases", out var phases))
            {
                foreach (var p in phases.EnumerateObject())
                {
                    Assert.True(p.Value.GetString() == store.Get(p.Name)?.Phase.Wire(), $"{name} phase {p.Name}: {store.Get(p.Name)?.Phase}");
                }
            }
            if (s.TryGetProperty("cancelRequested", out var cancelled))
            {
                foreach (var p in cancelled.EnumerateObject())
                {
                    Assert.Equal(p.Value.GetBoolean(), store.Get(p.Name)!.CancelRequested);
                }
            }
            var log = Log();
            foreach (var line in Strings(s.GetProperty("logContains")))
            {
                Assert.True(log.Contains(line, StringComparison.Ordinal), $"{name}: log lacks '{line}'");
            }
        }
        finally
        {
            presenter.Release.TrySetResult();
            var job = server.Completion;
            server.Halt();
            if (job is not null)
            {
                await job.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }
}
