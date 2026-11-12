// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/transport/EnvelopeCodec.kt (decodeRequest, decodeControl and its helpers,
//   isValidId, isRequestFileName, nestingDepth, size / TTL / ask limits)
// Registered in docs/UPSTREAMS.md. Behaviour locked by tests/TestVectors/relay-envelope.json (the legacy Kotlin
// LegacyRelayEnvelopeParityTest runs the same cases against the upstream codec). Response encoding is not ported yet.

using System.Text.Json;
using System.Text.RegularExpressions;

namespace DshMobile.Runtime.Automation.Relay;

/// <summary>A well-formed v1 capability request.</summary>
public sealed record RelayRequest(string Id, long SentAtMs, string Capability, JsonElement Args, long TimeoutMs);

/// <summary>Decoding outcome: a rejection always carries an id, so the caller can answer instead of timing out.</summary>
public abstract record RelayDecodeResult
{
    public sealed record Valid(RelayRequest Request) : RelayDecodeResult;

    public sealed record Rejected(string Id, RelayError Error, string Cause) : RelayDecodeResult;
}

/// <summary>A v2 control request: one subtype per op, so each op's required fields are explicit.</summary>
public abstract record ControlRequest(string Id)
{
    public sealed record Submit(string Id, string Capability, JsonElement Args, long HostTtlMs, string PayloadDigest) : ControlRequest(Id);

    public sealed record Status(string Id, string TargetId) : ControlRequest(Id);

    public sealed record Cancel(string Id, string TargetId) : ControlRequest(Id);

    public sealed record Health(string Id) : ControlRequest(Id);

    public sealed record Ask(string Id, string Question, IReadOnlyList<string> Options, long TimeoutMs) : ControlRequest(Id);
}

public abstract record ControlDecodeResult
{
    public sealed record Valid(ControlRequest Control) : ControlDecodeResult;

    public sealed record Rejected(string Id, RelayError Error, string Cause) : ControlDecodeResult;
}

/// <summary>
/// Envelope decoding of the Relay mailbox. Everything in a request comes from the sandbox and is untrusted: size,
/// nesting depth, ids, capability name, version, TTL bounds, digest shape and <c>ask</c> limits are all checked here,
/// and anything out of shape rejects the whole request before it can reach any decision. The id of a request is the
/// file name without <c>.json</c> (the only trusted source: the response is written next to it); a body claiming
/// another id is rejected.
/// </summary>
public static partial class RelayEnvelope
{
    public const string JsonSuffix = ".json";
    public const long MaxRequestBytes = 256L * 1024L;
    public const int MaxResponseChars = 512_000;
    public const int MaxControlChars = 64_000;
    public const int MaxJsonChars = 200_000;
    public const int MaxDepth = 24;
    public const long MinTtlMs = 1_000;
    public const long MaxTtlMs = 120_000;
    public const long DefaultTtlMs = RelayProtocol.DefaultTimeoutMs; // already within [MinTtlMs, MaxTtlMs]
    public const int MaxAskQuestionChars = 200;
    public const int MaxAskOptionChars = 60;
    public const int MinAskOptions = 2;
    public const int MaxAskOptions = 3;
    public const long MinAskTimeoutMs = 5_000;
    public const long MaxAskTimeoutMs = 120_000;
    public const long DefaultAskTimeoutMs = 60_000;

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{1,64}\z")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{1,64}\.json\z")]
    private static partial Regex FilePattern();

    [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
    private static partial Regex DigestPattern();

    public static bool IsValidId(string value) => IdPattern().IsMatch(value);

    /// <summary>Allowlist of request file names (skips in-flight parts and anything else the sandbox creates).</summary>
    public static bool IsRequestFileName(string name) => FilePattern().IsMatch(name);

    private static string ExpectedId(string fileName) =>
        fileName.EndsWith(JsonSuffix, StringComparison.Ordinal) ? fileName[..^JsonSuffix.Length] : fileName;

    private static long Ttl(JsonElement json, string name, long min, long max, long fallback)
    {
        var v = OrgJson.OptLong(json, name);
        return v > 0 ? Math.Clamp(v, min, max) : fallback;
    }

    /// <summary>Upstream <c>decodeRequest</c> (v1 capability channel, <c>inbox/&lt;id&gt;.json</c>).</summary>
    public static RelayDecodeResult DecodeRequest(string text, string fileName)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(fileName);
        var expectedId = ExpectedId(fileName);
        RelayDecodeResult Reject(RelayError e, string cause) => new RelayDecodeResult.Rejected(expectedId, e, cause);
        if (text.Length > MaxJsonChars)
        {
            return Reject(RelayError.RequestTooLarge, "oversized");
        }
        if (NestingDepth(text) > MaxDepth)
        {
            return Reject(RelayError.TransportMalformed, "too deep");
        }
        if (OrgJson.ParseObject(text) is not { } json)
        {
            return Reject(RelayError.TransportMalformed, "not an object");
        }
        var id = OrgJson.OptString(json, "id");
        if (!IsValidId(id))
        {
            return Reject(RelayError.TransportMalformed, "bad id");
        }
        if (id != expectedId)
        {
            return Reject(RelayError.TransportMalformed, "id mismatch");
        }
        if (OrgJson.OptInt(json, "v") != RelayProtocol.V1)
        {
            return Reject(RelayError.TransportMalformed, "version");
        }
        var capability = OrgJson.OptString(json, "capability");
        if (!RelayProtocol.IsCapability(capability))
        {
            return Reject(RelayError.CapabilityNotImplemented, "unknown capability");
        }
        if (OrgJson.OptObject(json, "args") is not { } args)
        {
            return Reject(RelayError.TransportMalformed, "args missing");
        }
        return new RelayDecodeResult.Valid(new RelayRequest(
            id,
            Math.Max(OrgJson.OptLong(json, "ts"), 0L),
            capability,
            args.Clone(),
            Ttl(json, "ttlMs", MinTtlMs, MaxTtlMs, DefaultTtlMs)));
    }

    /// <summary>Upstream <c>decodeControl</c> (v2 control channel, <c>control-inbox/&lt;id&gt;.json</c>).</summary>
    public static ControlDecodeResult DecodeControl(string text, string fileName)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(fileName);
        var expectedId = ExpectedId(fileName);
        if (text.Length > MaxControlChars)
        {
            return Rejected(expectedId, RelayError.RequestTooLarge, "oversized");
        }
        if (NestingDepth(text) > MaxDepth)
        {
            return Rejected(expectedId, RelayError.TransportMalformed, "too deep");
        }
        if (OrgJson.ParseObject(text) is not { } json)
        {
            return Rejected(expectedId, RelayError.TransportMalformed, "not an object");
        }
        var id = OrgJson.OptString(json, "id");
        if (!IsValidId(id))
        {
            return Rejected(expectedId, RelayError.TransportMalformed, "bad id");
        }
        if (id != expectedId)
        {
            return Rejected(expectedId, RelayError.TransportMalformed, "id mismatch");
        }
        if (OrgJson.OptInt(json, "v") != RelayProtocol.V2)
        {
            return Rejected(expectedId, RelayError.TransportMalformed, "version");
        }
        return OrgJson.OptString(json, "op") switch
        {
            "submit" => DecodeSubmit(id, json),
            "status" => DecodeTargeted(id, json, cancel: false),
            "cancel" => DecodeTargeted(id, json, cancel: true),
            "health" => new ControlDecodeResult.Valid(new ControlRequest.Health(id)),
            "ask" => DecodeAsk(id, json),
            _ => Rejected(expectedId, RelayError.TransportMalformed, "op"),
        };
    }

    private static ControlDecodeResult.Rejected Rejected(string id, RelayError error, string cause) => new(id, error, cause);

    private static ControlDecodeResult DecodeSubmit(string id, JsonElement json)
    {
        var capability = OrgJson.OptString(json, "capability");
        if (!RelayProtocol.IsCapability(capability))
        {
            return Rejected(id, RelayError.CapabilityNotImplemented, "unknown capability");
        }
        if (OrgJson.OptObject(json, "args") is not { } args)
        {
            return Rejected(id, RelayError.TransportMalformed, "args missing");
        }
        var digest = OrgJson.OptString(json, "payloadDigest");
        if (!DigestPattern().IsMatch(digest))
        {
            return Rejected(id, RelayError.TransportMalformed, "digest");
        }
        return new ControlDecodeResult.Valid(new ControlRequest.Submit(id, capability, args.Clone(),
            Ttl(json, "hostTtlMs", MinTtlMs, MaxTtlMs, DefaultTtlMs), digest));
    }

    private static ControlDecodeResult DecodeTargeted(string id, JsonElement json, bool cancel)
    {
        var target = OrgJson.OptString(json, "targetId");
        if (!IsValidId(target))
        {
            return Rejected(id, RelayError.TransportMalformed, "target");
        }
        return new ControlDecodeResult.Valid(cancel ? new ControlRequest.Cancel(id, target) : new ControlRequest.Status(id, target));
    }

    private static ControlDecodeResult DecodeAsk(string id, JsonElement json)
    {
        var question = OrgJson.KotlinTrim(OrgJson.OptString(json, "question"));
        if (question.Length == 0 || question.Length > MaxAskQuestionChars)
        {
            return Rejected(id, RelayError.TransportMalformed, "question");
        }
        if (OrgJson.OptArray(json, "options") is not { } array)
        {
            return Rejected(id, RelayError.TransportMalformed, "options");
        }
        var count = array.GetArrayLength();
        if (count is < MinAskOptions or > MaxAskOptions)
        {
            return Rejected(id, RelayError.TransportMalformed, "options count");
        }
        var options = new List<string>(count);
        foreach (var item in array.EnumerateArray())
        {
            // Element by element: a number must not pass as a "string" option.
            if (item.ValueKind != JsonValueKind.String)
            {
                return Rejected(id, RelayError.TransportMalformed, "options shape");
            }
            options.Add(OrgJson.KotlinTrim(item.GetString()!));
        }
        if (options.Any(o => o.Length == 0 || o.Length > MaxAskOptionChars))
        {
            return Rejected(id, RelayError.TransportMalformed, "option text");
        }
        return new ControlDecodeResult.Valid(new ControlRequest.Ask(id, question, options,
            Ttl(json, "timeoutMs", MinAskTimeoutMs, MaxAskTimeoutMs, DefaultAskTimeoutMs)));
    }

    /// <summary>Linear bracket scan (string-aware); org.json itself has no depth limit.</summary>
    public static int NestingDepth(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var depth = 0;
        var max = 0;
        var inString = false;
        var escaped = false;
        foreach (var ch in text)
        {
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }
            }
            else if (ch == '"')
            {
                inString = true;
            }
            else if (ch is '{' or '[')
            {
                depth++;
                if (depth > max)
                {
                    max = depth;
                }
            }
            else if (ch is '}' or ']')
            {
                depth--;
            }
        }
        return max;
    }
}
