using System.Text.RegularExpressions;

namespace DshMobile.Agent.Domain;

/// <summary>
/// 插件 SDUI 扩展 RPC 端点白名单校验（Slice 1A）：
/// 仅放行 <c>namespace/method</c> 两段式、非核心保留命名空间的端点，
/// 任何空白/控制字符、<c>..</c> 路径穿越、<c>$</c> 开头保留命名空间或超长端点一律拒绝。
/// </summary>
public static partial class PluginRpcEndpointValidator
{
    /// <summary>端点字符串的最大长度（含分隔符）。</summary>
    public const int MaxEndpointLength = 128;

    /// <summary>单个命名空间或方法名的最大长度。</summary>
    public const int MaxSegmentLength = 64;

    /// <summary>核心保留命名空间（大小写不敏感），扩展通道绝不转发到这些端点。</summary>
    private static readonly HashSet<string> ReservedNamespaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "session",
        "commands",
        "credentials",
        "settings",
        "pluginInventory",
        "pluginManager",
        "workspace",
        "fs",
        "system",
        "relay",
        "ui",
        "remote",
        "connection",
        "webserver",
        "typert",
    };

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex EndpointSegmentRegex();

    /// <summary>
    /// 校验插件扩展 RPC 端点是否在白名单范围内：必须是两段式 <c>namespace/method</c>，
    /// 每段以字母或数字开头且只含字母、数字、下划线、点、连字符，且命名空间不属于核心保留集合。
    /// </summary>
    public static bool IsAllowedExtensionEndpoint(string? endpoint)
    {
        if (string.IsNullOrEmpty(endpoint) || endpoint.Length > MaxEndpointLength)
        {
            return false;
        }
        if (endpoint.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }
        foreach (var character in endpoint)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                return false;
            }
        }

        var segments = endpoint.Split('/');
        if (segments.Length != 2)
        {
            return false;
        }
        foreach (var segment in segments)
        {
            if (segment.Length == 0 ||
                segment.Length > MaxSegmentLength ||
                !EndpointSegmentRegex().IsMatch(segment))
            {
                return false;
            }
        }

        return !ReservedNamespaces.Contains(segments[0]);
    }
}
