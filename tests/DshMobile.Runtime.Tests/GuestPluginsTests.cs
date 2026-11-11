using DshMobile.Runtime.Guest;

namespace DshMobile.Runtime.Tests;

public class GuestPluginsTests
{
    private const string PnpmList = """
        Progress: resolved 1, reused 0
        [
          {
            "name": "web-profile",
            "dependencies": {
              "zeta": { "version": "2.0.0" },
              "@local/browser-use": { "version": "link:../x" },
              "is-number": { "version": "7.0.0" }
            },
            "devDependencies": { "typescript": { "version": "5.9.0" } },
            "optionalDependencies": { "fsevents": "not-an-object" }
          }
        ]
        """;

    [Fact]
    public void ParseList_ReadsAllDependencySectionsSortedOrdinal()
    {
        var list = GuestPlugins.ParseList(PnpmList);
        Assert.Equal(
            [("@local/browser-use", "link:../x"), ("fsevents", ""), ("is-number", "7.0.0"), ("typescript", "5.9.0"), ("zeta", "2.0.0")],
            list);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ERR_PNPM_NO_IMPORTER_MANIFEST_FOUND")]
    [InlineData("[ not json")]
    public void ParseList_UnparsableOutput_IsEmpty(string output) => Assert.Empty(GuestPlugins.ParseList(output));

    [Fact]
    public void ShellLine_QuotesEveryTokenAndMatchesLegacyShape()
    {
        Assert.True(PluginCommand.TryBuild("add", "web", "pkg@>=1.0.0", out var argv, out _));
        Assert.Equal(
            "export PATH=/opt/dshbox:/root/projects/.dsh/dshbox/bin:$PATH; cd /root/projects && '/root/projects/.dsh/dshbox/bin/dsh' 'plugin' '--profile' 'web' 'add' 'pkg@>=1.0.0'",
            GuestPlugins.ShellLine(argv));
    }

    [Fact]
    public void ShellLine_MatchesLegacyRuntimeControllerSource()
    {
        var src = File.ReadAllText(Repo.PathOf("native", "native-bridge", "src", "main", "java", "dshmobile", "nativebridge", "RuntimeController.kt"));
        // runGuestScript's PATH (with Kotlin's ${'$'} for '$') and runPluginCommand's cd + quoted wrapper + shellJoin(argv).
        Assert.Contains("val path = \"export PATH=/opt/dshbox:/root/projects/.dsh/dshbox/bin:${'$'}PATH\"", src, StringComparison.Ordinal);
        Assert.Contains("runGuestScript(\"cd /root/projects && ${shellQuote(GUEST_DSH)} ${shellJoin(argv)}\")", src, StringComparison.Ordinal);
        Assert.StartsWith(GuestPlugins.PathExport.Replace("$PATH", "", StringComparison.Ordinal), "export PATH=/opt/dshbox:/root/projects/.dsh/dshbox/bin:", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_InvalidOperand_IsBadRequestWithoutSpawning()
    {
        var layout = new RuntimeLayout(Path.Combine(Path.GetTempPath(), "dshm-plugins-none"), null, Path.Combine(Path.GetTempPath(), "dshm-nolibs"));
        var r = await GuestPlugins.RunAsync(new ProotGuestProcess(layout), "add", "web", "pkg; rm -rf /", CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Null(r.ExitCode);
        Assert.StartsWith("BAD_REQUEST", r.Error, StringComparison.Ordinal);
    }
}
