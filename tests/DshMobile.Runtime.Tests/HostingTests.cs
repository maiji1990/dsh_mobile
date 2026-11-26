using System.Text.Json;
using DshMobile.Native;
using DshMobile.Runtime.Hosting;

namespace DshMobile.Runtime.Tests;

public sealed class HostingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dshm-host-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Redactor_MatchesLegacyVectors()
    {
        var cases = Repo.Vectors("log-redactor.json").GetProperty("redact").EnumerateArray().ToList();
        Assert.True(cases.Count >= 8);
        foreach (var c in cases)
        {
            Assert.Equal(c.GetProperty("out").GetString(), LogRedactor.Redact(c.GetProperty("in").GetString()!));
        }
    }

    [Fact]
    public void LaunchToken_ParsesLikeLegacyIngest()
    {
        foreach (var c in Repo.Vectors("log-redactor.json").GetProperty("launchToken").EnumerateArray())
        {
            var expected = c.GetProperty("token").ValueKind == JsonValueKind.Null ? null : c.GetProperty("token").GetString();
            Assert.Equal(expected, DshLaunchToken.TryParse(c.GetProperty("line").GetString()!));
        }
    }

    [Fact]
    public void LaunchTokenLine_IsNeverLoggedRaw()
    {
        const string line = "dsh web: http://127.0.0.1:3080/?token=s3cr3t-value";
        Assert.Equal("s3cr3t-value", DshLaunchToken.TryParse(line));
        Assert.DoesNotContain("s3cr3t-value", LogRedactor.Redact(line), StringComparison.Ordinal);
    }

    private string Lock(string rel, string content)
    {
        var p = Path.Combine(_dir, "user-data", ".dsh", rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void WriterLockSweeper_RemovesOnlyDeadPidLocks()
    {
        var dead = Lock("sessions/a.json.lock", "4242\n");
        var alive = Lock("sessions/b.json.lock", "100");
        var junk = Lock("c.lock", "not-a-pid");
        var tooDeep = Lock("1/2/3/4/5/6/7/d.lock", "4242");
        var notLock = Lock("e.json", "4242");
        var r = DshWriterLockSweeper.Sweep(Path.Combine(_dir, "user-data"), pid => pid == 100, () => true);
        Assert.False(File.Exists(dead));
        Assert.True(File.Exists(alive) && File.Exists(junk) && File.Exists(tooDeep) && File.Exists(notLock));
        Assert.Equal([".dsh/sessions/a.json.lock"], r.Removed); // relative to the workspace, like legacy
        Assert.Equal(3, r.Scanned);
    }

    [Fact]
    public void WriterLockSweeper_SkipsWhenLivenessIsUnknown()
    {
        var dead = Lock("a.lock", "4242");
        var r = DshWriterLockSweeper.Sweep(Path.Combine(_dir, "user-data"), _ => false, () => false);
        Assert.True(r.Skipped);
        Assert.True(File.Exists(dead));
    }

    private static readonly Action<string> NoLog = _ => { };

    [Fact]
    public async Task Host_RefusesProotOutsideNativeLibraryDir()
    {
        var layout = new RuntimeLayout(Path.Combine(_dir, "files"), null, Path.Combine(_dir, "nolibs"));
        await using var host = new DshHost(layout, NoLog);
        var r = await host.StartAsync(CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(NativeErrorCodes.ExecutableNotAllowed, r.Code);
        Assert.Equal(DshHostState.Stopped, host.State);
        // ADR-0015: a refused start owns nothing, so the legacy fallback may start right after.
        Assert.False(host.OwnsProcesses);
        Assert.Null(host.UiToolsAtStart);
    }

    [Fact]
    public async Task Host_RefusesWithoutRuntimeOrNodeLayer()
    {
        var native = Path.Combine(_dir, "lib");
        Directory.CreateDirectory(native);
        File.WriteAllText(Path.Combine(native, "libproot.so"), "");
        var layout = new RuntimeLayout(Path.Combine(_dir, "files"), null, native);
        await using var host = new DshHost(layout, NoLog);
        Assert.Equal(DshHost.NotInstalled, (await host.StartAsync(CancellationToken.None)).Code);
        Directory.CreateDirectory(Path.Combine(layout.LayerDir("base"), "usr", "bin"));
        File.WriteAllText(Path.Combine(layout.LayerDir("base"), "usr", "bin", "bash"), "");
        Assert.Equal(DshHost.NodeMissing, (await host.StartAsync(CancellationToken.None)).Code);
        Assert.Null(host.Token);
    }

    [Fact]
    public async Task Host_StopWhenStopped_IsNoop()
    {
        var layout = new RuntimeLayout(Path.Combine(_dir, "files"), null, Path.Combine(_dir, "lib"));
        await using var host = new DshHost(layout, NoLog);
        var r = await host.StopAsync(CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal(DshHostState.Stopped, host.State);
        Assert.False(host.OwnsProcesses);
    }

    [Fact]
    public void StartupProbeInterval_FastDuringInitialWindow_ThenFallsBackToOneSecond()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(150), DshHost.StartupProbeInterval(0));
        Assert.Equal(TimeSpan.FromMilliseconds(150), DshHost.StartupProbeInterval(19_999));
        Assert.Equal(TimeSpan.FromSeconds(1), DshHost.StartupProbeInterval(20_000));
        Assert.Equal(TimeSpan.FromSeconds(1), DshHost.StartupProbeInterval(60_000));
    }

    [Fact]
    public void StartupProbeInterval_HintWindowUses25ms_ThenRecoversToFastAndOneSecond()
    {
        // hint 窗口内即使已超过 20 s 基线窗口也使用 25 ms。
        Assert.Equal(TimeSpan.FromMilliseconds(25), DshHost.StartupProbeInterval(25_000, 1_000, 3_000));
        Assert.Equal(TimeSpan.FromMilliseconds(25), DshHost.StartupProbeInterval(0, 2_999, 3_000));
        // 窗口到期恢复原语义：20 s 内 150 ms，之后 1 s。
        Assert.Equal(TimeSpan.FromMilliseconds(150), DshHost.StartupProbeInterval(5_000, 3_000, 3_000));
        Assert.Equal(TimeSpan.FromSeconds(1), DshHost.StartupProbeInterval(20_000, 3_001, 3_000));
    }

    [Fact]
    public async Task CleanupStaleDshAsync_CompletesBothStepsBeforeAwaitReturns()
    {
        var order = new List<string>();
        var workspace = Path.Combine(_dir, "no-dsh-home");
        var task = DshHost.CleanupStaleDshAsync(
            () =>
            {
                lock (order)
                {
                    order.Add("kill-stale-dsh");
                }
            },
            workspace);

        var result = await task;
        lock (order)
        {
            order.Add("sweep-done");
        }

        // 清理任务只有在 kill 与 sweep 都完成后才返回，StartAsync 才能安全 spawn DSH。
        Assert.Equal(["kill-stale-dsh", "sweep-done"], order);
        Assert.Equal(0, result.Scanned);
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task CleanupStaleDshAsync_PropagatesFailure_SoStartAsyncTakesFailurePath()
    {
        var task = DshHost.CleanupStaleDshAsync(
            () => throw new IOException("stale cleanup failed"),
            Path.Combine(_dir, "no-dsh-home"));

        await Assert.ThrowsAsync<IOException>(() => task);
    }

    [Fact]
    public async Task WaitSandboxReady_ReturnsImmediatelyWhenMarkerAlreadySeen()
    {
        var psi = OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo("cmd.exe", "/d /c ping -n 10 127.0.0.1 >nul") { UseShellExecute = false }
            : new System.Diagnostics.ProcessStartInfo("/bin/sh", "-c \"sleep 10\"") { UseShellExecute = false };
        using var process = System.Diagnostics.Process.Start(psi)!;
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await DshHost.WaitSandboxReadyAsync(process, Task.FromResult(true), TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"expected early return, took {stopwatch.Elapsed}");
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        }
    }
}
