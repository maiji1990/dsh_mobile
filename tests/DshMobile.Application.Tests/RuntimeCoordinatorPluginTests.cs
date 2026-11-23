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
}
