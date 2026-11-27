using System.Text.RegularExpressions;

namespace DshMobile.Agent.Domain;

/// <summary>
/// 纯托管插件包名与入口 ID 严格白名单校验器（PR-07, N-07-01 / ADR-0033）：
/// 仅允许合规 unscoped/scoped npm 包名及可选精确 SemVer 版本号，拒绝路径穿越、flag 注入与 shell 元字符。
/// </summary>
public static partial class PluginPackageValidator
{
    private const int MaxPackageNameLength = 214;
    private const int MaxEntryIdLength = 256;

    /// <summary>受保护的宿主核心 Cordis 入口 ID（禁止 L1 关闭或卸载）。</summary>
    public static IReadOnlySet<string> ProtectedEntryIds { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "connection",
        "webserver",
        "web-startup",
        "web-runtime",
        "session-controller",
        "settings-controller",
        "plugin-inventory",
        "api-remotes",
        "dshmobile-ui",
    };

    /// <summary>受保护的宿主核心 npm 包名（禁止卸载）。</summary>
    public static IReadOnlySet<string> ProtectedPackageNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "@deepseek-ai/dsh",
        "@deepseek-ai/dsh-web-app",
        "@deepseek-ai/dsh-base",
        "@deepseek-ai/dsh-client-connection",
        "@deepseek-ai/dsh-host-webserver",
        "@deepseek-ai/dsh-api-session-controller",
        "@deepseek-ai/dsh-api-settings-controller",
        "@deepseek-ai/dsh-host-plugin-inventory",
        "@deepseek-ai/dsh-api-remotes",
        "@deepseek-ai/dsh-plugin-manager",
    };

    [GeneratedRegex(@"^(@[a-z0-9][a-z0-9._-]{0,213}/)?[a-z0-9][a-z0-9._-]{0,213}$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageNameRegex();

    [GeneratedRegex(@"^((?:@[a-z0-9][a-z0-9._-]{0,213}/)?[a-z0-9][a-z0-9._-]{0,213})(?:@([0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?))?$", RegexOptions.CultureInvariant)]
    private static partial Regex InstallSpecRegex();

    [GeneratedRegex(@"^[A-Za-z0-9._:/@-]{1,256}$", RegexOptions.CultureInvariant)]
    private static partial Regex EntryIdRegex();

    /// <summary>校验是否为合规 unscoped/scoped npm 包名（不含版本号与任何特殊符号）。</summary>
    public static bool IsValidPackageName(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate) || candidate.Length > MaxPackageNameLength)
        {
            return false;
        }
        if (HasForbiddenControlOrTraversal(candidate))
        {
            return false;
        }
        return IsFullMatch(PackageNameRegex(), candidate);
    }

    /// <summary>校验是否为合规 npm 包安装规格（包名 + 可选精确 SemVer 版本号）。</summary>
    public static bool IsValidInstallSpec(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }
        if (HasForbiddenControlOrTraversal(candidate))
        {
            return false;
        }
        var match = InstallSpecRegex().Match(candidate);
        if (!match.Success || match.Index != 0 || match.Length != candidate.Length)
        {
            return false;
        }
        var packageName = match.Groups[1].Value;
        return packageName.Length <= MaxPackageNameLength && !packageName.Contains("..", StringComparison.Ordinal);
    }

    /// <summary>校验插件入口 ID 是否安全合法。</summary>
    public static bool IsValidEntryId(string? entryId)
    {
        if (string.IsNullOrEmpty(entryId) || entryId.Length > MaxEntryIdLength)
        {
            return false;
        }
        if (HasForbiddenControlOrTraversal(entryId))
        {
            return false;
        }
        return IsFullMatch(EntryIdRegex(), entryId);
    }

    /// <summary>从模块名（含子路径如 <c>@scope/pkg/sub</c>）提取 npm 包名。</summary>
    public static string ExtractPackageName(string? moduleName)
    {
        if (string.IsNullOrWhiteSpace(moduleName))
        {
            return string.Empty;
        }
        var trimmed = moduleName.Trim();
        if (trimmed.StartsWith('@'))
        {
            var firstSlash = trimmed.IndexOf('/');
            if (firstSlash < 0)
            {
                return trimmed;
            }
            var secondSlash = trimmed.IndexOf('/', firstSlash + 1);
            return secondSlash < 0 ? trimmed : trimmed[..secondSlash];
        }
        var slash = trimmed.IndexOf('/');
        return slash < 0 ? trimmed : trimmed[..slash];
    }

    /// <summary>判断是否为系统核心入口（不可关闭/卸载）。</summary>
    public static bool IsProtectedEntry(string entryId, string moduleName)
    {
        var patchId = entryId.StartsWith("include:", StringComparison.Ordinal)
            ? entryId["include:".Length..]
            : entryId;
        if (ProtectedEntryIds.Contains(entryId) || ProtectedEntryIds.Contains(patchId))
        {
            return true;
        }
        var pkg = ExtractPackageName(moduleName);
        return ProtectedPackageNames.Contains(pkg);
    }

    /// <summary>判断是否为核心宿主包名（不可卸载）。</summary>
    public static bool IsProtectedPackage(string packageName) =>
        ProtectedPackageNames.Contains(packageName);

    /// <summary>
    /// 构造受控推荐安装规格：若 <c>PackageName@Version</c> 通过精确 SemVer 安装规格校验则采用之，
    /// 否则退化为通过包名校验的 <c>PackageName</c>；若均不合法则返回 <see langword="null"/>。
    /// </summary>
    public static string? BuildRecommendedInstallSpec(string? packageName, string? version)
    {
        var trimmedPackage = packageName?.Trim();
        if (!IsValidPackageName(trimmedPackage))
        {
            return null;
        }

        var trimmedVersion = version?.Trim();
        if (!string.IsNullOrEmpty(trimmedVersion))
        {
            var pinnedSpec = $"{trimmedPackage}@{trimmedVersion}";
            if (IsValidInstallSpec(pinnedSpec))
            {
                return pinnedSpec;
            }
        }

        return IsValidInstallSpec(trimmedPackage) ? trimmedPackage : null;
    }

    /// <summary>判断已安装插件是否存在诊断异常、启动失败或待重启生效状态。</summary>
    public static bool HasIssue(PluginEntryView entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return !string.IsNullOrWhiteSpace(entry.DiagnosticSummary)
            || string.Equals(entry.FiberPhase, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.FiberPhase, "pending-restart", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 识别携带自定义 WebUI 的插件（ADR-0033：禁止内嵌 WebView 与自创 L2 Schema，UI 必须如实提示）。
    /// </summary>
    public static bool IsCustomWebUiPlugin(string moduleName, string entryId, bool manifestDeclaresClient = false)
    {
        if (manifestDeclaresClient)
        {
            return true;
        }
        var patchId = entryId.StartsWith("include:", StringComparison.Ordinal)
            ? entryId["include:".Length..]
            : entryId;
        if (patchId.StartsWith("ui-", StringComparison.Ordinal))
        {
            return true;
        }
        var pkg = ExtractPackageName(moduleName);
        return pkg.StartsWith("@deepseek-ai/dsh-client-", StringComparison.Ordinal)
            || pkg.Contains("-client-ui-", StringComparison.Ordinal)
            || pkg.EndsWith("-webui", StringComparison.Ordinal)
            || moduleName.EndsWith("/client", StringComparison.Ordinal);
    }

    private static bool HasForbiddenControlOrTraversal(string value)
    {
        if (value.StartsWith('-') || value.Contains("..", StringComparison.Ordinal))
        {
            return true;
        }
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsFullMatch(Regex regex, string input)
    {
        var m = regex.Match(input);
        return m.Success && m.Index == 0 && m.Length == input.Length;
    }
}

/// <summary>已安装插件列表的本地多维筛选模式。</summary>
public enum PluginFilterMode
{
    All,
    Manageable,
    Enabled,
    Protected,
    Issues,
}

/// <summary>插件管理页标签模式（已安装 / 发现远端插件）。</summary>
public enum PluginTabMode
{
    Installed,
    Discover,
}

/// <summary>远端插件目录（npm Registry）搜索条目视图模型。</summary>
public sealed record PluginCatalogEntryView(
    string PackageName,
    string Version,
    string Description,
    IReadOnlyList<string> Keywords,
    string? Publisher,
    long? PublishedAtUnixMs,
    string? NpmUrl,
    bool IsOfficialScope,
    bool IsInstalled,
    string? InstalledVersion,
    string RecommendedInstallSpec);

/// <summary>远端插件目录搜索结果快照。</summary>
public sealed record PluginCatalogSearchResult(
    IReadOnlyList<PluginCatalogEntryView> Items,
    int TotalCount,
    string? DiagnosticWarning = null);

/// <summary>纯托管远端插件目录搜索接口（默认对接 npm Registry HTTPS Search API）。</summary>
public interface IPluginCatalogSearch
{
    Task<Outcome<PluginCatalogSearchResult>> SearchAsync(string query, int limit = 25, CancellationToken ct = default);
}

/// <summary>默认 fail-closed 占位实现（未注入远端搜索服务时返回 <see cref="DshFailureCode.Unsupported"/>）。</summary>
public sealed class NoopPluginCatalogSearch : IPluginCatalogSearch
{
    public static readonly NoopPluginCatalogSearch Instance = new();

    private NoopPluginCatalogSearch() { }

    public Task<Outcome<PluginCatalogSearchResult>> SearchAsync(string query, int limit = 25, CancellationToken ct = default) =>
        Task.FromResult(Outcome<PluginCatalogSearchResult>.Fail(DshFailureCode.Unsupported, "plugins.catalog_search_unavailable"));
}

/// <summary>来自 <c>dsh plugin --profile web list --json</c> 的已安装 npm 包信息。</summary>
public sealed record PluginInstalledPackageView(
    string Name,
    string Version,
    bool HasCustomWebUi = false,
    bool IsProtected = false);

/// <summary>原生插件目录单条展示项（L0 融合视图 + L1 权限标记，无 L2 动态表单）。</summary>
public sealed record PluginEntryView(
    string EntryId,
    string ModuleName,
    string PackageName,
    string DisplayName,
    string? Version,
    bool Enabled,
    string? FiberPhase,
    string? DiagnosticSummary,
    bool HasCustomWebUi,
    bool IsInstalledInProfile,
    bool IsProtected,
    bool CanToggle,
    bool CanUninstall);

/// <summary>L0 插件目录聚合快照。</summary>
public sealed record PluginInventorySnapshotResult(
    bool ManagementAvailable,
    IReadOnlyList<PluginEntryView> Entries,
    IReadOnlyList<PluginInstalledPackageView> InstalledPackages,
    string? DiagnosticWarning = null);

/// <summary>L1 受控插件变更结果（任意变更成功后标记 <see cref="RequiresDshRestart"/> = true）。</summary>
public sealed record PluginMutationResult(
    bool Succeeded,
    bool RequiresDshRestart,
    string Target,
    string? FailureCode = null,
    string? DiagnosticSummary = null);

/// <summary>
/// 宿主侧受控插件操作桥接接口（由 <c>DshMobile.Runtime</c>/<c>Application</c> 实现并通过组合根注入）。
/// </summary>
public interface IPluginOperations
{
    Task<Outcome<IReadOnlyList<PluginInstalledPackageView>>> ListInstalledPackagesAsync(CancellationToken ct = default);
    Task<Outcome<Unit>> InstallPackageAsync(string packageSpec, CancellationToken ct = default);
    Task<Outcome<Unit>> UninstallPackageAsync(string packageName, CancellationToken ct = default);
    Task<Outcome<Unit>> SetPluginEnabledAsync(string entryId, string moduleName, bool enabled, CancellationToken ct = default);
    Task<Outcome<Unit>> RestartDshHostAsync(CancellationToken ct = default);
}

/// <summary>默认 fail-closed 占位实现（未注入运行时操作时返回 <see cref="DshFailureCode.Unsupported"/>）。</summary>
public sealed class NoopPluginOperations : IPluginOperations
{
    public static readonly NoopPluginOperations Instance = new();

    private NoopPluginOperations() { }

    public Task<Outcome<IReadOnlyList<PluginInstalledPackageView>>> ListInstalledPackagesAsync(CancellationToken ct = default) =>
        Task.FromResult(Outcome<IReadOnlyList<PluginInstalledPackageView>>.Fail(DshFailureCode.Unsupported, "plugins.cli_unavailable"));

    public Task<Outcome<Unit>> InstallPackageAsync(string packageSpec, CancellationToken ct = default) =>
        Task.FromResult(Outcome<Unit>.Fail(DshFailureCode.Unsupported, "plugins.install_unavailable"));

    public Task<Outcome<Unit>> UninstallPackageAsync(string packageName, CancellationToken ct = default) =>
        Task.FromResult(Outcome<Unit>.Fail(DshFailureCode.Unsupported, "plugins.uninstall_unavailable"));

    public Task<Outcome<Unit>> SetPluginEnabledAsync(string entryId, string moduleName, bool enabled, CancellationToken ct = default) =>
        Task.FromResult(Outcome<Unit>.Fail(DshFailureCode.Unsupported, "plugins.toggle_unavailable"));

    public Task<Outcome<Unit>> RestartDshHostAsync(CancellationToken ct = default) =>
        Task.FromResult(Outcome<Unit>.Fail(DshFailureCode.Unsupported, "plugins.restart_unavailable"));
}
