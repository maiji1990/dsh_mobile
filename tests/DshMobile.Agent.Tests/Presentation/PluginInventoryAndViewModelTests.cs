using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;
using DshMobile.Agent.Presentation;
using DshMobile.Agent.Tests.Quality;

namespace DshMobile.Agent.Tests.Presentation;

public sealed class PluginInventoryAndViewModelTests
{
    [Fact]
    public async Task LoadInventoryAsync_MergesPackages_DetectsCustomWebUi_AndRedactsSecrets()
    {
        var gw = new StubPluginGateway();
        var ops = new StubPluginOperations
        {
            InstalledPackages =
            [
                new("@thirdparty/my-ui-plugin", "1.2.3", HasCustomWebUi: true),
                new("pending-restart-plugin", "0.5.0", HasCustomWebUi: false),
            ],
        };
        var service = new PluginInventoryService(gw, ops);

        var outcome = await service.LoadInventoryAsync(includeCliPackages: true);
        Assert.True(outcome.IsOk);
        var entries = outcome.Value!.Entries;
        Assert.Equal(4, entries.Count);

        // 核心受保护插件禁止卸载与开关
        var core = Assert.Single(entries, e => e.EntryId == "connection");
        Assert.True(core.IsProtected);
        Assert.False(core.CanToggle);
        Assert.False(core.CanUninstall);

        // 错误诊断文本必须经过 SecretRedactor 脱敏
        var failed = Assert.Single(entries, e => e.EntryId == "include:broken-plugin");
        Assert.Equal("failed", failed.FiberPhase);
        Assert.NotNull(failed.DiagnosticSummary);
        Assert.DoesNotContain("sk-live-secret-999999", failed.DiagnosticSummary!, StringComparison.Ordinal);
        Assert.DoesNotContain("my-bearer-jwt-token", failed.DiagnosticSummary!, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", failed.DiagnosticSummary!, StringComparison.Ordinal);

        // 自定义 WebUI 插件如实标记 HasCustomWebUi
        var customUi = Assert.Single(entries, e => e.PackageName == "@thirdparty/my-ui-plugin");
        Assert.True(customUi.HasCustomWebUi);
        Assert.True(customUi.CanToggle);
        Assert.True(customUi.CanUninstall);
        Assert.Equal("1.2.3", customUi.Version);

        // 新装待重启生效包出现在清单中且可卸载
        var pending = Assert.Single(entries, e => e.PackageName == "pending-restart-plugin");
        Assert.Equal("pending-restart", pending.FiberPhase);
        Assert.True(pending.CanUninstall);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("foo; rm -rf /")]
    [InlineData("foo && whoami")]
    [InlineData("$(cat /etc/shadow)")]
    [InlineData("../escape")]
    [InlineData("--registry=https://evil.test")]
    [InlineData("pkg@^1.0.0")]
    [InlineData("pkg@~2.0.0")]
    [InlineData("pkg name")]
    [InlineData("pkg\n")]
    public async Task InstallAndUninstall_InvalidSpec_RejectsWithZeroProcessCalls(string badSpec)
    {
        var gw = new StubPluginGateway();
        var ops = new StubPluginOperations();
        var service = new PluginInventoryService(gw, ops);
        await using var vm = new PluginsViewModel(gw, service);

        var installRes = await vm.InstallPluginAsync(badSpec);
        Assert.False(installRes.IsOk);
        Assert.Equal(DshFailureCode.InvalidState, installRes.Error!.Code);

        var uninstallRes = await vm.UninstallPluginAsync(badSpec);
        Assert.False(uninstallRes.IsOk);
        Assert.Equal(DshFailureCode.InvalidState, uninstallRes.Error!.Code);

        Assert.Equal(0, ops.InstallCalls);
        Assert.Equal(0, ops.UninstallCalls);
        Assert.False(vm.State.RequiresDshRestart);
    }

    [Fact]
    public async Task FailedInstallOrUninstall_RecoversCleanly_RedactsError_AndPreservesExistingEntries()
    {
        var gw = new StubPluginGateway();
        var ops = new StubPluginOperations
        {
            NextInstallFailure = Outcome<Unit>.Fail(
                DshFailureCode.ProcessExited,
                "npm ERR! token=sk-secret-abc123 Authorization: Bearer leak-token-777"),
            NextUninstallFailure = Outcome<Unit>.Fail(
                DshFailureCode.ProcessExited,
                "{\"apiKey\":\"sk-json-leak-888\"}"),
        };
        var service = new PluginInventoryService(gw, ops);
        await using var vm = new PluginsViewModel(gw, service);

        await vm.RefreshAsync(includeCliPackages: false);
        Assert.Equal(3, vm.State.Entries.Count);

        var failInstall = await vm.InstallPluginAsync("demo-plugin@1.0.0");
        Assert.False(failInstall.IsOk);
        Assert.False(vm.State.IsMutating);
        Assert.False(vm.State.RequiresDshRestart);
        Assert.Equal(3, vm.State.Entries.Count); // 原列表保留，不清空不崩溃
        Assert.DoesNotContain("sk-secret-abc123", vm.State.DiagnosticSummary ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("leak-token-777", vm.State.DiagnosticSummary ?? string.Empty, StringComparison.Ordinal);

        var failUninstall = await vm.UninstallPluginAsync("demo-plugin");
        Assert.False(failUninstall.IsOk);
        Assert.False(vm.State.IsMutating);
        Assert.False(vm.State.RequiresDshRestart);
        Assert.DoesNotContain("sk-json-leak-888", vm.State.DiagnosticSummary ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MutationsTriggerRestartBanner_AndHostRestartClearsIt()
    {
        var gw = new StubPluginGateway();
        var ops = new StubPluginOperations();
        var service = new PluginInventoryService(gw, ops);
        await using var vm = new PluginsViewModel(gw, service);

        await vm.RefreshAsync(includeCliPackages: false);
        Assert.False(vm.State.RequiresDshRestart);

        // 安装成功后置位重启提示
        var installOk = await vm.InstallPluginAsync("@thirdparty/sample@1.0.0");
        Assert.True(installOk.IsOk);
        Assert.True(installOk.Value!.RequiresDshRestart);
        Assert.True(vm.State.RequiresDshRestart);

        // 点击「重启 DSH」后清除提示
        var restartOk = await vm.RestartDshAsync();
        Assert.True(restartOk.IsOk);
        Assert.Equal(1, ops.RestartCalls);
        Assert.False(vm.State.RequiresDshRestart);

        // 启停切换再次触发重启提示
        var toggleOk = await vm.SetPluginEnabledAsync("include:custom-ui", "@thirdparty/my-ui-plugin", false);
        Assert.True(toggleOk.IsOk);
        Assert.True(vm.State.RequiresDshRestart);
        Assert.False(vm.State.Entries.First(e => e.EntryId == "include:custom-ui").Enabled);

        // 宿主外部完成新一轮启动握手通知后，同样自动清除重启提示
        vm.NotifyHostStarted();
        Assert.False(vm.State.RequiresDshRestart);
    }

    [Fact]
    public void PluginsFeature_HasZeroWebViewAndUsesOfficialSettingsOnly()
    {
        var repoRoot = RepoLocator.FindRepoRoot();

        // ADR-0034：永久锁死零 WebView / Android.Webkit，并禁止引入外部/自创 JsonSchema / SchemaForm 表单库
        var pluginCsFiles = RepoLocator.EnumerateSourceFiles("src/DshMobile.Android/Plugins").ToList();
        Assert.NotEmpty(pluginCsFiles);
        foreach (var file in pluginCsFiles)
        {
            var codeOnly = StripCSharpSingleLineComments(File.ReadAllText(file));
            Assert.DoesNotContain("WebView", codeOnly, StringComparison.Ordinal);
            Assert.DoesNotContain("Android.Webkit", codeOnly, StringComparison.Ordinal);
            Assert.DoesNotContain("JsonSchema", codeOnly, StringComparison.Ordinal);
            Assert.DoesNotContain("SchemaForm", codeOnly, StringComparison.Ordinal);
        }

        var layoutDir = Path.Combine(repoRoot, "src/DshMobile.Android/Resources/layout");
        var pluginLayouts = Directory.EnumerateFiles(layoutDir, "*plugin*.xml").ToList();
        Assert.NotEmpty(pluginLayouts);
        foreach (var xml in pluginLayouts)
        {
            var xmlNoComments = System.Text.RegularExpressions.Regex.Replace(
                File.ReadAllText(xml),
                @"<!--[\s\S]*?-->",
                string.Empty);
            Assert.DoesNotContain("WebView", xmlNoComments, StringComparison.OrdinalIgnoreCase);
        }

        // 检查 Agent 插件全链路源码零 WebView、零外部 JsonSchema/SchemaForm 依赖（仅使用官方 settings/* 纯托管模型）
        var agentPluginFiles = new[]
        {
            Path.Combine(repoRoot, "src/DshMobile.Agent/Domain/PluginModels.cs"),
            Path.Combine(repoRoot, "src/DshMobile.Agent/Engines/PluginInventoryService.cs"),
            Path.Combine(repoRoot, "src/DshMobile.Agent/Engines/NpmRegistryPluginSearchService.cs"),
            Path.Combine(repoRoot, "src/DshMobile.Agent/Engines/PluginSettingsSchemaParser.cs"),
            Path.Combine(repoRoot, "src/DshMobile.Agent/Presentation/PluginsViewModel.cs"),
            Path.Combine(repoRoot, "src/DshMobile.Agent/Presentation/PluginDetailViewModel.cs"),
        };
        foreach (var file in agentPluginFiles)
        {
            var codeOnly = StripCSharpSingleLineComments(File.ReadAllText(file));
            Assert.DoesNotContain("WebView", codeOnly, StringComparison.Ordinal);
            Assert.DoesNotContain("Android.Webkit", codeOnly, StringComparison.Ordinal);
            Assert.DoesNotContain("JsonSchema", codeOnly, StringComparison.Ordinal);
            Assert.DoesNotContain("SchemaForm", codeOnly, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task LoadInventoryAsync_AnnotatesConfigurableSettings_TrimmedRows_AndNonBundlePackages()
    {
        var gw = new ConfigurablePluginGateway();
        var ops = new TrimmedAwarePluginOperations
        {
            InstalledPackages =
            [
                new("@thirdparty/my-ui-plugin", "1.2.3", HasCustomWebUi: true, HasBundleManifest: true, Description: "UI 面板扩展"),
                new("plain-npm-lib", "0.1.0", HasCustomWebUi: false, HasBundleManifest: false, Description: "普通库"),
            ],
            TrimmedIds = new HashSet<string>(StringComparer.Ordinal) { "custom-ui" },
            FullWebUiCompat = false,
        };
        var service = new PluginInventoryService(gw, ops);
        await using var vm = new PluginsViewModel(gw, service);

        await vm.RefreshAsync(includeCliPackages: true);
        Assert.False(vm.State.IsFullWebUiCompatEnabled);

        // 精简模式下被裁剪的条目：标记 IsTrimmedByNativeMode=true 且禁止拨动
        var trimmed = Assert.Single(vm.State.Entries, e => e.EntryId == "include:custom-ui");
        Assert.True(trimmed.IsTrimmedByNativeMode);
        Assert.False(trimmed.CanToggle);
        Assert.True(trimmed.HasConfigurableSettings);
        Assert.Equal("custom-ui", trimmed.SettingsNamespace);

        // 非 Bundle 普通 npm 包明确标记 HasBundleManifest=false 与 FiberPhase="not-bundle"
        var nonBundle = Assert.Single(vm.State.Entries, e => e.PackageName == "plain-npm-lib");
        Assert.False(nonBundle.HasBundleManifest);
        Assert.Equal("not-bundle", nonBundle.FiberPhase);

        // 搜索支持命中拆分出的 Description 字段
        vm.SetSearchQuery("面板扩展");
        Assert.Equal("@thirdparty/my-ui-plugin", Assert.Single(vm.State.Entries).PackageName);

        // 开启完整 WebUI 兼容模式后，不再标记为被精简裁剪且允许切换
        ops.FullWebUiCompat = true;
        vm.SetSearchQuery(string.Empty);
        await vm.RefreshAsync(includeCliPackages: true);
        Assert.True(vm.State.IsFullWebUiCompatEnabled);
        var restored = Assert.Single(vm.State.Entries, e => e.EntryId == "include:custom-ui");
        Assert.False(restored.IsTrimmedByNativeMode);
        Assert.True(restored.CanToggle);
    }

    [Fact]
    public async Task LocalFilterModes_AndTotalInstalledCount_WorkAccurately()
    {
        var gw = new StubPluginGateway();
        var ops = new StubPluginOperations
        {
            InstalledPackages =
            [
                new("@thirdparty/my-ui-plugin", "1.2.3", HasCustomWebUi: true),
                new("pending-restart-plugin", "0.5.0", HasCustomWebUi: false),
            ],
        };
        var service = new PluginInventoryService(gw, ops);
        await using var vm = new PluginsViewModel(gw, service);

        await vm.RefreshAsync(includeCliPackages: true);
        Assert.Equal(4, vm.State.TotalInstalledCount);
        Assert.Equal(4, vm.State.Entries.Count);

        vm.SetFilterMode(PluginFilterMode.Manageable);
        Assert.Equal(4, vm.State.TotalInstalledCount);
        Assert.Equal(3, vm.State.Entries.Count);
        Assert.DoesNotContain(vm.State.Entries, e => e.IsProtected);

        vm.SetFilterMode(PluginFilterMode.Enabled);
        Assert.Equal(3, vm.State.Entries.Count);
        Assert.All(vm.State.Entries, e => Assert.True(e.Enabled));

        vm.SetFilterMode(PluginFilterMode.Protected);
        var protectedOnly = Assert.Single(vm.State.Entries);
        Assert.Equal("connection", protectedOnly.EntryId);

        vm.SetFilterMode(PluginFilterMode.Issues);
        Assert.Equal(2, vm.State.Entries.Count);
        Assert.Contains(vm.State.Entries, e => e.EntryId == "include:broken-plugin");
        Assert.Contains(vm.State.Entries, e => e.PackageName == "pending-restart-plugin");

        // 搜索无结果时 TotalInstalledCount 仍保留未过滤总数，防止 UI 误判为空列表重复刷新
        vm.SetFilterMode(PluginFilterMode.All);
        vm.SetSearchQuery("definitely-not-matching-anything");
        Assert.Empty(vm.State.Entries);
        Assert.Equal(4, vm.State.TotalInstalledCount);
    }

    [Fact]
    public async Task LocalMultiTokenSearch_MatchesAcrossFields_AndRanksByRelevanceWithStableTieBreak()
    {
        var gw = new RichSearchPluginGateway();
        var ops = new StubPluginOperations
        {
            InstalledPackages =
            [
                new("@community/extra-notes", "2.4.1"),
                new("@deepseek-ai/dsh-timer", "1.0.0"),
                new("@thirdparty/timer-runner", "3.1.0"),
                new("@deepseek-ai/dsh-hmr", "1.2.0"),
            ],
        };
        var service = new PluginInventoryService(gw, ops);
        await using var vm = new PluginsViewModel(gw, service);

        await vm.RefreshAsync(includeCliPackages: true);

        // 1. 多关键词空格分词 AND 匹配（跨短标题与中文描述）
        vm.SetSearchQuery("timer 延时");
        Assert.Equal(2, vm.State.Entries.Count);
        // 短标题前缀命中 (timer-runner) > 短标题包含命中 (dsh-timer)
        Assert.Equal("@thirdparty/timer-runner", vm.State.Entries[0].PackageName);
        Assert.Equal("@deepseek-ai/dsh-timer", vm.State.Entries[1].PackageName);

        // 2. 多关键词匹配范围包 + 短标题
        vm.SetSearchQuery("dsh hmr");
        var hmr = Assert.Single(vm.State.Entries);
        Assert.Equal("@deepseek-ai/dsh-hmr", hmr.PackageName);

        // 3. 相关性排序：短标题精确/前缀 > 包名/模块名 > 描述/诊断
        vm.SetSearchQuery("timer");
        Assert.Equal(3, vm.State.Entries.Count);
        Assert.Equal("@thirdparty/timer-runner", vm.State.Entries[0].PackageName); // 短标题前缀 timer-runner (90)
        Assert.Equal("@deepseek-ai/dsh-timer", vm.State.Entries[1].PackageName);   // 短标题包含 dsh-timer (70)
        Assert.Equal("@community/extra-notes", vm.State.Entries[2].PackageName);   // 描述/诊断命中 timer (25)

        // 4. 版本号与诊断阶段字段也可检索
        vm.SetSearchQuery("2.4.1");
        Assert.Equal("@community/extra-notes", Assert.Single(vm.State.Entries).PackageName);

        vm.SetSearchQuery("degraded");
        Assert.Equal("@community/extra-notes", Assert.Single(vm.State.Entries).PackageName);
    }

    [Fact]
    public async Task CatalogSearch_AlignsInstalledState_UpdatesOnInstallAndUninstall_AndSupportsDefaultDiscovery()
    {
        var gw = new StubPluginGateway();
        var ops = new StubPluginOperations
        {
            InstalledPackages =
            [
                new("@thirdparty/my-ui-plugin", "1.2.3", HasCustomWebUi: true),
            ],
        };
        var catalogSearch = new StubPluginCatalogSearch((query, _, _) =>
        {
            IReadOnlyList<PluginCatalogEntryView> items =
            [
                new(
                    PackageName: "@thirdparty/my-ui-plugin",
                    Version: "1.5.0",
                    Description: "UI 扩展",
                    Keywords: ["ui"],
                    Publisher: "thirdparty",
                    PublishedAtUnixMs: 1_700_000_000_000,
                    NpmUrl: "https://www.npmjs.com/package/@thirdparty/my-ui-plugin",
                    IsOfficialScope: false,
                    IsInstalled: false,
                    InstalledVersion: null,
                    RecommendedInstallSpec: "@thirdparty/my-ui-plugin@1.5.0"),
                new(
                    PackageName: "@deepseek-ai/dsh-timer",
                    Version: "0.9.0",
                    Description: "官方定时器插件",
                    Keywords: ["dsh", "timer"],
                    Publisher: "deepseek",
                    PublishedAtUnixMs: 1_700_000_100_000,
                    NpmUrl: "https://www.npmjs.com/package/@deepseek-ai/dsh-timer",
                    IsOfficialScope: true,
                    IsInstalled: false,
                    InstalledVersion: null,
                    RecommendedInstallSpec: "@deepseek-ai/dsh-timer@0.9.0"),
            ];
            return Task.FromResult(Outcome<PluginCatalogSearchResult>.Ok(new PluginCatalogSearchResult(items, TotalCount: 2)));
        });

        var service = new PluginInventoryService(gw, ops);
        await using var vm = new PluginsViewModel(gw, service, catalogSearch);

        Assert.True(vm.State.CanSearchCatalog);
        vm.SetActiveTab(PluginTabMode.Discover);
        Assert.Equal(PluginTabMode.Discover, vm.State.ActiveTab);

        await vm.RefreshAsync(includeCliPackages: true);

        // 空关键词自动使用默认推荐发现词
        await vm.SearchCatalogAsync(string.Empty);
        Assert.Equal(PluginsViewModel.DefaultCatalogDiscoveryQuery, catalogSearch.LastQuery);
        Assert.Equal(2, vm.State.CatalogEntries.Count);
        Assert.Equal(2, vm.State.CatalogTotalCount);
        Assert.False(vm.State.IsSearchingCatalog);

        // 首次对齐：my-ui-plugin 已装 v1.2.3，dsh-timer 未装
        var installedItem = Assert.Single(vm.State.CatalogEntries, e => e.PackageName == "@thirdparty/my-ui-plugin");
        Assert.True(installedItem.IsInstalled);
        Assert.Equal("1.2.3", installedItem.InstalledVersion);

        var notInstalledItem = Assert.Single(vm.State.CatalogEntries, e => e.PackageName == "@deepseek-ai/dsh-timer");
        Assert.False(notInstalledItem.IsInstalled);
        Assert.Null(notInstalledItem.InstalledVersion);

        // 安装 dsh-timer 后，CatalogEntries 自动同步变为已装
        ops.InstalledPackages =
        [
            new("@thirdparty/my-ui-plugin", "1.2.3", HasCustomWebUi: true),
            new("@deepseek-ai/dsh-timer", "0.9.0"),
        ];
        var installOutcome = await vm.InstallPluginAsync("@deepseek-ai/dsh-timer@0.9.0");
        Assert.True(installOutcome.IsOk);

        var syncedInstalled = Assert.Single(vm.State.CatalogEntries, e => e.PackageName == "@deepseek-ai/dsh-timer");
        Assert.True(syncedInstalled.IsInstalled);
        Assert.Equal("0.9.0", syncedInstalled.InstalledVersion);

        // 卸载 dsh-timer 后，CatalogEntries 自动同步恢复未装
        ops.InstalledPackages =
        [
            new("@thirdparty/my-ui-plugin", "1.2.3", HasCustomWebUi: true),
        ];
        var uninstallOutcome = await vm.UninstallPluginAsync("@deepseek-ai/dsh-timer");
        Assert.True(uninstallOutcome.IsOk);

        var syncedUninstalled = Assert.Single(vm.State.CatalogEntries, e => e.PackageName == "@deepseek-ai/dsh-timer");
        Assert.False(syncedUninstalled.IsInstalled);
        Assert.Null(syncedUninstalled.InstalledVersion);
    }

    [Fact]
    public async Task CatalogSearch_CancelsPreviousInFlightRequest_AndRedactsFailures()
    {
        var gw = new StubPluginGateway();
        var ops = new StubPluginOperations();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var catalogSearch = new StubPluginCatalogSearch(async (query, _, ct) =>
        {
            if (query == "first")
            {
                firstStarted.TrySetResult();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                }
                catch (OperationCanceledException)
                {
                    firstCancelled.TrySetResult();
                    throw;
                }
            }

            if (query == "fail-secret")
            {
                return Outcome<PluginCatalogSearchResult>.Fail(
                    DshFailureCode.TransportLost,
                    "registry failed with Bearer secret-token-999");
            }

            return Outcome<PluginCatalogSearchResult>.Ok(new PluginCatalogSearchResult(
                Items:
                [
                    new(
                        PackageName: "second-plugin",
                        Version: "2.0.0",
                        Description: "第二次搜索命中",
                        Keywords: [],
                        Publisher: "bob",
                        PublishedAtUnixMs: null,
                        NpmUrl: null,
                        IsOfficialScope: false,
                        IsInstalled: false,
                        InstalledVersion: null,
                        RecommendedInstallSpec: "second-plugin@2.0.0"),
                ],
                TotalCount: 1));
        });

        var service = new PluginInventoryService(gw, ops);
        await using var vm = new PluginsViewModel(gw, service, catalogSearch);

        var firstTask = vm.SearchCatalogAsync("first");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // 发起新搜索应自动取消第一次在途搜索，且最终状态仅反映第二次结果
        await vm.SearchCatalogAsync("second");
        await firstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await firstTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(vm.State.IsSearchingCatalog);
        Assert.Equal("second", vm.State.CatalogQuery);
        Assert.Equal("second-plugin", Assert.Single(vm.State.CatalogEntries).PackageName);

        // 搜索失败时脱敏诊断文本且保留既有结果
        await vm.SearchCatalogAsync("fail-secret");
        Assert.False(vm.State.IsSearchingCatalog);
        Assert.Equal(DshFailureCode.TransportLost, vm.State.CatalogFailureCode);
        Assert.DoesNotContain("secret-token-999", vm.State.CatalogDiagnosticSummary ?? string.Empty, StringComparison.Ordinal);
        Assert.Single(vm.State.CatalogEntries);

        // 默认 Noop 构造下 CanSearchCatalog 为 false，且返回 Unsupported
        await using var noopVm = new PluginsViewModel(gw, service);
        Assert.False(noopVm.State.CanSearchCatalog);
        await noopVm.SearchCatalogAsync("anything");
        Assert.Equal(DshFailureCode.Unsupported, noopVm.State.CatalogFailureCode);
    }

    [Fact]
    public async Task CatalogSearch_CallerCancellationPropagatesOperationCanceledException()
    {
        var gw = new StubPluginGateway();
        var ops = new StubPluginOperations();
        var catalogSearch = new StubPluginCatalogSearch(async (_, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return Outcome<PluginCatalogSearchResult>.Ok(new PluginCatalogSearchResult([], 0));
        });

        var service = new PluginInventoryService(gw, ops);
        await using var vm = new PluginsViewModel(gw, service, catalogSearch);

        using var callerCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vm.SearchCatalogAsync("slow", callerCts.Token));
        Assert.False(vm.State.IsSearchingCatalog);
    }

    private static string StripCSharpSingleLineComments(string source) =>
        string.Join('\n', source.Split('\n').Select(line =>
        {
            var idx = line.IndexOf("//", StringComparison.Ordinal);
            return idx >= 0 ? line[..idx] : line;
        }));

    [Fact]
    public async Task CheckUpdatesAsync_MarksUpdatableAndFiltersByUpdatableMode()
    {
        var gateway = new StubPluginGateway
        {
            Bundles =
            [
                CreateBundle("@thirdparty/my-ui-plugin", "profile", removable: true),
                CreateBundle("@deepseek-ai/dsh-client-connection", "installation", removable: false),
            ],
        };
        var operations = new StubPluginOperations
        {
            InstalledPackages = [new PluginInstalledPackageView("@thirdparty/my-ui-plugin", "1.0.0")],
        };
        var catalogSearch = new UpdateCatalogSearch();
        catalogSearch.LatestVersions["@thirdparty/my-ui-plugin"] = "2.0.0";
        var service = new PluginInventoryService(gateway, operations, catalogSearch);
        await using var viewModel = new PluginsViewModel(gateway, service, catalogSearch);

        await viewModel.RefreshAsync();
        await viewModel.CheckUpdatesAsync();

        Assert.False(viewModel.State.IsCheckingUpdates);
        Assert.Equal(1, viewModel.State.UpdatableCount);
        Assert.NotNull(viewModel.State.LastUpdateCheckAtUnixMs);
        var updatable = Assert.Single(viewModel.State.Entries, entry => entry.PackageName == "@thirdparty/my-ui-plugin");
        Assert.Equal("2.0.0", updatable.LatestVersion);
        Assert.True(updatable.HasUpdate);
        Assert.True(updatable.CanUpgrade);

        viewModel.SetFilterMode(PluginFilterMode.Updatable);

        var filtered = Assert.Single(viewModel.State.Entries);
        Assert.Equal("@thirdparty/my-ui-plugin", filtered.PackageName);
        Assert.Equal(PluginFilterMode.Updatable, viewModel.State.FilterMode);
    }

    [Fact]
    public async Task CheckUpdatesAsync_FailureKeepsExistingEntries_AndRecordsRedactedDiagnostic()
    {
        var gateway = new StubPluginGateway
        {
            Bundles = [CreateBundle("@thirdparty/my-ui-plugin", "profile", removable: true)],
        };
        var operations = new StubPluginOperations
        {
            InstalledPackages = [new PluginInstalledPackageView("@thirdparty/my-ui-plugin", "1.0.0")],
        };
        var catalogSearch = new UpdateCatalogSearch
        {
            LatestVersionsOutcome = Outcome<IReadOnlyList<PluginLatestVersionView>>.Fail(
                DshFailureCode.TransportLost,
                "plugins.update_check_failed:sk-live-secret-999999"),
        };
        var service = new PluginInventoryService(gateway, operations, catalogSearch);
        await using var viewModel = new PluginsViewModel(gateway, service, catalogSearch);
        await viewModel.RefreshAsync();
        var entryCountBeforeCheck = viewModel.State.Entries.Count;

        await viewModel.CheckUpdatesAsync();

        Assert.Equal(entryCountBeforeCheck, viewModel.State.Entries.Count);
        Assert.Equal(DshFailureCode.TransportLost.ToString(), viewModel.State.UpdateCheckFailureCode);
        Assert.DoesNotContain("sk-live-secret-999999", viewModel.State.UpdateCheckDiagnostic ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchCatalogAsync_AlignsHasUpdate_FromInstalledVersionAndRegistryVersion()
    {
        var gateway = new StubPluginGateway
        {
            Bundles = [CreateBundle("@thirdparty/my-ui-plugin", "profile", removable: true)],
        };
        var operations = new StubPluginOperations
        {
            InstalledPackages = [new PluginInstalledPackageView("@thirdparty/my-ui-plugin", "1.0.0")],
        };
        var catalogSearch = new UpdateCatalogSearch
        {
            SearchResult = new PluginCatalogSearchResult(
                [
                    new PluginCatalogEntryView(
                        PackageName: "@thirdparty/my-ui-plugin",
                        Version: "2.0.0",
                        Description: "演示插件",
                        Keywords: ["dsh"],
                        Publisher: "tester",
                        PublishedAtUnixMs: null,
                        NpmUrl: "https://www.npmjs.com/package/@thirdparty/my-ui-plugin",
                        IsOfficialScope: false,
                        IsInstalled: false,
                        InstalledVersion: null,
                        RecommendedInstallSpec: "@thirdparty/my-ui-plugin@2.0.0"),
                ],
                1),
        };
        var service = new PluginInventoryService(gateway, operations, catalogSearch);
        await using var viewModel = new PluginsViewModel(gateway, service, catalogSearch);
        await viewModel.RefreshAsync();

        await viewModel.SearchCatalogAsync("demo");

        var catalogEntry = Assert.Single(viewModel.State.CatalogEntries);
        Assert.True(catalogEntry.IsInstalled);
        Assert.Equal("1.0.0", catalogEntry.InstalledVersion);
        Assert.True(catalogEntry.HasUpdate);
    }

    [Fact]
    public async Task UpgradePluginAsync_ValidEntry_UpgradesAndKeepsRestartBanner()
    {
        var gateway = new StubPluginGateway
        {
            Bundles = [CreateBundle("@thirdparty/my-ui-plugin", "profile", removable: true)],
        };
        var operations = new StubPluginOperations
        {
            InstalledPackages = [new PluginInstalledPackageView("@thirdparty/my-ui-plugin", "1.0.0")],
        };
        var catalogSearch = new UpdateCatalogSearch();
        catalogSearch.LatestVersions["@thirdparty/my-ui-plugin"] = "2.0.0";
        var service = new PluginInventoryService(gateway, operations, catalogSearch);
        await using var viewModel = new PluginsViewModel(gateway, service, catalogSearch);
        await viewModel.RefreshAsync();
        await viewModel.CheckUpdatesAsync();
        var entry = Assert.Single(viewModel.State.Entries, item => item.CanUpgrade);

        var result = await viewModel.UpgradePluginAsync(entry.PackageName, entry.LatestVersion!);

        Assert.True(result.IsOk);
        Assert.Equal(1, operations.UpgradeCalls);
        Assert.Equal("@thirdparty/my-ui-plugin", operations.LastUpgradePackageName);
        Assert.Equal("2.0.0", operations.LastUpgradeTargetVersion);
        Assert.True(viewModel.State.RequiresDshRestart);
        Assert.Equal("plugins.status.upgraded", viewModel.State.StatusKey);
        Assert.False(viewModel.State.IsMutating);
    }

    [Fact]
    public async Task UpgradePluginAsync_InstallationShippedEntry_RejectedWithZeroUpgradeCalls()
    {
        var gateway = new StubPluginGateway
        {
            Bundles = [CreateBundle("@deepseek-ai/dsh-client-connection", "installation", removable: false)],
        };
        var operations = new StubPluginOperations();
        var catalogSearch = new UpdateCatalogSearch();
        var service = new PluginInventoryService(gateway, operations, catalogSearch);
        await using var viewModel = new PluginsViewModel(gateway, service, catalogSearch);
        await viewModel.RefreshAsync();

        var result = await viewModel.UpgradePluginAsync("@deepseek-ai/dsh-client-connection", "2.0.0");

        Assert.False(result.IsOk);
        Assert.Equal(DshFailureCode.InvalidState, result.Error!.Code);
        Assert.Equal(0, operations.UpgradeCalls);
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

    private sealed class UpdateCatalogSearch : IPluginCatalogSearch
    {
        public Dictionary<string, string?> LatestVersions { get; } = new(StringComparer.Ordinal);
        public Outcome<IReadOnlyList<PluginLatestVersionView>>? LatestVersionsOutcome { get; init; }
        public PluginCatalogSearchResult SearchResult { get; init; } = new([], 0);

        public Task<Outcome<PluginCatalogSearchResult>> SearchAsync(
            string query,
            int limit = 25,
            CancellationToken ct = default) =>
            Task.FromResult(Outcome<PluginCatalogSearchResult>.Ok(SearchResult));

        public Task<Outcome<PluginCatalogDetailView>> GetPackageDetailAsync(
            string packageName,
            CancellationToken ct = default) =>
            Task.FromResult(Outcome<PluginCatalogDetailView>.Fail(DshFailureCode.Unsupported));

        public Task<Outcome<IReadOnlyList<PluginLatestVersionView>>> GetLatestVersionsAsync(
            IReadOnlyList<string> packageNames,
            CancellationToken ct = default)
        {
            if (LatestVersionsOutcome is { } predefined)
            {
                return Task.FromResult(predefined);
            }
            IReadOnlyList<PluginLatestVersionView> results = packageNames
                .Select(packageName => new PluginLatestVersionView(
                    packageName,
                    LatestVersions.GetValueOrDefault(packageName)))
                .ToArray();
            return Task.FromResult(Outcome<IReadOnlyList<PluginLatestVersionView>>.Ok(results));
        }
    }

    private sealed class StubPluginGateway : DefaultDshGateway
    {
        public IReadOnlyList<PluginBundleInfoView> Bundles { get; init; } = [];

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
            GitOperations: false);

        public override Task<Outcome<DshCapabilities>> ProbeCapabilitiesAsync(CancellationToken ct = default) =>
            Task.FromResult(Outcome<DshCapabilities>.Ok(Capabilities));

        public override Task<Outcome<IReadOnlyList<PluginBundleInfoView>>> ListPluginBundlesAsync(CancellationToken ct = default) =>
            Task.FromResult(Outcome<IReadOnlyList<PluginBundleInfoView>>.Ok(Bundles));

        public override Task<Outcome<PluginInventoryView>> GetPluginInventoryAsync(CancellationToken ct = default)
        {
            var entries = new PluginInventoryEntryView[]
            {
                new("connection", "@deepseek-ai/dsh-client-connection", true, "active", "Connection"),
                new("include:custom-ui", "@thirdparty/my-ui-plugin", true, "active", "My UI Plugin"),
                new("include:broken-plugin", "broken-plugin", true, "failed", "Broken Plugin",
                    "Boot failed with Bearer my-bearer-jwt-token and key sk-live-secret-999999"),
            };
            return Task.FromResult(Outcome<PluginInventoryView>.Ok(new PluginInventoryView(false, entries)));
        }
    }

    private sealed class StubPluginOperations : IPluginOperations
    {
        public IReadOnlyList<PluginInstalledPackageView> InstalledPackages { get; set; } = [];
        public Outcome<Unit>? NextInstallFailure { get; set; }
        public Outcome<Unit>? NextUninstallFailure { get; set; }
        public int InstallCalls { get; private set; }
        public int UninstallCalls { get; private set; }
        public int ToggleCalls { get; private set; }
        public int RestartCalls { get; private set; }
        public int UpgradeCalls { get; private set; }
        public string? LastUpgradePackageName { get; private set; }
        public string? LastUpgradeTargetVersion { get; private set; }

        public Task<Outcome<IReadOnlyList<PluginInstalledPackageView>>> ListInstalledPackagesAsync(CancellationToken ct = default) =>
            Task.FromResult(Outcome<IReadOnlyList<PluginInstalledPackageView>>.Ok(InstalledPackages));

        public Task<Outcome<Unit>> InstallPackageAsync(string packageSpec, CancellationToken ct = default)
        {
            InstallCalls++;
            return Task.FromResult(NextInstallFailure ?? Outcome<Unit>.Ok(Unit.Value));
        }

        public Task<Outcome<Unit>> UninstallPackageAsync(string packageName, CancellationToken ct = default)
        {
            UninstallCalls++;
            return Task.FromResult(NextUninstallFailure ?? Outcome<Unit>.Ok(Unit.Value));
        }

        public Task<Outcome<Unit>> SetPluginEnabledAsync(string entryId, string moduleName, bool enabled, CancellationToken ct = default)
        {
            ToggleCalls++;
            return Task.FromResult(Outcome<Unit>.Ok(Unit.Value));
        }

        public Task<Outcome<Unit>> RestartDshHostAsync(CancellationToken ct = default)
        {
            RestartCalls++;
            return Task.FromResult(Outcome<Unit>.Ok(Unit.Value));
        }

        public Task<Outcome<Unit>> UpgradePackageAsync(string packageName, string targetVersion, CancellationToken ct = default)
        {
            UpgradeCalls++;
            LastUpgradePackageName = packageName;
            LastUpgradeTargetVersion = targetVersion;
            return Task.FromResult(Outcome<Unit>.Ok(Unit.Value));
        }
    }

    private sealed class RichSearchPluginGateway : DefaultDshGateway
    {
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
            GitOperations: false);

        public override Task<Outcome<DshCapabilities>> ProbeCapabilitiesAsync(CancellationToken ct = default) =>
            Task.FromResult(Outcome<DshCapabilities>.Ok(Capabilities));

        public override Task<Outcome<PluginInventoryView>> GetPluginInventoryAsync(CancellationToken ct = default)
        {
            var entries = new PluginInventoryEntryView[]
            {
                new("include:notes", "@community/extra-notes", true, "degraded", "附带 timer 提醒的笔记插件", "timer sync lag"),
                new("include:dsh-timer", "@deepseek-ai/dsh-timer", true, "active", "官方延时触发服务"),
                new("include:timer-runner", "@thirdparty/timer-runner", true, "active", "支持延时调度"),
                new("include:hmr", "@deepseek-ai/dsh-hmr", true, "active", "热重载服务"),
            };
            return Task.FromResult(Outcome<PluginInventoryView>.Ok(new PluginInventoryView(false, entries)));
        }
    }

    private sealed class StubPluginCatalogSearch(
        Func<string, int, CancellationToken, Task<Outcome<PluginCatalogSearchResult>>> handler)
        : IPluginCatalogSearch
    {
        public string? LastQuery { get; private set; }

        public Task<Outcome<PluginCatalogSearchResult>> SearchAsync(string query, int limit = 25, CancellationToken ct = default)
        {
            LastQuery = query;
            return handler(query, limit, ct);
        }
    }

    private sealed class ConfigurablePluginGateway : DefaultDshGateway
    {
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
            PluginSettings: true);

        public override Task<Outcome<DshCapabilities>> ProbeCapabilitiesAsync(CancellationToken ct = default) =>
            Task.FromResult(Outcome<DshCapabilities>.Ok(Capabilities));

        public override Task<Outcome<PluginInventoryView>> GetPluginInventoryAsync(CancellationToken ct = default)
        {
            var entries = new PluginInventoryEntryView[]
            {
                new("include:custom-ui", "@thirdparty/my-ui-plugin", false, null, "My UI Plugin", null, "UI 面板扩展"),
            };
            return Task.FromResult(Outcome<PluginInventoryView>.Ok(new PluginInventoryView(false, entries)));
        }

        public override Task<Outcome<SettingsDescribeSnapshotView>> DescribeSettingsAsync(CancellationToken ct = default)
        {
            using var schemaDoc = System.Text.Json.JsonDocument.Parse("""{"type":"object","dict":{"enabled":{"type":"boolean"}}}""");
            using var valDoc = System.Text.Json.JsonDocument.Parse("""{"enabled":true}""");
            var ns = new SettingsNamespaceView(
                Namespace: "custom-ui",
                AutoGenerate: true,
                Schema: schemaDoc.RootElement.Clone(),
                Value: valDoc.RootElement.Clone(),
                Base: null,
                User: null,
                Applies: "live",
                Secrets: [],
                Revision: 1);
            return Task.FromResult(Outcome<SettingsDescribeSnapshotView>.Ok(
                new SettingsDescribeSnapshotView(Writable: true, HasDocument: true, [ns])));
        }
    }

    private sealed class TrimmedAwarePluginOperations : IPluginOperations
    {
        public IReadOnlyList<PluginInstalledPackageView> InstalledPackages { get; set; } = [];
        public IReadOnlySet<string> TrimmedIds { get; set; } = new HashSet<string>(StringComparer.Ordinal);
        public bool FullWebUiCompat { get; set; }

        public bool IsFullWebUiCompatEnabled => FullWebUiCompat;
        public IReadOnlySet<string> GetNativeTrimmedEntryIds() => TrimmedIds;

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
    }
}
