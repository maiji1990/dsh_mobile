using DshMobile.Runtime.Guest;
using DshMobile.Runtime.Plugins;

namespace DshMobile.Runtime.Tests;

public sealed class GuestPluginServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dshm-plugins-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private RuntimeLayout CreateLayout() => new(
        Path.Combine(_dir, "files"),
        Path.Combine(_dir, "cache"),
        Path.Combine(_dir, "native"));

    [Theory]
    [InlineData("is-number")]
    [InlineData("lodash.get")]
    [InlineData("@deepseek-ai/dsh-demo")]
    [InlineData("@scope/my-plugin_1.0")]
    public void IsValidPackageName_AcceptsCompliantNames(string name)
    {
        Assert.True(PluginCommandRules.IsValidPackageName(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-g")]
    [InlineData("--registry=https://evil.test")]
    [InlineData("../evil")]
    [InlineData("foo..bar")]
    [InlineData("foo;id")]
    [InlineData("foo && whoami")]
    [InlineData("$(cat /etc/passwd)")]
    [InlineData("foo bar")]
    [InlineData("foo\n")]
    [InlineData("pkg@1.2.3")]
    [InlineData("https://evil.test/a.tgz")]
    [InlineData("file:./local")]
    public void IsValidPackageName_RejectsInjectionAndInvalidNames(string name)
    {
        Assert.False(PluginCommandRules.IsValidPackageName(name));
    }

    [Theory]
    [InlineData("my-plugin")]
    [InlineData("my-plugin@1.2.3")]
    [InlineData("@deepseek-ai/dsh-demo@0.2.2-rc.5")]
    [InlineData("@scope/pkg@2.0.0+build.1")]
    public void IsValidInstallSpec_AcceptsNameAndExactVersion(string spec)
    {
        Assert.True(PluginCommandRules.IsValidInstallSpec(spec));
    }

    [Theory]
    [InlineData("my-plugin@^1.2.3")]
    [InlineData("my-plugin@~1.2.3")]
    [InlineData("my-plugin@>=1.0.0")]
    [InlineData("my-plugin@latest")]
    [InlineData("my-plugin; rm -rf /")]
    [InlineData("my-plugin && curl evil")]
    [InlineData("$(touch /tmp/x)")]
    [InlineData("--save-exact")]
    [InlineData("../escape")]
    [InlineData("my-plugin\n")]
    public void IsValidInstallSpec_RejectsRangesFlagsAndInjection(string spec)
    {
        Assert.False(PluginCommandRules.IsValidInstallSpec(spec));
    }

    [Fact]
    public void BuildAddCommand_UsesGuestCommandLineSingleQuoting()
    {
        Assert.True(PluginCommandRules.TryBuildAddCommand("web", "@deepseek-ai/dsh-demo@0.2.2-rc.5", out var line, out var error));
        Assert.Null(error);
        Assert.Contains("'/root/projects/.dsh/dshmobile/bin/dsh'", line, StringComparison.Ordinal);
        Assert.Contains("'plugin' '--profile' 'web' 'add' '@deepseek-ai/dsh-demo@0.2.2-rc.5'", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallAndRemove_InvalidInputs_DoNotInvokeGuestRunner()
    {
        var calls = 0;
        var svc = new GuestPluginService(CreateLayout(), (_, _, _) =>
        {
            calls++;
            return Task.FromResult(new GuestProcessResult(true, 0, "", null));
        });

        foreach (var bad in new[] { "foo;id", "foo && ls", "$(id)", "--flag", "../bad", "pkg@^1.0.0", "pkg\n" })
        {
            var addRes = await svc.InstallAsync(bad, CancellationToken.None);
            Assert.False(addRes.Ok);
            Assert.Equal(RuntimeErrorCodes.BadRequest, addRes.Code);

            var remRes = await svc.RemoveAsync(bad, CancellationToken.None);
            Assert.False(remRes.Ok);
            Assert.Equal(RuntimeErrorCodes.BadRequest, remRes.Code);
        }

        var protectedRem = await svc.RemoveAsync("@deepseek-ai/dsh-web-app", CancellationToken.None);
        Assert.False(protectedRem.Ok);
        Assert.Equal(RuntimeErrorCodes.BadRequest, protectedRem.Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task InstallAsync_RedactsSecretsOnFailure()
    {
        var svc = new GuestPluginService(CreateLayout(), (_, _, _) =>
            Task.FromResult(new GuestProcessResult(
                false,
                1,
                "npm ERR! Authorization: Bearer secret-jwt-token-xyz\napiKey=sk-live-secret-999999",
                "GUEST_CMD_FAILED")));

        var res = await svc.InstallAsync("demo-plugin@1.0.0", CancellationToken.None);
        Assert.False(res.Ok);
        Assert.NotNull(res.Diagnostic);
        Assert.DoesNotContain("secret-jwt-token-xyz", res.Diagnostic!, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-live-secret-999999", res.Diagnostic!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListInstalledAsync_ParsesPnpmJsonAndDetectsCustomWebUi()
    {
        var layout = CreateLayout();
        var pkgDir = Path.Combine(GuestPluginService.WebProfileHostDir(layout), "node_modules", "@thirdparty", "my-ui-plugin");
        Directory.CreateDirectory(pkgDir);
        await File.WriteAllTextAsync(
            Path.Combine(pkgDir, "package.json"),
            """{"name":"@thirdparty/my-ui-plugin","version":"1.4.2","dsh":{"client":{"platform":"web"}}}""");

        const string stdout = """
            dsh: warning: sample pre-output
            [
              {
                "name": "web-profile",
                "dependencies": {
                  "@thirdparty/my-ui-plugin": { "version": "1.4.2" },
                  "helper-plugin": { "version": "0.9.0" }
                }
              }
            ]
            """;

        var svc = new GuestPluginService(layout, (_, _, _) =>
            Task.FromResult(new GuestProcessResult(true, 0, stdout, null)));

        var res = await svc.ListInstalledAsync(CancellationToken.None);
        Assert.True(res.Ok);
        Assert.Equal(2, res.Packages.Count);
        var uiPkg = Assert.Single(res.Packages, p => p.Name == "@thirdparty/my-ui-plugin");
        Assert.True(uiPkg.HasCustomWebUi);
        Assert.Equal("1.4.2", uiPkg.Version);

        var plainPkg = Assert.Single(res.Packages, p => p.Name == "helper-plugin");
        Assert.False(plainPkg.HasCustomWebUi);
    }

    [Fact]
    public void PluginPatchEditor_PreservesCommentsAndUpdatesDisabledFlagAtomically()
    {
        var patchPath = Path.Combine(_dir, "cordis.patch.yml");
        File.WriteAllText(patchPath, """
            # user comment
            - id: custom-plugin
              disabled: false
            - id: other-js
              config: !!js process.env.FOO
            """);

        var first = PluginPatchEditor.SetPluginDisabled(patchPath, "custom-plugin", disabled: true);
        Assert.True(first.Ok);
        var content1 = File.ReadAllText(patchPath);
        Assert.Contains("# user comment", content1, StringComparison.Ordinal);
        Assert.Contains("disabled: true", content1, StringComparison.Ordinal);
        Assert.Contains("config: !!js process.env.FOO", content1, StringComparison.Ordinal);

        var second = PluginPatchEditor.SetPluginDisabled(patchPath, "new-plugin", disabled: true);
        Assert.True(second.Ok);
        var content2 = File.ReadAllText(patchPath);
        Assert.Contains("- id: new-plugin", content2, StringComparison.Ordinal);
        Assert.False(File.Exists(patchPath + ".tmp"));
    }

    [Fact]
    public async Task SetEnabledAsync_RejectsProtectedCoreEntries()
    {
        var layout = CreateLayout();
        var svc = new GuestPluginService(layout);

        var protectedRes = await svc.SetEnabledAsync("include:connection", "@deepseek-ai/dsh-client-connection", false, CancellationToken.None);
        Assert.False(protectedRes.Ok);
        Assert.Equal(RuntimeErrorCodes.BadRequest, protectedRes.Code);
        Assert.False(File.Exists(GuestPluginService.WebProfilePatchFile(layout)));
    }
}
