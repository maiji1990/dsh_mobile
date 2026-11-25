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

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DshCommand_AddsNodePreserveSymlinksOnce_RightAfterExposeInternals(bool inPlaceRestart, bool trimmedWeb)
    {
        var files = Path.Combine(_dir, $"files-preserve-{inPlaceRestart}-{trimmedWeb}");
        var native = Path.Combine(_dir, $"native-preserve-{inPlaceRestart}-{trimmedWeb}");
        Directory.CreateDirectory(native);
        File.WriteAllText(Path.Combine(native, "libproot.so"), "");
        var layout = new RuntimeLayout(files, Path.Combine(_dir, "cache"), native);
        Directory.CreateDirectory(layout.LayerDir("node"));
        Directory.CreateDirectory(layout.LayerDir("dsh"));
        Directory.CreateDirectory(layout.ShimDir);
        File.WriteAllText(layout.ShimFile, "// shim");
        if (trimmedWeb)
        {
            var trimPatch = GuestAssetProvisioner.WebTrimPatchHostPath(layout);
            Directory.CreateDirectory(Path.GetDirectoryName(trimPatch)!);
            File.WriteAllText(trimPatch, "- id: modules\n  disabled: true\n");
        }

        var argv = new ProotCommandBuilder(layout).DshCommand(inPlaceRestart: inPlaceRestart, trimmedWeb: trimmedWeb).ToList();
        var exposeIndex = argv.IndexOf("--expose-internals");
        var preserveIndex = argv.IndexOf("--preserve-symlinks");
        var firstImportIndex = argv.IndexOf("--import");
        var entryIndex = argv.IndexOf(RuntimeConstants.GuestDshBin);

        Assert.True(exposeIndex > 0);
        Assert.Equal(exposeIndex + 1, preserveIndex);
        Assert.Single(argv, a => a == "--preserve-symlinks");
        Assert.True(firstImportIndex > preserveIndex);
        Assert.True(preserveIndex < entryIndex);
        // 裁剪只影响 --patch/--no-open，不得改变 Node 标志的有无或位置。
        Assert.Equal(trimmedWeb, argv[^1] == "--no-open");
    }

    [Fact]
    public void GuestAndSandboxCommands_DoNotGetPreserveSymlinks()
    {
        var native = Path.Combine(_dir, "native-nonode");
        Directory.CreateDirectory(native);
        var layout = new RuntimeLayout(Path.Combine(_dir, "files-nonode"), null, native);
        Directory.CreateDirectory(layout.ShimDir);

        // 一次性 guest 命令与沙箱 keepalive 都是 bash，不是 Node 进程，不得带 Node 专用标志。
        Assert.DoesNotContain("--preserve-symlinks", new ProotCommandBuilder(layout).GuestCommand("uname -m"));
        Assert.DoesNotContain("--preserve-symlinks", new ProotCommandBuilder(layout).SandboxCommand());
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
            $"--bind={layout.ShimDir}:/opt/dshmobile",
            $"--bind={layout.GuestCacheDir}:/opt/dshmobile-cache",
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
    public void DshEnvironment_EnablesReadonlyPrefixCache_OnlyForDsh()
    {
        var files = Path.Combine(_dir, "files-cache");
        var native = Path.Combine(_dir, "native-cache");
        Directory.CreateDirectory(native);
        var current = Path.Combine(files, "runtime", "runtime-current");
        Directory.CreateDirectory(current);
        // Minimal profile: no layers, so only the role-specific keys under test can differ.
        File.WriteAllText(Path.Combine(current, "runtime-profile.json"),
            "{\"bundle\":{\"version\":\"0.1.0\",\"arch\":\"arm64\",\"compression\":\"zstd\"},\"layers\":[],\"assembly\":[]}");
        var builder = new ProotCommandBuilder(new RuntimeLayout(files, null, native));

        var dsh = builder.Environment("dsh");
        Assert.Equal("/opt/dshapp/runtime:/usr/local", dsh["PROOT_RO_CACHE_PREFIXES"]);
        Assert.Equal("0", dsh["PROOT_RO_CACHE_NONEXIST"]);
        // 严格只读层负缓存：只允许 /opt/dshapp/runtime，绝不包含可写的 /usr/local 或用户路径。
        Assert.Equal(ProotCommandBuilder.ReadonlyNonexistCachePaths, dsh["PROOT_RO_CACHE_NONEXIST_PREFIXES"]);
        Assert.Equal("/opt/dshapp/runtime", dsh["PROOT_RO_CACHE_NONEXIST_PREFIXES"]);
        Assert.DoesNotContain("/usr/local", dsh["PROOT_RO_CACHE_NONEXIST_PREFIXES"], StringComparison.Ordinal);
        Assert.DoesNotContain("/root/projects", dsh["PROOT_RO_CACHE_NONEXIST_PREFIXES"], StringComparison.Ordinal);
        Assert.DoesNotContain(RuntimeConstants.GuestCacheDir, dsh["PROOT_RO_CACHE_NONEXIST_PREFIXES"], StringComparison.Ordinal);
        // Node compile cache 只在 dsh 角色、且落在 guest 缓存目录下。
        Assert.Equal($"{RuntimeConstants.GuestCacheDir}/node-compile-cache", dsh["NODE_COMPILE_CACHE"]);
        Assert.StartsWith(RuntimeConstants.GuestCacheDir + "/", dsh["NODE_COMPILE_CACHE"], StringComparison.Ordinal);
        // Sessions, sandbox keepalive and terminal children keep the uncached path resolution.
        Assert.DoesNotContain("PROOT_RO_CACHE_PREFIXES", builder.Environment("guest").Keys);
        Assert.DoesNotContain("PROOT_RO_CACHE_NONEXIST", builder.Environment("guest").Keys);
        Assert.DoesNotContain("PROOT_RO_CACHE_NONEXIST_PREFIXES", builder.Environment("guest").Keys);
        Assert.DoesNotContain("NODE_COMPILE_CACHE", builder.Environment("guest").Keys);

        // ADR-0030: the cached prefixes must be exactly the read-only layer binds, so a renamed layer cannot leave a
        // prefix pointing at the workspace or the guest cache.
        Directory.CreateDirectory(new RuntimeLayout(files, null, native).LayerDir("node"));
        Directory.CreateDirectory(new RuntimeLayout(files, null, native).LayerDir("dsh"));
        var argv = builder.GuestCommand("true");
        foreach (var prefix in ProotCommandBuilder.ReadonlyPrefixCachePaths.Split(':'))
        {
            Assert.Contains(argv, a => a.StartsWith("--bind=", StringComparison.Ordinal) && a.EndsWith(":" + prefix, StringComparison.Ordinal));
        }
        Assert.DoesNotContain("/root/projects", ProotCommandBuilder.ReadonlyPrefixCachePaths, StringComparison.Ordinal);
        Assert.DoesNotContain(RuntimeConstants.GuestCacheDir, ProotCommandBuilder.ReadonlyPrefixCachePaths, StringComparison.Ordinal);
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
