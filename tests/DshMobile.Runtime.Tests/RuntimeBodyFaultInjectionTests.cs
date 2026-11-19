using System.Text;
using DshMobile.Runtime.Layers;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// The runtime body swap (<c>base</c>, <c>node</c>, <c>android-side</c>, <c>runtime-profile.json</c>) under injected
/// faults. Whatever fails — moving the old body aside, placing the new layers (rename and the copy fallback with a full
/// disk), writing the profile, the restore, or the process dying at any single step — a usable body survives: the old one,
/// or the new one once it was committed. The legacy <c>swapRuntimeBody</c> deleted every current layer as soon as
/// <c>previous/</c> was missing, before the new layers were moved in, and lost the whole body when placing them then failed.
/// The end trees of successful imports stay those of install-scenarios.json (<see cref="LayerInstallerParityTests"/>).
/// </summary>
public sealed class RuntimeBodyFaultInjectionTests : IDisposable
{
    private const string OldProfile = "old-profile\n";
    private readonly string _dir = Directory.CreateTempSubdirectory("dshm-bodyfault-").FullName;
    private int _n;

    public void Dispose() => LayerInstaller.DeleteTree(_dir);

    private static string Bundle => Repo.PathOf("tests", "TestVectors", "bundles", "good.zip");

    /// <summary>files/ with an old body, a DSH layer and user data (the install-scenarios.json "replace" trees).</summary>
    private string Files(bool withPrevious)
    {
        var files = Path.Combine(_dir, "files-" + _n++);
        var rc = Path.Combine(files, "runtime", "runtime-current");
        foreach (var layer in (string[])["base", "node", "android-side"])
        {
            Directory.CreateDirectory(Path.Combine(rc, layer));
            File.WriteAllText(Path.Combine(rc, layer, "old.txt"), $"old-{layer}\n");
        }
        File.WriteAllText(Path.Combine(rc, "runtime-profile.json"), OldProfile);
        Directory.CreateDirectory(Path.Combine(rc, "dsh"));
        File.WriteAllText(Path.Combine(rc, "dsh", "dsh.txt"), "dsh\n");
        if (withPrevious)
        {
            Directory.CreateDirectory(Path.Combine(rc, "previous", "base"));
            File.WriteAllText(Path.Combine(rc, "previous", "base", "older.txt"), "older-base\n");
            Directory.CreateDirectory(Path.Combine(rc, "previous", "dsh"));
            File.WriteAllText(Path.Combine(rc, "previous", "dsh", "prev-dsh.txt"), "prev-dsh\n");
            File.WriteAllText(Path.Combine(rc, "previous", "runtime-profile.json"), "older-profile\n");
        }
        Directory.CreateDirectory(Path.Combine(files, "user-data", ".dsh"));
        File.WriteAllText(Path.Combine(files, "user-data", ".dsh", "credentials.yaml"), "secret");
        return files;
    }

    private static LayerInstaller Installer(string files, LayerFileOps? ops = null) =>
        new(new RuntimeLayout(files, null, "/lib"), LayerExtractor.AndroidLikeHost) { Ops = ops ?? LayerFileOps.Real };

    private static string Rc(string files) => Path.Combine(files, "runtime", "runtime-current");

    private static SortedDictionary<string, string> Snapshot(string root, bool withoutPrevious = false)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (withoutPrevious && rel.StartsWith("runtime/runtime-current/previous", StringComparison.Ordinal))
            {
                continue;
            }
            map[rel] = Directory.Exists(path) ? "dir" : Encoding.UTF8.GetString(File.ReadAllBytes(path));
        }
        return map;
    }

    /// <summary>The swap left no transaction state behind.</summary>
    private static void AssertSettled(LayerInstaller i)
    {
        Assert.False(LayerInstaller.Exists(i.AsideDir), "aside left");
        Assert.False(File.Exists(i.JournalFile), "journal left");
        Assert.False(LayerInstaller.Exists(i.StagingDir), "staging left");
    }

    private static Task<LayerInstallResult> Install(LayerInstaller i) => i.InstallAsync(Bundle, null, CancellationToken.None);

    private static bool IsStaged(LayerInstaller i, string path) =>
        path.StartsWith(i.StagingDir + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveAsideRefused_ForTheSecondLayer_FirstIsPutBack_NothingChanges(bool withPrevious)
    {
        var files = Files(withPrevious);
        var before = Snapshot(files);
        var real = LayerFileOps.Real;
        LayerInstaller i = null!;
        i = Installer(files, new LayerFileOps
        {
            Rename = (a, b) => !(Path.GetFileName(a) == "node" && b.StartsWith(i.AsideDir, StringComparison.Ordinal)) && real.Rename(a, b),
        });
        var r = await Install(i);
        Assert.Equal(LayerInstaller.ImportFailed, r.Code);
        Assert.Contains("(node) aside; nothing was changed", r.Message, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(files));
        AssertSettled(i);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlacementFails_RenameRefusedAndDiskFullDuringCopy_OldBodyKept(bool withPrevious)
    {
        var files = Files(withPrevious);
        var before = Snapshot(files);
        var real = LayerFileOps.Real;
        LayerInstaller i = null!;
        i = Installer(files, new LayerFileOps
        {
            Rename = (a, b) => !IsStaged(i, a) && real.Rename(a, b),
            CopyTree = (src, dest) =>
            {
                if (Path.GetFileName(src) == "base")
                {
                    real.CopyTree(src, dest); // base lands completely...
                    return;
                }
                // ...half of node does, then the disk is full.
                Directory.CreateDirectory(Path.Combine(dest, "bin"));
                File.WriteAllText(Path.Combine(dest, "bin", "partial"), "x");
                throw new IOException("No space left on device");
            },
        });
        var r = await Install(i);
        Assert.Equal(LayerInstaller.ImportFailed, r.Code);
        Assert.Contains("Cannot place the new node layer", r.Message, StringComparison.Ordinal);
        Assert.Contains("the previous runtime body was kept", r.Message, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(files));
        AssertSettled(i);
    }

    [Fact]
    public async Task ProfileWriteFails_DiskFull_OldBodyKept()
    {
        var files = Files(withPrevious: false);
        var before = Snapshot(files);
        var real = LayerFileOps.Real;
        var i = Installer(files, new LayerFileOps
        {
            WriteFile = (p, c) =>
            {
                if (Path.GetFileName(p) == "runtime-profile.json")
                {
                    throw new IOException("No space left on device");
                }
                real.WriteFile(p, c);
            },
        });
        var r = await Install(i);
        Assert.Equal(LayerInstaller.ImportFailed, r.Code);
        Assert.Contains("No space left on device; the previous runtime body was kept", r.Message, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(files));
        AssertSettled(i);
    }

    [Fact]
    public async Task PlacementAndRestoreFail_JournalKept_NextRecoveryRestores()
    {
        var files = Files(withPrevious: false);
        var before = Snapshot(files);
        var real = LayerFileOps.Real;
        LayerInstaller i = null!;
        i = Installer(files, new LayerFileOps
        {
            Rename = (a, b) => !IsStaged(i, a) && !a.StartsWith(i.AsideDir, StringComparison.Ordinal) && real.Rename(a, b),
            CopyTree = (_, _) => throw new IOException("No space left on device"),
        });
        var r = await Install(i);
        Assert.Equal(LayerInstaller.ImportFailed, r.Code);
        Assert.Contains("restoring the previous body failed", r.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(i.JournalFile));
        Assert.Equal("old-base\n", File.ReadAllText(Path.Combine(i.AsideDir, "base", "old.txt"))); // still on disk, aside

        // The next import (or start / rollback) recovers first.
        var healthy = Installer(files);
        Assert.Equal(new RuntimeBodyRecovery(true, "restored", null), healthy.Recover());
        Assert.Equal(before, Snapshot(files));
        AssertSettled(healthy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldBody_IsRetiredOnlyAfterTheNewOneIsInPlace(bool withPrevious)
    {
        var files = Files(withPrevious);
        var calls = new List<string>();
        var real = LayerFileOps.Real;
        var rc = Rc(files);
        LayerInstaller i = null!;
        string Rel(string p) => IsStaged(i, p) ? "staging/" + Path.GetFileName(p) : Path.GetRelativePath(rc, p).Replace('\\', '/');
        i = Installer(files, new LayerFileOps
        {
            Rename = (a, b) => { calls.Add($"mv {Rel(a)} {Rel(b)}"); return real.Rename(a, b); },
            DeleteTree = p => { if (LayerInstaller.Exists(p) && p != i.StagingDir) { calls.Add($"rm {Rel(p)}"); } real.DeleteTree(p); },
            WriteFile = (p, c) => { calls.Add($"write {Rel(p)}" + (p == i.JournalFile ? " " + c.Split('\n')[0] : "")); real.WriteFile(p, c); },
            DeleteFile = p => { calls.Add($"rm {Rel(p)}"); real.DeleteFile(p); },
        });
        var r = await Install(i);
        Assert.True(r.Ok, r.Message);
        Assert.Null(r.Message);
        string[] head =
        [
            "write dshmobile-body-install.journal phase=begin",
            "mv base dshmobile-body-old/base", "mv node dshmobile-body-old/node", "mv android-side dshmobile-body-old/android-side",
            "mv runtime-profile.json dshmobile-body-old/runtime-profile.json",
            "mv staging/base base", "mv staging/node node", "mv staging/android-side android-side",
            "write runtime-profile.json",
            "write dshmobile-body-install.journal phase=placed",
        ];
        string[] retire = withPrevious
            ?
            [
                "rm previous/base", "mv dshmobile-body-old/base previous/base", "mv dshmobile-body-old/node previous/node",
                "mv dshmobile-body-old/android-side previous/android-side",
                "rm previous/runtime-profile.json", "mv dshmobile-body-old/runtime-profile.json previous/runtime-profile.json",
            ]
            :
            [
                "rm dshmobile-body-old/base", "rm dshmobile-body-old/node", "rm dshmobile-body-old/android-side",
                "rm dshmobile-body-old/runtime-profile.json",
            ];
        Assert.Equal([.. head, .. retire, "rm dshmobile-body-old", "rm dshmobile-body-install.journal"], calls);
        Assert.Equal(withPrevious, Directory.Exists(Path.Combine(rc, "previous"))); // previous/ is never created
        Assert.Equal(withPrevious, File.Exists(Path.Combine(rc, "previous", "base", "old.txt")));
        Assert.Equal("dsh\n", File.ReadAllText(Path.Combine(rc, "dsh", "dsh.txt")));
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
    public async Task ProcessDeathAtEveryStep_RecoveryLeavesTheOldOrTheNewBody_NeverNone(bool withPrevious)
    {
        // How many mutating calls a clean import makes.
        var count = 0;
        var probe = Files(withPrevious);
        Assert.True((await Install(Installer(probe, DyingAt(int.MaxValue, () => count++)))).Ok);
        var installed = Snapshot(probe);
        Assert.True(count >= 12, $"only {count} mutating steps");

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
            var rc = Rc(files);
            var profile = File.ReadAllText(Path.Combine(rc, "runtime-profile.json"));
            var old = profile == OldProfile;
            Assert.True(File.Exists(Path.Combine(rc, "base", old ? "old.txt" : "etc/os-release")), $"step {k}: no usable base");
            Assert.Equal(old ? before : installed, Snapshot(files));
            Assert.Equal("secret", File.ReadAllText(Path.Combine(files, "user-data", ".dsh", "credentials.yaml")));
            outcomes.Add($"{k}:{(old ? "old" : "new")}:{recovery.Action}");

            // ...and the next import goes through (with previous/, that one now keeps the body it replaced).
            var again = await Install(healthy);
            Assert.True(again.Ok, $"step {k}: {again.Message}");
            AssertSettled(healthy);
            Assert.Equal(Snapshot(probe, withoutPrevious: true), Snapshot(files, withoutPrevious: true));
        }
        Assert.Contains(outcomes, o => o.Contains(":old:restored", StringComparison.Ordinal));
        Assert.Contains(outcomes, o => o.Contains(":new:completed", StringComparison.Ordinal));
    }

    [Fact]
    public void Recover_WithoutAJournal_TouchesNothing_ButPutsBackAnOrphanedAsideBody()
    {
        var files = Files(withPrevious: false);
        var before = Snapshot(files);
        var i = Installer(files);
        Assert.Equal(new RuntimeBodyRecovery(true, "none", null), i.Recover());
        Assert.Equal(before, Snapshot(files));

        Directory.CreateDirectory(i.AsideDir);
        Directory.Move(Path.Combine(Rc(files), "base"), Path.Combine(i.AsideDir, "base"));
        File.Move(Path.Combine(Rc(files), "runtime-profile.json"), Path.Combine(i.AsideDir, "runtime-profile.json"));
        Assert.Equal("restored", i.Recover().Action);
        Assert.Equal(before, Snapshot(files));
        AssertSettled(i);
    }

    [Fact]
    public async Task FreshInstall_PlacementFails_LeavesNoHalfBody()
    {
        var files = Path.Combine(_dir, "fresh");
        Directory.CreateDirectory(Rc(files));
        var real = LayerFileOps.Real;
        LayerInstaller i = null!;
        i = Installer(files, new LayerFileOps
        {
            Rename = (a, b) => !(IsStaged(i, a) && Path.GetFileName(a) == "android-side") && real.Rename(a, b),
            CopyTree = (_, dest) =>
            {
                Directory.CreateDirectory(Path.Combine(dest, "proot"));
                throw new IOException("No space left on device");
            },
        });
        var r = await Install(i);
        Assert.Equal(LayerInstaller.ImportFailed, r.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Rc(files))); // base and node were placed, then dropped again
        AssertSettled(i);
    }

    [Fact]
    public async Task UnrecoverableEarlierImport_RefusesTheNextOne()
    {
        var files = Files(withPrevious: false);
        var real = LayerFileOps.Real;
        LayerInstaller i = null!;
        i = Installer(files, new LayerFileOps
        {
            Rename = (a, b) => !IsStaged(i, a) && !a.StartsWith(i.AsideDir, StringComparison.Ordinal) && real.Rename(a, b),
            CopyTree = (_, _) => throw new IOException("No space left on device"),
        });
        Assert.False((await Install(i)).Ok);
        var again = await Install(i); // the restore still cannot rename: refused before anything else is touched
        Assert.Equal(LayerInstaller.ImportFailed, again.Code);
        Assert.StartsWith("an earlier interrupted runtime import could not be recovered", again.Message, StringComparison.Ordinal);
        Assert.Null(again.Inspection);
        Assert.True(File.Exists(i.JournalFile));
    }

    [Fact]
    public async Task Held_KeepsTheOldBodyAside_ThenCommitOrAbortSettlesIt_ADR0029()
    {
        var files = Files(withPrevious: false);
        var before = Snapshot(files);
        var i = Installer(files);
        var r = await i.InstallAsync(Bundle, null, CancellationToken.None, holdForProbe: true);
        Assert.True(r.Ok, r.Message);
        Assert.True(i.IsHeldForProbe);
        Assert.True(File.Exists(Path.Combine(i.AsideDir, "base", "old.txt")));
        Assert.Equal(new RuntimeBodyRecovery(true, "held", null), i.Recover(keepProbing: true));
        Assert.True(i.IsHeldForProbe);

        // 中止（或非持有探测的进程恢复）应完整恢复旧运行时主体。
        var aborted = i.AbortProbed();
        Assert.Equal(new RuntimeBodyRecovery(true, "restored", null), aborted);
        Assert.False(i.IsHeldForProbe);
        Assert.Equal(before, Snapshot(files));
        AssertSettled(i);

        // 再次暂存并提交，旧运行时主体应被退役。
        Assert.True((await i.InstallAsync(Bundle, null, CancellationToken.None, holdForProbe: true)).Ok);
        Assert.True(i.CommitProbed().Ok);
        Assert.False(i.IsHeldForProbe);
        Assert.True(File.Exists(Path.Combine(Rc(files), "base", "etc", "os-release")));
        AssertSettled(i);
    }
}
