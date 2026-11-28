using System.Text;
using System.Text.Json;
using DshMobile.Agent.Domain;

namespace DshMobile.Agent.Sdui;

/// <summary>
/// 一次 SDUI 文档加载的结果。<see cref="Source"/> 为 <c>cache</c> 或 <c>manifest</c>，
/// 失败时 <see cref="DiagnosticKey"/> 给出稳定诊断键，<see cref="ValidationErrors"/> 给出可展示的中文原因。
/// </summary>
public sealed record PluginSduiLoadResult(
    bool Found,
    PluginSduiDocument? Document = null,
    string? Source = null,
    string? DiagnosticKey = null,
    IReadOnlyList<string>? ValidationErrors = null);

/// <summary>
/// 插件 SDUI 文档的版本化缓存存储（Slice 1B）：
/// 优先读取 <c>&lt;cacheDir&gt;/sdui-cache/&lt;包名&gt;@&lt;版本&gt;.json</c>，未命中或缓存损坏时回退到
/// 运行时桥接层提供的包内置 <c>dshmobile.sdui.json</c>，校验通过后原子写回缓存。
/// 所有路径都做包名校验与目录穿越防护，单个缓存文件上限 512 KiB，损坏缓存会被自动删除。
/// </summary>
public sealed class PluginSduiStore
{
    /// <summary>缓存子目录名。</summary>
    public const string CacheDirectoryName = "sdui-cache";

    /// <summary>单个缓存文件的最大字节数（512 KiB）。</summary>
    public const int MaxCacheFileBytes = 512 * 1024;

    /// <summary>结果来源：版本化缓存。</summary>
    public const string CacheSource = "cache";

    /// <summary>结果来源：包内置清单。</summary>
    public const string ManifestSource = "manifest";

    /// <summary>诊断键：包名非法。</summary>
    public const string InvalidPackageDiagnostic = "plugin_sdui.invalid_package";

    /// <summary>诊断键：JSON 无法解析。</summary>
    public const string InvalidJsonDiagnostic = "plugin_sdui.invalid_json";

    /// <summary>诊断键：文档未通过安全校验。</summary>
    public const string InvalidDocumentDiagnostic = "plugin_sdui.invalid_document";

    /// <summary>诊断键：内置清单不可读。</summary>
    public const string ManifestUnavailableDiagnostic = "plugin_sdui.manifest_unavailable";

    /// <summary>诊断键：内置清单超出大小上限。</summary>
    public const string ManifestTooLargeDiagnostic = "plugin_sdui.manifest_too_large";

    /// <summary>诊断键：缓存与内置清单均不存在。</summary>
    public const string NotFoundDiagnostic = "plugin_sdui.not_found";

    /// <summary>诊断键：生成文档超出单个缓存文件大小上限。</summary>
    public const string DocumentTooLargeDiagnostic = "plugin_sdui.document_too_large";

    /// <summary>诊断键：生成文档写入缓存失败。</summary>
    public const string CacheWriteFailedDiagnostic = "plugin_sdui.cache_write_failed";

    private readonly string _cacheRootDirectory;
    private readonly IPluginOperations _pluginOperations;

    /// <summary>创建存储；<paramref name="cacheDirectory"/> 为宿主缓存根目录，内部会追加 <c>sdui-cache</c> 子目录。</summary>
    public PluginSduiStore(string cacheDirectory, IPluginOperations pluginOperations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        ArgumentNullException.ThrowIfNull(pluginOperations);
        _cacheRootDirectory = Path.GetFullPath(Path.Combine(cacheDirectory, CacheDirectoryName));
        _pluginOperations = pluginOperations;
    }

    /// <summary>实际使用的版本化缓存目录。</summary>
    public string CacheDirectory => _cacheRootDirectory;

    /// <summary>
    /// 计算某个包与版本对应的缓存文件绝对路径；包名非法或路径越界时返回 <see langword="null"/>。
    /// </summary>
    public string? TryGetCacheFilePath(string packageName, string? pluginVersion)
    {
        if (!PluginPackageValidator.IsValidPackageName(packageName))
        {
            return null;
        }

        var fileName = string.Concat(
            SanitizeFileNameSegment(packageName),
            "@",
            SanitizeFileNameSegment(NormalizeVersion(pluginVersion)),
            ".json");

        var fullPath = Path.GetFullPath(Path.Combine(_cacheRootDirectory, fileName));
        var directoryPrefix = _cacheRootDirectory + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
    }

    /// <summary>
    /// 加载插件 SDUI 文档：缓存优先，未命中/损坏/版本不匹配时回退到包内置清单；
    /// 内置清单校验通过后按版本原子写回缓存。任何失败都返回稳定诊断键，绝不伪造文档。
    /// </summary>
    public async Task<PluginSduiLoadResult> LoadAsync(
        string packageName,
        string? pluginVersion = null,
        CancellationToken ct = default)
    {
        if (!PluginPackageValidator.IsValidPackageName(packageName))
        {
            return new PluginSduiLoadResult(false, DiagnosticKey: InvalidPackageDiagnostic);
        }

        var requestedVersion = string.IsNullOrWhiteSpace(pluginVersion) ? null : pluginVersion.Trim();
        if (requestedVersion is not null)
        {
            var cachePath = TryGetCacheFilePath(packageName, requestedVersion);
            if (cachePath is not null)
            {
                var cachedDocument = await TryReadCacheAsync(cachePath, packageName, requestedVersion, ct).ConfigureAwait(false);
                if (cachedDocument is not null)
                {
                    return new PluginSduiLoadResult(true, cachedDocument, CacheSource);
                }
            }
        }

        Outcome<string> manifest;
        try
        {
            manifest = await _pluginOperations.ReadLocalSduiManifestAsync(packageName, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PluginSduiLoadResult(false, DiagnosticKey: ManifestUnavailableDiagnostic);
        }

        if (!manifest.IsOk || string.IsNullOrWhiteSpace(manifest.Value))
        {
            return new PluginSduiLoadResult(
                false,
                DiagnosticKey: manifest.Error?.DiagnosticKey ?? ManifestUnavailableDiagnostic);
        }

        var manifestJson = manifest.Value;
        if (Encoding.UTF8.GetByteCount(manifestJson) > MaxCacheFileBytes)
        {
            return new PluginSduiLoadResult(false, DiagnosticKey: ManifestTooLargeDiagnostic);
        }

        var document = TryDeserialize(manifestJson);
        if (document is null)
        {
            return new PluginSduiLoadResult(false, DiagnosticKey: InvalidJsonDiagnostic);
        }

        var validation = PluginSduiValidator.Validate(document);
        if (!validation.IsValid)
        {
            return new PluginSduiLoadResult(
                false,
                document,
                ManifestSource,
                InvalidDocumentDiagnostic,
                validation.Errors);
        }

        if (!string.Equals(document.PluginPackage, packageName, StringComparison.Ordinal))
        {
            return new PluginSduiLoadResult(
                false,
                document,
                ManifestSource,
                InvalidDocumentDiagnostic,
                [$"pluginPackage 与读取来源不一致：{document.PluginPackage}。"]);
        }

        if (requestedVersion is not null &&
            string.Equals(document.PluginVersion, requestedVersion, StringComparison.Ordinal))
        {
            await TryWriteCacheAsync(document, packageName, requestedVersion, ct).ConfigureAwait(false);
        }

        return new PluginSduiLoadResult(true, document, ManifestSource);
    }

    /// <summary>
    /// 保存 Agent 生成的 SDUI 文档（Slice 1B-2）：先做包名、文档安全校验与来源一致性检查，
    /// 再按目标版本原子写入版本化缓存。任何校验或落盘失败都返回稳定诊断键，绝不返回“看似成功”。
    /// </summary>
    /// <param name="document">待保存的 SDUI 文档。</param>
    /// <param name="expectedPackageName">期望的 npm 包名，必须与文档中的 <c>pluginPackage</c> 完全一致。</param>
    /// <param name="expectedVersion">目标版本；为空时使用文档自身的 <c>pluginVersion</c>。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<PluginSduiLoadResult> SaveGeneratedAsync(
        PluginSduiDocument document,
        string expectedPackageName,
        string? expectedVersion = null,
        CancellationToken ct = default)
    {
        if (!PluginPackageValidator.IsValidPackageName(expectedPackageName))
        {
            return new PluginSduiLoadResult(false, DiagnosticKey: InvalidPackageDiagnostic);
        }

        var validation = PluginSduiValidator.Validate(document);
        if (!validation.IsValid)
        {
            return new PluginSduiLoadResult(
                false,
                document,
                null,
                InvalidDocumentDiagnostic,
                validation.Errors);
        }

        if (!string.Equals(document.PluginPackage, expectedPackageName, StringComparison.Ordinal))
        {
            return new PluginSduiLoadResult(
                false,
                document,
                null,
                InvalidDocumentDiagnostic,
                [$"pluginPackage 与目标包名不一致：{document.PluginPackage}。"]);
        }

        var targetVersion = !string.IsNullOrWhiteSpace(expectedVersion)
            ? expectedVersion.Trim()
            : document.PluginVersion;

        string json;
        try
        {
            json = JsonSerializer.Serialize(document, PluginSduiJsonContext.Default.PluginSduiDocument);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PluginSduiLoadResult(
                false,
                document,
                null,
                InvalidDocumentDiagnostic,
                ["SDUI 文档无法序列化。"]
            );
        }

        if (Encoding.UTF8.GetByteCount(json) > MaxCacheFileBytes)
        {
            return new PluginSduiLoadResult(
                false,
                document,
                null,
                DocumentTooLargeDiagnostic,
                [$"SDUI 文档超过 {MaxCacheFileBytes} 字节上限。"]);
        }

        ct.ThrowIfCancellationRequested();
        var written = await TryWriteCacheAsync(document, expectedPackageName, targetVersion, ct).ConfigureAwait(false);
        if (!written)
        {
            return new PluginSduiLoadResult(
                false,
                document,
                null,
                CacheWriteFailedDiagnostic,
                ["SDUI 文档写入版本化缓存失败。"]);
        }

        return new PluginSduiLoadResult(true, document, CacheSource);
    }

    /// <summary>删除某个包的全部版本化缓存文件；目录不存在或包名非法时返回对应结果。</summary>
    public Task<bool> ClearCachedAsync(string packageName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!PluginPackageValidator.IsValidPackageName(packageName) || !Directory.Exists(_cacheRootDirectory))
        {
            return Task.FromResult(false);
        }

        var prefix = SanitizeFileNameSegment(packageName) + "@";
        var succeeded = true;
        foreach (var path in Directory.EnumerateFiles(_cacheRootDirectory, prefix + "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                succeeded = false;
            }
        }

        return Task.FromResult(succeeded);
    }

    private static async Task<PluginSduiDocument?> TryReadCacheAsync(
        string cachePath,
        string packageName,
        string expectedVersion,
        CancellationToken ct)
    {
        try
        {
            var fileInfo = new FileInfo(cachePath);
            if (!fileInfo.Exists)
            {
                return null;
            }

            if (fileInfo.Length is <= 0 or > MaxCacheFileBytes)
            {
                DeleteQuietly(cachePath);
                return null;
            }

            var json = await File.ReadAllTextAsync(cachePath, Encoding.UTF8, ct).ConfigureAwait(false);
            var document = TryDeserialize(json);
            if (document is null ||
                !string.Equals(document.PluginPackage, packageName, StringComparison.Ordinal) ||
                !string.Equals(document.PluginVersion, expectedVersion, StringComparison.Ordinal) ||
                !PluginSduiValidator.Validate(document).IsValid)
            {
                DeleteQuietly(cachePath);
                return null;
            }

            return document;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DeleteQuietly(cachePath);
            return null;
        }
    }

    private async Task<bool> TryWriteCacheAsync(
        PluginSduiDocument document,
        string packageName,
        string pluginVersion,
        CancellationToken ct)
    {
        var cachePath = TryGetCacheFilePath(packageName, pluginVersion);
        if (cachePath is null)
        {
            return false;
        }

        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(cachePath);
            if (string.IsNullOrEmpty(directory))
            {
                return false;
            }
            Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(document, PluginSduiJsonContext.Default.PluginSduiDocument);
            if (Encoding.UTF8.GetByteCount(json) > MaxCacheFileBytes)
            {
                return false;
            }

            temporaryPath = string.Concat(cachePath, ".tmp-", Guid.NewGuid().ToString("N"));
            await File.WriteAllTextAsync(temporaryPath, json, new UTF8Encoding(false), ct).ConfigureAwait(false);
            File.Move(temporaryPath, cachePath, overwrite: true);
            temporaryPath = null;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 缓存写入只是加速路径：失败不影响本次已校验文档的返回。
            if (temporaryPath is not null)
            {
                DeleteQuietly(temporaryPath);
            }
            return false;
        }
    }

    private static PluginSduiDocument? TryDeserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, PluginSduiJsonContext.Default.PluginSduiDocument);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 损坏缓存清理失败不影响加载主流程，下一次加载会再次尝试。
        }
    }

    private static string NormalizeVersion(string? pluginVersion)
    {
        if (string.IsNullOrWhiteSpace(pluginVersion))
        {
            return "unknown";
        }

        var trimmed = pluginVersion.Trim();
        return trimmed.Length > 64 ? trimmed[..64] : trimmed;
    }

    private static string SanitizeFileNameSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'
                ? character
                : '_');
        }

        var sanitized = builder.ToString();
        while (sanitized.Contains("..", StringComparison.Ordinal))
        {
            sanitized = sanitized.Replace("..", "_", StringComparison.Ordinal);
        }

        sanitized = sanitized.Trim('.');
        return sanitized.Length == 0 ? "_" : sanitized;
    }
}
