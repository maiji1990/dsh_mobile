using System.Globalization;
using System.Text.Json;

namespace DshMobile.Runtime.Automation.Relay;

/// <summary>
/// The read semantics of Android's org.json (libcore <c>JSONObject.opt*</c> / <c>JSON.toInteger</c> / <c>toLong</c> /
/// <c>toString</c>, and number literals as <c>JSONTokener.readLiteral</c> types them) over System.Text.Json values,
/// because the Relay envelope decoder is defined by them on the device: <c>"v":"1"</c> is version 1, <c>"id":123</c>
/// is the id <c>"123"</c>, <c>"id":null</c> is the string <c>"null"</c>, a long <c>v</c> wraps to 32 bits.
/// Deliberate difference: only strict JSON (RFC 8259) parses — the guest CLI only writes <c>JSON.stringify</c> output,
/// and org.json's lenient extras (single quotes, unquoted names, comments, trailing text) are rejected here.
/// </summary>
internal static class OrgJson
{
    private static readonly JsonDocumentOptions Strict = new() { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64 };

    /// <summary><c>new JSONObject(text)</c>: the root object, or null when the text is not a JSON object.</summary>
    public static JsonElement? ParseObject(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text, Strict);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null; // invalid UTF-16 in the text
        }
    }

    /// <summary>Duplicate names: the last one wins, like libcore's <c>JSONObject</c> (System.Text.Json matches the last).</summary>
    private static bool TryGet(JsonElement obj, string name, out JsonElement value) => obj.TryGetProperty(name, out value);

    /// <summary><c>optString(name)</c>: "" when absent, otherwise <c>String.valueOf</c> of the value (null → "null").</summary>
    public static string OptString(JsonElement obj, string name) =>
        TryGet(obj, name, out var v) ? JavaString(v) : "";

    public static string JavaString(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString()!,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        JsonValueKind.Number => NumberText(v.GetRawText()),
        _ => LibcoreJson.Serialize(v), // object / array: libcore toString (compact, "/" escaped)
    };

    /// <summary>A number literal as libcore types it (Integer / Long when integral, else Double), then <c>toString</c>.</summary>
    private static string NumberText(string raw)
    {
        if (!raw.Contains('.', StringComparison.Ordinal) && long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
        {
            return l.ToString(CultureInfo.InvariantCulture);
        }
        return JavaDouble(double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture));
    }

    /// <summary><c>optInt(name)</c>: 0 when absent or not convertible (booleans, objects, unparsable strings).</summary>
    public static int OptInt(JsonElement obj, string name)
    {
        if (!TryGet(obj, name, out var v))
        {
            return 0;
        }
        switch (v.ValueKind)
        {
            case JsonValueKind.Number:
                var raw = v.GetRawText();
                if (!raw.Contains('.', StringComparison.Ordinal) && long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
                {
                    return unchecked((int)l); // Integer as is; Long.intValue() keeps the low 32 bits
                }
                return JavaDoubleToInt(double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture));
            case JsonValueKind.String:
                return JavaParseDouble(v.GetString()!) is { } d ? JavaDoubleToInt(d) : 0;
            default:
                return 0;
        }
    }

    /// <summary><c>optLong(name)</c>: 0 when absent or not convertible.</summary>
    public static long OptLong(JsonElement obj, string name)
    {
        if (!TryGet(obj, name, out var v))
        {
            return 0;
        }
        switch (v.ValueKind)
        {
            case JsonValueKind.Number:
                var raw = v.GetRawText();
                if (!raw.Contains('.', StringComparison.Ordinal) && long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
                {
                    return l;
                }
                return JavaDoubleToLong(double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture));
            case JsonValueKind.String:
                return JavaParseDouble(v.GetString()!) is { } d ? JavaDoubleToLong(d) : 0;
            default:
                return 0;
        }
    }

    /// <summary><c>optJSONObject(name)</c>.</summary>
    public static JsonElement? OptObject(JsonElement obj, string name) =>
        TryGet(obj, name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    /// <summary><c>optJSONArray(name)</c>.</summary>
    public static JsonElement? OptArray(JsonElement obj, string name) =>
        TryGet(obj, name, out var v) && v.ValueKind == JsonValueKind.Array ? v : null;

    /// <summary>Java's <c>(int) double</c>: NaN → 0, saturating at the int range, truncation toward zero.</summary>
    public static int JavaDoubleToInt(double d) =>
        double.IsNaN(d) ? 0 : d >= int.MaxValue ? int.MaxValue : d <= int.MinValue ? int.MinValue : (int)d;

    /// <summary>Java's <c>(long) double</c>.</summary>
    public static long JavaDoubleToLong(double d) =>
        double.IsNaN(d) ? 0 : d >= long.MaxValue ? long.MaxValue : d <= long.MinValue ? long.MinValue : (long)d;

    /// <summary>
    /// <c>Double.parseDouble</c> for the forms that matter here: surrounding chars ≤ U+0020 ignored, optional
    /// <c>d/D/f/F</c> suffix, signed <c>Infinity</c> / <c>NaN</c>. Hexadecimal floats are not supported (null).
    /// </summary>
    public static double? JavaParseDouble(string s)
    {
        var t = s.Trim(Enumerable.Range(0, 0x21).Select(c => (char)c).ToArray());
        if (t.Length == 0)
        {
            return null;
        }
        var body = t.TrimStart('+', '-');
        var negative = t[0] == '-';
        if (t.Length - body.Length > 1)
        {
            return null; // more than one sign
        }
        if (body == "Infinity")
        {
            return negative ? double.NegativeInfinity : double.PositiveInfinity;
        }
        if (body == "NaN")
        {
            return double.NaN;
        }
        if (t[^1] is 'd' or 'D' or 'f' or 'F')
        {
            t = t[..^1];
        }
        if (t.Length == 0 || t.Contains("0x", StringComparison.OrdinalIgnoreCase) || t.Any(char.IsWhiteSpace))
        {
            return null;
        }
        return double.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary><c>Double.toString</c>: plain decimal in [1e-3, 1e7), otherwise <c>d.dddE±n</c>; always shortest digits.</summary>
    public static string JavaDouble(double d)
    {
        if (double.IsNaN(d))
        {
            return "NaN";
        }
        if (double.IsInfinity(d))
        {
            return d > 0 ? "Infinity" : "-Infinity";
        }
        if (d == 0)
        {
            return double.IsNegative(d) ? "-0.0" : "0.0";
        }
        var abs = Math.Abs(d);
        var sign = d < 0 ? "-" : "";
        // Shortest round-trip digits and the decimal exponent of the first one.
        var (digits, exp) = Decompose(abs.ToString("R", CultureInfo.InvariantCulture));
        if (abs >= 1e-3 && abs < 1e7)
        {
            // exp = position of the decimal point relative to the first digit
            string intPart, frac;
            if (exp >= 0)
            {
                var pointAt = exp + 1;
                intPart = pointAt >= digits.Length ? digits + new string('0', pointAt - digits.Length) : digits[..pointAt];
                frac = pointAt >= digits.Length ? "" : digits[pointAt..];
            }
            else
            {
                intPart = "0";
                frac = new string('0', -exp - 1) + digits;
            }
            return sign + intPart + "." + (frac.Length == 0 ? "0" : frac);
        }
        var mantissa = digits.Length == 1 ? digits + ".0" : digits[..1] + "." + digits[1..];
        return sign + mantissa + "E" + exp.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Significant digits (no leading/trailing zeros) and the decimal exponent of the first digit.</summary>
    private static (string Digits, int Exp) Decompose(string roundTrip)
    {
        var s = roundTrip;
        var exp = 0;
        var e = s.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            exp = int.Parse(s[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            s = s[..e];
        }
        var point = s.IndexOf('.', StringComparison.Ordinal);
        var intLen = point < 0 ? s.Length : point;
        var all = s.Replace(".", "", StringComparison.Ordinal);
        var lead = 0;
        while (lead < all.Length - 1 && all[lead] == '0')
        {
            lead++;
        }
        var digits = all[lead..].TrimEnd('0');
        if (digits.Length == 0)
        {
            digits = "0";
        }
        return (digits, exp + intLen - 1 - lead);
    }

    /// <summary>
    /// Kotlin <c>String.trim()</c>: strips chars where <c>Char.isWhitespace</c> (Java <c>Character.isWhitespace</c> or
    /// <c>isSpaceChar</c>): Unicode space / line / paragraph separators plus TAB, LF, VT, FF, CR and U+001C..U+001F.
    /// Not .NET's <c>Trim()</c>, which also strips U+0085 and keeps U+001C..U+001F.
    /// </summary>
    public static string KotlinTrim(string s)
    {
        var start = 0;
        var end = s.Length;
        while (start < end && KotlinIsWhitespace(s[start]))
        {
            start++;
        }
        while (end > start && KotlinIsWhitespace(s[end - 1]))
        {
            end--;
        }
        return s[start..end];
    }

    public static bool KotlinIsWhitespace(char c) =>
        c is '\t' or '\n' or '\u000B' or '\f' or '\r' or '\u001C' or '\u001D' or '\u001E' or '\u001F'
        || char.GetUnicodeCategory(c) is UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
}
