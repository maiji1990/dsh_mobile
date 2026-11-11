using System.Text.Json;
using DshMobile.Core.WebUi;

namespace DshMobile.Core.Tests;

/// <summary>
/// C# half of the WebView policy contract: tests/TestVectors/webview-policy.json is also run against the legacy
/// DSHBox DshOriginPolicy (real java.net.URI), DshTokenRevisionTracker and WebUiController.urlWithToken
/// (native-bridge LegacyWebViewPolicyParityTest).
/// </summary>
public class WebViewPolicyParityTests
{
    private static JsonElement Doc()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "tests", "TestVectors", "webview-policy.json"))).RootElement.Clone();
    }

    [Fact]
    public void OriginPolicy_MatchesJavaNetUriSemantics()
    {
        var wrong = new List<string>();
        var checkedCount = 0;
        foreach (var b in Doc().GetProperty("originPolicy").EnumerateArray())
        {
            var baseUrl = b.GetProperty("base").GetString()!;
            DshOriginPolicy? policy = null;
            try
            {
                policy = DshOriginPolicy.Create(baseUrl);
            }
            catch (ArgumentException)
            {
            }
            if ((policy is not null) != b.GetProperty("valid").GetBoolean())
            {
                wrong.Add($"base '{baseUrl}': valid={policy is not null}");
            }
            checkedCount++;
            if (policy is null || !b.TryGetProperty("candidates", out var cs))
            {
                continue;
            }
            foreach (var c in cs.EnumerateArray())
            {
                var url = c.GetProperty("url").GetString()!;
                var actual = policy.IsTrusted(url);
                if (actual != c.GetProperty("trusted").GetBoolean())
                {
                    wrong.Add($"base '{baseUrl}' url '{url}': trusted={actual}");
                }
                checkedCount++;
            }
        }
        Assert.True(checkedCount > 70, $"checked only {checkedCount}");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void SystemUri_WouldTrustWhatLegacyRejects()
    {
        // Why the policy has its own parser: System.Uri canonicalizes these to a trusted loopback origin.
        Assert.Equal("127.0.0.1", new Uri("http://127.1:3080/").Host);
        Assert.Equal("[::1]", new Uri("http://[0:0:0:0:0:0:0:1]:3080/").Host);
        var policy = DshOriginPolicy.Create("http://127.0.0.1:3080");
        Assert.False(policy.IsTrusted("http://127.1:3080/"));
        Assert.False(DshOriginPolicy.Create("http://[::1]:3080").IsTrusted("http://[0:0:0:0:0:0:0:1]:3080/"));
    }

    [Fact]
    public void TokenTracker_MatchesSharedSequences()
    {
        foreach (var seq in Doc().GetProperty("tokenTracker").EnumerateArray())
        {
            var t = new DshTokenRevisionTracker();
            var i = 0;
            foreach (var s in seq.GetProperty("steps").EnumerateArray())
            {
                var label = $"{seq.GetProperty("name").GetString()} #{i++}";
                if (s.TryGetProperty("clear", out var clear) && clear.GetBoolean())
                {
                    t.ClearForRelease();
                }
                else
                {
                    var v = s.GetProperty("update").ValueKind == JsonValueKind.Null ? null : s.GetProperty("update").GetString();
                    Assert.True(s.GetProperty("changed").GetBoolean() == t.Update(v), label);
                }
                Assert.True(s.GetProperty("hasToken").GetBoolean() == (t.Token is not null), label);
                long? expected = s.GetProperty("revision").ValueKind == JsonValueKind.Null ? null : s.GetProperty("revision").GetInt64();
                Assert.True(expected == t.Revision, $"{label}: revision {t.Revision}");
                Assert.True(t.Token is null || !t.ToString().Contains(t.Token, StringComparison.Ordinal), label);
            }
        }
    }

    [Fact]
    public void UrlWithToken_MatchesSharedVectors()
    {
        foreach (var c in Doc().GetProperty("urlWithToken").EnumerateArray())
        {
            var token = c.GetProperty("token").ValueKind == JsonValueKind.Null ? null : c.GetProperty("token").GetString();
            Assert.Equal(c.GetProperty("url").GetString(), WebUiPolicy.UrlWithToken(c.GetProperty("base").GetString()!, token));
        }
    }

    [Theory]
    [InlineData("http://127.0.0.1:3080/x", true, WebUiNavigation.Allow)]
    [InlineData("http://127.0.0.1:3080/x", false, WebUiNavigation.Allow)]
    [InlineData("https://example.com/", true, WebUiNavigation.OpenExternally)]
    [InlineData("HTTP://example.com/", true, WebUiNavigation.OpenExternally)]
    [InlineData("https://example.com/", false, WebUiNavigation.Block)]
    [InlineData("intent://x#Intent;end", true, WebUiNavigation.Block)]
    [InlineData("javascript:alert(1)", true, WebUiNavigation.Block)]
    [InlineData("file:///sdcard/a", true, WebUiNavigation.Block)]
    [InlineData("http://127.1:3080/", true, WebUiNavigation.OpenExternally)]
    public void Navigate_FollowsLegacyShouldOverrideUrlLoading(string url, bool mainFrame, WebUiNavigation expected) =>
        Assert.Equal(expected, WebUiPolicy.Navigate(DshOriginPolicy.Create("http://127.0.0.1:3080"), url, mainFrame));

    [Fact]
    public void Unauthorized_RetriesOncePerTokenRevision()
    {
        long? recovered = null;
        Assert.True(WebUiPolicy.ShouldRetryAfter401(1, ref recovered));
        Assert.False(WebUiPolicy.ShouldRetryAfter401(1, ref recovered)); // second 401, same token: report, don't loop
        Assert.True(WebUiPolicy.ShouldRetryAfter401(2, ref recovered)); // new token revision: one more re-exchange
        Assert.False(WebUiPolicy.ShouldRetryAfter401(null, ref recovered)); // no token at all
    }
}
