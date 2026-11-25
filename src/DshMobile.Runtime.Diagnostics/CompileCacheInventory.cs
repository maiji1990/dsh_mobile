using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DshMobile.Runtime.Diagnostics;

/// <summary>
/// 生产 <c>_layout.GuestCacheDir/node-compile-cache</c> 的只读盘点结果。它只暴露文件数、总字节数、子目录数量，以及
/// 经过白名单提取的 Node 版本 / CPU 架构前缀；完整缓存目录名（其中可能含 uid）和任何用户路径都不会进入日志或 JSON。
/// </summary>
public sealed record CompileCacheInventory(
    bool Exists,
    int FileCount,
    long TotalBytes,
    int DirectoryCount,
    IReadOnlyList<string> VersionDirectories,
    IReadOnlyList<string> Architectures,
    string? ErrorCode)
{
    public const string ErrorIo = "CACHE_INVENTORY_IO";

    private static readonly string[] KnownArchitectures =
    [
        "arm64", "x64", "x86_64", "arm", "ia32", "ppc64", "s390x", "riscv64", "loong64",
    ];

    /// <summary>空盘点：目录不存在不是错误，只是“没有可用证据”。</summary>
    public static CompileCacheInventory Empty { get; } = new(false, 0, 0, 0, [], [], null);

    /// <summary>只读盘点；任何 IO / 权限异常都收敛成固定错误码，不把异常消息（可能含路径）带出去。</summary>
    public static CompileCacheInventory Inspect(string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        try
        {
            if (!Directory.Exists(cacheDirectory))
            {
                return Empty;
            }

            var directories = Directory.EnumerateDirectories(cacheDirectory, "*", SearchOption.AllDirectories).ToList();
            var versions = new SortedSet<string>(StringComparer.Ordinal);
            var architectures = new SortedSet<string>(StringComparer.Ordinal);

            // 只读取第一层目录名，并只提取 “v<major>.<minor>...-<arch>” 中的版本与架构前缀；
            // uid、hash、临时后缀等其余 token 一律丢弃。
            foreach (var directory in Directory.EnumerateDirectories(cacheDirectory))
            {
                var name = Path.GetFileName(directory);
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }
                var tokens = name.Split('-', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length > 0 && IsVersionToken(tokens[0]))
                {
                    versions.Add(tokens[0]);
                }
                foreach (var token in tokens.Skip(1))
                {
                    if (KnownArchitectures.Contains(token, StringComparer.Ordinal))
                    {
                        architectures.Add(token);
                        break;
                    }
                }
            }

            var files = Directory.EnumerateFiles(cacheDirectory, "*", SearchOption.AllDirectories).ToList();
            long bytes = 0;
            foreach (var file in files)
            {
                try
                {
                    bytes += new FileInfo(file).Length;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // 单个文件读不到长度时仍保留文件计数；总字节数只累加真实可读的长度，不伪造。
                }
            }

            return new CompileCacheInventory(
                true,
                files.Count,
                bytes,
                directories.Count,
                versions.ToArray(),
                architectures.ToArray(),
                null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new CompileCacheInventory(false, 0, 0, 0, [], [], ErrorIo);
        }
    }

    /// <summary>只统计文件数与总字节数；用于隔离冷/热缓存的前后对比。</summary>
    public static (int Files, long Bytes) Count(string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        try
        {
            if (!Directory.Exists(cacheDirectory))
            {
                return (0, 0);
            }
            var files = Directory.EnumerateFiles(cacheDirectory, "*", SearchOption.AllDirectories).ToList();
            long bytes = 0;
            foreach (var file in files)
            {
                try
                {
                    bytes += new FileInfo(file).Length;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
            return (files.Count, bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return (0, 0);
        }
    }

    /// <summary>DshMobile 日志用摘要：只有计数与白名单前缀，没有路径。</summary>
    public string Summary
    {
        get
        {
            if (!Exists)
            {
                return ErrorCode is null ? "cache=absent" : $"cache=absent error={ErrorCode}";
            }
            var versions = VersionDirectories.Count == 0 ? "-" : string.Join(',', VersionDirectories);
            var architectures = Architectures.Count == 0 ? "-" : string.Join(',', Architectures);
            return "files=" + FileCount.ToString(CultureInfo.InvariantCulture)
                + " bytes=" + TotalBytes.ToString(CultureInfo.InvariantCulture)
                + " dirs=" + DirectoryCount.ToString(CultureInfo.InvariantCulture)
                + " versions=" + versions
                + " arch=" + architectures
                + (ErrorCode is null ? string.Empty : " error=" + ErrorCode);
        }
    }

    /// <summary>结构化 JSONL 行（schema 1）；只写白名单字段。</summary>
    public string ToJsonLine()
    {
        using var buffer = new MemoryStream(384);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", 1);
            writer.WriteString("experiment", WebProfileExperiments.CacheExperiment);
            writer.WriteString("kind", "inventory");
            writer.WriteString("cacheMode", "production");
            writer.WriteBoolean("exists", Exists);
            writer.WriteNumber("fileCount", FileCount);
            writer.WriteNumber("totalBytes", TotalBytes);
            writer.WriteNumber("directoryCount", DirectoryCount);
            writer.WriteStartArray("versionDirectories");
            foreach (var version in VersionDirectories)
            {
                writer.WriteStringValue(IsSafeToken(version) ? version : "?");
            }
            writer.WriteEndArray();
            writer.WriteStartArray("architectures");
            foreach (var architecture in Architectures)
            {
                writer.WriteStringValue(IsSafeToken(architecture) ? architecture : "?");
            }
            writer.WriteEndArray();
            writer.WriteString("result", ErrorCode is null ? "ok" : "failed");
            if (ErrorCode is null)
            {
                writer.WriteNull("errorCode");
            }
            else
            {
                writer.WriteString("errorCode", ErrorCode);
            }
            writer.WriteString("startedAt", DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static bool IsVersionToken(string token)
    {
        if (token.Length is < 3 or > 24 || token[0] != 'v')
        {
            return false;
        }
        var parts = token[1..].Split('.');
        if (parts.Length is < 2 or > 4)
        {
            return false;
        }
        foreach (var part in parts)
        {
            if (part.Length is 0 or > 8)
            {
                return false;
            }
            foreach (var ch in part)
            {
                if (!char.IsAsciiDigit(ch))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool IsSafeToken(string text)
    {
        if (text.Length is 0 or > 24)
        {
            return false;
        }
        foreach (var ch in text)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-'))
            {
                return false;
            }
        }
        return true;
    }
}
