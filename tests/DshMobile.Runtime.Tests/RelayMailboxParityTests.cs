using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DshMobile.Runtime.Automation.Relay;
using DshMobile.Runtime.Automation.Relay.Logging;
using DshMobile.Runtime.Automation.Relay.Storage;
using DshMobile.Runtime.Automation.Relay.Transport;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// PR-07 segment 7: the C# mailbox loop and request state store over tests/TestVectors/relay-mailbox.json. The legacy
/// Kotlin LegacyRelayMailboxParityTest drives the real upstream MailboxServer / RequestStateStore with the same cases.
/// </summary>
public sealed class RelayMailboxParityTests : IDisposable
{
    private static readonly JsonElement V = Repo.Vectors("relay-mailbox.json");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dshm-relay-mailbox-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static List<JsonElement> List(string name) => [.. V.GetProperty(name).EnumerateArray()];

    private static string[] Strings(JsonElement a) => [.. a.EnumerateArray().Select(e => e.GetString()!)];

    private static long? OptLong(JsonElement o, string name) =>
        o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : null;

    private static RequestState? State(JsonElement s) => s.ValueKind == JsonValueKind.Null ? null : new RequestState(
        s.GetProperty("id").GetString()!, s.GetProperty("digest").GetString()!, s.GetProperty("capability").GetString()!,
        RequestPhases.Parse(s.GetProperty("phase").GetString())!.Value, s.GetProperty("createdAtMs").GetInt64(), OptLong(s, "admittedAtMs"),
        s.GetProperty("ttlMs").GetInt64(), TerminalAtMs: OptLong(s, "terminalAtMs"),
        ResultJson: s.TryGetProperty("resultJson", out var r) ? r.GetString() : null);

    [Fact]
    public void PureFunctions_MatchSharedVectors()
    {
        foreach (var c in List("orderPending"))
        {
            var entries = c.GetProperty("entries").EnumerateArray()
                .Select(e => (e[0].GetString()!, e[1].ValueKind == JsonValueKind.Null ? (long?)null : e[1].GetInt64())).ToList();
            Assert.Equal(Strings(c.GetProperty("ordered")), MailboxServer.OrderPending(entries));
        }
        foreach (var c in List("evictionVictims"))
        {
            Assert.Equal(Strings(c.GetProperty("victims")),
                MailboxServer.EvictionVictims(Strings(c.GetProperty("names")), c.GetProperty("capacity").GetInt32()));
        }
        foreach (var c in List("needsSettle"))
        {
            Assert.Equal(c[3].GetBoolean(), MailboxServer.NeedsSettle(c[0].GetInt64(), c[1].GetInt64(), c[2].GetBoolean()));
        }
        foreach (var c in List("incomingPart"))
        {
            Assert.Equal(c[1].GetBoolean(), MailboxServer.IsIncomingPartName(c[0].GetString()!));
        }
        foreach (var c in List("claimBlocked"))
        {
            Assert.True(c.GetProperty("blocked").GetBoolean() == MailboxServer.ClaimBlockedByInFlight(
                State(c.GetProperty("state")), c.GetProperty("now").GetInt64(), c.GetProperty("processing").GetBoolean()), c.ToString());
        }
        foreach (var c in List("queueCounts"))
        {
            Dictionary<string, long> Map(string n) => c.GetProperty(n).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt64());
            var counts = MailboxServer.QueueCountsOf(Strings(c.GetProperty("inbox")), Strings(c.GetProperty("processing")),
                Map("seen"), Map("admitted"), c.GetProperty("now").GetInt64());
            Assert.Equal(c.GetProperty("depth").GetInt32(), counts.Depth);
            Assert.Equal(OptLong(c, "oldest"), counts.OldestAgeMs);
        }
        foreach (var c in List("terminalFrame"))
        {
            var frame = MailboxServer.TerminalFrame(c.GetProperty("id").GetString()!, c.GetProperty("responseOk").GetBoolean(), State(c.GetProperty("state")));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(c.GetProperty("frame").GetRawText()), JsonNode.Parse(frame)), frame);
        }
    }

    [Fact]
    public void Budget_AndMalformedIds_FollowUpstreamRules()
    {
        Assert.Equal(750, MailboxServer.Budget(1000, 0));
        Assert.Equal(3000, MailboxServer.Budget(4000, 0));
        Assert.Equal(88_500, MailboxServer.Budget(90_000, 0));
        Assert.Equal(0, MailboxServer.Budget(1000, 750));
        Assert.Equal("bad_name_", MailboxServer.MalformedNameId("bad name!.json"));
        Assert.Equal("unnamed", MailboxServer.MalformedNameId(".json"));
        Assert.Equal(new string('x', 64), MailboxServer.MalformedNameId(new string('x', 70) + ".json"));
        Assert.Equal("a_b", MailboxServer.MalformedNameId("a.b"));
    }

    [Fact]
    public void StateStore_StepsMatchSharedVectors()
    {
        var s = V.GetProperty("stateSteps");
        var wall = s.GetProperty("wallMs").GetInt64();
        long t = 0;
        var dir = Path.Combine(_root, "requests");
        var store = new RequestStateStore(dir, () => t, () => wall);
        string? Text(string id) => File.Exists(Path.Combine(dir, id + ".json")) ? File.ReadAllText(Path.Combine(dir, id + ".json"), Encoding.UTF8) : null;
        var i = 0;
        foreach (var step in s.GetProperty("steps").EnumerateArray())
        {
            var where = $"step {i++} {step}";
            t = step.GetProperty("t").GetInt64();
            var id = step.TryGetProperty("id", out var idEl) ? idEl.GetString()! : "";
            string? outcome;
            switch (step.GetProperty("op").GetString())
            {
                case "create":
                    outcome = store.Create(id, step.GetProperty("digest").GetString()!, step.GetProperty("cap").GetString()!, step.GetProperty("ttl").GetInt64())
                        .GetType().Name;
                    break;
                case "admit":
                    outcome = store.MarkAdmitted(id)?.Phase.Wire();
                    break;
                case "waiting":
                    outcome = store.MarkWaiting(id, step.GetProperty("relayUser").GetBoolean())?.Phase.Wire();
                    break;
                case "cancel":
                    outcome = store.RequestCancel(id) switch
                    {
                        CancelOutcome.Requested r => "Requested:" + (r.ProvablyUndispatched ? "true" : "false"),
                        var other => other.GetType().Name,
                    };
                    break;
                case "guard":
                    outcome = store.ArmExecDispatchGuard(id) switch
                    {
                        DispatchGuardOutcome.NotExecutable n => "NotExecutable:" + (n.State?.Phase.Wire() ?? "null"),
                        var other => other.GetType().Name,
                    };
                    break;
                case "terminal":
                    var phase = RequestPhases.Parse(step.GetProperty("phase").GetString())!.Value;
                    var result = step.GetProperty("result");
                    outcome = store.RecordTerminal(id, phase, result.ValueKind == JsonValueKind.Null ? null : result.GetString())?.Phase.Wire();
                    break;
                case "purge":
                    store.PurgeExpired(step.GetProperty("now").GetInt64());
                    outcome = null;
                    break;
                case "recover":
                    var recovered = store.RecoverUnfinished().OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => $"{r.Id}:{r.Phase.Wire()}");
                    Assert.Equal(step.GetProperty("recovered").EnumerateArray().Select(p => $"{p[0].GetString()}:{p[1].GetString()}"), recovered);
                    outcome = null;
                    break;
                default:
                    throw new InvalidOperationException(where);
            }
            if (step.TryGetProperty("outcome", out var want))
            {
                Assert.True((want.ValueKind == JsonValueKind.Null ? null : want.GetString()) == outcome, $"{where} got {outcome}");
            }
            if (step.TryGetProperty("record", out var rec))
            {
                Assert.True((rec.ValueKind == JsonValueKind.Null ? null : step.GetProperty("recordText").GetString()) == Text(id), $"{where} got {Text(id)}");
            }
            if (step.TryGetProperty("recordTexts", out var texts))
            {
                foreach (var p in texts.EnumerateObject())
                {
                    Assert.True((p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.GetString()) == Text(p.Name), $"{where} {p.Name} got {Text(p.Name)}");
                }
            }
        }
        Assert.Equal(V.GetProperty("stateJsonOrderText").GetString(), RequestStateStore.ToJson(State(V.GetProperty("stateJsonOrder"))! with
        {
            MayHaveDispatched = true,
            CancelRequested = true,
        }, wall));
    }

    // ---- end-to-end ----

    private sealed class ScriptedFailure : Exception;

    /// <summary>Drops every elapsedMs and reduces each error object to its code: the structure both sides compare.</summary>
    private static JsonNode? Normalize(JsonNode? n)
    {
        switch (n)
        {
            case JsonObject o:
                var copy = new JsonObject();
                foreach (var (k, val) in o)
                {
                    if (k == "elapsedMs")
                    {
                        continue;
                    }
                    copy[k] = k == "error" && val is JsonObject e ? new JsonObject { ["code"] = e["code"]?.DeepClone() } : Normalize(val);
                }
                return copy;
            case JsonArray a:
                return new JsonArray([.. a.Select(Normalize)]);
            default:
                return n?.DeepClone();
        }
    }

    private static void AssertResponse(JsonElement want, string actualText, string where)
    {
        var expected = JsonNode.Parse(want.GetRawText())!.AsObject();
        var actual = Normalize(JsonNode.Parse(actualText))!.AsObject();
        if (expected["reason"] is JsonValue rv && rv.GetValue<string>() is { } reason && reason.StartsWith('@'))
        {
            Assert.StartsWith(reason[1..], actual["reason"]?.GetValue<string>() ?? "");
            expected.Remove("reason");
            actual.Remove("reason");
        }
        Assert.True(JsonNode.DeepEquals(expected, actual), $"{where}: {actual.ToJsonString()}");
    }

    private static string FileText(JsonElement f) => f.TryGetProperty("padId", out var padId)
        ? "{\"v\":1,\"id\":\"" + padId.GetString() + "\",\"capability\":\"ui.snapshot\",\"args\":{\"pad\":\"" + new string('p', f.GetProperty("padChars").GetInt32()) + "\"}}"
        : f.GetProperty("text").GetString()!;

    private static RelayResponse Ok(string id, JsonElement data) => new(id, true, data, [], null, null, null, null, 0);

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    public static IEnumerable<object[]> Scenarios() => V.GetProperty("scenarios").EnumerateArray().Select(s => new object[] { s.GetProperty("name").GetString()! });

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Mailbox_ScenarioMatchesSharedVectors(string name)
    {
        var s = V.GetProperty("scenarios").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        var clock = Stopwatch.StartNew();
        long Now() => 1_000 + clock.ElapsedMilliseconds;
        var paths = new RelayPaths(_root, RelayPathPolicy.DshMobile);
        paths.EnsureWritableDirs();
        var runLog = new RunLog(paths.LogsDir);
        var reaper = new StorageReaper(paths, new QuotaLedger(paths.QuotaFile), runLog, Now, RelayClock.WallNow, _ => long.MaxValue);
        var store = new RequestStateStore(paths.RequestStateDir, Now, RelayClock.WallNow, runLog);
        var budgets = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
        MailboxServer? server = null;
        async Task<HandlerResult> Handle(RelayRequest r, long budget, CancellationToken ct)
        {
            budgets[r.Id] = budget;
            switch (r.Capability)
            {
                case "ui.node":
                    throw new ScriptedFailure();
                case "ui.tap":
                    await Task.Delay(5000, ct);
                    return new HandlerResult.Done(Ok(r.Id, Json(new { late = true })));
                case "screen.observe":
                    return new HandlerResult.Done(Ok(r.Id, Json(new { blob = new string('x', 600_000) })));
                case "ui.click" or "ui.longClick":
                    var click = r.Capability == "ui.click";
                    var park = new ParkedRequest(r.Id,
                        _ => Task.FromResult(click
                            ? Ok(r.Id, Json(new { resumed = r.Capability }))
                            : RelayResponse.Failure(r.Id, RelayError.GateCancelled, 0)),
                        click ? Now() + 60_000 : Now() - 20_000, Now() + 15_000, "test");
                    Assert.True(server!.Register(park));
                    return new HandlerResult.Parked(park);
                default:
                    var data = new JsonObject { ["cap"] = r.Capability, ["args"] = JsonNode.Parse(r.Args.GetRawText()) };
                    return new HandlerResult.Done(Ok(r.Id, JsonSerializer.SerializeToElement(data)));
            }
        }
        server = new MailboxServer(paths, reaper, runLog, Handle, now: Now, stateStore: store);
        void Ops(string key)
        {
            if (!s.TryGetProperty(key, out var ops))
            {
                return;
            }
            foreach (var op in ops.EnumerateArray())
            {
                var id = op.GetProperty("id").GetString()!;
                _ = op.GetProperty("op").GetString() switch
                {
                    "create" => (object)store.Create(id, "d1", op.GetProperty("cap").GetString()!, 30_000),
                    "guard" => store.ArmExecDispatchGuard(id),
                    "admit" => store.MarkAdmitted(id)!,
                    var other => throw new InvalidOperationException(other),
                };
            }
        }
        var staging = Path.Combine(_root, "drop");
        Directory.CreateDirectory(staging);
        void Drop()
        {
            foreach (var f in s.GetProperty("files").EnumerateArray())
            {
                var fileName = f.GetProperty("name").GetString()!;
                var temp = Path.Combine(staging, fileName);
                File.WriteAllText(temp, FileText(f), new UTF8Encoding(false));
                File.Move(temp, Path.Combine(paths.InboxDir, fileName));
            }
        }
        Ops("preStart");
        var preStartFiles = s.TryGetProperty("preStartFiles", out var psf) && psf.GetBoolean();
        if (preStartFiles)
        {
            Drop();
        }
        server.Start();
        try
        {
            Assert.True(server.IsRunning);
            Ops("postStart");
            if (!preStartFiles)
            {
                Drop();
            }
            var outbox = s.GetProperty("outbox");
            var tally = s.TryGetProperty("outboxTally", out var tl) ? tl.EnumerateObject().Sum(p => p.Value.GetInt32()) : 0;
            var expectedCount = outbox.EnumerateObject().Count() + tally;
            var resume = s.TryGetProperty("resume", out var rs) ? Strings(rs) : [];
            var resumed = false;
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(40))
            {
                if (!resumed && resume.Length > 0 && resume.All(id => store.Get(id)?.Phase == RequestPhase.WaitingRelayUser
                        && File.Exists(Path.Combine(paths.ProcessingDir, id + ".json"))))
                {
                    foreach (var p in s.GetProperty("waitingPhases").EnumerateObject())
                    {
                        Assert.Equal(p.Value.GetString(), store.Get(p.Name)!.Phase.Wire());
                    }
                    foreach (var id in resume)
                    {
                        server.ResumeParked(id);
                        server.ResumeParked(id); // second trigger is a no-op
                    }
                    resumed = true;
                }
                var done = Directory.GetFiles(paths.OutboxDir, "*.json").Length >= expectedCount
                    && Directory.GetFiles(paths.ProcessingDir).Length == 0
                    && Directory.GetFiles(paths.InboxDir).Length == s.GetProperty("inboxLeft").GetArrayLength();
                if (done)
                {
                    break;
                }
                await Task.Delay(50);
            }
            await Task.Delay(200); // let the last settle write its control frame and log lines
            foreach (var p in outbox.EnumerateObject())
            {
                var file = Path.Combine(paths.OutboxDir, p.Name);
                Assert.True(File.Exists(file), $"{name}: missing outbox/{p.Name}");
                AssertResponse(p.Value, File.ReadAllText(file, Encoding.UTF8), $"{name} outbox/{p.Name}");
            }
            if (s.TryGetProperty("outboxTally", out var tallyWant))
            {
                var got = Directory.GetFiles(paths.OutboxDir, "*.json").Select(f => JsonNode.Parse(File.ReadAllText(f))!)
                    .GroupBy(n => n["ok"]!.GetValue<bool>() ? "ok" : n["error"]!["code"]!.GetValue<string>()).ToDictionary(g => g.Key, g => g.ToList());
                foreach (var p in tallyWant.EnumerateObject())
                {
                    Assert.Equal(p.Value.GetInt32(), got.TryGetValue(p.Name, out var l) ? l.Count : 0);
                }
                Assert.Equal(tallyWant.EnumerateObject().Count(), got.Count);
                foreach (var p in s.GetProperty("tallyReasons").EnumerateObject())
                {
                    Assert.All(got[p.Name], n => Assert.StartsWith(p.Value.GetString()![1..], n["reason"]!.GetValue<string>()));
                }
            }
            else
            {
                Assert.Equal(outbox.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal),
                    Directory.GetFiles(paths.OutboxDir).Select(Path.GetFileName).Order(StringComparer.Ordinal)!);
            }
            var control = s.GetProperty("controlOutbox");
            Assert.Equal(control.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal),
                Directory.GetFiles(paths.ControlOutboxDir).Select(Path.GetFileName).Order(StringComparer.Ordinal)!);
            foreach (var p in control.EnumerateObject())
            {
                AssertResponse(p.Value, File.ReadAllText(Path.Combine(paths.ControlOutboxDir, p.Name), Encoding.UTF8), $"{name} control-outbox/{p.Name}");
            }
            if (s.TryGetProperty("phases", out var phases))
            {
                foreach (var p in phases.EnumerateObject())
                {
                    Assert.True(p.Value.GetString() == store.Get(p.Name)?.Phase.Wire(), $"{name} phase {p.Name}: {store.Get(p.Name)?.Phase}");
                }
            }
            Assert.Equal(Strings(s.GetProperty("inboxLeft")).Order(StringComparer.Ordinal),
                Directory.GetFiles(paths.InboxDir).Select(Path.GetFileName).Order(StringComparer.Ordinal)!);
            Assert.Empty(Directory.GetFiles(paths.ProcessingDir));
            if (s.TryGetProperty("budgets", out var b))
            {
                foreach (var p in b.EnumerateObject())
                {
                    Assert.InRange(budgets[p.Name], p.Value[0].GetInt64(), p.Value[1].GetInt64());
                }
            }
            var log = SharedRead.Logs(paths.LogsDir);
            foreach (var line in Strings(s.GetProperty("logContains")))
            {
                Assert.True(log.Contains(line, StringComparison.Ordinal), $"{name}: log lacks '{line}'");
            }
        }
        finally
        {
            var job = server.Completion;
            server.Stop();
            if (job is not null)
            {
                await job.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }
}
