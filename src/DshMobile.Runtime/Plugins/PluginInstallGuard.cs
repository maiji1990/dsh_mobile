// Ported from DSHBox plugin-manager (GPL-3.0), commit 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4:
//   plugin-manager/src/main/java/com/dshbox/pluginmanager/market/data/InstallGuard.kt (InstallGuard.install)
// Registered in docs/UPSTREAMS.md. RemoveAsync's post-check is DSH Mobile's own (upstream has none).
//
// Why: `dsh plugin add` exits 0 for packages that cannot load (no `dsh` field — e.g. a plain npm package, observed on
// device with DSH 0.2.1-rc.5 — missing entry artifacts, broken manifest). The guard verifies the packages the install
// actually added (before/after dependency diff, never a name guessed from the target) and removes only those.

using DshMobile.Runtime.Guest;

namespace DshMobile.Runtime.Plugins;

/// <summary>The two guest operations the guard needs (C# PRoot launcher in production, a fake in tests).</summary>
public interface IPluginOps
{
    Task<GuestPluginResult> AddAsync(string spec, CancellationToken ct);

    Task<GuestPluginResult> RemoveAsync(string name, CancellationToken ct);
}

/// <summary>Runs <c>dsh plugin add/remove</c> for one profile through <see cref="GuestPlugins.RunAsync"/>.</summary>
public sealed class GuestPluginOps(ProotGuestProcess exec, string profile) : IPluginOps
{
    public Task<GuestPluginResult> AddAsync(string spec, CancellationToken ct) => GuestPlugins.RunAsync(exec, "add", profile, spec, ct);

    public Task<GuestPluginResult> RemoveAsync(string name, CancellationToken ct) => GuestPlugins.RunAsync(exec, "remove", profile, name, ct);
}

/// <summary>
/// Outcome of a guarded change. <see cref="Ok"/> false always carries a stable <see cref="Code"/>; <see cref="Lines"/>
/// holds command output and guard actions (rollback, "unverified") in order.
/// </summary>
public sealed record PluginChangeResult(bool Ok, string? Code, string? Message, IReadOnlyList<string> Lines, bool RolledBack = false)
{
    public const string InstallVerifyFailed = "INSTALL_VERIFY_FAILED";
    public const string RemoveVerifyFailed = "REMOVE_VERIFY_FAILED";
    public const string CommandFailed = "GUEST_CMD_FAILED";
    public const string Unverified = "未校验：无法确定它装成了哪个包，请刷新列表核对 / unverified: installed package unknown";
}

public sealed class PluginInstallGuard(IPluginOps ops, IPluginProfileFacts profile)
{
    /// <summary>
    /// Installs <paramref name="spec"/> and verifies it; rolls back only packages that this install newly added and that
    /// fail verification. <paramref name="declaredName"/> is a verification-only fallback (never removed).
    /// </summary>
    public async Task<PluginChangeResult> InstallAsync(string spec, string? declaredName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var lines = new List<string>();
        var before = profile.DependencyNames();
        var added = await ops.AddAsync(spec, ct).ConfigureAwait(false);
        Collect(lines, added);
        if (!added.Ok)
        {
            return CommandFailure(added, lines);
        }
        var after = profile.DependencyNames();

        var diff = before is not null && after is not null ? after.Except(before).OrderBy(n => n, StringComparer.Ordinal).ToList() : null;
        var usedDiff = diff is { Count: > 0 };
        var newNames = usedDiff
            ? diff!
            : PluginPackageRules.DeclaredPackageName(declaredName) is { } fallback ? [fallback] : new List<string>();
        lines.Add($"guard: dependencies before={Count(before)} after={Count(after)} new=[{string.Join(", ", diff ?? [])}]"
            + (usedDiff ? "" : $" fallback=[{string.Join(", ", newNames)}] (verify only, never removed)"));

        if (newNames.Count == 0)
        {
            // Not knowing the package is not a failed install: keep it and say it was not verified.
            lines.Add(PluginChangeResult.Unverified);
            return new PluginChangeResult(true, null, null, lines);
        }

        var broken = newNames.Where(n => PluginPackageRules.InstallGap(profile.Facts(n)) is not null).ToList();
        if (broken.Count == 0)
        {
            return new PluginChangeResult(true, null, null, lines);
        }
        var reason = PluginPackageRules.InstallGap(profile.Facts(broken[0])) ?? new PluginInstallGap("未知原因", "unknown reason");

        if (!usedDiff)
        {
            // The fallback name has no evidence this install touched it: verify, but never remove it.
            return new PluginChangeResult(false, PluginChangeResult.InstallVerifyFailed,
                $"命令报告成功，但目录声明的包不可加载：{reason.Zh}（无法确认它是否本次新增，未执行回收） / " +
                $"the command reported success, but the catalog-declared package is not loadable: {reason.En} (not confirmed as newly added, so nothing was removed)",
                lines);
        }

        var failedRollbacks = new List<string>();
        foreach (var name in broken)
        {
            lines.Add($"rollback: dsh plugin remove '{name}'");
            var removed = await ops.RemoveAsync(name, ct).ConfigureAwait(false);
            Collect(lines, removed);
            if (!removed.Ok)
            {
                failedRollbacks.Add(name);
            }
        }
        var rollbackNote = failedRollbacks.Count == 0
            ? ""
            : $"；回收 {string.Join("、", failedRollbacks)} 也失败了，磁盘上可能留下半个包 / the rollback ({string.Join("、", failedRollbacks)}) failed too, so a half-installed package may remain";
        return new PluginChangeResult(false, PluginChangeResult.InstallVerifyFailed,
            $"命令报告成功，但装出来的包不可加载：{reason.Zh}。已执行回收{rollbackNote} / " +
            $"the command reported success, but the installed package is not loadable: {reason.En}; it has been rolled back",
            lines, RolledBack: failedRollbacks.Count == 0);
    }

    /// <summary>
    /// Removes <paramref name="name"/> and checks it left the profile's dependencies (DSH Mobile's own post-check:
    /// exit 0 with the dependency still present is <see cref="PluginChangeResult.RemoveVerifyFailed"/>).
    /// </summary>
    public async Task<PluginChangeResult> RemoveAsync(string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);
        var lines = new List<string>();
        var removed = await ops.RemoveAsync(name, ct).ConfigureAwait(false);
        Collect(lines, removed);
        if (!removed.Ok)
        {
            return CommandFailure(removed, lines);
        }
        var after = profile.DependencyNames();
        lines.Add($"guard: dependencies after={Count(after)} still-present={(after?.Contains(name) ?? false)}");
        if (after is null)
        {
            lines.Add("未校验：profile 清单读不出来 / unverified: the profile manifest could not be read");
            return new PluginChangeResult(true, null, null, lines);
        }
        return after.Contains(name)
            ? new PluginChangeResult(false, PluginChangeResult.RemoveVerifyFailed,
                $"命令报告成功，但 {name} 仍在 profile 依赖里 / the command reported success, but {name} is still a profile dependency", lines)
            : new PluginChangeResult(true, null, null, lines);
    }

    private static string Count(IReadOnlySet<string>? names) =>
        names?.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unreadable";

    private static void Collect(List<string> lines, GuestPluginResult r)
    {
        if (r.Output.Length > 0)
        {
            lines.AddRange(r.Output.Split('\n'));
        }
    }

    private static PluginChangeResult CommandFailure(GuestPluginResult r, List<string> lines)
    {
        var error = r.Error ?? $"{PluginChangeResult.CommandFailed}: exit={r.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}";
        var colon = error.IndexOf(':', StringComparison.Ordinal);
        var code = colon > 0 && error[..colon].All(c => char.IsAsciiLetterUpper(c) || c == '_') ? error[..colon] : PluginChangeResult.CommandFailed;
        return new PluginChangeResult(false, code, error, lines);
    }
}
