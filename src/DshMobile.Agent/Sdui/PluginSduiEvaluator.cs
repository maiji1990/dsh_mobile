using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace DshMobile.Agent.Sdui;

/// <summary>
/// SDUI 求值上下文：把五类只读数据根暴露给绑定路径求值器。
/// 所有根均为 <see cref="JsonElement"/>，生命周期由调用方持有的 <see cref="JsonDocument"/> 保证。
/// </summary>
public sealed class SduiEvalContext
{
    /// <summary><c>sources.*</c> 根：各数据源最近一次成功结果。</summary>
    public JsonElement? Sources { get; init; }

    /// <summary><c>ui.*</c> 根：页面本地 UI 状态。</summary>
    public JsonElement? UiState { get; init; }

    /// <summary><c>item.*</c> 根：列表单项作用域。</summary>
    public JsonElement? Item { get; init; }

    /// <summary><c>form.*</c> 根：表单当前值。</summary>
    public JsonElement? Form { get; init; }

    /// <summary><c>result.*</c> 根：最近一次动作返回值。</summary>
    public JsonElement? Result { get; init; }
}

/// <summary>
/// SDUI 安全求值器（Slice 1B）：只读点分路径求值、条件判断、受限格式化与模板物化。
/// 不执行任何脚本、反射或格式化字符串插值；任何越界、类型不匹配或无效模板都安静地退化为空值。
/// </summary>
public static class PluginSduiEvaluator
{
    private const decimal MicroCentsPerUsd = 100_000_000m;

    // DateTimeOffset.FromUnixTimeSeconds / FromUnixTimeMilliseconds 的合法闭区间（与 .NET 实现一致）。
    private const long MinUnixSeconds = -62_135_596_800L;
    private const long MaxUnixSeconds = 253_402_300_799L;
    private const long MinUnixMilliseconds = -62_135_596_800_000L;
    private const long MaxUnixMilliseconds = 253_402_300_799_999L;

    // JavaScript Date.now() 返回 13 位毫秒；达到该阈值即按毫秒解析，否则按秒。
    private const long MillisecondsThreshold = 100_000_000_000L;

    /// <summary>
    /// 安全解析点分绑定路径（如 <c>sources.usage.items.0.limit</c>），支持数组数字索引与 <c>.length</c>。
    /// 路径非法、根缺失或中间节点不存在时返回 <see langword="null"/>。
    /// </summary>
    public static JsonElement? ResolvePath(string? bindPath, SduiEvalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(bindPath))
        {
            return null;
        }

        var segments = bindPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        var current = ResolveRoot(segments[0], context);
        for (var index = 1; index < segments.Length; index++)
        {
            if (current is not { } element)
            {
                return null;
            }
            current = ResolveSegment(element, segments[index]);
        }

        return current;
    }

    /// <summary>
    /// 判断可见性条件：条件为空视为可见；否则按绑定取值做相等或非空判断，取不到值一律不可见。
    /// </summary>
    public static bool EvaluateCondition(SduiCondition? condition, SduiEvalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (condition is null)
        {
            return true;
        }

        if (ResolvePath(condition.Bind, context) is not { } actual ||
            actual.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        if (condition.NotEmpty)
        {
            return HasContent(actual);
        }

        if (condition.EqualsValue is { } expected)
        {
            return ValuesEqual(actual, expected);
        }

        return HasContent(actual);
    }

    /// <summary>
    /// 按受控格式把绑定取值渲染为展示文本：<c>plain</c>、<c>number</c>、<c>microCentsUsd</c>（1 USD = 100_000_000 微美分）、
    /// <c>relativeTime</c>。无法识别时退化为原文，绝不抛出。
    /// </summary>
    public static string FormatValue(
        JsonElement? value,
        string? format = null,
        string? unit = null,
        DateTimeOffset? now = null)
    {
        if (value is not { } actual || actual.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return string.Empty;
        }

        var normalizedFormat = string.IsNullOrWhiteSpace(format) ? SduiValueFormats.Plain : format.Trim();
        var text = normalizedFormat switch
        {
            SduiValueFormats.Number => FormatNumber(actual),
            SduiValueFormats.MicroCentsUsd => FormatMicroCentsUsd(actual),
            SduiValueFormats.RelativeTime => FormatRelativeTime(actual, now ?? DateTimeOffset.UtcNow),
            _ => FormatPlain(actual),
        };

        if (text.Length > 0 && !string.IsNullOrWhiteSpace(unit))
        {
            return string.Concat(text, " ", unit.Trim());
        }

        return text;
    }

    /// <summary>
    /// 物化动作/数据源模板：递归展开 <c>$bind</c>（缺失时回退 <c>$default</c>）与 <c>$gen</c>（<c>hex32</c> / <c>uuid</c>），
    /// 结果通过 <see cref="Utf8JsonWriter"/> 直接构建，避免字符串拼接产生非法 JSON。
    /// </summary>
    public static JsonElement? MaterializeTemplate(
        JsonElement? template,
        SduiEvalContext context,
        Func<string>? hex32Factory = null,
        Func<string>? uuidFactory = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (template is not { } requested || requested.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteMaterialized(writer, requested, context, hex32Factory, uuidFactory);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static JsonElement? ResolveRoot(string root, SduiEvalContext context) => root switch
    {
        "sources" => context.Sources,
        "ui" => context.UiState,
        "item" => context.Item,
        "form" => context.Form,
        "result" => context.Result,
        _ => null,
    };

    private static JsonElement? ResolveSegment(JsonElement element, string segment)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty(segment, out var exact))
                {
                    return exact;
                }
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, segment, StringComparison.OrdinalIgnoreCase))
                    {
                        return property.Value;
                    }
                }
                return null;

            case JsonValueKind.Array:
                if (segment.Equals("length", StringComparison.Ordinal))
                {
                    return CreateNumberElement(element.GetArrayLength());
                }
                if (int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
                    index >= 0 && index < element.GetArrayLength())
                {
                    return element[index];
                }
                return null;

            case JsonValueKind.String:
                return segment.Equals("length", StringComparison.Ordinal)
                    ? CreateNumberElement(element.GetString()?.Length ?? 0)
                    : null;

            default:
                return null;
        }
    }

    private static JsonElement CreateNumberElement(int value)
    {
        using var document = JsonDocument.Parse(value.ToString(CultureInfo.InvariantCulture));
        return document.RootElement.Clone();
    }

    private static bool HasContent(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
        JsonValueKind.Array => value.GetArrayLength() > 0,
        JsonValueKind.Object => value.EnumerateObject().Any(),
        JsonValueKind.True => true,
        JsonValueKind.Number => true,
        _ => false,
    };

    private static bool ValuesEqual(JsonElement actual, JsonElement expected)
    {
        if (actual.ValueKind == JsonValueKind.String && expected.ValueKind == JsonValueKind.String)
        {
            return string.Equals(actual.GetString(), expected.GetString(), StringComparison.Ordinal);
        }

        if (IsNumber(actual) && IsNumber(expected) &&
            TryGetDecimal(actual, out var actualNumber) &&
            TryGetDecimal(expected, out var expectedNumber))
        {
            return actualNumber == expectedNumber;
        }

        if (actual.ValueKind == expected.ValueKind)
        {
            return actual.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null;
        }

        return string.Equals(actual.GetRawText(), expected.GetRawText(), StringComparison.Ordinal);
    }

    private static bool IsNumber(JsonElement value) => value.ValueKind == JsonValueKind.Number;

    private static string FormatPlain(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText(),
    };

    private static string FormatNumber(JsonElement value)
    {
        if (!TryGetDecimal(value, out var numeric))
        {
            return FormatPlain(value);
        }

        return numeric == decimal.Truncate(numeric)
            ? numeric.ToString("N0", CultureInfo.InvariantCulture)
            : numeric.ToString("N2", CultureInfo.InvariantCulture);
    }

    private static string FormatMicroCentsUsd(JsonElement value)
    {
        if (!TryGetDecimal(value, out var microCents))
        {
            return FormatPlain(value);
        }

        var dollars = microCents / MicroCentsPerUsd;
        var absolute = Math.Abs(dollars).ToString("0.00", CultureInfo.InvariantCulture);
        return dollars < 0 ? string.Concat("-$", absolute) : string.Concat("$", absolute);
    }

    private static string FormatRelativeTime(JsonElement value, DateTimeOffset now)
    {
        if (!TryGetTimestamp(value, out var target))
        {
            return FormatPlain(value);
        }

        var delta = target - now;
        var future = delta > TimeSpan.Zero;
        var absolute = delta.Duration();
        var suffix = future ? "后" : "前";

        if (absolute < TimeSpan.FromSeconds(60))
        {
            return "刚刚";
        }
        if (absolute < TimeSpan.FromHours(1))
        {
            return string.Concat((int)absolute.TotalMinutes, " 分钟", suffix);
        }
        if (absolute < TimeSpan.FromDays(1))
        {
            return string.Concat((int)absolute.TotalHours, " 小时", suffix);
        }
        if (absolute < TimeSpan.FromDays(30))
        {
            return string.Concat((int)absolute.TotalDays, " 天", suffix);
        }
        if (absolute < TimeSpan.FromDays(365))
        {
            return string.Concat((int)(absolute.TotalDays / 30), " 个月", suffix);
        }
        return string.Concat((int)(absolute.TotalDays / 365), " 年", suffix);
    }

    private static bool TryGetTimestamp(JsonElement value, out DateTimeOffset timestamp)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number when value.TryGetInt64(out var unixValue):
                return TryFromUnixInteger(unixValue, out timestamp);

            case JsonValueKind.Number when value.TryGetDouble(out var unixValueFraction):
                return TryFromUnixDouble(unixValueFraction, out timestamp);

            case JsonValueKind.String when DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed):
                timestamp = parsed;
                return true;

            default:
                timestamp = default;
                return false;
        }
    }

    private static bool TryFromUnixInteger(long unixValue, out DateTimeOffset timestamp)
    {
        // 不用 Math.Abs：long.MinValue 会抛 OverflowException；直接比较等价且安全。
        var isMilliseconds = unixValue <= -MillisecondsThreshold || unixValue >= MillisecondsThreshold;
        if (isMilliseconds)
        {
            return TryFromUnixMilliseconds(unixValue, out timestamp);
        }

        if (unixValue < MinUnixSeconds || unixValue > MaxUnixSeconds)
        {
            timestamp = default;
            return false;
        }

        timestamp = DateTimeOffset.FromUnixTimeSeconds(unixValue);
        return true;
    }

    private static bool TryFromUnixDouble(double unixValue, out DateTimeOffset timestamp)
    {
        if (!double.IsFinite(unixValue))
        {
            timestamp = default;
            return false;
        }

        var isMilliseconds = Math.Abs(unixValue) >= MillisecondsThreshold;
        if (isMilliseconds)
        {
            return TryFromUnixMilliseconds(unixValue, out timestamp);
        }

        if (unixValue < MinUnixSeconds || unixValue > MaxUnixSeconds)
        {
            timestamp = default;
            return false;
        }

        // 秒 * 1000 可能因浮点舍入越过边界，按毫秒再校验一次后换算，避免抛异常。
        return TryFromUnixMilliseconds(unixValue * 1000d, out timestamp);
    }

    private static bool TryFromUnixMilliseconds(double unixMilliseconds, out DateTimeOffset timestamp)
    {
        if (unixMilliseconds < MinUnixMilliseconds || unixMilliseconds > MaxUnixMilliseconds)
        {
            timestamp = default;
            return false;
        }

        timestamp = DateTimeOffset.FromUnixTimeMilliseconds((long)unixMilliseconds);
        return true;
    }

    private static bool TryGetDecimal(JsonElement value, out decimal result)
    {
        const NumberStyles styles = NumberStyles.Float | NumberStyles.AllowThousands;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return decimal.TryParse(value.GetRawText(), styles, CultureInfo.InvariantCulture, out result);

            case JsonValueKind.String:
                return decimal.TryParse(value.GetString(), styles, CultureInfo.InvariantCulture, out result);

            default:
                result = 0m;
                return false;
        }
    }

    private static void WriteMaterialized(
        Utf8JsonWriter writer,
        JsonElement element,
        SduiEvalContext context,
        Func<string>? hex32Factory,
        Func<string>? uuidFactory)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteMaterializedObject(writer, element, context, hex32Factory, uuidFactory);
                return;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteMaterialized(writer, item, context, hex32Factory, uuidFactory);
                }
                writer.WriteEndArray();
                return;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                return;

            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                return;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                return;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                return;

            default:
                writer.WriteNullValue();
                return;
        }
    }

    private static void WriteMaterializedObject(
        Utf8JsonWriter writer,
        JsonElement element,
        SduiEvalContext context,
        Func<string>? hex32Factory,
        Func<string>? uuidFactory)
    {
        if (element.TryGetProperty("$bind", out var bindNode) && bindNode.ValueKind == JsonValueKind.String)
        {
            var resolved = ResolvePath(bindNode.GetString(), context);
            if (resolved is { } found && found.ValueKind != JsonValueKind.Undefined)
            {
                WriteMaterialized(writer, found, context, hex32Factory, uuidFactory);
                return;
            }

            if (element.TryGetProperty("$default", out var defaultNode))
            {
                WriteMaterialized(writer, defaultNode, context, hex32Factory, uuidFactory);
                return;
            }

            writer.WriteNullValue();
            return;
        }

        if (element.TryGetProperty("$gen", out var generatorNode) && generatorNode.ValueKind == JsonValueKind.String)
        {
            writer.WriteStringValue(GenerateValue(generatorNode.GetString(), hex32Factory, uuidFactory));
            return;
        }

        writer.WriteStartObject();
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals("$bind") || property.NameEquals("$gen") || property.NameEquals("$default"))
            {
                continue;
            }

            writer.WritePropertyName(property.Name);
            WriteMaterialized(writer, property.Value, context, hex32Factory, uuidFactory);
        }
        writer.WriteEndObject();
    }

    private static string GenerateValue(string? generator, Func<string>? hex32Factory, Func<string>? uuidFactory) => generator switch
    {
        "hex32" => hex32Factory?.Invoke() ?? CreateHex32(),
        "uuid" => uuidFactory?.Invoke() ?? Guid.NewGuid().ToString("D"),
        _ => string.Empty,
    };

    private static string CreateHex32() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
