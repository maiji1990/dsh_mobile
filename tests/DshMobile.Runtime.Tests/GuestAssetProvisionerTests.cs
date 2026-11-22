using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DshMobile.Core.Contracts;
using DshMobile.Runtime.Automation;
using DshMobile.Runtime.Guest;
using DshMobile.Runtime.Hosting;
using DshMobile.Runtime.Layers;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// The guest host files C# stages itself (legacy <c>provisionGuestAssets</c>), the C# profile probe, the runtime facts that
/// replace the legacy <c>runtime.status</c>, and the layered rollback.
/// </summary>
public sealed partial class GuestAssetProvisionerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dshm-guestassets-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private RuntimeLayout Layout(string? nativeLib = null)
    {
        var files = Path.Combine(_dir, "files");
        Directory.CreateDirectory(files);
        return new RuntimeLayout(files, null, nativeLib ?? Path.Combine(_dir, "nolib"));
    }

    private sealed class FakeAssets(Dictionary<string, byte[]> files) : IBundledAssets
    {
        public IReadOnlyList<string> List(string dir) =>
            files.Keys.Where(k => k.StartsWith(dir + "/", StringComparison.Ordinal)).Select(k => k[(dir.Length + 1)..]).ToList();

        public Stream Open(string path) => files.TryGetValue(path, out var b) ? new MemoryStream(b) : throw new FileNotFoundException(path);
    }

    private static readonly byte[] Shim = "export const shim = 1;\n"u8.ToArray();
    private static readonly byte[] ShimSo = [0x7F, (byte)'E', (byte)'L', (byte)'F', 1, 2, 3];

    /// <summary>APK assets with a pnpm cache fixture and its checksum sidecar.</summary>
    private static FakeAssets Assets(string? sha = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, TarEntryFormat.Ustar, leaveOpen: true))
        {
            using var entryPoint = new MemoryStream("export {};\n"u8.ToArray());
            writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, GuestToolchain.PnpmEntryPoint)
            {
                DataStream = entryPoint,
                ModificationTime = DateTimeOffset.UnixEpoch,
            });
        }
        var tar = stream.ToArray();
        return new FakeAssets(new Dictionary<string, byte[]>
        {
            [GuestAssetProvisioner.LinkShimAsset] = Shim,
            [GuestAssetProvisioner.LinkShimSoAsset] = ShimSo,
            [$"{GuestToolchain.AssetDir}/{GuestToolchain.CorepackAsset}"] = tar,
            [$"{GuestToolchain.AssetDir}/{GuestToolchain.CorepackAsset}.sha256"] =
                System.Text.Encoding.UTF8.GetBytes((sha ?? Convert.ToHexStringLower(SHA256.HashData(tar))) + "  " + GuestToolchain.CorepackAsset + "\n"),
        });
    }

    [Fact]
    public async Task Ensure_StagesEveryLegacyFile_WhereTheGuestBindsThem_ThenIsIdempotent()
    {
        var layout = Layout();
        var r = await GuestAssetProvisioner.EnsureAsync(layout, Assets(), CancellationToken.None);
        Assert.True(r.Ok, r.Describe());
        Assert.Equal(Shim, File.ReadAllBytes(layout.ShimFile));
        Assert.Equal(ShimSo, File.ReadAllBytes(Path.Combine(layout.ShimDir, GuestAssetProvisioner.LinkShimSoName)));
        var dshmobile = Path.Combine(layout.GuestCacheDir, "dshmobile");
        Assert.Equal(File.ReadAllBytes(Repo.PathOf("src", "DshMobile.Runtime", "Guest", "Assets", "dsh-probe.mjs")),
            File.ReadAllBytes(Path.Combine(dshmobile, GuestAssetProvisioner.ProbeName)));
        Assert.Equal(File.ReadAllBytes(Repo.PathOf("src", "DshMobile.Runtime", "Guest", "Assets", "dsh-e2e.mjs")),
            File.ReadAllBytes(Path.Combine(dshmobile, GuestAssetProvisioner.E2eName)));
        Assert.Equal(File.ReadAllBytes(Repo.PathOf("src", "DshMobile.Runtime", "Guest", "Assets", "startup-fs-cache-shim.mjs")),
            File.ReadAllBytes(Path.Combine(dshmobile, GuestAssetProvisioner.StartupFsCacheShimName)));
        var trimPatchBytes = File.ReadAllBytes(Path.Combine(dshmobile, GuestAssetProvisioner.WebTrimPatchName));
        Assert.Equal(File.ReadAllBytes(Repo.PathOf("src", "DshMobile.Runtime", "Guest", "Assets", "dsh-web-trim-patch.yml")),
            trimPatchBytes);
        Assert.Equal("ca9797888b7f6402a21a15dd9bd3308faeb936d50f171a31262d50824d605fd2",
            Convert.ToHexStringLower(SHA256.HashData(trimPatchBytes)));
        Assert.True(UiFacadeAssets.Intact(layout.GuestCacheDir));
        var corepack = GuestToolchain.CorepackDir(layout);
        Assert.True(File.Exists(Path.Combine(corepack, GuestToolchain.ReadyMarker)));
        Assert.True(File.Exists(Path.Combine(corepack, GuestToolchain.PnpmEntryPoint)));
        Assert.True(File.Exists(Path.Combine(GuestToolchain.ShimBinDir(layout), "dsh")));
        Assert.True(File.Exists(Path.Combine(GuestToolchain.ShimBinDir(layout), "pnpm")));
        Assert.True(File.Exists(Path.Combine(GuestToolchain.ShimBinDir(layout), "xdg-user-dir")));
        Assert.Empty(Directory.GetDirectories(layout.GuestCacheDir, "corepack-stage-*"));

        var again = await GuestAssetProvisioner.EnsureAsync(layout, Assets(), CancellationToken.None);
        Assert.True(again.Ok);
        Assert.Empty(again.Written);
    }

    [Fact]
    public async Task Ensure_RepairsAChangedShim_AndOnlyThat()
    {
        var layout = Layout();
        await GuestAssetProvisioner.EnsureAsync(layout, Assets(), CancellationToken.None);
        File.WriteAllText(layout.ShimFile, "stale");
        var r = await GuestAssetProvisioner.EnsureAsync(layout, Assets(), CancellationToken.None);
        Assert.Equal(["link-shim.mjs"], r.Written);
        Assert.Equal(Shim, File.ReadAllBytes(layout.ShimFile));
    }

    [Fact]
    public async Task Ensure_RewritesLegacyPnpmShim_AndRepairsMissingCacheEntryPoint()
    {
        var layout = Layout();
        var assets = Assets();
        var initial = await GuestAssetProvisioner.EnsureAsync(layout, assets, CancellationToken.None);
        Assert.True(initial.Ok, initial.Describe());
        var entryPoint = Path.Combine(GuestToolchain.CorepackDir(layout), GuestToolchain.PnpmEntryPoint);
        var shim = Path.Combine(GuestToolchain.ShimBinDir(layout), "pnpm");
        File.Delete(entryPoint);
        File.WriteAllText(shim, "#!/bin/sh\nexec /usr/local/bin/corepack pnpm \"$@\"\n");

        var repaired = await GuestAssetProvisioner.EnsureAsync(layout, assets, CancellationToken.None);

        Assert.True(repaired.Ok, repaired.Describe());
        Assert.True(File.Exists(entryPoint));
        var wrapper = await File.ReadAllTextAsync(shim);
        Assert.Contains("exec /usr/local/bin/node \"$PNPM\" \"$@\"", wrapper);
        Assert.DoesNotContain("/usr/local/bin/corepack", wrapper);
    }

    [Fact]
    public async Task Ensure_CorepackChecksumMismatch_IsAFailure_AndLeavesNoCache()
    {
        var layout = Layout();
        var r = await GuestAssetProvisioner.EnsureAsync(layout, Assets(sha: new string('0', 64)), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains(r.Failures, f => f.Contains("sha256 mismatch", StringComparison.Ordinal));
        Assert.False(Directory.Exists(GuestToolchain.CorepackDir(layout)));
        Assert.Empty(Directory.GetDirectories(layout.GuestCacheDir, "corepack-stage-*"));
        // The other steps are independent (legacy runCatching blocks).
        Assert.True(File.Exists(layout.ShimFile));
        Assert.True(File.Exists(Path.Combine(GuestToolchain.ShimBinDir(layout), "pnpm")));
    }

    [Fact]
    public async Task Ensure_WithoutApkAssets_ReportsBothAssetSteps_AndStillStagesTheRest()
    {
        var layout = Layout();
        var r = await GuestAssetProvisioner.EnsureAsync(layout, null, CancellationToken.None);
        Assert.Equal(2, r.Failures.Count);
        Assert.False(File.Exists(layout.ShimFile));
        Assert.True(UiFacadeAssets.Intact(layout.GuestCacheDir));
        Assert.True(File.Exists(Path.Combine(GuestToolchain.ShimBinDir(layout), "dsh")));
    }

    [Fact]
    public void Paths_MatchTheLegacyBinds()
    {
        var layout = Layout();
        Assert.Equal(Path.Combine(layout.FilesDir, "dshmobile", "link-shim.mjs"), layout.ShimFile);
        Assert.Equal(Path.Combine(layout.UserDataDir, ".dsh", "dshmobile", "bin"), GuestToolchain.ShimBinDir(layout));
        Assert.Equal(Path.Combine(layout.FilesDir, "guest-cache", "corepack"), GuestToolchain.CorepackDir(layout));
        Assert.Equal("/opt/dshmobile-cache/dshmobile/dsh-probe.mjs", GuestAssetProvisioner.GuestProbe);
        Assert.Equal("/opt/dshmobile-cache/dshmobile/dsh-e2e.mjs", GuestAssetProvisioner.GuestE2e);
        Assert.Equal("/opt/dshmobile-cache/dshmobile/startup-fs-cache-shim.mjs", GuestAssetProvisioner.GuestStartupFsCacheShim);
    }

    // ------------------------------------------------------------------ pinned wrapper text

    /// <summary>
    /// The dsh wrapper matches the former bridge at 128c2ea (ADR-0018). The pnpm wrapper retains the bridge's
    /// store selection and copy mode, with the pinned pnpm entry point run directly by Node.
    /// </summary>
    private static readonly Dictionary<string, string> ExpectedShims = new()
    {
        // Raw literals take the checkout's line endings (CRLF on a Windows `* text=auto` checkout); the wrappers are LF.
        ["dsh"] = DshShim.ReplaceLineEndings("\n") + "\n",
        ["pnpm"] = PnpmShim.ReplaceLineEndings("\n") + "\n",
    };

    private const string DshShim = """
#!/bin/sh
# DSH Mobile: `dsh` = the app-managed DSH layer's official CLI (same version and profiles as the WebUI).
# The hard-link shim is required: DSH publishes files with link(), which Android's data partition refuses.
# Note: `dsh web` would start a second web server and collide with the app's port 3080.
[ -n "$DSH_HOME" ] || DSH_HOME=/root/projects/.dsh
export DSH_HOME
BIN="${DSH_BIN:-/opt/dshapp/runtime/node_modules/@deepseek-ai/dsh/lib/bin.js}"
SHIM=""
[ -f /opt/dshmobile/link-shim.mjs ] && SHIM="--import /opt/dshmobile/link-shim.mjs"
exec /usr/local/bin/node --expose-internals $SHIM "$BIN" "$@"
""";

    private const string PnpmShim = """
#!/bin/sh
# DSH Mobile: use the pinned pnpm package from the APK cache.
PNPM="/opt/dshmobile-cache/corepack/v1/pnpm/11.1.2/bin/pnpm.mjs"
if [ ! -f "$PNPM" ]; then
  printf '%s\n' 'dshmobile: bundled pnpm entry point is missing' >&2
  exit 127
fi
STORE=""
if [ -f ./.modules.yaml ]; then
  RECORDED=$(sed -n 's/.*"storeDir"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' ./.modules.yaml 2>/dev/null | head -n 1)
  if [ -z "$RECORDED" ]; then
    RECORDED=$(sed -n 's/^storeDir:[[:space:]]*//p' ./.modules.yaml 2>/dev/null | head -n 1)
  fi
  if [ -n "$RECORDED" ] && [ -d "$RECORDED" ]; then
    case "$RECORDED" in
      */v[0-9]*) STORE=${RECORDED%/*} ;;
      *) STORE="$RECORDED" ;;
    esac
  fi
fi
if [ -z "$STORE" ]; then
  if [ -d /opt/dshmobile-cache ]; then
    STORE=/opt/dshmobile-cache/pnpm-store
  else
    STORE=/root/projects/.dsh/pnpm-store
  fi
fi
export pnpm_config_store_dir="$STORE"
export pnpm_config_package_import_method=copy
mkdir -p "$STORE" 2>/dev/null || true
exec /usr/local/bin/node "$PNPM" "$@"
""";

    [Theory]
    [InlineData("dsh")]
    [InlineData("pnpm")]
    public async Task Shims_MatchThePinnedTextAndMode(string file)
    {
        var layout = Layout();
        GuestToolchain.WriteShims(layout);
        Assert.Equal(ExpectedShims[file], (await File.ReadAllTextAsync(Path.Combine(GuestToolchain.ShimBinDir(layout), file))).Replace("\r\n", "\n", StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(Path.Combine(GuestToolchain.ShimBinDir(layout), file)).HasFlag(UnixFileMode.OtherExecute));
        }
    }

    /// <summary>The bridge's GuestToolchain.kt / RuntimeController.kt values at 128c2ea (pinned in PR-10).</summary>
    [Fact]
    public void Corepack_AndAssetConstants_AreTheLegacyOnes()
    {
        Assert.Equal("toolchain-cache", GuestToolchain.AssetDir);
        Assert.Equal("corepack-pnpm-11.1.2.tar.zst", GuestToolchain.CorepackAsset);
        Assert.Equal(".ready-sha256", GuestToolchain.ReadyMarker);
        Assert.Equal("dshmobile/link-shim.mjs", GuestAssetProvisioner.LinkShimAsset);
        Assert.Equal("dshmobile/libdshmobile-link.so", GuestAssetProvisioner.LinkShimSoAsset);
        Assert.Equal("/opt/dshmobile-cache/dshmobile/dsh-probe.mjs", GuestAssetProvisioner.GuestProbe);
    }

    // ------------------------------------------------------------------ probe

    [Fact]
    public void Probe_Command_IsTheLegacyFixedCommand_AndOnlyForShippedProfiles()
    {
        // Legacy `node $GUEST_PROBE $profile`, built from argv with every token quoted (AGENTS.md rule 11).
        Assert.Equal("'node' '/opt/dshmobile-cache/dshmobile/dsh-probe.mjs' 'web'", DshProbe.Command("web"));
        Assert.Equal("'node' '/opt/dshmobile-cache/dshmobile/dsh-probe.mjs' 'sdk-minimal'", DshProbe.Command("sdk-minimal"));
        Assert.Throws<ArgumentException>(() => DshProbe.Command("web; rm -rf /"));
    }

    [Fact]
    public void Probe_Parse_TakesTheLastResultLine()
    {
        var run = new GuestProcessResult(true, 0,
            "starting\nDSHPROBE {\"profile\":\"web\",\"status\":\"fail\",\"checks\":[]}\nnoise\n"
            + "DSHPROBE {\"profile\":\"web\",\"status\":\"pass\",\"needsCredential\":false,\"shim\":true,\"checks\":[{\"name\":\"dump-config\",\"ok\":true,\"ms\":12,\"detail\":\"ok\"}]}\n",
            null);
        var o = DshProbe.Parse("web", run);
        Assert.Null(o.ErrorCode);
        Assert.Equal("pass", o.Result!.Status);
        Assert.True(o.Result.Shim);
        Assert.Equal("dump-config", Assert.Single(o.Result.Checks).Name);
    }

    [Fact]
    public void Probe_Parse_NoResultLine_IsAnError_WithTheTail_NeverAPass()
    {
        var o = DshProbe.Parse("acp", new GuestProcessResult(false, 255, "a\nb\nIllegal instruction\n", "exit 255"));
        Assert.Null(o.Result);
        Assert.Equal(DshProbe.NoResult, o.ErrorCode);
        Assert.Contains("Illegal instruction", o.Error, StringComparison.Ordinal);
        Assert.Equal(DshProbe.Malformed, DshProbe.Parse("acp", new GuestProcessResult(true, 0, "DSHPROBE {not json", null)).ErrorCode);
    }

    [Fact]
    public void Probe_ProfilesAreTheScriptsProfiles()
    {
        var js = File.ReadAllText(Repo.PathOf("src", "DshMobile.Runtime", "Guest", "Assets", "dsh-probe.mjs"));
        var m = ProfilesSet().Match(js);
        Assert.True(m.Success);
        var inScript = Regex.Matches(m.Groups[1].Value, "\"([^\"]+)\"").Select(x => x.Groups[1].Value).ToList();
        Assert.Equal(inScript, DshProfiles.All);
    }

    [GeneratedRegex(@"const PROFILES = new Set\(\[([^\]]*)\]\)")]
    private static partial Regex ProfilesSet();

    // ------------------------------------------------------------------ runtime facts (legacy runtime.status)

    private static void Installed(RuntimeLayout layout, string sha = "aa")
    {
        var rc = layout.RuntimeCurrentDir;
        Directory.CreateDirectory(Path.Combine(rc, "base", "etc"));
        File.WriteAllText(Path.Combine(rc, "base", "etc", "os-release"), "ID=debian");
        Directory.CreateDirectory(Path.Combine(rc, "node", "bin"));
        File.WriteAllText(Path.Combine(rc, "node", "bin", "node"), "elf");
        File.WriteAllText(Path.Combine(rc, "runtime-profile.json"),
            $$"""{"bundle":{"version":"1"},"layers":[{"name":"base","sha256":"{{sha}}"}],"assembly":["base"]}""");
    }

    [Fact]
    public void Facts_NotInstalled_WithoutPRootOrProfile()
    {
        var layout = Layout();
        var f = RuntimeStatusReader.Read(layout);
        Assert.False(f.RuntimeInstalled);
        Assert.Equal("proot/base missing", f.InstallReason);
        Assert.Equal(RuntimeState.NotInstalled, RuntimeStatusReader.Idle(f, DateTimeOffset.UnixEpoch).State);
    }

    [Fact]
    public void Facts_Installed_HealsTheMissingSentinel_LikeLegacyIsRuntimeInstalled()
    {
        var lib = Path.Combine(_dir, "lib");
        Directory.CreateDirectory(lib);
        File.WriteAllText(Path.Combine(lib, "libproot.so"), "elf");
        var layout = Layout(lib);
        Installed(layout);
        var f = RuntimeStatusReader.Read(layout);
        Assert.True(f.RuntimeInstalled, f.InstallReason);
        Assert.True(f.NodeLayerInstalled);
        Assert.False(f.LinkShim);
        Assert.Null(f.DshVersion);
        Assert.Equal("aa", File.ReadAllText(Path.Combine(layout.LayerDir("base"), ".dshmobile", "layer-base.sha256")));
        var idle = RuntimeStatusReader.Idle(f, DateTimeOffset.UnixEpoch);
        Assert.Equal((RuntimeState.Ready, HarnessState.Stopped), (idle.State, idle.Harness));
        Assert.Contains("runtime=True node=True", f.Describe("Ready", "Stopped", false), StringComparison.Ordinal);
    }

    [Fact]
    public void Facts_BrokenSentinel_IsNotInstalled_WithTheReason()
    {
        var lib = Path.Combine(_dir, "lib");
        Directory.CreateDirectory(lib);
        File.WriteAllText(Path.Combine(lib, "libproot.so"), "elf");
        var layout = Layout(lib);
        Installed(layout);
        Directory.CreateDirectory(Path.Combine(layout.LayerDir("base"), ".dshmobile"));
        File.WriteAllText(Path.Combine(layout.LayerDir("base"), ".dshmobile", "layer-base.sha256"), "bb");
        var f = RuntimeStatusReader.Read(layout);
        Assert.False(f.RuntimeInstalled);
        Assert.Contains("base:sha-mismatch", f.InstallReason, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ rollback

    private static void Body(string dir, string marker)
    {
        foreach (var layer in new[] { "base", "node", "android-side" })
        {
            Directory.CreateDirectory(Path.Combine(dir, layer));
            File.WriteAllText(Path.Combine(dir, layer, "marker"), marker + ":" + layer);
        }
        File.WriteAllText(Path.Combine(dir, "runtime-profile.json"), "{\"v\":\"" + marker + "\"}");
    }

    [Fact]
    public void Rollback_NoPreviousBody_IsRefused_AndTouchesNothing()
    {
        var layout = Layout();
        Body(layout.RuntimeCurrentDir, "new");
        Directory.CreateDirectory(Path.Combine(LayerRollback.PreviousDir(layout), "dsh")); // only the DSH layer's previous
        var r = LayerRollback.Rollback(layout);
        Assert.False(r.Ok);
        Assert.Equal(LayerRollback.NoRollbackSlot, r.Code);
        Assert.Equal("new:base", File.ReadAllText(Path.Combine(layout.LayerDir("base"), "marker")));
        Assert.False(Directory.Exists(LayerRollback.FailedDir(layout)));
    }

    [Fact]
    public void Rollback_SwapsThePreviousBodyIn_KeepsTheCurrentInFailed_AndLeavesDshAlone()
    {
        var layout = Layout();
        Body(layout.RuntimeCurrentDir, "new");
        Body(LayerRollback.PreviousDir(layout), "old");
        Directory.CreateDirectory(layout.LayerDir("dsh"));
        File.WriteAllText(Path.Combine(layout.LayerDir("dsh"), "marker"), "dsh-current");
        Directory.CreateDirectory(Path.Combine(LayerRollback.PreviousDir(layout), "dsh"));
        File.WriteAllText(Path.Combine(LayerRollback.PreviousDir(layout), "dsh", "marker"), "dsh-previous");
        Directory.CreateDirectory(layout.UserDataDir);
        File.WriteAllText(Path.Combine(layout.UserDataDir, "keep"), "user");

        var r = LayerRollback.Rollback(layout);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(["base", "node", "android-side", "runtime-profile.json"], r.Restored);
        Assert.Equal("old:node", File.ReadAllText(Path.Combine(layout.LayerDir("node"), "marker")));
        Assert.Equal("{\"v\":\"old\"}", File.ReadAllText(Path.Combine(layout.RuntimeCurrentDir, "runtime-profile.json")));
        Assert.Equal("new:base", File.ReadAllText(Path.Combine(LayerRollback.FailedDir(layout), "base", "marker")));
        Assert.Equal("dsh-current", File.ReadAllText(Path.Combine(layout.LayerDir("dsh"), "marker")));
        Assert.Equal("dsh-previous", File.ReadAllText(Path.Combine(LayerRollback.PreviousDir(layout), "dsh", "marker")));
        Assert.Equal("user", File.ReadAllText(Path.Combine(layout.UserDataDir, "keep")));
        Assert.False(LayerRollback.Available(layout)); // the previous body is now current: nothing more to roll back to
    }

    [Fact]
    public void Rollback_AFailedMove_IsUndone_SoTheCurrentBodyIsNeverLost()
    {
        var layout = Layout();
        Body(layout.RuntimeCurrentDir, "new");
        Body(LayerRollback.PreviousDir(layout), "old");
        // Every move aside succeeds, then restoring previous/node fails: everything already moved goes back.
        var prevNode = Path.Combine(LayerRollback.PreviousDir(layout), "node");
        var r = LayerRollback.Rollback(layout, (from, to) => from != prevNode && LayerInstaller.TryRename(from, to));
        Assert.False(r.Ok);
        Assert.Equal(LayerRollback.RollbackFailed, r.Code);
        Assert.Contains("failed to restore previous body: node", r.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("could not undo", r.Message, StringComparison.Ordinal);
        foreach (var layer in new[] { "base", "node", "android-side" })
        {
            Assert.Equal("new:" + layer, File.ReadAllText(Path.Combine(layout.LayerDir(layer), "marker")));
            Assert.Equal("old:" + layer, File.ReadAllText(Path.Combine(LayerRollback.PreviousDir(layout), layer, "marker")));
        }
        Assert.Equal("{\"v\":\"new\"}", File.ReadAllText(Path.Combine(layout.RuntimeCurrentDir, "runtime-profile.json")));
    }
}
