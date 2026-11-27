using System.Text.RegularExpressions;
using DshMobile.Runtime.Guest;

namespace DshMobile.Runtime.Plugins;

/// <summary>
/// 受控插件命令与参数白名单校验（PR-07, N-07-01）：
/// 仅允许合规的 unscoped/scoped npm 包名及可选精确 SemVer 版本号，拒绝路径穿越、flag 注入与 shell 元字符；
/// 所有 guest CLI 调用均严格通过 <see cref="GuestCommandLine"/> 逐 token 单引号转义生成 argv。
/// </summary>
public static partial class PluginCommandRules
{
    /// <summary>受支持写入/查询的 DSH profile 名称白名单（禁止触碰 desktop 保留 profile）。</summary>
    public static IReadOnlySet<string> AllowedProfiles { get; } =
        new HashSet<string>(StringComparer.Ordinal) { RuntimeConstants.DshWebProfile };

    /// <summary>禁止通过原生 L1 开关关闭或卸载的核心宿主控制器入口 ID。</summary>
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

    /// <summary>禁止通过原生 L1 开关关闭或卸载的核心宿主包名。</summary>
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

    /// <summary>guest 内插件命令所需的 PATH 环境变量（包含 link-shim 与随包 pnpm/dsh 包装）。</summary>
    public const string GuestPathExport = "export PATH=/opt/dshmobile:/root/projects/.dsh/dshmobile/bin:$PATH";

    /// <summary>guest 内 <c>dsh</c> CLI 包装可执行文件路径。</summary>
    public const string GuestDshWrapper = RuntimeConstants.AssetsGuestDir + "/bin/dsh";

    private const int MaxPackageNameLength = 214;
    private const int MaxEntryIdLength = 256;

    [GeneratedRegex(@"^(@[a-z0-9][a-z0-9._-]{0,213}/)?[a-z0-9][a-z0-9._-]{0,213}$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageNameRegex();

    [GeneratedRegex(@"^((?:@[a-z0-9][a-z0-9._-]{0,213}/)?[a-z0-9][a-z0-9._-]{0,213})(?:@([0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?))?$", RegexOptions.CultureInvariant)]
    private static partial Regex InstallSpecRegex();

    [GeneratedRegex(@"^[A-Za-z0-9._:/@-]{1,256}$", RegexOptions.CultureInvariant)]
    private static partial Regex EntryIdRegex();

    /// <summary>校验是否为合法且无注入风险的 npm 包名（不含版本号、路径、flag 或 shell 元字符）。</summary>
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

    /// <summary>校验是否为合法的 npm 包安装规格（包名 + 可选精确 SemVer 版本号）。</summary>
    public static bool IsValidInstallSpec(string? candidate) =>
        TryExtractPackageNameFromInstallSpec(candidate, out _);

    /// <summary>校验 npm 安装规格并提取其中的基础包名（剥离可选的精确 SemVer 版本后缀）。</summary>
    public static bool TryExtractPackageNameFromInstallSpec(string? candidate, out string packageName)
    {
        packageName = string.Empty;
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
        var extracted = match.Groups[1].Value;
        if (extracted.Length > MaxPackageNameLength || extracted.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }
        packageName = extracted;
        return true;
    }

    /// <summary>校验 Cordis 插件入口 ID 是否安全合法。</summary>
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

    /// <summary>从 Loader 的 <c>entryId</c>（可能带 <c>include:</c> 前缀）提取用于 <c>cordis.patch.yml</c> 的节点 ID。</summary>
    public static string ExtractPatchId(string entryId)
    {
        ArgumentException.ThrowIfNullOrEmpty(entryId);
        const string prefix = "include:";
        return entryId.StartsWith(prefix, StringComparison.Ordinal)
            ? entryId[prefix.Length..]
            : entryId;
    }

    /// <summary>判断插件入口是否为系统必需核心入口（不可关闭/卸载）。</summary>
    public static bool IsProtectedEntry(string entryId, string moduleName)
    {
        var patchId = string.IsNullOrEmpty(entryId) ? string.Empty : ExtractPatchId(entryId);
        if (ProtectedEntryIds.Contains(entryId) || ProtectedEntryIds.Contains(patchId))
        {
            return true;
        }
        var pkg = ExtractPackageNameFromModule(moduleName);
        return ProtectedPackageNames.Contains(pkg);
    }

    /// <summary>判断包名是否为系统核心包（不可卸载）。</summary>
    public static bool IsProtectedPackage(string packageName) =>
        ProtectedPackageNames.Contains(packageName);

    /// <summary>从模块名（如 <c>@scope/pkg/subpath</c>）中提取基础 npm 包名。</summary>
    public static string ExtractPackageNameFromModule(string? moduleName)
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

    /// <summary>构建受控 <c>dsh plugin --profile web list --json --depth 0</c> 命令。</summary>
    public static bool TryBuildListCommand(string profile, out string shellLine, out string? error)
    {
        if (!TryBuildArgv("list", profile, operand: null, out var argv, out error))
        {
            shellLine = string.Empty;
            return false;
        }
        shellLine = BuildShellLine(argv);
        return true;
    }

    /// <summary>构建受控 <c>dsh plugin --profile web add &lt;spec&gt;</c> 命令（校验失败直接拒绝）。</summary>
    public static bool TryBuildAddCommand(string profile, string? packageSpec, out string shellLine, out string? error)
    {
        if (!TryBuildArgv("add", profile, packageSpec, out var argv, out error))
        {
            shellLine = string.Empty;
            return false;
        }
        shellLine = BuildShellLine(argv);
        return true;
    }

    /// <summary>构建受控 <c>dsh plugin --profile web remove &lt;name&gt;</c> 命令（校验失败直接拒绝）。</summary>
    public static bool TryBuildRemoveCommand(string profile, string? packageName, out string shellLine, out string? error)
    {
        if (!TryBuildArgv("remove", profile, packageName, out var argv, out error))
        {
            shellLine = string.Empty;
            return false;
        }
        shellLine = BuildShellLine(argv);
        return true;
    }

    internal static bool TryBuildArgv(string verb, string profile, string? operand, out IReadOnlyList<string> argv, out string? error)
    {
        argv = [];
        error = null;
        if (!AllowedProfiles.Contains(profile))
        {
            error = $"profile must be one of [{string.Join(", ", AllowedProfiles)}]";
            return false;
        }

        switch (verb)
        {
            case "list":
                argv = ["plugin", "--profile", profile, "list", "--json", "--depth", "0"];
                return true;
            case "add" when IsValidInstallSpec(operand):
                argv = ["plugin", "--profile", profile, "add", operand!];
                return true;
            case "add":
                error = "package spec must be a valid npm package name with optional exact version";
                return false;
            case "remove" when IsValidPackageName(operand):
                if (IsProtectedPackage(operand!))
                {
                    error = "protected core package cannot be removed";
                    return false;
                }
                argv = ["plugin", "--profile", profile, "remove", operand!];
                return true;
            case "remove":
                error = "package name must be a valid npm package name";
                return false;
            default:
                error = $"unsupported plugin verb: {verb}";
                return false;
        }
    }

    /// <summary>使用 <see cref="GuestCommandLine"/> 对每个 argv token 单引号转义并拼接 guest shell 行。</summary>
    internal static string BuildShellLine(IReadOnlyList<string> argv) =>
        $"{GuestPathExport}; cd {RuntimeConstants.SandboxWorkspace} && {GuestCommandLine.Quote(GuestDshWrapper)} {GuestCommandLine.Join(argv)}";

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
        var match = regex.Match(input);
        return match.Success && match.Index == 0 && match.Length == input.Length;
    }
}
