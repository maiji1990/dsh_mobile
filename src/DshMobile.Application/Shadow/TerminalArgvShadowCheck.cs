namespace DshMobile.Application.Shadow;

/// <summary>
/// PR-06 shadow (read-only): the guest login-shell argv the C# <see cref="DshMobile.Runtime.Guest.TerminalCommandBuilder"/>
/// would start, versus the exact <c>/proc/&lt;pid&gt;/cmdline</c> of each terminal session the legacy TerminalView
/// started. Inconclusive when no legacy terminal session is open.
/// </summary>
public static class TerminalArgvShadowCheck
{
    public const string Slice = "terminal.launcher-argv";

    /// <summary>A PRoot launched as the guest login shell: argv ends with <c>/usr/bin/bash --noprofile --login</c>.</summary>
    public static bool IsTerminalShell(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        return argv.Count >= 4
            && (argv[0].EndsWith("/libproot.so", StringComparison.Ordinal) || argv[0].EndsWith("/proot", StringComparison.Ordinal))
            && argv[^3] == "/usr/bin/bash" && argv[^2] == "--noprofile" && argv[^1] == "--login";
    }

    public static ShadowReport Compare(IReadOnlyList<IReadOnlyList<string>> legacySessions, IReadOnlyList<string>? expected, long csharpMs, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(legacySessions);
        if (legacySessions.Count == 0)
        {
            return new ShadowReport(Slice, at, ShadowVerdict.Inconclusive, false, null, expected is not null, null, "no legacy terminal session open", csharpMs);
        }
        if (expected is null)
        {
            return new ShadowReport(Slice, at, ShadowVerdict.Diverge, true, null, false, null, "csharp: no runtime rootfs (legacy has a running terminal)", csharpMs);
        }
        var diffs = legacySessions
            .Select((s, i) => s.SequenceEqual(expected, StringComparer.Ordinal) ? null : $"session {i + 1}: " + LauncherArgvShadowCheck.FirstDifferenceOf(s, expected))
            .OfType<string>()
            .ToList();
        return diffs.Count == 0
            ? new ShadowReport(Slice, at, ShadowVerdict.Agree, true, null, true, null, $"{legacySessions.Count} session(s) identical ({expected.Count} args)", csharpMs)
            : new ShadowReport(Slice, at, ShadowVerdict.Diverge, true, null, false, null, string.Join("; ", diffs), csharpMs);
    }
}
