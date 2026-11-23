using System.Collections.Frozen;
using System.Text.Json;
using DshMobile.Core.Files;

namespace DshMobile.Agent.Presentation;

/// <summary>
/// 从工具调用参数中安全提取受控工作区相对路径（N-06-02）：
/// 仅允许位于 <c>/root/projects/</c> 内或合法工作区相对路径的文件，严格拒绝 <c>.dsh/</c>、目录遍历与工作区外绝对路径。
/// </summary>
public static class WorkspacePathExtractor
{
    private const string GuestWorkspacePrefix = "/root/projects/";

    private static readonly string[] CandidateKeys =
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

    private static readonly FrozenSet<string> DiffPreferredTools = FrozenSet.ToFrozenSet(
        [
            "edit",
            "write",
            "fs_write",
            "write_file",
            "replace_file_content",
            "multi_replace_file_content",
            "apply_patch",
            "patch",
            "str_replace",
            "create_file",
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>判断工具是否为文件写入/修改类（优先提供 Git Diff 跳转提示）。</summary>
    public static bool IsWriteLikeTool(string? toolName) =>
        !string.IsNullOrWhiteSpace(toolName) && DiffPreferredTools.Contains(toolName.Trim());

    /// <summary>尝试从工具参数 JSON 中提取合法且不受保护的工作区相对文件路径，失败返回 null。</summary>
    public static string? TryExtractWorkspaceFilePath(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            return TryExtractFromObject(doc.RootElement, depth: 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryExtractFromObject(JsonElement obj, int depth)
    {
        foreach (var key in CandidateKeys)
        {
            if (obj.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String)
            {
                if (TryNormalizeWorkspaceFile(val.GetString(), out var normalized))
                {
                    return normalized;
                }
            }
        }

        if (depth < 1)
        {
            foreach (var prop in obj.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    var nested = TryExtractFromObject(prop.Value, depth + 1);
                    if (nested is not null)
                    {
                        return nested;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>将潜在的 guest 路径或相对路径规范化为工作区相对路径，拒绝越界与 <c>.dsh/</c>。</summary>
    public static bool TryNormalizeWorkspaceFile(string? rawCandidate, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(rawCandidate))
        {
            return false;
        }

        var trimmed = rawCandidate.Trim().Replace('\\', '/');
        if (trimmed.StartsWith(GuestWorkspacePrefix, StringComparison.Ordinal))
        {
            trimmed = trimmed[GuestWorkspacePrefix.Length..];
        }
        else if (string.Equals(trimmed, "/root/projects", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var norm = WorkspaceFileSystem.NormalizeRelative(trimmed);
            if (norm.Length == 0 || WorkspaceFileSystem.IsProtected(norm))
            {
                return false;
            }
            normalizedPath = norm;
            return true;
        }
        catch (WorkspaceException)
        {
            return false;
        }
    }
}
