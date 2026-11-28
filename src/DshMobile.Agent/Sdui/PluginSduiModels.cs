using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshMobile.Agent.Sdui;

/// <summary>
/// 插件声明式 SDUI（服务端驱动界面）文档根模型（Slice 1B，ADR-0033 受控 L2）。
/// 文档来源于插件包内只读的 <c>dshmobile.sdui.json</c>，只描述原生控件的组合与数据绑定，
/// 绝不包含 HTML、脚本或任意布局指令。
/// </summary>
public sealed class PluginSduiDocument
{
    /// <summary>协议版本，当前只接受 1。</summary>
    public int SchemaVersion { get; init; }

    /// <summary>声明本界面的 npm 包名，必须与读取来源一致。</summary>
    public string PluginPackage { get; init; } = string.Empty;

    /// <summary>插件版本，用于缓存失效与来源核对。</summary>
    public string PluginVersion { get; init; } = string.Empty;

    /// <summary>页面标题。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>页面副标题，可为空。</summary>
    public string? Subtitle { get; init; }

    /// <summary>插件官方配置命名空间，用于 <c>settings</c> 数据源与 <c>settings_bridge</c> 区块。</summary>
    public string? SettingsNamespace { get; init; }

    /// <summary>数据源声明，最多 16 个。</summary>
    public IReadOnlyList<SduiDataSource> Sources { get; init; } = [];

    /// <summary>初始 UI 状态（纯数据，供 <c>ui.*</c> 绑定读取）。</summary>
    public JsonElement? InitialUiState { get; init; }

    /// <summary>标签页声明，数量必须为 1..8。</summary>
    public IReadOnlyList<SduiTabSpec> Tabs { get; init; } = [];

    /// <summary>可被区块引用的动作声明，最多 32 个。</summary>
    public IReadOnlyList<SduiActionSpec> Actions { get; init; } = [];
}

/// <summary>数据源声明：RPC 扩展端点或插件配置命名空间，两者互斥。</summary>
public sealed class SduiDataSource
{
    /// <summary>数据源 ID，在同级 <c>sources</c> 中唯一，供 <c>sources.&lt;id&gt;</c> 绑定使用。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>数据源类型：<c>rpc</c> 或 <c>settings</c>。</summary>
    public string Kind { get; init; } = SduiDataSourceKinds.Rpc;

    /// <summary>RPC 端点（<c>namespace/method</c> 两段式），仅在 <c>kind = rpc</c> 时使用。</summary>
    public string? Endpoint { get; init; }

    /// <summary>插件配置命名空间，仅在 <c>kind = settings</c> 时使用。</summary>
    public string? Namespace { get; init; }

    /// <summary>RPC 调用参数模板，支持 <c>$bind</c> 与 <c>$gen</c> 展开。</summary>
    public JsonElement? ArgsTemplate { get; init; }

    /// <summary>自动刷新间隔（秒），存在时必须大于等于 15。</summary>
    public int? RefreshIntervalSec { get; init; }

    /// <summary>页面首次展示时是否自动加载。</summary>
    public bool AutoLoad { get; init; } = true;
}

/// <summary>数据源类型常量。</summary>
public static class SduiDataSourceKinds
{
    public const string Rpc = "rpc";
    public const string Settings = "settings";
}

/// <summary>单个标签页及其区块列表。</summary>
public sealed class SduiTabSpec
{
    /// <summary>标签页 ID，在文档内唯一。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>标签页显示文本。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>该标签页下的区块，扁平强类型。</summary>
    public IReadOnlyList<SduiBlockSpec> Blocks { get; init; } = [];
}

/// <summary>
/// 扁平强类型区块：所有区块类型共用一个模型，由 <see cref="Type"/> 决定实际渲染的字段，
/// 未使用的字段保持为 <see langword="null"/>，避免为每种区块引入继承层级。
/// </summary>
public sealed class SduiBlockSpec
{
    /// <summary>区块类型，取值见 <see cref="SduiBlockTypes"/>。</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>语义色调（如 <c>info</c> / <c>warn</c> / <c>success</c> / <c>danger</c>）。</summary>
    public string? Tone { get; init; }

    /// <summary>静态标题。</summary>
    public string? Title { get; init; }

    /// <summary>标题绑定路径。</summary>
    public string? TitleBind { get; init; }

    /// <summary>静态副标题。</summary>
    public string? Subtitle { get; init; }

    /// <summary>副标题绑定路径。</summary>
    public string? SubtitleBind { get; init; }

    /// <summary>通用标签文本（如 <c>quota_bar</c> / <c>segmented_switch</c>）。</summary>
    public string? Label { get; init; }

    /// <summary>进度百分比绑定路径（0..100）。</summary>
    public string? PercentBind { get; init; }

    /// <summary>状态文本绑定路径。</summary>
    public string? StatusBind { get; init; }

    /// <summary>额度重置时间绑定路径。</summary>
    public string? ResetAtBind { get; init; }

    /// <summary>指标或键值对条目（<c>metric_grid</c> / <c>key_value_list</c>）。</summary>
    public IReadOnlyList<SduiMetricOrKvItem>? Items { get; init; }

    /// <summary>分段开关绑定的 UI 状态键。</summary>
    public string? StateKey { get; init; }

    /// <summary>分段开关候选项。</summary>
    public IReadOnlyList<SduiOptionItem>? Options { get; init; }

    /// <summary>分段开关变化时触发的动作 ID。</summary>
    public string? OnChangeActionId { get; init; }

    /// <summary>列表数据绑定路径（指向数组）。</summary>
    public string? ItemsBind { get; init; }

    /// <summary>列表为空时的提示文本。</summary>
    public string? EmptyText { get; init; }

    /// <summary>列表单项标题绑定路径（相对 <c>item.</c>）。</summary>
    public string? ItemTitleBind { get; init; }

    /// <summary>列表单项副标题绑定路径（相对 <c>item.</c>）。</summary>
    public string? ItemSubtitleBind { get; init; }

    /// <summary>列表单项徽标绑定路径（相对 <c>item.</c>）。</summary>
    public string? ItemBadgeBind { get; init; }

    /// <summary>列表单项进度绑定路径（相对 <c>item.</c>）。</summary>
    public string? ItemProgressBind { get; init; }

    /// <summary>列表单项附带的动作按钮。</summary>
    public IReadOnlyList<SduiButtonItem>? ItemActions { get; init; }

    /// <summary>区块内的动作按钮（<c>action_bar</c>）。</summary>
    public IReadOnlyList<SduiButtonItem>? Actions { get; init; }

    /// <summary>外部链接地址（<c>external_link</c>），必须为绝对 <c>https://</c> URI。</summary>
    public string? Url { get; init; }

    /// <summary>配置命名空间（<c>settings_bridge</c>）。</summary>
    public string? Namespace { get; init; }

    /// <summary>可见性条件，条件不成立时整个区块不渲染。</summary>
    public SduiCondition? VisibleWhen { get; init; }
}

/// <summary>区块类型常量（受控白名单）。</summary>
public static class SduiBlockTypes
{
    public const string StatusBanner = "status_banner";
    public const string MetricGrid = "metric_grid";
    public const string QuotaBar = "quota_bar";
    public const string KeyValueList = "key_value_list";
    public const string SegmentedSwitch = "segmented_switch";
    public const string ListSection = "list_section";
    public const string ActionBar = "action_bar";
    public const string ExternalLink = "external_link";
    public const string SettingsBridge = "settings_bridge";
}

/// <summary>指标网格 / 键值对列表中的单个条目。</summary>
public sealed class SduiMetricOrKvItem
{
    /// <summary>条目标签。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>取值绑定路径。</summary>
    public string ValueBind { get; init; } = string.Empty;

    /// <summary>展示单位，如 <c>USD</c>。</summary>
    public string? Unit { get; init; }

    /// <summary>取值格式化方式，取值见 <see cref="SduiValueFormats"/>。</summary>
    public string? Format { get; init; }

    /// <summary>是否使用等宽字体展示。</summary>
    public bool Monospace { get; init; }
}

/// <summary>取值格式化方式常量。</summary>
public static class SduiValueFormats
{
    public const string Plain = "plain";
    public const string Number = "number";
    public const string MicroCentsUsd = "microCentsUsd";
    public const string RelativeTime = "relativeTime";
}

/// <summary>单选 / 分段开关候选项。</summary>
public sealed class SduiOptionItem
{
    /// <summary>提交值。</summary>
    public string Value { get; init; } = string.Empty;

    /// <summary>显示文本。</summary>
    public string Label { get; init; } = string.Empty;
}

/// <summary>动作按钮。</summary>
public sealed class SduiButtonItem
{
    /// <summary>按钮显示文本。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>按钮样式（如 <c>primary</c> / <c>danger</c> / <c>text</c>）。</summary>
    public string? Style { get; init; }

    /// <summary>点击后触发的动作 ID。</summary>
    public string ActionId { get; init; } = string.Empty;

    /// <summary>二次确认标题。</summary>
    public string? ConfirmTitle { get; init; }

    /// <summary>二次确认正文。</summary>
    public string? ConfirmMessage { get; init; }

    /// <summary>是否要求二次确认。</summary>
    public bool Confirm { get; init; }
}

/// <summary>可见性条件：对绑定路径取值后做相等或非空判断。</summary>
public sealed class SduiCondition
{
    /// <summary>绑定路径。</summary>
    public string Bind { get; init; } = string.Empty;

    /// <summary>目标相等值；存在时优先按相等判断。</summary>
    public JsonElement? EqualsValue { get; init; }

    /// <summary>为真时只要求取值非空。</summary>
    public bool NotEmpty { get; init; }
}

/// <summary>动作声明：可由按钮、分段开关或表单提交引用。</summary>
public sealed class SduiActionSpec
{
    /// <summary>动作 ID，供区块引用。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>动作类型，取值见 <see cref="SduiActionKinds"/>。</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>RPC 端点（<c>kind = rpc</c>）。</summary>
    public string? Endpoint { get; init; }

    /// <summary>RPC 请求参数模板，支持 <c>$bind</c> 与 <c>$gen</c> 展开。</summary>
    public JsonElement? PayloadTemplate { get; init; }

    /// <summary>需要刷新的数据源 ID 列表。</summary>
    public IReadOnlyList<string>? Targets { get; init; }

    /// <summary>表单标题（<c>open_form</c>）。</summary>
    public string? Title { get; init; }

    /// <summary>表单字段（<c>open_form</c>）。</summary>
    public IReadOnlyList<SduiFormField>? Fields { get; init; }

    /// <summary>表单提交后执行的动作 ID（<c>open_form</c>）。</summary>
    public string? SubmitAction { get; init; }

    /// <summary>动作成功后需要刷新的数据源 ID 列表。</summary>
    public IReadOnlyList<string>? OnSuccessRefresh { get; init; }

    /// <summary>结果提示文本绑定路径（如 <c>result.message</c>）。</summary>
    public string? ToastResultBind { get; init; }

    /// <summary>外部链接地址（<c>open_url</c>），必须为绝对 <c>https://</c> URI。</summary>
    public string? Url { get; init; }

    /// <summary>需要写入的 UI 状态键（<c>set_ui_state</c>）。</summary>
    public string? StateKey { get; init; }

    /// <summary>需要写入的 UI 状态值（<c>set_ui_state</c>）。</summary>
    public JsonElement? StateValue { get; init; }
}

/// <summary>动作类型常量（受控白名单）。</summary>
public static class SduiActionKinds
{
    public const string Rpc = "rpc";
    public const string SettingsMutate = "settings_mutate";
    public const string OpenForm = "open_form";
    public const string OpenUrl = "open_url";
    public const string RefreshSources = "refresh_sources";
    public const string SetUiState = "set_ui_state";
}

/// <summary>表单字段声明（<c>open_form</c>）。</summary>
public sealed class SduiFormField
{
    /// <summary>字段键，提交时作为表单数据的属性名。</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>字段显示文本。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>字段类型，取值见 <see cref="SduiFormFieldTypes"/>。</summary>
    public string Type { get; init; } = SduiFormFieldTypes.Text;

    /// <summary>是否必填。</summary>
    public bool Required { get; init; }

    /// <summary>最大输入长度。</summary>
    public int? MaxLength { get; init; }

    /// <summary>下拉候选项（<c>select</c>）。</summary>
    public IReadOnlyList<SduiOptionItem>? Options { get; init; }

    /// <summary>默认值。</summary>
    public string? DefaultValue { get; init; }

    /// <summary>输入占位提示。</summary>
    public string? Placeholder { get; init; }

    /// <summary>是否为敏感字段（<c>secret_input</c> / <c>password</c>），展示时脱敏。</summary>
    [JsonIgnore]
    public bool IsSecret => Type is SduiFormFieldTypes.SecretInput or SduiFormFieldTypes.Password;
}

/// <summary>表单字段类型常量（受控白名单）。</summary>
public static class SduiFormFieldTypes
{
    public const string Text = "text";
    public const string SecretInput = "secret_input";
    public const string Password = "password";
    public const string Number = "number";
    public const string Select = "select";
    public const string Bool = "bool";
}
