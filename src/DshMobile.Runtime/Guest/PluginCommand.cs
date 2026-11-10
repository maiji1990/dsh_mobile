using System.Text.RegularExpressions;

namespace DshMobile.Runtime.Guest;

/// <summary>
/// Validated argv for <c>dsh plugin list|add|remove</c>. C# port of the legacy Kotlin
/// <c>RuntimeController.pluginCommand</c> (PR-00 fix); both run tests/TestVectors/plugin-commands.json.
/// </summary>
public static partial class PluginCommand
{
    /// <summary>Profiles whose plugins may be managed (shipped templates; <c>desktop</c> is reserved by DSH).</summary>
    public static IReadOnlySet<string> Profiles { get; } = new HashSet<string>(StringComparer.Ordinal) { "web", "headless", "sdk", "sdk-minimal", "acp" };

    /// <summary>npm registry spec: optional @scope, name, optional version/range/tag; no paths, URLs, git, whitespace.</summary>
    [GeneratedRegex(@"^(@[a-z0-9][a-z0-9._-]{0,213}/)?[a-z0-9][a-z0-9._-]{0,213}(@[A-Za-z0-9._^~<>=*-]{1,64})?$", RegexOptions.CultureInvariant)]
    public static partial Regex PackageSpec();

    [GeneratedRegex(@"^(@[a-z0-9][a-z0-9._-]{0,213}/)?[a-z0-9][a-z0-9._-]{0,213}$", RegexOptions.CultureInvariant)]
    public static partial Regex PackageName();

    /// <summary>Returns argv, or a stable <c>BAD_REQUEST</c> failure message.</summary>
    public static bool TryBuild(string verb, string profile, string? operand, out IReadOnlyList<string> argv, out string? error)
    {
        argv = [];
        error = null;
        if (!Profiles.Contains(profile))
        {
            error = $"profile must be one of [{string.Join(", ", Profiles)}]";
            return false;
        }
        switch (verb)
        {
            case "list":
                argv = ["plugin", "--profile", profile, "list", "--json", "--depth", "0"];
                return true;
            case "add" when operand is not null && IsFullMatch(PackageSpec(), operand):
                argv = ["plugin", "--profile", profile, "add", operand];
                return true;
            case "add":
                error = "spec must be an npm registry package spec";
                return false;
            case "remove" when operand is not null && IsFullMatch(PackageName(), operand):
                argv = ["plugin", "--profile", profile, "remove", operand];
                return true;
            case "remove":
                error = "name must be an npm package name";
                return false;
            default:
                error = $"unknown plugin verb: {verb}";
                return false;
        }
    }

    /// <summary>
    /// Kotlin <c>Regex.matches</c> is a whole-input match; .NET <c>$</c> also matches before a trailing newline, so a
    /// spec like <c>"pkg\n"</c> must be rejected explicitly.
    /// </summary>
    private static bool IsFullMatch(Regex regex, string input)
    {
        var m = regex.Match(input);
        return m.Success && m.Index == 0 && m.Length == input.Length;
    }
}
