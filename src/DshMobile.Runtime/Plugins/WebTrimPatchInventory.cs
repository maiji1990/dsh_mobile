using System.Collections.Frozen;
using System.Text;
using System.Text.RegularExpressions;
using DshMobile.Runtime.Guest;

namespace DshMobile.Runtime.Plugins;

/// <summary>
/// 集中解析内嵌资产 <c>Guest/Assets/dsh-web-trim-patch.yml</c> 中被强制标记为 <c>disabled: true</c> 的
/// Cordis 插件入口 ID 集合（ADR-0034）。
/// 与 <see cref="GuestAssetProvisioner.ReadWebTrimPatch"/> 同源，避免在 Runtime 与 UI 侧硬编码列表导致漂移；
/// 当宿主处于精简原生模式（<c>TrimmedWeb == true</c>）时，上层据此识别被 <c>--patch</c> 强覆盖禁用的内置行。
/// </summary>
public static partial class WebTrimPatchInventory
{
    private static readonly Lazy<IReadOnlySet<string>> CachedTrimmedEntryIds = new(
        LoadEmbeddedTrimmedEntryIds,
        LazyThreadSafetyMode.ExecutionAndPublication);

    [GeneratedRegex(@"^\s*-\s*id:\s*(\S+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex EntryIdLineRegex();

    [GeneratedRegex(@"^\s+disabled:\s*(true|false)\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DisabledLineRegex();

    /// <summary>精简模式下由 <c>dsh-web-trim-patch.yml</c> 强制禁用的全部 Cordis 入口 ID。</summary>
    public static IReadOnlySet<string> TrimmedEntryIds => CachedTrimmedEntryIds.Value;

    /// <summary>判断指定插件入口 ID（支持带或不带 <c>include:</c> 前缀）是否属于精简模式强制禁用行。</summary>
    public static bool IsTrimmedEntry(string? entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId))
        {
            return false;
        }
        var trimmed = entryId.Trim();
        if (TrimmedEntryIds.Contains(trimmed))
        {
            return true;
        }
        var patchId = PluginCommandRules.ExtractPatchId(trimmed);
        return TrimmedEntryIds.Contains(patchId);
    }

    private static IReadOnlySet<string> LoadEmbeddedTrimmedEntryIds()
    {
        var patchBytes = GuestAssetProvisioner.ReadWebTrimPatch();
        var yamlContent = Encoding.UTF8.GetString(patchBytes);
        return ParseDisabledEntryIds(yamlContent);
    }

    /// <summary>解析裁剪 patch YAML 文本，仅收集确切带有 <c>disabled: true</c> 的入口 ID。</summary>
    internal static IReadOnlySet<string> ParseDisabledEntryIds(string yamlContent)
    {
        ArgumentNullException.ThrowIfNull(yamlContent);

        var disabledIds = new HashSet<string>(StringComparer.Ordinal);
        string? currentEntryId = null;
        var currentDisabled = false;

        foreach (var rawLine in yamlContent.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmedStart = line.TrimStart();
            if (trimmedStart.Length == 0 || trimmedStart.StartsWith('#'))
            {
                continue;
            }

            var idMatch = EntryIdLineRegex().Match(line);
            if (idMatch.Success)
            {
                if (currentEntryId is not null && currentDisabled)
                {
                    disabledIds.Add(currentEntryId);
                }
                currentEntryId = idMatch.Groups[1].Value;
                currentDisabled = false;
                continue;
            }

            if (currentEntryId is null)
            {
                continue;
            }

            var disabledMatch = DisabledLineRegex().Match(line);
            if (disabledMatch.Success)
            {
                currentDisabled = string.Equals(disabledMatch.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
            }
        }

        if (currentEntryId is not null && currentDisabled)
        {
            disabledIds.Add(currentEntryId);
        }

        return disabledIds.ToFrozenSet(StringComparer.Ordinal);
    }
}
