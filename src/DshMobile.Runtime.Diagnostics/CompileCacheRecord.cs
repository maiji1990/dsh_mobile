using System.Globalization;
using System.Text;
using System.Text.Json;
using DshMobile.Core.Diagnostics;

namespace DshMobile.Runtime.Diagnostics;

/// <summary>
/// E5 编译缓存诊断记录（schema 1，独立 JSONL）。它记录的是“生产缓存盘点 / 冷启动落盘 / 热缓存命中率采样”的聚合证据：
/// 只有计数、版本/架构前缀、固定错误码与时间，绝不包含缓存目录路径、模块名、用户名或 token/Cookie。
/// </summary>
public sealed record CompileCacheRecord
{
    public const int Schema = 1;
    public const string InventoryKind = "inventory";
    public const string ColdKind = "cold";
    public const string ProbeKind = "probe";

    public const string FlushMissing = "COMPILE_CACHE_FLUSH_MISSING";
    public const string FlushUnsupported = "COMPILE_CACHE_FLUSH_UNSUPPORTED";
    public const string FlushFailed = "COMPILE_CACHE_FLUSH_FAILED";
    public const string CacheEmpty = "COMPILE_CACHE_EMPTY";
    public const string NoDebugOutput = "COMPILE_CACHE_NO_DEBUG_OUTPUT";

    public required string Kind { get; init; }
    public required string CacheMode { get; init; }
    public string? Variant { get; init; }
    public int? Iteration { get; init; }
    public double? HttpReadyMs { get; init; }
    public bool Exists { get; init; }
    public int FileCount { get; init; }
    public long TotalBytes { get; init; }
    public int DirectoryCount { get; init; }
    public IReadOnlyList<string> VersionDirectories { get; init; } = [];
    public IReadOnlyList<string> Architectures { get; init; } = [];
    public bool? FlushSupported { get; init; }
    public bool? FlushOk { get; init; }
    public string? FlushError { get; init; }
    public CompileCacheDebugCounts? Debug { get; init; }
    public required string Result { get; init; }
    public string? ErrorCode { get; init; }
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>能力 A：生产缓存只读盘点；目录不存在是有效观测，不报“成功落盘”。</summary>
    public static CompileCacheRecord ForInventory(CompileCacheInventory inventory, DateTimeOffset startedAt) =>
        new()
        {
            Kind = InventoryKind,
            CacheMode = "production",
            Exists = inventory.Exists,
            FileCount = inventory.FileCount,
            TotalBytes = inventory.TotalBytes,
            DirectoryCount = inventory.DirectoryCount,
            VersionDirectories = inventory.VersionDirectories,
            Architectures = inventory.Architectures,
            Result = inventory.ErrorCode is null ? ExperimentRecord.Ok : ExperimentRecord.Failed,
            ErrorCode = inventory.ErrorCode,
            StartedAt = startedAt,
        };

    /// <summary>冷启动：就绪后由 preload 主动 flush；文件数为 0、flush 缺失或失败都不得算成功。</summary>
    /// <param name="iteration">冷启动轮次（从 1 开始）；默认 null 表示未标注。</param>
    public static CompileCacheRecord ForCold(
        WebProfileRecord run,
        CompileCacheInventory inventory,
        CompileCacheFlushSummary? flush,
        DateTimeOffset startedAt,
        int? iteration = null)
    {
        var result = run.Result;
        var error = run.ErrorCode;
        if (string.Equals(result, ExperimentRecord.Ok, StringComparison.Ordinal))
        {
            if (flush is null)
            {
                result = ExperimentRecord.Failed;
                error = FlushMissing;
            }
            else if (!flush.Supported)
            {
                result = ExperimentRecord.Failed;
                error = FlushUnsupported;
            }
            else if (!flush.Flushed)
            {
                result = ExperimentRecord.Failed;
                error = FlushFailed;
            }
            else if (inventory.FileCount == 0)
            {
                result = ExperimentRecord.Failed;
                error = CacheEmpty;
            }
        }
        return new CompileCacheRecord
        {
            Kind = ColdKind,
            CacheMode = "cold",
            Variant = run.Variant,
            HttpReadyMs = run.HttpReadyMs,
            Exists = inventory.Exists,
            FileCount = inventory.FileCount,
            TotalBytes = inventory.TotalBytes,
            DirectoryCount = inventory.DirectoryCount,
            VersionDirectories = inventory.VersionDirectories,
            Architectures = inventory.Architectures,
            FlushSupported = flush?.Supported,
            FlushOk = flush?.Flushed,
            FlushError = flush?.Error,
            Iteration = iteration,
            Result = result,
            ErrorCode = error,
            StartedAt = startedAt,
        };
    }

    /// <summary>热缓存命中率采样：只有确实读到 COMPILE_CACHE 日志才算成功；命中率为 0 是数据，不是伪造的成功。</summary>
    /// <param name="iteration">采样轮次；默认 null 表示未标注。</param>
    public static CompileCacheRecord ForProbe(
        WebProfileRecord run,
        CompileCacheInventory inventory,
        CompileCacheDebugCounts debug,
        DateTimeOffset startedAt,
        int? iteration = null)
    {
        var result = run.Result;
        var error = run.ErrorCode;
        if (string.Equals(result, ExperimentRecord.Ok, StringComparison.Ordinal) && debug.Readings == 0)
        {
            result = ExperimentRecord.Failed;
            error = NoDebugOutput;
        }
        return new CompileCacheRecord
        {
            Kind = ProbeKind,
            CacheMode = "warm",
            Variant = run.Variant,
            HttpReadyMs = run.HttpReadyMs,
            Exists = inventory.Exists,
            FileCount = inventory.FileCount,
            TotalBytes = inventory.TotalBytes,
            DirectoryCount = inventory.DirectoryCount,
            VersionDirectories = inventory.VersionDirectories,
            Architectures = inventory.Architectures,
            Debug = debug,
            Iteration = iteration,
            Result = result,
            ErrorCode = error,
            StartedAt = startedAt,
        };
    }

    /// <summary>DshMobile 日志用摘要；只有计数与固定码。</summary>
    public string Summary
    {
        get
        {
            var text = "kind=" + Kind
                + " files=" + FileCount.ToString(CultureInfo.InvariantCulture)
                + " bytes=" + TotalBytes.ToString(CultureInfo.InvariantCulture);
            if (Debug is { } debug)
            {
                text += " read=" + debug.Readings.ToString(CultureInfo.InvariantCulture)
                    + " hit=" + debug.Hits.ToString(CultureInfo.InvariantCulture)
                    + " miss=" + debug.Misses.ToString(CultureInfo.InvariantCulture)
                    + " write=" + debug.Writes.ToString(CultureInfo.InvariantCulture)
                    + " reject=" + debug.Rejects.ToString(CultureInfo.InvariantCulture)
                    + " hitRate=" + (debug.HitRate is { } rate ? rate.ToString("0.####", CultureInfo.InvariantCulture) : "-");
            }
            return text + " result=" + Result + (ErrorCode is null ? string.Empty : " error=" + ErrorCode);
        }
    }

    /// <summary>固定字段 JSONL；不写入任何路径或自由文本。</summary>
    public string ToJsonLine()
    {
        using var buffer = new MemoryStream(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", Schema);
            writer.WriteString("experiment", WebProfileExperiments.CacheExperiment);
            writer.WriteString("kind", Kind);
            writer.WriteString("cacheMode", CacheMode);
            WriteText(writer, "variant", Variant);
            WriteInt(writer, "iteration", Iteration);
            WriteNumber(writer, "httpReadyMs", HttpReadyMs);
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
            WriteBool(writer, "flushSupported", FlushSupported);
            WriteBool(writer, "flushOk", FlushOk);
            WriteText(writer, "flushError", FlushError);
            WriteInt(writer, "readings", Debug?.Readings);
            WriteInt(writer, "hits", Debug?.Hits);
            WriteInt(writer, "misses", Debug?.Misses);
            WriteInt(writer, "writes", Debug?.Writes);
            WriteInt(writer, "rejects", Debug?.Rejects);
            WriteInt(writer, "readFailures", Debug?.ReadFailures);
            WriteNumber(writer, "hitRate", Debug?.HitRate);
            writer.WriteString("result", Result);
            WriteText(writer, "errorCode", ErrorCode);
            writer.WriteString("startedAt", StartedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteText(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, IsSafeToken(value) ? value : StartupTrace.Redacted);
        }
    }

    private static void WriteInt(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteNumber(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } number && double.IsFinite(number))
        {
            writer.WriteNumber(name, Math.Round(number, 4, MidpointRounding.AwayFromZero));
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteBool(Utf8JsonWriter writer, string name, bool? value)
    {
        if (value is { } flag)
        {
            writer.WriteBoolean(name, flag);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static bool IsSafeToken(string text)
    {
        if (text.Length is 0 or > 64)
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

/// <summary>
/// preload 在 stop 阶段主动调用 <c>module.flushCompileCache()</c> 后写回的数字/固定码摘要。
/// 解析只接受 schema 1 与固定布尔的 error token；任何路径、自由文本或额外字段都不会被采用。
/// </summary>
public sealed record CompileCacheFlushSummary(bool Supported, bool Flushed, string? Error)
{
    public static CompileCacheFlushSummary? TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schema", out var schema)
                || schema.GetInt32() != 1
                || !root.TryGetProperty("supported", out var supported)
                || supported.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty("flushed", out var flushed)
                || flushed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }
            string? error = null;
            if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind != JsonValueKind.Null)
            {
                if (errorElement.ValueKind != JsonValueKind.String)
                {
                    return null;
                }
                var text = errorElement.GetString();
                if (text is null || text.Length is 0 or > 64 || !text.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-'))
                {
                    return null;
                }
                error = text;
            }
            return new CompileCacheFlushSummary(supported.GetBoolean(), flushed.GetBoolean(), error);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
