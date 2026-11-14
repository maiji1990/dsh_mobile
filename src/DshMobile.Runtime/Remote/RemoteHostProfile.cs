// Ported from this repo's native/native-bridge/src/main/java/dshmobile/nativebridge/RemoteHosts.kt (RemoteHostProfile,
// RemoteHosts.list / requestedId; transition code for ADR-0006 — DSHBox has no remote-host feature). Registered in
// docs/UPSTREAMS.md. Behaviour locked by tests/TestVectors/remote-ssh.json (legacy Kotlin LegacyRemoteSshParityTest
// runs the same cases); request fields are read with Android libcore org.json semantics (the device's).

using System.Text.Json;
using System.Text.RegularExpressions;
using DshMobile.Runtime.Automation.Relay;

namespace DshMobile.Runtime.Remote;

/// <summary>A host profile request the legacy bridge answers with <c>BAD_REQUEST</c> and this message.</summary>
public sealed class RemoteProfileException(string message) : Exception(message);

/// <summary>One remote DSH host as the user configured it. Contains no secret (ADR-0006).</summary>
public sealed record RemoteHostProfile(
    string Id,
    string Label,
    string Host,
    int Port,
    string User,
    int DshPort,
    string? PinnedFingerprint)
{
    /// <summary>SharedPreferences file and key the legacy <c>RemoteHosts</c> stores the profile list under.</summary>
    public const string PreferencesName = "dshmobile_remote_hosts";
    public const string PreferencesKey = "hosts";

    public const int DefaultPort = 22;
    public const int DefaultDshPort = 3091;

    // Legacy regexes, anchored for the whole input (Kotlin Regex.matches) and ASCII-only, as in Java.
    private static readonly Regex HostPattern = new(
        @"\A(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,62})(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,62}))*|[0-9a-fA-F:.]{2,45})\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex UserPattern = new(@"\A[A-Za-z0-9_][A-Za-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant);
    private static readonly Regex FingerprintPattern = new(@"\ASHA256:[A-Za-z0-9+/]{43}\z", RegexOptions.CultureInvariant);

    public static bool IsFingerprint(string? s) => s is not null && FingerprintPattern.IsMatch(s);

    /// <summary>
    /// Validates a <c>remote.hosts.save</c> request the way the legacy <c>fromRequest</c> does (same check order and
    /// messages). Changing host or port drops the pin: a new address must be confirmed again.
    /// <paramref name="newId"/> supplies the id of a new host (legacy: a random UUID).
    /// </summary>
    public static RemoteHostProfile FromRequest(JsonElement request, RemoteHostProfile? existing, Func<string> newId)
    {
        ArgumentNullException.ThrowIfNull(newId);
        if (request.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("request must be a JSON object", nameof(request));
        }
        var host = KotlinText.Trim(OrgJson.OptString(request, "host"));
        var user = KotlinText.Trim(OrgJson.OptString(request, "user"));
        var port = OrgJson.OptInt(request, "port", DefaultPort);
        var dshPort = OrgJson.OptInt(request, "dshPort", DefaultDshPort);
        var label = KotlinText.Trim(OrgJson.OptString(request, "label"));
        if (label.Length == 0)
        {
            label = $"{user}@{host}";
        }
        if (!HostPattern.IsMatch(host))
        {
            throw new RemoteProfileException("host must be a host name or IP address");
        }
        if (!UserPattern.IsMatch(user))
        {
            throw new RemoteProfileException("user may contain letters, digits, . _ - (max 64)");
        }
        if (port is < 1 or > 65535)
        {
            throw new RemoteProfileException("port must be 1..65535");
        }
        if (dshPort is < 1024 or > 65535)
        {
            throw new RemoteProfileException("dshPort must be 1024..65535");
        }
        if (label.Length > 64 || label.Any(KotlinText.IsIsoControl))
        {
            throw new RemoteProfileException("label must be at most 64 printable characters");
        }
        var samePeer = existing is not null && existing.Host == host && existing.Port == port;
        return new RemoteHostProfile(
            existing?.Id ?? newId(), label, host, port, user, dshPort,
            samePeer ? existing!.PinnedFingerprint : null);
    }

    /// <summary>
    /// The id of a save request, or null for a new host. JSON <c>null</c>, a missing id and a blank one all mean
    /// "new" (legacy <c>requestedId</c>; libcore's <c>optString</c> would otherwise turn null into "null").
    /// </summary>
    public static string? RequestedId(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty("id", out var id) || id.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        var s = OrgJson.JavaString(id);
        return KotlinText.IsBlank(s) ? null : s;
    }

    /// <summary>
    /// The stored profile list (SharedPreferences <see cref="PreferencesKey"/>; absent = <c>"[]"</c>). As in the
    /// legacy <c>list()</c>, one unreadable entry makes the whole list empty.
    /// </summary>
    public static IReadOnlyList<RemoteHostProfile> ParseStored(string? stored)
    {
        try
        {
            using var doc = JsonDocument.Parse(stored ?? "[]");
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            var all = new List<RemoteHostProfile>();
            foreach (var o in doc.RootElement.EnumerateArray())
            {
                all.Add(FromStored(o));
            }
            return all;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>
    /// Legacy <c>fromJson</c>: libcore <c>getString</c> / <c>getInt</c> (absent or unconvertible → error), and a
    /// stored pin that is not a well-formed fingerprint is dropped.
    /// </summary>
    private static RemoteHostProfile FromStored(JsonElement o)
    {
        if (o.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("profile entry is not an object");
        }
        var pin = OrgJson.OptString(o, "pinnedFingerprint");
        return new RemoteHostProfile(
            GetString(o, "id"), GetString(o, "label"), GetString(o, "host"), GetInt(o, "port"),
            GetString(o, "user"), GetInt(o, "dshPort"), IsFingerprint(pin) ? pin : null);
    }

    /// <summary>
    /// libcore <c>getString</c>: only a missing name fails; any value is text, JSON null included ("null": libcore
    /// stores it as <c>JSONObject.NULL</c>, which <c>get</c> returns).
    /// </summary>
    private static string GetString(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) ? OrgJson.JavaString(v) : throw new KeyNotFoundException(name);

    /// <summary>libcore <c>getInt</c>: a number (narrowed like <c>optInt</c>) or a numeric string; anything else fails.</summary>
    private static int GetInt(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v))
        {
            throw new KeyNotFoundException(name);
        }
        return v.ValueKind switch
        {
            JsonValueKind.Number => OrgJson.OptInt(o, name, 0), // every number converts (Number.intValue)
            JsonValueKind.String when OrgJson.JavaParseDouble(v.GetString()!) is { } d => OrgJson.JavaDoubleToInt(d),
            _ => throw new FormatException(name),
        };
    }
}

/// <summary>Kotlin / Java character rules the legacy profile code relies on.</summary>
internal static class KotlinText
{
    /// <summary>
    /// Kotlin <c>Char.isWhitespace()</c>: Java <c>Character.isWhitespace</c> || <c>isSpaceChar</c> — the Unicode
    /// space separators (no-break spaces included), line / paragraph separators, U+0009–U+000D and U+001C–U+001F.
    /// Unlike .NET <see cref="char.IsWhiteSpace(char)"/> it excludes U+0085 and includes U+001C–U+001F.
    /// </summary>
    public static bool IsWhitespace(char c) =>
        c is (>= '\u0009' and <= '\u000D') or (>= '\u001C' and <= '\u001F')
        || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.SpaceSeparator
            or System.Globalization.UnicodeCategory.LineSeparator
            or System.Globalization.UnicodeCategory.ParagraphSeparator;

    /// <summary>Kotlin <c>String.trim()</c>.</summary>
    public static string Trim(string s)
    {
        var start = 0;
        var end = s.Length;
        while (start < end && IsWhitespace(s[start]))
        {
            start++;
        }
        while (end > start && IsWhitespace(s[end - 1]))
        {
            end--;
        }
        return s[start..end];
    }

    /// <summary>Kotlin <c>isBlank()</c>.</summary>
    public static bool IsBlank(string s) => s.All(IsWhitespace);

    /// <summary>Java <c>Character.isISOControl</c>: U+0000–U+001F and U+007F–U+009F.</summary>
    public static bool IsIsoControl(char c) => c <= '\u001F' || c is >= '\u007F' and <= '\u009F';
}
