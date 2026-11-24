namespace DshMobile.Agent.Presentation;

/// <summary>插件卡片可读标题、描述与去重包名结果（纠正上游 DisplayName 与模块名倒置）。</summary>
public sealed record FormattedPluginDisplay(
    string ShortTitle,
    string? Description,
    string Meta);

/// <summary>纯托管插件展示格式化器（无 Android 依赖，供列表适配器与单元测试复用）。</summary>
public static class PluginDisplayFormatter
{
    public static FormattedPluginDisplay Format(Domain.PluginEntryView entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var shortTitle = ExtractShortTitle(entry.ModuleName, entry.PackageName, entry.EntryId);
        var displayName = entry.DisplayName?.Trim() ?? string.Empty;
        string? description = null;
        if (displayName.Length > 0
            && !string.Equals(displayName, entry.ModuleName, StringComparison.Ordinal)
            && !string.Equals(displayName, entry.PackageName, StringComparison.Ordinal)
            && !string.Equals(displayName, entry.EntryId, StringComparison.Ordinal)
            && !string.Equals(displayName, shortTitle, StringComparison.Ordinal))
        {
            description = displayName;
        }

        var module = entry.ModuleName?.Trim() ?? string.Empty;
        var pkg = entry.PackageName?.Trim() ?? string.Empty;
        string meta;
        if (string.IsNullOrEmpty(pkg) || string.Equals(module, pkg, StringComparison.Ordinal))
        {
            meta = module.Length > 0 ? module : pkg;
        }
        else if (module.StartsWith(pkg + "/", StringComparison.Ordinal))
        {
            meta = module;
        }
        else if (module.Length == 0)
        {
            meta = pkg;
        }
        else
        {
            meta = $"{pkg} · {module}";
        }

        return new FormattedPluginDisplay(shortTitle, description, meta);
    }

    private static string ExtractShortTitle(string? moduleName, string? packageName, string? entryId)
    {
        var candidate = !string.IsNullOrWhiteSpace(moduleName)
            ? moduleName.Trim()
            : !string.IsNullOrWhiteSpace(packageName)
                ? packageName.Trim()
                : entryId?.Trim() ?? string.Empty;

        if (candidate.StartsWith('@'))
        {
            var slash = candidate.IndexOf('/');
            if (slash >= 0 && slash + 1 < candidate.Length)
            {
                candidate = candidate[(slash + 1)..];
            }
        }
        return candidate;
    }
}
