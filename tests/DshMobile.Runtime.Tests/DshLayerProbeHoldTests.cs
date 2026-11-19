using System.Text;
using DshMobile.Runtime.Layers;
using DshMobile.Runtime.Updates;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// vNext PR-05: the DSH layer install held for the update health check (journal phase <c>probing</c>), the update state
/// record and the planner.
/// </summary>
public sealed class DshLayerProbeHoldTests : IDisposable
{
    private const string Bin = "node_modules/@deepseek-ai/dsh/lib/bin.js";
    private readonly string _dir = Directory.CreateTempSubdirectory("dshm-hold-").FullName;
    private int _n;

    public void Dispose() => LayerInstaller.DeleteTree(_dir);

    private static string Bundle => Repo.PathOf("tests", "TestVectors", "bundles", "dsh-good.tar");

    private string Files()
    {
        var files = Path.Combine(_dir, "f" + _n++);
        var dsh = Path.Combine(files, "runtime", "runtime-current", "dsh");
        Directory.CreateDirectory(Path.Combine(dsh, Path.GetDirectoryName(Bin)!));
        File.WriteAllText(Path.Combine(dsh, Bin), "old-bin\n");
        Directory.CreateDirectory(Path.Combine(dsh, ".dshmobile"));
        File.WriteAllText(Path.Combine(dsh, ".dshmobile", "version"), "1.0.0");
        return files;
    }

    private static DshLayerInstaller Installer(string files, LayerFileOps? ops = null) =>
        new(new RuntimeLayout(files, null, "/lib"), LayerExtractor.AndroidLikeHost) { Ops = ops ?? LayerFileOps.Real };

    private static Task<DshLayerResult> Hold(DshLayerInstaller i) => i.InstallFromFileAsync(Bundle, null, "1.2.0", false, CancellationToken.None, holdForProbe: true);

    [Fact]
    public async Task Held_KeepsTheOldLayerAside_ThenCommitRetiresIt()
    {
        var i = Installer(Files());
        var r = await Hold(i);
        Assert.True(r.Ok && r.Changed, r.Message);
        Assert.True(i.IsHeldForProbe);
        Assert.Equal("1.2.0", i.InstalledVersion());
        Assert.Equal("old-bin\n", File.ReadAllText(Path.Combine(i.AsideDir, Bin)));
        Assert.Equal(new DshLayerRecovery(true, "held", null), i.Recover(keepProbing: true)); // the holder's own recovery leaves it
        Assert.True(i.IsHeldForProbe);

        Assert.True(i.CommitProbed().Ok);
        Assert.False(i.IsHeldForProbe);
        Assert.False(Directory.Exists(i.AsideDir));
        Assert.False(File.Exists(i.JournalFile));
        Assert.Equal("1.2.0", i.InstalledVersion());
    }

    [Fact]
    public async Task Held_AbortOrAnyoneElsesRecovery_RestoresTheOldLayer()
    {
        foreach (var undo in (Func<DshLayerInstaller, DshLayerRecovery>[])[x => x.AbortProbed(), x => x.Recover()])
        {
            var i = Installer(Files());
            Assert.True((await Hold(i)).Ok);
            var r = undo(i);
            Assert.True(r.Ok, r.Message);
            Assert.Equal("restored", r.Action);
            Assert.Equal("1.0.0", i.InstalledVersion());
            Assert.Equal("old-bin\n", File.ReadAllText(Path.Combine(i.DshDir, Bin)));
            Assert.False(Directory.Exists(i.AsideDir));
            Assert.False(i.IsHeldForProbe);
        }
    }

    [Fact]
    public void CommitOrAbort_WithoutAHold_IsRefused()
    {
        var i = Installer(Files());
        Assert.False(i.CommitProbed().Ok);
        Assert.False(i.AbortProbed().Ok);
        Assert.Equal("1.0.0", i.InstalledVersion());
    }

    [Fact]
    public async Task ProcessDeathAtEveryStepOfAHeldInstall_RecoversToTheOldLayer()
    {
        var count = 0;
        var real = LayerFileOps.Real;
        LayerFileOps Counting(int dieAt) => new()
        {
            Rename = (a, b) => Tick(dieAt) && real.Rename(a, b),
            CopyTree = (a, b) => { Tick(dieAt); real.CopyTree(a, b); },
            DeleteTree = p => { Tick(dieAt); real.DeleteTree(p); },
            WriteFile = (p, c) => { Tick(dieAt); real.WriteFile(p, c); },
            DeleteFile = p => { Tick(dieAt); real.DeleteFile(p); },
        };
        bool Tick(int dieAt)
        {
            if (count++ >= dieAt)
            {
                throw new InvalidProgramException("process died");
            }
            return true;
        }

        count = 0;
        Assert.True((await Hold(Installer(Files(), Counting(int.MaxValue)))).Ok);
        var steps = count;
        for (var k = 0; k < steps; k++)
        {
            count = 0;
            var files = Files();
            await Assert.ThrowsAsync<InvalidProgramException>(() => Hold(Installer(files, Counting(k))));
            var next = Installer(files); // a new process: it does not hold the probe
            Assert.True(next.Recover().Ok);
            Assert.Equal("1.0.0", next.InstalledVersion()); // never committed, so always the old layer
            Assert.Equal("old-bin\n", File.ReadAllText(Path.Combine(next.DshDir, Bin)));
            Assert.False(Directory.Exists(next.AsideDir));
        }
    }

    // ------------------------------------------------------------------ update state record + planner

    private static readonly DateTimeOffset T = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UpdateState_RoundTrips_AndAnUnfinishedOneIsSettledOnceAsInterrupted()
    {
        var store = new UpdateStateStore(new RuntimeLayout(Path.Combine(_dir, "s"), null, "/lib"));
        Assert.Null(store.Read());
        Assert.Null(store.SettleInterrupted(T));
        var s = new UpdateState(UpdatePhases.Probing, "dsh", "1.2.0", new string('a', 64), "preview", T, T, null, null);
        store.Write(s);
        Assert.Equal(s, store.Read());
        var settled = store.SettleInterrupted(T.AddMinutes(1))!;
        Assert.Equal((UpdatePhases.Failed, UpdateTransactionCodes.Interrupted), (settled.Phase, settled.Code));
        Assert.Contains("during PROBING", settled.Message, StringComparison.Ordinal);
        Assert.Null(store.SettleInterrupted(T.AddMinutes(2)));
        store.Write(s with { Phase = UpdatePhases.Committed });
        Assert.Null(store.SettleInterrupted(T));

        File.WriteAllText(store.FilePath, "{");
        Assert.Equal((UpdatePhases.Failed, UpdateTransactionCodes.Interrupted), (store.Read()!.Phase, store.Read()!.Code));
    }

    private static ReleaseArtifact A(string kind, string version) => new(kind, version, "arm64", $"{kind}-{version}.tar.zst",
        new Uri($"https://github.com/maiji1990/dsh_mobile/releases/download/{kind}-{version}/{kind}-{version}.tar.zst"), new string('c', 64), 10,
        null, null, kind == "dsh" ? "deepseek-ai/deepseek-harness" : null, kind == "dsh" ? "dsh-v" + version : null, kind == "dsh" ? new string('d', 40) : null);

    [Fact]
    public void Planner_InstallsRuntimeAndDshUpdates_AndExplainsEveryOtherRow()
    {
        var rt = A("runtime", "0.3.0");
        var dsh = A("dsh", "0.2.2");
        var blocked = A("dsh", "0.3.0");
        var plan = UpdatePlanner.Plan([
            new ReleaseChoice("runtime", "0.2.0", [new ReleaseOffer(rt, null)], rt),
            new ReleaseChoice("node", null, [], null),
            new ReleaseChoice("dsh", "0.2.1", [new ReleaseOffer(blocked, "needs Node 26.x (installed 24.19.0)"), new ReleaseOffer(dsh, null)], dsh),
            new ReleaseChoice("app", "0.1.0", [new ReleaseOffer(A("app", "0.2.0"), null)], A("app", "0.2.0")),
        ]);
        Assert.Equal(rt, plan.Runtime);
        Assert.Equal(dsh, plan.Dsh);
        Assert.True(plan.HasWork);
        Assert.Equal(2, plan.Notes.Count);
        Assert.Contains("dsh 0.3.0: needs Node 26.x (installed 24.19.0)", plan.Notes);
        Assert.Contains(plan.Notes, n => n.StartsWith("app 0.2.0: UPDATE_KIND_NOT_SUPPORTED", StringComparison.Ordinal));
        var nothing = UpdatePlanner.Plan([new ReleaseChoice("dsh", "0.2.2", [new ReleaseOffer(dsh, null)], null)]);
        Assert.Null(nothing.Dsh);
        Assert.Null(nothing.Runtime);
        Assert.False(nothing.HasWork);
    }
}
