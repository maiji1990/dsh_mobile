using System.Text.Json;
using DshMobile.Runtime.Automation.Relay;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// PR-07 segment 3: C# Relay admission (before the gate) and response encoding over
/// tests/TestVectors/relay-admission.json. The legacy Kotlin LegacyRelayAdmissionParityTest runs the shared cases
/// against upstream; here encodings are compared byte for byte with the text Relay writes on the device.
/// </summary>
public sealed class RelayAdmissionParityTests
{
    private static readonly JsonElement V = Repo.Vectors("relay-admission.json");

    private static List<JsonElement> List(string name) => [.. V.GetProperty(name).EnumerateArray()];

    private static JsonElement Parse(string json) => JsonElement.Parse(json);

    public static TheoryData<string> ArgCases()
    {
        var d = new TheoryData<string>();
        foreach (var c in List("args").Concat(V.GetProperty("csharpOnly").GetProperty("args").EnumerateArray()))
        {
            d.Add(c.GetProperty("name").GetString()!);
        }
        return d;
    }

    [Theory]
    [MemberData(nameof(ArgCases))]
    public void ArgsPreCheck_MatchesTheVector(string name)
    {
        var c = List("args").Concat(V.GetProperty("csharpOnly").GetProperty("args").EnumerateArray())
            .Single(x => x.GetProperty("name").GetString() == name);
        var want = c.GetProperty("expect").ValueKind == JsonValueKind.Null ? null : c.GetProperty("expect").GetString();
        Assert.Equal(want, CapabilityArgsSpec.Validate(c.GetProperty("capability").GetString()!, Parse(c.GetProperty("args").GetString()!)));
    }

    [Fact]
    public void EveryCapability_HasAnArgsSpec_AndTheTablesMatch()
    {
        var category = V.GetProperty("category");
        var limit = V.GetProperty("executionLimit");
        var retry = V.GetProperty("retryPolicy");
        Assert.Equal(RelayProtocol.Capabilities.Count, category.EnumerateObject().Count());
        foreach (var wire in RelayProtocol.Capabilities)
        {
            Assert.Equal(category.GetProperty(wire).GetString(), RelaySemantics.CategoryOf(wire).ToString().ToUpperInvariant());
            Assert.Equal(limit.GetProperty(wire).GetInt32(), RelayRateLimiter.ExecutionLimit(wire));
            Assert.Equal(2 * limit.GetProperty(wire).GetInt32(), RelayRateLimiter.EntryLimit(wire));
            Assert.Equal(retry.GetProperty(wire).GetString(), RelaySemantics.RetryPolicyOf(wire).Wire());
            // Every capability has a spec: an unknown key is always refused.
            Assert.NotNull(CapabilityArgsSpec.Validate(wire, Parse("""{"zz-not-an-arg":1}""")));
        }
        foreach (var e in List("errors"))
        {
            var error = RelayError.FromCode(e.GetProperty("code").GetString()!)!;
            Assert.Equal(e.GetProperty("effectStateOnFailure").GetString(), RelaySemantics.EffectStateFor(false, error).Wire());
            Assert.Equal(e.GetProperty("consentKind").ValueKind == JsonValueKind.Null ? null : e.GetProperty("consentKind").GetString(),
                RelaySemantics.ConsentKindFor(error));
        }
        Assert.Equal(EffectState.Verified, RelaySemantics.EffectStateFor(true, null));
    }

    public static TheoryData<string> AdmissionCases()
    {
        var d = new TheoryData<string>();
        foreach (var c in List("admission"))
        {
            d.Add(c.GetProperty("name").GetString()!);
        }
        return d;
    }

    [Theory]
    [MemberData(nameof(AdmissionCases))]
    public void Admission_MatchesTheVector(string name)
    {
        var c = List("admission").Single(x => x.GetProperty("name").GetString() == name);
        var registered = (c.TryGetProperty("registered", out var r) ? r : V.GetProperty("registered")).EnumerateArray().Select(x => x.GetString()!);
        var admission = new RelayAdmission(registered, V.GetProperty("selfPackage").GetString()!, new RelayRateLimiter(() => 0, RelayRateLimiter.EntryLimit));
        var request = new RelayRequest("req-1", 0, c.GetProperty("capability").GetString()!, Parse(c.GetProperty("args").GetString()!), 5000);
        var got = admission.Check(request, elapsedMs: 7);
        var expect = c.GetProperty("expect");
        if (expect.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(got);
            return;
        }
        Assert.NotNull(got);
        Assert.False(got.Ok);
        Assert.Equal("req-1", got.Id);
        Assert.Equal(7, got.ElapsedMs);
        Assert.Equal(expect.GetProperty("code").GetString(), got.Error!.Code);
        Assert.Equal(expect.GetProperty("reason").GetString(), got.Reason);
        Assert.Equal(expect.GetProperty("retryPolicy").ValueKind == JsonValueKind.Null ? null : expect.GetProperty("retryPolicy").GetString(),
            got.RetryPolicy?.Wire());
        Assert.Equal(expect.GetProperty("effectState").GetString(), got.EffectState?.Wire());
        Assert.Equal(expect.TryGetProperty("consentKind", out var ck) ? ck.GetString() : null, got.ConsentKind);
    }

    [Fact]
    public void EntryFlood_IsRefusedBeforeTheArgsCheck()
    {
        var admission = new RelayAdmission(["ui.snapshot"], "self", new RelayRateLimiter(() => 0, _ => 1));
        Assert.Null(admission.Check(new RelayRequest("a", 0, "ui.snapshot", Parse("{}"), 5000)));
        var flood = admission.Check(new RelayRequest("b", 0, "ui.snapshot", Parse("""{"bad":1}"""), 5000));
        Assert.Equal(RelayError.RateLimited, flood!.Error);
        Assert.Equal("E_RATE_LIMITED/entry_flood", flood.Reason);
    }

    [Fact]
    public void EntryLimiter_MatchesTheVectors()
    {
        foreach (var c in List("rate"))
        {
            long clock = 0;
            var limiter = new RelayRateLimiter(() => clock, RelayRateLimiter.EntryLimit);
            var wire = c.GetProperty("capability").GetString()!;
            var j = 0;
            foreach (var e in c.GetProperty("events").EnumerateArray())
            {
                clock = e.GetProperty("at").GetInt64();
                for (var k = 0; k < e.GetProperty("repeat").GetInt32(); k++)
                {
                    Assert.True(e.GetProperty("accepted").GetBoolean() == limiter.Accepted(wire), $"{c.GetProperty("name").GetString()} event {j} #{k}");
                }
                j++;
            }
        }
    }

    private static T? Opt<T>(JsonElement o, string name, Func<string, T> map) where T : struct =>
        o.TryGetProperty(name, out var p) ? map(p.GetString()!) : null;

    private static SurfaceKind ToSurface(string w) => Enum.GetValues<SurfaceKind>().Single(s => s.Wire() == w);

    [Fact]
    public void Encode_IsByteForByteTheDeviceText()
    {
        foreach (var c in List("encode"))
        {
            var r = c.GetProperty("response");
            var response = new RelayResponse(
                r.GetProperty("id").GetString()!,
                r.GetProperty("ok").GetBoolean(),
                r.TryGetProperty("data", out var data) ? Parse(data.GetString()!) : null,
                r.TryGetProperty("artifacts", out var art) ? [.. art.EnumerateArray().Select(x => x.GetString()!)] : [],
                Opt(r, "surface", ToSurface),
                Opt(r, "degradedFrom", ToSurface),
                r.TryGetProperty("reason", out var reason) ? reason.GetString() : null,
                r.TryGetProperty("error", out var err) ? RelayError.FromCode(err.GetString()!) : null,
                r.GetProperty("elapsedMs").GetInt64(),
                r.TryGetProperty("degradeReason", out var dr) ? dr.GetString() : null,
                Opt(r, "retryPolicy", w => Enum.GetValues<RetryPolicy>().Single(p => p.Wire() == w)),
                Opt(r, "effectState", w => Enum.GetValues<EffectState>().Single(p => p.Wire() == w)),
                r.TryGetProperty("consentKind", out var ck) ? ck.GetString() : null);
            Assert.Equal(c.GetProperty("json").GetString(), RelayEncoding.Encode(response));
        }
        foreach (var c in List("controlEncode"))
        {
            var payload = Parse(c.GetProperty("payload").GetString()!).EnumerateObject()
                .Select(p => new KeyValuePair<string, object?>(p.Name, p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => p.Value.GetInt64(),
                    _ => p.Value,
                }));
            Assert.Equal(c.GetProperty("json").GetString(),
                RelayEncoding.EncodeControl(c.GetProperty("id").GetString()!, c.GetProperty("op").GetString()!, c.GetProperty("ok").GetBoolean(), payload));
        }
    }

    [Theory]
    [InlineData("""{"b":1,"a":[1.0,-0.0,1e20,"x/y",{"k":null}]}""", """{"b":1,"a":[1,-0,1.0E20,"x\/y",{"k":null}]}""")]
    [InlineData("""{"a":1,"a":2}""", """{"a":2}""")]
    [InlineData("""{"s":"é—\u0007"}""", "{\"s\":\"é—\\u0007\"}")]
    public void LibcoreSerialization(string input, string expected) =>
        Assert.Equal(expected, LibcoreJson.Serialize(Parse(input)));

    [Fact]
    public void ControlRejection_EscapesTheSlashLikeRelay() =>
        Assert.Equal("""{"v":2,"id":"c","op":"unknown","ok":false,"cause":"E_TRANSPORT_MALFORMED\/op"}""",
            RelayEncoding.EncodeControlRejection("c", "E_TRANSPORT_MALFORMED/op"));
}
