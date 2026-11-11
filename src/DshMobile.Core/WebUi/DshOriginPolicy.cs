// Ported from DSHBox v1.5.1 (7c9d33c) dsh-renderer/api DshOriginPolicy.kt (GPL-3.0-or-later). Parity:
// tests/TestVectors/webview-policy.json (legacy: native-bridge LegacyWebViewPolicyParityTest, real java.net.URI).

using System.Globalization;

namespace DshMobile.Core.WebUi;

/// <summary>
/// Exact DSH loopback trust-origin check: trusted only when scheme + host + effective port equal the configured base
/// and the host is literally <c>127.0.0.1</c>, <c>localhost</c> or <c>[::1]</c> (case-insensitive), with no user info.
/// <para>
/// Deliberately does NOT use <see cref="Uri"/>: System.Uri normalizes inputs that java.net.URI keeps literal (e.g.
/// <c>http://127.1</c> → 127.0.0.1, expanded IPv6 → <c>::1</c>), so it would trust origins the legacy policy rejects.
/// This parser implements the java.net.URI (RFC 2396 + RFC 2732) rules the decision depends on, and rejects anything
/// java.net.URI would reject (illegal characters, bad escapes, malformed authority).
/// </para>
/// </summary>
public sealed class DshOriginPolicy
{
    private readonly string _scheme;
    private readonly string _host;
    private readonly int _port;

    private DshOriginPolicy(string scheme, string host, int port)
    {
        _scheme = scheme;
        _host = host;
        _port = port;
    }

    /// <exception cref="ArgumentException">The base is not an absolute http(s) loopback origin without user info.</exception>
    public static DshOriginPolicy Create(string baseOrigin)
    {
        ArgumentNullException.ThrowIfNull(baseOrigin);
        var o = Parse(baseOrigin) ?? throw new ArgumentException("Base origin must be an absolute http(s) loopback URI without user info", nameof(baseOrigin));
        if (o.Port is < 0 or > 65535)
        {
            throw new ArgumentException("Base origin port is out of range", nameof(baseOrigin));
        }
        return new DshOriginPolicy(o.Scheme, o.Host, o.Port);
    }

    public bool IsTrusted(string candidate)
    {
        if (candidate is null || Parse(candidate) is not { } o || o.Port is < 0 or > 65535)
        {
            return false;
        }
        return o.Scheme == _scheme && o.Host == _host && o.Port == _port;
    }

    private readonly record struct Origin(string Scheme, string Host, int Port);

    /// <summary>
    /// Null unless the input is a java.net.URI-valid absolute hierarchical http(s) URI whose server authority has no
    /// user info and a canonical loopback host. Port is the effective port (default 80/443), possibly out of range.
    /// </summary>
    private static Origin? Parse(string s)
    {
        // scheme ":" — the first ':' before any of "/?#" ends the scheme (otherwise the URI is relative).
        var colon = s.IndexOf(':', StringComparison.Ordinal);
        var firstDelim = s.IndexOfAny(['/', '?', '#']);
        if (colon <= 0 || (firstDelim >= 0 && firstDelim < colon))
        {
            return null;
        }
        var scheme = s[..colon];
        if (!char.IsAsciiLetter(scheme[0]) || !scheme.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.'))
        {
            return null;
        }
        scheme = scheme.ToLowerInvariant();
        if (scheme is not ("http" or "https"))
        {
            return null;
        }
        var rest = s.AsSpan(colon + 1);
        if (!rest.StartsWith("//"))
        {
            return null; // opaque or not hierarchical
        }
        rest = rest[2..];
        var authEnd = rest.IndexOfAny('/', '?', '#');
        var authority = authEnd < 0 ? rest : rest[..authEnd];
        var tail = authEnd < 0 ? ReadOnlySpan<char>.Empty : rest[authEnd..];
        if (!ValidTail(tail))
        {
            return null;
        }
        if (authority.Contains('@'))
        {
            return null; // user info is never trusted (and rejected for a base)
        }

        string host;
        ReadOnlySpan<char> portPart;
        if (authority.StartsWith("["))
        {
            var close = authority.IndexOf(']');
            if (close < 0)
            {
                return null;
            }
            host = authority[1..close].ToString().ToLowerInvariant();
            portPart = authority[(close + 1)..];
            if (host != "::1")
            {
                return null; // only the literal [::1]; java keeps other IPv6 spellings literal too
            }
        }
        else
        {
            var c = authority.IndexOf(':');
            host = (c < 0 ? authority : authority[..c]).ToString().ToLowerInvariant();
            portPart = c < 0 ? ReadOnlySpan<char>.Empty : authority[c..];
            if (host is not ("127.0.0.1" or "localhost"))
            {
                return null;
            }
        }

        int port;
        if (portPart.IsEmpty || portPart is ":")
        {
            port = scheme == "https" ? 443 : 80; // java: empty port = -1 = default
        }
        else
        {
            var digits = portPart[1..];
            if (portPart[0] != ':' || digits.IsEmpty || digits.IndexOfAnyExceptInRange('0', '9') >= 0)
            {
                return null; // not a server authority (java falls back to registry-based: host == null)
            }
            if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out port))
            {
                return null; // java: malformed port number
            }
        }
        return new Origin(scheme, host, port);
    }

    /// <summary>java.net.URI character rules for path (pchar ; /), query and fragment (uric, incl. "[]").</summary>
    private static bool ValidTail(ReadOnlySpan<char> tail)
    {
        var part = 0; // 0 path, 1 query, 2 fragment
        for (var i = 0; i < tail.Length; i++)
        {
            var c = tail[i];
            if (c == '?' && part == 0)
            {
                part = 1;
                continue;
            }
            if (c == '#' && part < 2)
            {
                part = 2;
                continue;
            }
            if (c == '%')
            {
                if (i + 2 >= tail.Length || !char.IsAsciiHexDigit(tail[i + 1]) || !char.IsAsciiHexDigit(tail[i + 2]))
                {
                    return false;
                }
                i += 2;
                continue;
            }
            if (char.IsAsciiLetterOrDigit(c) || "_-!.~'()*".Contains(c) || ":@&=+$,;/".Contains(c))
            {
                continue;
            }
            if (part > 0 && (c is '?' or '[' or ']'))
            {
                continue; // reserved in query / fragment
            }
            if (c > 0x7F && IsJavaOther(c))
            {
                continue;
            }
            return false;
        }
        return true;
    }

    /// <summary>java.net.URI "other": non-US-ASCII, not an ISO control and not a Java space char.</summary>
    private static bool IsJavaOther(char c)
    {
        if (c is >= '\u0080' and <= '\u009F')
        {
            return false;
        }
        return char.GetUnicodeCategory(c) is not (UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);
    }
}
