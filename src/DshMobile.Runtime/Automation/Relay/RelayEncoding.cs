// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/transport/EnvelopeCodec.kt (encode, encodeControl)
// Text output follows Android libcore org.json (JSONStringer / JSONObject.toString), which is what Relay writes on the
// device: keys in insertion order, '/' escaped as "\/", other chars raw except C0 controls. Registered in UPSTREAMS.md.

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DshMobile.Runtime.Automation.Relay;

/// <summary>Response encoding of the Relay mailbox, byte-for-byte as Relay writes it on the device.</summary>
public static class RelayEncoding
{
    /// <summary>Upstream <c>EnvelopeCodec.encode</c> (v1 <c>outbox/&lt;id&gt;.json</c>).</summary>
    public static string Encode(RelayResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var o = new LibcoreJson.ObjectWriter();
        o.Put("v", RelayProtocol.V1);
        o.Put("id", response.Id);
        o.Put("ok", response.Ok);
        o.Put("elapsedMs", response.ElapsedMs);
        if (response.Surface is { } surface)
        {
            o.Put("surface", surface.Wire());
        }
        if (response.DegradedFrom is { } from)
        {
            o.Put("degradedFrom", from.Wire());
        }
        if (response.Surface is not null)
        {
            o.Put("degraded", response.DegradedFrom is not null);
        }
        if (response.Reason is { } reason)
        {
            o.Put(response.Ok ? "note" : "reason", reason);
        }
        if (response.DegradeReason is { } degrade)
        {
            o.Put("degradeReason", degrade);
        }
        if (response.Data is { ValueKind: JsonValueKind.Object } data && data.EnumerateObject().Any())
        {
            o.PutRaw("data", LibcoreJson.Serialize(data));
        }
        if (response.Artifacts.Count > 0)
        {
            var a = new StringBuilder("[");
            for (var i = 0; i < response.Artifacts.Count; i++)
            {
                a.Append(i == 0 ? "" : ",").Append(LibcoreJson.Quote(response.Artifacts[i]));
            }
            o.PutRaw("artifacts", a.Append(']').ToString());
        }
        if (response.Error is { } e)
        {
            var err = new LibcoreJson.ObjectWriter();
            err.Put("code", e.Code);
            err.Put("retryable", e.Retryable);
            err.Put("exitCode", e.ExitCode);
            o.PutRaw("error", err.ToString());
        }
        if (response.RetryPolicy is { } rp)
        {
            o.Put("retryPolicy", rp.Wire());
        }
        if (response.EffectState is { } es)
        {
            o.Put("effectState", es.Wire());
        }
        if (response.ConsentKind is { } ck)
        {
            o.Put("consentKind", ck);
        }
        return o.ToString();
    }

    /// <summary>
    /// Upstream <c>EnvelopeCodec.encodeControl</c>: <c>{"v":2,"id":…,"op":…,"ok":…,…payload}</c>; payload values are
    /// strings, booleans, integers or raw JSON elements, in the order given.
    /// </summary>
    public static string EncodeControl(string id, string op, bool ok, IEnumerable<KeyValuePair<string, object?>>? payload = null)
    {
        var o = new LibcoreJson.ObjectWriter();
        o.Put("v", RelayProtocol.V2);
        o.Put("id", id);
        o.Put("op", op);
        o.Put("ok", ok);
        foreach (var (key, value) in payload ?? [])
        {
            switch (value)
            {
                case string s:
                    o.Put(key, s);
                    break;
                case bool b:
                    o.Put(key, b);
                    break;
                case int i:
                    o.Put(key, i);
                    break;
                case long l:
                    o.Put(key, l);
                    break;
                case JsonElement el:
                    o.PutRaw(key, LibcoreJson.Serialize(el));
                    break;
                case null:
                    break; // JSONObject.put(key, null) removes the key
                default:
                    throw new ArgumentException($"unsupported payload value for {key}: {value.GetType().Name}", nameof(payload));
            }
        }
        return o.ToString();
    }

    /// <summary>The answer the control channel gives a rejected request: <c>op:"unknown"</c>, <c>cause</c>.</summary>
    public static string EncodeControlRejection(string id, string cause) =>
        EncodeControl(id, "unknown", false, [new("cause", cause)]);
}

/// <summary>Android libcore org.json text forms (JSONStringer, JSONObject.numberToString, String.valueOf).</summary>
internal static class LibcoreJson
{
    /// <summary>A JSONObject being built: insertion order, a repeated key keeps its first position and takes the new value.</summary>
    internal sealed class ObjectWriter
    {
        private readonly List<string> _keys = [];
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public void PutRaw(string key, string json)
        {
            if (!_values.ContainsKey(key))
            {
                _keys.Add(key);
            }
            _values[key] = json;
        }

        public void Put(string key, string value) => PutRaw(key, Quote(value));

        public void Put(string key, bool value) => PutRaw(key, value ? "true" : "false");

        public void Put(string key, long value) => PutRaw(key, value.ToString(CultureInfo.InvariantCulture));

        public override string ToString()
        {
            var sb = new StringBuilder("{");
            for (var i = 0; i < _keys.Count; i++)
            {
                sb.Append(i == 0 ? "" : ",").Append(Quote(_keys[i])).Append(':').Append(_values[_keys[i]]);
            }
            return sb.Append('}').ToString();
        }
    }

    /// <summary><c>JSONStringer.string</c>: escapes '"', '\', '/', the named controls, other C0 as \u00xx.</summary>
    public static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"' or '\\' or '/':
                    sb.Append('\\').Append(c);
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                default:
                    if (c <= '\u001F')
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>A parsed value written back the way <c>JSONObject.toString()</c> writes it (document order).</summary>
    public static string Serialize(JsonElement v)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.Object:
                var o = new ObjectWriter();
                foreach (var p in v.EnumerateObject())
                {
                    o.PutRaw(p.Name, Serialize(p.Value)); // duplicate names: last value, first position (LinkedHashMap.put)
                }
                return o.ToString();
            case JsonValueKind.Array:
                return "[" + string.Join(",", v.EnumerateArray().Select(Serialize)) + "]";
            case JsonValueKind.String:
                return Quote(v.GetString()!);
            case JsonValueKind.Number:
                return NumberToString(v.GetRawText());
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            default:
                return "null";
        }
    }

    /// <summary><c>JSONObject.numberToString</c> of the number libcore parsed from <paramref name="raw"/>.</summary>
    public static string NumberToString(string raw)
    {
        if (IsIntegralLiteral(raw) && long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
        {
            return l.ToString(CultureInfo.InvariantCulture);
        }
        var d = double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (d == 0 && double.IsNegative(d))
        {
            return "-0";
        }
        var asLong = OrgJson.JavaDoubleToLong(d);
        return d == asLong ? asLong.ToString(CultureInfo.InvariantCulture) : OrgJson.JavaDouble(d);
    }

    private static bool IsIntegralLiteral(string raw) => raw.IndexOfAny(['.', 'e', 'E']) < 0;

    /// <summary>The Java class libcore gives a parsed value (for messages that print <c>javaClass.simpleName</c>).</summary>
    public static string JavaTypeName(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Object => "JSONObject",
        JsonValueKind.Array => "JSONArray",
        JsonValueKind.String => "String",
        JsonValueKind.True or JsonValueKind.False => "Boolean",
        JsonValueKind.Number => NumberTypeName(v.GetRawText()),
        _ => "Null",
    };

    private static string NumberTypeName(string raw)
    {
        if (IsIntegralLiteral(raw) && long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
        {
            return l is >= int.MinValue and <= int.MaxValue ? "Integer" : "Long";
        }
        return "Double";
    }

    /// <summary><c>String.valueOf(value)</c> / Kotlin string templates of a parsed value.</summary>
    public static string ValueOf(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Object or JsonValueKind.Array => Serialize(v),
        _ => OrgJson.JavaString(v),
    };
}
