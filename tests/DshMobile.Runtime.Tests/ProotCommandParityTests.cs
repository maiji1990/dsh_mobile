using System.Diagnostics;
using System.Text.RegularExpressions;
using DshMobile.Runtime.Guest;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// C# half of the PRoot exec contract. Env: tests/TestVectors/proot-env.json is also computed by the legacy
/// buildProotEnv (native-bridge LegacyProotEnvParityTest). Argv: checked here against the legacy builder's structure
/// and on the device against the legacy process's /proc/&lt;pid&gt;/cmdline (docs/COMPATIBILITY.md).
/// </summary>
public sealed partial class ProotCommandParityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dshm-proot-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Environment_MatchesSharedVectors()
    {
        var doc = Repo.Vectors("proot-env.json");
        var cases = doc.GetProperty("cases").EnumerateArray().ToList();
        Assert.True(cases.Count >= 4);
        foreach (var (c, i) in cases.Select((c, i) => (c, i)))
        {
            var files = Path.GetFullPath(Path.Combine(_dir, "files-" + i));
            var native = Path.GetFullPath(Path.Combine(_dir, "native-" + i));
            Directory.CreateDirectory(native);
            var current = Path.Combine(files, "runtime", "runtime-current");
            foreach (var f in doc.GetProperty("envFragments").EnumerateObject())
            {
                var path = Path.Combine(current, f.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, f.Value.GetString());
            }
            if (c.GetProperty("withProfile").GetBoolean())
            {
                File.WriteAllText(Path.Combine(current, "runtime-profile.json"), doc.GetProperty("profile").GetString());
            }
            foreach (var lib in c.GetProperty("nativeLibs").EnumerateArray())
            {
                File.WriteAllText(Path.Combine(native, lib.GetString()!), "");
            }
            var env = new ProotCommandBuilder(new RuntimeLayout(files, null, native)).Environment(c.GetProperty("role").GetString()!);
            string Norm(string v) => v.Replace(files, "@FILES@", StringComparison.Ordinal).Replace(native, "@NATIVE@", StringComparison.Ordinal).Replace('\\', '/');
            var expected = c.GetProperty("env").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
            Assert.Equal(new SortedDictionary<string, string>(expected, StringComparer.Ordinal),
                new SortedDictionary<string, string>(env.ToDictionary(kv => kv.Key, kv => Norm(kv.Value)), StringComparer.Ordinal));
        }
    }

    [Fact]
    public void GuestCommand_HasTheLegacyArgvShape()
    {
        var files = Path.Combine(_dir, "files");
        var native = Path.Combine(_dir, "native");
        Directory.CreateDirectory(native);
        File.WriteAllText(Path.Combine(native, "libproot.so"), "");
        var layout = new RuntimeLayout(files, Path.Combine(_dir, "cache"), native);
        Directory.CreateDirectory(layout.LayerDir("node"));
        Directory.CreateDirectory(layout.LayerDir("dsh"));
        Directory.CreateDirectory(layout.ShimDir);
        var argv = new ProotCommandBuilder(layout).GuestCommand("uname -m");
        string[] expected =
        [
            Path.Combine(native, "libproot.so"),
            "--rootfs=" + layout.LayerDir("base"),
            "--bind=/system", "--bind=/apex", "--bind=/proc", "--bind=/dev",
            $"--bind={layout.LayerDir("node")}:/usr/local",
            $"--bind={layout.LayerDir("dsh")}:/opt/dshapp/runtime",
            $"--bind={layout.ShimDir}:/opt/dshbox",
            $"--bind={layout.GuestCacheDir}:/opt/dshbox-cache",
            $"--bind={layout.UserDataDir}:/root/projects",
            $"--bind={layout.NpmCacheDir}:/root/.npm",
            $"--bind={layout.PilotEntryDir}:/opt/pilot",
            "--cwd=/root", "--kill-on-exit", "/usr/bin/bash", "-c", "uname -m",
        ];
        Assert.Equal(expected, argv);
        // Bind sources PRoot requires to exist are created while building, as in legacy.
        Assert.True(Directory.Exists(layout.NpmCacheDir) && Directory.Exists(layout.GuestCacheDir) && Directory.Exists(layout.PilotEntryDir));
    }

    [Fact]
    public void GuestCommand_SkipsMissingOptionalLayers_AndFallsBackForProot()
    {
        var layout = new RuntimeLayout(Path.Combine(_dir, "files"), null, Path.Combine(_dir, "nolibs"));
        var argv = new ProotCommandBuilder(layout).GuestCommand("true");
        Assert.Equal(Path.Combine(layout.LayerDir("android-side"), "bin", "proot"), argv[0]);
        Assert.DoesNotContain(argv, a => a.EndsWith(":/usr/local", StringComparison.Ordinal) || a.EndsWith(":/opt/dshapp/runtime", StringComparison.Ordinal) || a.EndsWith(":/opt/browser", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProotOutsideNativeLibraryDir_IsRefusedNotSpawned()
    {
        // The android-side fallback lives under filesDir: executing it would violate W^X on Android 10+.
        var layout = new RuntimeLayout(Path.Combine(_dir, "files"), null, Path.Combine(_dir, "nolibs"));
        var r = await new ProotGuestProcess(layout).RunShellAsync("true", new GuestExecutionOptions(), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Null(r.ExitCode);
        Assert.StartsWith("EXECUTABLE_NOT_ALLOWED", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseEnvExports_FollowsLegacyRules()
    {
        var parsed = ProotCommandBuilder.ParseEnvExports("# c\nexport A=\"1\"\r\nB='2'\n  export C = 3 \n=bad\nnoeq\nA=4\n").ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal(new Dictionary<string, string> { ["A"] = "4", ["B"] = "2", ["C"] = "3" }, parsed);
    }

    /// <summary>The bridge RuntimeController.kt DIAGNOSTICS map at 128c2ea (pinned in PR-10, ADR-0018).</summary>
    [Fact]
    public void Diagnostics_AreTheLegacyRuntimeControllerOnes()
    {
        (string, string)[] legacy =
        [
            ("arch", "uname -m"),
            ("debian", "cat /etc/debian_version"),
            ("node", "node --version"),
            ("dsh", "node " + RuntimeConstants.GuestDshBin + " --version"),
        ];
        Assert.Equal(legacy, GuestDiagnostics.Commands.Select(c => (c.Key, c.Command)));
    }

    // ---------------------------------------------------------------- the process runner, with host commands

    private static string[] Shell(string script) =>
        OperatingSystem.IsWindows() ? ["cmd.exe", "/d", "/c", script] : ["/bin/sh", "-c", script];

    private static readonly Dictionary<string, string> NoEnv = [];

    [Fact]
    public async Task Runner_ReportsRealExitCodeAndMergesStreams()
    {
        var r = await ProotGuestProcess.RunProcessAsync(Shell("echo out& echo err 1>&2& exit 3"), NoEnv, new GuestExecutionOptions(), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("out", r.Output, StringComparison.Ordinal);
        Assert.Contains("err", r.Output, StringComparison.Ordinal);
        Assert.Contains("exit=3", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runner_PassesEnvironmentAndClosesStdin()
    {
        var script = OperatingSystem.IsWindows() ? "set /p X=& echo v=%DSHM_T%" : "read X; echo v=$DSHM_T";
        var r = await ProotGuestProcess.RunProcessAsync(Shell(script), new Dictionary<string, string> { ["DSHM_T"] = "42" },
            new GuestExecutionOptions(Timeout: TimeSpan.FromSeconds(20)), CancellationToken.None);
        Assert.True(r.Ok, r.Error);
        Assert.Contains("v=42", r.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runner_TimeoutKillsAndIsNeverSuccess()
    {
        var script = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 >nul" : "sleep 30";
        var sw = Stopwatch.StartNew();
        var r = await ProotGuestProcess.RunProcessAsync(Shell(script), NoEnv, new GuestExecutionOptions(Timeout: TimeSpan.FromSeconds(1)), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(-1, r.ExitCode);
        Assert.StartsWith("GUEST_TIMEOUT", r.Error, StringComparison.Ordinal);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task Runner_KeepsOnlyTheLastLines()
    {
        var script = OperatingSystem.IsWindows() ? "for /l %i in (1,1,50) do @echo line%i" : "for i in $(seq 1 50); do echo line$i; done";
        var r = await ProotGuestProcess.RunProcessAsync(Shell(script), NoEnv, new GuestExecutionOptions(MaxOutputLines: 5), CancellationToken.None);
        Assert.True(r.Ok, r.Error);
        var lines = r.Output.Split('\n');
        Assert.Equal(5, lines.Length);
        Assert.Equal("line50", lines[^1].Trim());
    }

    [Fact]
    public async Task Runner_MissingExecutable_IsSpawnFailed()
    {
        var r = await ProotGuestProcess.RunProcessAsync([Path.Combine(_dir, "does-not-exist")], NoEnv, new GuestExecutionOptions(), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.StartsWith("GUEST_SPAWN_FAILED", r.Error, StringComparison.Ordinal);
    }
}
