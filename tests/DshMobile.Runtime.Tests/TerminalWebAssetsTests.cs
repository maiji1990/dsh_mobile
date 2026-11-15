using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// PR-06 C# terminal page (src/DshMobile.Android/Assets/terminal): the vendored xterm.js files are byte-identical to
/// the pinned npm tarballs (tooling/terminal-web.lock.json), and the page stays offline and locked down.
/// </summary>
public class TerminalWebAssetsTests
{
    private static string Asset(params string[] parts) => Repo.PathOf(["src", "DshMobile.Android", "Assets", "terminal", .. parts]);

    [Fact]
    public void VendoredFiles_MatchTheLock_AndNothingUnpinnedIsVendored()
    {
        using var lockDoc = JsonDocument.Parse(File.ReadAllText(Repo.PathOf("tooling", "terminal-web.lock.json")));
        var dir = Repo.PathOf(lockDoc.RootElement.GetProperty("dir").GetString()!.Split('/'));
        var pinned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pkg in lockDoc.RootElement.GetProperty("packages").EnumerateArray())
        {
            Assert.Matches(@"^\d+\.\d+\.\d+$", pkg.GetProperty("version").GetString()!); // exact, never a range
            Assert.StartsWith("sha512-", pkg.GetProperty("integrity").GetString(), StringComparison.Ordinal);
            foreach (var file in pkg.GetProperty("files").EnumerateObject())
            {
                var path = Path.Combine(dir, file.Name);
                var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
                Assert.True(file.Value.GetProperty("sha256").GetString() == actual, $"{file.Name}: {actual}");
                pinned.Add(file.Name);
            }
        }
        Assert.Equal(pinned.Order(), Directory.EnumerateFiles(dir).Select(Path.GetFileName).Order()!);
    }

    [Fact]
    public void Page_HasAStrictCsp_AndLoadsOnlyLocalFiles()
    {
        var html = File.ReadAllText(Asset("index.html"));
        var csp = Regex.Match(html, "Content-Security-Policy\"\\s+content=\"([^\"]+)\"").Groups[1].Value;
        foreach (var directive in new[] { "default-src 'none'", "script-src 'self'", "connect-src 'none'", "img-src 'none'", "frame-src 'none'", "form-action 'none'" })
        {
            Assert.Contains(directive, csp, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("unsafe-eval", csp, StringComparison.Ordinal);
        var refs = Regex.Matches(html, "(?:src|href)=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(["vendor/xterm.css", "vendor/xterm.js", "vendor/addon-fit.js", "terminal.js"], refs);
        Assert.All(refs, r => Assert.True(File.Exists(Asset(r.Split('/'))), r));
    }

    [Fact]
    public void GlueScript_UsesNoNetworkStorageOrDynamicCode()
    {
        var js = File.ReadAllText(Asset("terminal.js"));
        foreach (var api in new[] { "fetch(", "XMLHttpRequest", "WebSocket", "EventSource", "eval(", "new Function", "innerHTML", "localStorage", "indexedDB", "document.cookie", "location" })
        {
            Assert.DoesNotContain(api, js, StringComparison.Ordinal);
        }
        Assert.Contains("e.data !== 'dshm-port'", js, StringComparison.Ordinal); // takes exactly one handed-over port
    }

    [Fact]
    public void Screen_ServesOnlyTerminalAssets_AndBlocksNavigation()
    {
        var cs = File.ReadAllText(Repo.PathOf("src", "DshMobile.Android", "Terminal", "TerminalFragment.cs"));
        Assert.Contains("path.StartsWith(\"/terminal/\", StringComparison.Ordinal) && !path.Contains(\"..\", StringComparison.Ordinal)", cs, StringComparison.Ordinal);
        Assert.Contains("return Refuse(403, \"Forbidden\");", cs, StringComparison.Ordinal);
        Assert.Contains("public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request) => true;", cs, StringComparison.Ordinal);
        Assert.Contains("s.AllowFileAccess = false;", cs, StringComparison.Ordinal);
        Assert.Contains("s.AllowContentAccess = false;", cs, StringComparison.Ordinal);
        Assert.DoesNotContain("AddJavascriptInterface", cs, StringComparison.Ordinal);
    }
}
