using System.Text;
using System.Text.Json;
using DshMobile.Core.Diagnostics;

namespace DshMobile.Runtime.Diagnostics;

/// <summary>Fixed Web-profile diagnostic variants (docs/WEB_STARTUP_DIAGNOSIS.md). No caller-supplied argv or path.</summary>
public sealed record WebProfileVariant(string Id, string Mode);

public static class WebProfileExperiments
{
    public const string Experiment = "E5";
    public const string Suite = "E5";
    public const string Off = "E5-OFF";
    public const string Profile = "E5-PROFILE";

    /// <summary>编译缓存盘点 / 冷启动 / 命中率采样的独立 JSONL 组名。</summary>
    public const string CacheExperiment = "E5-CACHE";

    /// <summary>Guest directory of the preload and the file handshake (bound from the isolated cache).</summary>
    public const string GuestDir = RuntimeConstants.GuestCacheDir + "/experiments/e5";

    /// <summary>与生产完全一致的 Node compile cache guest 路径（isolated.CacheDir/node-compile-cache）。</summary>
    public const string NodeCompileCacheGuest = RuntimeConstants.GuestCacheDir + "/node-compile-cache";

    public const string PreloadName = "web-profile-preload.mjs";
    public const string StopRequest = "stop.request";
    public const string Done = "profile.done";
    public const string Summary = "summary.json";
    public const string ProfileFile = "profile.cpuprofile";

    /// <summary>preload 主动 flush compile cache 后写回的固定摘要文件。</summary>
    public const string CompileCacheFlush = "compile-cache.flush.json";

    public const string ProfileTimeout = "PROFILE_TIMEOUT";
    public const string ProfileWriteFailed = "PROFILE_WRITE_FAILED";
    public const string ProfileEmpty = "PROFILE_EMPTY";
    public const string ProfileUnavailable = "PROFILE_UNAVAILABLE";

    /// <summary>How long the host waits for the preload to finish writing after the stop request.</summary>
    public static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(5);

    public static IReadOnlyList<WebProfileVariant> All { get; } =
    [
        new(Off, "off"),
        new(Profile, "profile"),
    ];

    public static bool TryGet(string id, out WebProfileVariant variant)
    {
        foreach (var v in All)
        {
            if (string.Equals(v.Id, id, StringComparison.Ordinal))
            {
                variant = v;
                return true;
            }
        }
        variant = null!;
        return false;
    }
}

/// <summary>
/// One Web-profile diagnostic run (schema 1, separate JSONL stream - the E1-E4 schema is untouched). Only numbers and
/// fixed codes; the raw CPU profile never appears here (docs/WEB_STARTUP_DIAGNOSIS.md).
/// </summary>
public sealed record WebProfileRecord
{
    public const int Schema = 1;

    public required string Variant { get; init; }
    public required string RunId { get; init; }
    public required int Iteration { get; init; }
    public double? HttpReadyMs { get; init; }
    public double? ProfileCaptureMs { get; init; }
    public int? ProfileSampleCount { get; init; }
    public double? NodeUserCpuMs { get; init; }
    public double? NodeSystemCpuMs { get; init; }
    public double? EventLoopActiveMs { get; init; }
    public double? EventLoopIdleMs { get; init; }
    public double? StopLatencyMs { get; init; }
    public required string Result { get; init; }
    public string? ErrorCode { get; init; }
    public bool ProfileStoredPrivately { get; init; }
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>热缓存轮次标记：cold 只写独立 E5-CACHE 行，warm 才进入 OFF/PROFILE 配对样本。</summary>
    public string? CacheMode { get; init; }

    /// <summary>该轮结束时的隔离 compile cache 文件数（只读计数）。</summary>
    public int? CompileCacheFileCount { get; init; }

    /// <summary>该轮结束时的隔离 compile cache 总字节数。</summary>
    public long? CompileCacheBytes { get; init; }

    public string ToJsonLine()
    {
        using var buffer = new MemoryStream(512);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", Schema);
            w.WriteString("experiment", WebProfileExperiments.Experiment);
            WriteText(w, "variant", Variant);
            WriteText(w, "runId", RunId);
            w.WriteNumber("iteration", Iteration);
            WriteNumber(w, "httpReadyMs", HttpReadyMs);
            WriteNumber(w, "profileCaptureMs", ProfileCaptureMs);
            WriteInt(w, "profileSampleCount", ProfileSampleCount);
            WriteNumber(w, "nodeUserCpuMs", NodeUserCpuMs);
            WriteNumber(w, "nodeSystemCpuMs", NodeSystemCpuMs);
            WriteNumber(w, "eventLoopActiveMs", EventLoopActiveMs);
            WriteNumber(w, "eventLoopIdleMs", EventLoopIdleMs);
            WriteNumber(w, "stopLatencyMs", StopLatencyMs);
            WriteText(w, "result", Result);
            if (ErrorCode is null)
            {
                w.WriteNull("errorCode");
            }
            else
            {
                WriteText(w, "errorCode", ErrorCode);
            }
            w.WriteBoolean("profileStoredPrivately", ProfileStoredPrivately);
            if (CacheMode is null)
            {
                w.WriteNull("cacheMode");
            }
            else
            {
                WriteText(w, "cacheMode", CacheMode);
            }
            WriteInt(w, "compileCacheFileCount", CompileCacheFileCount);
            WriteLong(w, "compileCacheBytes", CompileCacheBytes);
            w.WriteString("startedAt", StartedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture));
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteText(Utf8JsonWriter w, string name, string value) =>
        w.WriteString(name, IsToken(value) ? value : StartupTrace.Redacted);

    private static void WriteNumber(Utf8JsonWriter w, string name, double? value)
    {
        if (value is { } v && double.IsFinite(v))
        {
            w.WriteNumber(name, Math.Round(v, 3, MidpointRounding.AwayFromZero));
        }
        else
        {
            w.WriteNull(name);
        }
    }

    private static void WriteInt(Utf8JsonWriter w, string name, int? value)
    {
        if (value is { } v)
        {
            w.WriteNumber(name, v);
        }
        else
        {
            w.WriteNull(name);
        }
    }

    private static void WriteLong(Utf8JsonWriter w, string name, long? value)
    {
        if (value is { } v)
        {
            w.WriteNumber(name, v);
        }
        else
        {
            w.WriteNull(name);
        }
    }

    private static bool IsToken(string text)
    {
        if (text.Length is 0 or > 48)
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

/// <summary>The numeric summary the preload writes after the stop request (never contains names or paths).</summary>
public sealed record WebProfileSummary(
    double? WallMs,
    double? NodeUserCpuMs,
    double? NodeSystemCpuMs,
    double? EventLoopActiveMs,
    double? EventLoopIdleMs,
    int? ProfileSampleCount,
    double? StopLatencyMs,
    string? Error)
{
    /// <summary>
    /// Parses only the documented numeric fields and a fixed error-code allowlist. Anything else (a path, a module name,
    /// a token) is ignored, and a malformed or unexpected payload returns null instead of a half-trusted summary.
    /// </summary>
    public static WebProfileSummary? TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var schema) || schema.GetInt32() != 1)
            {
                return null;
            }
            string? error = null;
            if (root.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
            {
                if (err.ValueKind != JsonValueKind.String)
                {
                    return null;
                }
                var text = err.GetString();
                if (text is not (WebProfileExperiments.ProfileEmpty or WebProfileExperiments.ProfileWriteFailed or WebProfileExperiments.ProfileUnavailable))
                {
                    return null; // an unknown code is not handed back as "no error"
                }
                error = text;
            }
            return new WebProfileSummary(
                Number(root, "wallMs"),
                Number(root, "nodeUserCpuMs"),
                Number(root, "nodeSystemCpuMs"),
                Number(root, "eventLoopActiveMs"),
                Number(root, "eventLoopIdleMs"),
                Int(root, "profileSampleCount"),
                Number(root, "stopLatencyMs"),
                error);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static double? Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    private static int? Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
