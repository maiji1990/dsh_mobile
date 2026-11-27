using DshMobile.Application.Runtime;
using DshMobile.Application.Shadow;
using DshMobile.Runtime;
using DshMobile.Runtime.Hosting;
using DshMobile.Runtime.Journal;
using DshMobile.Runtime.Plugins;

namespace DshMobile.Application.Tests;

public sealed class RuntimeCoordinatorPluginTests : IDisposable
{
    private sealed class FakeForeground : IForegroundServiceController
    {
        public bool Held;
        public void Acquire() => Held = true;
        public void Release() => Held = false;
    }

    private sealed class ListLog : IDiagnosticsLog, IShadowReportSink
    {
        public readonly List<string> Lines = [];
        public void Note(string line) => Lines.Add(line);
        public void Report(ShadowReport report) { }
    }

    private readonly string _dir = Directory.CreateTempSubdirectory("dshm-coord-plugin-").FullName;
    private readonly List<string> _hostCalls = [];
    private readonly FakeForeground _fg = new();
    private readonly ListLog _log = new();
    private readonly InMemoryRuntimeJournal _journal = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private RuntimeLayout CreateLayout() => new(
        Path.Combine(_dir, "files"),
        Path.Combine(_dir, "cache"),
        Path.Combine(_dir, "native"));

    [Fact]
    public async Task InstallAndUninstall_InvalidSpec_RejectsWithZeroProcessSpawn()
    {
        var guestCalls = 0;
        var layout = CreateLayout();
        using var coordinator = new RuntimeCoordinator(
            layout,
            new RuntimeStateMachine(_journal, RuntimeCoordinator.Implementation),
            _fg,
            _log,
            _log,
            new RuntimeOptions
            {
                GuestShellRunner = (_, _, _) =>
                {
                    guestCalls++;
                    return Task.FromResult(new GuestProcessResult(true, 0, "", null));
                },
            },
            hostFactory: (_, _, _) => new FakeDshHost(_hostCalls));

        var badAdd = await coordinator.InstallProfilePluginAsync("foo; rm -rf /", CancellationToken.None);
        Assert.False(badAdd.Ok);
        Assert.Equal(RuntimeErrorCodes.BadRequest, badAdd.Code);

        var badRem = await coordinator.UninstallProfilePluginAsync("--registry=evil", CancellationToken.None);
        Assert.False(badRem.Ok);
        Assert.Equal(RuntimeErrorCodes.BadRequest, badRem.Code);

        var protectedRem = await coordinator.UninstallProfilePluginAsync("@deepseek-ai/dsh", CancellationToken.None);
        Assert.False(protectedRem.Ok);
        Assert.Equal(RuntimeErrorCodes.BadRequest, protectedRem.Code);

        Assert.Equal(0, guestCalls);
    }

    [Fact]
    public async Task InstallProfilePluginAsync_ValidSpec_RunsQuotedGuestCommandAndHandlesFailure()
    {
        var executedCommands = new List<string>();
        var layout = CreateLayout();
        using var coordinator = new RuntimeCoordinator(
            layout,
            new RuntimeStateMachine(_journal, RuntimeCoordinator.Implementation),
            _fg,
            _log,
            _log,
            new RuntimeOptions
            {
                GuestShellRunner = (cmd, _, _) =>
                {
                    executedCommands.Add(cmd);
                    return Task.FromResult(new GuestProcessResult(false, 1, "network error Authorization: Bearer secret-xyz sk-secret-999", "GUEST_CMD_FAILED"));
                },
            },
            hostFactory: (_, _, _) => new FakeDshHost(_hostCalls));

        var res = await coordinator.InstallProfilePluginAsync("@scope/sample-plugin@1.0.0", CancellationToken.None);
        Assert.False(res.Ok);
        Assert.Single(executedCommands);
        Assert.Contains("'@scope/sample-plugin@1.0.0'", executedCommands[0], StringComparison.Ordinal);
        Assert.DoesNotContain("secret-xyz", res.Diagnostic ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-secret-999", res.Diagnostic ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestartWebHostAsync_StopsRunningHostAndStartsFresh()
    {
        var layout = CreateLayout();
        TestRuntime.Install(layout.FilesDir);
        var host = new FakeDshHost(_hostCalls);
        using var coordinator = new RuntimeCoordinator(
            layout,
            new RuntimeStateMachine(_journal, RuntimeCoordinator.Implementation),
            _fg,
            _log,
            _log,
            new RuntimeOptions { HostPathExists = DshMobile.Runtime.Layers.LayerExtractor.AndroidLikeHost },
            hostFactory: (_, _, _) => host);

        var started = await coordinator.StartAsync(DshProfile.Web, CancellationToken.None);
        Assert.True(started.Ok);
        Assert.Equal(DshHostState.Ready, host.State);

        var restarted = await coordinator.RestartWebHostAsync(CancellationToken.None);
        Assert.True(restarted.Ok);
        Assert.Equal(["host.start", "host.stop", "host.start"], _hostCalls);
        Assert.Equal(DshHostState.Ready, host.State);
    }

    [Fact]
    public async Task FullWebUiCompatMode_ReflectsLiveProcessAndGatesLocalBrowserUri()
    {
        var layout = CreateLayout();
        TestRuntime.Install(layout.FilesDir);
        var preferTrimmed = true;
        var host = new FakeDshHost(_hostCalls)
        {
            UseTrimmedWeb = () => preferTrimmed,
            LaunchToken = "live-session-token-42",
        };
        using var coordinator = new RuntimeCoordinator(
            layout,
            new RuntimeStateMachine(_journal, RuntimeCoordinator.Implementation),
            _fg,
            _log,
            _log,
            new RuntimeOptions { HostPathExists = DshMobile.Runtime.Layers.LayerExtractor.AndroidLikeHost },
            hostFactory: (_, _, _) => host);

        // 启动前：既不是精简运行中也不是完整 WebUI 运行中，URI 为 null。
        Assert.False(coordinator.IsTrimmedWebActive);
        Assert.False(coordinator.IsFullWebUiActive);
        Assert.Null(coordinator.HostTrimmedWebAtStart);
        Assert.Null(coordinator.TryGetLocalWebUiBrowserUri());

        // 1. 以默认精简模式启动：无法获取本机完整 WebUI 浏览器 URI。
        Assert.True((await coordinator.StartAsync(DshProfile.Web, CancellationToken.None)).Ok);
        Assert.True(coordinator.IsTrimmedWebActive);
        Assert.False(coordinator.IsFullWebUiActive);
        Assert.True(coordinator.HostTrimmedWebAtStart);
        Assert.Null(coordinator.TryGetLocalWebUiBrowserUri());

        // 2. 用户修改偏好为完整 WebUI 但尚未重启：状态必须仍反映当前活着的进程（精简模式）。
        preferTrimmed = false;
        Assert.True(coordinator.IsTrimmedWebActive);
        Assert.False(coordinator.IsFullWebUiActive);
        Assert.Null(coordinator.TryGetLocalWebUiBrowserUri());

        // 3. 重启后切入完整 WebUI 兼容模式：状态翻转并允许受控构造本机 127.0.0.1 URI。
        Assert.True((await coordinator.RestartWebHostAsync(CancellationToken.None)).Ok);
        Assert.False(coordinator.IsTrimmedWebActive);
        Assert.True(coordinator.IsFullWebUiActive);
        Assert.False(coordinator.HostTrimmedWebAtStart);
        var uri = coordinator.TryGetLocalWebUiBrowserUri();
        Assert.NotNull(uri);
        Assert.Equal("http://127.0.0.1:3080/?token=live-session-token-42", uri!.AbsoluteUri);
        // 严禁把令牌或带令牌 URI 写入诊断日志。
        Assert.DoesNotContain(_log.Lines, line => line.Contains("live-session-token-42", StringComparison.Ordinal));

        // 4. 停止后立即收回访问地址与激活状态。
        Assert.True((await coordinator.StopAsync(CancellationToken.None)).Ok);
        Assert.False(coordinator.IsFullWebUiActive);
        Assert.Null(coordinator.TryGetLocalWebUiBrowserUri());
    }

    [Fact]
    public async Task ReadLocalPluginDetailAsync_ReadsLocalPackageWithoutSpawningGuest()
    {
        var guestCalls = 0;
        var layout = CreateLayout();
        var pkgDir = Path.Combine(GuestPluginService.WebProfileNodeModulesHostDir(layout), "local-doc-plugin");
        Directory.CreateDirectory(pkgDir);
        await File.WriteAllTextAsync(
            Path.Combine(pkgDir, "package.json"),
            """{"name":"local-doc-plugin","version":"1.0.0","description":"Local doc test"}""");
        await File.WriteAllTextAsync(Path.Combine(pkgDir, "README.md"), "# Local Doc\n\nContent");

        using var coordinator = new RuntimeCoordinator(
            layout,
            new RuntimeStateMachine(_journal, RuntimeCoordinator.Implementation),
            _fg,
            _log,
            _log,
            new RuntimeOptions
            {
                GuestShellRunner = (_, _, _) =>
                {
                    guestCalls++;
                    return Task.FromResult(new GuestProcessResult(true, 0, "", null));
                },
            },
            hostFactory: (_, _, _) => new FakeDshHost(_hostCalls));

        var detail = await coordinator.ReadLocalPluginDetailAsync("local-doc-plugin", "zh-CN", CancellationToken.None);
        Assert.True(detail.Ok);
        Assert.Equal("1.0.0", detail.Version);
        Assert.Equal(GuestLocalPackageDetail.OriginReadme, detail.DocumentOrigin);
        Assert.Equal("# Local Doc\n\nContent", detail.Markdown);
        Assert.Equal(0, guestCalls);
    }

    [Fact]
    public async Task InstallProfilePluginAsync_PropagatesNonBundleWarning()
    {
        var layout = CreateLayout();
        using var coordinator = new RuntimeCoordinator(
            layout,
            new RuntimeStateMachine(_journal, RuntimeCoordinator.Implementation),
            _fg,
            _log,
            _log,
            new RuntimeOptions
            {
                GuestShellRunner = (_, _, _) =>
                    Task.FromResult(new GuestProcessResult(true, 0, "notice: pkg declares no dsh.bundle", null)),
            },
            hostFactory: (_, _, _) => new FakeDshHost(_hostCalls));

        var res = await coordinator.InstallProfilePluginAsync("plain-pkg@1.0.0", CancellationToken.None);
        Assert.True(res.Ok);
        Assert.False(res.DeclaredBundleManifest);
        Assert.Equal(GuestPluginService.NoBundleWarningSummary, res.WarningSummary);
        Assert.Contains(_log.Lines, l => l.Contains("no dsh.bundle", StringComparison.Ordinal));
    }
}
