// Ported from DSHBox v1.5.1 (7c9d33c) dsh-renderer/api DshTokenRevisionTracker.kt, app/.../webview/DshUrl.kt (via
// the legacy WebUiController.urlWithToken) and the navigation / 401 rules of WebUiController.kt (GPL-3.0-or-later).

using System.Globalization;
using System.Text;

namespace DshMobile.Core.WebUi;

/// <summary>
/// Session-local launch-token availability plus a non-secret, monotonic revision. A token that comes back after a
/// temporary absence keeps its revision; only a genuinely different token gets a new one. Never prints the token.
/// </summary>
public sealed class DshTokenRevisionTracker
{
    private string? _lastAvailable;
    private long _generation;

    public DshTokenRevisionTracker(string? initialToken = null)
    {
        _lastAvailable = string.IsNullOrEmpty(initialToken) ? null : initialToken;
        _generation = _lastAvailable is null ? 0 : 1;
        Token = _lastAvailable;
    }

    public string? Token { get; private set; }

    public long? Revision => Token is null ? null : _generation;

    /// <returns>Whether availability or the token value changed.</returns>
    public bool Update(string? value)
    {
        var available = string.IsNullOrEmpty(value) ? null : value;
        if (string.Equals(available, Token, StringComparison.Ordinal))
        {
            return false;
        }
        Token = available;
        if (available is not null && !string.Equals(available, _lastAvailable, StringComparison.Ordinal))
        {
            _generation++;
            _lastAvailable = available;
        }
        return true;
    }

    /// <summary>Teardown only.</summary>
    public void ClearForRelease()
    {
        Token = null;
        _lastAvailable = null;
        _generation = 0;
    }

    public override string ToString() => $"DshTokenRevisionTracker(revision={Revision?.ToString(CultureInfo.InvariantCulture) ?? "null"})";
}

public enum WebUiNavigation
{
    /// <summary>Trusted DSH origin: load in the WebView.</summary>
    Allow,
    /// <summary>Top-level http(s) elsewhere: hand to the user's browser, never this privileged WebView.</summary>
    OpenExternally,
    /// <summary>Anything else (other schemes, sub-frame navigations off-origin): blocked.</summary>
    Block,
}

/// <summary>The WebView decisions that do not need Android types; the Activity only executes them.</summary>
public static class WebUiPolicy
{
    private const string UnreservedExtra = "-_.~";

    /// <summary>
    /// <c>base?token=&lt;encoded&gt;</c>, replacing any existing token parameter: letters and digits (Unicode) and
    /// <c>-_.~</c> stay, other ASCII is %XX, other non-ASCII passes through (legacy DshUrl rules).
    /// </summary>
    public static string UrlWithToken(string baseUrl, string? token)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        if (string.IsNullOrEmpty(token))
        {
            return baseUrl;
        }
        var encoded = new StringBuilder(token.Length * 3);
        foreach (var c in token)
        {
            if (char.IsLetterOrDigit(c) || UnreservedExtra.Contains(c))
            {
                encoded.Append(c);
            }
            else if (c < 0x80)
            {
                encoded.Append('%').Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
            }
            else
            {
                encoded.Append(c);
            }
        }
        var q = baseUrl.IndexOf('?', StringComparison.Ordinal);
        if (q < 0)
        {
            return $"{baseUrl}?token={encoded}";
        }
        var kept = baseUrl[(q + 1)..].Split('&').Where(p => p.Length > 0 && !p.StartsWith("token=", StringComparison.Ordinal));
        return baseUrl[..q] + "?" + string.Join('&', kept.Append($"token={encoded}"));
    }

    /// <summary><c>shouldOverrideUrlLoading</c>: trusted → allow; main-frame http(s) → external; otherwise block.</summary>
    public static WebUiNavigation Navigate(DshOriginPolicy policy, string url, bool isMainFrame)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.IsTrusted(url))
        {
            return WebUiNavigation.Allow;
        }
        var colon = url.IndexOf(':', StringComparison.Ordinal);
        var scheme = colon > 0 ? url[..colon].ToLowerInvariant() : null;
        return isMainFrame && scheme is "http" or "https" ? WebUiNavigation.OpenExternally : WebUiNavigation.Block;
    }

    /// <summary>
    /// A main-frame 401: re-exchange the token once per token revision; a second 401 on the same revision (or no
    /// token) is reported instead of looping. Returns the new "recovered" revision when a reload should happen.
    /// </summary>
    public static bool ShouldRetryAfter401(long? currentRevision, ref long? recoveredRevision)
    {
        if (currentRevision is { } r && r != recoveredRevision)
        {
            recoveredRevision = r;
            return true;
        }
        return false;
    }
}
