using System.Text.Json;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;
using DshMobile.Agent.Presentation;

namespace DshMobile.Agent.Tests.Presentation;

public sealed class PluginDetailViewModelTests
{
    [Fact]
    public async Task LoadInstalledAsync_FallsBackAcrossBundleLocalAndCatalogDocs_AndLoadsLiveSettings()
    {
        var gw = new FakeDetailGateway
        {
            BundleDetailResult = new PluginBundleDetailView(Found: true, Origin: "locale", Path: "locale/zh.md", Markdown: "# 官方本地化文档"),
        };
        var ops = new FakeDetailOperations();
        var catalog = new FakeDetailCatalogSearch();
        var invService = new PluginInventoryService(gw, ops);
        await using var vm = new PluginDetailViewModel(gw, invService, ops, catalog);

        // 1. 优先命中 pluginManager/readBundleDetail
        await vm.LoadInstalledAsync("include:demo-plugin", "zh-CN");
        Assert.True(vm.State.IsInstalled);
        Assert.Equal("# 官方本地化文档", vm.State.MarkdownDocument);
        Assert.Equal("bundle-locale", vm.State.DocumentOrigin);
        Assert.True(vm.State.HasSettings);
        Assert.True(vm.State.SettingsWritable);
        Assert.Equal("demo-plugin", vm.State.SettingsNamespace);
        Assert.Equal(5, vm.State.SettingsRevision);
        Assert.Equal(3, vm.State.ConfigFields.Count);

        // 2. Bundle 无文档时回退本地安全读取
        gw.BundleDetailResult = new PluginBundleDetailView(Found: false);
        ops.LocalDetailMarkdown = "# 本地 README 文档";
        await vm.LoadInstalledAsync("include:demo-plugin", "zh-CN");
        Assert.Equal("# 本地 README 文档", vm.State.MarkdownDocument);
        Assert.Equal("local-readme", vm.State.DocumentOrigin);

        // 3. 本地也无文档时回退远端 npm README
        ops.LocalDetailMarkdown = null;
        await vm.LoadInstalledAsync("include:demo-plugin", "zh-CN");
        Assert.Equal("# 远端 NPM README", vm.State.MarkdownDocument);
        Assert.Equal("npm-readme", vm.State.DocumentOrigin);
    }

    [Fact]
    public async Task LoadCatalogAsync_FusesInstalledStateAndSettings_WhenPackageIsInstalledLocally()
    {
        var gw = new FakeDetailGateway();
        var ops = new FakeDetailOperations();
        var catalog = new FakeDetailCatalogSearch();
        var invService = new PluginInventoryService(gw, ops);
        await using var vm = new PluginDetailViewModel(gw, invService, ops, catalog);

        // 1. 远端包已在本机安装：融合运行态、启停与实时配置
        await vm.LoadCatalogAsync("@thirdparty/demo-plugin");
        Assert.True(vm.State.IsInstalled);
        Assert.False(vm.State.CanInstall);
        Assert.True(vm.State.CanToggle);
        Assert.True(vm.State.CanUninstall);
        Assert.True(vm.State.HasSettings);
        Assert.Equal("# 远端 NPM README", vm.State.MarkdownDocument);
        Assert.Equal(["2.0.0", "1.0.0"], vm.State.AvailableVersions);

        // 2. 远端未安装包：展示安装入口，无本地配置区块
        await vm.LoadCatalogAsync("@thirdparty/not-installed");
        Assert.False(vm.State.IsInstalled);
        Assert.True(vm.State.CanInstall);
        Assert.False(vm.State.CanToggle);
        Assert.False(vm.State.CanUninstall);
        Assert.False(vm.State.HasSettings);
        Assert.Equal("@thirdparty/not-installed@1.0.0", vm.State.RecommendedInstallSpec);
    }

    [Fact]
    public async Task SaveResetAndConflictRefresh_FollowExpectedRevisionAndProtectSecrets()
    {
        var gw = new FakeDetailGateway();
        var ops = new FakeDetailOperations();
        var invService = new PluginInventoryService(gw, ops);
        await using var vm = new PluginDetailViewModel(gw, invService, ops);

        await vm.LoadInstalledAsync("include:demo-plugin");
        Assert.Equal(5, vm.State.SettingsRevision);

        // 1. 实时保存单个字段：携带 expectedRevision=5，成功后 revision 推进到 6 且提示 live 生效
        var saveOutcome = await vm.SaveFieldAsync(["timeout"], PluginSettingsSchemaParser.EncodeNumber(30));
        Assert.True(saveOutcome.IsOk);
        Assert.Equal(5, gw.LastMutateExpectedRevision);
        Assert.Equal(6, vm.State.SettingsRevision);
        Assert.Equal("plugin_detail.status.config_saved_live", vm.State.StatusKey);
        Assert.Equal("30", Assert.Single(vm.State.ConfigFields, f => f.KeyDisplay == "timeout").CurrentValueJson);

        // 2. 单字段重置
        var resetFieldOutcome = await vm.ResetFieldAsync(["timeout"]);
        Assert.True(resetFieldOutcome.IsOk);
        Assert.Equal(6, gw.LastMutateExpectedRevision);
        Assert.Equal(7, vm.State.SettingsRevision);
        Assert.Equal("plugin_detail.status.config_field_reset", vm.State.StatusKey);

        // 3. 存在密钥槽位时全量重置：严禁调用 ReplaceSettingsAsync，仅对非密钥已覆盖项发 Mutate(unset)
        var resetAllWithSecret = await vm.ResetAllFieldsAsync();
        Assert.True(resetAllWithSecret.IsOk);
        Assert.Equal(0, gw.ReplaceCallCount);
        Assert.NotNull(gw.LastMutateOps);
        Assert.All(gw.LastMutateOps!, op =>
        {
            Assert.Equal(SettingsPathOpKind.Unset, op.Op);
            Assert.False(op.Path.SequenceEqual(["apiToken"]));
        });
        Assert.Equal("plugin_detail.status.config_reset_preserving_secrets", vm.State.StatusKey);

        // 4. 遇 settings/conflict 自动重新拉取 describe 同步最新 revision（99）并提示重试
        gw.NextMutateConflict = true;
        gw.CurrentRevision = 99;
        var conflictOutcome = await vm.SaveFieldAsync(["enabled"], PluginSettingsSchemaParser.EncodeBoolean(false));
        Assert.False(conflictOutcome.IsOk);
        Assert.Equal(DshFailureCode.SettingsConflict, conflictOutcome.Error!.Code);
        Assert.Equal(99, vm.State.SettingsRevision);
        Assert.Equal("plugin_detail.status.config_conflict_refreshed", vm.State.StatusKey);

        // 5. 无密钥槽位时全量重置：走 ReplaceSettingsAsync({})
        gw.IncludeSecretSlot = false;
        await vm.LoadInstalledAsync("include:demo-plugin");
        var resetAllNoSecret = await vm.ResetAllFieldsAsync();
        Assert.True(resetAllNoSecret.IsOk);
        Assert.Equal(1, gw.ReplaceCallCount);
        Assert.Equal("plugin_detail.status.config_reset_all", vm.State.StatusKey);
    }

    [Fact]
    public async Task LifecycleActions_ToggleInstallUninstallRestart_UpdateDetailState()
    {
        var gw = new FakeDetailGateway();
        var ops = new FakeDetailOperations();
        var catalog = new FakeDetailCatalogSearch();
        var invService = new PluginInventoryService(gw, ops);
        await using var vm = new PluginDetailViewModel(gw, invService, ops, catalog);

        await vm.LoadInstalledAsync("include:demo-plugin");
        Assert.True(vm.State.Enabled);

        var toggleRes = await vm.SetEnabledAsync(false);
        Assert.True(toggleRes.IsOk);
        Assert.False(vm.State.Enabled);
        Assert.True(vm.State.RequiresDshRestart);

        var restartRes = await vm.RestartDshAsync();
        Assert.True(restartRes.IsOk);
        Assert.False(vm.State.RequiresDshRestart);
        Assert.Equal(1, ops.RestartCalls);

        await vm.LoadCatalogAsync("@thirdparty/not-installed");
        var installRes = await vm.InstallAsync();
        Assert.True(installRes.IsOk);
        Assert.Equal(1, ops.InstallCalls);
        Assert.True(vm.State.RequiresDshRestart);
    }

    private sealed class FakeDetailGateway : DefaultDshGateway
    {
        public PluginBundleDetailView BundleDetailResult { get; set; } = new(Found: false);
        public bool IncludeSecretSlot { get; set; } = true;
        public bool NextMutateConflict { get; set; }
        public long CurrentRevision { get; set; } = 5;
        public double CurrentTimeout { get; set; } = 15;
        public bool CurrentEnabled { get; set; } = true;
        public bool HasTimeoutOverride { get; set; } = true;
        public bool HasEnabledOverride { get; set; } = true;
        public long? LastMutateExpectedRevision { get; private set; }
        public IReadOnlyList<SettingsPathOp>? LastMutateOps { get; private set; }
        public int ReplaceCallCount { get; private set; }

        public override DshCapabilities Capabilities => new(
            Prompt: true,
            PromptImages: true,
            SlashCommands: true,
            StreamingEvents: true,
            Approvals: true,
            UserQuestions: true,
            ResumeKnownSession: true,
            ListSessions: true,
            HistoryReplay: true,
            CancelSession: true,
            SwitchModelInSession: true,
            ManageCredentials: true,
            ServerPromptIdempotency: true,
            PluginInventory: true,
            PluginMutation: false,
            GitOperations: false,
            PluginBundles: true,
            PluginSettings: true);

        public override Task<Outcome<PluginInventoryView>> GetPluginInventoryAsync(CancellationToken ct = default)
        {
            var entries = new PluginInventoryEntryView[]
            {
                new("include:demo-plugin", "@thirdparty/demo-plugin", true, "active", "Demo Plugin", null, "演示插件简介"),
            };
            return Task.FromResult(Outcome<PluginInventoryView>.Ok(new PluginInventoryView(false, entries)));
        }

        public override Task<Outcome<IReadOnlyList<PluginBundleInfoView>>> ListPluginBundlesAsync(CancellationToken ct = default)
        {
            var bundles = new PluginBundleInfoView[]
            {
                new(
                    Name: "@thirdparty/demo-plugin",
                    Official: false,
                    Availability: "profile",
                    Version: "1.0.0",
                    DisplayName: "Demo Plugin",
                    Description: "演示插件简介",
                    Enabled: true,
                    Installed: true,
                    Optional: false,
                    Removable: true,
                    Rows: [new PluginBundleRowView("demo-plugin", "@thirdparty/demo-plugin", "Demo Plugin", "演示插件简介", "include:demo-plugin")],
                    Overrides: []),
            };
            return Task.FromResult(Outcome<IReadOnlyList<PluginBundleInfoView>>.Ok(bundles));
        }

        public override Task<Outcome<PluginBundleDetailView>> ReadBundleDetailAsync(string bundleName, string language, CancellationToken ct = default) =>
            Task.FromResult(Outcome<PluginBundleDetailView>.Ok(BundleDetailResult));

        public override Task<Outcome<SettingsDescribeSnapshotView>> DescribeSettingsAsync(CancellationToken ct = default) =>
            Task.FromResult(Outcome<SettingsDescribeSnapshotView>.Ok(
                new SettingsDescribeSnapshotView(Writable: true, HasDocument: true, [BuildCurrentNsView()])));

        public override Task<Outcome<SettingsNamespaceView>> MutateSettingsAsync(
            string ns,
            IReadOnlyList<SettingsPathOp> ops,
            long? expectedRevision = null,
            CancellationToken ct = default)
        {
            LastMutateExpectedRevision = expectedRevision;
            LastMutateOps = ops;
            if (NextMutateConflict)
            {
                NextMutateConflict = false;
                return Task.FromResult(Outcome<SettingsNamespaceView>.Fail(DshFailureCode.SettingsConflict, "settings/conflict", retryable: true));
            }

            foreach (var op in ops)
            {
                if (op.Path.SequenceEqual(["timeout"]))
                {
                    if (op.Op == SettingsPathOpKind.Set && op.Value is { } v)
                    {
                        CurrentTimeout = v.GetDouble();
                        HasTimeoutOverride = true;
                    }
                    else
                    {
                        CurrentTimeout = 10;
                        HasTimeoutOverride = false;
                    }
                }
                else if (op.Path.SequenceEqual(["enabled"]))
                {
                    if (op.Op == SettingsPathOpKind.Set && op.Value is { } v)
                    {
                        CurrentEnabled = v.GetBoolean();
                        HasEnabledOverride = true;
                    }
                    else
                    {
                        CurrentEnabled = false;
                        HasEnabledOverride = false;
                    }
                }
            }

            CurrentRevision++;
            return Task.FromResult(Outcome<SettingsNamespaceView>.Ok(BuildCurrentNsView()));
        }

        public override Task<Outcome<SettingsNamespaceView>> ReplaceSettingsAsync(
            string ns,
            JsonElement section,
            long? expectedRevision = null,
            CancellationToken ct = default)
        {
            ReplaceCallCount++;
            CurrentTimeout = 10;
            CurrentEnabled = false;
            HasTimeoutOverride = false;
            HasEnabledOverride = false;
            CurrentRevision++;
            return Task.FromResult(Outcome<SettingsNamespaceView>.Ok(BuildCurrentNsView()));
        }

        private SettingsNamespaceView BuildCurrentNsView()
        {
            var secretSchema = IncludeSecretSlot
                ? ""","apiToken":{"type":"string","meta":{"role":"secret","description":"访问令牌"}}"""
                : string.Empty;
            var schemaJson = $$$"""{"type":"object","dict":{"enabled":{"type":"boolean"},"timeout":{"type":"number","meta":{"min":1,"max":120}}{{{secretSchema}}}}}""";
            var valueJson = $$$"""{"enabled":{{{(CurrentEnabled ? "true" : "false")}}},"timeout":{{{CurrentTimeout}}}}""";
            var userParts = new List<string>();
            if (HasEnabledOverride)
            {
                userParts.Add($"\"enabled\":{(CurrentEnabled ? "true" : "false")}");
            }
            if (HasTimeoutOverride)
            {
                userParts.Add($"\"timeout\":{CurrentTimeout}");
            }
            var userJson = "{" + string.Join(',', userParts) + "}";

            using var schemaDoc = JsonDocument.Parse(schemaJson);
            using var valDoc = JsonDocument.Parse(valueJson);
            using var userDoc = JsonDocument.Parse(userJson);

            IReadOnlyList<SettingsSecretSlotView> secrets = IncludeSecretSlot
                ? [new SettingsSecretSlotView(["apiToken"], IsSet: true)]
                : [];

            return new SettingsNamespaceView(
                Namespace: "demo-plugin",
                AutoGenerate: true,
                Schema: schemaDoc.RootElement.Clone(),
                Value: valDoc.RootElement.Clone(),
                Base: null,
                User: userDoc.RootElement.Clone(),
                Applies: "live",
                Secrets: secrets,
                Revision: CurrentRevision);
        }
    }

    private sealed class FakeDetailOperations : IPluginOperations
    {
        public string? LocalDetailMarkdown { get; set; }
        public int InstallCalls { get; private set; }
        public int RestartCalls { get; private set; }

        public Task<Outcome<IReadOnlyList<PluginInstalledPackageView>>> ListInstalledPackagesAsync(CancellationToken ct = default) =>
            Task.FromResult(Outcome<IReadOnlyList<PluginInstalledPackageView>>.Ok([
                new PluginInstalledPackageView("@thirdparty/demo-plugin", "1.0.0", HasCustomWebUi: false, HasBundleManifest: true, Description: "演示插件简介"),
            ]));

        public Task<Outcome<PluginLocalPackageDetailView>> ReadLocalPackageDetailAsync(string packageName, string language, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(LocalDetailMarkdown))
            {
                return Task.FromResult(Outcome<PluginLocalPackageDetailView>.Fail(DshFailureCode.Unsupported));
            }
            return Task.FromResult(Outcome<PluginLocalPackageDetailView>.Ok(new PluginLocalPackageDetailView(
                PackageName: packageName,
                Version: "1.0.0",
                Markdown: LocalDetailMarkdown,
                DocumentOrigin: "readme",
                DocumentPath: "README.md")));
        }

        public Task<Outcome<Unit>> InstallPackageAsync(string packageSpec, CancellationToken ct = default)
        {
            InstallCalls++;
            return Task.FromResult(Outcome<Unit>.Ok(Unit.Value));
        }

        public Task<Outcome<Unit>> UninstallPackageAsync(string packageName, CancellationToken ct = default) =>
            Task.FromResult(Outcome<Unit>.Ok(Unit.Value));

        public Task<Outcome<Unit>> SetPluginEnabledAsync(string entryId, string moduleName, bool enabled, CancellationToken ct = default) =>
            Task.FromResult(Outcome<Unit>.Ok(Unit.Value));

        public Task<Outcome<Unit>> RestartDshHostAsync(CancellationToken ct = default)
        {
            RestartCalls++;
            return Task.FromResult(Outcome<Unit>.Ok(Unit.Value));
        }
    }

    private sealed class FakeDetailCatalogSearch : IPluginCatalogSearch
    {
        public Task<Outcome<PluginCatalogSearchResult>> SearchAsync(string query, int limit = 25, CancellationToken ct = default) =>
            Task.FromResult(Outcome<PluginCatalogSearchResult>.Ok(new PluginCatalogSearchResult([], 0)));

        public Task<Outcome<PluginCatalogDetailView>> GetPackageDetailAsync(string packageName, CancellationToken ct = default)
        {
            if (packageName == "@thirdparty/demo-plugin")
            {
                return Task.FromResult(Outcome<PluginCatalogDetailView>.Ok(new PluginCatalogDetailView(
                    PackageName: "@thirdparty/demo-plugin",
                    LatestVersion: "2.0.0",
                    Description: "远端简介",
                    ReadmeMarkdown: "# 远端 NPM README",
                    Keywords: ["dsh", "demo"],
                    License: "MIT",
                    Author: "Bob",
                    HomepageUrl: "https://example.com/demo",
                    RepositoryUrl: "https://github.com/example/demo",
                    NpmUrl: "https://www.npmjs.com/package/@thirdparty/demo-plugin",
                    AvailableVersions: ["2.0.0", "1.0.0"],
                    IsOfficialScope: false,
                    HasBundleManifest: true,
                    HasCustomWebUi: false,
                    RecommendedInstallSpec: "@thirdparty/demo-plugin@2.0.0")));
            }

            return Task.FromResult(Outcome<PluginCatalogDetailView>.Ok(new PluginCatalogDetailView(
                PackageName: packageName,
                LatestVersion: "1.0.0",
                Description: "未安装远端插件",
                ReadmeMarkdown: "# 未安装插件文档",
                Keywords: ["dsh"],
                License: "Apache-2.0",
                Author: "Carol",
                HomepageUrl: null,
                RepositoryUrl: null,
                NpmUrl: $"https://www.npmjs.com/package/{packageName}",
                AvailableVersions: ["1.0.0"],
                IsOfficialScope: false,
                HasBundleManifest: true,
                HasCustomWebUi: true,
                RecommendedInstallSpec: $"{packageName}@1.0.0")));
        }
    }
}
