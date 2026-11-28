using DshMobile.Runtime;
using DshMobile.Runtime.Guest;
using DshMobile.Runtime.Plugins;

namespace DshMobile.Runtime.Tests;

/// <summary>插件受控升级的运行时边界测试：精确版本校验、命令构建、内置包拦截与零进程语义。</summary>
public sealed class PluginUpgradeTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("dshm-plugin-upgrade-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private RuntimeLayout CreateLayout() => new(
        Path.Combine(_directory, "files"),
        Path.Combine(_directory, "cache"),
        Path.Combine(_directory, "native"));

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("0.0.1")]
    [InlineData("1.2.3-rc.1")]
    [InlineData("1.2.3+build.5")]
    [InlineData("1.2.3-rc.1+build.5")]
    public void IsValidExactVersion_AcceptsStrictSemVer(string version)
    {
        Assert.True(PluginCommandRules.IsValidExactVersion(version));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("latest")]
    [InlineData("^1.2.3")]
    [InlineData("~1.2.3")]
    [InlineData(">=1.0.0")]
    [InlineData("1.2")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.3; rm -rf /")]
    [InlineData("../1.2.3")]
    [InlineData("-1.2.3")]
    [InlineData("1.2.3 ")]
    public void IsValidExactVersion_RejectsRangesTagsAndInjection(string version)
    {
        Assert.False(PluginCommandRules.IsValidExactVersion(version));
    }

    [Fact]
    public void TryBuildUpgradeCommand_ValidThirdParty_QuotesEveryToken()
    {
        Assert.True(PluginCommandRules.TryBuildUpgradeCommand(
            "web", "my-plugin", "1.2.3", out var shellLine, out var error));

        Assert.Null(error);
        Assert.Contains(
            "'plugin' '--profile' 'web' 'add' 'my-plugin@1.2.3'",
            shellLine,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TryBuildUpgradeCommand_RejectsProtectedPackageAndRange()
    {
        Assert.False(PluginCommandRules.TryBuildUpgradeCommand(
            "web", "@deepseek-ai/dsh-web-app", "1.2.3", out _, out var protectedError));
        Assert.Contains("protected", protectedError, StringComparison.OrdinalIgnoreCase);

        Assert.False(PluginCommandRules.TryBuildUpgradeCommand(
            "web", "my-plugin", "^1.2.3", out _, out var rangeError));
        Assert.Contains("exact SemVer", rangeError, StringComparison.OrdinalIgnoreCase);

        Assert.False(PluginCommandRules.TryBuildUpgradeCommand(
            "desktop", "my-plugin", "1.2.3", out _, out var profileError));
        Assert.Contains("profile", profileError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsInstallationShippedPackage_ReadsDependencies_AndFailsSafe()
    {
        var layout = CreateLayout();
        var dshLayerDirectory = layout.LayerDir("dsh");
        var manifestDirectory = Path.Combine(dshLayerDirectory, "node_modules", "@deepseek-ai", "dsh");
        Directory.CreateDirectory(manifestDirectory);
        File.WriteAllText(
            Path.Combine(manifestDirectory, "package.json"),
            """{"name":"@deepseek-ai/dsh","dependencies":{"@deepseek-ai/dsh-web-app":"0.2.3-alpha.2"}}""");

        Assert.True(PluginCommandRules.IsInstallationShippedPackage(dshLayerDirectory, "@deepseek-ai/dsh-web-app"));
        Assert.False(PluginCommandRules.IsInstallationShippedPackage(dshLayerDirectory, "third-party-plugin"));
        Assert.False(PluginCommandRules.IsInstallationShippedPackage(
            Path.Combine(_directory, "missing-layer"), "@deepseek-ai/dsh-web-app"));
        Assert.False(PluginCommandRules.IsInstallationShippedPackage(string.Empty, "@deepseek-ai/dsh-web-app"));
    }

    [Fact]
    public async Task UpgradeAsync_InvalidInputs_DoNotInvokeGuestRunner()
    {
        var guestCalls = 0;
        var layout = CreateLayout();
        var shippedDirectory = Path.Combine(layout.LayerDir("dsh"), "node_modules", "@deepseek-ai", "dsh");
        Directory.CreateDirectory(shippedDirectory);
        File.WriteAllText(
            Path.Combine(shippedDirectory, "package.json"),
            """{"name":"@deepseek-ai/dsh","dependencies":{"@deepseek-ai/dsh-shipped-test":"0.2.3-alpha.2"}}""");

        var service = new GuestPluginService(layout, (_, _, _) =>
        {
            guestCalls++;
            return Task.FromResult(new GuestProcessResult(true, 0, string.Empty, null));
        });

        var invalidVersion = await service.UpgradeAsync("my-plugin", "^1.2.3", CancellationToken.None);
        Assert.False(invalidVersion.Ok);
        Assert.Equal(RuntimeErrorCodes.BadRequest, invalidVersion.Code);

        var protectedPackage = await service.UpgradeAsync("@deepseek-ai/dsh-web-app", "1.2.3", CancellationToken.None);
        Assert.False(protectedPackage.Ok);
        Assert.Equal(RuntimeErrorCodes.BadRequest, protectedPackage.Code);

        var shippedPackage = await service.UpgradeAsync("@deepseek-ai/dsh-shipped-test", "1.2.3", CancellationToken.None);
        Assert.False(shippedPackage.Ok);
        Assert.Equal(GuestPluginService.ShippedPackageCode, shippedPackage.Code);

        Assert.Equal(0, guestCalls);
    }

    [Fact]
    public async Task UpgradeAsync_ValidPackage_RunsQuotedAddCommand()
    {
        var executedCommands = new List<string>();
        var service = new GuestPluginService(CreateLayout(), (command, _, _) =>
        {
            executedCommands.Add(command);
            return Task.FromResult(new GuestProcessResult(true, 0, string.Empty, null));
        });

        var result = await service.UpgradeAsync("@thirdparty/demo-plugin", "2.0.0", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Single(executedCommands);
        Assert.Contains(
            "'plugin' '--profile' 'web' 'add' '@thirdparty/demo-plugin@2.0.0'",
            executedCommands[0],
            StringComparison.Ordinal);
    }
}
