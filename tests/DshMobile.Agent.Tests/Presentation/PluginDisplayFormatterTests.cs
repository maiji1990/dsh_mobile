using DshMobile.Agent.Domain;
using DshMobile.Agent.Presentation;

namespace DshMobile.Agent.Tests.Presentation;

public sealed class PluginDisplayFormatterTests
{
    [Fact]
    public void Format_ExtractsShortTitle_PreservesDescription_DeduplicatesMeta()
    {
        var entry = new PluginEntryView(
            EntryId: "hmr",
            ModuleName: "@deepseek-ai/dsh-hmr",
            PackageName: "@deepseek-ai/dsh-hmr",
            DisplayName: "热重载服务（开发期自动重启）",
            Version: "1.2.3",
            Enabled: true,
            FiberPhase: "active",
            DiagnosticSummary: null,
            HasCustomWebUi: false,
            IsInstalledInProfile: true,
            IsProtected: true,
            CanToggle: false,
            CanUninstall: false);

        var formatted = PluginDisplayFormatter.Format(entry);
        Assert.Equal("dsh-hmr", formatted.ShortTitle);
        Assert.Equal("热重载服务（开发期自动重启）", formatted.Description);
        Assert.Equal("@deepseek-ai/dsh-hmr", formatted.Meta);
    }

    [Fact]
    public void Format_HidesRedundantDescriptionAndSubpathDuplicates()
    {
        var subEntry = new PluginEntryView(
            EntryId: "custom-ui",
            ModuleName: "@example/plugin/client",
            PackageName: "@example/plugin",
            DisplayName: "@example/plugin/client",
            Version: null,
            Enabled: false,
            FiberPhase: "inactive",
            DiagnosticSummary: null,
            HasCustomWebUi: true,
            IsInstalledInProfile: true,
            IsProtected: false,
            CanToggle: true,
            CanUninstall: true);

        var formatted = PluginDisplayFormatter.Format(subEntry);
        Assert.Equal("plugin/client", formatted.ShortTitle);
        Assert.Null(formatted.Description);
        Assert.Equal("@example/plugin/client", formatted.Meta);
    }

    [Fact]
    public void FormatCatalog_ExtractsShortTitle_VersionMeta_AndOptionalDescription()
    {
        var catalogEntry = new PluginCatalogEntryView(
            PackageName: "@deepseek-ai/dsh-timer",
            Version: "1.4.0",
            Description: "  定时提醒与延时触发  ",
            Keywords: ["timer"],
            Publisher: "deepseek",
            PublishedAtUnixMs: 1_700_000_000_000,
            NpmUrl: "https://www.npmjs.com/package/@deepseek-ai/dsh-timer",
            IsOfficialScope: true,
            IsInstalled: false,
            InstalledVersion: null,
            RecommendedInstallSpec: "@deepseek-ai/dsh-timer@1.4.0");

        var formatted = PluginDisplayFormatter.FormatCatalog(catalogEntry);
        Assert.Equal("dsh-timer", formatted.ShortTitle);
        Assert.Equal("定时提醒与延时触发", formatted.Description);
        Assert.Equal("@deepseek-ai/dsh-timer · v1.4.0", formatted.Meta);
    }
}
