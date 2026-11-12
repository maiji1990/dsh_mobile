using System.Text.Json;
using DshMobile.Runtime.Automation.Relay;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// PR-07 segment 2: the C# Relay envelope decoder over tests/TestVectors/relay-envelope.json. The legacy Kotlin
/// LegacyRelayEnvelopeParityTest runs the shared "request" / "control" cases against the upstream EnvelopeCodec; the
/// "csharpOnly" cases follow Android libcore org.json (what Relay runs on the device) where the JVM's Maven org.json
/// differs, or are deliberate C# strictness.
/// </summary>
public sealed class RelayEnvelopeParityTests
{
    private static readonly JsonElement Vectors = Repo.Vectors("relay-envelope.json");

    private static List<JsonElement> List(string name) => [.. Vectors.GetProperty(name).EnumerateArray()];

    private static List<JsonElement> CSharpOnly(string name) =>
        Vectors.GetProperty("csharpOnly").TryGetProperty(name, out var l) ? [.. l.EnumerateArray()] : [];

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var list in new[] { "request", "control" })
        {
            foreach (var c in List(list))
            {
                data.Add(list, c.GetProperty("name").GetString()!);
            }
        }
        foreach (var c in CSharpOnly("request"))
        {
            data.Add("csharpOnly.request", c.GetProperty("name").GetString()!);
        }
        return data;
    }

    private static JsonElement Case(string list, string name)
    {
        var cases = list switch
        {
            "csharpOnly.request" => CSharpOnly("request"),
            _ => List(list),
        };
        return cases.Single(c => c.GetProperty("name").GetString() == name);
    }

    /// <summary>The request text, padded with trailing spaces to <c>padTo</c> UTF-16 chars when asked.</summary>
    internal static string Text(JsonElement c)
    {
        var text = c.GetProperty("text").GetString()!;
        if (c.TryGetProperty("padTo", out var pad))
        {
            var n = pad.GetInt32();
            Assert.True(n >= text.Length, "padTo shorter than the text");
            text = text.PadRight(n, ' ');
        }
        return text;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Decode_MatchesTheVector(string list, string name)
    {
        var c = Case(list, name);
        var text = Text(c);
        var file = c.GetProperty("file").GetString()!;
        var expect = c.GetProperty("expect");
        if (list == "control")
        {
            AssertControl(name, expect, RelayEnvelope.DecodeControl(text, file));
        }
        else
        {
            AssertRequest(name, expect, RelayEnvelope.DecodeRequest(text, file));
        }
    }

    private static void AssertRejected(string name, JsonElement r, string id, RelayError error, string cause)
    {
        Assert.Equal(r.GetProperty("id").GetString(), id);
        Assert.Equal(r.GetProperty("code").GetString(), error.Code);
        Assert.Equal(r.GetProperty("cause").GetString(), cause);
        Assert.Same(RelayError.FromCode(error.Code), error);
        _ = name;
    }

    private static void AssertRequest(string name, JsonElement expect, RelayDecodeResult result)
    {
        if (expect.TryGetProperty("rejected", out var r))
        {
            var rejected = Assert.IsType<RelayDecodeResult.Rejected>(result);
            AssertRejected(name, r, rejected.Id, rejected.Error, rejected.Cause);
            return;
        }
        var v = expect.GetProperty("valid");
        var req = Assert.IsType<RelayDecodeResult.Valid>(result).Request;
        Assert.Equal(v.GetProperty("id").GetString(), req.Id);
        Assert.Equal(v.GetProperty("sentAtMs").GetInt64(), req.SentAtMs);
        Assert.Equal(v.GetProperty("capability").GetString(), req.Capability);
        Assert.True(JsonElement.DeepEquals(v.GetProperty("args"), req.Args), $"{name}: args {req.Args.GetRawText()}");
        Assert.Equal(v.GetProperty("timeoutMs").GetInt64(), req.TimeoutMs);
    }

    private static void AssertControl(string name, JsonElement expect, ControlDecodeResult result)
    {
        if (expect.TryGetProperty("rejected", out var r))
        {
            var rejected = Assert.IsType<ControlDecodeResult.Rejected>(result);
            AssertRejected(name, r, rejected.Id, rejected.Error, rejected.Cause);
            return;
        }
        var v = expect.GetProperty("valid");
        var control = Assert.IsType<ControlDecodeResult.Valid>(result).Control;
        Assert.Equal(v.GetProperty("id").GetString(), control.Id);
        switch (v.GetProperty("op").GetString())
        {
            case "health":
                Assert.IsType<ControlRequest.Health>(control);
                break;
            case "status":
                Assert.Equal(v.GetProperty("targetId").GetString(), Assert.IsType<ControlRequest.Status>(control).TargetId);
                break;
            case "cancel":
                Assert.Equal(v.GetProperty("targetId").GetString(), Assert.IsType<ControlRequest.Cancel>(control).TargetId);
                break;
            case "submit":
                var s = Assert.IsType<ControlRequest.Submit>(control);
                Assert.Equal(v.GetProperty("capability").GetString(), s.Capability);
                Assert.True(JsonElement.DeepEquals(v.GetProperty("args"), s.Args), $"{name}: args {s.Args.GetRawText()}");
                Assert.Equal(v.GetProperty("hostTtlMs").GetInt64(), s.HostTtlMs);
                Assert.Equal(v.GetProperty("payloadDigest").GetString(), s.PayloadDigest);
                break;
            case "ask":
                var a = Assert.IsType<ControlRequest.Ask>(control);
                Assert.Equal(v.GetProperty("question").GetString(), a.Question);
                Assert.Equal(v.GetProperty("options").EnumerateArray().Select(o => o.GetString()!), a.Options);
                Assert.Equal(v.GetProperty("timeoutMs").GetInt64(), a.TimeoutMs);
                break;
            default:
                Assert.Fail($"{name}: unknown op in the vector");
                break;
        }
    }

    [Fact]
    public void Vectors_AreNotEmpty_AndProbeCasesAreAlwaysRejections()
    {
        Assert.True(List("request").Count >= 30);
        Assert.True(List("control").Count >= 30);
        var probes = List("request").Concat(List("control")).Where(c => c.TryGetProperty("probe", out var p) && p.GetBoolean()).ToList();
        Assert.True(probes.Count >= 10);
        // The device probe writes these into the real mailbox: none may ever decode as valid.
        Assert.All(probes, c => Assert.True(c.GetProperty("expect").TryGetProperty("rejected", out _), c.GetProperty("name").GetString()));
        // Probe file names are unique and are request file names Relay will pick up.
        var files = probes.Select(c => c.GetProperty("file").GetString()!).ToList();
        Assert.Equal(files.Count, files.Distinct(StringComparer.Ordinal).Count());
        Assert.All(files, f => Assert.True(RelayEnvelope.IsRequestFileName(f), f));
    }

    [Fact]
    public void CapabilityAndErrorTables_MatchTheVectors()
    {
        Assert.Equal(Vectors.GetProperty("capabilities").EnumerateArray().Select(e => e.GetString()!), RelayProtocol.Capabilities);
        var errors = Vectors.GetProperty("errors").EnumerateArray().ToList();
        Assert.Equal(errors.Count, RelayError.All.Count);
        for (var i = 0; i < errors.Count; i++)
        {
            var e = errors[i];
            var c = RelayError.All[i];
            Assert.Equal(e.GetProperty("name").GetString(), c.Name);
            Assert.Equal(e.GetProperty("code").GetString(), c.Code);
            Assert.Equal(e.GetProperty("retryable").GetBoolean(), c.Retryable);
            Assert.Equal(e.GetProperty("exitCode").GetInt32(), c.ExitCode);
        }
        Assert.Equal(RelayError.All.Count, RelayError.All.Select(e => e.Code).Distinct().Count());
    }

    [Theory]
    [InlineData("a.json", true)]
    [InlineData("A-b_9.json", true)]
    [InlineData(".a.json.part", false)]
    [InlineData("a.json.part", false)]
    [InlineData("a.JSON", false)]
    [InlineData("a b.json", false)]
    [InlineData(".json", false)]
    [InlineData("a.json\n", false)]
    public void IsRequestFileName_IsAnExactAllowlist(string name, bool expected) =>
        Assert.Equal(expected, RelayEnvelope.IsRequestFileName(name));

    [Theory]
    [InlineData("{}", 1)]
    [InlineData("{\"a\":[[{}]]}", 4)]
    [InlineData("{\"a\":\"[[[\"}", 1)]
    [InlineData("{\"a\":\"\\\\\",\"b\":[]}", 2)] // an escaped backslash does not hide the closing quote
    [InlineData("", 0)]
    public void NestingDepth_IsStringAware(string text, int expected) =>
        Assert.Equal(expected, RelayEnvelope.NestingDepth(text));
}

/// <summary>The libcore org.json read semantics the decoder depends on.</summary>
public sealed class OrgJsonTests
{
    private static JsonElement Obj(string json) => OrgJson.ParseObject(json) ?? throw new InvalidOperationException(json);

    [Theory]
    [InlineData("{\"x\":\"s\"}", "s")]
    [InlineData("{\"x\":null}", "null")]
    [InlineData("{}", "")]
    [InlineData("{\"x\":true}", "true")]
    [InlineData("{\"x\":123}", "123")]
    [InlineData("{\"x\":-0}", "0")]
    [InlineData("{\"x\":1.0}", "1.0")]
    [InlineData("{\"x\":1.5}", "1.5")]
    [InlineData("{\"x\":1e3}", "1000.0")]
    [InlineData("{\"x\":1e7}", "1.0E7")]
    [InlineData("{\"x\":0.001}", "0.001")]
    [InlineData("{\"x\":0.0001}", "1.0E-4")]
    [InlineData("{\"x\":123456789.25}", "1.2345678925E8")]
    [InlineData("{\"x\":9223372036854775808}", "9.223372036854776E18")]
    public void OptString_FollowsStringValueOf(string json, string expected) =>
        Assert.Equal(expected, OrgJson.OptString(Obj(json), "x"));

    [Theory]
    [InlineData("{\"x\":1}", 1)]
    [InlineData("{\"x\":\"1\"}", 1)]
    [InlineData("{\"x\":\" 2 \"}", 2)]
    [InlineData("{\"x\":\"2.9\"}", 2)]
    [InlineData("{\"x\":\"-2.9\"}", -2)]
    [InlineData("{\"x\":\"1e1\"}", 10)]
    [InlineData("{\"x\":\"3f\"}", 3)]
    [InlineData("{\"x\":\"0x10\"}", 0)]
    [InlineData("{\"x\":\"abc\"}", 0)]
    [InlineData("{\"x\":\"\"}", 0)]
    [InlineData("{\"x\":\"NaN\"}", 0)]
    [InlineData("{\"x\":\"Infinity\"}", int.MaxValue)]
    [InlineData("{\"x\":true}", 0)]
    [InlineData("{\"x\":null}", 0)]
    [InlineData("{\"x\":{}}", 0)]
    [InlineData("{\"x\":4294967297}", 1)]
    [InlineData("{\"x\":1e20}", int.MaxValue)]
    [InlineData("{\"x\":-1e20}", int.MinValue)]
    [InlineData("{\"x\":1.9}", 1)]
    public void OptInt_FollowsLibcore(string json, int expected) =>
        Assert.Equal(expected, OrgJson.OptInt(Obj(json), "x"));

    [Theory]
    [InlineData("{\"x\":1762900000000}", 1762900000000L)]
    [InlineData("{\"x\":\"3000\"}", 3000L)]
    [InlineData("{\"x\":1e19}", long.MaxValue)]
    [InlineData("{\"x\":false}", 0L)]
    public void OptLong_FollowsLibcore(string json, long expected) =>
        Assert.Equal(expected, OrgJson.OptLong(Obj(json), "x"));

    [Theory]
    [InlineData("[]")]
    [InlineData("1")]
    [InlineData("\"s\"")]
    [InlineData("null")]
    [InlineData("{'a':1}")]
    [InlineData("{\"a\":1,}")]
    [InlineData("{\"a\":1} x")]
    [InlineData("{a:1}")]
    public void ParseObject_OnlyAcceptsStrictJsonObjects(string text) => Assert.Null(OrgJson.ParseObject(text));

    [Fact]
    public void DuplicateNames_LastOneWins() =>
        Assert.Equal("b", OrgJson.OptString(Obj("{\"x\":\"a\",\"x\":\"b\"}"), "x"));

    [Theory]
    [InlineData(" a ", "a")]
    [InlineData("　a　", "a")]
    [InlineData("\u001Fa\u001C", "a")]
    [InlineData("\u0085a", "\u0085a")] // NEL is not Kotlin whitespace (.NET Trim would strip it)
    [InlineData(" a", "a")] // no-break space is a Unicode space separator
    [InlineData("\t\n\u000B\f\r", "")]
    public void KotlinTrim_UsesKotlinWhitespace(string input, string expected) =>
        Assert.Equal(expected, OrgJson.KotlinTrim(input));
}
