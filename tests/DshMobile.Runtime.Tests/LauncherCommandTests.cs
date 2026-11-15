using System.Text.RegularExpressions;
using DshMobile.Runtime.Guest;
using DshMobile.Runtime.Supervision;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// The C# sandbox / DSH launch commands against the REAL argv the legacy runtime used on a device
/// (tests/TestVectors/device-proot-cmdlines.json, read from /proc/&lt;pid&gt;/cmdline), plus the legacy source shape.
/// </summary>
public sealed partial class LauncherCommandTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dshm-launch-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A files/ tree shaped like the device's at capture time (node + dsh layers, shim, browser-tools).</summary>
    private (RuntimeLayout Layout, string Files, string Native) DeviceLikeLayout()
    {
        var files = Path.Combine(_dir, "files");
        var native = Path.Combine(_dir, "lib", "arm64");
        Directory.CreateDirectory(native);
        File.WriteAllText(Path.Combine(native, "libproot.so"), "");
        var layout = new RuntimeLayout(files, Path.Combine(_dir, "cache"), native);
        Directory.CreateDirectory(layout.LayerDir("node"));
        Directory.CreateDirectory(layout.LayerDir("dsh"));
        Directory.CreateDirectory(layout.ShimDir);
        File.WriteAllText(layout.ShimFile, "// shim");
        Directory.CreateDirectory(layout.BrowserEntryDir);
        return (layout, files, native);
    }

    private static List<string> Golden(string name, string files, string native) =>
        Repo.Vectors("device-proot-cmdlines.json").GetProperty(name).EnumerateArray()
            .Select(a => a.GetString()!.Replace("@FILES@", files, StringComparison.Ordinal).Replace("@NATIVE@", native, StringComparison.Ordinal))
            .Select(a => OperatingSystem.IsWindows() ? a.Replace(files.Replace('\\', '/'), files, StringComparison.Ordinal) : a)
            .ToList();

    /// <summary>On Windows, Path.Combine joins with '\'; normalize both sides to '/' for comparison only.</summary>
    private static List<string> Norm(IEnumerable<string> argv) => argv.Select(a => a.Replace('\\', '/')).ToList();

    [Fact]
    public void SandboxCommand_EqualsDeviceArgv()
    {
        var (layout, files, native) = DeviceLikeLayout();
        Assert.Equal(Norm(Golden("sandboxKeepalive", files, native)), Norm(new ProotCommandBuilder(layout).SandboxCommand()));
    }

    [Fact]
    public void DshCommand_FirstStart_EqualsDeviceArgv()
    {
        var (layout, files, native) = DeviceLikeLayout();
        Assert.Equal(Norm(Golden("dshWeb", files, native)), Norm(new ProotCommandBuilder(layout).DshCommand()));
    }

    [Fact]
    public void DshCommand_InPlaceRestart_DropsOnlyTheGuestCacheBind()
    {
        var (layout, _, _) = DeviceLikeLayout();
        var b = new ProotCommandBuilder(layout);
        var first = b.DshCommand();
        var restart = b.DshCommand(inPlaceRestart: true);
        Assert.Equal(first.Where(a => !a.EndsWith(":/opt/dshbox-cache", StringComparison.Ordinal)), restart);
        Assert.Equal(first.Count - 1, restart.Count);
    }

    [Fact]
    public void DshCommand_WithoutShimFile_HasNoShimBindOrImport()
    {
        var (layout, _, _) = DeviceLikeLayout();
        File.Delete(layout.ShimFile);
        var argv = new ProotCommandBuilder(layout).DshCommand();
        Assert.DoesNotContain(argv, a => a.EndsWith(":/opt/dshbox", StringComparison.Ordinal));
        Assert.DoesNotContain("--import", argv);
    }

    [Fact]
    public void DshCommand_PassesOnlyNonEmptyOverlays_InLegacyOrder()
    {
        var (layout, _, _) = DeviceLikeLayout();
        void Overlay(string rel, string content)
        {
            var p = Path.Combine(layout.UserDataDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, content);
        }
        Overlay(".dsh/dshbox/safe-mode/absolute-safe-mode.yml", "x: 1");
        Overlay(".dsh/dshbox/plugin-market/market-and-safe-mode-overlay.yml", ""); // empty: skipped
        Overlay(".dsh/dshbox/dsh-official-plugin/dsh-official-plugin-overlay.yml", "y: 2");
        var argv = new ProotCommandBuilder(layout).DshCommand();
        var patches = argv.Select((a, i) => (a, i)).Where(t => t.a == "--patch").Select(t => argv[t.i + 1]).ToList();
        Assert.Equal(["/root/projects/.dsh/dshbox/dsh-official-plugin/dsh-official-plugin-overlay.yml", "/root/projects/.dsh/dshbox/safe-mode/absolute-safe-mode.yml"], patches);
    }

    [Fact]
    public void Argv_ReadsExactNulSeparatedTokens()
    {
        var pid = Path.Combine(_dir, "proc", "42");
        Directory.CreateDirectory(pid);
        File.WriteAllText(Path.Combine(pid, "stat"), "42 (x) S 1 0");
        File.WriteAllText(Path.Combine(pid, "cmdline"), "/usr/bin/bash\0-c\0echo a b; true\0");
        Assert.Equal(["/usr/bin/bash", "-c", "echo a b; true"], ProcessTable.Read(Path.Combine(_dir, "proc"))!.Argv(42));
    }

}
