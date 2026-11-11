using DshMobile.Runtime.Guest;

namespace DshMobile.Application.Shadow;

/// <summary>
/// PR-05 shadow: the legacy <c>plugin.list</c> result versus the same <c>dsh plugin list --json --depth 0</c> run by the
/// C# PRoot launcher. Agree needs the same success flag, BOTH outputs to contain a parsable JSON list (so two equally
/// broken outputs are not mistaken for two empty lists), and identical parsed plugins (name + version). Raw output is
/// not compared: pnpm progress noise differs between runs.
/// </summary>
public static class PluginListShadowCheck
{
    public const string Slice = "plugins.list";

    public static ShadowReport Compare(bool legacyOk, string legacyOutput, GuestPluginResult csharp, long csharpMs, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(legacyOutput);
        ArgumentNullException.ThrowIfNull(csharp);
        var legacyJson = GuestPlugins.TryParseList(legacyOutput, out var legacy);
        var csJson = GuestPlugins.TryParseList(csharp.Output, out var cs);
        var agree = legacyOk == csharp.Ok && legacyJson && csJson && legacy.SequenceEqual(cs);
        string detail;
        if (agree)
        {
            detail = $"{cs.Count} plugin(s) identical, both JSON lists" + (cs.Count == 0 ? "" : ": " + string.Join(", ", cs.Take(6).Select(p => $"{p.Name}@{p.Version}")));
        }
        else
        {
            var onlyLegacy = legacy.Except(cs).Select(p => $"{p.Name}@{p.Version}");
            var onlyCs = cs.Except(legacy).Select(p => $"{p.Name}@{p.Version}");
            detail = $"legacy {(legacyOk ? "ok" : "fail")} json={(legacyJson ? "yes" : "no")} {legacy.Count} vs csharp {(csharp.Ok ? "ok" : "fail")} json={(csJson ? "yes" : "no")} {cs.Count}"
                + (onlyLegacy.Any() ? "; only legacy: " + string.Join(", ", onlyLegacy) : "")
                + (onlyCs.Any() ? "; only csharp: " + string.Join(", ", onlyCs) : "")
                + (csharp.Error is null ? "" : "; csharp error: " + csharp.Error);
        }
        return new ShadowReport(Slice, at, agree ? ShadowVerdict.Agree : ShadowVerdict.Diverge, legacyOk, null, csharp.Ok, null, detail, csharpMs);
    }
}
