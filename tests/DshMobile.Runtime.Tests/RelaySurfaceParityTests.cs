using System.Text.Json;
using DshMobile.Runtime.Automation.Relay;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// PR-07 segment 4: the C# execution-surface decision, the steps around the gate and the execution quota over
/// tests/TestVectors/relay-surface.json. The legacy Kotlin LegacyRelaySurfaceParityTest runs the same registry,
/// decide, stage and quota cases against upstream; responses are compared here byte for byte with device text.
/// </summary>
public sealed class RelaySurfaceParityTests
{
    private static readonly JsonElement V = Repo.Vectors("relay-surface.json");

    private static List<JsonElement> List(string name) => [.. V.GetProperty(name).EnumerateArray()];

    private static SurfaceKind Kind(string wire) => RelaySurfaces.FromWire(wire) ?? throw new ArgumentException(wire);

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static string Describe(SurfaceDecision d) =>
        $"{d.Surface.Wire()}|{d.DegradedFrom?.Wire()}|{d.Reason}|{d.RequiresMode?.Wire()}";

    private static string Describe(JsonElement e) =>
        $"{Str(e, "surface")}|{Str(e, "degradedFrom")}|{Str(e, "reason")}|{Str(e, "requiresMode")}";

    private static HashSet<SurfaceKind> CannotServe(JsonElement c) =>
        c.TryGetProperty("cannotServe", out var a) ? [.. a.EnumerateArray().Select(x => Kind(x.GetString()!))] : [];

    private static SurfacePolicy PolicyOf(JsonElement c)
    {
        var cannot = CannotServe(c);
        return new(() => RelaySurfaces.ParsePreference(c.GetProperty("preference").GetString()),
            () => c.GetProperty("shell").GetBoolean(), (_, s) => !cannot.Contains(s), c.GetProperty("sdk").GetInt32());
    }

    [Fact]
    public void Registry_MatchesUpstream_InOrder()
    {
        var registry = V.GetProperty("registry");
        Assert.Equal(RelayProtocol.Capabilities, registry.EnumerateObject().Select(p => p.Name));
        foreach (var p in registry.EnumerateObject())
        {
            Assert.Equal(p.Value.GetProperty("surfaces").EnumerateArray().Select(x => x.GetString()), RelaySurfaces.SurfacesOf(p.Name).Select(s => s.Wire()));
            Assert.Equal(p.Value.GetProperty("backends").EnumerateArray().Select(x => x.GetString()),
                RelaySurfaces.BackendsOf(p.Name).Select(b => b.ToString().ToLowerInvariant()));
        }
    }

    public static TheoryData<string> Names(string section)
    {
        var d = new TheoryData<string>();
        foreach (var c in List(section))
        {
            d.Add(c.GetProperty("name").GetString()!);
        }
        return d;
    }

    private static JsonElement Case(string section, string name) => List(section).Single(c => c.GetProperty("name").GetString() == name);

    [Theory]
    [MemberData(nameof(Names), "decide")]
    public void Decide_MatchesTheVector(string name)
    {
        var c = Case("decide", name);
        var wire = c.GetProperty("capability").GetString()!;
        var policy = PolicyOf(c);
        var decision = c.TryGetProperty("surfaces", out var s)
            ? policy.Decide(wire, [.. s.EnumerateArray().Select(x => Kind(x.GetString()!))])
            : policy.Decide(wire);
        Assert.Equal(Describe(c.GetProperty("expect")), Describe(decision));
    }

    [Theory]
    [MemberData(nameof(Names), "stage")]
    public void BeforeGate_MatchesTheVector_ByteForByte(string name)
    {
        var c = Case("stage", name);
        var implemented = c.GetProperty("implemented").GetBoolean();
        var stage = new RelayExecutionStage(PolicyOf(c), _ => implemented, new RelayRateLimiter(() => 0, RelayRateLimiter.ExecutionLimit),
            V.GetProperty("selfPackage").GetString()!);
        var request = new RelayRequest(c.GetProperty("id").GetString()!, 0, c.GetProperty("capability").GetString()!,
            JsonElement.Parse(c.GetProperty("args").GetString()!), 30_000);
        var result = stage.BeforeGate(request, elapsedMs: 3);
        Assert.Equal(Describe(c.GetProperty("decision")), Describe(result.Decision));
        var rejected = c.GetProperty("rejected");
        if (rejected.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(result.Rejection);
            return;
        }
        Assert.NotNull(result.Rejection);
        Assert.Equal(rejected.GetProperty("code").GetString(), result.Rejection.Error!.Code);
        Assert.Equal(rejected.GetProperty("json").GetString(), RelayEncoding.Encode(result.Rejection));
    }

    [Theory]
    [MemberData(nameof(Names), "execQuota")]
    public void ExecutionQuota_MatchesTheVector(string name)
    {
        var c = Case("execQuota", name);
        var wire = c.GetProperty("capability").GetString()!;
        Assert.Equal(c.GetProperty("limit").GetInt32(), RelayRateLimiter.ExecutionLimit(wire));
        long clock = 0;
        var stage = new RelayExecutionStage(new SurfacePolicy(() => SurfacePreference.Foreground, () => false, null, 36), _ => true,
            new RelayRateLimiter(() => clock, RelayRateLimiter.ExecutionLimit), "self");
        var request = new RelayRequest("q", 0, wire, JsonElement.Parse("{}"), 30_000);
        var n = 0;
        foreach (var e in c.GetProperty("events").EnumerateArray())
        {
            clock = e.GetProperty("at").GetInt64();
            for (var i = 0; i < e.GetProperty("repeat").GetInt32(); i++, n++)
            {
                var r = stage.AfterGate(request, new SurfaceDecision(SurfaceKind.Foreground), e.GetProperty("approval").GetBoolean());
                Assert.True(e.GetProperty("accepted").GetBoolean() == (r is null), $"event {n} at {clock}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Names), "execReject")]
    public void ExecutionQuotaRefusal_IsByteIdentical(string name)
    {
        var c = Case("execReject", name);
        var d = c.GetProperty("decision");
        var decision = new SurfaceDecision(Kind(Str(d, "surface")!), Str(d, "degradedFrom") is { } f ? Kind(f) : null, Str(d, "reason"));
        var stage = new RelayExecutionStage(new SurfacePolicy(() => SurfacePreference.Foreground, () => false, null, 36), _ => true,
            new RelayRateLimiter(() => 0, _ => 0), "self");
        var request = new RelayRequest(c.GetProperty("id").GetString()!, 0, c.GetProperty("capability").GetString()!, JsonElement.Parse("{}"), 30_000);
        var r = stage.AfterGate(request, decision, requiresApproval: false, elapsedMs: 4);
        Assert.NotNull(r);
        Assert.Equal(c.GetProperty("json").GetString(), RelayEncoding.Encode(r));
        // The approval path is never charged here, even with the quota exhausted.
        Assert.Null(stage.AfterGate(request, decision, requiresApproval: true, elapsedMs: 4));
    }

    public static TheoryData<string> FactNames()
    {
        var d = new TheoryData<string>();
        foreach (var f in V.GetProperty("csharpOnly").GetProperty("backendMatrix").EnumerateArray())
        {
            d.Add(f.GetProperty("name").GetString()!);
        }
        return d;
    }

    [Theory]
    [MemberData(nameof(FactNames))]
    public void BackendMatrix_MatchesTheVector(string name)
    {
        var f = V.GetProperty("csharpOnly").GetProperty("backendMatrix").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        var facts = new RelayBackendFacts(f.GetProperty("sdk").GetInt32(), f.GetProperty("shell").GetBoolean(), f.GetProperty("displayAlive").GetBoolean());
        var got = new List<string>();
        foreach (var wire in RelayProtocol.Capabilities)
        {
            foreach (var b in new[] { RelayBackendId.Direct, RelayBackendId.A11y, RelayBackendId.Shizuku })
            {
                foreach (var s in new[] { SurfaceKind.Foreground, SurfaceKind.BackendTrusted, SurfaceKind.ObserveOnly })
                {
                    if (RelayBackendSupport.Supports(b, wire, s, facts))
                    {
                        got.Add($"{b.ToString().ToLowerInvariant()}:{wire}@{s.Wire()}");
                    }
                }
            }
        }
        Assert.Equal(f.GetProperty("supports").EnumerateArray().Select(x => x.GetString()), got);
        Assert.Equal(f.GetProperty("notImplemented").EnumerateArray().Select(x => x.GetString()),
            RelayProtocol.Capabilities.Where(w => !RelayBackendSupport.IsImplemented(w, facts)));
        foreach (var (key, pref) in new[] { ("decideForeground", SurfacePreference.Foreground), ("decideBackground", SurfacePreference.BackgroundPreferred) })
        {
            var policy = SurfacePolicy.For(pref, facts);
            foreach (var p in f.GetProperty(key).EnumerateObject())
            {
                Assert.Equal(Describe(p.Value), Describe(policy.Decide(p.Name)));
            }
        }
    }

    [Fact]
    public void Preference_IsTheExactEnumName_ElseForeground()
    {
        Assert.Equal(SurfacePreference.BackgroundPreferred, RelaySurfaces.ParsePreference("BACKGROUND_PREFERRED"));
        Assert.Equal(SurfacePreference.Foreground, RelaySurfaces.ParsePreference("FOREGROUND"));
        Assert.Equal(SurfacePreference.Foreground, RelaySurfaces.ParsePreference("background_preferred"));
        Assert.Equal(SurfacePreference.Foreground, RelaySurfaces.ParsePreference(null));
        Assert.Equal(SurfacePreference.Foreground, RelaySurfaces.ParsePreference(""));
    }

    [Fact]
    public void ModeRequired_IsDecidedBeforeImplementation_AndNeverReachesTheGate()
    {
        var policy = new SurfacePolicy(() => SurfacePreference.Foreground, () => true, null, 36);
        var asked = false;
        var stage = new RelayExecutionStage(policy, _ => asked = true, new RelayRateLimiter(() => 0, RelayRateLimiter.ExecutionLimit), "self");
        var r = stage.BeforeGate(new RelayRequest("m", 0, "surface.virtual", JsonElement.Parse("{}"), 30_000));
        Assert.Equal(RelayError.SurfaceModeRequired, r.Rejection!.Error);
        Assert.False(asked);
    }
}
