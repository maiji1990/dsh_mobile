// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/protocol/CapabilityArgsSpec.kt (key sets, required keys, types, validate)
//   src/main/java/interlock/relay/core/protocol/ArgErrors.kt          (unknown / notNumber messages)
// Registered in docs/UPSTREAMS.md. Locked by tests/TestVectors/relay-admission.json. Iteration orders matter (the
// first problem found is the one reported) and follow upstream: argument keys in document order (libcore
// LinkedHashMap), required / type tables in declaration order (Kotlin setOf / mapOf keep insertion order).

using System.Globalization;
using System.Text.Json;

namespace DshMobile.Runtime.Automation.Relay;

/// <summary>
/// Common pre-check of capability arguments, before any approval: top-level key set, required keys, value types,
/// whole-number keys. No device reads, no side effects. Checks that need runtime objects stay with the backends.
/// </summary>
public static class CapabilityArgsSpec
{
    private enum ArgType
    {
        Number,
        String,
        Boolean,
        Object,
        Array,
    }

    private sealed record Spec(string[] Allowed, string[] Required, (string Key, ArgType Type)[] Types);

    private static Spec S(string[] allowed, string[]? required = null, (string, ArgType)[]? types = null) =>
        new(allowed, required ?? [], types ?? []);

    private static readonly string[] TargetKeys = ["selector", "nodeId", "relative"];

    private static readonly (string, ArgType)[] TargetTypes =
        [("selector", ArgType.Object), ("nodeId", ArgType.Number), ("relative", ArgType.Object)];

    private static readonly HashSet<string> WholeNumberKeys = new(StringComparer.Ordinal) { "nodeId" };

    private static readonly Dictionary<string, Spec> Specs = new(StringComparer.Ordinal)
    {
        ["ui.snapshot"] = S([]),
        ["screen.capture"] = S([]),
        ["screen.observe"] = S([]),
        ["ui.tap"] = S(["x", "y"], ["x", "y"], [("x", ArgType.Number), ("y", ArgType.Number)]),
        ["ui.swipe"] = S(["fromX", "fromY", "toX", "toY", "durationMs"], ["fromX", "fromY", "toX", "toY"],
            [("fromX", ArgType.Number), ("fromY", ArgType.Number), ("toX", ArgType.Number), ("toY", ArgType.Number)]),
        ["ui.text"] = S(["text"], ["text"]),
        ["ui.key"] = S(["key"], ["key"]),
        ["ui.click"] = S(TargetKeys, types: TargetTypes),
        ["ui.longClick"] = S(TargetKeys, types: TargetTypes),
        ["ui.select"] = S(TargetKeys, types: TargetTypes),
        ["ui.dismiss"] = S(TargetKeys, types: TargetTypes),
        ["ui.node"] = S(TargetKeys, types: TargetTypes),
        ["ui.imeAction"] = S(TargetKeys, types: TargetTypes),
        ["ui.scroll"] = S([.. TargetKeys, "direction", "times", "until"], ["direction"],
            [.. TargetTypes, ("times", ArgType.Number), ("until", ArgType.Number)]),
        ["ui.setValue"] = S([.. TargetKeys, "text"], ["text"], TargetTypes),
        ["ui.setProgress"] = S([.. TargetKeys, "percent", "value"], types: [.. TargetTypes, ("percent", ArgType.Number), ("value", ArgType.Number)]),
        ["ui.waitFor"] = S([.. TargetKeys, "text", "checked", "absent", "timeoutMs"], types: [.. TargetTypes, ("timeoutMs", ArgType.Number)]),
        ["app.launch"] = S(["package"], ["package"]),
        ["app.stop"] = S(["package"], ["package"]),
        ["surface.virtual"] = S(["action", "width", "height", "dpi"]),
        ["sys.intent"] = S(["template", "hour", "minute", "length", "message", "page", "package", "number", "url"], ["template"]),
        ["sys.shell"] = S(["verb", "args"], ["verb"], [("verb", ArgType.String), ("args", ArgType.Array)]),
        ["app.install"] = S(["upload"], ["upload"]),
        ["sys.settings.write"] = S(["key", "value", "namespace"], ["key", "value"]),
        ["appops.set"] = S(["package", "op", "mode"], ["package", "op", "mode"]),
        ["pkg.query"] = S(["keyword"]),
        ["clip.read"] = S([]),
        ["clip.write"] = S(["text"], ["text"]),
        ["contact.read"] = S(["limit"]),
        ["contact.write"] = S(["name", "number"], ["name"]),
        ["cal.read"] = S(["limit"]),
        ["cal.write"] = S(["title", "startMs", "endMs"], ["title"]),
        ["loc.read"] = S([]),
        ["media.read"] = S(["kind", "limit"]),
        ["media.write"] = S(["file", "kind"], ["file"]),
        ["notify.read"] = S(["limit", "package", "includeOngoing"]),
        ["notify.post"] = S(["title", "text", "id"], ["title"]),
        ["audio.capture"] = S(["seconds"]),
        ["screen.record"] = S(["seconds"]),
    };

    /// <summary>Upstream <c>validate</c>: the first problem as the reason text, or null when the arguments pass.</summary>
    public static string? Validate(string capability, JsonElement args)
    {
        ArgumentNullException.ThrowIfNull(capability);
        if (!Specs.TryGetValue(capability, out var spec))
        {
            return null;
        }
        foreach (var p in args.EnumerateObject())
        {
            if (!spec.Allowed.Contains(p.Name, StringComparer.Ordinal))
            {
                return ArgErrors.Unknown(p.Name, spec.Allowed);
            }
        }
        foreach (var key in spec.Required)
        {
            if (IsKotlinBlank(OrgJson.OptString(args, key)))
            {
                return $"missing arg: {key}";
            }
        }
        foreach (var (key, type) in spec.Types)
        {
            if (!args.TryGetProperty(key, out var value))
            {
                continue;
            }
            if (!Matches(type, value))
            {
                return TypeMismatch(key, type, value);
            }
        }
        // Whole-number check after the type check: a string is "must be a number", a non-integral number lands here.
        foreach (var key in spec.Allowed)
        {
            if (!WholeNumberKeys.Contains(key) || !args.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Number)
            {
                continue;
            }
            var d = double.Parse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture); // never throws on 1e400 (GetDouble would)
            if (!double.IsFinite(d) || d % 1.0 != 0.0)
            {
                return $"{key} must be a whole number, got {LibcoreJson.ValueOf(value)} ({LibcoreJson.JavaTypeName(value)})";
            }
        }
        return null;
    }

    private static bool IsKotlinBlank(string s) => s.All(OrgJson.KotlinIsWhitespace);

    private static bool Matches(ArgType type, JsonElement v) => type switch
    {
        ArgType.Number => v.ValueKind == JsonValueKind.Number,
        ArgType.String => v.ValueKind == JsonValueKind.String,
        ArgType.Boolean => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
        ArgType.Object => v.ValueKind == JsonValueKind.Object,
        ArgType.Array => v.ValueKind == JsonValueKind.Array,
        _ => false,
    };

    private static string TypeMismatch(string key, ArgType type, JsonElement value)
    {
        var wanted = type switch
        {
            ArgType.String => "a string",
            ArgType.Boolean => "true or false",
            ArgType.Object => "an object",
            ArgType.Array => "an array",
            _ => null,
        };
        return wanted is null ? ArgErrors.NotNumber(key, value) : $"{key} must be {wanted}, got {ArgErrors.Got(value)}";
    }
}

/// <summary>Upstream <c>ArgErrors</c>.</summary>
public static class ArgErrors
{
    private static readonly HashSet<string> DisplayTargetingKeys = new(StringComparer.Ordinal) { "display", "displayId", "surface", "display_id" };

    public static string Unknown(string key, IEnumerable<string>? allowed = null)
    {
        if (DisplayTargetingKeys.Contains(key))
        {
            return $"unknown arg: {key} — which display a call runs on is decided by the user's surface " +
                "preference, not by the caller. Read capabilities.json " +
                "backend.shizuku.trustedDisplay {alive,displayId} to see whether a virtual display " +
                "exists, and each response's surface/degraded fields to see where it actually ran.";
        }
        var accepted = allowed?.Where(k => !DisplayTargetingKeys.Contains(k)).Order(StringComparer.Ordinal).ToList();
        return accepted is null || accepted.Count == 0
            ? $"unknown arg: {key}"
            : $"unknown arg: {key} — this call takes: {string.Join(", ", accepted)}";
    }

    public static string NotNumber(string key, JsonElement raw) => $"{key} must be a number, got {Got(raw)}";

    /// <summary>"nothing" for null, a quoted 40-char prefix for strings, else <c>"$value (SimpleName)"</c>.</summary>
    internal static string Got(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => "nothing",
        JsonValueKind.String => $"\"{Take(value.GetString()!, 40)}\" (string)",
        _ => $"{LibcoreJson.ValueOf(value)} ({LibcoreJson.JavaTypeName(value)})",
    };

    private static string Take(string s, int n) => s.Length <= n ? s : s[..n];
}
