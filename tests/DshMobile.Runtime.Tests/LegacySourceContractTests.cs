using System.Text.RegularExpressions;
using DshMobile.Runtime.Guest;
using DshMobile.Runtime.Legacy;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// Pins the C# port to the legacy sources it replaces, without running Android: scans the pinned DSHBox submodule and
/// the legacy Kotlin bridge. Any upstream change to these facts fails here first. Removed with the legacy code (PR-10);
/// until then a missing submodule is a test failure, not a skip.
/// </summary>
public partial class LegacySourceContractTests
{
    private static string Dshbox(params string[] parts) => File.ReadAllText(Repo.PathOf(["third_party", "dshbox", .. parts]));

    private static string Bridge(string file) =>
        File.ReadAllText(Repo.PathOf("native", "native-bridge", "src", "main", "java", "dshmobile", "nativebridge", file));

    private static string ConstantsKt() => Dshbox("common", "src", "main", "java", "com", "dshbox", "app", "common", "Constants.kt");

    [GeneratedRegex("""const val (?<name>[A-Z_]+) = (?<value>"[^"$]*"|-?\d+)\s*$""", RegexOptions.Multiline)]
    private static partial Regex KotlinConst();

    private static Dictionary<string, string> LiteralConstants(string source) =>
        KotlinConst().Matches(source).ToDictionary(m => m.Groups["name"].Value, m => m.Groups["value"].Value.Trim('"'));

    [Fact]
    public void RuntimeConstants_MatchUpstreamConstantsKt()
    {
        var c = LiteralConstants(ConstantsKt());
        Assert.Equal(RuntimeConstants.DshHost, c["DSH_DEFAULT_HOST"]);
        Assert.Equal(RuntimeConstants.DshPort.ToString(System.Globalization.CultureInfo.InvariantCulture), c["DSH_DEFAULT_PORT"]);
        Assert.Equal(RuntimeConstants.SandboxWorkspace, c["SANDBOX_WORKSPACE"]);
        Assert.Equal(RuntimeConstants.SandboxKeepaliveMarker, c["SANDBOX_KEEPALIVE_MARKER"]);
        Assert.Equal(RuntimeConstants.DshStartScript, c["DSH_START_SCRIPT"]);
        Assert.Equal(RuntimeConstants.DshWebProfile, c["DSH_WEB_PROFILE"]);
        Assert.Equal(RuntimeConstants.LinkShimGuestDir, c["DSH_LINK_SHIM_GUEST_DIR"]);
        Assert.Equal(RuntimeConstants.GuestCacheDir, c["DSHBOX_GUEST_CACHE_DIR"]);
        Assert.Equal(RuntimeConstants.GuestCacheDirLeaf, c["GUEST_CACHE_DIR_LEAF"]);
        Assert.Equal(RuntimeConstants.AssetsRelativeDir, c["DSHBOX_ASSETS_RELATIVE_DIR"]);
        Assert.Equal(RuntimeConstants.MinSupportedSdk.ToString(System.Globalization.CultureInfo.InvariantCulture), c["MIN_SUPPORTED_SDK"]);
        Assert.Equal(RuntimeConstants.MaxAutoRestartAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture), c["MAX_AUTO_RESTART_ATTEMPTS"]);
        Assert.Equal(RuntimeConstants.LinkShimGuestDir + "/link-shim.mjs", RuntimeConstants.LinkShimGuestPath);
        Assert.Equal(RuntimeConstants.LinkShimGuestDir + "/libdshbox-link.so", RuntimeConstants.LinkShimSoGuestPath);
        Assert.Equal(RuntimeConstants.SandboxWorkspace + "/" + RuntimeConstants.AssetsRelativeDir, RuntimeConstants.AssetsGuestDir);
    }

    [GeneratedRegex("""val (?<prop>[a-zA-Z]+Dir): File = File\(appFilesDir, "(?<leaf>[a-z-]+)"\)""")]
    private static partial Regex SandboxConfigDir();

    [Fact]
    public void RuntimeLayout_MatchesUpstreamSandboxConfig()
    {
        var src = Dshbox("sandbox-manager", "src", "main", "java", "com", "dshbox", "app", "sandbox", "SandboxConfig.kt");
        var dirs = SandboxConfigDir().Matches(src).ToDictionary(m => m.Groups["prop"].Value, m => m.Groups["leaf"].Value);
        Assert.True(dirs.Count >= 8, $"parsed only {dirs.Count} dirs from SandboxConfig.kt");
        var layout = new RuntimeLayout("/data/data/x/files", "/data/data/x/cache", "/data/app/x/lib/arm64");
        var csharp = new Dictionary<string, string>
        {
            ["runtimeDir"] = layout.RuntimeDir, ["sandboxDir"] = layout.SandboxDir, ["userDataDir"] = layout.UserDataDir,
            ["logsDir"] = layout.LogsDir, ["backupsDir"] = layout.BackupsDir, ["updatesDir"] = layout.UpdatesDir,
            ["browserEntryDir"] = layout.BrowserEntryDir, ["dshShimDir"] = layout.ShimDir, ["pilotEntryDir"] = layout.PilotEntryDir,
        };
        foreach (var (prop, leaf) in dirs)
        {
            Assert.True(csharp.TryGetValue(prop, out var path), $"SandboxConfig.{prop} has no C# counterpart");
            Assert.Equal(Path.Combine("/data/data/x/files", leaf), path);
        }
    }

    [GeneratedRegex("""enum class SandboxState \{(?<body>[^}]*)\}""")]
    private static partial Regex SandboxEnum();

    [GeneratedRegex("""enum class DshState \{(?<body>[^}]*)\}""")]
    private static partial Regex DshEnum();

    private static List<string> EnumValues(Regex r, string src) =>
        r.Match(src).Groups["body"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    [Fact]
    public void LegacyStateMapper_CoversEveryUpstreamState()
    {
        var src = Dshbox("sandbox-manager", "src", "main", "java", "com", "dshbox", "app", "sandbox", "SandboxState.kt");
        var sandbox = EnumValues(SandboxEnum(), src);
        var dsh = EnumValues(DshEnum(), src);
        Assert.NotEmpty(sandbox);
        Assert.NotEmpty(dsh);
        Assert.Equal(sandbox.ToHashSet(), LegacyStateMapper.SandboxStates.ToHashSet());
        Assert.Equal(dsh.ToHashSet(), LegacyStateMapper.DshStates.ToHashSet());
        foreach (var s in sandbox)
        {
            Assert.NotNull(LegacyStateMapper.MapSandbox(s, runtimeInstalled: true));
            Assert.NotNull(LegacyStateMapper.MapSandbox(s, runtimeInstalled: false));
        }
        foreach (var d in dsh)
        {
            Assert.NotNull(LegacyStateMapper.MapDsh(d));
        }
    }

    [GeneratedRegex("""val PACKAGE_SPEC = Regex\("(?<re>[^"]+)"\)""")]
    private static partial Regex KotlinSpec();

    [GeneratedRegex("""val PACKAGE_NAME = Regex\("(?<re>[^"]+)"\)""")]
    private static partial Regex KotlinName();

    [GeneratedRegex("""val PLUGIN_PROFILES = setOf\((?<body>[^)]*)\)""")]
    private static partial Regex KotlinProfiles();

    [Fact]
    public void PluginAllowlists_MatchLegacyRuntimeController()
    {
        var src = Bridge("RuntimeController.kt");
        Assert.Equal(KotlinSpec().Match(src).Groups["re"].Value, PluginCommand.PackageSpec().ToString());
        Assert.Equal(KotlinName().Match(src).Groups["re"].Value, PluginCommand.PackageName().ToString());
        var profiles = KotlinProfiles().Match(src).Groups["body"].Value.Split(',', StringSplitOptions.TrimEntries).Select(p => p.Trim('"')).ToHashSet();
        Assert.Equal(profiles, PluginCommand.Profiles.ToHashSet());
    }

    [Fact]
    public void LegacyPluginCommand_NeverInterpolatesRawOperands()
    {
        // The PR-00 fix: the only way plugin operands reach the guest shell is through shellJoin().
        var src = Bridge("RuntimeController.kt");
        Assert.DoesNotContain("add $spec", src, StringComparison.Ordinal);
        Assert.DoesNotContain("remove $name", src, StringComparison.Ordinal);
        Assert.Contains("shellJoin(argv)", src, StringComparison.Ordinal);
    }

    [Fact]
    public void GuestPaths_MatchLegacyRuntimeController()
    {
        var src = Bridge("RuntimeController.kt");
        Assert.Contains($"GUEST_DSH_BIN = \"{RuntimeConstants.GuestDshBin}\"", src, StringComparison.Ordinal);
        Assert.Contains($"GUEST_DSH = \"{RuntimeConstants.GuestDshWrapper}\"", src, StringComparison.Ordinal);
    }
}
