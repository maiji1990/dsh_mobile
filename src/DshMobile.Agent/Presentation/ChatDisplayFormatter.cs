using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using DshMobile.Agent.Domain;

namespace DshMobile.Agent.Presentation;

/// <summary>工具调用卡片的结构化展示摘要（折叠态人类可读摘要 + 展开态美化 JSON）。</summary>
public sealed record ToolCallDisplaySummary(
    string HeaderLabel,
    string? SummaryLine,
    string? DetailJson);

/// <summary>
/// 聊天与会话列表展示格式化工具（纯托管逻辑，供 Android UI 复用并接受单元测试）。
/// </summary>
public static partial class ChatDisplayFormatter
{
    private static readonly JsonWriterOptions IndentedJsonOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly string[] DescriptionKeys = ["description", "desc", "summary", "title"];
    private static readonly string[] CommandKeys = ["command", "cmd", "script"];
    private static readonly string[] PathKeys =
    [
        "file_path",
        "filePath",
        "target_file",
        "targetFile",
        "path",
        "file",
        "filename",
        "relative_path",
        "relativePath",
    ];
    private static readonly string[] PatternKeys = ["pattern", "query", "glob", "regex", "url"];

    /// <summary>将空标题、裸 UUID 或 <c>session-&lt;uuid&gt;</c> 格式化为友好的短标题（如 <c>未命名会话 · #bcce5e</c>）。</summary>
    public static string FormatShortSessionTitle(
        string sessionId,
        string? title,
        string untitledPrefix = "未命名会话")
    {
        var trimmed = title?.Trim();
        if (!string.IsNullOrEmpty(trimmed) && !IsUuidLike(trimmed))
        {
            return trimmed;
        }

        var source = StripSessionPrefix(!string.IsNullOrEmpty(trimmed) ? trimmed : sessionId.Trim());
        var sb = new StringBuilder(6);
        foreach (var ch in source)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                if (sb.Length == 6)
                {
                    break;
                }
            }
        }
        var tag = sb.Length > 0 ? sb.ToString() : "session";
        return $"{untitledPrefix} · #{tag}";
    }

    /// <summary>
    /// 会话列表紧凑相对时间：今天内 5 分钟内为「刚刚」，否则 <c>HH:mm</c>；昨天为「昨天」；更早为 <c>MM-dd</c>。
    /// 无有效时间戳（<paramref name="updatedAtUnixMs"/> ≤ 0）返回空串，由 UI 自行隐藏时间槽。
    /// </summary>
    public static string FormatRelativeSessionTime(long updatedAtUnixMs, DateTimeOffset now)
    {
        if (updatedAtUnixMs <= 0)
        {
            return string.Empty;
        }

        var updated = DateTimeOffset.FromUnixTimeMilliseconds(updatedAtUnixMs).ToLocalTime();
        var reference = now.ToLocalTime();
        var updatedDate = updated.Date;
        var today = reference.Date;
        if (updatedDate == today)
        {
            var age = reference - updated;
            return age >= TimeSpan.Zero && age < TimeSpan.FromMinutes(5)
                ? "刚刚"
                : updated.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        return updatedDate == today.AddDays(-1)
            ? "昨天"
            : updated.ToString("MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>提取工作区路径的末级目录名（兼容 <c>/</c> 与 <c>\</c>），用于替代副标题里重复的整串路径。</summary>
    public static string? ExtractLastPathSegment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = path.Replace('\\', '/').Trim().TrimEnd('/');
        if (normalized.Length == 0)
        {
            return null;
        }

        var separator = normalized.LastIndexOf('/');
        return separator >= 0 && separator + 1 < normalized.Length
            ? normalized[(separator + 1)..]
            : normalized;
    }

    /// <summary>生成底部输入区使用的紧凑模型/档位胶囊文本（去掉冗余 Provider 前缀）。</summary>
    public static string FormatCompactModelBadge(
        string? modelLabel,
        ModelSelectionSpec? currentModel,
        string fallbackLabel)
    {
        string? modelName = currentModel?.Model;
        var effort = currentModel?.ReasoningEffort;

        if (string.IsNullOrWhiteSpace(modelName) && !string.IsNullOrWhiteSpace(modelLabel))
        {
            var raw = modelLabel.Trim();
            var slash = raw.LastIndexOf('/');
            modelName = slash >= 0 && slash + 1 < raw.Length ? raw[(slash + 1)..] : raw;
        }

        if (string.IsNullOrWhiteSpace(modelName))
        {
            return fallbackLabel;
        }

        modelName = modelName.Trim();
        if (string.IsNullOrWhiteSpace(effort))
        {
            return modelName;
        }

        var effortTrimmed = effort.Trim();
        return modelName.Contains(effortTrimmed, StringComparison.OrdinalIgnoreCase)
            ? modelName
            : $"{modelName} · {effortTrimmed}";
    }

    /// <summary>解析工具调用参数 JSON，生成紧凑单行可读摘要与展开态美化 JSON。</summary>
    public static ToolCallDisplaySummary FormatToolCallSummary(
        string toolName,
        string? argumentsJson,
        string fallbackPrefix = "执行工具")
    {
        var shortTool = string.IsNullOrWhiteSpace(toolName) ? "tool" : toolName.Trim();
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return new ToolCallDisplaySummary($"{fallbackPrefix} · {shortTool}", null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ToolCallDisplaySummary(
                    $"{fallbackPrefix} · {shortTool}",
                    ToSingleLine(argumentsJson, 140),
                    PrettyJson(doc.RootElement));
            }

            var root = doc.RootElement;
            return BuildStructuredSummary(
                shortTool,
                fallbackPrefix,
                FindFirstString(root, DescriptionKeys),
                FindFirstString(root, CommandKeys),
                FindFirstString(root, PathKeys),
                FindFirstString(root, PatternKeys),
                argumentsJson,
                PrettyJson(root));
        }
        catch (JsonException)
        {
            var looseDesc = FindFirstLooseString(argumentsJson, DescriptionKeys);
            var looseCmd = FindFirstLooseString(argumentsJson, CommandKeys);
            var loosePath = FindFirstLooseString(argumentsJson, PathKeys);
            var loosePattern = FindFirstLooseString(argumentsJson, PatternKeys);

            if (looseDesc is not null || looseCmd is not null || loosePath is not null || loosePattern is not null)
            {
                return BuildStructuredSummary(
                    shortTool,
                    fallbackPrefix,
                    looseDesc,
                    looseCmd,
                    loosePath,
                    loosePattern,
                    argumentsJson,
                    argumentsJson);
            }

            return new ToolCallDisplaySummary(
                $"{fallbackPrefix} · {shortTool}",
                ToSingleLine(argumentsJson, 140),
                argumentsJson);
        }
    }

    private static ToolCallDisplaySummary BuildStructuredSummary(
        string shortTool,
        string fallbackPrefix,
        string? description,
        string? command,
        string? path,
        string? pattern,
        string rawArguments,
        string detailJson)
    {
        string headerLabel;
        if (!string.IsNullOrWhiteSpace(description))
        {
            headerLabel = $"{shortTool} · {ToSingleLine(description, 48)}";
        }
        else if (!string.IsNullOrWhiteSpace(path))
        {
            headerLabel = $"{shortTool} · {ExtractFileNameOrTail(path)}";
        }
        else if (!string.IsNullOrWhiteSpace(pattern))
        {
            headerLabel = $"{shortTool} · {ToSingleLine(pattern, 40)}";
        }
        else
        {
            headerLabel = $"{fallbackPrefix} · {shortTool}";
        }

        string? summaryLine = null;
        if (!string.IsNullOrWhiteSpace(command))
        {
            summaryLine = $"$ {ToSingleLine(command, 140)}";
        }
        else if (!string.IsNullOrWhiteSpace(path))
        {
            summaryLine = !string.IsNullOrWhiteSpace(pattern)
                ? $"{ToSingleLine(pattern, 60)} @ {ToSingleLine(path, 80)}"
                : ToSingleLine(path, 140);
        }
        else if (!string.IsNullOrWhiteSpace(pattern))
        {
            summaryLine = ToSingleLine(pattern, 140);
        }
        else if (!string.IsNullOrWhiteSpace(description))
        {
            summaryLine = null;
        }
        else
        {
            summaryLine = ToSingleLine(rawArguments, 140);
        }

        return new ToolCallDisplaySummary(headerLabel, summaryLine, detailJson);
    }

    private static string StripSessionPrefix(string value)
    {
        if (value.StartsWith("session-", StringComparison.OrdinalIgnoreCase))
        {
            return value[8..];
        }
        if (value.StartsWith("sess-", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("sess_", StringComparison.OrdinalIgnoreCase))
        {
            return value[5..];
        }
        return value;
    }

    private static bool IsUuidLike(string value)
    {
        var stripped = StripSessionPrefix(value);
        if (Guid.TryParse(stripped, out _))
        {
            return true;
        }
        return stripped.Length >= 16
            && stripped.Contains('-', StringComparison.Ordinal)
            && stripped.All(c => char.IsAsciiHexDigit(c) || c == '-' || c == '…');
    }

    private static string? FindFirstLooseString(string json, string[] keys)
    {
        foreach (var key in keys)
        {
            var m = Regex.Match(
                json,
                $"\"{Regex.Escape(key)}\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)(?:\"|$)",
                RegexOptions.CultureInvariant);
            if (m.Success)
            {
                var raw = m.Groups[1].Value;
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    return raw
                        .Replace("\\\"", "\"", StringComparison.Ordinal)
                        .Replace("\\n", "\n", StringComparison.Ordinal)
                        .Replace("\\t", " ", StringComparison.Ordinal)
                        .Replace("\\\\", "\\", StringComparison.Ordinal);
                }
            }
        }
        return null;
    }

    private static string? FindFirstString(JsonElement obj, string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String)
            {
                var str = val.GetString();
                if (!string.IsNullOrWhiteSpace(str))
                {
                    return str;
                }
            }
        }
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                var nested = FindFirstString(prop.Value, keys);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        return null;
    }

    private static string ExtractFileNameOrTail(string rawPath)
    {
        var normalized = rawPath.Replace('\\', '/').Trim().TrimEnd('/');
        if (normalized.Length == 0)
        {
            return rawPath;
        }
        var idx = normalized.LastIndexOf('/');
        var tail = idx >= 0 && idx + 1 < normalized.Length ? normalized[(idx + 1)..] : normalized;
        return ToSingleLine(tail, 44);
    }

    private static string ToSingleLine(string raw, int maxLen)
    {
        var sb = new StringBuilder(Math.Min(raw.Length, maxLen + 1));
        var lastSpace = false;
        foreach (var ch in raw.AsSpan().Trim())
        {
            if (ch is '\r' or '\n' or '\t' or ' ')
            {
                if (!lastSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    lastSpace = true;
                }
                continue;
            }
            lastSpace = false;
            sb.Append(ch);
            if (sb.Length >= maxLen)
            {
                sb.Append('…');
                break;
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string PrettyJson(JsonElement element)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, IndentedJsonOptions))
        {
            element.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }
}
