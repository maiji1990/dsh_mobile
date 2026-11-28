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
    Updatable,
}

/// <summary>插件受控变更的类型，用于区分安装、卸载、启停、重启与升级的结果语义。</summary>
public enum PluginMutationKind
{
    Install,
    Uninstall,
    Toggle,
    Restart,
    Upgrade,
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
    string RecommendedInstallSpec,
    bool HasUpdate = false);

/// <summary>远端插件目录搜索结果快照。</summary>
public sealed record PluginCatalogSearchResult(
    IReadOnlyList<PluginCatalogEntryView> Items,
    int TotalCount,
    string? DiagnosticWarning = null);

/// <summary>远端插件包完整详情与 README 视图模型（npm Registry）。</summary>
public sealed record PluginCatalogDetailView(
    string PackageName,
    string LatestVersion,
    string Description,
    string? ReadmeMarkdown,
    IReadOnlyList<string> Keywords,
    string? License,
    string? Author,
    string? HomepageUrl,
    string? RepositoryUrl,
    string? NpmUrl,
    IReadOnlyList<string> AvailableVersions,
    bool IsOfficialScope,
    bool HasBundleManifest,
    bool HasCustomWebUi,
    string RecommendedInstallSpec);

/// <summary>
/// 单个 npm 包的最新版本查询结果（由宿主侧 registry 探测返回）。
/// <see cref="LatestVersion"/> 为 <see langword="null"/> 表示该包本次查询失败或没有可用的 <c>latest</c> 标签，
/// <see cref="DiagnosticCode"/> 给出经脱敏的稳定原因，供调用方统计失败数并在诊断摘要中如实展示。
/// </summary>
public sealed record PluginLatestVersionView(
    string PackageName,
    string? LatestVersion,
    IReadOnlyList<string>? AvailableVersions = null,
    string? DiagnosticCode = null);

/// <summary>本地已安装包在安全边界内读取的详情文档与元信息。</summary>
public sealed record PluginLocalPackageDetailView(
    string PackageName,
    string? Version = null,
    string? Title = null,
    string? Description = null,
    string? Markdown = null,
    string? DocumentOrigin = null,
    string? DocumentPath = null,
    string? Author = null,
    string? License = null,
    string? HomepageUrl = null,
    string? RepositoryUrl = null,
    bool HasBundleManifest = false,
    bool HasCustomWebUi = false);

/// <summary>官方 Schemastery 配置字段映射类型（受控 L2，绝不自创专有 Schema）。</summary>
public enum PluginConfigFieldKind
{
    Boolean,
    Number,
    SingleSelect,
    Secret,
    Text,
    JsonFallback,
}

/// <summary>官方 Schemastery <c>union(const)</c> 单选字段候选项。</summary>
public sealed record PluginConfigSelectOptionView(
    string RawValueJson,
    string DisplayLabel,
    string? Description = null);

/// <summary>官方 <c>settings/*</c> 解析后的强类型配置字段视图。</summary>
public sealed record PluginConfigFieldView(
    IReadOnlyList<string> Path,
    string KeyDisplay,
    string Label,
    string? Description,
    PluginConfigFieldKind Kind,
    string? CurrentValueJson,
    string? BaseValueJson,
    bool HasUserOverride,
    bool IsSecretSet,
    IReadOnlyList<PluginConfigSelectOptionView> Options,
    double? Min = null,
    double? Max = null,
    double? Step = null,
    bool ReadOnly = false);

/// <summary>纯托管远端插件目录搜索与详情接口（默认对接 npm Registry HTTPS API）。</summary>
public interface IPluginCatalogSearch
{
    Task<Outcome<PluginCatalogSearchResult>> SearchAsync(string query, int limit = 25, CancellationToken ct = default);

    Task<Outcome<PluginCatalogDetailView>> GetPackageDetailAsync(string packageName, CancellationToken ct = default) =>
        Task.FromResult(Outcome<PluginCatalogDetailView>.Fail(DshFailureCode.Unsupported, "plugins.catalog_detail_unavailable"));

    /// <summary>
    /// 批量查询多个 npm 包的最新版本。默认实现 fail-closed，由宿主侧 registry 探测服务覆盖；
    /// 调用方必须在返回 <see cref="DshFailureCode.Unsupported"/> 时如实提示“检查更新暂不可用”，不得伪造结果。
    /// </summary>
    Task<Outcome<IReadOnlyList<PluginLatestVersionView>>> GetLatestVersionsAsync(
        IReadOnlyList<string> packageNames,
        CancellationToken ct = default) =>
        Task.FromResult(Outcome<IReadOnlyList<PluginLatestVersionView>>.Fail(
            DshFailureCode.Unsupported, "plugins.catalog_versions_unavailable"));
}

/// <summary>默认 fail-closed 占位实现（未注入远端搜索服务时返回 <see cref="DshFailureCode.Unsupported"/>）。</summary>
public sealed class NoopPluginCatalogSearch : IPluginCatalogSearch
{
    public static readonly NoopPluginCatalogSearch Instance = new();

    private NoopPluginCatalogSearch() { }

    public Task<Outcome<PluginCatalogSearchResult>> SearchAsync(string query, int limit = 25, CancellationToken ct = default) =>
        Task.FromResult(Outcome<PluginCatalogSearchResult>.Fail(DshFailureCode.Unsupported, "plugins.catalog_search_unavailable"));

    public Task<Outcome<PluginCatalogDetailView>> GetPackageDetailAsync(string packageName, CancellationToken ct = default) =>
        Task.FromResult(Outcome<PluginCatalogDetailView>.Fail(DshFailureCode.Unsupported, "plugins.catalog_detail_unavailable"));
}

/// <summary>来自 <c>dsh plugin --profile web list --json</c> 与本地 package manifest 的已安装 npm 包信息。</summary>
public sealed record PluginInstalledPackageView(
    string Name,
    string Version,
    bool HasCustomWebUi = false,
    bool IsProtected = false,
    bool HasBundleManifest = true,
    string? Description = null,
    string? Author = null,
    string? License = null,
    string? HomepageUrl = null,
    string? RepositoryUrl = null);

/// <summary>原生插件目录单条展示项（L0 融合视图 + L1 权限标记 + 官方 <c>settings/*</c> 实时配置能力标注）。</summary>
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
    bool CanUninstall,
    string? Description = null,
    bool HasBundleManifest = true,
    bool IsTrimmedByNativeMode = false,
    bool HasConfigurableSettings = false,
    string? SettingsNamespace = null,
    string? Author = null,
    string? License = null,
    string? HomepageUrl = null,
    string? RepositoryUrl = null,
    string? Availability = null,
    string? LatestVersion = null,
    bool HasUpdate = false,
    bool CanUpgrade = false);

/// <summary>L0 插件目录聚合快照。</summary>
public sealed record PluginInventorySnapshotResult(
    bool ManagementAvailable,
    IReadOnlyList<PluginEntryView> Entries,
    IReadOnlyList<PluginInstalledPackageView> InstalledPackages,
    string? DiagnosticWarning = null);

/// <summary>
/// 一次“检查插件更新”的结果：<see cref="Entries"/> 已回填 <c>Availability</c>/<c>LatestVersion</c>/
/// <c>HasUpdate</c>/<c>CanUpgrade</c>，调用方可直接替换本地列表。
/// </summary>
public sealed record PluginUpdateCheckResult(
    IReadOnlyList<PluginEntryView> Entries,
    int CheckedCount,
    int FailedCount,
    string? DiagnosticWarning = null);

/// <summary>L1 受控插件变更结果（任意变更成功后标记 <see cref="RequiresDshRestart"/> = true）。</summary>
public sealed record PluginMutationResult(
    bool Succeeded,
    bool RequiresDshRestart,
    string Target,
    string? FailureCode = null,
    string? DiagnosticSummary = null,
    PluginMutationKind Kind = PluginMutationKind.Install,
    string? FromVersion = null,
    string? ToVersion = null);

/// <summary>
/// 宿主侧受控插件操作桥接接口（由 <c>DshMobile.Runtime</c>/<c>Application</c> 实现并通过组合根注入）。
/// </summary>
public interface IPluginOperations
{
    private static readonly IReadOnlySet<string> EmptyStringSet = new HashSet<string>(StringComparer.Ordinal);

    Task<Outcome<IReadOnlyList<PluginInstalledPackageView>>> ListInstalledPackagesAsync(CancellationToken ct = default);
    Task<Outcome<Unit>> InstallPackageAsync(string packageSpec, CancellationToken ct = default);
    Task<Outcome<Unit>> UninstallPackageAsync(string packageName, CancellationToken ct = default);
    Task<Outcome<Unit>> SetPluginEnabledAsync(string entryId, string moduleName, bool enabled, CancellationToken ct = default);
    Task<Outcome<Unit>> RestartDshHostAsync(CancellationToken ct = default);

    /// <summary>
    /// 受控升级到精确版本。默认实现 fail-closed（不猜测安装语义），由运行时桥接层覆盖为
    /// <c>dsh plugin --profile web add &lt;包名&gt;@&lt;精确版本&gt;</c>。
    /// </summary>
    Task<Outcome<Unit>> UpgradePackageAsync(string packageName, string targetVersion, CancellationToken ct = default) =>
        Task.FromResult(Outcome<Unit>.Fail(DshFailureCode.Unsupported, "plugins.upgrade_unavailable"));

    Task<Outcome<PluginLocalPackageDetailView>> ReadLocalPackageDetailAsync(string packageName, string language, CancellationToken ct = default) =>
        Task.FromResult(Outcome<PluginLocalPackageDetailView>.Fail(DshFailureCode.Unsupported, "plugins.local_detail_unavailable"));

    /// <summary>
    /// Slice 1A：只读本地已安装包的 <c>dshmobile.sdui.json</c> 扩展清单。
    /// 默认实现 fail-closed，由运行时桥接层覆盖为安全边界内的文件读取。
    /// </summary>
    Task<Outcome<string>> ReadLocalSduiManifestAsync(string packageName, CancellationToken ct = default) =>
        Task.FromResult(Outcome<string>.Fail(DshFailureCode.Unsupported, "plugins.local_sdui_unavailable"));

    IReadOnlySet<string> GetNativeTrimmedEntryIds() => EmptyStringSet;

    bool IsFullWebUiCompatEnabled => false;
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

    public Task<Outcome<Unit>> UpgradePackageAsync(string packageName, string targetVersion, CancellationToken ct = default) =>
        Task.FromResult(Outcome<Unit>.Fail(DshFailureCode.Unsupported, "plugins.upgrade_unavailable"));
}
