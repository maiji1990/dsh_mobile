namespace DshMobile.Runtime.Guest;

/// <summary>
/// Builds the single <c>sh -c</c> line that PRoot runs in the guest from argv. Every token is POSIX single-quoted,
/// so shell metacharacters in a validated token (e.g. <c>&gt;=</c> in an npm range) stay literal. Identical rule to
/// the legacy Kotlin <c>RuntimeController.shellQuote</c>; tests/TestVectors/guest-command-line.json pins both.
/// New C# code passes argv to the native exec layer directly where possible; this is for the guest shell hop only.
/// </summary>
public static class GuestCommandLine
{
    public static string Quote(string arg)
    {
        ArgumentNullException.ThrowIfNull(arg);
        if (arg.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("argv token contains NUL", nameof(arg));
        }
        return "'" + arg.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    public static string Join(IEnumerable<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        return string.Join(' ', argv.Select(Quote));
    }
}
