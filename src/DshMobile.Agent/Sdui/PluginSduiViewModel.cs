using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;

namespace DshMobile.Agent.Sdui;

/// <summary>标签页候选项（纯数据，供 Android 原生 TabLayout / ChipGroup 绑定）。</summary>
public sealed record SduiTabOption(string Id, string Label);

/// <summary>
/// 扁平化、强类型的 SDUI 渲染项基类：Android <c>RecyclerView</c> 直接按 <see cref="ViewType"/>
/// 选择既有 ViewHolder 绑定，绝不在宿主侧动态造控件。
/// </summary>
public abstract record SduiRenderItem
{
    /// <summary>渲染项类型（与 SDUI 区块类型同名），供列表多类型适配器分发。</summary>
    public abstract string ViewType { get; }
}

/// <summary>状态横幅渲染项。</summary>
public sealed record SduiStatusBannerRenderItem(string Tone, string Title, string Subtitle) : SduiRenderItem
{
    public override string ViewType => SduiBlockTypes.StatusBanner;
}

/// <summary>指标/键值条目渲染数据。</summary>
public sealed record SduiMetricEntry(string Label, string ValueText, string? Unit, bool Monospace);

/// <summary>指标网格渲染项。</summary>
public sealed record SduiMetricGridRenderItem(IReadOnlyList<SduiMetricEntry> Items) : SduiRenderItem
{
    public override string ViewType => SduiBlockTypes.MetricGrid;
}

/// <summary>额度进度条渲染项，<see cref="Percent"/> 已归一到 0..100。</summary>
public sealed record SduiQuotaBarRenderItem(string Label, double Percent, string StatusText, string ResetAtText) : SduiRenderItem
{
    public override string ViewType => SduiBlockTypes.QuotaBar;
}

/// <summary>键值对列表渲染项。</summary>
public sealed record SduiKeyValueListRenderItem(IReadOnlyList<SduiMetricEntry> Items) : SduiRenderItem
{
    public override string ViewType => SduiBlockTypes.KeyValueList;
}

/// <summary>分段开关渲染项。</summary>
public sealed record SduiSegmentedSwitchRenderItem(
    string Label,
    string StateKey,
    string SelectedValue,
    IReadOnlyList<SduiOptionItem> Options,
    string? OnChangeActionId) : SduiRenderItem
{
    public override string ViewType => SduiBlockTypes.SegmentedSwitch;
}

/// <summary>按钮的纯展示数据（动作执行仍由 ViewModel 完成）。</summary>
public sealed record SduiButtonView(
    string Label,
    string? Style,
    string ActionId,
    bool Confirm,
    string? ConfirmTitle,
    string? ConfirmMessage);

/// <summary>列表区块标题渲染项。</summary>
public sealed record SduiListHeaderRenderItem(string Title) : SduiRenderItem
{
    public override string ViewType => SduiBlockTypes.ListSection;
}

/// <summary>列表为空时的占位渲染项。</summary>
public sealed record SduiListEmptyRenderItem(string Title, string EmptyText) : SduiRenderItem
{
    public override string ViewType => SduiBlockTypes.ListSection;
}

/// <summary>
/// 列表行渲染项：保留原始条目 <see cref="Item"/> 供行内动作以 <c>item.*</c> 绑定，
/// 宿主只做只读投影，不解析业务结构。
/// </summary>
public sealed record SduiListRowRenderItem(
    int ItemIndex,
    JsonElement Item,
    string Title,
    string Subtitle,
    string Badge,
    double? ProgressPercent,
    IReadOnlyList<SduiButtonView> Actions) : SduiRenderItem
{
    public override string ViewType => SduiBlockTypes.ListSection;
}

/// <summary>动作栏渲染项。</summary>
public sealed record SduiActionBarRenderItem(IReadOnlyList<SduiButtonView> Actions) : SduiRenderItem
{
    public override string ViewType => SduiBlockTypes.ActionBar;
}

/// <summary>外部链接渲染项。</summary>
public sealed record SduiExternalLinkRenderItem(string Label, string Url) : SduiRenderItem
{
    public override string ViewType => SduiBlockTypes.ExternalLink;
}

/// <summary>官方配置桥标题渲染项。</summary>
public sealed record SduiSettingsBridgeHeaderRenderItem(string Namespace, string Title) : SduiRenderItem
{
    public override string ViewType => SduiBlockTypes.SettingsBridge;
}

/// <summary>官方配置桥字段渲染项（复用 Schemastery 强类型字段视图）。</summary>
public sealed record SduiSettingsBridgeFieldRenderItem(string Namespace, PluginConfigFieldView Field) : SduiRenderItem
{
    public override string ViewType => SduiBlockTypes.SettingsBridge;
}

/// <summary>非核心数据源拉取失败的页内诊断渲染项，绝不因单个数据源失败而崩溃整页。</summary>
public sealed record SduiSourceErrorRenderItem(string SourceId, string DiagnosticKey, string Message) : SduiRenderItem
{
    public override string ViewType => "source_error";
}

/// <summary>
/// RPC 动作的持久结果行（第 2 项精修）：无论成功或失败都置顶显示，携带端点、稳定错误码与耗时。
/// 所有文本均已脱敏截断，绝不含明文凭据。
/// </summary>
public sealed record SduiActionResultRenderItem(
    bool Success,
    string Endpoint,
    string? ErrorCode,
    long ElapsedMs,
    string SummaryText) : SduiRenderItem
{
    public override string ViewType => "action_result";
}

/// <summary>待弹出表单字段（只含声明与默认值，绝不含用户已输入的明文）。</summary>
public sealed record PendingSduiFormFieldView(
    string Key,
    string Label,
    string Type,
    bool Required,
    int? MaxLength,
    IReadOnlyList<SduiOptionItem> Options,
    string? DefaultValue,
    string? Placeholder,
    bool IsSecret);

/// <summary>待弹出的表单请求；用户输入只存在于 <c>SubmitFormAsync</c> 的调用栈中。</summary>
public sealed record PendingSduiFormRequest(
    string ActionId,
    string? Title,
    IReadOnlyList<PendingSduiFormFieldView> Fields,
    string SubmitActionId,
    JsonElement? ItemContext);

/// <summary>待二次确认后打开的外部链接。</summary>
public sealed record PendingSduiExternalUrl(string ActionId, string Url);

/// <summary>插件 SDUI 页面的不可变状态快照。</summary>
public sealed record PluginSduiUiState(
    bool IsLoading,
    bool IsExecutingAction,
    string Title,
    string Subtitle,
    string PackageName,
    string PluginVersion,
    string? DocumentSource)
{
    /// <summary>标签页候选。</summary>
    public IReadOnlyList<SduiTabOption> Tabs { get; init; } = [];

    /// <summary>当前选中的标签页 ID。</summary>
    public string? ActiveTabId { get; init; }

    /// <summary>扁平化的原生渲染项（仅包含当前标签页）。</summary>
    public IReadOnlyList<SduiRenderItem> RenderItems { get; init; } = [];

    /// <summary>一次成功动作的提示文本。</summary>
    public string? StatusMessage { get; init; }

    /// <summary>失败分类码。</summary>
    public string? FailureCode { get; init; }

    /// <summary>稳定诊断键。</summary>
    public string? DiagnosticKey { get; init; }

    /// <summary>等待 UI 弹出的表单；不含任何已输入值。</summary>
    public PendingSduiFormRequest? PendingFormRequest { get; init; }

    /// <summary>等待 UI 二次确认的外部链接。</summary>
    public PendingSduiExternalUrl? PendingExternalUrl { get; init; }

    /// <summary>最近一次 RPC 动作的持久结果行（成功或失败），由页面顶部固定展示。</summary>
    public SduiActionResultRenderItem? LastActionResult { get; init; }

    /// <summary>动作反馈序号：每次 RPC 动作完成（成功或失败）自增，供宿主播放一次性反馈动画。</summary>
    public long ActionFeedbackSequence { get; init; }

    /// <summary>初始空状态。</summary>
    public static readonly PluginSduiUiState Empty = new(
        IsLoading: false,
        IsExecutingAction: false,
        Title: string.Empty,
        Subtitle: string.Empty,
        PackageName: string.Empty,
        PluginVersion: string.Empty,
        DocumentSource: null);
}

/// <summary>
/// 纯托管、不可变、线程安全的插件 SDUI ViewModel（Slice 1B-3）：
/// <list type="bullet">
/// <item>把声明式文档与数据源结果扁平化为强类型 <see cref="SduiRenderItem"/>，宿主零动态造控件；</item>
/// <item>受控动作执行（rpc / settings_mutate / open_form / open_url / refresh_sources / set_ui_state）；</item>
/// <item>表单输入只存在于 <see cref="SubmitFormAsync"/> 调用栈，绝不进入状态或日志；</item>
/// <item>仅对 <c>refreshIntervalSec &gt;= 15</c> 的数据源启动后台刷新，释放即停。</item>
/// </list>
/// </summary>
public sealed class PluginSduiViewModel : IAsyncDisposable
{
    /// <summary>诊断键：数据源加载失败。</summary>
    public const string SourceFailedDiagnostic = "sdui.source_failed";

    /// <summary>诊断键：settings 数据源目标命名空间不存在。</summary>
    public const string SettingsNamespaceMissingDiagnostic = "sdui.settings_namespace_missing";

    /// <summary>诊断键：动作 ID 未声明。</summary>
    public const string UnknownActionDiagnostic = "sdui.unknown_action";

    /// <summary>诊断键：标签页 ID 未声明。</summary>
    public const string UnknownTabDiagnostic = "sdui.unknown_tab";

    /// <summary>诊断键：扩展 RPC 端点未通过白名单校验。</summary>
    public const string InvalidEndpointDiagnostic = "sdui.invalid_endpoint";

    /// <summary>诊断键：动作参数模板非法。</summary>
    public const string InvalidPayloadDiagnostic = "sdui.invalid_payload";

    /// <summary>诊断键：外链不是合法 https 地址。</summary>
    public const string InvalidUrlDiagnostic = "sdui.invalid_url";

    /// <summary>诊断键：表单必填项缺失。</summary>
    public const string FormFieldRequiredDiagnostic = "sdui.form_field_required";

    /// <summary>诊断键：表单输入超过最大长度。</summary>
    public const string FormFieldTooLongDiagnostic = "sdui.form_field_too_long";

    /// <summary>诊断键：表单输入不是该字段允许的取值。</summary>
    public const string FormInvalidValueDiagnostic = "sdui.form_invalid_value";

    /// <summary>诊断键：表单缺少提交动作。</summary>
    public const string FormSubmitMissingDiagnostic = "sdui.form_submit_missing";

    /// <summary>诊断键：网关调用意外失败。</summary>
    public const string ActionFailedDiagnostic = "sdui.action_failed";

    /// <summary>状态提示：设置已保存。</summary>
    public const string SettingsSavedStatus = "sdui.status.settings_saved";

    private const int MaxDiagnosticLength = 240;

    /// <summary>动作反馈行（端点/错误码/提示）的最大长度。</summary>
    private const int MaxActionFeedbackLength = 120;

    /// <summary>自动摘要中单个标量值的最大长度。</summary>
    private const int MaxActionSummaryValueLength = 48;

    /// <summary>自动挂载的“设置”页签 ID。</summary>
    private const string AutoSettingsTabId = "auto_settings";

    /// <summary>自动挂载的“设置”页签标签。</summary>
    private const string AutoSettingsTabLabel = "设置";

    private readonly IDshGateway _gateway;
    private readonly Lock _stateLock = new();
    private readonly SemaphoreSlim _opGate = new(1, 1);

    private readonly Dictionary<string, JsonElement> _sourceData = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sourceErrors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SettingsNamespaceView> _settingsViews = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> _uiState = new(StringComparer.Ordinal);
    private readonly List<Task> _timerTasks = [];

    private PluginSduiDocument? _document;
    private PluginSduiUiState _state = PluginSduiUiState.Empty;
    private JsonElement? _lastResult;
    private SduiActionResultRenderItem? _lastActionResult;
    private long _actionFeedbackSequence;
    private CancellationTokenSource _timerCts = new();
    private int _disposed;
    private int _autoLoadRefreshInFlight;

    /// <summary>创建 ViewModel；网关用于扩展 RPC、官方 settings 与 describe 调用。</summary>
    public PluginSduiViewModel(IDshGateway gateway)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    }

    /// <summary>当前不可变状态快照。</summary>
    public PluginSduiUiState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    /// <summary>状态变化通知（在发布线程同步触发，订阅方不得阻塞）。</summary>
    public event Action<PluginSduiUiState>? StateChanged;

    /// <summary>
    /// 初始化页面：写入文档元数据与 <c>initialUiState</c>，选中第一个标签页，并发加载全部
    /// <c>autoLoad = true</c> 的数据源（含 settings 数据源），再按刷新间隔启动受控后台刷新。
    /// </summary>
    public async Task InitializeAsync(
        PluginSduiDocument document,
        string documentSource,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(document);
        ct.ThrowIfCancellationRequested();

        var tabs = (document.Tabs ?? []).Select(tab => new SduiTabOption(tab.Id, tab.Label)).ToArray();
        var initialTabId = tabs.FirstOrDefault()?.Id;

        var initialUiState = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (document.InitialUiState is { ValueKind: JsonValueKind.Object } seed)
        {
            foreach (var property in seed.EnumerateObject())
            {
                initialUiState[property.Name] = property.Value.Clone();
            }
        }

        PluginSduiUiState loadingState;
        lock (_stateLock)
        {
            _document = document;
            _sourceData.Clear();
            _sourceErrors.Clear();
            _settingsViews.Clear();
            _uiState.Clear();
            foreach (var pair in initialUiState)
            {
                _uiState[pair.Key] = pair.Value;
            }
            _lastResult = null;
            _lastActionResult = null;
            _actionFeedbackSequence = 0;

            _state = new PluginSduiUiState(
                IsLoading: true,
                IsExecutingAction: false,
                Title: document.Title ?? string.Empty,
                Subtitle: document.Subtitle ?? string.Empty,
                PackageName: document.PluginPackage ?? string.Empty,
                PluginVersion: document.PluginVersion ?? string.Empty,
                DocumentSource: string.IsNullOrWhiteSpace(documentSource) ? null : documentSource.Trim())
            {
                Tabs = tabs,
                ActiveTabId = initialTabId,
            };
            loadingState = _state;
        }
        StateChanged?.Invoke(loadingState);

        try
        {
            // 第 3 项精修：文档声明了 settingsNamespace 却没有 settings_bridge 时，按 describe 结果自动挂载。
            var effectiveDocument = await ApplyAutoSettingsBridgeAsync(document, ct).ConfigureAwait(false) ?? document;

            await EnsureSettingsBridgeViewsAsync(effectiveDocument, ct).ConfigureAwait(false);

            var autoLoadIds = (effectiveDocument.Sources ?? [])
                .Where(source => source.AutoLoad)
                .Select(source => source.Id)
                .ToArray();
            await RefreshSourcesCoreAsync(autoLoadIds, ct).ConfigureAwait(false);
            StartTimers(effectiveDocument);
        }
        finally
        {
            UpdateState(current => current with { IsLoading = false });
        }
    }

    /// <summary>切换标签页并重建渲染项；未知标签页 ID 会发布稳定诊断。</summary>
    public void SelectTab(string tabId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var target = tabId?.Trim() ?? string.Empty;
        var document = GetDocument();
        var tab = document?.Tabs?.FirstOrDefault(item => string.Equals(item.Id, target, StringComparison.Ordinal));
        if (tab is null)
        {
            PublishFailure(new DshFailure(DshFailureCode.InvalidState, UnknownTabDiagnostic));
            return;
        }

        UpdateState(current => current with
        {
            ActiveTabId = tab.Id,
            FailureCode = null,
            DiagnosticKey = null,
        });
        RebuildRenderItems();
    }

    /// <summary>刷新全部已声明的数据源。</summary>
    public async Task<Outcome<Unit>> RefreshAllAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        var ids = GetDocument()?.Sources?.Select(source => source.Id).ToArray() ?? [];
        UpdateState(current => current with { IsLoading = true, FailureCode = null, DiagnosticKey = null });
        try
        {
            await RefreshSourcesCoreAsync(ids, ct).ConfigureAwait(false);
            return Outcome<Unit>.Ok(Unit.Value);
        }
        finally
        {
            UpdateState(current => current with { IsLoading = false });
        }
    }

    /// <summary>
    /// 仅刷新文档中 <c>autoLoad = true</c> 的数据源。页面已处于加载中或本方法已有调用在途时，
    /// 立即安静跳过并返回成功，绝不并发重入；取消异常原样透传。
    /// </summary>
    public async Task<Outcome<Unit>> RefreshAutoLoadSourcesAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        // IsLoading 覆盖初始化/全量刷新；Interlocked 守卫覆盖同一时刻并发进入本方法的调用。
        if (State.IsLoading || Interlocked.CompareExchange(ref _autoLoadRefreshInFlight, 1, 0) != 0)
        {
            return Outcome<Unit>.Ok(Unit.Value);
        }

        try
        {
            var ids = GetDocument()?.Sources?
                .Where(source => source.AutoLoad)
                .Select(source => source.Id)
                .ToArray() ?? [];

            UpdateState(current => current with
            {
                IsLoading = true,
                FailureCode = null,
                DiagnosticKey = null,
            });
            try
            {
                await RefreshSourcesCoreAsync(ids, ct).ConfigureAwait(false);
                return Outcome<Unit>.Ok(Unit.Value);
            }
            finally
            {
                UpdateState(current => current with { IsLoading = false });
            }
        }
        finally
        {
            Interlocked.Exchange(ref _autoLoadRefreshInFlight, 0);
        }
    }

    /// <summary>按 ID 刷新指定数据源；单个数据源失败只产生页内诊断项，不抛出、不阻塞其余数据源。</summary>
    public async Task<Outcome<Unit>> RefreshSourcesAsync(
        IReadOnlyList<string> sourceIds,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(sourceIds);
        ct.ThrowIfCancellationRequested();

        await RefreshSourcesCoreAsync(sourceIds, ct).ConfigureAwait(false);
        return Outcome<Unit>.Ok(Unit.Value);
    }

    /// <summary>切换分段开关取值：写入本地 UI 状态（用于 <c>ui.*</c> 绑定），再执行声明的变化动作。</summary>
    public async Task<Outcome<Unit>> SelectSegmentedValueAsync(
        string stateKey,
        string selectedValue,
        string? onChangeActionId = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateKey);
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(selectedValue))
        {
            return FailInvalid(FormInvalidValueDiagnostic);
        }

        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SetUiStateValue(stateKey, CreateStringElement(selectedValue));
            if (!string.IsNullOrWhiteSpace(onChangeActionId))
            {
                var outcome = await ExecuteActionCoreAsync(onChangeActionId.Trim(), null, null, ct).ConfigureAwait(false);
                if (!outcome.IsOk)
                {
                    return outcome;
                }
            }

            RebuildRenderItems();
            return Outcome<Unit>.Ok(Unit.Value);
        }
        finally
        {
            _opGate.Release();
        }
    }

    /// <summary>
    /// 执行按钮/行内动作：<c>rpc</c>、<c>settings_mutate</c>、<c>open_form</c>、<c>open_url</c>、
    /// <c>refresh_sources</c>、<c>set_ui_state</c>；成功后按 <c>onSuccessRefresh</c> 自动刷新并重建渲染项。
    /// </summary>
    public async Task<Outcome<Unit>> InvokeButtonActionAsync(
        string actionId,
        JsonElement? itemContext = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(actionId))
        {
            return FailInvalid(UnknownActionDiagnostic);
        }

        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            UpdateState(current => current with
            {
                IsExecutingAction = true,
                FailureCode = null,
                DiagnosticKey = null,
            });
            return await ExecuteActionCoreAsync(actionId.Trim(), itemContext, null, ct).ConfigureAwait(false);
        }
        finally
        {
            UpdateState(current => current with { IsExecutingAction = false });
            _opGate.Release();
        }
    }

    /// <summary>
    /// 提交待弹出表单：仅按声明字段筛选并校验输入，构建只存在于本次调用栈的 <c>form</c> JSON，
    /// 物化 <c>submitAction.payloadTemplate</c> 后执行；密码等敏感值绝不写入状态或诊断。
    /// </summary>
    public async Task<Outcome<Unit>> SubmitFormAsync(
        PendingSduiFormRequest request,
        IReadOnlyDictionary<string, string> fieldValues,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(fieldValues);
        ct.ThrowIfCancellationRequested();

        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var document = GetDocument();
            var submit = document?.Actions?.FirstOrDefault(action =>
                string.Equals(action.Id, request.SubmitActionId, StringComparison.Ordinal));
            if (submit is null ||
                submit.Kind is not (SduiActionKinds.Rpc or SduiActionKinds.SettingsMutate))
            {
                return FailInvalid(FormSubmitMissingDiagnostic);
            }

            var form = BuildFormElement(request.Fields, fieldValues, out var errorKey);
            if (form is null)
            {
                return FailInvalid(errorKey ?? FormInvalidValueDiagnostic);
            }

            UpdateState(current => current with
            {
                IsExecutingAction = true,
                FailureCode = null,
                DiagnosticKey = null,
            });

            var outcome = submit.Kind == SduiActionKinds.Rpc
                ? await ExecuteRpcActionAsync(submit, request.ItemContext, form, ct).ConfigureAwait(false)
                : await ExecuteSettingsMutateAsync(submit, request.ItemContext, form, ct).ConfigureAwait(false);

            if (outcome.IsOk)
            {
                UpdateState(current => current with { PendingFormRequest = null });
                if (submit.OnSuccessRefresh is { Count: > 0 } refresh)
                {
                    await RefreshSourcesCoreAsync(refresh, ct).ConfigureAwait(false);
                    RebuildRenderItems();
                }
            }

            return outcome;
        }
        finally
        {
            UpdateState(current => current with { IsExecutingAction = false });
            _opGate.Release();
        }
    }

    /// <summary>关闭待弹出表单（用户取消），不执行任何提交动作。</summary>
    public void DismissPendingForm() =>
        UpdateState(current => current with { PendingFormRequest = null });

    /// <summary>清除已确认/已取消的外部链接。</summary>
    public void ClearPendingExternalUrl() =>
        UpdateState(current => current with { PendingExternalUrl = null });

    /// <summary>保存官方配置桥字段（前置强类型编码，实时热生效并携带乐观锁 revision）。</summary>
    public async Task<Outcome<Unit>> SaveSettingsBridgeFieldAsync(
        string settingsNamespace,
        PluginConfigFieldView field,
        string? rawInput,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsNamespace);
        ArgumentNullException.ThrowIfNull(field);
        ct.ThrowIfCancellationRequested();

        if (!PluginSettingsSchemaParser.TryEncodeFieldValue(field, rawInput, out var encoded, out var errorKey))
        {
            return FailInvalid(errorKey ?? "settings.invalid_value");
        }

        return await MutateSettingsFieldAsync(
            settingsNamespace.Trim(),
            [SettingsPathOp.Set(field.Path, encoded)],
            ct).ConfigureAwait(false);
    }

    /// <summary>重置官方配置桥字段为默认值（发 <c>settings/mutate</c> 的 <c>unset</c>，携带乐观锁 revision）。</summary>
    public async Task<Outcome<Unit>> ResetSettingsBridgeFieldAsync(
        string settingsNamespace,
        PluginConfigFieldView field,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsNamespace);
        ArgumentNullException.ThrowIfNull(field);
        ct.ThrowIfCancellationRequested();

        return await MutateSettingsFieldAsync(
            settingsNamespace.Trim(),
            [SettingsPathOp.Unset(field.Path)],
            ct).ConfigureAwait(false);
    }

    private async Task<Outcome<Unit>> MutateSettingsFieldAsync(
        string settingsNamespace,
        IReadOnlyList<SettingsPathOp> ops,
        CancellationToken ct)
    {
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            UpdateState(current => current with
            {
                IsExecutingAction = true,
                FailureCode = null,
                DiagnosticKey = null,
            });

            var revision = GetSettingsRevision(settingsNamespace);
            Outcome<SettingsNamespaceView> outcome;
            try
            {
                outcome = await _gateway
                    .MutateSettingsAsync(settingsNamespace, ops, revision, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _ = ex;
                outcome = Outcome<SettingsNamespaceView>.Fail(DshFailureCode.Unavailable, ActionFailedDiagnostic);
            }

            if (!outcome.IsOk || outcome.Value is null)
            {
                var failure = outcome.Error ?? new DshFailure(DshFailureCode.Unavailable, ActionFailedDiagnostic);
                PublishFailure(failure);
                if (failure.Code == DshFailureCode.SettingsConflict)
                {
                    await RefreshSettingsNamespaceAsync(settingsNamespace, ct).ConfigureAwait(false);
                }
                return Outcome<Unit>.Fail(failure);
            }

            ApplySettingsView(outcome.Value);
            // 保存/重置返回的配置视图已就位，立即重建渲染项：无需切换页签即可看到新值。
            RebuildRenderItems();
            UpdateState(current => current with
            {
                StatusMessage = SettingsSavedStatus,
                FailureCode = null,
                DiagnosticKey = null,
            });
            return Outcome<Unit>.Ok(Unit.Value);
        }
        finally
        {
            UpdateState(current => current with { IsExecutingAction = false });
            _opGate.Release();
        }
    }

    private async Task RefreshSettingsNamespaceAsync(string settingsNamespace, CancellationToken ct)
    {
        var document = GetDocument();
        if (document?.Sources is null)
        {
            return;
        }

        var ids = document.Sources
            .Where(source => string.Equals(source.Kind, SduiDataSourceKinds.Settings, StringComparison.Ordinal) &&
                             string.Equals(source.Namespace, settingsNamespace, StringComparison.Ordinal))
            .Select(source => source.Id)
            .ToArray();
        if (ids.Length > 0)
        {
            await RefreshSourcesCoreAsync(ids, ct).ConfigureAwait(false);
        }
    }

    private async Task<Outcome<Unit>> ExecuteActionCoreAsync(
        string actionId,
        JsonElement? itemContext,
        JsonElement? form,
        CancellationToken ct)
    {
        var document = GetDocument();
        var action = document?.Actions?.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, actionId, StringComparison.Ordinal));
        if (action is null)
        {
            return FailInvalid(UnknownActionDiagnostic);
        }

        Outcome<Unit> result;
        switch (action.Kind)
        {
            case SduiActionKinds.Rpc:
                result = await ExecuteRpcActionAsync(action, itemContext, form, ct).ConfigureAwait(false);
                break;

            case SduiActionKinds.SettingsMutate:
                result = await ExecuteSettingsMutateAsync(action, itemContext, form, ct).ConfigureAwait(false);
                break;

            case SduiActionKinds.OpenForm:
                result = PublishFormRequest(action, itemContext);
                break;

            case SduiActionKinds.OpenUrl:
                result = PublishExternalUrl(action);
                break;

            case SduiActionKinds.RefreshSources:
            {
                var targets = action.Targets is { Count: > 0 }
                    ? action.Targets
                    : (IReadOnlyList<string>)(document?.Sources?.Select(source => source.Id).ToArray() ?? []);
                await RefreshSourcesCoreAsync(targets, ct).ConfigureAwait(false);
                result = Outcome<Unit>.Ok(Unit.Value);
                break;
            }

            case SduiActionKinds.SetUiState:
                result = ApplyUiStateAction(action);
                break;

            default:
                result = FailInvalid(InvalidPayloadDiagnostic);
                break;
        }

        if (result.IsOk &&
            action.Kind is SduiActionKinds.Rpc or SduiActionKinds.SettingsMutate &&
            action.OnSuccessRefresh is { Count: > 0 } refresh)
        {
            await RefreshSourcesCoreAsync(refresh, ct).ConfigureAwait(false);
            RebuildRenderItems();
        }

        return result;
    }

    private async Task<Outcome<Unit>> ExecuteRpcActionAsync(
        SduiActionSpec action,
        JsonElement? itemContext,
        JsonElement? form,
        CancellationToken ct)
    {
        var endpoint = action.Endpoint?.Trim() ?? string.Empty;
        if (!PluginRpcEndpointValidator.IsAllowedExtensionEndpoint(endpoint))
        {
            var invalidFailure = new DshFailure(DshFailureCode.InvalidState, InvalidEndpointDiagnostic);
            PublishFailure(invalidFailure);
            PublishActionResult(
                success: false,
                endpoint,
                invalidFailure,
                elapsedMs: 0,
                detail: null);
            return Outcome<Unit>.Fail(invalidFailure);
        }

        var context = CreateContext(itemContext, form, null);
        var args = PluginSduiEvaluator.MaterializeTemplate(action.PayloadTemplate, context) ?? CreateEmptyObject();

        var stopwatch = Stopwatch.StartNew();
        Outcome<JsonElement> outcome;
        try
        {
            outcome = await _gateway
                .InvokePluginRpcAsync(endpoint, args, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            outcome = Outcome<JsonElement>.Fail(DshFailureCode.Unavailable, ActionFailedDiagnostic);
        }
        finally
        {
            stopwatch.Stop();
        }

        var elapsedMs = stopwatch.ElapsedMilliseconds;

        if (!outcome.IsOk)
        {
            var failure = outcome.Error ?? new DshFailure(DshFailureCode.Unavailable, ActionFailedDiagnostic);
            PublishFailure(failure);
            PublishActionResult(
                success: false,
                endpoint,
                failure,
                elapsedMs,
                detail: null);
            return Outcome<Unit>.Fail(failure);
        }

        var resultElement = outcome.Value;
        lock (_stateLock)
        {
            _lastResult = resultElement.ValueKind is JsonValueKind.Undefined ? null : resultElement.Clone();
        }

        string? detail = null;
        if (!string.IsNullOrWhiteSpace(action.ToastResultBind))
        {
            var resultContext = CreateContext(itemContext, form, resultElement);
            var resolved = PluginSduiEvaluator.ResolvePath(action.ToastResultBind, resultContext);
            var text = PluginSduiEvaluator.FormatValue(resolved);
            if (!string.IsNullOrWhiteSpace(text))
            {
                detail = SecretRedactor.RedactAndTruncate(text.Trim(), MaxActionFeedbackLength);
            }
        }

        // 未配置 toastResultBind 或绑定值为空时，自动生成返回值摘要（脱敏、最多 3 个顶层字段）。
        detail ??= BuildActionResultSummary(resultElement);

        var actionResult = PublishActionResult(
            success: true,
            endpoint,
            failure: null,
            elapsedMs,
            detail);

        UpdateState(current => current with
        {
            StatusMessage = detail ?? actionResult.SummaryText,
            FailureCode = null,
            DiagnosticKey = null,
        });
        return Outcome<Unit>.Ok(Unit.Value);
    }

    /// <summary>
    /// 发布一次 RPC 动作的持久结果行：记录 <see cref="PluginSduiUiState.LastActionResult"/>、
    /// 自增 <see cref="PluginSduiUiState.ActionFeedbackSequence"/> 并重建当前页签渲染项（置顶第 0 条）。
    /// 端点与错误码在拼接摘要前统一脱敏截断。
    /// </summary>
    private SduiActionResultRenderItem PublishActionResult(
        bool success,
        string endpoint,
        DshFailure? failure,
        long elapsedMs,
        string? detail)
    {
        var safeEndpoint = SecretRedactor.RedactAndTruncate(endpoint, MaxActionFeedbackLength);
        var errorCode = failure is null
            ? null
            : TrimDiagnostic(failure.DiagnosticKey) ?? failure.Code.ToString();
        var safeErrorCode = string.IsNullOrWhiteSpace(errorCode)
            ? null
            : SecretRedactor.RedactAndTruncate(errorCode, MaxActionFeedbackLength);

        SduiActionResultRenderItem item;
        if (success)
        {
            item = string.IsNullOrWhiteSpace(detail)
                ? new SduiActionResultRenderItem(
                    true,
                    safeEndpoint,
                    ErrorCode: null,
                    elapsedMs,
                    $"成功 · {safeEndpoint} · {elapsedMs} ms")
                : new SduiActionResultRenderItem(
                    true,
                    safeEndpoint,
                    ErrorCode: null,
                    elapsedMs,
                    $"成功 · {safeEndpoint} · {elapsedMs} ms · {detail}");
        }
        else
        {
            var code = safeErrorCode ?? "unknown";
            item = new SduiActionResultRenderItem(
                false,
                safeEndpoint,
                safeErrorCode,
                elapsedMs,
                $"失败 · {safeEndpoint} · {code} · {elapsedMs} ms");
        }

        long sequence;
        lock (_stateLock)
        {
            _lastActionResult = item;
            sequence = ++_actionFeedbackSequence;
        }

        UpdateState(current => current with
        {
            LastActionResult = item,
            ActionFeedbackSequence = sequence,
        });
        RebuildRenderItems();
        return item;
    }

    /// <summary>把 RPC 返回值压缩成单行摘要：Object 取前 3 个顶层字段，嵌套集合只显示键名，敏感值固定 <c>***</c>。</summary>
    private static string? BuildActionResultSummary(JsonElement result)
    {
        switch (result.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var parts = new List<string>(3);
                foreach (var property in result.EnumerateObject())
                {
                    if (parts.Count >= 3)
                    {
                        break;
                    }

                    parts.Add(SummarizeResultProperty(property));
                }

                return parts.Count == 0 ? null : string.Join(" · ", parts);
            }

            case JsonValueKind.Array:
                return $"{result.GetArrayLength()} 项";

            case JsonValueKind.String:
            {
                var text = SecretRedactor.RedactAndTruncate(result.GetString() ?? string.Empty, MaxActionFeedbackLength);
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return SecretRedactor.RedactAndTruncate(
                    SummarizeScalar(result),
                    MaxActionFeedbackLength);

            default:
                return null;
        }
    }

    private static string SummarizeResultProperty(JsonProperty property)
    {
        if (IsSensitivePropertyName(property.Name))
        {
            return $"{property.Name}=***";
        }

        return property.Value.ValueKind switch
        {
            JsonValueKind.Object or JsonValueKind.Array => property.Name,
            JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False =>
                $"{property.Name}={SecretRedactor.RedactAndTruncate(SummarizeScalar(property.Value), MaxActionSummaryValueLength)}",
            _ => property.Name,
        };
    }

    private static string SummarizeScalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => string.Empty,
    };

    /// <summary>
    /// 明确敏感词判定：避免把 <c>keyCount</c> / <c>keys</c> 等无害字段误判为凭据；
    /// 同时覆盖 <c>password</c> / <c>apiKey</c> / <c>token</c> / <c>authorization</c> 等常见命名。
    /// </summary>
    private static bool IsSensitivePropertyName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var normalized = name.Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
        if (normalized == "key")
        {
            return true;
        }

        return normalized.Contains("password", StringComparison.Ordinal) ||
               normalized.Contains("passwd", StringComparison.Ordinal) ||
               normalized.Contains("pwd", StringComparison.Ordinal) ||
               normalized.Contains("secret", StringComparison.Ordinal) ||
               normalized.Contains("token", StringComparison.Ordinal) ||
               normalized.Contains("apikey", StringComparison.Ordinal) ||
               normalized.Contains("credential", StringComparison.Ordinal) ||
               normalized.Contains("authorization", StringComparison.Ordinal) ||
               normalized.Contains("cookie", StringComparison.Ordinal) ||
               normalized.Contains("privatekey", StringComparison.Ordinal) ||
               normalized.Contains("accesskey", StringComparison.Ordinal);
    }

    private async Task<Outcome<Unit>> ExecuteSettingsMutateAsync(
        SduiActionSpec action,
        JsonElement? itemContext,
        JsonElement? form,
        CancellationToken ct)
    {
        var document = GetDocument();
        var settingsNamespace = !string.IsNullOrWhiteSpace(action.Endpoint)
            ? action.Endpoint!.Trim()
            : document?.SettingsNamespace?.Trim();
        if (string.IsNullOrWhiteSpace(settingsNamespace))
        {
            return FailInvalid(SettingsNamespaceMissingDiagnostic);
        }

        var context = CreateContext(itemContext, form, null);
        var payload = PluginSduiEvaluator.MaterializeTemplate(action.PayloadTemplate, context);
        if (payload is not { ValueKind: JsonValueKind.Object } payloadObject)
        {
            return FailInvalid(InvalidPayloadDiagnostic);
        }

        var ops = new List<SettingsPathOp>();
        foreach (var property in payloadObject.EnumerateObject())
        {
            ops.Add(SettingsPathOp.Set([property.Name], property.Value.Clone()));
        }
        if (ops.Count == 0)
        {
            return FailInvalid(InvalidPayloadDiagnostic);
        }

        Outcome<SettingsNamespaceView> outcome;
        try
        {
            outcome = await _gateway
                .MutateSettingsAsync(settingsNamespace, ops, GetSettingsRevision(settingsNamespace), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            outcome = Outcome<SettingsNamespaceView>.Fail(DshFailureCode.Unavailable, ActionFailedDiagnostic);
        }

        if (!outcome.IsOk || outcome.Value is null)
        {
            var failure = outcome.Error ?? new DshFailure(DshFailureCode.Unavailable, ActionFailedDiagnostic);
            PublishFailure(failure);
            return Outcome<Unit>.Fail(failure);
        }

        ApplySettingsView(outcome.Value);
        UpdateState(current => current with
        {
            StatusMessage = SettingsSavedStatus,
            FailureCode = null,
            DiagnosticKey = null,
        });
        return Outcome<Unit>.Ok(Unit.Value);
    }

    private Outcome<Unit> PublishFormRequest(SduiActionSpec action, JsonElement? itemContext)
    {
        if (string.IsNullOrWhiteSpace(action.SubmitAction))
        {
            return FailInvalid(FormSubmitMissingDiagnostic);
        }

        var fields = (action.Fields ?? [])
            .Select(field => new PendingSduiFormFieldView(
                field.Key,
                field.Label,
                field.Type,
                field.Required,
                field.MaxLength,
                field.Options ?? [],
                field.DefaultValue,
                field.Placeholder,
                field.IsSecret))
            .ToArray();

        var request = new PendingSduiFormRequest(
            action.Id,
            action.Title,
            fields,
            action.SubmitAction!.Trim(),
            itemContext?.Clone());

        UpdateState(current => current with
        {
            PendingFormRequest = request,
            PendingExternalUrl = null,
            FailureCode = null,
            DiagnosticKey = null,
        });
        return Outcome<Unit>.Ok(Unit.Value);
    }

    private Outcome<Unit> PublishExternalUrl(SduiActionSpec action)
    {
        if (!PluginSduiValidator.IsAllowedHttpsUrl(action.Url))
        {
            return FailInvalid(InvalidUrlDiagnostic);
        }

        UpdateState(current => current with
        {
            PendingExternalUrl = new PendingSduiExternalUrl(action.Id, action.Url!.Trim()),
            PendingFormRequest = null,
            FailureCode = null,
            DiagnosticKey = null,
        });
        return Outcome<Unit>.Ok(Unit.Value);
    }

    private Outcome<Unit> ApplyUiStateAction(SduiActionSpec action)
    {
        if (string.IsNullOrWhiteSpace(action.StateKey))
        {
            return FailInvalid(InvalidPayloadDiagnostic);
        }

        SetUiStateValue(
            action.StateKey.Trim(),
            action.StateValue is { ValueKind: not JsonValueKind.Undefined } value ? value.Clone() : null);
        RebuildRenderItems();
        return Outcome<Unit>.Ok(Unit.Value);
    }

    private void SetUiStateValue(string stateKey, JsonElement? value)
    {
        lock (_stateLock)
        {
            if (value is { } element)
            {
                _uiState[stateKey] = element;
            }
            else
            {
                _uiState.Remove(stateKey);
            }
        }
    }

    /// <summary>
    /// 第 3 项精修：文档声明了 <see cref="PluginSduiDocument.SettingsNamespace"/> 但全文没有
    /// <c>settings_bridge</c> 时，按 describe 快照自动补一个“设置”页签（或追加到最后一个页签）。
    /// describe 失败、命名空间不存在或没有可配置字段时安静跳过，绝不阻断主面板。
    /// </summary>
    /// <returns>规范化后的文档副本；无需自动挂载时返回 <see langword="null"/>。</returns>
    private async Task<PluginSduiDocument?> ApplyAutoSettingsBridgeAsync(
        PluginSduiDocument document,
        CancellationToken ct)
    {
        var settingsNamespace = document.SettingsNamespace?.Trim();
        if (string.IsNullOrWhiteSpace(settingsNamespace))
        {
            return null;
        }

        foreach (var tab in document.Tabs ?? [])
        {
            foreach (var block in tab.Blocks ?? [])
            {
                if (string.Equals(block.Type, SduiBlockTypes.SettingsBridge, StringComparison.Ordinal))
                {
                    // 文档已自行声明配置桥，尊重原作者布局，不自动挂载。
                    return null;
                }
            }
        }

        Outcome<SettingsDescribeSnapshotView> describe;
        try
        {
            describe = await _gateway.DescribeSettingsAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            return null;
        }

        if (!describe.IsOk || describe.Value is null)
        {
            return null;
        }

        var view = describe.Value.Namespaces?.FirstOrDefault(candidate =>
            string.Equals(candidate.Namespace, settingsNamespace, StringComparison.Ordinal));
        if (view is null)
        {
            return null;
        }

        var fields = PluginSettingsSchemaParser.ParseFields(view, profileWritable: true, preferredLocale: "zh");
        if (fields.Count == 0)
        {
            return null;
        }

        lock (_stateLock)
        {
            _settingsViews[settingsNamespace!] = view;
        }

        var tabs = (document.Tabs ?? []).ToList();
        var bridgeBlock = new SduiBlockSpec
        {
            Type = SduiBlockTypes.SettingsBridge,
            Title = AutoSettingsTabLabel,
            Namespace = settingsNamespace,
        };

        if (tabs.Count < PluginSduiValidator.MaxTabs)
        {
            var tabId = AutoSettingsTabId;
            var suffix = 1;
            while (tabs.Any(candidate => string.Equals(candidate.Id, tabId, StringComparison.Ordinal)))
            {
                tabId = $"{AutoSettingsTabId}_{suffix++}";
            }

            tabs.Add(new SduiTabSpec
            {
                Id = tabId,
                Label = AutoSettingsTabLabel,
                Blocks = [bridgeBlock],
            });
        }
        else
        {
            // 页签已达上限：追加到最后一个页签末尾，保持激活页签与既有布局不变。
            var last = tabs[^1];
            tabs[^1] = new SduiTabSpec
            {
                Id = last.Id,
                Label = last.Label,
                Blocks = [.. (last.Blocks ?? []), bridgeBlock],
            };
        }

        var normalized = new PluginSduiDocument
        {
            SchemaVersion = document.SchemaVersion,
            PluginPackage = document.PluginPackage,
            PluginVersion = document.PluginVersion,
            Title = document.Title,
            Subtitle = document.Subtitle,
            SettingsNamespace = document.SettingsNamespace,
            Sources = document.Sources,
            InitialUiState = document.InitialUiState,
            Tabs = tabs,
            Actions = document.Actions,
        };

        var tabOptions = tabs.Select(tab => new SduiTabOption(tab.Id, tab.Label)).ToArray();
        lock (_stateLock)
        {
            _document = normalized;
        }

        UpdateState(current => current with { Tabs = tabOptions });
        return normalized;
    }

    private async Task EnsureSettingsBridgeViewsAsync(PluginSduiDocument document, CancellationToken ct)
    {
        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tab in document.Tabs ?? [])
        {
            foreach (var block in tab.Blocks ?? [])
            {
                if (!string.Equals(block.Type, SduiBlockTypes.SettingsBridge, StringComparison.Ordinal))
                {
                    continue;
                }

                var bridgeNamespace = !string.IsNullOrWhiteSpace(block.Namespace)
                    ? block.Namespace!.Trim()
                    : document.SettingsNamespace?.Trim();
                if (!string.IsNullOrWhiteSpace(bridgeNamespace))
                {
                    namespaces.Add(bridgeNamespace!);
                }
            }
        }

        var pending = namespaces
            .Where(candidate => !IsSettingsViewLoaded(candidate))
            .ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        Outcome<SettingsDescribeSnapshotView> describe;
        try
        {
            describe = await _gateway.DescribeSettingsAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            return;
        }

        if (!describe.IsOk || describe.Value is null)
        {
            return;
        }

        lock (_stateLock)
        {
            foreach (var candidate in pending)
            {
                var view = describe.Value.Namespaces?.FirstOrDefault(item =>
                    string.Equals(item.Namespace, candidate, StringComparison.Ordinal));
                if (view is not null)
                {
                    _settingsViews[candidate] = view;
                }
            }
        }
    }

    private bool IsSettingsViewLoaded(string settingsNamespace)
    {
        lock (_stateLock)
        {
            return _settingsViews.ContainsKey(settingsNamespace);
        }
    }

    private async Task RefreshSourcesCoreAsync(IReadOnlyList<string> sourceIds, CancellationToken ct)
    {
        PluginSduiDocument? document;
        lock (_stateLock)
        {
            document = _document;
        }

        if (document is null || sourceIds.Count == 0)
        {
            return;
        }

        var selected = new List<SduiDataSource>();
        foreach (var id in sourceIds.Distinct(StringComparer.Ordinal))
        {
            var source = document.Sources?.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id, StringComparison.Ordinal));
            if (source is not null)
            {
                selected.Add(source);
            }
        }

        if (selected.Count == 0)
        {
            return;
        }

        await Task.WhenAll(selected.Select(source => LoadSourceAsync(source, ct))).ConfigureAwait(false);
        RebuildRenderItems();
    }

    private async Task LoadSourceAsync(SduiDataSource source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.Equals(source.Kind, SduiDataSourceKinds.Settings, StringComparison.Ordinal))
        {
            await LoadSettingsSourceAsync(source, ct).ConfigureAwait(false);
            return;
        }

        if (!PluginRpcEndpointValidator.IsAllowedExtensionEndpoint(source.Endpoint))
        {
            RecordSourceError(source.Id, InvalidEndpointDiagnostic);
            return;
        }

        var context = CreateContext(null, null, null);
        var args = PluginSduiEvaluator.MaterializeTemplate(source.ArgsTemplate, context) ?? CreateEmptyObject();

        Outcome<JsonElement> outcome;
        try
        {
            outcome = await _gateway
                .InvokePluginRpcAsync(source.Endpoint!, args, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            RecordSourceError(source.Id, SourceFailedDiagnostic);
            return;
        }

        if (!outcome.IsOk)
        {
            RecordSourceError(source.Id, outcome.Error?.DiagnosticKey ?? SourceFailedDiagnostic);
            return;
        }

        lock (_stateLock)
        {
            _sourceData[source.Id] = outcome.Value.Clone();
            _sourceErrors.Remove(source.Id);
        }
    }

    private async Task LoadSettingsSourceAsync(SduiDataSource source, CancellationToken ct)
    {
        var settingsNamespace = source.Namespace?.Trim();
        if (string.IsNullOrWhiteSpace(settingsNamespace))
        {
            RecordSourceError(source.Id, SettingsNamespaceMissingDiagnostic);
            return;
        }

        Outcome<SettingsDescribeSnapshotView> describe;
        try
        {
            describe = await _gateway.DescribeSettingsAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            RecordSourceError(source.Id, SourceFailedDiagnostic);
            return;
        }

        if (!describe.IsOk || describe.Value is null)
        {
            RecordSourceError(source.Id, describe.Error?.DiagnosticKey ?? SourceFailedDiagnostic);
            return;
        }

        var view = describe.Value.Namespaces?.FirstOrDefault(candidate =>
            string.Equals(candidate.Namespace, settingsNamespace, StringComparison.Ordinal));
        if (view is null)
        {
            RecordSourceError(source.Id, SettingsNamespaceMissingDiagnostic);
            return;
        }

        lock (_stateLock)
        {
            _settingsViews[settingsNamespace!] = view;
            _sourceData[source.Id] = BuildSettingsSourceData(view);
            _sourceErrors.Remove(source.Id);
        }
    }

    private void RecordSourceError(string sourceId, string diagnosticKey)
    {
        lock (_stateLock)
        {
            _sourceErrors[sourceId] = TrimDiagnostic(diagnosticKey) ?? SourceFailedDiagnostic;
            _sourceData.Remove(sourceId);
        }
    }

    private void ApplySettingsView(SettingsNamespaceView view)
    {
        lock (_stateLock)
        {
            _settingsViews[view.Namespace] = view;
            var document = _document;
            if (document?.Sources is null)
            {
                return;
            }

            foreach (var source in document.Sources)
            {
                if (string.Equals(source.Kind, SduiDataSourceKinds.Settings, StringComparison.Ordinal) &&
                    string.Equals(source.Namespace?.Trim(), view.Namespace, StringComparison.Ordinal))
                {
                    _sourceData[source.Id] = BuildSettingsSourceData(view);
                    _sourceErrors.Remove(source.Id);
                }
            }
        }
    }

    private void RebuildRenderItems()
    {
        PluginSduiDocument? document;
        PluginSduiUiState state;
        Dictionary<string, JsonElement> sources;
        Dictionary<string, JsonElement> uiState;
        Dictionary<string, string> errors;
        Dictionary<string, SettingsNamespaceView> settingsViews;
        SduiActionResultRenderItem? lastActionResult;
        lock (_stateLock)
        {
            document = _document;
            state = _state;
            sources = new Dictionary<string, JsonElement>(_sourceData, StringComparer.Ordinal);
            uiState = new Dictionary<string, JsonElement>(_uiState, StringComparer.Ordinal);
            errors = new Dictionary<string, string>(_sourceErrors, StringComparer.Ordinal);
            settingsViews = new Dictionary<string, SettingsNamespaceView>(_settingsViews, StringComparer.Ordinal);
            lastActionResult = _lastActionResult;
        }

        if (document is null)
        {
            return;
        }

        var items = new List<SduiRenderItem>();
        if (lastActionResult is not null)
        {
            // 持久结果行置顶第 0 条：切页签/刷新后仍保留，直到下一次 RPC 动作覆盖。
            items.Add(lastActionResult);
        }

        foreach (var pair in errors.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            items.Add(new SduiSourceErrorRenderItem(
                pair.Key,
                pair.Value,
                $"数据源 {pair.Key} 加载失败，请稍后重试（{pair.Value}）。"));
        }

        var context = new SduiEvalContext
        {
            Sources = BuildObjectRoot(sources),
            UiState = BuildObjectRoot(uiState),
        };

        var activeTab = document.Tabs?.FirstOrDefault(tab =>
            string.Equals(tab.Id, state.ActiveTabId, StringComparison.Ordinal));
        if (activeTab?.Blocks is { } blocks)
        {
            foreach (var block in blocks)
            {
                items.AddRange(BuildBlockItems(block, context, settingsViews, document));
            }
        }

        UpdateState(current => current with { RenderItems = items });
    }

    private static IReadOnlyList<SduiRenderItem> BuildBlockItems(
        SduiBlockSpec block,
        SduiEvalContext context,
        IReadOnlyDictionary<string, SettingsNamespaceView> settingsViews,
        PluginSduiDocument document)
    {
        if (!PluginSduiEvaluator.EvaluateCondition(block.VisibleWhen, context))
        {
            return [];
        }

        switch (block.Type)
        {
            case SduiBlockTypes.StatusBanner:
                return BuildStatusBanner(block, context);

            case SduiBlockTypes.QuotaBar:
                return [BuildQuotaBar(block, context)];

            case SduiBlockTypes.MetricGrid:
                return [new SduiMetricGridRenderItem(BuildMetricEntries(block, context))];

            case SduiBlockTypes.KeyValueList:
                return [new SduiKeyValueListRenderItem(BuildMetricEntries(block, context))];

            case SduiBlockTypes.SegmentedSwitch:
                return [BuildSegmentedSwitch(block, context)];

            case SduiBlockTypes.ListSection:
                return BuildListSection(block, context);

            case SduiBlockTypes.ActionBar:
                return [new SduiActionBarRenderItem(MapButtons(block.Actions))];

            case SduiBlockTypes.ExternalLink:
                return [new SduiExternalLinkRenderItem(
                    ResolveText(block.Label ?? block.Title, block.TitleBind, context),
                    block.Url ?? string.Empty)];

            case SduiBlockTypes.SettingsBridge:
                return BuildSettingsBridge(block, document, settingsViews);

            default:
                return [];
        }
    }

    private static IReadOnlyList<SduiRenderItem> BuildStatusBanner(SduiBlockSpec block, SduiEvalContext context)
    {
        var title = ResolveText(block.Title, block.TitleBind, context);
        var subtitle = ResolveText(block.Subtitle, block.SubtitleBind, context);
        if (title.Length == 0 && subtitle.Length == 0)
        {
            return [];
        }

        var tone = string.IsNullOrWhiteSpace(block.Tone) ? "info" : block.Tone!.Trim();
        return [new SduiStatusBannerRenderItem(tone, title, subtitle)];
    }

    private static SduiQuotaBarRenderItem BuildQuotaBar(SduiBlockSpec block, SduiEvalContext context)
    {
        var percent = ResolveNumber(block.PercentBind, context) ?? 0d;
        percent = Math.Clamp(percent, 0d, 100d);
        var statusText = ResolveText(null, block.StatusBind, context);
        var resetAtText = PluginSduiEvaluator.FormatValue(
            PluginSduiEvaluator.ResolvePath(block.ResetAtBind, context),
            SduiValueFormats.RelativeTime);
        return new SduiQuotaBarRenderItem(block.Label ?? string.Empty, percent, statusText, resetAtText);
    }

    private static List<SduiMetricEntry> BuildMetricEntries(SduiBlockSpec block, SduiEvalContext context)
    {
        var items = new List<SduiMetricEntry>();
        foreach (var item in block.Items ?? [])
        {
            var value = PluginSduiEvaluator.ResolvePath(item.ValueBind, context);
            items.Add(new SduiMetricEntry(
                item.Label,
                PluginSduiEvaluator.FormatValue(value, item.Format, item.Unit),
                item.Unit,
                item.Monospace));
        }
        return items;
    }

    private static SduiSegmentedSwitchRenderItem BuildSegmentedSwitch(SduiBlockSpec block, SduiEvalContext context)
    {
        var selectedValue = string.Empty;
        if (!string.IsNullOrWhiteSpace(block.StateKey))
        {
            var resolved = PluginSduiEvaluator.ResolvePath("ui." + block.StateKey, context);
            if (resolved is { ValueKind: JsonValueKind.String } element)
            {
                selectedValue = element.GetString() ?? string.Empty;
            }
            else if (resolved is { } other && other.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            {
                selectedValue = PluginSduiEvaluator.FormatValue(other);
            }
        }

        return new SduiSegmentedSwitchRenderItem(
            block.Label ?? string.Empty,
            block.StateKey ?? string.Empty,
            selectedValue,
            block.Options ?? [],
            block.OnChangeActionId);
    }

    private static List<SduiRenderItem> BuildListSection(SduiBlockSpec block, SduiEvalContext context)
    {
        var title = ResolveText(block.Title, block.TitleBind, context);
        var items = new List<SduiRenderItem>();
        if (title.Length > 0)
        {
            items.Add(new SduiListHeaderRenderItem(title));
        }

        var resolved = PluginSduiEvaluator.ResolvePath(block.ItemsBind, context);
        if (resolved is not { ValueKind: JsonValueKind.Array } array || array.GetArrayLength() == 0)
        {
            items.Add(new SduiListEmptyRenderItem(title, block.EmptyText ?? string.Empty));
            return items;
        }

        var actions = MapButtons(block.ItemActions);
        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            var rowContext = new SduiEvalContext
            {
                Sources = context.Sources,
                UiState = context.UiState,
                Item = element,
            };

            double? progress = null;
            if (ResolveNumber(block.ItemProgressBind, rowContext) is { } progressValue)
            {
                progress = Math.Clamp(progressValue, 0d, 100d);
            }

            items.Add(new SduiListRowRenderItem(
                index,
                element.Clone(),
                ResolveText(null, block.ItemTitleBind, rowContext),
                ResolveText(null, block.ItemSubtitleBind, rowContext),
                ResolveText(null, block.ItemBadgeBind, rowContext),
                progress,
                actions));
            index++;
        }

        return items;
    }

    private static List<SduiRenderItem> BuildSettingsBridge(
        SduiBlockSpec block,
        PluginSduiDocument document,
        IReadOnlyDictionary<string, SettingsNamespaceView> settingsViews)
    {
        var settingsNamespace = !string.IsNullOrWhiteSpace(block.Namespace)
            ? block.Namespace!.Trim()
            : document.SettingsNamespace?.Trim();
        if (string.IsNullOrWhiteSpace(settingsNamespace))
        {
            return [];
        }

        var title = string.IsNullOrWhiteSpace(block.Title) ? settingsNamespace! : block.Title!.Trim();
        var items = new List<SduiRenderItem>
        {
            new SduiSettingsBridgeHeaderRenderItem(settingsNamespace!, title),
        };

        if (settingsViews.TryGetValue(settingsNamespace!, out var view))
        {
            var fields = PluginSettingsSchemaParser.ParseFields(view, profileWritable: true, preferredLocale: "zh");
            foreach (var field in fields)
            {
                items.Add(new SduiSettingsBridgeFieldRenderItem(settingsNamespace!, field));
            }
        }

        return items;
    }

    private static SduiButtonView[] MapButtons(IReadOnlyList<SduiButtonItem>? buttons)
    {
        if (buttons is not { Count: > 0 })
        {
            return [];
        }

        return buttons
            .Select(button => new SduiButtonView(
                button.Label,
                button.Style,
                button.ActionId,
                button.Confirm,
                button.ConfirmTitle,
                button.ConfirmMessage))
            .ToArray();
    }

    private static string ResolveText(string? staticValue, string? bindPath, SduiEvalContext context)
    {
        if (!string.IsNullOrWhiteSpace(bindPath))
        {
            var value = PluginSduiEvaluator.ResolvePath(bindPath, context);
            var text = PluginSduiEvaluator.FormatValue(value);
            if (text.Length > 0)
            {
                return text;
            }
        }

        return staticValue?.Trim() ?? string.Empty;
    }

    private static double? ResolveNumber(string? bindPath, SduiEvalContext context)
    {
        var value = PluginSduiEvaluator.ResolvePath(bindPath, context);
        if (value is not { } element)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number))
        {
            return number;
        }

        if (element.ValueKind == JsonValueKind.String &&
            double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private SduiEvalContext CreateContext(JsonElement? item, JsonElement? form, JsonElement? result)
    {
        Dictionary<string, JsonElement> sources;
        Dictionary<string, JsonElement> uiState;
        JsonElement? lastResult;
        lock (_stateLock)
        {
            sources = new Dictionary<string, JsonElement>(_sourceData, StringComparer.Ordinal);
            uiState = new Dictionary<string, JsonElement>(_uiState, StringComparer.Ordinal);
            lastResult = _lastResult;
        }

        return new SduiEvalContext
        {
            Sources = BuildObjectRoot(sources),
            UiState = BuildObjectRoot(uiState),
            Item = item,
            Form = form,
            Result = result ?? lastResult,
        };
    }

    private static JsonElement BuildObjectRoot(IReadOnlyDictionary<string, JsonElement> properties)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var pair in properties)
            {
                writer.WritePropertyName(pair.Key);
                if (pair.Value.ValueKind == JsonValueKind.Undefined)
                {
                    // JsonElement.WriteTo 对 Undefined 会抛异常；根对象里写 null 保持键存在。
                    writer.WriteNullValue();
                }
                else
                {
                    pair.Value.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static JsonElement BuildSettingsSourceData(SettingsNamespaceView view)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("namespace", view.Namespace);
            writer.WriteNumber("revision", view.Revision);
            writer.WritePropertyName("value");
            if (view.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                writer.WriteNullValue();
            }
            else
            {
                view.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static JsonElement? BuildFormElement(
        IReadOnlyList<PendingSduiFormFieldView> fields,
        IReadOnlyDictionary<string, string> fieldValues,
        out string? errorKey)
    {
        errorKey = null;
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var field in fields)
            {
                var provided = fieldValues.TryGetValue(field.Key, out var raw);
                var value = raw ?? string.Empty;

                if (!provided || (field.Required && string.IsNullOrWhiteSpace(value)))
                {
                    if (field.Required)
                    {
                        errorKey = FormFieldRequiredDiagnostic;
                        return null;
                    }
                    continue;
                }

                if (field.MaxLength is { } maxLength && value.Length > maxLength)
                {
                    errorKey = FormFieldTooLongDiagnostic;
                    return null;
                }

                switch (field.Type)
                {
                    case SduiFormFieldTypes.Bool:
                        if (!bool.TryParse(value.Trim(), out var boolean))
                        {
                            errorKey = FormInvalidValueDiagnostic;
                            return null;
                        }
                        writer.WriteBoolean(field.Key, boolean);
                        break;

                    case SduiFormFieldTypes.Number:
                        if (!decimal.TryParse(
                                value.Trim(),
                                NumberStyles.Float,
                                CultureInfo.InvariantCulture,
                                out var number))
                        {
                            errorKey = FormInvalidValueDiagnostic;
                            return null;
                        }
                        writer.WriteNumber(field.Key, number);
                        break;

                    case SduiFormFieldTypes.Select:
                        if (!field.Options.Any(option => string.Equals(option.Value, value, StringComparison.Ordinal)))
                        {
                            errorKey = FormInvalidValueDiagnostic;
                            return null;
                        }
                        writer.WriteString(field.Key, value);
                        break;

                    default:
                        writer.WriteString(field.Key, value);
                        break;
                }
            }
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static JsonElement CreateStringElement(string value)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStringValue(value);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static JsonElement CreateEmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private void StartTimers(PluginSduiDocument document)
    {
        var token = _timerCts.Token;
        foreach (var source in document.Sources ?? [])
        {
            if (source.RefreshIntervalSec is not { } seconds ||
                seconds < PluginSduiValidator.MinRefreshIntervalSec ||
                string.IsNullOrWhiteSpace(source.Id))
            {
                continue;
            }

            var sourceId = source.Id;
            var interval = TimeSpan.FromSeconds(seconds);
            var task = Task.Run(() => RunRefreshLoopAsync(sourceId, interval, token), token);
            lock (_stateLock)
            {
                _timerTasks.Add(task);
            }
        }
    }

    private async Task RunRefreshLoopAsync(string sourceId, TimeSpan interval, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                await RefreshSourcesCoreAsync([sourceId], ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 释放/取消时安静停止后台刷新循环。
        }
    }

    private long? GetSettingsRevision(string settingsNamespace)
    {
        lock (_stateLock)
        {
            return _settingsViews.TryGetValue(settingsNamespace, out var view) ? view.Revision : null;
        }
    }

    private PluginSduiDocument? GetDocument()
    {
        lock (_stateLock)
        {
            return _document;
        }
    }

    private Outcome<Unit> FailInvalid(string diagnosticKey)
    {
        var failure = new DshFailure(DshFailureCode.InvalidState, diagnosticKey);
        PublishFailure(failure);
        return Outcome<Unit>.Fail(failure);
    }

    private void PublishFailure(DshFailure failure)
    {
        UpdateState(current => current with
        {
            FailureCode = failure.Code.ToString(),
            DiagnosticKey = TrimDiagnostic(failure.DiagnosticKey),
            StatusMessage = null,
        });
    }

    private static string? TrimDiagnostic(string? diagnosticKey)
    {
        if (string.IsNullOrWhiteSpace(diagnosticKey))
        {
            return null;
        }

        return SecretRedactor.RedactAndTruncate(diagnosticKey.Trim(), MaxDiagnosticLength);
    }

    private void UpdateState(Func<PluginSduiUiState, PluginSduiUiState> transform)
    {
        PluginSduiUiState snapshot;
        lock (_stateLock)
        {
            snapshot = transform(_state);
            _state = snapshot;
        }
        StateChanged?.Invoke(snapshot);
    }

    /// <summary>停止后台刷新（取消即停）并释放同步原语。</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _timerCts.CancelAsync().ConfigureAwait(false);

        Task[] tasks;
        lock (_stateLock)
        {
            tasks = _timerTasks.ToArray();
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 后台刷新循环以取消结束，属于正常释放路径。
        }

        _timerCts.Dispose();
        _opGate.Dispose();
    }
}
