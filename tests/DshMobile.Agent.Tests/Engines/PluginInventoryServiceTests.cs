using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;

namespace DshMobile.Agent.Tests.Engines;

/// <summary>插件清单合并、更新检查与受控升级的引擎层用例。</summary>
public sealed class PluginInventoryServiceTests
{
    private const string ThirdPartyPackage = "@thirdparty/demo-plugin";
    private const string BuiltinPackage = "@deepseek-ai/dsh-web-app";
    private const string ProtectedPackage = "@deepseek-ai/dsh-base";

    [Fact]
    public async Task LoadInventoryAsync_MergesAvailability_FromListBundles()
    {
        var gateway = new StubGateway
        {
            Entries =
            [
                new PluginInventoryEntryView("include:third-party", ThirdPartyPackage, true, "active"),
                new PluginInventoryEntryView("include:builtin", BuiltinPackage, true, "active"),
            ],
            Bundles =
            [
                CreateBundle(ThirdPartyPackage, "profile", removable: true),
                CreateBundle(BuiltinPackage, "installation", removable: false),
            ],
        };
        var operations = new StubOperations
        {
            InstalledPackages = [new PluginInstalledPackageView(ThirdPartyPackage, "1.0.0")],
        };
        var service = new PluginInventoryService(gateway, operations);

        var outcome = await service.LoadInventoryAsync();

        Assert.True(outcome.IsOk);
        var entries = outcome.Value!.Entries;
        Assert.Equal("profile", Assert.Single(entries, entry => entry.PackageName == ThirdPartyPackage).Availability);
        Assert.Equal("installation", Assert.Single(entries, entry => entry.PackageName == BuiltinPackage).Availability);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_MarksOnlyProfileOwnedPackages_AsUpdatable()
    {
        var gateway = new StubGateway
        {
            Entries =
            [
                new PluginInventoryEntryView("include:third-party", ThirdPartyPackage, true, "active"),
                new PluginInventoryEntryView("include:builtin", BuiltinPackage, true, "active"),
                new PluginInventoryEntryView("include:protected", ProtectedPackage, true, "active"),
            ],
            Bundles =
            [
                CreateBundle(ThirdPartyPackage, "profile", removable: true),
                CreateBundle(BuiltinPackage, "installation", removable: false),
                CreateBundle(ProtectedPackage, "profile", removable: true),
            ],
        };
        var operations = new StubOperations
        {
            InstalledPackages =
            [
                new PluginInstalledPackageView(ThirdPartyPackage, "1.0.0"),
                new PluginInstalledPackageView(ProtectedPackage, "1.0.0"),
            ],
        };
        var catalogSearch = new StubCatalogSearch
        {
            LatestVersions =
            {
                [ThirdPartyPackage] = "2.0.0",
                [BuiltinPackage] = "2.0.0",
                [ProtectedPackage] = "2.0.0",
            },
        };
        var service = new PluginInventoryService(gateway, operations, catalogSearch);
        var inventory = await service.LoadInventoryAsync();
        Assert.True(inventory.IsOk);

        var outcome = await service.CheckForUpdatesAsync(inventory.Value!.Entries);

        Assert.True(outcome.IsOk);
        var result = outcome.Value!;
        Assert.Equal(1, result.CheckedCount);
        Assert.Equal(0, result.FailedCount);

        var thirdParty = Assert.Single(result.Entries, entry => entry.PackageName == ThirdPartyPackage);
        Assert.Equal("2.0.0", thirdParty.LatestVersion);
        Assert.True(thirdParty.HasUpdate);
        Assert.True(thirdParty.CanUpgrade);

        var builtin = Assert.Single(result.Entries, entry => entry.PackageName == BuiltinPackage);
        Assert.False(builtin.HasUpdate);
        Assert.False(builtin.CanUpgrade);

        var protectedEntry = Assert.Single(result.Entries, entry => entry.PackageName == ProtectedPackage);
        Assert.False(protectedEntry.HasUpdate);
        Assert.False(protectedEntry.CanUpgrade);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_UnsupportedCatalogSearch_ReturnsUnsupported()
    {
        var gateway = new StubGateway
        {
            Entries = [new PluginInventoryEntryView("include:third-party", ThirdPartyPackage, true, "active")],
            Bundles = [CreateBundle(ThirdPartyPackage, "profile", removable: true)],
        };
        var operations = new StubOperations
        {
            InstalledPackages = [new PluginInstalledPackageView(ThirdPartyPackage, "1.0.0")],
        };
        var service = new PluginInventoryService(gateway, operations);
        var inventory = await service.LoadInventoryAsync();
        Assert.True(inventory.IsOk);

        var outcome = await service.CheckForUpdatesAsync(inventory.Value!.Entries);

        Assert.False(outcome.IsOk);
        Assert.Equal(DshFailureCode.Unsupported, outcome.Error!.Code);
    }

    [Fact]
    public async Task UpgradePluginAsync_InvalidOrProtectedSpec_ZerosUpgradeCalls()
    {
        var operations = new StubOperations();
        var service = new PluginInventoryService(new StubGateway(), operations);

        var invalidVersion = await service.UpgradePluginAsync(ThirdPartyPackage, "^2.0.0");
        Assert.False(invalidVersion.IsOk);
        Assert.Equal(DshFailureCode.InvalidState, invalidVersion.Error!.Code);

        var protectedPackage = await service.UpgradePluginAsync(ProtectedPackage, "2.0.0");
        Assert.False(protectedPackage.IsOk);
        Assert.Equal(DshFailureCode.InvalidState, protectedPackage.Error!.Code);

        var invalidName = await service.UpgradePluginAsync("../evil", "2.0.0");
        Assert.False(invalidName.IsOk);

        Assert.Equal(0, operations.UpgradeCalls);
    }

    [Fact]
    public async Task UpgradePluginAsync_ValidSpec_CallsRuntimeAndMarksRestartRequired()
    {
        var operations = new StubOperations();
        var service = new PluginInventoryService(new StubGateway(), operations);

        var outcome = await service.UpgradePluginAsync(ThirdPartyPackage, "2.0.0");

        Assert.True(outcome.IsOk);
        Assert.Equal(1, operations.UpgradeCalls);
        Assert.Equal(ThirdPartyPackage, operations.LastUpgradePackageName);
        Assert.Equal("2.0.0", operations.LastUpgradeTargetVersion);
        Assert.True(outcome.Value!.Succeeded);
        Assert.True(outcome.Value.RequiresDshRestart);
        Assert.Equal(PluginMutationKind.Upgrade, outcome.Value.Kind);
        Assert.Equal("2.0.0", outcome.Value.ToVersion);
        Assert.True(service.RequiresDshRestart);
    }

    private static PluginBundleInfoView CreateBundle(string name, string availability, bool removable) => new(
        Name: name,
        Official: false,
        Availability: availability,
        Version: "1.0.0",
        DisplayName: null,
        Description: null,
        Enabled: true,
        Installed: true,
        Optional: false,
        Removable: removable,
        Rows: [],
        Overrides: []);

    private sealed class StubGateway : DefaultDshGateway
    {
        public IReadOnlyList<PluginInventoryEntryView> Entries { get; init; } = [];
        public IReadOnlyList<PluginBundleInfoView> Bundles { get; init; } = [];

        public override Task<Outcome<PluginInventoryView>> GetPluginInventoryAsync(CancellationToken ct = default) =>
            Task.FromResult(Outcome<PluginInventoryView>.Ok(new PluginInventoryView(false, Entries)));

        public override Task<Outcome<IReadOnlyList<PluginBundleInfoView>>> ListPluginBundlesAsync(CancellationToken ct = default) =>
            Task.FromResult(Outcome<IReadOnlyList<PluginBundleInfoView>>.Ok(Bundles));
    }

    private sealed class StubOperations : IPluginOperations
    {
        public IReadOnlyList<PluginInstalledPackageView> InstalledPackages { get; init; } = [];
        public int UpgradeCalls { get; private set; }
        public string? LastUpgradePackageName { get; private set; }
        public string? LastUpgradeTargetVersion { get; private set; }

        public Task<Outcome<IReadOnlyList<PluginInstalledPackageView>>> ListInstalledPackagesAsync(CancellationToken ct = default) =>
            Task.FromResult(Outcome<IReadOnlyList<PluginInstalledPackageView>>.Ok(InstalledPackages));

        public Task<Outcome<Unit>> InstallPackageAsync(string packageSpec, CancellationToken ct = default) =>
            Task.FromResult(Outcome<Unit>.Ok(Unit.Value));

        public Task<Outcome<Unit>> UninstallPackageAsync(string packageName, CancellationToken ct = default) =>
            Task.FromResult(Outcome<Unit>.Ok(Unit.Value));

        public Task<Outcome<Unit>> SetPluginEnabledAsync(string entryId, string moduleName, bool enabled, CancellationToken ct = default) =>
            Task.FromResult(Outcome<Unit>.Ok(Unit.Value));

        public Task<Outcome<Unit>> RestartDshHostAsync(CancellationToken ct = default) =>
            Task.FromResult(Outcome<Unit>.Ok(Unit.Value));

        public Task<Outcome<Unit>> UpgradePackageAsync(string packageName, string targetVersion, CancellationToken ct = default)
        {
            UpgradeCalls++;
            LastUpgradePackageName = packageName;
            LastUpgradeTargetVersion = targetVersion;
            return Task.FromResult(Outcome<Unit>.Ok(Unit.Value));
        }
    }

    private sealed class StubCatalogSearch : IPluginCatalogSearch
    {
        public Dictionary<string, string?> LatestVersions { get; } = new(StringComparer.Ordinal);

        public Task<Outcome<PluginCatalogSearchResult>> SearchAsync(string query, int limit = 25, CancellationToken ct = default) =>
            Task.FromResult(Outcome<PluginCatalogSearchResult>.Ok(new PluginCatalogSearchResult([], 0)));

        public Task<Outcome<PluginCatalogDetailView>> GetPackageDetailAsync(string packageName, CancellationToken ct = default) =>
            Task.FromResult(Outcome<PluginCatalogDetailView>.Fail(DshFailureCode.Unsupported));

        public Task<Outcome<IReadOnlyList<PluginLatestVersionView>>> GetLatestVersionsAsync(
            IReadOnlyList<string> packageNames,
            CancellationToken ct = default)
        {
            IReadOnlyList<PluginLatestVersionView> results = packageNames
                .Select(packageName => new PluginLatestVersionView(
                    packageName,
                    LatestVersions.GetValueOrDefault(packageName)))
                .ToArray();
            return Task.FromResult(Outcome<IReadOnlyList<PluginLatestVersionView>>.Ok(results));
        }
    }
}
