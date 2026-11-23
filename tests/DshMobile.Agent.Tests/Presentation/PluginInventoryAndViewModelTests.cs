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
    public void PluginsFeature_HasZeroL2DynamicSchemaFormAndZeroWebView()
    {
        var repoRoot = RepoLocator.FindRepoRoot();

        // 检查 Android Plugins 目录及布局 XML 中零 WebView、零 L2 动态 Schema 表单解析（剥离注释行以防注释中的否定表述误报）
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

        // 检查 PluginsViewModel / PluginInventoryService 无 L2 动态表单解析逻辑
        var agentPluginFiles = new[]
        {
            Path.Combine(repoRoot, "src/DshMobile.Agent/Domain/PluginModels.cs"),
            Path.Combine(repoRoot, "src/DshMobile.Agent/Engines/PluginInventoryService.cs"),
            Path.Combine(repoRoot, "src/DshMobile.Agent/Presentation/PluginsViewModel.cs"),
        };
        foreach (var file in agentPluginFiles)
        {
            var codeOnly = StripCSharpSingleLineComments(File.ReadAllText(file));
            Assert.DoesNotContain("WebView", codeOnly, StringComparison.Ordinal);
            Assert.DoesNotContain("JsonSchema", codeOnly, StringComparison.Ordinal);
            Assert.DoesNotContain("SchemaForm", codeOnly, StringComparison.Ordinal);
        }
    }

    private static string StripCSharpSingleLineComments(string source) =>
        string.Join('\n', source.Split('\n').Select(line =>
        {
            var idx = line.IndexOf("//", StringComparison.Ordinal);
            return idx >= 0 ? line[..idx] : line;
        }));

    private sealed class StubPluginGateway : DefaultDshGateway
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
    }
}
