using System.Text.RegularExpressions;

namespace DshMobile.Agent.Engines;

/// <summary>
/// Unified privacy redactor applied before UI approval cards, send previews, or disk cache persistence (P0-3).
/// Strips bearer tokens, API keys, cookie pairs, private key headers, and common key=value / JSON credential fields.
/// </summary>
public static partial class SecretRedactor
{
    public static string Redact(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var result = KnownTokenPrefixRegex().Replace(raw, "[REDACTED]");
        result = JsonSecretPropertyRegex().Replace(result, "\"$1\":\"[REDACTED]\"");
        result = KeyValueSecretRegex().Replace(result, "$1=[REDACTED]");
        result = PrivateKeyBlockRegex().Replace(result, "[REDACTED_PRIVATE_KEY]");
        return result;
    }

    public static string RedactAndTruncate(string raw, int maxChars = 200)
    {
        var clean = Redact(raw);
        return clean.Length <= maxChars ? clean : string.Concat(clean.AsSpan(0, maxChars), "…");
    }

    [GeneratedRegex(
        @"(sk-[A-Za-z0-9_-]{6,}|ghp_[A-Za-z0-9]{10,}|gho_[A-Za-z0-9]{10,}|AKIA[0-9A-Z]{16}|xox[bp]-[A-Za-z0-9-]+|Bearer\s+[A-Za-z0-9._-]+|dsh-auth-\d+=[^\s;]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex KnownTokenPrefixRegex();

    [GeneratedRegex(
        "\\\\?\"(password|passwd|pwd|apiKey|api_key|api-key|secret|clientSecret|client_secret|token|accessToken|access_token|access-token|refreshToken|refresh_token|authorization|privateKey|private_key|credential|launchToken)\\\\?\"\\s*:\\s*\\\\?\"[^\"\\\\]*\\\\?\"",
        RegexOptions.IgnoreCase)]
    private static partial Regex JsonSecretPropertyRegex();

    [GeneratedRegex(
        @"(?<=\b|--|\b[xX]-)(password|passwd|pwd|apiKey|api_key|api-key|secret|token|access_token|access-token|authorization|private_key|launchToken)\s*(?:[=:]|\s+)\s*([^\s,;""']+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex KeyValueSecretRegex();

    [GeneratedRegex(
        @"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?(?:-----END [A-Z0-9 ]*PRIVATE KEY-----|$)",
        RegexOptions.IgnoreCase)]
    private static partial Regex PrivateKeyBlockRegex();
}
