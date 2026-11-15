using DshMobile.Runtime.Guest;
using DshMobile.Runtime.Remote;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// The facts the C# port was checked against while the legacy sources were in the repository (the former
/// LegacySourceContractTests, which scanned them). PR-10 (ADR-0018) removed the DSHBox submodule and the Kotlin bridge,
/// so each value below is the literal those scans found, taken from DSHBox v1.5.1
/// (<c>7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4</c>: <c>common/.../Constants.kt</c>,
/// <c>sandbox-manager/.../SandboxConfig.kt</c>) and from this repository's bridge at <c>128c2ea</c>
/// (<c>native/native-bridge/.../RuntimeController.kt</c>, <c>RemoteHosts.kt</c>, <c>SshKeys.kt</c>). A C# change that
/// drifts from what DSHBox did fails here; a deliberate change updates the literal and says why.
/// ADR-0019 renamed the <c>dshbox</c> names (DSHBox values: <c>/opt/dshbox</c>, <c>/opt/dshbox-cache</c>, <c>.dsh/dshbox</c>,
/// <c>libdshbox-link.so</c>, asset dir <c>dshbox/</c>) to <c>dshmobile</c>; <see cref="Migration.DshboxNameMigration"/> moves old data.
/// </summary>
public sealed class UpstreamPinnedValuesTests
{
    [Fact]
    public void RuntimeConstants_AreDshboxConstantsKt()
    {
        Assert.Equal("127.0.0.1", RuntimeConstants.DshHost);
        Assert.Equal(3080, RuntimeConstants.DshPort);
        Assert.Equal("/root/projects", RuntimeConstants.SandboxWorkspace);
        Assert.Equal("dshapp-sandbox-keepalive", RuntimeConstants.SandboxKeepaliveMarker);
        Assert.Equal("@deepseek-ai/dsh/lib/bin.js", RuntimeConstants.DshStartScript);
        Assert.Equal("web", RuntimeConstants.DshWebProfile);
        Assert.Equal("/opt/dshmobile", RuntimeConstants.LinkShimGuestDir); // ADR-0019 (DSHBox: /opt/dshbox, likewise below)
        Assert.Equal("/opt/dshmobile-cache", RuntimeConstants.GuestCacheDir);
        Assert.Equal("guest-cache", RuntimeConstants.GuestCacheDirLeaf);
        Assert.Equal(".dsh/dshmobile", RuntimeConstants.AssetsRelativeDir);
        Assert.Equal(29, RuntimeConstants.MinSupportedSdk);
        Assert.Equal(3, RuntimeConstants.MaxAutoRestartAttempts);
        Assert.Equal(RuntimeConstants.LinkShimGuestDir + "/link-shim.mjs", RuntimeConstants.LinkShimGuestPath);
        Assert.Equal(RuntimeConstants.LinkShimGuestDir + "/libdshmobile-link.so", RuntimeConstants.LinkShimSoGuestPath);
        Assert.Equal(RuntimeConstants.SandboxWorkspace + "/" + RuntimeConstants.AssetsRelativeDir, RuntimeConstants.AssetsGuestDir);
    }

    [Fact]
    public void RuntimeLayout_IsDshboxSandboxConfig()
    {
        var layout = new RuntimeLayout("/data/data/x/files", "/data/data/x/cache", "/data/app/x/lib/arm64");
        var dirs = new Dictionary<string, string>
        {
            [layout.RuntimeDir] = "runtime", [layout.SandboxDir] = "sandbox", [layout.UserDataDir] = "user-data",
            [layout.LogsDir] = "logs", [layout.BackupsDir] = "backups", [layout.UpdatesDir] = "updates",
            [layout.BrowserEntryDir] = "browser-tools", [layout.PilotEntryDir] = "pilot",
        };
        foreach (var (path, leaf) in dirs)
        {
            Assert.Equal(Path.Combine("/data/data/x/files", leaf), path);
        }
    }

    [Fact]
    public void PluginAllowlists_AreTheBridgesRuntimeController()
    {
        Assert.Equal("^(@[a-z0-9][a-z0-9._-]{0,213}/)?[a-z0-9][a-z0-9._-]{0,213}(@[A-Za-z0-9._^~<>=*-]{1,64})?$", PluginCommand.PackageSpec().ToString());
        Assert.Equal("^(@[a-z0-9][a-z0-9._-]{0,213}/)?[a-z0-9][a-z0-9._-]{0,213}$", PluginCommand.PackageName().ToString());
        Assert.Equal(new HashSet<string>(["web", "headless", "sdk", "sdk-minimal", "acp"]), PluginCommand.Profiles.ToHashSet());
    }

    [Fact]
    public void GuestPaths_AreTheBridgesRuntimeController()
    {
        Assert.Equal("/opt/dshapp/runtime/node_modules/@deepseek-ai/dsh/lib/bin.js", RuntimeConstants.GuestDshBin);
        Assert.Equal("/root/projects/.dsh/dshmobile/bin/dsh", RuntimeConstants.GuestDshWrapper);
    }

    [Fact]
    public void RemoteHosts_StorageAndMessages_AreTheBridgesRemoteHosts()
    {
        Assert.Equal("dshmobile_remote_hosts", RemoteHostProfile.PreferencesName);
        Assert.Equal("hosts", RemoteHostProfile.PreferencesKey);
        Assert.Equal("fingerprint does not match the key last presented by this host", HostKeyPin.TrustRejected);
        Assert.Equal("confirm the host key fingerprint before connecting", RemoteConnectRules.UnknownMessage);
        Assert.Equal("host key differs from the pinned one; connection refused", RemoteConnectRules.ChangedMessage);
        Assert.Equal("public key not accepted; add this phone's key to authorized_keys", RemoteConnectRules.AuthMessage);
        Assert.Equal("dsh not found on the remote PATH", RemoteConnectRules.DshMissingMessage);
        Assert.Equal(("node --version", TimeSpan.FromSeconds(20)), RemoteConnectRules.NodeProbe);
        Assert.Equal(("dsh --version", TimeSpan.FromSeconds(60)), RemoteConnectRules.DshProbe);
        Assert.Equal(8192, RemoteConnectRules.OutputCap);
        Assert.Equal("connect first", RemoteDshRules.NotConnectedMessage);
        Assert.Equal("remote dsh web printed no launch URL; tail: ", RemoteDshRules.NoLaunchUrlPrefix);
        Assert.Equal("remote dsh web exited", RemoteDshRules.ExitedMessage);
        Assert.Equal("connection lost; connect again", RemoteDshRules.ConnectionLostMessage);
    }

    /// <summary>
    /// The guest APK assets (<c>src/DshMobile.Android/Assets/README.md</c>): copied unmodified from DSHBox v1.5.1, except the
    /// link shim, which ADR-0019 rebuilds here (<c>native/link-shim/build.sh</c>; DSHBox's prebuilt one was 7d880b83…1973a).
    /// Any change to them must be deliberate and update that table.
    /// </summary>
    [Theory]
    [InlineData("dshmobile/link-shim.mjs", "c34505677976141016d7ceffb7fc255f8e4de1ac6c232eb6cd7b4d6d0db9b497")]
    [InlineData("dshmobile/libdshmobile-link.so", "400b9ba76c9f60b502a8add61e9c6e1bf3e5e3f3bebe7bf56c78012e482a1730")]
    [InlineData("toolchain-cache/corepack-pnpm-11.1.2.tar.zst", "d9f9677d89a9027df3f5c8d70922789a0c00cdd15b4dece8b36f0d07b0f02498")]
    [InlineData("toolchain-cache/corepack-pnpm-11.1.2.tar.zst.sha256", "c1dcec1a5cf41a8bebdf87cab32ca127c9588772632b7b7f60d5202f07d57dba")]
    public void GuestApkAssets_AreThePinnedBytes(string asset, string sha256)
    {
        var path = Repo.PathOf(["src", "DshMobile.Android", "Assets", .. asset.Split('/')]);
        Assert.Equal(sha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
    }

    [Fact]
    public void GuestApkAssets_AreExactlyTheRegisteredOnes()
    {
        var root = Repo.PathOf("src", "DshMobile.Android", "Assets");
        var found = new[] { "dshmobile", "toolchain-cache" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*", SearchOption.AllDirectories))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);
        Assert.Equal(
            ["dshmobile/libdshmobile-link.so", "dshmobile/link-shim.mjs", "toolchain-cache/corepack-pnpm-11.1.2.tar.zst", "toolchain-cache/corepack-pnpm-11.1.2.tar.zst.sha256"],
            found);
    }

    [Fact]
    public void SshKeys_AliasCommentAlgorithm_AreTheBridgesSshKeys()
    {
        Assert.Equal("dshmobile-ssh-ecdsa-p256", SshIdentity.KeystoreAlias);
        Assert.Equal("dsh-mobile", SshIdentity.Comment);
        Assert.Equal("ecdsa-sha2-nistp256", SshWire.Algorithm);
    }
}
