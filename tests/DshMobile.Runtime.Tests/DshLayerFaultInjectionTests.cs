using System.Text;
using DshMobile.Runtime.Layers;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// P0-02: the DSH layer swap under injected faults. Whatever fails — the rename aside, the placement (rename and the
/// copy fallback with a full disk), the restore, or the process dying at any single step — a runnable layer survives:
/// the old one, or the new one once it was committed. The legacy <c>DshLayer</c> deleted the old layer as soon as
/// <c>previous/</c> was missing and lost both layers when the placement then failed.
/// </summary>
public sealed class DshLayerFaultInjectionTests : IDisposable
{
    private const string OldBin = "node_modules/@deepseek-ai/dsh/lib/bin.js";
    private readonly string _dir = Directory.CreateTempSubdirectory("dshm-dshfault-").FullName;
    private int _n;

    public void Dispose() => LayerInstaller.DeleteTree(_dir);

    private static string Bundle => Repo.PathOf("tests", "TestVectors", "bundles", "dsh-good.tar");

    /// <summary>files/ with a runnable 1.0.0 layer (and optionally a <c>previous/</c> slot) plus user data.</summary>
    private string Files(bool withPrevious)
    {
        var files = Path.Combine(_dir, "files-" + _n++);
        var dsh = Path.Combine(files, "runtime", "runtime-current", "dsh");
        Directory.CreateDirectory(Path.Combine(dsh, Path.GetDirectoryName(OldBin)!));
        File.WriteAllText(Path.Combine(dsh, OldBin), "old-bin\n");
        Directory.CreateDirectory(Path.Combine(dsh, ".dshmobile"));
        File.WriteAllText(Path.Combine(dsh, ".dshmobile", "version"), "1.0.0");
        File.WriteAllText(Path.Combine(dsh, "old.txt"), "old-dsh\n");
        if (withPrevious)
        {
            Directory.CreateDirectory(Path.Combine(files, "runtime", "runtime-current", "previous", "dsh"));
            File.WriteAllText(Path.Combine(files, "runtime", "runtime-current", "previous", "dsh", "prev.txt"), "older\n");
        }
        Directory.CreateDirectory(Path.Combine(files, "user-data", ".dsh"));
        File.WriteAllText(Path.Combine(files, "user-data", ".dsh", "credentials.yaml"), "secret");
        return files;
    }

    private static DshLayerInstaller Installer(string files, LayerFileOps? ops = null) =>
        new(new RuntimeLayout(files, null, "/lib"), LayerExtractor.AndroidLikeHost) { Ops = ops ?? LayerFileOps.Real };

    private static SortedDictionary<string, string> Snapshot(string root)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            map[rel] = Directory.Exists(path) ? "dir" : Encoding.UTF8.GetString(File.ReadAllBytes(path));
        }
        return map;
    }

    /// <summary>The swap left no transaction state behind.</summary>
    private static void AssertSettled(DshLayerInstaller i)
    {
        Assert.False(LayerInstaller.Exists(i.AsideDir), "aside left");
        Assert.False(File.Exists(i.JournalFile), "journal left");
        Assert.False(LayerInstaller.Exists(i.StagingDir), "staging left");
    }

    private static Task<DshLayerResult> Install(DshLayerInstaller i) =>
        i.InstallFromFileAsync(Bundle, null, "1.2.0", allowDowngrade: false, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveAsideRefused_NothingChanges(bool withPrevious)
    {
        var files = Files(withPrevious);
        var before = Snapshot(files);
        var real = LayerFileOps.Real;
        var i = Installer(files, new LayerFileOps { Rename = (a, b) => !a.EndsWith("dsh", StringComparison.Ordinal) && real.Rename(a, b) });
        var r = await Install(i);
        Assert.Equal(DshLayerErrorCodes.InstallFailed, r.Code);
        Assert.False(r.Changed);
        Assert.Equal("1.0.0", r.Version);
        Assert.Contains("nothing was changed", r.Message, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(files));
        AssertSettled(i);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlacementFails_RenameRefusedAndDiskFullDuringCopy_OldLayerKept(bool withPrevious)
    {
        var files = Files(withPrevious);
        var before = Snapshot(files);
        var real = LayerFileOps.Real;
        var i = Installer(files, new LayerFileOps
        {
            Rename = (a, b) => !a.EndsWith("dshmobile-dsh-staging", StringComparison.Ordinal) && real.Rename(a, b),
            CopyTree = (src, dest) =>
            {
                // Half a layer lands, then the disk is full.
                Directory.CreateDirectory(Path.Combine(dest, "node_modules"));
                File.WriteAllText(Path.Combine(dest, "node_modules", "partial"), "x");
                throw new IOException("No space left on device");
            },
        });
        var r = await Install(i);
        Assert.Equal(DshLayerErrorCodes.InstallFailed, r.Code);
        Assert.Contains("the previous layer was kept", r.Message, StringComparison.Ordinal);
        Assert.Equal("1.0.0", r.Version);
        Assert.Equal(before, Snapshot(files));
        AssertSettled(i);
    }

    [Fact]
    public async Task PlacementAndRestoreFail_JournalKept_NextInstallRecoversFirst()
    {
        var files = Files(withPrevious: false);
        var before = Snapshot(files);
        var real = LayerFileOps.Real;
        var i = Installer(files, new LayerFileOps
        {
            Rename = (a, b) => !a.EndsWith("dshmobile-dsh-staging", StringComparison.Ordinal) && !a.EndsWith("dshmobile-dsh-old", StringComparison.Ordinal) && real.Rename(a, b),
            CopyTree = (_, _) => throw new IOException("No space left on device"),
        });
        var r = await Install(i);
        Assert.Equal(DshLayerErrorCodes.InstallFailed, r.Code);
        Assert.Contains("restoring the previous layer failed", r.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(i.JournalFile));
        Assert.True(File.Exists(Path.Combine(i.AsideDir, OldBin)), "the old layer is still on disk, aside");

        // Reads alone never repair; the next install (or start) does, before deciding anything.
        var healthy = Installer(files);
        Assert.Equal(new DshLayerRecovery(true, "restored", null), healthy.Recover());
        Assert.Equal(before, Snapshot(files));
        Assert.Equal("1.0.0", healthy.InstalledVersion());
        AssertSettled(healthy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldLayer_IsRetiredOnlyAfterTheNewOneIsInPlace(bool withPrevious)
    {
        var files = Files(withPrevious);
        var calls = new List<string>();
        var real = LayerFileOps.Real;
        string Rel(string p) => Path.GetRelativePath(Path.Combine(files, "runtime", "runtime-current"), p).Replace('\\', '/');
        var i = Installer(files, new LayerFileOps
        {
            Rename = (a, b) => { calls.Add($"mv {Rel(a)} {Rel(b)}"); return real.Rename(a, b); },
            DeleteTree = p => { if (Directory.Exists(p) || File.Exists(p)) { calls.Add($"rm {Rel(p)}"); } real.DeleteTree(p); },
        });
        var r = await Install(i);
        Assert.True(r.Ok, r.Message);
        Assert.Equal("1.2.0", r.Version);
        Assert.Equal(withPrevious
            ? ["mv dsh dshmobile-dsh-old", "mv dshmobile-dsh-staging dsh", "rm previous/dsh", "mv dshmobile-dsh-old previous/dsh"]
            : ["mv dsh dshmobile-dsh-old", "mv dshmobile-dsh-staging dsh", "rm dshmobile-dsh-old"], calls);
        Assert.Equal(withPrevious, File.Exists(Path.Combine(i.PreviousDshDir, "old.txt")));
        Assert.Equal(withPrevious, Directory.Exists(Path.GetDirectoryName(i.PreviousDshDir))); // previous/ is never created
        AssertSettled(i);
    }

    private sealed class SimulatedCrash : Exception
    {
        public SimulatedCrash() : base("process died")
        {
        }
    }

    /// <summary>Ops that die at the <paramref name="at"/>-th mutating call and stay dead (the process is gone).</summary>
    private static LayerFileOps DyingAt(int at, Action? onCall = null)
    {
        var real = LayerFileOps.Real;
        var n = 0;
        void Tick()
        {
            onCall?.Invoke();
            if (n++ >= at)
            {
                throw new SimulatedCrash();
            }
        }
        return new LayerFileOps
        {
            Rename = (a, b) => { Tick(); return real.Rename(a, b); },
            CopyTree = (a, b) => { Tick(); real.CopyTree(a, b); },
            DeleteTree = p => { Tick(); real.DeleteTree(p); },
            WriteFile = (p, c) => { Tick(); real.WriteFile(p, c); },
            DeleteFile = p => { Tick(); real.DeleteFile(p); },
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessDeathAtEveryStep_RecoveryLeavesTheOldOrTheNewLayer_NeverNone(bool withPrevious)
    {
        // How many mutating calls a clean install makes.
        var count = 0;
        var probe = Files(withPrevious);
        Assert.True((await Install(Installer(probe, DyingAt(int.MaxValue, () => count++)))).Ok);
        var installed = Snapshot(probe);
        Assert.True(count >= 6, $"only {count} mutating steps");

        var outcomes = new List<string>();
        for (var k = 0; k < count; k++)
        {
            var files = Files(withPrevious);
            var before = Snapshot(files);
            await Assert.ThrowsAsync<SimulatedCrash>(() => Install(Installer(files, DyingAt(k))));

            var healthy = Installer(files);
            var recovery = healthy.Recover();
            Assert.True(recovery.Ok, $"step {k}: {recovery.Message}");
            AssertSettled(healthy);
            var version = healthy.InstalledVersion();
            Assert.True(File.Exists(Path.Combine(healthy.DshDir, OldBin)), $"step {k}: no runnable layer");
            Assert.Equal(version == "1.0.0" ? before : installed, Snapshot(files));
            Assert.Equal("secret", File.ReadAllText(Path.Combine(files, "user-data", ".dsh", "credentials.yaml")));
            outcomes.Add($"{k}:{version}");

            // ...and the next install goes through.
            var again = await Install(healthy);
            Assert.True(again.Ok, $"step {k}: {again.Message}");
            Assert.Equal(installed, Snapshot(files));
        }
        Assert.Contains(outcomes, o => o.EndsWith(":1.0.0", StringComparison.Ordinal));
        Assert.Contains(outcomes, o => o.EndsWith(":1.2.0", StringComparison.Ordinal));
    }

    [Fact]
    public void Recover_WithoutAJournal_TouchesNothing_ButPutsBackAnOrphanedAsideLayer()
    {
        var files = Files(withPrevious: false);
        var before = Snapshot(files);
        var i = Installer(files);
        Assert.Equal(new DshLayerRecovery(true, "none", null), i.Recover());
        Assert.Equal(before, Snapshot(files));

        Directory.Move(i.DshDir, i.AsideDir);
        Assert.Null(i.InstalledVersion());
        Assert.Equal("restored", i.Recover().Action);
        Assert.Equal(before, Snapshot(files));
    }

    [Fact]
    public async Task FreshInstall_PlacementFails_LeavesNoHalfLayer()
    {
        var files = Path.Combine(_dir, "fresh");
        Directory.CreateDirectory(Path.Combine(files, "runtime", "runtime-current"));
        var real = LayerFileOps.Real;
        var i = Installer(files, new LayerFileOps
        {
            Rename = (a, b) => !a.EndsWith("dshmobile-dsh-staging", StringComparison.Ordinal) && real.Rename(a, b),
            CopyTree = (_, dest) =>
            {
                Directory.CreateDirectory(Path.Combine(dest, "node_modules"));
                throw new IOException("No space left on device");
            },
        });
        var r = await Install(i);
        Assert.Equal(DshLayerErrorCodes.InstallFailed, r.Code);
        Assert.False(i.IsInstalled);
        Assert.False(Directory.Exists(i.DshDir));
        AssertSettled(i);
    }
}
