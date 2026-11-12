using System.Text.Json;
using DshMobile.Runtime.Automation.Relay;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// PR-07 segment 5: the C# approval-gate decision over tests/TestVectors/relay-gate.json. The legacy Kotlin
/// LegacyRelayGateParityTest drives the real upstream InterlockGate.authorize, TierStore and SystemStateProbe with the
/// same cases; refusal responses are compared here byte for byte with device text.
/// </summary>
public sealed class RelayGateParityTests
{
    private static readonly JsonElement V = Repo.Vectors("relay-gate.json");

    private static List<JsonElement> List(string name) => [.. V.GetProperty(name).EnumerateArray()];

    private static T Enum<T>(string upperSnake) where T : struct, System.Enum =>
        System.Enum.Parse<T>(upperSnake.Replace("_", "", StringComparison.Ordinal), ignoreCase: true);

    private static string Snake<T>(T value) where T : struct, System.Enum =>
        string.Concat(value.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + c : c.ToString())).ToUpperInvariant();

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    [Fact]
    public void PolicyTable_MatchesUpstream()
    {
        var policy = V.GetProperty("policy");
        Assert.Equal(RelayProtocol.Capabilities, policy.EnumerateObject().Select(p => p.Name));
        foreach (var p in policy.EnumerateObject())
        {
            Assert.Equal(p.Value.GetProperty("ceiling").GetString(), Snake(RelayGatePolicy.CeilingOf(p.Name)));
            Assert.Equal(p.Value.GetProperty("permissions").EnumerateArray().Select(x => x.GetString()), RelayGatePolicy.RuntimePermissionsOf(p.Name));
        }
    }

    [Fact]
    public void Verdict_EveryCombination()
    {
        var cases = List("verdict");
        Assert.Equal(54, cases.Count);
        foreach (var c in cases)
        {
            var system = Enum<SystemGrantState>(c.GetProperty("system").GetString()!);
            var tier = Enum<AccessTier>(c.GetProperty("tier").GetString()!);
            var got = RelayGatePolicy.Verdict(system, tier, Enum<TierCeiling>(c.GetProperty("ceiling").GetString()!));
            var expect = c.GetProperty("expect");
            var name = $"{system}/{tier}/{c.GetProperty("ceiling").GetString()}";
            if (expect.ValueKind == JsonValueKind.Null)
            {
                Assert.True(got is null, name);
                continue;
            }
            Assert.NotNull(got);
            Assert.False(got.Allowed);
            Assert.False(got.RequiresApproval);
            Assert.Equal((expect.GetProperty("error").GetString(), expect.GetProperty("label").GetString()), (got.Error!.Code, got.DecisionLabel));
            Assert.Equal((system, tier), (got.SystemState, got.Tier));
        }
        // Shared with the capability list: exactly the three states the user must go and grant.
        Assert.Equal([SystemGrantState.RuntimeAskable, SystemGrantState.ManualOnly, SystemGrantState.UnavailableOnDevice],
            System.Enum.GetValues<SystemGrantState>().Where(RelayGatePolicy.SystemBlocks));
    }

    [Fact]
    public void RequiresApproval_EveryCombination()
    {
        var cases = List("requiresApproval");
        Assert.Equal(36, cases.Count);
        foreach (var c in cases)
        {
            Assert.True(c.GetProperty("expect").GetBoolean() == RelayGatePolicy.RequiresApproval(
                Enum<AccessTier>(c.GetProperty("tier").GetString()!), Enum<TierCeiling>(c.GetProperty("ceiling").GetString()!),
                c.GetProperty("sessionGranted").GetBoolean(), c.GetProperty("consentScreen").GetBoolean()), c.ToString());
        }
    }

    [Fact]
    public void OutcomesChoicesAndLabels()
    {
        foreach (var c in List("outcomes"))
        {
            var outcome = System.Enum.Parse<ApprovalOutcome>(c.GetProperty("outcome").GetString()!);
            Assert.Equal(Str(c, "errorFor"), RelayGatePolicy.ErrorFor(outcome)?.Code);
            Assert.Equal(c.GetProperty("label").GetString(), RelayGatePolicy.ApprovalLabel(outcome, RelayGatePolicy.ErrorFor(outcome) ?? RelayError.GateHostDenied));
        }
        foreach (var p in V.GetProperty("choices").EnumerateObject())
        {
            Assert.Equal(p.Value.GetString(), RelayGatePolicy.OutcomeOf(Enum<InterlockChoice>(p.Name)).ToString());
        }
    }

    [Fact]
    public void Tiers_MatchTheVector()
    {
        foreach (var c in List("tiers"))
        {
            var prefs = c.GetProperty("prefs").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
            var store = new RelayTierStore(k => prefs.GetValueOrDefault(k));
            Assert.Equal(c.GetProperty("expect").GetString(), Snake(store.TierOf(c.GetProperty("capability").GetString()!)));
        }
    }

    private static RelaySystemFacts Facts(JsonElement f) => new(
        f.GetProperty("sdk").GetInt32(), f.GetProperty("a11yBound").GetBoolean(), f.GetProperty("a11yListed").GetBoolean(),
        f.GetProperty("shell").GetBoolean(), f.GetProperty("trusted").GetBoolean(), f.GetProperty("notif").GetBoolean(),
        f.GetProperty("granted").EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal),
        f.GetProperty("micBlocked").GetBoolean());

    public static TheoryData<string> ProbeSets()
    {
        var d = new TheoryData<string>();
        foreach (var s in List("probe"))
        {
            d.Add(s.GetProperty("facts").GetProperty("name").GetString()!);
        }
        return d;
    }

    [Theory]
    [MemberData(nameof(ProbeSets))]
    public void SystemProbe_MatchesTheVector(string name)
    {
        var set = List("probe").Single(s => s.GetProperty("facts").GetProperty("name").GetString() == name);
        var facts = Facts(set.GetProperty("facts"));
        var rows = set.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(RelayProtocol.Capabilities.Count * 4, rows.Count);
        foreach (var r in rows)
        {
            var wire = r.GetProperty("capability").GetString()!;
            var route = Str(r, "route") is { } w ? RelaySurfaces.FromWire(w) : null;
            var got = RelaySystemProbe.Probe(wire, route, facts);
            Assert.Equal($"{wire}@{Str(r, "route")}: {r.GetProperty("state").GetString()} [{string.Join(",", r.GetProperty("gaps").EnumerateArray().Select(x => x.GetString()))}]",
                $"{wire}@{Str(r, "route")}: {Snake(got.State)} [{string.Join(",", got.Gaps)}]");
        }
    }

    public static TheoryData<string> Scenarios()
    {
        var d = new TheoryData<string>();
        foreach (var s in List("authorize"))
        {
            d.Add(s.GetProperty("name").GetString()!);
        }
        return d;
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Authorize_MatchesTheScenario(string name)
    {
        var s = List("authorize").Single(x => x.GetProperty("name").GetString() == name);
        var prefs = s.GetProperty("prefs").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        long clock = 0;
        var gate = new RelayGate(new RelayTierStore(k => prefs.GetValueOrDefault(k)), () => clock);
        foreach (var st in s.GetProperty("steps").EnumerateArray())
        {
            clock = st.GetProperty("at").GetInt64();
            var expect = st.GetProperty("expect");
            var repeat = st.TryGetProperty("repeat", out var rp) ? rp.GetInt32() : 1;
            var distinct = st.TryGetProperty("distinct", out var ds) && ds.GetBoolean();
            var from = st.TryGetProperty("from", out var fr) ? fr.GetInt32() : 0;
            var indexes = new List<int>();
            for (var i = 0; i < repeat; i++)
            {
                var ident = st.GetProperty("argsIdentity").GetString()!;
                var call = new GateCall(st.GetProperty("capability").GetString()!, distinct ? $"{ident}-{from + i}" : ident,
                    new ProbeResult(Enum<SystemGrantState>(st.GetProperty("system").GetString()!), []), st.GetProperty("budgetMs").GetInt64(),
                    st.TryGetProperty("consentScreen", out var cs) && cs.GetBoolean(),
                    st.TryGetProperty("hostBringToFront", out var hb) && hb.GetBoolean());
                GatePrompt? shown = null;
                var choice = Enum<InterlockChoice>(st.GetProperty("choice").GetString()!);
                var r = await gate.AuthorizeAsync(call, (p, _) => { shown = p; return Task.FromResult(choice); }, CancellationToken.None);
                var where = $"{name} @ {clock} {call.Capability} {call.ArgsIdentity}";
                Assert.True(expect.GetProperty("allowed").GetBoolean() == r.Allowed, where);
                Assert.True(expect.GetProperty("requiresApproval").GetBoolean() == r.RequiresApproval, where);
                Assert.Equal(Str(expect, "error"), r.Error?.Code);
                Assert.Equal(expect.GetProperty("label").GetString(), r.DecisionLabel);
                Assert.True(expect.GetProperty("presented").GetBoolean() == shown is not null, where + " presented");
                if (shown is not null)
                {
                    Assert.True(expect.GetProperty("allowsSessionGrant").GetBoolean() == shown.AllowsSessionGrant, where);
                    indexes.Add(shown.CallIndexInSession);
                }
            }
            if (expect.TryGetProperty("callIndexInSession", out var idx))
            {
                Assert.Equal(idx.EnumerateArray().Select(x => x.GetInt32()), indexes);
            }
        }
    }

    [Fact]
    public void DecideWithoutPrompt_NeverCountsTowardTheApprovalFrequency()
    {
        long clock = 0;
        var gate = new RelayGate(new RelayTierStore(_ => null), () => clock);
        var call = new GateCall("ui.node", "same", ProbeResult.GrantedResult, 30_000);
        for (var i = 0; i < 100; i++)
        {
            Assert.Null(gate.DecideWithoutPrompt(call with { ArgsIdentity = "id" + i }));
        }
        // Still 30 fresh questions available: nothing above was charged.
        for (var i = 0; i < 30; i++)
        {
            var r = gate.AuthorizeAsync(call with { ArgsIdentity = "q" + i }, (_, _) => Task.FromResult(InterlockChoice.Awaiting), CancellationToken.None).Result;
            Assert.Equal("awaiting_consent", r.DecisionLabel);
        }
    }

    [Fact]
    public async Task APresenterThatNeverAnswers_TimesOutOrAwaits_ByWhetherTheBudgetCutTheWindow()
    {
        var gate = new RelayGate(new RelayTierStore(_ => null), () => 0, approvalWindowMs: 5_200);
        static async Task<InterlockChoice> Never(GatePrompt p, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return InterlockChoice.AllowOnce;
        }
        var full = await gate.AuthorizeAsync(new GateCall("ui.node", "a", ProbeResult.GrantedResult, 30_000), Never, CancellationToken.None);
        Assert.Equal(("E_GATE_APPROVAL_TIMEOUT", "expired_E_GATE_APPROVAL_TIMEOUT"), (full.Error!.Code, full.DecisionLabel));
        var cut = await gate.AuthorizeAsync(new GateCall("ui.node", "b", ProbeResult.GrantedResult, 5_100), Never, CancellationToken.None);
        Assert.Equal(("E_AWAITING_CONSENT", "awaiting_consent"), (cut.Error!.Code, cut.DecisionLabel));
    }

    [Fact]
    public void Refusals_AreByteIdentical()
    {
        foreach (var c in List("refusals"))
        {
            var stage = new RelayExecutionStage(new SurfacePolicy(() => SurfacePreference.Foreground, () => false, null, 37), _ => true,
                new RelayRateLimiter(() => 0, RelayRateLimiter.ExecutionLimit), "self");
            var gate = new GateResult(false, false, RelayError.FromCode(c.GetProperty("error").GetString()!), DecisionLabel: c.GetProperty("label").GetString()!);
            var request = new RelayRequest(c.GetProperty("id").GetString()!, 0, c.GetProperty("capability").GetString()!, JsonElement.Parse("{}"), 30_000);
            var r = stage.AfterGate(request, new SurfaceDecision(SurfaceKind.Foreground), gate, elapsedMs: 7);
            Assert.NotNull(r);
            Assert.Equal(c.GetProperty("json").GetString(), RelayEncoding.Encode(r));
        }
        // The dead-end hint is appended only to E_GATE_NO_FOREGROUND.
        var s = new RelayExecutionStage(new SurfacePolicy(() => SurfacePreference.Foreground, () => false, null, 37), _ => true,
            new RelayRateLimiter(() => 0, RelayRateLimiter.ExecutionLimit), "self");
        var req = new RelayRequest("h", 0, "ui.node", JsonElement.Parse("{}"), 30_000);
        Assert.Equal("E_GATE_NO_FOREGROUND/no_surface; turn notifications on",
            s.AfterGate(req, new SurfaceDecision(SurfaceKind.Foreground), new GateResult(false, true, RelayError.GateNoForeground, DecisionLabel: "no_surface"), 0, "turn notifications on")!.Reason);
        Assert.Equal("E_GATE_HOST_DENIED/tier_denied",
            s.AfterGate(req, new SurfaceDecision(SurfaceKind.Foreground), new GateResult(false, false, RelayError.GateHostDenied, DecisionLabel: "tier_denied"), 0, "ignored")!.Reason);
    }
}
