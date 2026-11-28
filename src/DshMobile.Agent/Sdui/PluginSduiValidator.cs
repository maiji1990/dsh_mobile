using System.Text.Json;
using System.Text.RegularExpressions;
using DshMobile.Agent.Domain;

namespace DshMobile.Agent.Sdui;

/// <summary>SDUI 文档校验结果：失败时携带全部可展示的中文错误原因，绝不放行部分合法文档。</summary>
public sealed record SduiValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    /// <summary>校验通过的共享结果。</summary>
    public static SduiValidationResult Success { get; } = new(true, []);

    /// <summary>由错误列表构造失败结果；空列表仍视为失败，避免“零错误却拒绝”语义混乱。</summary>
    public static SduiValidationResult Failure(IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return new SduiValidationResult(false, errors.Count == 0 ? ["SDUI 文档校验失败。"] : errors);
    }
}

/// <summary>
/// 插件 SDUI 文档的安全校验器（Slice 1B）：在渲染与执行任何动作之前，
/// 对版本、包名、节点数量、RPC 端点白名单、绑定路径、模板生成器、外链协议与刷新间隔做 fail-closed 校验。
/// </summary>
public static partial class PluginSduiValidator
{
    /// <summary>当前协议版本。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>允许的标签页数量上限。</summary>
    public const int MaxTabs = 8;

    /// <summary>整份文档允许的区块总数上限。</summary>
    public const int MaxBlocksPerDocument = 64;

    /// <summary>允许的数据源数量上限。</summary>
    public const int MaxSources = 16;

    /// <summary>允许的动作数量上限。</summary>
    public const int MaxActions = 32;

    /// <summary>自动刷新间隔下限（秒），防止把插件 RPC 当作高频轮询通道。</summary>
    public const int MinRefreshIntervalSec = 15;

    /// <summary>绑定路径根命名空间白名单。</summary>
    private static readonly HashSet<string> AllowedBindRoots = new(StringComparer.Ordinal)
    {
        "sources",
        "ui",
        "item",
        "form",
        "result",
    };

    private static readonly HashSet<string> AllowedBlockTypes = new(StringComparer.Ordinal)
    {
        SduiBlockTypes.StatusBanner,
        SduiBlockTypes.MetricGrid,
        SduiBlockTypes.QuotaBar,
        SduiBlockTypes.KeyValueList,
        SduiBlockTypes.SegmentedSwitch,
        SduiBlockTypes.ListSection,
        SduiBlockTypes.ActionBar,
        SduiBlockTypes.ExternalLink,
        SduiBlockTypes.SettingsBridge,
    };

    private static readonly HashSet<string> AllowedActionKinds = new(StringComparer.Ordinal)
    {
        SduiActionKinds.Rpc,
        SduiActionKinds.SettingsMutate,
        SduiActionKinds.OpenForm,
        SduiActionKinds.OpenUrl,
        SduiActionKinds.RefreshSources,
        SduiActionKinds.SetUiState,
    };

    private static readonly HashSet<string> AllowedFieldTypes = new(StringComparer.Ordinal)
    {
        SduiFormFieldTypes.Text,
        SduiFormFieldTypes.SecretInput,
        SduiFormFieldTypes.Password,
        SduiFormFieldTypes.Number,
        SduiFormFieldTypes.Select,
        SduiFormFieldTypes.Bool,
    };

    private static readonly HashSet<string> AllowedGeneratorKinds = new(StringComparer.Ordinal)
    {
        "hex32",
        "uuid",
    };

    [GeneratedRegex(@"^(sources|ui|item|form|result)(\.[A-Za-z0-9_-]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex BindPathRegex();

    /// <summary>
    /// 校验整份 SDUI 文档。返回全部失败原因，便于诊断页一次性展示；任何一条失败即 <see cref="SduiValidationResult.IsValid"/> = false。
    /// </summary>
    public static SduiValidationResult Validate(PluginSduiDocument? document)
    {
        var errors = new List<string>();
        if (document is null)
        {
            return SduiValidationResult.Failure(["SDUI 文档内容为空。"]);
        }

        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            errors.Add($"schemaVersion 必须为 {CurrentSchemaVersion}，实际为 {document.SchemaVersion}。");
        }

        if (!PluginPackageValidator.IsValidPackageName(document.PluginPackage))
        {
            errors.Add("pluginPackage 不是合法的 npm 包名。");
        }

        if (string.IsNullOrWhiteSpace(document.Title))
        {
            errors.Add("title 不能为空。");
        }

        var tabs = document.Tabs ?? [];
        if (tabs.Count is < 1 or > MaxTabs)
        {
            errors.Add($"tabs 数量必须为 1..{MaxTabs}，实际为 {tabs.Count}。");
        }

        var sources = document.Sources ?? [];
        if (sources.Count > MaxSources)
        {
            errors.Add($"sources 数量不能超过 {MaxSources}，实际为 {sources.Count}。");
        }

        var actions = document.Actions ?? [];
        if (actions.Count > MaxActions)
        {
            errors.Add($"actions 数量不能超过 {MaxActions}，实际为 {actions.Count}。");
        }

        ValidateSources(sources, errors);
        ValidateActions(actions, errors);
        ValidateTabs(tabs, errors);

        return errors.Count == 0 ? SduiValidationResult.Success : SduiValidationResult.Failure(errors);
    }

    private static void ValidateSources(IReadOnlyList<SduiDataSource> sources, List<string> errors)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            var location = $"sources[{index}]";
            if (source is null)
            {
                errors.Add($"{location} 为空。");
                continue;
            }

            if (string.IsNullOrWhiteSpace(source.Id))
            {
                errors.Add($"{location}.id 不能为空。");
            }
            else if (!seenIds.Add(source.Id))
            {
                errors.Add($"{location}.id 重复：{source.Id}。");
            }

            switch (source.Kind)
            {
                case SduiDataSourceKinds.Rpc:
                    if (!PluginRpcEndpointValidator.IsAllowedExtensionEndpoint(source.Endpoint))
                    {
                        errors.Add($"{location}.endpoint 不是允许的插件扩展 RPC 端点：{source.Endpoint}。");
                    }
                    break;

                case SduiDataSourceKinds.Settings:
                    if (string.IsNullOrWhiteSpace(source.Namespace))
                    {
                        errors.Add($"{location}.namespace 在 settings 数据源中不能为空。");
                    }
                    break;

                default:
                    errors.Add($"{location}.kind 非法：{source.Kind}。");
                    break;
            }

            if (source.RefreshIntervalSec is { } interval && interval < MinRefreshIntervalSec)
            {
                errors.Add($"{location}.refreshIntervalSec 必须大于等于 {MinRefreshIntervalSec}，实际为 {interval}。");
            }

            ValidateTemplate(source.ArgsTemplate, $"{location}.argsTemplate", errors);
        }
    }

    private static void ValidateActions(IReadOnlyList<SduiActionSpec> actions, List<string> errors)
    {
        for (var index = 0; index < actions.Count; index++)
        {
            var action = actions[index];
            var location = $"actions[{index}]";
            if (action is null)
            {
                errors.Add($"{location} 为空。");
                continue;
            }

            if (string.IsNullOrWhiteSpace(action.Id))
            {
                errors.Add($"{location}.id 不能为空。");
            }

            if (!AllowedActionKinds.Contains(action.Kind))
            {
                errors.Add($"{location}.kind 非法：{action.Kind}。");
                continue;
            }

            if (action.Kind == SduiActionKinds.Rpc &&
                !PluginRpcEndpointValidator.IsAllowedExtensionEndpoint(action.Endpoint))
            {
                errors.Add($"{location}.endpoint 不是允许的插件扩展 RPC 端点：{action.Endpoint}。");
            }

            if (action.Kind == SduiActionKinds.OpenUrl && !IsAllowedHttpsUrl(action.Url))
            {
                errors.Add($"{location}.url 必须为合法的绝对 https:// URI。");
            }

            if (action.Kind == SduiActionKinds.OpenForm)
            {
                ValidateFormFields(action, location, errors);
            }

            if (!string.IsNullOrWhiteSpace(action.ToastResultBind))
            {
                ValidateBindPath(action.ToastResultBind, $"{location}.toastResultBind", errors);
            }

            ValidateTemplate(action.PayloadTemplate, $"{location}.payloadTemplate", errors);
        }
    }

    private static void ValidateFormFields(SduiActionSpec action, string location, List<string> errors)
    {
        var fields = action.Fields ?? [];
        if (fields.Count == 0)
        {
            errors.Add($"{location}.fields 在 open_form 动作中不能为空。");
            return;
        }

        for (var index = 0; index < fields.Count; index++)
        {
            var field = fields[index];
            var fieldLocation = $"{location}.fields[{index}]";
            if (field is null)
            {
                errors.Add($"{fieldLocation} 为空。");
                continue;
            }

            if (string.IsNullOrWhiteSpace(field.Key))
            {
                errors.Add($"{fieldLocation}.key 不能为空。");
            }
            if (string.IsNullOrWhiteSpace(field.Label))
            {
                errors.Add($"{fieldLocation}.label 不能为空。");
            }
            if (!AllowedFieldTypes.Contains(field.Type))
            {
                errors.Add($"{fieldLocation}.type 非法：{field.Type}。");
            }
            if (field.Type == SduiFormFieldTypes.Select && (field.Options is null || field.Options.Count == 0))
            {
                errors.Add($"{fieldLocation}.options 在 select 字段中不能为空。");
            }
            if (field.MaxLength is <= 0)
            {
                errors.Add($"{fieldLocation}.maxLength 必须为正数。");
            }
        }
    }

    private static void ValidateTabs(IReadOnlyList<SduiTabSpec> tabs, List<string> errors)
    {
        var seenTabIds = new HashSet<string>(StringComparer.Ordinal);
        var totalBlocks = 0;
        for (var tabIndex = 0; tabIndex < tabs.Count; tabIndex++)
        {
            var tab = tabs[tabIndex];
            var location = $"tabs[{tabIndex}]";
            if (tab is null)
            {
                errors.Add($"{location} 为空。");
                continue;
            }

            if (string.IsNullOrWhiteSpace(tab.Id))
            {
                errors.Add($"{location}.id 不能为空。");
            }
            else if (!seenTabIds.Add(tab.Id))
            {
                errors.Add($"{location}.id 重复：{tab.Id}。");
            }

            if (string.IsNullOrWhiteSpace(tab.Label))
            {
                errors.Add($"{location}.label 不能为空。");
            }

            var blocks = tab.Blocks ?? [];
            totalBlocks += blocks.Count;
            for (var blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
            {
                ValidateBlock(blocks[blockIndex], $"{location}.blocks[{blockIndex}]", errors);
            }
        }

        if (totalBlocks > MaxBlocksPerDocument)
        {
            errors.Add($"区块总数不能超过 {MaxBlocksPerDocument}，实际为 {totalBlocks}。");
        }
    }

    private static void ValidateBlock(SduiBlockSpec? block, string location, List<string> errors)
    {
        if (block is null)
        {
            errors.Add($"{location} 为空。");
            return;
        }

        if (!AllowedBlockTypes.Contains(block.Type))
        {
            errors.Add($"{location}.type 非法：{block.Type}。");
            return;
        }

        ValidateOptionalBind(block.TitleBind, $"{location}.titleBind", errors);
        ValidateOptionalBind(block.SubtitleBind, $"{location}.subtitleBind", errors);
        ValidateOptionalBind(block.PercentBind, $"{location}.percentBind", errors);
        ValidateOptionalBind(block.StatusBind, $"{location}.statusBind", errors);
        ValidateOptionalBind(block.ResetAtBind, $"{location}.resetAtBind", errors);
        ValidateOptionalBind(block.ItemsBind, $"{location}.itemsBind", errors);
        ValidateOptionalBind(block.ItemTitleBind, $"{location}.itemTitleBind", errors);
        ValidateOptionalBind(block.ItemSubtitleBind, $"{location}.itemSubtitleBind", errors);
        ValidateOptionalBind(block.ItemBadgeBind, $"{location}.itemBadgeBind", errors);
        ValidateOptionalBind(block.ItemProgressBind, $"{location}.itemProgressBind", errors);

        var items = block.Items ?? [];
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var itemLocation = $"{location}.items[{index}]";
            if (item is null)
            {
                errors.Add($"{itemLocation} 为空。");
                continue;
            }
            if (string.IsNullOrWhiteSpace(item.Label))
            {
                errors.Add($"{itemLocation}.label 不能为空。");
            }
            ValidateBindPath(item.ValueBind, $"{itemLocation}.valueBind", errors);
        }

        switch (block.Type)
        {
            case SduiBlockTypes.MetricGrid:
            case SduiBlockTypes.KeyValueList:
                if (items.Count == 0)
                {
                    errors.Add($"{location}.items 在 {block.Type} 区块中不能为空。");
                }
                break;

            case SduiBlockTypes.ListSection:
                ValidateBindPath(block.ItemsBind, $"{location}.itemsBind", errors);
                break;

            case SduiBlockTypes.SegmentedSwitch:
                if (string.IsNullOrWhiteSpace(block.StateKey))
                {
                    errors.Add($"{location}.stateKey 在 segmented_switch 区块中不能为空。");
                }
                if (block.Options is not { Count: >= 2 })
                {
                    errors.Add($"{location}.options 在 segmented_switch 区块中至少需要 2 项。");
                }
                break;

            case SduiBlockTypes.ExternalLink:
                if (!IsAllowedHttpsUrl(block.Url))
                {
                    errors.Add($"{location}.url 必须为合法的绝对 https:// URI。");
                }
                break;

            case SduiBlockTypes.ActionBar:
                if (block.Actions is not { Count: > 0 })
                {
                    errors.Add($"{location}.actions 在 action_bar 区块中不能为空。");
                }
                break;
        }

        ValidateButtons(block.Actions, $"{location}.actions", errors);
        ValidateButtons(block.ItemActions, $"{location}.itemActions", errors);

        if (block.VisibleWhen is { } condition)
        {
            ValidateBindPath(condition.Bind, $"{location}.visibleWhen.bind", errors);
        }
    }

    private static void ValidateButtons(IReadOnlyList<SduiButtonItem>? buttons, string location, List<string> errors)
    {
        if (buttons is null)
        {
            return;
        }

        for (var index = 0; index < buttons.Count; index++)
        {
            var button = buttons[index];
            var buttonLocation = $"{location}[{index}]";
            if (button is null)
            {
                errors.Add($"{buttonLocation} 为空。");
                continue;
            }
            if (string.IsNullOrWhiteSpace(button.Label))
            {
                errors.Add($"{buttonLocation}.label 不能为空。");
            }
            if (string.IsNullOrWhiteSpace(button.ActionId))
            {
                errors.Add($"{buttonLocation}.actionId 不能为空。");
            }
        }
    }

    private static void ValidateOptionalBind(string? bindPath, string location, List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(bindPath))
        {
            ValidateBindPath(bindPath, location, errors);
        }
    }

    private static void ValidateBindPath(string? bindPath, string location, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(bindPath))
        {
            errors.Add($"{location} 绑定路径不能为空。");
            return;
        }

        if (!BindPathRegex().IsMatch(bindPath) || !AllowedBindRoots.Contains(GetBindRoot(bindPath)))
        {
            errors.Add($"{location} 绑定路径非法：{bindPath}。");
        }
    }

    private static string GetBindRoot(string bindPath)
    {
        var firstSeparator = bindPath.IndexOf('.', StringComparison.Ordinal);
        return firstSeparator < 0 ? bindPath : bindPath[..firstSeparator];
    }

    private static void ValidateTemplate(JsonElement? template, string location, List<string> errors)
    {
        if (template is not { } element || element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return;
        }

        ValidateTemplateNode(element, location, errors);
    }

    private static void ValidateTemplateNode(JsonElement node, string location, List<string> errors)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                if (node.TryGetProperty("$bind", out var bindNode))
                {
                    if (bindNode.ValueKind != JsonValueKind.String ||
                        !BindPathRegex().IsMatch(bindNode.GetString() ?? string.Empty))
                    {
                        errors.Add($"{location}.$bind 绑定路径非法。");
                    }
                }

                if (node.TryGetProperty("$gen", out var generatorNode))
                {
                    var generator = generatorNode.ValueKind == JsonValueKind.String
                        ? generatorNode.GetString()
                        : null;
                    if (generator is null || !AllowedGeneratorKinds.Contains(generator))
                    {
                        errors.Add($"{location}.$gen 只允许 hex32 或 uuid，实际为 {generatorNode.GetRawText()}。");
                    }
                }

                foreach (var property in node.EnumerateObject())
                {
                    if (property.NameEquals("$bind") || property.NameEquals("$gen"))
                    {
                        continue;
                    }
                    ValidateTemplateNode(property.Value, $"{location}.{property.Name}", errors);
                }
                break;

            case JsonValueKind.Array:
                var itemIndex = 0;
                foreach (var item in node.EnumerateArray())
                {
                    ValidateTemplateNode(item, $"{location}[{itemIndex}]", errors);
                    itemIndex++;
                }
                break;

            default:
                break;
        }
    }

    /// <summary>校验是否为不含用户信息、主机非空的绝对 https URI。</summary>
    internal static bool IsAllowedHttpsUrl(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && !string.IsNullOrEmpty(uri.Host);
    }
}
