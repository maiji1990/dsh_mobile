using System.Text.Json;
using DshMobile.Runtime.Guest;
using DshMobile.Runtime.Hosting;

namespace DshMobile.Runtime.Plugins;

/// <summary>通过 <c>dsh plugin --profile web list --json</c> 读取到的已装包摘要。</summary>
public sealed record GuestInstalledPlugin(
    string Name,
    string Version,
    bool HasCustomWebUi,
    bool IsProtected);

/// <summary>受控 <c>dsh plugin list</c> 结果。</summary>
public sealed record GuestPluginListResult(
    bool Ok,
    string Code,
    IReadOnlyList<GuestInstalledPlugin> Packages,
    string? Diagnostic = null);

/// <summary>受控插件安装/卸载/启停变更结果。</summary>
public sealed record GuestPluginChangeResult(
    bool Ok,
    string Code,
    string? Diagnostic = null);

/// <summary>
/// Runtime 侧受控插件管理执行器（PR-07, N-07-01）：
/// L0 复用 <c>dsh plugin --profile web list --json --depth 0</c> 补充版本与前端声明信息；
/// L1 仅执行经 <see cref="PluginCommandRules"/> 白名单校验并通过 <see cref="GuestCommandLine"/> 单引号转义的 CLI 命令，
/// 不在 Android 侧重写 npm/pnpm 安装逻辑。
/// </summary>
public sealed class GuestPluginService
{
    public static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan ChangeTimeout = TimeSpan.FromMinutes(10);

    private readonly RuntimeLayout _layout;
    private readonly Func<string, GuestExecutionOptions, CancellationToken, Task<GuestProcessResult>> _runner;

    public GuestPluginService(
        RuntimeLayout layout,
        Func<string, GuestExecutionOptions, CancellationToken, Task<GuestProcessResult>>? guestShellRunner = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _runner = guestShellRunner ?? ((cmd, opts, ct) => new ProotGuestProcess(_layout).RunShellAsync(cmd, opts, ct));
    }

    /// <summary>guest 中 <c>web</c> profile 目录在宿主文件系统上的根路径。</summary>
    public static string WebProfileHostDir(RuntimeLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return Path.Combine(layout.UserDataDir, ".dsh", "profiles", RuntimeConstants.DshWebProfile);
    }

    /// <summary>guest 中 <c>web</c> profile 的 <c>cordis.patch.yml</c> 宿主路径。</summary>
    public static string WebProfilePatchFile(RuntimeLayout layout) =>
        Path.Combine(WebProfileHostDir(layout), "cordis.patch.yml");

    /// <summary>执行受控 <c>dsh plugin --profile web list --json --depth 0</c>。</summary>
    public async Task<GuestPluginListResult> ListInstalledAsync(CancellationToken ct)
    {
        if (!PluginCommandRules.TryBuildListCommand(RuntimeConstants.DshWebProfile, out var shellLine, out var error))
        {
            return new GuestPluginListResult(false, RuntimeErrorCodes.BadRequest, [], error);
        }

        var res = await _runner(shellLine, new GuestExecutionOptions(Timeout: ListTimeout, MaxOutputLines: 2000), ct)
            .ConfigureAwait(false);
        var redactedOutput = RedactLines(res.Output);
        if (!res.Ok)
        {
            var diag = string.IsNullOrWhiteSpace(res.Error)
                ? redactedOutput
                : LogRedactor.Redact(res.Error) + (string.IsNullOrWhiteSpace(redactedOutput) ? string.Empty : "\n" + redactedOutput);
            return new GuestPluginListResult(false, RuntimeErrorCodes.Upstream, [], Truncate(diag));
        }

        if (!TryParsePnpmListJson(res.Output, out var parsed))
        {
            return new GuestPluginListResult(false, "PLUGIN_LIST_UNPARSABLE", [], Truncate(redactedOutput));
        }

        var profileDir = WebProfileHostDir(_layout);
        var packages = parsed
            .Select(p => new GuestInstalledPlugin(
                p.Name,
                p.Version,
                HasCustomWebUi: DetectCustomWebUiFromManifest(profileDir, p.Name),
                IsProtected: PluginCommandRules.IsProtectedPackage(p.Name)))
            .ToArray();
        return new GuestPluginListResult(true, RuntimeErrorCodes.Ok, packages);
    }

    /// <summary>执行受控安装：<c>dsh plugin --profile web add &lt;spec&gt;</c>（非法参数零进程启动）。</summary>
    public async Task<GuestPluginChangeResult> InstallAsync(string packageSpec, CancellationToken ct)
    {
        var trimmed = packageSpec?.Trim(' ');
        if (!PluginCommandRules.TryBuildAddCommand(RuntimeConstants.DshWebProfile, trimmed, out var shellLine, out var error))
        {
            return new GuestPluginChangeResult(false, RuntimeErrorCodes.BadRequest, error);
        }

        var res = await _runner(shellLine, new GuestExecutionOptions(Timeout: ChangeTimeout, MaxOutputLines: 2000), ct)
            .ConfigureAwait(false);
        return ToChangeResult(res);
    }

    /// <summary>执行受控卸载：<c>dsh plugin --profile web remove &lt;name&gt;</c>（非法参数零进程启动）。</summary>
    public async Task<GuestPluginChangeResult> RemoveAsync(string packageName, CancellationToken ct)
    {
        var trimmed = packageName?.Trim(' ');
        if (!PluginCommandRules.TryBuildRemoveCommand(RuntimeConstants.DshWebProfile, trimmed, out var shellLine, out var error))
        {
            return new GuestPluginChangeResult(false, RuntimeErrorCodes.BadRequest, error);
        }

        var res = await _runner(shellLine, new GuestExecutionOptions(Timeout: ChangeTimeout, MaxOutputLines: 2000), ct)
            .ConfigureAwait(false);
        return ToChangeResult(res);
    }

    /// <summary>执行受控启停（写入 <c>profiles/web/cordis.patch.yml</c>，并校验非受保护核心插件）。</summary>
    public Task<GuestPluginChangeResult> SetEnabledAsync(string entryId, string moduleName, bool enabled, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!PluginCommandRules.IsValidEntryId(entryId))
        {
            return Task.FromResult(new GuestPluginChangeResult(false, RuntimeErrorCodes.BadRequest, "invalid plugin entryId"));
        }
        if (PluginCommandRules.IsProtectedEntry(entryId, moduleName))
        {
            return Task.FromResult(new GuestPluginChangeResult(false, RuntimeErrorCodes.BadRequest, "protected core plugin cannot be toggled"));
        }

        var patchId = PluginCommandRules.ExtractPatchId(entryId);
        var (ok, code, diag) = PluginPatchEditor.SetPluginDisabled(WebProfilePatchFile(_layout), patchId, !enabled);
        return Task.FromResult(new GuestPluginChangeResult(ok, code, diag is null ? null : LogRedactor.Redact(diag)));
    }

    /// <summary>解析 <c>dsh plugin list --json --depth 0</c> 的 pnpm JSON 输出，容忍前导告警行。</summary>
    public static bool TryParsePnpmListJson(string output, out IReadOnlyList<(string Name, string Version)> packages)
    {
        ArgumentNullException.ThrowIfNull(output);
        packages = [];
        var start = output.IndexOf('[', StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(output[start..]);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var project in doc.RootElement.EnumerateArray())
            {
                if (project.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                foreach (var section in new[] { "dependencies", "devDependencies", "optionalDependencies" })
                {
                    if (!project.TryGetProperty(section, out var deps) || deps.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    foreach (var dep in deps.EnumerateObject())
                    {
                        var version = dep.Value.ValueKind == JsonValueKind.Object &&
                                      dep.Value.TryGetProperty("version", out var v) &&
                                      v.ValueKind == JsonValueKind.String
                            ? v.GetString() ?? string.Empty
                            : string.Empty;
                        seen[dep.Name] = version;
                    }
                }
            }
            packages = seen
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => (kv.Key, kv.Value))
                .ToArray();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 只读检查本地已安装包 manifest 是否声明 <c>dsh.client</c>（自定义 WebUI），严格限制在 profile 目录边界内且不跟随符号链接。
    /// </summary>
    internal static bool DetectCustomWebUiFromManifest(string profileHostDir, string packageName)
    {
        if (string.IsNullOrWhiteSpace(profileHostDir) || !PluginCommandRules.IsValidPackageName(packageName))
        {
            return false;
        }
        if (packageName.StartsWith("@deepseek-ai/dsh-client-", StringComparison.Ordinal) ||
            packageName.Contains("-client-ui-", StringComparison.Ordinal) ||
            packageName.EndsWith("-webui", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            var rootFull = Path.GetFullPath(profileHostDir);
            var pkgSegments = packageName.Split('/');
            var manifestPath = Path.GetFullPath(Path.Combine([rootFull, "node_modules", .. pkgSegments, "package.json"]));
            if (!manifestPath.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return false;
            }
            if (!File.Exists(manifestPath))
            {
                return false;
            }
            var info = new FileInfo(manifestPath);
            if (info.LinkTarget is not null)
            {
                return false;
            }
            var dirInfo = info.Directory;
            if (dirInfo is not null && dirInfo.LinkTarget is not null)
            {
                return false;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (doc.RootElement.TryGetProperty("dsh", out var dsh) && dsh.ValueKind == JsonValueKind.Object)
            {
                if (dsh.TryGetProperty("client", out var client) && client.ValueKind == JsonValueKind.Object)
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
        return false;
    }

    private static GuestPluginChangeResult ToChangeResult(GuestProcessResult res)
    {
        var redactedOut = RedactLines(res.Output);
        if (res.Ok)
        {
            return new GuestPluginChangeResult(true, RuntimeErrorCodes.Ok);
        }
        var err = string.IsNullOrWhiteSpace(res.Error) ? string.Empty : LogRedactor.Redact(res.Error);
        var combined = string.IsNullOrWhiteSpace(redactedOut) ? err : $"{err}\n{redactedOut}".Trim();
        return new GuestPluginChangeResult(false, RuntimeErrorCodes.Upstream, Truncate(combined));
    }

    private static string RedactLines(string raw) =>
        string.IsNullOrEmpty(raw)
            ? string.Empty
            : string.Join('\n', raw.Split('\n').Select(LogRedactor.Redact));

    private static string Truncate(string raw, int maxChars = 400) =>
        raw.Length <= maxChars ? raw : raw[..maxChars] + "…";
}
