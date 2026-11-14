// Ported from this repo's native/native-bridge/src/main/java/dshmobile/nativebridge/RemoteHosts.kt (companion tokenFrom,
// redact; transition code for ADR-0006). Registered in docs/UPSTREAMS.md. Behaviour locked by
// tests/TestVectors/remote-ssh.json. Not the same parser as Hosting.DshLaunchToken (local DSH, other rules).

using System.Text.RegularExpressions;

namespace DshMobile.Runtime.Remote;

/// <summary>
/// The launch token of a remote <c>dsh web</c>, read from its <c>dsh web: …?token=…</c> line. The token lives only in
/// memory; anything kept for messages goes through <see cref="Redact"/>.
/// </summary>
public static class RemoteLaunchToken
{
    // Java's \s is ASCII-only: [ \t\n\x0B\f\r]. (TokenFrom stops at any Kotlin whitespace; the legacy pair differs.)
    private static readonly Regex TokenPattern = new(@"token=[^ \t\n\x0B\f\r&#]+", RegexOptions.CultureInvariant);

    /// <summary><c>dsh web: http://host:port/?token=&lt;value&gt;</c> → value; null without the marker or an empty value.</summary>
    public static string? TokenFrom(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var at = line.IndexOf("dsh web:", StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }
        var t = line.IndexOf("token=", at, StringComparison.Ordinal);
        if (t < 0)
        {
            return null;
        }
        var start = t + "token=".Length;
        var end = start;
        while (end < line.Length && !KotlinText.IsWhitespace(line[end]) && line[end] != '&' && line[end] != '#')
        {
            end++;
        }
        return end > start ? line[start..end] : null;
    }

    /// <summary>Remote output kept for error messages never carries a token.</summary>
    public static string Redact(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return TokenPattern.Replace(line, "token=***");
    }
}
