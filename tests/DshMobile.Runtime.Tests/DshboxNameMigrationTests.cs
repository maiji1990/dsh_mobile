using DshMobile.Runtime.Migration;

namespace DshMobile.Runtime.Tests;

/// <summary>ADR-0019: DSHBox-era names on disk are moved to <c>dshmobile</c> without losing or overwriting user files.</summary>
public sealed class DshboxNameMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dshm-namemig-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private RuntimeLayout Layout() => new(Path.Combine(_dir, "files"), null, Path.Combine(_dir, "lib"));

    private string F(RuntimeLayout l, string relative) => Path.Combine([l.FilesDir, .. relative.Split('/')]);

    private void Write(RuntimeLayout l, string relative, string text)
    {
        var path = F(l, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private string Read(RuntimeLayout l, string relative) => File.ReadAllText(F(l, relative));

    [Fact]
    public void FreshInstall_HasNothingToMigrate_AndWritesTheMarker()
    {
        var l = Layout();
        var r = DshboxNameMigration.Run(l);
        Assert.True(r.Ok);
        Assert.False(r.Changed);
        Assert.True(File.Exists(DshboxNameMigration.MarkerFile(l)));
        Assert.True(DshboxNameMigration.Run(l).AlreadyDone);
    }

    [Fact]
    public void LegacyLayout_IsMovedToTheNewNames()
    {
        var l = Layout();
        Write(l, "user-data/.dsh/dshbox/bin/dsh", "#!/bin/sh\n");
        Write(l, "user-data/.dsh/dshbox/plugin-market/market-and-safe-mode-overlay.yml", "x: /root/projects/.dsh/dshbox/bin\n");
        Write(l, "user-data/keep.txt", "user\n");
        Write(l, "runtime/runtime-current/base/.dshbox/layer-base.sha256", "aa");
        Write(l, "runtime/runtime-current/base/.dshbox/env.d/base.sh", "export LANG=C.UTF-8\n");
        Write(l, "runtime/runtime-current/dsh/.dshbox/version", "0.2.1-rc.5");
        Write(l, "runtime/runtime-current/previous/node/.dshbox/layer-node.sha256", "bb");
        Write(l, "dshbox/link-shim.mjs", "shim");

        var r = DshboxNameMigration.Run(l);

        Assert.True(r.Ok, r.Describe());
        Assert.Equal("#!/bin/sh\n", Read(l, "user-data/.dsh/dshmobile/bin/dsh"));
        Assert.Equal("x: /root/projects/.dsh/dshmobile/bin\n", Read(l, "user-data/.dsh/dshmobile/plugin-market/market-and-safe-mode-overlay.yml"));
        Assert.False(Directory.Exists(F(l, "user-data/.dsh/dshbox")));
        Assert.Equal("user\n", Read(l, "user-data/keep.txt"));
        Assert.Equal("aa", Read(l, "runtime/runtime-current/base/.dshmobile/layer-base.sha256"));
        Assert.Equal("0.2.1-rc.5", Read(l, "runtime/runtime-current/dsh/.dshmobile/version"));
        Assert.Equal("bb", Read(l, "runtime/runtime-current/previous/node/.dshmobile/layer-node.sha256"));
        // The bundle's own env fragments are layer content (profile env_file) and stay where the profile points.
        Assert.Equal("export LANG=C.UTF-8\n", Read(l, "runtime/runtime-current/base/.dshbox/env.d/base.sh"));
        Assert.False(File.Exists(F(l, "runtime/runtime-current/base/.dshbox/layer-base.sha256")));
        Assert.False(Directory.Exists(F(l, "runtime/runtime-current/dsh/.dshbox")));
        Assert.False(Directory.Exists(F(l, "dshbox")));
        Assert.True(File.Exists(DshboxNameMigration.MarkerFile(l)));
    }

    [Fact]
    public void BothWorkspaceDirs_AreMerged_WithoutOverwritingTheNewOnes()
    {
        var l = Layout();
        Write(l, "user-data/.dsh/dshbox/bin/dsh", "old wrapper");
        Write(l, "user-data/.dsh/dshbox/safe-mode/absolute-safe-mode.yml", "safe: true\n");
        Write(l, "user-data/.dsh/dshmobile/bin/dsh", "new wrapper");

        var r = DshboxNameMigration.Run(l);

        Assert.False(r.Ok); // the old bin/dsh is kept, so the old dir cannot go yet
        Assert.Equal("new wrapper", Read(l, "user-data/.dsh/dshmobile/bin/dsh"));
        Assert.Equal("safe: true\n", Read(l, "user-data/.dsh/dshmobile/safe-mode/absolute-safe-mode.yml"));
        Assert.Equal("old wrapper", Read(l, "user-data/.dsh/dshbox/bin/dsh"));
        Assert.False(File.Exists(DshboxNameMigration.MarkerFile(l)));
        Assert.Contains(r.Failed, f => f.StartsWith("workspace:", StringComparison.Ordinal));
    }

    [Fact]
    public void FailedRun_IsRetried_AndCompletesOnceTheConflictIsGone()
    {
        var l = Layout();
        Write(l, "user-data/.dsh/dshbox/bin/dsh", "old wrapper");
        Write(l, "user-data/.dsh/dshmobile/bin/dsh", "new wrapper");
        Assert.False(DshboxNameMigration.Run(l).Ok);

        File.Delete(F(l, "user-data/.dsh/dshbox/bin/dsh"));
        var r = DshboxNameMigration.Run(l);

        Assert.True(r.Ok, r.Describe());
        Assert.False(Directory.Exists(F(l, "user-data/.dsh/dshbox")));
        Assert.True(File.Exists(DshboxNameMigration.MarkerFile(l)));
    }

    [Fact]
    public void PnpmModulesYaml_StoreDir_IsRewritten()
    {
        var l = Layout();
        Write(l, "user-data/.dsh/plugins/p/node_modules/.modules.yaml", "storeDir: /opt/dshbox-cache/pnpm-store/v10\nvirtualStoreDir: .pnpm\n");
        Write(l, "user-data/.dsh/plugins/p/node_modules/pkg/notes.yaml", "storeDir: /opt/dshbox-cache/pnpm-store\n");

        var r = DshboxNameMigration.Run(l);

        Assert.True(r.Ok, r.Describe());
        Assert.Equal("storeDir: /opt/dshmobile-cache/pnpm-store/v10\nvirtualStoreDir: .pnpm\n", Read(l, "user-data/.dsh/plugins/p/node_modules/.modules.yaml"));
        // Only pnpm's own metadata and our config files are touched, not package contents.
        Assert.Equal("storeDir: /opt/dshbox-cache/pnpm-store\n", Read(l, "user-data/.dsh/plugins/p/node_modules/pkg/notes.yaml"));
    }

    [Fact]
    public void GuestPathRewrites_DoNotDoubleRewrite()
    {
        var text = "/opt/dshbox-cache/a /opt/dshbox/b /root/projects/.dsh/dshbox/c";
        foreach (var (o, n) in DshboxNameMigration.GuestPathRewrites)
        {
            text = text.Replace(o, n, StringComparison.Ordinal);
        }
        Assert.Equal("/opt/dshmobile-cache/a /opt/dshmobile/b /root/projects/.dsh/dshmobile/c", text);
    }

    [Fact]
    public void StaleOldSentinel_DoesNotReplaceTheInstallersCurrentOne()
    {
        var l = Layout();
        Write(l, "runtime/runtime-current/base/.dshbox/layer-base.sha256", "old");
        Write(l, "runtime/runtime-current/base/.dshmobile/layer-base.sha256", "new");

        Assert.True(DshboxNameMigration.Run(l).Ok);
        Assert.Equal("new", Read(l, "runtime/runtime-current/base/.dshmobile/layer-base.sha256"));
        Assert.False(Directory.Exists(F(l, "runtime/runtime-current/base/.dshbox")));
    }

    [Fact]
    public void LayerStateDir_IsTheOneTheInstallersWrite()
    {
        Assert.Equal(".dshmobile", RuntimeConstants.LayerStateDir);
        Assert.Equal(".dshmobile/version", Layers.DshLayerInstaller.VersionFile);
        Assert.Equal(Path.Combine(Layout().FilesDir, "dshmobile"), Layout().ShimDir);
    }
}
