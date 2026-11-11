namespace DshMobile.Application.Shadow;

/// <summary>
/// PR-03 launcher shadow (read-only): the argv the C# <see cref="DshMobile.Runtime.Guest.ProotCommandBuilder"/> would
/// use for the sandbox keepalive and the DSH web PRoot, versus the exact argv of the processes the legacy runtime
/// actually started (<c>/proc/&lt;pid&gt;/cmdline</c>). The DSH side matches either legacy call site (first start or
/// the health loop's in-place restart, which binds less).
/// </summary>
public static class LauncherArgvShadowCheck
{
    public const string Slice = "runtime.launcher-argv";

    public static ShadowReport Compare(
        IReadOnlyList<string>? observedSandbox,
        IReadOnlyList<string>? observedDsh,
        IReadOnlyList<string> expectedSandbox,
        IReadOnlyList<string> expectedDshFirstStart,
        IReadOnlyList<string> expectedDshRestart,
        long csharpMs,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(expectedSandbox);
        ArgumentNullException.ThrowIfNull(expectedDshFirstStart);
        ArgumentNullException.ThrowIfNull(expectedDshRestart);
        if (observedSandbox is null && observedDsh is null)
        {
            return new ShadowReport(Slice, at, ShadowVerdict.Inconclusive, false, null, false, null, "no legacy PRoot process running", csharpMs);
        }
        var details = new List<string>();
        var diverged = false;
        if (observedSandbox is not null)
        {
            var same = observedSandbox.SequenceEqual(expectedSandbox, StringComparer.Ordinal);
            diverged |= !same;
            details.Add(same ? $"sandbox: identical ({observedSandbox.Count} args)" : "sandbox: " + FirstDifference(observedSandbox, expectedSandbox));
        }
        if (observedDsh is not null)
        {
            if (observedDsh.SequenceEqual(expectedDshFirstStart, StringComparer.Ordinal))
            {
                details.Add($"dsh: identical to first start ({observedDsh.Count} args)");
            }
            else if (observedDsh.SequenceEqual(expectedDshRestart, StringComparer.Ordinal))
            {
                details.Add($"dsh: identical to in-place restart ({observedDsh.Count} args, no guest-cache bind)");
            }
            else
            {
                diverged = true;
                details.Add("dsh: " + FirstDifference(observedDsh, expectedDshFirstStart));
            }
        }
        return new ShadowReport(Slice, at, diverged ? ShadowVerdict.Diverge : ShadowVerdict.Agree, true, null, !diverged, null, string.Join("; ", details), csharpMs);
    }

    private static string FirstDifference(IReadOnlyList<string> legacy, IReadOnlyList<string> csharp)
    {
        var n = Math.Min(legacy.Count, csharp.Count);
        for (var i = 0; i < n; i++)
        {
            if (!string.Equals(legacy[i], csharp[i], StringComparison.Ordinal))
            {
                return $"arg {i}: legacy '{legacy[i]}' vs csharp '{csharp[i]}'";
            }
        }
        return $"length: legacy {legacy.Count} vs csharp {csharp.Count}" + (legacy.Count > n ? $" (legacy extra '{legacy[n]}')" : csharp.Count > n ? $" (csharp extra '{csharp[n]}')" : "");
    }
}
