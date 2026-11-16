namespace DshMobile.Application.Terminal;

/// <summary>A link tapped in terminal output, after validation: what the confirmation dialog shows.</summary>
public sealed record TerminalLink(Uri Uri, string Host, bool HasUserInfo);

/// <summary>
/// vNext PR-08: links recognised in terminal output are untrusted text (any program can print one). The page only asks;
/// the host accepts absolute http(s) URLs with a host, at most <see cref="MaxLength"/> long, and the user confirms
/// (seeing the host on its own line, and a warning when the URL carries a <c>user@</c> part, the classic way to
/// disguise one host as another) before the system browser opens it. Nothing opens inside the app.
/// </summary>
public static class TerminalLinks
{
    public const int MaxLength = 2048;

    public static TerminalLink? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength || text.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            return null;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.IdnHost))
        {
            return null;
        }
        return new TerminalLink(uri, uri.IdnHost, uri.UserInfo.Length > 0);
    }
}
