using DshMobile.Runtime;

namespace DshMobile.Application.Shadow;

/// <summary>
/// PR-03 first slice (Exec/PRoot): the legacy <c>runtime.diagnostics</c> results versus the same fixed guest commands
/// started by the C# <see cref="DshMobile.Runtime.Guest.ProotGuestProcess"/>. Agree = every command has the same
/// success flag and the same (trimmed) output.
/// </summary>
public static class GuestExecShadowCheck
{
    public const string Slice = "runtime.guest-exec";

    public static ShadowReport Compare(
        IReadOnlyList<(string Key, bool Ok, string Output)> legacy,
        IReadOnlyList<(string Key, GuestProcessResult Result)> csharp,
        long csharpMs,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(csharp);
        var byKey = csharp.ToDictionary(c => c.Key, c => c.Result, StringComparer.Ordinal);
        var details = new List<string>();
        var diverged = false;
        foreach (var (key, ok, output) in legacy)
        {
            if (!byKey.TryGetValue(key, out var r))
            {
                diverged = true;
                details.Add($"{key}: not run by csharp");
                continue;
            }
            var same = ok == r.Ok && string.Equals(output.Trim(), r.Output.Trim(), StringComparison.Ordinal);
            diverged |= !same;
            details.Add(same
                ? $"{key}={Short(r.Output)}"
                : $"{key}: legacy {(ok ? "ok" : "fail")} '{Short(output)}' vs csharp {(r.Ok ? "ok" : "fail")} '{Short(r.Output)}'{(r.Error is null ? "" : " (" + r.Error + ")")}");
        }
        foreach (var extra in csharp.Where(c => legacy.All(l => l.Key != c.Key)))
        {
            diverged = true;
            details.Add($"{extra.Key}: not run by legacy");
        }
        return new ShadowReport(Slice, at, diverged ? ShadowVerdict.Diverge : ShadowVerdict.Agree,
            legacy.All(l => l.Ok), null, csharp.All(c => c.Result.Ok), null, string.Join("; ", details), csharpMs);
    }

    private static string Short(string s)
    {
        var t = s.Trim().Replace('\n', ' ');
        return t.Length <= 60 ? t : t[..60] + "…";
    }
}
