using System.Text.RegularExpressions;
using DshMobile.Runtime.Guest;
using DshMobile.Runtime.Hosting;
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

    /// <summary>
    /// 真机向量是 legacy 抓包基线；生产按 E6-1 结论固定多一个 --preserve-symlinks。
    /// 这里只把这一处已登记差异叠加到历史的 dshWeb 向量上，向量文件本身保持真机原样。
    /// </summary>
    private static List<string> ExpectedDshWebArgv(string files, string native)
    {
        var argv = Golden("dshWeb", files, native);
        var exposeIndex = argv.IndexOf("--expose-internals");
        Assert.True(exposeIndex >= 0, "dshWeb 向量必须包含 --expose-internals");
        argv.Insert(exposeIndex + 1, "--preserve-symlinks");
        return argv;
    }

    /// <summary>On Windows, Path.Combine joins with '\'; normalize both sides to '/' for comparison only.</summary>
    private static List<string> Norm(IEnumerable<string> argv) => argv.Select(a => a.Replace('\\', '/')).ToList();

    [Fact]
    public void SandboxCommand_EqualsDeviceArgv()
    {
        var (layout, files, native) = DeviceLikeLayout();
        Assert.Equal(Norm(Golden("sandboxKeepalive", files, native)), Norm(new ProotCommandBuilder(layout).SandboxCommand()));
    }

    [Fact]
    public void DshCommand_FirstStart_EqualsDeviceArgv_PlusPreserveSymlinks()
    {
        var (layout, files, native) = DeviceLikeLayout();
        Assert.Equal(Norm(ExpectedDshWebArgv(files, native)), Norm(new ProotCommandBuilder(layout).DshCommand()));
    }

    /// <summary>防止把生产差异写回真机抓包：向量必须仍以 --expose-internals 紧接 --import。</summary>
    [Fact]
    public void DeviceArgvVector_RemainsLegacyCapture_WithoutPreserveSymlinks()
    {
        var raw = Golden("dshWeb", "files", "native");
        var exposeIndex = raw.IndexOf("--expose-internals");

        Assert.True(exposeIndex >= 0);
        Assert.DoesNotContain("--preserve-symlinks", raw);
        Assert.Equal("--import", raw[exposeIndex + 1]);
    }

    [Fact]
    public void DshCommand_InPlaceRestart_PreservesGuestCacheBind()
    {
        var (layout, _, _) = DeviceLikeLayout();
        var b = new ProotCommandBuilder(layout);
        var first = b.DshCommand();
        var restart = b.DshCommand(inPlaceRestart: true);
        Assert.Equal(first, restart);
        Assert.Contains(restart, a => a.EndsWith(":/opt/dshmobile-cache", StringComparison.Ordinal));
    }

    [Fact]
    public void DshCommand_WithoutShimFile_HasNoShimBindOrImport()
    {
        var (layout, _, _) = DeviceLikeLayout();
        File.Delete(layout.ShimFile);
        var argv = new ProotCommandBuilder(layout).DshCommand();
        Assert.DoesNotContain(argv, a => a.EndsWith(":/opt/dshmobile", StringComparison.Ordinal));
        Assert.DoesNotContain("--import", argv);
    }

    [Fact]
    public void DshCommand_WithStartupFsCacheShim_AddsSecondImportAfterLinkShim_AndKeepsOnInPlaceRestart()
    {
        var (layout, _, _) = DeviceLikeLayout();
        var cacheShimHost = GuestAssetProvisioner.StartupFsCacheShimHostPath(layout);
        Directory.CreateDirectory(Path.GetDirectoryName(cacheShimHost)!);
        File.WriteAllText(cacheShimHost, "// fs cache shim");

        var builder = new ProotCommandBuilder(layout);
        var first = builder.DshCommand();
        var linkImportIdx = first.ToList().IndexOf(RuntimeConstants.LinkShimGuestPath);
        var cacheImportIdx = first.ToList().IndexOf(GuestAssetProvisioner.GuestStartupFsCacheShim);
        var entryIdx = first.ToList().IndexOf(RuntimeConstants.GuestDshBin);

        Assert.True(linkImportIdx > 0);
        Assert.Equal(linkImportIdx + 2, cacheImportIdx);
        Assert.Equal("--import", first[cacheImportIdx - 1]);
        Assert.True(cacheImportIdx < entryIdx);

        // PR-01 (N-01-01): inPlaceRestart preserves guest-cache and startup-fs-cache-shim.mjs.
        var restart = builder.DshCommand(inPlaceRestart: true);
        Assert.Contains(GuestAssetProvisioner.GuestStartupFsCacheShim, restart);
        Assert.Contains(RuntimeConstants.LinkShimGuestPath, restart);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DshCommand_TrimmedWeb_PassesTrimPatchBeforeOverlays_AndNoOpenAtEnd(bool inPlaceRestart)
    {
        var (layout, _, _) = DeviceLikeLayout();
        var trimHost = GuestAssetProvisioner.WebTrimPatchHostPath(layout);
        Directory.CreateDirectory(Path.GetDirectoryName(trimHost)!);
        File.WriteAllText(trimHost, "- id: modules\n  disabled: true\n");

        var uiOverlay = Path.Combine(layout.UserDataDir, ".dsh/dshmobile/dsh-official-plugin/dsh-official-plugin-overlay.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(uiOverlay)!);
        File.WriteAllText(uiOverlay, "- insert: []\n");

        var argv = new ProotCommandBuilder(layout).DshCommand(inPlaceRestart: inPlaceRestart, trimmedWeb: true).ToList();
        var patches = argv.Select((a, i) => (a, i)).Where(t => t.a == "--patch").Select(t => argv[t.i + 1]).ToList();
        Assert.Equal(
            [GuestAssetProvisioner.GuestWebTrimPatch, "/root/projects/.dsh/dshmobile/dsh-official-plugin/dsh-official-plugin-overlay.yml"],
            patches);
        Assert.Equal("--no-open", argv[^1]);
        // 原地重启也必须保留 guest-cache 挂载，否则重启后裁剪层与 ui_* 脚本都会读不到。
        Assert.Contains(argv, a => a.EndsWith(":/opt/dshmobile-cache", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DshHost_BuildDshArgv_KeepsTrimmedWeb_OnFirstStartAndInPlaceRestart(bool inPlaceRestart)
    {
        var (layout, _, _) = DeviceLikeLayout();
        var trimHost = GuestAssetProvisioner.WebTrimPatchHostPath(layout);
        Directory.CreateDirectory(Path.GetDirectoryName(trimHost)!);
        File.WriteAllText(trimHost, "- id: modules\n  disabled: true\n");

        var argv = DshHost.BuildDshArgv(
            new ProotCommandBuilder(layout),
            new DshHostOptions(TrimmedWeb: true),
            inPlaceRestart).ToList();

        var patchIndex = argv.IndexOf("--patch");
        Assert.True(patchIndex > 0);
        Assert.Equal(GuestAssetProvisioner.GuestWebTrimPatch, argv[patchIndex + 1]);
        Assert.Equal("--no-open", argv[^1]);
        Assert.Contains(argv, a => a.EndsWith(":/opt/dshmobile-cache", StringComparison.Ordinal));
    }

    [Fact]
    public void DshHost_BuildDshArgv_WithoutTrimmedWeb_HasNoTrimPatchOrNoOpen()
    {
        var (layout, _, _) = DeviceLikeLayout();

        var argv = DshHost.BuildDshArgv(new ProotCommandBuilder(layout), new DshHostOptions(), inPlaceRestart: false);

        Assert.DoesNotContain(GuestAssetProvisioner.GuestWebTrimPatch, argv);
        Assert.DoesNotContain("--no-open", argv);
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
        Overlay(".dsh/dshmobile/safe-mode/absolute-safe-mode.yml", "x: 1");
        Overlay(".dsh/dshmobile/plugin-market/market-and-safe-mode-overlay.yml", ""); // empty: skipped
        Overlay(".dsh/dshmobile/dsh-official-plugin/dsh-official-plugin-overlay.yml", "y: 2");
        var argv = new ProotCommandBuilder(layout).DshCommand();
        var patches = argv.Select((a, i) => (a, i)).Where(t => t.a == "--patch").Select(t => argv[t.i + 1]).ToList();
        Assert.Equal(["/root/projects/.dsh/dshmobile/dsh-official-plugin/dsh-official-plugin-overlay.yml", "/root/projects/.dsh/dshmobile/safe-mode/absolute-safe-mode.yml"], patches);
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
