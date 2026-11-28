using System.Text;
using System.Text.Json;
using DshMobile.Runtime.Guest;
using DshMobile.Runtime.Hosting;

namespace DshMobile.Runtime.Plugins;

/// <summary>通过 <c>dsh plugin --profile web list --json</c> 与本地 manifest 读取到的已装包摘要。</summary>
public sealed record GuestInstalledPlugin(
    string Name,
    string Version,
    bool HasCustomWebUi,
    bool IsProtected,
    bool HasBundleManifest = false,
    string? Description = null,
    string? Author = null,
    string? License = null,
    string? HomepageUrl = null,
    string? RepositoryUrl = null);

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
    string? Diagnostic = null,
    bool DeclaredBundleManifest = true,
    string? WarningSummary = null);

/// <summary>本地已安装包（profile 或只读 DSH 层）的安全只读元信息与详情文档。</summary>
public sealed record GuestLocalPackageDetail(
    bool Ok,
    string Code,
    string PackageName,
    string? Version = null,
    string? Description = null,
    string? Author = null,
    string? License = null,
    string? HomepageUrl = null,
    string? RepositoryUrl = null,
    bool HasCustomWebUi = false,
    bool HasBundleManifest = false,
    string? Markdown = null,
    string DocumentOrigin = GuestLocalPackageDetail.OriginNone,
    string? DocumentRelativePath = null,
    string? PackageRootSource = null,
    string? Diagnostic = null)
{
    /// <summary>未找到可读的详情文档。</summary>
    public const string OriginNone = "none";

    /// <summary>详情文档来自 <c>locale/&lt;lang&gt;.json</c> 的 <c>meta.detail</c> 指向文件。</summary>
    public const string OriginLocaleDetail = "locale-detail";

    /// <summary>详情文档回退自包根目录下的 README 文件。</summary>
    public const string OriginReadme = "readme";
}

/// <summary>本地已安装包 <c>dshmobile.sdui.json</c> 清单的受控只读读取结果（Slice 1A）。</summary>
public sealed record GuestLocalSduiManifestResult(
    bool Ok,
    string Code,
    string? JsonContent = null,
    string? Diagnostic = null);

/// <summary>
/// Runtime 侧受控插件管理执行器（PR-07, N-07-01；ADR-0034 增强）：
/// L0 复用 <c>dsh plugin --profile web list --json --depth 0</c> 与边界内安全符号链接解析补充完整包元信息与本地详情文档；
/// L1 仅执行经 <see cref="PluginCommandRules"/> 白名单校验并通过 <see cref="GuestCommandLine"/> 单引号转义的 CLI 命令，
/// 不在 Android 侧重写 npm/pnpm 安装逻辑。
/// </summary>
public sealed class GuestPluginService
{
    public static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan ChangeTimeout = TimeSpan.FromMinutes(10);

    /// <summary>本地详情文档单次最大读取字节数（64 KiB），防止超大文件消耗内存。</summary>
    public const int MaxLocalDetailBytes = 64 * 1024;

    private const int MaxManifestBytes = 512 * 1024;
    private const int MaxLocaleFileBytes = 256 * 1024;
    private const int MaxSymlinkResolutionDepth = 16;

    /// <summary>未声明 <c>dsh.bundle</c> 的普通 npm 包安装完成后的结构化警告文案。</summary>
    public const string NoBundleWarningSummary = "已安装为普通依赖，因未声明 dsh.bundle 不会自动挂载到 Cordis";

    /// <summary>本地详情查询未定位到目标包时的稳定错误码。</summary>
    public const string PackageNotFoundCode = "PLUGIN_NOT_FOUND";

    /// <summary>本地 <c>dshmobile.sdui.json</c> 清单文件名。</summary>
    public const string SduiManifestFileName = "dshmobile.sdui.json";

    /// <summary>本地 SDUI 清单单次最大读取字节数（256 KiB）。</summary>
    public const int MaxLocalSduiManifestBytes = 256 * 1024;

    /// <summary>本地 SDUI 清单超过大小上限时的稳定错误码。</summary>
    public const string SduiManifestTooLargeCode = "PLUGIN_SDUI_MANIFEST_TOO_LARGE";

    /// <summary>目标包随 DSH 层内置发布、不得在 web profile 中覆盖升级时的稳定错误码。</summary>
    public const string ShippedPackageCode = "PLUGIN_SHIPPED_PACKAGE";

    private static readonly string[] ReadmeCandidateNames =
    [
        "README.md",
        "readme.md",
        "Readme.md",
        "README.markdown",
        "README",
    ];

    private static readonly UTF8Encoding LenientUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

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

    /// <summary>guest 中 <c>web</c> profile 的 <c>node_modules</c> 宿主路径。</summary>
    public static string WebProfileNodeModulesHostDir(RuntimeLayout layout) =>
        Path.Combine(WebProfileHostDir(layout), "node_modules");

    /// <summary>只读 DSH 运行层（guest <c>/opt/dshapp/runtime/node_modules</c>）在宿主文件系统上的路径。</summary>
    public static string DshRuntimeNodeModulesHostDir(RuntimeLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return Path.Combine(layout.LayerDir("dsh"), "node_modules");
    }

    /// <summary>guest 中 <c>web</c> profile 的 <c>cordis.patch.yml</c> 宿主路径。</summary>
    public static string WebProfilePatchFile(RuntimeLayout layout) =>
        Path.Combine(WebProfileHostDir(layout), "cordis.patch.yml");

    /// <summary>执行受控 <c>dsh plugin --profile web list --json --depth 0</c> 并从本地 manifest 读取完整元信息。</summary>
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

        var profileNodeModulesDir = WebProfileNodeModulesHostDir(_layout);
        var packages = parsed
            .Select(p =>
            {
                var metadata = ReadPackageManifestMetadata(profileNodeModulesDir, p.Name);
                var hasCustomWebUi = metadata.HasCustomWebUi || HasCustomWebUiNameHeuristic(p.Name);
                return new GuestInstalledPlugin(
                    Name: p.Name,
                    Version: string.IsNullOrWhiteSpace(p.Version) ? (metadata.Version ?? string.Empty) : p.Version,
                    HasCustomWebUi: hasCustomWebUi,
                    IsProtected: PluginCommandRules.IsProtectedPackage(p.Name),
                    HasBundleManifest: metadata.HasBundleManifest,
                    Description: metadata.Description,
                    Author: metadata.Author,
                    License: metadata.License,
                    HomepageUrl: metadata.HomepageUrl,
                    RepositoryUrl: metadata.RepositoryUrl);
            })
            .ToArray();
        return new GuestPluginListResult(true, RuntimeErrorCodes.Ok, packages);
    }

    /// <summary>执行受控安装：<c>dsh plugin --profile web add &lt;spec&gt;</c>（非法参数零进程启动），并检测是否缺少 <c>dsh.bundle</c> 声明。</summary>
    public async Task<GuestPluginChangeResult> InstallAsync(string packageSpec, CancellationToken ct)
    {
        var trimmed = packageSpec?.Trim(' ');
        if (!PluginCommandRules.TryBuildAddCommand(RuntimeConstants.DshWebProfile, trimmed, out var shellLine, out var error))
        {
            return new GuestPluginChangeResult(false, RuntimeErrorCodes.BadRequest, error);
        }

        var res = await _runner(shellLine, new GuestExecutionOptions(Timeout: ChangeTimeout, MaxOutputLines: 2000), ct)
            .ConfigureAwait(false);
        if (!res.Ok)
        {
            return ToChangeResult(res);
        }

        if (!PluginCommandRules.TryExtractPackageNameFromInstallSpec(trimmed, out var installedPackageName))
        {
            return new GuestPluginChangeResult(true, RuntimeErrorCodes.Ok);
        }
        return CompleteBundleChange(res, installedPackageName);
    }

    /// <summary>
    /// 执行受控升级：<c>dsh plugin --profile web add &lt;包名&gt;@&lt;精确版本&gt;</c>。
    /// 参数非法、属于核心保护包或随 DSH 层内置发布时零进程直接失败；否则复用安装完成后的
    /// <c>dsh.bundle</c> 检测与结果映射，升级成功后由上层标记需要重启 DSH 生效。
    /// </summary>
    public async Task<GuestPluginChangeResult> UpgradeAsync(string packageName, string exactVersion, CancellationToken ct)
    {
        var trimmedPackage = packageName?.Trim(' ');
        var trimmedVersion = exactVersion?.Trim(' ');
        if (!PluginCommandRules.TryBuildUpgradeCommand(
                RuntimeConstants.DshWebProfile, trimmedPackage, trimmedVersion, out var shellLine, out var error))
        {
            return new GuestPluginChangeResult(false, RuntimeErrorCodes.BadRequest, error);
        }
        if (PluginCommandRules.IsInstallationShippedPackage(_layout.LayerDir("dsh"), trimmedPackage!))
        {
            return new GuestPluginChangeResult(
                false,
                ShippedPackageCode,
                "package ships with the DSH layer and cannot be upgraded in the web profile");
        }

        var res = await _runner(shellLine, new GuestExecutionOptions(Timeout: ChangeTimeout, MaxOutputLines: 2000), ct)
            .ConfigureAwait(false);
        if (!res.Ok)
        {
            return ToChangeResult(res);
        }
        return CompleteBundleChange(res, trimmedPackage!);
    }

    /// <summary>安装/升级成功后的 <c>dsh.bundle</c> 声明检测，返回一致的结构化警告结果。</summary>
    private GuestPluginChangeResult CompleteBundleChange(GuestProcessResult res, string packageName)
    {
        var outputWarnsNoBundle = res.Output.Contains("declares no dsh.bundle", StringComparison.OrdinalIgnoreCase);
        var metadata = ReadPackageManifestMetadata(WebProfileNodeModulesHostDir(_layout), packageName);
        var manifestFoundWithoutBundle = metadata.Found && !metadata.HasBundleManifest;
        if (outputWarnsNoBundle || manifestFoundWithoutBundle)
        {
            return new GuestPluginChangeResult(
                Ok: true,
                Code: RuntimeErrorCodes.Ok,
                Diagnostic: null,
                DeclaredBundleManifest: false,
                WarningSummary: NoBundleWarningSummary);
        }
        return new GuestPluginChangeResult(true, RuntimeErrorCodes.Ok);
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

    /// <summary>
    /// 按上游 <c>@deepseek-ai/dsh-plugin-detail</c> 规范在安全路径边界内只读本地已安装包的元信息与详情文档。
    /// 优先查 <c>profiles/web/node_modules/&lt;pkg&gt;</c>，未命中时回退只读层 <c>layers/dsh/node_modules/&lt;pkg&gt;</c>；
    /// 详情文档优先查 <c>locale/&lt;lang&gt;.json</c> 的 <c>meta.detail</c>，再回退包根目录 README。
    /// </summary>
    public Task<GuestLocalPackageDetail> ReadLocalPackageDetailAsync(
        string packageName,
        string language,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var trimmedName = packageName?.Trim();
        if (!PluginCommandRules.IsValidPackageName(trimmedName))
        {
            return Task.FromResult(new GuestLocalPackageDetail(
                Ok: false,
                Code: RuntimeErrorCodes.BadRequest,
                PackageName: trimmedName ?? string.Empty,
                Diagnostic: "invalid package name"));
        }

        return Task.Run(() => ReadLocalPackageDetailCore(trimmedName!, language, ct), ct);
    }

    /// <summary>
    /// Slice 1A：在安全路径边界内只读本地已安装包的 <c>dshmobile.sdui.json</c> 扩展清单，不启动 guest 进程。
    /// 先查 <c>profiles/web/node_modules</c>，未命中时回退只读层 <c>layers/dsh/node_modules</c>；
    /// 清单缺失、超过 256 KiB 或无法读取时均 fail-closed，绝不返回边界外的文件内容。
    /// </summary>
    public GuestLocalSduiManifestResult ReadLocalSduiManifest(string packageName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var trimmedName = packageName?.Trim();
        if (!PluginCommandRules.IsValidPackageName(trimmedName))
        {
            return new GuestLocalSduiManifestResult(false, RuntimeErrorCodes.BadRequest, Diagnostic: "invalid package name");
        }

        var packageSegments = trimmedName!.Split('/');
        var relativeSegments = new string[packageSegments.Length + 1];
        packageSegments.CopyTo(relativeSegments, 0);
        relativeSegments[^1] = SduiManifestFileName;

        foreach (var nodeModulesDir in new[]
                 {
                     WebProfileNodeModulesHostDir(_layout),
                     DshRuntimeNodeModulesHostDir(_layout),
                 })
        {
            ct.ThrowIfCancellationRequested();
            var manifestPath = TryResolvePathWithinBoundary(nodeModulesDir, relativeSegments, expectDirectory: false);
            if (manifestPath is null)
            {
                continue;
            }

            long manifestLength;
            try
            {
                manifestLength = new FileInfo(manifestPath).Length;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new GuestLocalSduiManifestResult(
                    false,
                    RuntimeErrorCodes.Upstream,
                    Diagnostic: "dshmobile.sdui.json metadata could not be read");
            }

            // 超限清单直接拒绝，避免把被截断的 JSON 交给上层解析。
            if (manifestLength > MaxLocalSduiManifestBytes)
            {
                return new GuestLocalSduiManifestResult(
                    false,
                    SduiManifestTooLargeCode,
                    Diagnostic: "dshmobile.sdui.json exceeds 256 KiB");
            }

            if (!TryReadBoundedUtf8Text(manifestPath, MaxLocalSduiManifestBytes, out var jsonContent))
            {
                return new GuestLocalSduiManifestResult(
                    false,
                    RuntimeErrorCodes.Upstream,
                    Diagnostic: "dshmobile.sdui.json could not be read");
            }

            return new GuestLocalSduiManifestResult(true, RuntimeErrorCodes.Ok, jsonContent);
        }

        return new GuestLocalSduiManifestResult(
            false,
            PackageNotFoundCode,
            Diagnostic: "dshmobile.sdui.json not found in local profile or runtime layer");
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
    /// 只读检查本地已安装包 manifest 是否声明 <c>dsh.client</c>（自定义 WebUI）。
    /// 安全放行解析后仍严格落在 <c>profiles/web/node_modules</c> 边界内的 pnpm 符号链接，逃逸出边界的符号链接一律拒绝。
    /// </summary>
    internal static bool DetectCustomWebUiFromManifest(string profileHostDir, string packageName)
    {
        if (string.IsNullOrWhiteSpace(profileHostDir) || !PluginCommandRules.IsValidPackageName(packageName))
        {
            return false;
        }
        if (HasCustomWebUiNameHeuristic(packageName))
        {
            return true;
        }

        var nodeModulesDir = Path.Combine(profileHostDir, "node_modules");
        var metadata = ReadPackageManifestMetadata(nodeModulesDir, packageName);
        return metadata.HasCustomWebUi;
    }

    private GuestLocalPackageDetail ReadLocalPackageDetailCore(string packageName, string? language, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var packageSegments = packageName.Split('/');

        // 优先查找 web profile 的 node_modules，未命中时再查只读 DSH 运行层 node_modules。
        string? boundaryNodeModulesDir = null;
        string? packageRootDir = null;
        string? rootSource = null;

        var profileNodeModules = WebProfileNodeModulesHostDir(_layout);
        if (TryResolvePathWithinBoundary(profileNodeModules, packageSegments, expectDirectory: true) is { } resolvedProfilePkg)
        {
            boundaryNodeModulesDir = profileNodeModules;
            packageRootDir = resolvedProfilePkg;
            rootSource = "web-profile";
        }
        else
        {
            var runtimeNodeModules = DshRuntimeNodeModulesHostDir(_layout);
            if (TryResolvePathWithinBoundary(runtimeNodeModules, packageSegments, expectDirectory: true) is { } resolvedRuntimePkg)
            {
                boundaryNodeModulesDir = runtimeNodeModules;
                packageRootDir = resolvedRuntimePkg;
                rootSource = "dsh-runtime";
            }
        }

        if (packageRootDir is null || boundaryNodeModulesDir is null)
        {
            return new GuestLocalPackageDetail(
                Ok: false,
                Code: PackageNotFoundCode,
                PackageName: packageName,
                Diagnostic: "package not found in local profile or runtime layer");
        }

        var metadata = ReadPackageManifestMetadataFromResolvedDir(boundaryNodeModulesDir, packageRootDir);
        var hasCustomWebUi = metadata.HasCustomWebUi || HasCustomWebUiNameHeuristic(packageName);

        // 先按语言回退链读取 locale/<lang>.json 中的 meta.detail，再回退到包根目录 README。
        string? markdown = null;
        var origin = GuestLocalPackageDetail.OriginNone;
        string? relativeDocPath = null;

        var languageChain = BuildLanguageFallbackChain(language);
        foreach (var langCandidate in languageChain)
        {
            ct.ThrowIfCancellationRequested();
            if (TryReadLocaleDetailDocument(packageRootDir, langCandidate, out var localeMarkdown, out var detailRelPath))
            {
                markdown = localeMarkdown;
                origin = GuestLocalPackageDetail.OriginLocaleDetail;
                relativeDocPath = detailRelPath;
                break;
            }
        }

        if (markdown is null)
        {
            foreach (var readmeName in ReadmeCandidateNames)
            {
                ct.ThrowIfCancellationRequested();
                var readmeFile = TryResolvePathWithinBoundary(packageRootDir, [readmeName], expectDirectory: false);
                if (readmeFile is null)
                {
                    continue;
                }
                if (TryReadBoundedUtf8Text(readmeFile, MaxLocalDetailBytes, out var rawReadme))
                {
                    var cleaned = CleanMarkdownDocument(rawReadme);
                    if (!string.IsNullOrWhiteSpace(cleaned))
                    {
                        markdown = cleaned;
                        origin = GuestLocalPackageDetail.OriginReadme;
                        relativeDocPath = readmeName;
                        break;
                    }
                }
            }
        }

        return new GuestLocalPackageDetail(
            Ok: true,
            Code: RuntimeErrorCodes.Ok,
            PackageName: packageName,
            Version: metadata.Version,
            Description: metadata.Description,
            Author: metadata.Author,
            License: metadata.License,
            HomepageUrl: metadata.HomepageUrl,
            RepositoryUrl: metadata.RepositoryUrl,
            HasCustomWebUi: hasCustomWebUi,
            HasBundleManifest: metadata.HasBundleManifest,
            Markdown: markdown,
            DocumentOrigin: origin,
            DocumentRelativePath: relativeDocPath,
            PackageRootSource: rootSource);
    }

    private static bool TryReadLocaleDetailDocument(
        string packageRootDir,
        string langTag,
        out string? markdown,
        out string? relativePath)
    {
        markdown = null;
        relativePath = null;

        var localeFile = TryResolvePathWithinBoundary(packageRootDir, ["locale", $"{langTag}.json"], expectDirectory: false);
        if (localeFile is null || !TryReadBoundedUtf8Text(localeFile, MaxLocaleFileBytes, out var localeJson))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(localeJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("meta", out var metaElement) ||
                metaElement.ValueKind != JsonValueKind.Object ||
                !metaElement.TryGetProperty("detail", out var detailElement) ||
                detailElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var detailPathRaw = detailElement.GetString();
            if (!TryParseSafeRelativePathSegments(detailPathRaw, out var detailSegments, out var normalizedRelativePath))
            {
                return false;
            }

            var detailFile = TryResolvePathWithinBoundary(packageRootDir, detailSegments, expectDirectory: false);
            if (detailFile is null || !TryReadBoundedUtf8Text(detailFile, MaxLocalDetailBytes, out var rawDetail))
            {
                return false;
            }

            var cleaned = CleanMarkdownDocument(rawDetail);
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                return false;
            }

            markdown = cleaned;
            relativePath = normalizedRelativePath;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 构造语言回退链（如 <c>zh-CN</c> → <c>zh-cn</c>、<c>zh-CN</c>、<c>zh</c>、<c>en</c>）。
    /// 兼容大小写敏感文件系统（Linux/Android）上的 <c>zh-cn.json</c> 与 <c>zh-CN.json</c>。
    /// </summary>
    internal static IReadOnlyList<string> BuildLanguageFallbackChain(string? language)
    {
        var result = new List<string>();
        void AddUnique(string candidate)
        {
            if (candidate.Length > 0 && !result.Contains(candidate, StringComparer.Ordinal))
            {
                result.Add(candidate);
            }
        }

        if (!string.IsNullOrWhiteSpace(language))
        {
            var normalizedOriginal = language.Trim().Replace('_', '-');
            if (IsValidLanguageTag(normalizedOriginal))
            {
                var lower = normalizedOriginal.ToLowerInvariant();
                AddUnique(lower);
                if (!string.Equals(normalizedOriginal, lower, StringComparison.Ordinal))
                {
                    AddUnique(normalizedOriginal);
                }

                // 逐级剥离后缀子标签（例如 zh-hans-cn → zh-hans → zh）。
                var current = lower;
                while (current.LastIndexOf('-') is var dash and > 0)
                {
                    current = current[..dash];
                    AddUnique(current);
                }

                if (string.Equals(lower, "zh", StringComparison.Ordinal))
                {
                    AddUnique("zh-cn");
                }
            }
        }

        AddUnique("en");
        return result;
    }

    private static bool IsValidLanguageTag(string tag)
    {
        if (tag.Length is 0 or > 32)
        {
            return false;
        }
        foreach (var ch in tag)
        {
            if (!((ch is >= 'a' and <= 'z') || (ch is >= 'A' and <= 'Z') || (ch is >= '0' and <= '9') || ch == '-'))
            {
                return false;
            }
        }
        return tag[0] != '-' && tag[^1] != '-';
    }

    /// <summary>
    /// 校验包内相对路径（用于 <c>locale/&lt;lang&gt;.json</c> 的 <c>meta.detail</c>）：
    /// 拒绝空串、控制字符、反斜杠、协议头/盘符（含 <c>:</c>）、绝对路径与任意 <c>..</c> 段。
    /// </summary>
    internal static bool TryParseSafeRelativePathSegments(
        string? rawPath,
        out string[] segments,
        out string normalizedRelativePath)
    {
        segments = [];
        normalizedRelativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return false;
        }

        var trimmed = rawPath.Trim();
        if (trimmed.StartsWith('/') || trimmed.StartsWith('\\') || Path.IsPathRooted(trimmed))
        {
            return false;
        }
        if (trimmed.Contains(':', StringComparison.Ordinal) ||
            trimmed.Contains('\\', StringComparison.Ordinal) ||
            trimmed.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }
        foreach (var ch in trimmed)
        {
            if (char.IsControl(ch))
            {
                return false;
            }
        }

        var parts = trimmed.Split('/');
        var cleaned = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part.Length == 0)
            {
                return false;
            }
            if (part == ".")
            {
                continue;
            }
            if (part == "..")
            {
                return false;
            }
            cleaned.Add(part);
        }

        if (cleaned.Count == 0)
        {
            return false;
        }

        segments = [.. cleaned];
        normalizedRelativePath = string.Join('/', segments);
        return true;
    }

    /// <summary>剥离 UTF-8 BOM 与文档开头的 YAML front matter（<c>---\n...\n---</c>）。</summary>
    internal static string CleanMarkdownDocument(string rawText)
    {
        if (string.IsNullOrEmpty(rawText))
        {
            return string.Empty;
        }

        var text = rawText.TrimStart('\uFEFF');

        // 仅当文档最开头以独立 "---" 行起始，且后续存在闭合 "---" 或 "..." 行时才剥离 front matter。
        int firstLineEnd;
        if (text.StartsWith("---\r\n", StringComparison.Ordinal))
        {
            firstLineEnd = 5;
        }
        else if (text.StartsWith("---\n", StringComparison.Ordinal))
        {
            firstLineEnd = 4;
        }
        else
        {
            return text;
        }

        var scanPos = firstLineEnd;
        while (scanPos <= text.Length)
        {
            var nextLf = text.IndexOf('\n', scanPos);
            var lineEnd = nextLf < 0 ? text.Length : nextLf;
            var lineSpan = text.AsSpan(scanPos, lineEnd - scanPos);
            if (lineSpan.EndsWith('\r'))
            {
                lineSpan = lineSpan[..^1];
            }

            if (lineSpan.SequenceEqual("---") || lineSpan.SequenceEqual("..."))
            {
                var afterClose = nextLf < 0 ? text.Length : nextLf + 1;
                return text[afterClose..].TrimStart('\r', '\n');
            }

            if (nextLf < 0)
            {
                break;
            }
            scanPos = nextLf + 1;
        }

        return text;
    }

    private sealed record PackageManifestMetadata(
        bool Found,
        string? Version = null,
        bool HasCustomWebUi = false,
        bool HasBundleManifest = false,
        string? Description = null,
        string? Author = null,
        string? License = null,
        string? HomepageUrl = null,
        string? RepositoryUrl = null)
    {
        public static readonly PackageManifestMetadata Missing = new(false);
    }

    private static PackageManifestMetadata ReadPackageManifestMetadata(string nodeModulesDir, string packageName)
    {
        if (string.IsNullOrWhiteSpace(nodeModulesDir) || !PluginCommandRules.IsValidPackageName(packageName))
        {
            return PackageManifestMetadata.Missing;
        }

        var packageSegments = packageName.Split('/');
        var packageRootDir = TryResolvePathWithinBoundary(nodeModulesDir, packageSegments, expectDirectory: true);
        if (packageRootDir is null)
        {
            return PackageManifestMetadata.Missing;
        }

        return ReadPackageManifestMetadataFromResolvedDir(nodeModulesDir, packageRootDir);
    }

    private static PackageManifestMetadata ReadPackageManifestMetadataFromResolvedDir(
        string boundaryNodeModulesDir,
        string resolvedPackageRootDir)
    {
        try
        {
            var boundaryFull = TrimTrailingSeparators(Path.GetFullPath(boundaryNodeModulesDir));
            var relativePackageDir = Path.GetRelativePath(boundaryFull, resolvedPackageRootDir);
            var manifestSegments = relativePackageDir == "."
                ? ["package.json"]
                : relativePackageDir
                    .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
                    .Append("package.json")
                    .ToArray();

            var manifestPath = TryResolvePathWithinBoundary(boundaryFull, manifestSegments, expectDirectory: false);
            if (manifestPath is null || !TryReadBoundedUtf8Text(manifestPath, MaxManifestBytes, out var manifestText))
            {
                return PackageManifestMetadata.Missing;
            }

            using var doc = JsonDocument.Parse(manifestText);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return PackageManifestMetadata.Missing;
            }

            var version = ReadOptionalTrimmedString(root, "version");
            var description = ReadOptionalTrimmedString(root, "description");
            var author = ReadAuthor(root);
            var license = ReadLicense(root);
            var homepageUrl = TryNormalizeHttpsUrl(ReadOptionalTrimmedString(root, "homepage"));
            var repositoryUrl = TryNormalizeHttpsUrl(ReadRepositoryUrlRaw(root));

            var hasCustomWebUi = false;
            var hasBundleManifest = false;
            if (root.TryGetProperty("dsh", out var dshElement) && dshElement.ValueKind == JsonValueKind.Object)
            {
                if (dshElement.TryGetProperty("client", out var clientElement))
                {
                    hasCustomWebUi = clientElement.ValueKind switch
                    {
                        JsonValueKind.Object => true,
                        JsonValueKind.True => true,
                        JsonValueKind.String => !string.IsNullOrWhiteSpace(clientElement.GetString()),
                        JsonValueKind.Array => clientElement.GetArrayLength() > 0,
                        _ => false,
                    };
                }

                if (dshElement.TryGetProperty("bundle", out var bundleElement) &&
                    bundleElement.ValueKind == JsonValueKind.Object &&
                    bundleElement.TryGetProperty("patch", out var patchElement) &&
                    patchElement.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(patchElement.GetString()))
                {
                    hasBundleManifest = true;
                }
            }

            return new PackageManifestMetadata(
                Found: true,
                Version: version,
                HasCustomWebUi: hasCustomWebUi,
                HasBundleManifest: hasBundleManifest,
                Description: description,
                Author: author,
                License: license,
                HomepageUrl: homepageUrl,
                RepositoryUrl: repositoryUrl);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return PackageManifestMetadata.Missing;
        }
    }

    private static bool HasCustomWebUiNameHeuristic(string packageName) =>
        packageName.StartsWith("@deepseek-ai/dsh-client-", StringComparison.Ordinal) ||
        packageName.Contains("-client-ui-", StringComparison.Ordinal) ||
        packageName.EndsWith("-webui", StringComparison.Ordinal);

    private static string? ReadOptionalTrimmedString(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var value = prop.GetString()?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? ReadAuthor(JsonElement root)
    {
        if (!root.TryGetProperty("author", out var authorElement))
        {
            return null;
        }
        return authorElement.ValueKind switch
        {
            JsonValueKind.String => NonEmptyTrimmed(authorElement.GetString()),
            JsonValueKind.Object => ReadOptionalTrimmedString(authorElement, "name"),
            _ => null,
        };
    }

    private static string? ReadLicense(JsonElement root)
    {
        if (!root.TryGetProperty("license", out var licenseElement))
        {
            return null;
        }
        return licenseElement.ValueKind switch
        {
            JsonValueKind.String => NonEmptyTrimmed(licenseElement.GetString()),
            JsonValueKind.Object => ReadOptionalTrimmedString(licenseElement, "type"),
            _ => null,
        };
    }

    private static string? ReadRepositoryUrlRaw(JsonElement root)
    {
        if (!root.TryGetProperty("repository", out var repoElement))
        {
            return null;
        }
        return repoElement.ValueKind switch
        {
            JsonValueKind.String => NonEmptyTrimmed(repoElement.GetString()),
            JsonValueKind.Object => ReadOptionalTrimmedString(repoElement, "url"),
            _ => null,
        };
    }

    /// <summary>仅放行合法且无凭据段的 <c>https://</c> URL（兼容 npm repository 常见的 <c>git+https://</c> 前缀），拒绝其它协议以防注入。</summary>
    internal static string? TryNormalizeHttpsUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return null;
        }
        var candidate = rawUrl.Trim();
        if (candidate.StartsWith("git+https://", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate["git+".Length..];
        }
        foreach (var ch in candidate)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                return null;
            }
        }
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return null;
        }
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }
        return uri.AbsoluteUri;
    }

    private static string? NonEmptyTrimmed(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// 在 <paramref name="boundaryDir"/> 目录边界内安全解析目标路径：
    /// 逐段检查目录与文件，允许指向同一边界目录内的符号链接（如 pnpm <c>.pnpm</c> 虚拟存储布局），
    /// 并对解析后的中间路径再次验证以防止多级符号链接逃逸；任何逃逸出边界或循环链接均返回 <c>null</c>。
    /// </summary>
    internal static string? TryResolvePathWithinBoundary(
        string boundaryDir,
        IReadOnlyList<string> relativeSegments,
        bool expectDirectory)
    {
        if (string.IsNullOrWhiteSpace(boundaryDir) || relativeSegments.Count == 0)
        {
            return null;
        }

        try
        {
            var boundaryFull = TrimTrailingSeparators(Path.GetFullPath(boundaryDir));
            var boundaryInfo = new DirectoryInfo(boundaryFull);
            if (!boundaryInfo.Exists || boundaryInfo.LinkTarget is not null)
            {
                return null;
            }

            var pendingSegments = new Queue<string>(relativeSegments);
            var currentPath = boundaryFull;
            var symlinkHops = 0;

            while (pendingSegments.Count > 0)
            {
                var segment = pendingSegments.Dequeue();
                if (string.IsNullOrEmpty(segment) ||
                    segment is "." or ".." ||
                    segment.Contains('/', StringComparison.Ordinal) ||
                    segment.Contains('\\', StringComparison.Ordinal) ||
                    segment.Contains('\0', StringComparison.Ordinal))
                {
                    return null;
                }

                var isLastSegment = pendingSegments.Count == 0;
                var nextPath = TrimTrailingSeparators(Path.GetFullPath(Path.Combine(currentPath, segment)));
                if (!IsPathWithinBoundary(nextPath, boundaryFull))
                {
                    return null;
                }

                var treatAsDirectory = !isLastSegment || expectDirectory;
                FileSystemInfo entryInfo = treatAsDirectory
                    ? new DirectoryInfo(nextPath)
                    : new FileInfo(nextPath);
                if (!entryInfo.Exists)
                {
                    return null;
                }

                if (entryInfo.LinkTarget is not null)
                {
                    symlinkHops++;
                    if (symlinkHops > MaxSymlinkResolutionDepth)
                    {
                        return null;
                    }

                    var resolvedTarget = entryInfo.ResolveLinkTarget(returnFinalTarget: true);
                    if (resolvedTarget is null || !resolvedTarget.Exists)
                    {
                        return null;
                    }

                    var resolvedFull = TrimTrailingSeparators(Path.GetFullPath(resolvedTarget.FullName));
                    if (!IsPathWithinBoundary(resolvedFull, boundaryFull))
                    {
                        return null;
                    }

                    // 将目标路径相对边界拆段并重走验证，确保目标路径中的中间目录同样不存在越界符号链接。
                    var relativeTarget = Path.GetRelativePath(boundaryFull, resolvedFull);
                    if (relativeTarget == ".")
                    {
                        currentPath = boundaryFull;
                        continue;
                    }

                    var targetSegments = relativeTarget.Split(
                        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                        StringSplitOptions.RemoveEmptyEntries);
                    if (targetSegments.Length == 0 || targetSegments.Any(s => s is "." or ".."))
                    {
                        return null;
                    }

                    var remainingAfterLink = pendingSegments.ToArray();
                    pendingSegments.Clear();
                    foreach (var targetSegment in targetSegments)
                    {
                        pendingSegments.Enqueue(targetSegment);
                    }
                    foreach (var remainingSegment in remainingAfterLink)
                    {
                        pendingSegments.Enqueue(remainingSegment);
                    }

                    currentPath = boundaryFull;
                    continue;
                }

                currentPath = nextPath;
            }

            return IsPathWithinBoundary(currentPath, boundaryFull) ? currentPath : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsPathWithinBoundary(string candidateFullPath, string boundaryFullDir)
    {
        var normalizedCandidate = TrimTrailingSeparators(Path.GetFullPath(candidateFullPath));
        var normalizedBoundary = TrimTrailingSeparators(Path.GetFullPath(boundaryFullDir));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(normalizedCandidate, normalizedBoundary, comparison))
        {
            return true;
        }
        return normalizedCandidate.StartsWith(normalizedBoundary + Path.DirectorySeparatorChar, comparison);
    }

    private static string TrimTrailingSeparators(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(root) && string.Equals(fullPath, root, StringComparison.Ordinal))
        {
            return fullPath;
        }
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool TryReadBoundedUtf8Text(string filePath, int maxBytes, out string content)
    {
        content = string.Empty;
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var targetLength = (int)Math.Min(stream.Length, maxBytes);
            var buffer = new byte[targetLength];
            var totalRead = 0;
            while (totalRead < targetLength)
            {
                var read = stream.Read(buffer, totalRead, targetLength - totalRead);
                if (read <= 0)
                {
                    break;
                }
                totalRead += read;
            }

            // 若文件超过上限导致截断，剥离末尾不完整的多字节 UTF-8 序列避免出现乱码字符。
            if (stream.Length > maxBytes && totalRead > 0)
            {
                totalRead = TrimIncompleteUtf8Tail(buffer, totalRead);
            }

            content = LenientUtf8.GetString(buffer, 0, totalRead);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int TrimIncompleteUtf8Tail(byte[] buffer, int length)
    {
        var index = length - 1;
        // 向前跳过最多 3 个 UTF-8 续字节（10xxxxxx）。
        var continuationCount = 0;
        while (index >= 0 && (buffer[index] & 0xC0) == 0x80 && continuationCount < 3)
        {
            index--;
            continuationCount++;
        }

        if (index < 0)
        {
            return length;
        }

        var lead = buffer[index];
        var expectedBytes = lead switch
        {
            < 0x80 => 1,
            >= 0xC2 and <= 0xDF => 2,
            >= 0xE0 and <= 0xEF => 3,
            >= 0xF0 and <= 0xF4 => 4,
            _ => 1,
        };

        var actualBytes = length - index;
        return actualBytes < expectedBytes ? index : length;
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
