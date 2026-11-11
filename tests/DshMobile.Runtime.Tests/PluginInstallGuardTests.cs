// Transcribed from DSHBox plugin-manager InstallGuardTest.kt (GPL-3.0), commit 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4:
// every upstream case keeps its name (PascalCase) and assertions; RemoveAsync cases at the end are DSH Mobile's own.
// The plugin-manager module is not part of this repo's Gradle build, so the upstream suite cannot run side by side here.

using DshMobile.Runtime.Guest;
using DshMobile.Runtime.Plugins;

namespace DshMobile.Runtime.Tests;

public class PluginInstallGuardTests
{
    private sealed class FakeOps(bool addOk = true, bool removeOk = true, Action? onAdd = null) : IPluginOps
    {
        public List<string> Commands { get; } = [];

        public List<string> Verbs() => Commands.Select(c => c.Split(' ')[0]).ToList();

        public Task<GuestPluginResult> AddAsync(string spec, CancellationToken ct)
        {
            Commands.Add($"add '{spec}'");
            onAdd?.Invoke();
            return Task.FromResult(addOk ? new GuestPluginResult(true, 0, "added", null) : new GuestPluginResult(false, 1, "ERR_PNPM_FETCH_404", "GUEST_CMD_FAILED: guest command exit=1"));
        }

        public Task<GuestPluginResult> RemoveAsync(string name, CancellationToken ct)
        {
            Commands.Add($"remove '{name}'");
            return Task.FromResult(removeOk ? new GuestPluginResult(true, 0, "removed", null) : new GuestPluginResult(false, 1, "remove failed", "GUEST_CMD_FAILED: guest command exit=1"));
        }
    }

    private sealed class FakeProfile(IReadOnlyDictionary<string, PluginPackageFacts> facts, IReadOnlySet<string>? initial = null) : IPluginProfileFacts
    {
        public IReadOnlySet<string>? Names { get; set; } = initial ?? new HashSet<string>();

        public List<string> Queried { get; } = [];

        public IReadOnlySet<string>? DependencyNames() => Names;

        public PluginPackageFacts Facts(string name)
        {
            Queried.Add(name);
            return facts.TryGetValue(name, out var f) ? f : PluginPackageFacts.Missing(name);
        }
    }

    private static PluginPackageFacts Loadable(string name, bool hasBundle = true, bool hasClient = false) =>
        new(name, "1.0.0", true, true, true, hasBundle, hasClient, BundleEntryExists: hasBundle, ClientEntryExists: hasClient);

    private static Dictionary<string, PluginPackageFacts> Facts(params PluginPackageFacts[] f) => f.ToDictionary(x => x.Name);

    private static Task<PluginChangeResult> Install(FakeOps ops, FakeProfile profile, string target, string declared) =>
        new PluginInstallGuard(ops, profile).InstallAsync(target, declared, CancellationToken.None);

    [Fact]
    public async Task LoadablePackagePassesWithoutRollback()
    {
        var ops = new FakeOps();
        var r = await Install(ops, new FakeProfile(Facts(Loadable("foo"))), "foo", "foo");
        Assert.True(r.Ok);
        Assert.Equal(["add"], ops.Verbs());
    }

    [Fact]
    public async Task CommandSuccessButPackageMissingIsAFailureAndRollsBack()
    {
        var profile = new FakeProfile(Facts());
        var ops = new FakeOps(onAdd: () => profile.Names = new HashSet<string> { "foo" });
        var r = await Install(ops, profile, "foo", "foo");
        Assert.False(r.Ok);
        Assert.Equal("INSTALL_VERIFY_FAILED", r.Code);
        Assert.Contains("已执行回收", r.Message, StringComparison.Ordinal);
        Assert.True(r.RolledBack);
        Assert.Equal(["add", "remove"], ops.Verbs());
    }

    [Fact]
    public async Task UnreadableManifestIsAFailure()
    {
        var profile = new FakeProfile(Facts(PluginPackageFacts.Unreadable("foo")));
        var ops = new FakeOps(onAdd: () => profile.Names = new HashSet<string> { "foo" });
        var r = await Install(ops, profile, "foo", "foo");
        Assert.False(r.Ok);
        Assert.Contains("package.json 读不出来", r.Message, StringComparison.Ordinal);
        Assert.Equal(["add", "remove"], ops.Verbs());
    }

    [Fact]
    public async Task PackageWithoutDshFieldIsAFailure()
    {
        var r = await Install(new FakeOps(), new FakeProfile(Facts(new PluginPackageFacts("foo", "1.0.0", true, true, false, false, false))), "foo", "foo");
        Assert.False(r.Ok);
        Assert.Contains("没有 dsh 字段", r.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DshFieldWithoutEntryArtifactIsAFailure()
    {
        var r = await Install(new FakeOps(), new FakeProfile(Facts(Loadable("foo", hasBundle: false, hasClient: false))), "foo", "foo");
        Assert.False(r.Ok);
        Assert.Contains("没有入口产物", r.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeclaredEntryThatDoesNotExistOnDiskIsAFailure()
    {
        var profile = new FakeProfile(Facts(Loadable("foo") with { BundleEntryExists = false }));
        var ops = new FakeOps(onAdd: () => profile.Names = new HashSet<string> { "foo" });
        var r = await Install(ops, profile, "foo", "foo");
        Assert.False(r.Ok);
        Assert.Contains("入口产物", r.Message, StringComparison.Ordinal);
        Assert.Equal(["add", "remove"], ops.Verbs());
    }

    [Fact]
    public async Task ClientOnlyPackageIsLoadable() =>
        Assert.True((await Install(new FakeOps(), new FakeProfile(Facts(Loadable("foo", hasBundle: false, hasClient: true))), "foo", "foo")).Ok);

    [Fact]
    public async Task CommandFailureIsPassedThroughWithoutRollback()
    {
        var ops = new FakeOps(addOk: false);
        var r = await Install(ops, new FakeProfile(Facts()), "foo", "foo");
        Assert.False(r.Ok);
        Assert.Equal("GUEST_CMD_FAILED", r.Code);
        Assert.Contains("ERR_PNPM_FETCH_404", r.Lines);
        Assert.Equal(["add"], ops.Verbs());
    }

    [Fact]
    public async Task RepoNameDifferentFromPackageNameStillSucceeds()
    {
        var profile = new FakeProfile(Facts(Loadable("dsh-market")));
        var ops = new FakeOps(onAdd: () => profile.Names = new HashSet<string> { "dsh-market" });
        var r = await Install(ops, profile, "github:owner/awesome-dsh-plugin", "awesome-dsh-plugin");
        Assert.True(r.Ok);
        Assert.Equal(["dsh-market"], profile.Queried.Distinct());
        Assert.Equal(["add"], ops.Verbs());
    }

    [Fact]
    public async Task GithubTargetNeverDerivesAPackageNameFromTheRepo()
    {
        var profile = new FakeProfile(Facts(Loadable("repo")));
        var ops = new FakeOps();
        var r = await Install(ops, profile, "github:owner/repo#main", "");
        Assert.True(r.Ok);
        Assert.Empty(profile.Queried);
        Assert.Contains(r.Lines, l => l.Contains("未校验", StringComparison.Ordinal));
        Assert.Equal(["add"], ops.Verbs());
    }

    [Fact]
    public async Task RollbackOnlyTouchesTheNewlyAddedPackage()
    {
        var profile = new FakeProfile(Facts(Loadable("real-dep")), new HashSet<string> { "real-dep" });
        var ops = new FakeOps(onAdd: () => profile.Names = new HashSet<string> { "real-dep", "new-broken" });
        var r = await Install(ops, profile, "new-broken", "real-dep");
        Assert.False(r.Ok);
        Assert.All(profile.Queried, q => Assert.Equal("new-broken", q));
        Assert.Equal(["add", "remove"], ops.Verbs());
        Assert.Contains("remove 'new-broken'", ops.Commands);
        Assert.DoesNotContain("remove 'real-dep'", ops.Commands);
    }

    [Fact]
    public async Task UnrelatedLoadablePackageCannotStandInForTheAddedOne()
    {
        var profile = new FakeProfile(Facts(Loadable("unrelated")));
        var ops = new FakeOps(onAdd: () => profile.Names = new HashSet<string> { "new-broken" });
        var r = await Install(ops, profile, "new-broken", "unrelated");
        Assert.False(r.Ok);
        Assert.Contains("remove 'new-broken'", ops.Commands);
        Assert.DoesNotContain("remove 'unrelated'", ops.Commands);
    }

    [Fact]
    public async Task AliasInstallIsVerifiedUnderTheDependencyKeyNotTheTarget()
    {
        var profile = new FakeProfile(Facts());
        var ops = new FakeOps(onAdd: () => profile.Names = new HashSet<string> { "alias" });
        var r = await Install(ops, profile, "npm:real-pkg@1.0.0", "alias");
        Assert.False(r.Ok);
        Assert.Equal(["alias"], profile.Queried.Distinct());
        Assert.Contains("remove 'alias'", ops.Commands);
    }

    [Fact]
    public async Task UnreadableDependencyListFallsBackToTheDeclaredName()
    {
        var profile = new FakeProfile(Facts(Loadable("foo"))) { Names = null };
        var r = await Install(new FakeOps(), profile, "foo", "foo");
        Assert.True(r.Ok);
        Assert.Contains("foo", profile.Queried);
    }

    [Fact]
    public async Task UnreadableDependencyListAndNoDeclaredNameStaysUnverified()
    {
        var ops = new FakeOps();
        var r = await Install(ops, new FakeProfile(Facts()) { Names = null }, "weird-target", "");
        Assert.True(r.Ok);
        Assert.Contains(r.Lines, l => l.Contains("未校验", StringComparison.Ordinal));
        Assert.Equal(["add"], ops.Verbs());
    }

    [Fact]
    public async Task DeclaredNameIsUsedAsFallbackWhenNothingNewWasAdded()
    {
        var profile = new FakeProfile(Facts(Loadable("catalog-name")), new HashSet<string> { "catalog-name" });
        var r = await Install(new FakeOps(), profile, "https://example.com/pkg-1.0.0.tgz", "catalog-name");
        Assert.True(r.Ok);
        Assert.Contains("catalog-name", profile.Queried);
    }

    [Fact]
    public async Task UnverifiableTargetStaysInstalledAndSaysSo()
    {
        var ops = new FakeOps();
        var r = await Install(ops, new FakeProfile(Facts()), "#", "");
        Assert.True(r.Ok);
        Assert.Contains(r.Lines, l => l.Contains("未校验", StringComparison.Ordinal));
        Assert.Equal(["add"], ops.Verbs());
    }

    [Fact]
    public async Task RollbackFailureIsReportedInTheMessage()
    {
        var r = await Install(new FakeOps(removeOk: false), new FakeProfile(Facts()), "foo", "foo");
        Assert.False(r.Ok);
        Assert.Contains("回收", r.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RollbackFailureAfterDiffIsReportedAndNotMarkedRolledBack()
    {
        // Not upstream: the diff branch with a failing remove names the package and keeps RolledBack false.
        var profile = new FakeProfile(Facts());
        var ops = new FakeOps(removeOk: false, onAdd: () => profile.Names = new HashSet<string> { "foo" });
        var r = await Install(ops, profile, "foo", "foo");
        Assert.False(r.RolledBack);
        Assert.Contains("回收 foo 也失败了", r.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailureMessageKeepsExactlyOneBilingualSeparator()
    {
        var r = await Install(new FakeOps(), new FakeProfile(Facts()), "foo", "foo");
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(r.Message!, " / "));
    }

    [Fact]
    public void InstallGapCoversEveryLoadabilityRequirement()
    {
        Assert.Null(PluginPackageRules.InstallGap(Loadable("foo")));
        Assert.NotNull(PluginPackageRules.InstallGap(Loadable("foo") with { Installed = false }));
        Assert.NotNull(PluginPackageRules.InstallGap(Loadable("foo") with { ManifestReadable = false }));
        Assert.NotNull(PluginPackageRules.InstallGap(Loadable("foo") with { HasDshField = false }));
        Assert.NotNull(PluginPackageRules.InstallGap(Loadable("foo") with { HasBundle = false }));
        Assert.NotNull(PluginPackageRules.InstallGap(Loadable("foo") with { BundleEntryExists = false }));
        Assert.Null(PluginPackageRules.InstallGap(Loadable("foo", hasBundle: false, hasClient: true)));
        Assert.NotNull(PluginPackageRules.InstallGap(Loadable("foo", hasBundle: false, hasClient: true) with { ClientEntryExists = false }));
        // Carrier form: own artifact missing, mounted package has one.
        Assert.Null(PluginPackageRules.InstallGap(Loadable("foo") with { BundleEntryExists = false, HasCarrierEntry = true }));
    }

    [Fact]
    public void DeclaredPackageNameAcceptsOnlyPackageShapes()
    {
        Assert.Equal("foo", PluginPackageRules.DeclaredPackageName("foo"));
        Assert.Equal("@scope/foo", PluginPackageRules.DeclaredPackageName(" @scope/foo "));
        Assert.Null(PluginPackageRules.DeclaredPackageName(""));
        Assert.Null(PluginPackageRules.DeclaredPackageName("   "));
        Assert.Null(PluginPackageRules.DeclaredPackageName("not a package name"));
        Assert.Null(PluginPackageRules.DeclaredPackageName("github:owner/repo"));
    }

    // ── RemoveAsync (DSH Mobile's own post-check) ─────────────────────────

    [Fact]
    public async Task RemoveThatLeavesTheDependencyIsAVerifyFailure()
    {
        var profile = new FakeProfile(Facts(), new HashSet<string> { "foo" });
        var r = await new PluginInstallGuard(new FakeOps(), profile).RemoveAsync("foo", CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal("REMOVE_VERIFY_FAILED", r.Code);
    }

    [Fact]
    public async Task RemoveThatDropsTheDependencySucceeds()
    {
        var profile = new FakeProfile(Facts(), new HashSet<string> { "foo", "bar" });
        var ops = new FakeOps();
        var guard = new PluginInstallGuard(new RemovingOps(ops, profile), profile);
        var r = await guard.RemoveAsync("foo", CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal(["remove"], ops.Verbs());
    }

    [Fact]
    public async Task RemoveCommandFailureIsPassedThrough()
    {
        var r = await new PluginInstallGuard(new FakeOps(removeOk: false), new FakeProfile(Facts())).RemoveAsync("foo", CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal("GUEST_CMD_FAILED", r.Code);
    }

    private sealed class RemovingOps(FakeOps inner, FakeProfile profile) : IPluginOps
    {
        public Task<GuestPluginResult> AddAsync(string spec, CancellationToken ct) => inner.AddAsync(spec, ct);

        public async Task<GuestPluginResult> RemoveAsync(string name, CancellationToken ct)
        {
            var r = await inner.RemoveAsync(name, ct);
            profile.Names = profile.Names!.Where(n => n != name).ToHashSet();
            return r;
        }
    }
}
