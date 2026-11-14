// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/exec/shizuku/ShellVerbs.kt
// Registered in docs/UPSTREAMS.md. The closed verb table of sys.shell: verbs outside the table do not exist, verbs
// inside it have their arguments shape-checked, and every token is single-quoted. Raw command lines are never taken.

using System.Text.Json;
using System.Text.RegularExpressions;

namespace DshMobile.Runtime.Automation.Relay.Exec.Shizuku;

public static partial class ShellVerbs
{
    public const string KeyVerb = "verb";
    public const string KeyArgs = "args";
    private const int MaxArgs = 6;
    private const int MaxArgChars = 256;
    private const string MetaCharacters = "\"'`$;&|<>\n\r\t \\";

    public static readonly IReadOnlySet<string> SettingsNamespaces = new HashSet<string>(StringComparer.Ordinal) { "secure", "system", "global" };

    /// <summary>A table entry: <paramref name="Shape"/> is the literal command shape shown in the UI; Check returns null when well-formed.</summary>
    public sealed record Verb(string Shape, Func<IReadOnlyList<string>, string?> Check);

    private static readonly (string Name, Verb Verb)[] ReadOnly =
    [
        ("dumpsys", new Verb("dumpsys <service>", a =>
            a.Count != 1 || !Name().IsMatch(a[0]) ? "dumpsys needs exactly one service name, e.g. [battery]" : null)),
        ("pm", new Verb("pm list <packages|features|permissions|libraries>", a =>
            a.Count != 2 || a[0] != "list" || !Name().IsMatch(a[1]) ? "pm only accepts list <what>" : null)),
        ("settings", new Verb("settings get <secure|system|global> <key>", a =>
            a.Count == 0 || a[0] != "get" || a.Count < 2 || !SettingsNamespaces.Contains(a[1]) ||
            (a.Count > 2 && !SettingsKey().IsMatch(a[2]))
                ? "settings only accepts get <namespace> <key>"
                : null)),
        ("getprop", new Verb("getprop [key]", a =>
            a.Count > 1 || (a.Count == 1 && !SettingsKey().IsMatch(a[0])) ? "getprop accepts at most one property name" : null)),
        ("appops", new Verb("appops get <package>", a =>
            a.Count != 2 || a[0] != "get" || !PackageName().IsMatch(a[1]) ? "appops only accepts get <package>" : null)),
        ("wm", new Verb("wm size|density", a =>
            a.Count != 1 || (a[0] != "size" && a[0] != "density") ? "wm only accepts size|density (query)" : null)),
        ("screencap", new Verb("screencap  (returns an artifact file)", a =>
            a.Count != 0 ? "screencap takes no args; the frame comes back as an artifact" : null)),
    ];

    private static readonly (string Name, Verb Verb)[] Gated =
    [
        ("svc", new Verb("svc wifi|bluetooth|data enable|disable", a =>
            a.Count == 0 || a[0] is not ("wifi" or "bluetooth" or "data") ||
            a.Count < 2 || a[1] is not ("enable" or "disable") || a.Count > 2
                ? "svc only accepts wifi|bluetooth|data enable|disable"
                : null)),
        ("media", new Verb("media volume --stream <0..7> --set <0..100>", a =>
            a.Count == 0 || a[0] != "volume" || !a.Contains("--stream") || !a.Contains("--set") ||
            a.Any(t => t.StartsWith("--", StringComparison.Ordinal) && t is not ("--stream" or "--set" or "--show")) ||
            a.Count(t => !t.StartsWith("--", StringComparison.Ordinal)) > 3
                ? "media only accepts volume --stream <n> --set <n>"
                : null)),
        ("input", new Verb("input tap|swipe|text|keyevent <args>", a => (a.Count == 0 ? null : a[0]) switch
        {
            "tap" => a.Count != 3 || a.Skip(1).Any(t => !Coordinate().IsMatch(t)) ? "input tap needs two coordinates" : null,
            "swipe" => a.Count != 5 || a.Skip(1).Any(t => !Coordinate().IsMatch(t)) ? "input swipe needs x1 y1 x2 y2" : null,
            "text" => a.Count != 2 || a[1].Length == 0 ? "input text needs one string" : null,
            "keyevent" => a.Count != 2 || !(Numeric().IsMatch(a[1]) || Name().IsMatch(a[1])) ? "input keyevent needs one key code" : null,
            _ => "input only accepts tap|swipe|text|keyevent",
        })),
    ];

    /// <summary>Never opened: not in the table and no switch. Listed so "why not" is a testable assertion.</summary>
    public static readonly IReadOnlySet<string> Forbidden = new HashSet<string>(StringComparer.Ordinal)
    {
        "rm", "rmdir", "dd", "mkfs", "format", "sh", "su", "pm_clear", "pm_uninstall",
        "settings_delete", "content_delete", "am", "screenrecord", "setprop",
    };

    public static IReadOnlyList<string> ReadOnlyNames => [.. ReadOnly.Select(e => e.Name)];

    public static IReadOnlyList<string> GatedNames => [.. Gated.Select(e => e.Name)];

    public static IReadOnlyList<(string Name, string Shape)> ReadOnlyShapes => [.. ReadOnly.Select(e => (e.Name, e.Verb.Shape))];

    public static IReadOnlyList<(string Name, string Shape)> GatedShapes => [.. Gated.Select(e => (e.Name, e.Verb.Shape))];

    public abstract record Plan
    {
        public sealed record Run(string Command, string Verb) : Plan;

        /// <summary>The verb is in the table but its switch is off: the answer names the switch and its shape.</summary>
        public sealed record NeedsOptIn(string Verb, string Shape) : Plan;

        public sealed record Bad(string Reason) : Plan;
    }

    /// <summary>
    /// <paramref name="optInOpen"/> asks "has the user turned on this system-changing verb", supplied by the
    /// composition root from preferences: the table only knows shapes.
    /// </summary>
    public static Plan Build(JsonElement args, Func<string, bool>? optInOpen = null)
    {
        optInOpen ??= _ => false;
        var verb = OrgJson.KotlinTrim(OrgJson.OptString(args, KeyVerb));
        if (verb.Length == 0)
        {
            return new Plan.Bad("missing arg: " + KeyVerb);
        }
        if (!Name().IsMatch(verb))
        {
            return new Plan.Bad($"verb must be a single command name, got '{verb}'");
        }
        if (Forbidden.Contains(verb))
        {
            return new Plan.Bad("verb refused by policy and not configurable: " + verb);
        }
        var readOnly = Find(ReadOnly, verb);
        var entry = readOnly ?? Find(Gated, verb);
        if (entry is null)
        {
            return new Plan.Bad(
                $"unknown verb: {verb}, read-only set is [{string.Join(", ", ReadOnlyNames)}] " +
                $"and the opt-in set is [{string.Join(", ", GatedNames)}]");
        }
        if (readOnly is null && !optInOpen(verb))
        {
            return new Plan.NeedsOptIn(verb, entry.Shape);
        }
        var rest = new List<string>();
        if (OrgJson.OptArray(args, KeyArgs) is { } array)
        {
            // JSONArray.optString(i, ""): String.valueOf of each element, so a JSON null reads "null".
            rest.AddRange(array.EnumerateArray().Select(OrgJson.JavaString));
        }
        if (rest.Count > MaxArgs)
        {
            return new Plan.Bad($"{KeyArgs} accepts at most {MaxArgs} entries, got {rest.Count}");
        }
        foreach (var token in rest)
        {
            if (token.Length > MaxArgChars)
            {
                return new Plan.Bad($"each arg must be at most {MaxArgChars} characters");
            }
            if (token.Any(c => MetaCharacters.Contains(c, StringComparison.Ordinal) || c == '*' || c == '?'))
            {
                return new Plan.Bad($"arg contains shell metacharacters or whitespace: '{token}'");
            }
            if (readOnly is not null && token.StartsWith('-'))
            {
                return new Plan.Bad($"read-only verbs accept no option flags, got '{token}'");
            }
        }
        if (entry.Check(rest) is { } reason)
        {
            return new Plan.Bad(reason);
        }
        return new Plan.Run(verb + string.Concat(rest.Select(t => " " + ShellQuote(t))), verb);
    }

    /// <summary>Paired single quotes, an inner quote broken out as <c>'\''</c>: the same wrapping every shell capability uses.</summary>
    public static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static Verb? Find((string Name, Verb Verb)[] table, string verb)
    {
        foreach (var (name, entry) in table)
        {
            if (name == verb)
            {
                return entry;
            }
        }
        return null;
    }

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,31}\\z")]
    private static partial Regex Name();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*(\\.[A-Za-z0-9_]+){1,}\\z")]
    private static partial Regex PackageName();

    [GeneratedRegex("^[A-Za-z0-9_:.]{1,128}\\z")]
    private static partial Regex SettingsKey();

    [GeneratedRegex("^[0-9]{1,10}\\z")]
    private static partial Regex Numeric();

    [GeneratedRegex("^[0-9]{1,5}\\z")]
    private static partial Regex Coordinate();
}
