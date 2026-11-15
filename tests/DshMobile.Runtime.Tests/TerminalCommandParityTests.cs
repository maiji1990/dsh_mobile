using System.Text.Json;
using System.Text.RegularExpressions;
using DshMobile.Native;
using DshMobile.Runtime.Guest;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// PR-06: the C# terminal launch command against tests/TestVectors/terminal-command.json — the same cases the legacy
/// DSHBox TerminalCommandFactory / TerminalEnvFactory run in native-bridge (LegacyTerminalCommandParityTest).
/// Plus the native ABI contract: C exports, managed LibraryImports and both build scripts must list the same symbols.
/// </summary>
public sealed class TerminalCommandParityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dshm-term-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static List<string>? Strings(JsonElement c, string name) =>
        c.GetProperty(name).ValueKind == JsonValueKind.Null ? null : c.GetProperty(name).EnumerateArray().Select(e => e.GetString()!).ToList();

    [Fact]
    public void TerminalCommand_MatchesSharedVectors()
    {
        var cases = Repo.Vectors("terminal-command.json").GetProperty("cases").EnumerateArray().ToList();
        Assert.True(cases.Count >= 6);
        var i = 0;
        foreach (var c in cases)
        {
            var name = c.GetProperty("name").GetString()!;
            var files = Path.Combine(_root, "files-" + i);
            var native = Path.Combine(_root, "native-" + i++);
            Directory.CreateDirectory(files);
            Directory.CreateDirectory(native);
            foreach (var d in Strings(c, "dirs")!)
            {
                Directory.CreateDirectory(Path.Combine(files, d));
            }
            foreach (var f in Strings(c, "files")!)
            {
                var p = Path.Combine(files, f);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, "");
            }
            foreach (var lib in Strings(c, "nativeLibs")!)
            {
                File.WriteAllText(Path.Combine(native, lib), "");
            }
            // Separators are normalized only in host-path values (PS1 contains real backslashes).
            string Norm(string v)
            {
                var r = v.Replace(files, "@FILES@", StringComparison.Ordinal).Replace(native, "@NATIVE@", StringComparison.Ordinal);
                return r == v ? v : r.Replace('\\', '/');
            }

            var builder = new TerminalCommandBuilder(new RuntimeLayout(files, null, native));
            var argv = builder.LoginShell(_ => false);
            var expected = Strings(c, "argv");
            if (expected is null)
            {
                Assert.True(argv is null, name);
                continue;
            }
            Assert.True(argv is not null, name);
            Assert.Equal(expected, argv!.Select(Norm).ToList());
            Assert.Equal(Strings(c, "env"), builder.Environment().Select(Norm).ToList());
        }
    }

    [Fact]
    public void LinkerconfigBind_IsAddedBeforeTheWorkspace_WhenTheHostHasIt()
    {
        var files = Path.Combine(_root, "files");
        Directory.CreateDirectory(Path.Combine(files, "runtime", "runtime-current", "base"));
        var native = Path.Combine(_root, "native");
        Directory.CreateDirectory(native);
        File.WriteAllText(Path.Combine(native, "libproot.so"), "");
        var argv = new TerminalCommandBuilder(new RuntimeLayout(files, null, native)).LoginShell(p => p == "/linkerconfig")!;
        var i = argv.ToList().IndexOf("--bind=/linkerconfig");
        Assert.True(i > 0);
        Assert.StartsWith("--bind=", argv[i + 1], StringComparison.Ordinal);
        Assert.EndsWith(":/root/projects", argv[i + 1], StringComparison.Ordinal);
    }

    [Fact]
    public void ProcFake_WritesUpstreamTexts_AndKeepsExistingFiles()
    {
        var rootfs = Path.Combine(_root, "rootfs");
        var binds = TerminalCommandBuilder.ProcFakeBinds(rootfs);
        Assert.Equal(3, binds.Count);
        var stat = File.ReadAllText(Path.Combine(rootfs, "root", ".procfake", "stat"));
        Assert.StartsWith("cpu  1000 0 1000 1000000 0 0 0 0 0 0\ncpu0 125 0 125 125000 0 0 0 0 0 0\n", stat, StringComparison.Ordinal);
        Assert.Contains("cpu7 125 0 125 125000 0 0 0 0 0 0\nintr 0 0 0\n", stat, StringComparison.Ordinal);
        Assert.EndsWith("softirq 0 0 0 0\n", stat, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(rootfs, "root", ".procfake", "uptime"), "custom\n");
        TerminalCommandBuilder.ProcFakeBinds(rootfs);
        Assert.Equal("custom\n", File.ReadAllText(Path.Combine(rootfs, "root", ".procfake", "uptime")));
    }

    /// <summary>DSHBox v1.5.1 (7c9d33c) TerminalProcFake.kt UPTIME / LOADAVG, pinned in PR-10 (ADR-0018).</summary>
    [Fact]
    public void LegacyProcFakeTexts_AreTheOnesPorted()
    {
        Assert.Equal("100000.00 500000.00\n", TerminalCommandBuilder.Uptime);
        Assert.Equal("0.00 0.01 0.05 1/100 1000\n", TerminalCommandBuilder.LoadAvg);
    }

    [Fact]
    public async Task OpenPty_WithoutRootfs_IsNotInstalled_NotASpawnAttempt()
    {
        var files = Path.Combine(_root, "empty");
        Directory.CreateDirectory(files);
        var layout = new RuntimeLayout(files, null, Path.Combine(_root, "nolib"));
        var e = await Assert.ThrowsAsync<GuestPtyException>(() => new ProotGuestProcess(layout).OpenPtyAsync(new GuestExecutionOptions(), CancellationToken.None));
        Assert.Equal(RuntimeErrorCodes.NotInstalled, e.Code);
    }

    // ── native ABI contract (no device needed) ───────────────────────────

    private static string NativeSource => File.ReadAllText(Repo.PathOf("native", "dshmobile-native", "src", "dshm_native.c"));

    private static Dictionary<string, int> CExports() =>
        Regex.Matches(NativeSource, @"DSHM_EXPORT\s+int\s+(dshm_\w+)\s*\(([^)]*)\)")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim() is "void" or "" ? 0 : m.Groups[2].Value.Split(',').Length);

    [Fact]
    public void NativeAbi_ManagedImportsMatchTheCExports()
    {
        var exports = CExports();
        Assert.Equal(8, exports.Count);
        var cs = File.ReadAllText(Repo.PathOf("src", "DshMobile.Native", "DshNativeLibrary.cs"));
        var imports = Regex.Matches(cs, @"EntryPoint = ""(dshm_\w+)""[^\]]*\]\s*(?:internal|private|public)\s+static\s+(?:unsafe\s+)?partial\s+int\s+\w+\(([^)]*)\)")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim().Length == 0 ? 0 : m.Groups[2].Value.Split(',').Length);
        Assert.Equal(exports.Keys.Order(), imports.Keys.Order());
        foreach (var (name, arity) in exports)
        {
            Assert.True(imports[name] == arity, $"{name}: C has {arity} parameters, C# {imports[name]}");
        }
        var version = int.Parse(Regex.Match(NativeSource, @"#define DSHM_ABI_VERSION (\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(DshNativeLibrary.ExpectedAbiVersion, version);
    }

    [Fact]
    public void NativeBuildScripts_CheckEveryExport_And16KbAlignment()
    {
        var exports = CExports().Keys.Order().ToList();
        foreach (var script in new[] { "build.ps1", "build.sh" })
        {
            var text = File.ReadAllText(Repo.PathOf("native", "dshmobile-native", script));
            Assert.All(exports, e => Assert.Contains(e, text, StringComparison.Ordinal));
            Assert.Contains("max-page-size=16384", text, StringComparison.Ordinal);
            Assert.Contains("-Werror", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NativeChild_OnlyUsesAsyncSignalSafeCallsBetweenForkAndExec()
    {
        var src = NativeSource;
        var child = src[src.IndexOf("if (pid == 0) {", StringComparison.Ordinal)..src.IndexOf("int fork_errno = errno;", StringComparison.Ordinal)];
        var calls = Regex.Matches(child, @"\b([a-z_][a-z0-9_]*)\s*\(").Select(m => m.Groups[1].Value).Where(c => c is not ("if" or "for" or "sizeof")).ToHashSet();
        string[] allowed = ["close", "memset", "sigaction", "sigemptyset", "sigprocmask", "setsid", "open", "child_fail", "ioctl", "dup2", "close_from", "chdir", "execve"];
        Assert.Empty(calls.Except(allowed));
    }

    [Fact]
    public void NativePty_WithoutLibrary_FailsWithStableCode()
    {
        var r = NativePty.Spawn("/bin/sh", ["/bin/sh"], ["PATH=/bin"], "/", 24, 80);
        Assert.False(r.Ok);
        Assert.Equal(NativeErrorCodes.NativeUnavailable, r.Code);
    }
}
