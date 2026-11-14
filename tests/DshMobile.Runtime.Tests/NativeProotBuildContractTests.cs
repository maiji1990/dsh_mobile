using System.Text.RegularExpressions;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// ADR-0014 source contract for the in-repo PRoot build (native/proot): every source is pinned (https + SHA-256 +
/// exact version, AGENTS.md rule 10), the PowerShell and bash scripts stay in step, the compiled-in version follows
/// the lock, and the MSBuild switch swaps exactly the four DSHBox files. The binaries themselves are checked by the
/// build scripts (NEEDED, exports, alignment, loader entry), not here.
/// </summary>
public partial class NativeProotBuildContractTests
{
    private static string ProotDir => Path.Combine(Repo.Root, "native", "proot");

    private static List<string[]> LockEntries() =>
        File.ReadAllLines(Path.Combine(ProotDir, "sources.lock"))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('|'))
            .ToList();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256();

    [GeneratedRegex(@"^\d+(\.\d+)+$")]
    private static partial Regex ExactVersion();

    [Fact]
    public void SourcesLock_PinsEverySource()
    {
        var entries = LockEntries();
        Assert.Equal(["talloc", "libandroid-shmem", "proot"], entries.Select(e => e[0]));
        foreach (var e in entries)
        {
            Assert.Equal(6, e.Length);
            var (name, version, url, sha, top, license) = (e[0], e[1], e[2], e[3], e[4], e[5]);
            Assert.Matches(ExactVersion(), version);
            Assert.StartsWith("https://", url, StringComparison.Ordinal);
            Assert.Contains(version, url, StringComparison.Ordinal);
            Assert.Matches(Sha256(), sha);
            Assert.Equal($"{name}-{version}", top);
            Assert.False(string.IsNullOrWhiteSpace(license));
        }
    }

    [Fact]
    public void BuildScripts_AgreeWithEachOtherAndTheLock()
    {
        var ps = File.ReadAllText(Path.Combine(ProotDir, "build.ps1"));
        var sh = File.ReadAllText(Path.Combine(ProotDir, "build.sh"));
        var prootVersion = LockEntries().Single(e => e[0] == "proot")[1];
        string[] shared =
        [
            "27.0.12077973",
            $"-DVERSION=\"{prootVersion}\"",
            "-DARG_MAX=131072",
            "-DWITH_LIBANDROID_SHMEM",
            "-DPROOT_UNBUNDLE_LOADER=\"/data/data/com.termux/files/usr/libexec/proot\"",
            "-D_PATH_TMP=\"/data/data/com.termux/files/usr/tmp/\"",
            "compat/talloc", "termux-parity.h", "--version-script=exports.txt", "-fvisibility=hidden",
            "max-page-size=16384", "--rosegment", "-ltalloc", "-landroid-shmem", "-Wl,-rpath,$ORIGIN",
            "process_vm", "seccomp_filter", "pokedata_workaround", "talloc_lib_init", "SHA256SUMS",
        ];
        foreach (var token in shared)
        {
            Assert.True(ps.Replace('\\', '/').Contains(token, StringComparison.Ordinal), $"build.ps1 lacks {token}");
            Assert.True(sh.Contains(token, StringComparison.Ordinal), $"build.sh lacks {token}");
        }
        Assert.Contains("$api = 24", ps, StringComparison.Ordinal);
        Assert.Contains("api=24", sh, StringComparison.Ordinal);
    }

    [Fact]
    public void ProotSourceSwitch_ReplacesExactlyTheFourDshboxFiles()
    {
        var props = File.ReadAllText(Path.Combine(Repo.Root, "Directory.Build.props"));
        var m = Regex.Match(props, @"<DshProotSource Condition=""'\$\(DshProotSource\)' == ''"">(\w+)</DshProotSource>");
        Assert.True(m.Success, "DshProotSource default missing from Directory.Build.props");
        Assert.Contains(m.Groups[1].Value, new[] { "dshbox", "self" });

        var csproj = File.ReadAllText(Path.Combine(Repo.Root, "src", "DshMobile.NativeBinding", "DshMobile.NativeBinding.csproj"));
        foreach (var abi in new[] { "arm64-v8a", "x86_64" })
        {
            foreach (var lib in new[] { "libproot*.so", "libtalloc.so", "libandroid-shmem.so" })
            {
                Assert.Contains($@"jniLibs\{abi}\{lib}", csproj, StringComparison.Ordinal);
            }
            Assert.Contains($@"$(NativeArtifactsDir)proot\{abi}\*.so", csproj, StringComparison.Ordinal);
        }
    }
}
