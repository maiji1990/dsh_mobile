using System.Text;
using System.Text.RegularExpressions;

namespace DshMobile.Runtime.Plugins;

/// <summary>
/// <c>profiles/web/cordis.patch.yml</c> 受控启停补丁编辑器（PR-07, N-07-01）：
/// 仅在现有 YAML 序列中更新或追加目标 <c>id</c> 的 <c>disabled</c> 布尔开关，
/// 原子落盘（临时文件 + rename）并保留其他条目、注释与 <c>!!js</c> 表达式不被破坏。
/// </summary>
public static partial class PluginPatchEditor
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    [GeneratedRegex(@"^-\s+id:\s*(.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex EntryIdLineRegex();

    [GeneratedRegex(@"^(\s+)disabled:\s*.*$", RegexOptions.CultureInvariant)]
    private static partial Regex DisabledLineRegex();

    /// <summary>原子更新目标补丁文件中指定 <paramref name="patchId"/> 的 <c>disabled</c> 状态。</summary>
    public static (bool Ok, string Code, string? Diagnostic) SetPluginDisabled(string patchFilePath, string patchId, bool disabled)
    {
        ArgumentException.ThrowIfNullOrEmpty(patchFilePath);
        if (!PluginCommandRules.IsValidEntryId(patchId))
        {
            return (false, RuntimeErrorCodes.BadRequest, "invalid plugin patch id");
        }

        try
        {
            var raw = File.Exists(patchFilePath)
                ? File.ReadAllText(patchFilePath, Encoding.UTF8)
                : string.Empty;

            if (!TryRewriteSequence(raw, patchId, disabled, out var updated, out var error))
            {
                return (false, RuntimeErrorCodes.BadRequest, error);
            }

            var dir = Path.GetDirectoryName(patchFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var tmp = patchFilePath + ".tmp";
            File.WriteAllText(tmp, updated, Utf8NoBom);
            File.Move(tmp, patchFilePath, overwrite: true);
            return (true, RuntimeErrorCodes.Ok, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, RuntimeErrorCodes.Upstream, $"failed to update profile patch: {ex.Message}");
        }
    }

    internal static bool TryRewriteSequence(string raw, string patchId, bool disabled, out string updated, out string? error)
    {
        error = null;
        var disabledLiteral = disabled ? "true" : "false";
        var normalized = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var trimmed = normalized.Trim();

        if (string.IsNullOrEmpty(trimmed) || trimmed == "[]")
        {
            updated = $"- id: {FormatScalar(patchId)}\n  disabled: {disabledLiteral}\n";
            return true;
        }

        // 仅允许顶级 YAML 序列格式，遇到映射对象等非序列配置时拒绝覆盖，防止破坏用户自定义文件。
        var lines = normalized.Split('\n').ToList();
        var hasSequenceItem = false;
        var lastMatchStart = -1;
        var lastMatchEnd = -1;
        var matchHasInsert = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var trimmedLine = line.Trim();
            if (trimmedLine.Length == 0 || trimmedLine.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('-'))
            {
                hasSequenceItem = true;
                var m = EntryIdLineRegex().Match(line);
                if (m.Success && UnquoteScalar(m.Groups[1].Value) == patchId)
                {
                    var end = i + 1;
                    var hasInsert = false;
                    while (end < lines.Count)
                    {
                        var next = lines[end];
                        if (next.Length == 0 || next.TrimStart().StartsWith('#'))
                        {
                            end++;
                            continue;
                        }
                        if (!char.IsWhiteSpace(next[0]))
                        {
                            break;
                        }
                        if (next.TrimStart().StartsWith("insert:", StringComparison.Ordinal))
                        {
                            hasInsert = true;
                        }
                        end++;
                    }
                    lastMatchStart = i;
                    lastMatchEnd = end;
                    matchHasInsert = hasInsert;
                }
                continue;
            }

            if (!char.IsWhiteSpace(line[0]))
            {
                updated = string.Empty;
                error = "profile cordis.patch.yml is not a YAML sequence";
                return false;
            }
        }

        if (!hasSequenceItem)
        {
            // 全是注释的文件，直接追加序列项。
            var prefix = normalized.EndsWith('\n') ? normalized : normalized + "\n";
            updated = $"{prefix}- id: {FormatScalar(patchId)}\n  disabled: {disabledLiteral}\n";
            return true;
        }

        if (lastMatchStart >= 0 && !matchHasInsert)
        {
            var disabledIdx = -1;
            for (var j = lastMatchStart + 1; j < lastMatchEnd; j++)
            {
                if (DisabledLineRegex().IsMatch(lines[j]))
                {
                    disabledIdx = j;
                    break;
                }
            }
            if (disabledIdx >= 0)
            {
                lines[disabledIdx] = $"  disabled: {disabledLiteral}";
            }
            else
            {
                lines.Insert(lastMatchStart + 1, $"  disabled: {disabledLiteral}");
            }
        }
        else
        {
            while (lines.Count > 0 && string.IsNullOrEmpty(lines[^1]))
            {
                lines.RemoveAt(lines.Count - 1);
            }
            lines.Add($"- id: {FormatScalar(patchId)}");
            lines.Add($"  disabled: {disabledLiteral}");
        }

        updated = string.Join('\n', lines).TrimEnd('\n') + "\n";
        return true;
    }

    private static string UnquoteScalar(string raw)
    {
        var s = raw.Trim();
        if (s.Length >= 2 &&
            ((s[0] == '\'' && s[^1] == '\'') || (s[0] == '"' && s[^1] == '"')))
        {
            return s[1..^1];
        }
        return s;
    }

    private static string FormatScalar(string value) =>
        value.Contains(':') || value.Contains('#') || value.StartsWith('@')
            ? $"'{value.Replace("'", "''", StringComparison.Ordinal)}'"
            : value;
}
