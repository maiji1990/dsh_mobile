using System.Text.Json;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Sdui;

namespace DshMobile.Agent.Tests.Sdui;

/// <summary>
/// Slice 1B-3：<see cref="PluginSduiViewModel"/> 的初始化、数据源并发加载、分段开关联动、
/// 表单提交（敏感字段不外泄）与配置桥保存的端到端单元测试。
/// 全部基于真实强类型签名，网关桩只替换 RPC / settings 通道。
/// </summary>
public sealed class PluginSduiViewModelTests
{
    private const string SettingsNamespace = "opencode-go";
    private const string UsageEndpoint = "opencodeGoUsage/read";
    private const string ModelsEndpoint = "opencodeGoModels/read";
    private const string AnalyticsEndpoint = "opencodeGoAnalytics/read";
    private const string AccountsEndpoint = "opencodeGoAccounts/execute";
    private const string DocumentSource = "manifest:/dshmobile.sdui.json";

    private const string SchemaJson = """
    {
      "type": "object",
      "dict": {
        "apiBase": {
          "type": "string",
          "meta": { "description": { "zh": "接口地址", "en": "API base" } }
        },
        "maxAccounts": {
          "type": "natural",
          "meta": { "max": 10, "description": { "zh": "最大账号数", "en": "Max accounts" } }
        }
      }
    }
    """;

    private const string UsageResultJson = """
    {
      "status": "正常",
      "plan": "Pro",
      "limit": 150000000,
      "percent": 42.5,
      "resetAt": "2024-01-01T00:00:00Z",
      "items": [
        { "title": "deepseek-v4", "subtitle": "12 次调用", "badge": "ok" },
        { "title": "deepseek-v4-mini", "subtitle": "3 次调用", "badge": "ok" }
      ]
    }
    """;

    [Fact]
    public async Task InitializeAsync_LoadsSettingsAndAllRpcSourcesConcurrently_AndRendersAllBlockTypes()
    {
        var document = BuildDocument();
        var validation = PluginSduiValidator.Validate(document);
        Assert.True(validation.IsValid, string.Join("；", validation.Errors));

        var gateway = new FakeSduiGateway { ExpectConcurrentSources = true };
        await using var viewModel = new PluginSduiViewModel(gateway);

        await viewModel.InitializeAsync(document, DocumentSource);

        // settings 数据源 + 3 个 rpc 数据源必须在同一批里并发进行（顺序加载会卡在栅栏上直到超时）。
        Assert.True(gateway.AllSourcesArrivedConcurrently, "settings 与 rpc 数据源没有并发拉取。");
        Assert.Equal(3, gateway.SourceLoadCount);
        Assert.False(viewModel.State.IsLoading);

        var items = viewModel.State.RenderItems;

        var quota = Assert.Single(items.OfType<SduiQuotaBarRenderItem>());
        Assert.Equal("本月用量", quota.Label);
        Assert.Equal(42.5d, quota.Percent, 3);
        Assert.Equal("Pro", quota.StatusText);
        Assert.False(string.IsNullOrWhiteSpace(quota.ResetAtText));

        var metricEntries = items.OfType<SduiMetricGridRenderItem>().SelectMany(grid => grid.Items).ToArray();
        Assert.Contains(metricEntries, entry => entry.ValueText.Contains("$1.50", StringComparison.Ordinal));
        var limitEntry = metricEntries.Single(entry => entry.Label == "剩余额度");
        Assert.Equal("$1.50 USD", limitEntry.ValueText);
        Assert.Equal("USD", limitEntry.Unit);
        Assert.True(limitEntry.Monospace);

        // list_section：标题 + 两行真实数据（非空占位）。
        Assert.Single(items.OfType<SduiListHeaderRenderItem>());
        var rows = items.OfType<SduiListRowRenderItem>().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal("deepseek-v4", rows[0].Title);
        Assert.Equal("12 次调用", rows[0].Subtitle);
        Assert.DoesNotContain(items, item => item is SduiListEmptyRenderItem);

        // settings_bridge：头部 + 至少一个强类型字段（接口地址来自 describe 快照）。
        var bridgeHeader = Assert.Single(items.OfType<SduiSettingsBridgeHeaderRenderItem>());
        Assert.Equal(SettingsNamespace, bridgeHeader.Namespace);
        var bridgeFields = items.OfType<SduiSettingsBridgeFieldRenderItem>().ToArray();
        Assert.NotEmpty(bridgeFields);
        var apiBase = Assert.Single(bridgeFields, field => string.Join('.', field.Field.Path) == "apiBase");
        Assert.Equal(SettingsNamespace, apiBase.Namespace);
        Assert.Contains("https://api.example.com/v1", apiBase.Field.CurrentValueJson);

        Assert.Null(viewModel.State.FailureCode);
    }

    [Fact]
    public async Task SelectSegmentedValueAsync_UpdatesUiState_AndRefreshesBoundSourceWithNewArgument()
    {
        var gateway = new FakeSduiGateway();
        await using var viewModel = new PluginSduiViewModel(gateway);
        await viewModel.InitializeAsync(BuildDocument(), DocumentSource);

        Assert.Equal("7d", gateway.LastAnalyticsRange);
        Assert.Equal("7d", Assert.Single(viewModel.State.RenderItems.OfType<SduiSegmentedSwitchRenderItem>()).SelectedValue);

        var outcome = await viewModel.SelectSegmentedValueAsync("analyticsRange", "30d", "applyRange");

        Assert.True(outcome.IsOk, outcome.Error?.DiagnosticKey);

        // 变化动作（refresh_sources → analytics）携带更新后的参数重新请求。
        Assert.Equal("30d", gateway.LastAnalyticsRange);
        var analyticsCalls = gateway.RpcCalls.Where(call => call.Endpoint == AnalyticsEndpoint).ToArray();
        Assert.Equal(2, analyticsCalls.Length);
        Assert.Equal("7d", analyticsCalls[0].Args.GetProperty("range").GetString());
        Assert.Equal("30d", analyticsCalls[1].Args.GetProperty("range").GetString());

        // RenderItems 已按新状态重建：开关选中值与数据源指标同步刷新。
        var segmented = Assert.Single(viewModel.State.RenderItems.OfType<SduiSegmentedSwitchRenderItem>());
        Assert.Equal("analyticsRange", segmented.StateKey);
        Assert.Equal("30d", segmented.SelectedValue);
        Assert.Equal("applyRange", segmented.OnChangeActionId);

        var entries = viewModel.State.RenderItems.OfType<SduiMetricGridRenderItem>().SelectMany(grid => grid.Items).ToArray();
        Assert.Equal("30d", entries.Single(entry => entry.Label == "统计区间").ValueText);
        Assert.Equal("12", entries.Single(entry => entry.Label == "数据点").ValueText);
    }

    [Fact]
    public async Task SubmitFormAsync_SendsGeneratedCommand_AndNeverLeaksPlaintextSecretsIntoState()
    {
        const string password = "S3cr3t-P@ssw0rd-明文";
        const string token = "tok_secret_9f8e7d6c";

        var gateway = new FakeSduiGateway();
        await using var viewModel = new PluginSduiViewModel(gateway);
        await viewModel.InitializeAsync(BuildDocument(), DocumentSource);

        var openOutcome = await viewModel.InvokeButtonActionAsync("addAccount");
        Assert.True(openOutcome.IsOk, openOutcome.Error?.DiagnosticKey);

        var pending = viewModel.State.PendingFormRequest;
        Assert.NotNull(pending);
        Assert.Equal("submitAccount", pending!.SubmitActionId);
        Assert.Equal(
            new[] { "label", "password", "token" },
            pending.Fields.Select(field => field.Key).ToArray());
        Assert.True(pending.Fields.Single(field => field.Key == "password").IsSecret);
        Assert.True(pending.Fields.Single(field => field.Key == "token").IsSecret);

        var submitOutcome = await viewModel.SubmitFormAsync(
            pending,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["label"] = "主账号",
                ["password"] = password,
                ["token"] = token,
            });

        Assert.True(submitOutcome.IsOk, submitOutcome.Error?.DiagnosticKey);

        var call = Assert.Single(gateway.RpcCalls, item => item.Endpoint == AccountsEndpoint);
        var command = call.Args.GetProperty("command");
        Assert.Equal("add", command.GetProperty("kind").GetString());
        Assert.Matches("^[0-9a-f]{32}$", command.GetProperty("id").GetString()!);
        Assert.Equal("主账号", command.GetProperty("label").GetString());
        Assert.Equal(password, command.GetProperty("password").GetString());
        Assert.Equal(token, command.GetProperty("token").GetString());

        // 提交成功后弹窗关闭，状态与渲染项中不得残留任何明文敏感输入。
        Assert.Null(viewModel.State.PendingFormRequest);
        Assert.NotEqual(password, viewModel.State.StatusMessage);
        Assert.NotEqual(token, viewModel.State.DiagnosticKey);

        var stateJson = JsonSerializer.Serialize(viewModel.State);
        Assert.DoesNotContain(password, stateJson, StringComparison.Ordinal);
        Assert.DoesNotContain(token, stateJson, StringComparison.Ordinal);

        var renderJson = string.Join(
            '\n',
            viewModel.State.RenderItems.Select(item => JsonSerializer.Serialize(item, item.GetType())));
        Assert.DoesNotContain(password, renderJson, StringComparison.Ordinal);
        Assert.DoesNotContain(token, renderJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_FailingSource_DoesNotThrowAndEmitsErrorItem()
    {
        var gateway = new FakeSduiGateway();
        gateway.FailingEndpoints.Add(ModelsEndpoint);
        await using var viewModel = new PluginSduiViewModel(gateway);

        await viewModel.InitializeAsync(BuildDocument(), DocumentSource);

        Assert.False(viewModel.State.IsLoading);
        var error = Assert.Single(viewModel.State.RenderItems.OfType<SduiSourceErrorRenderItem>());
        Assert.Equal("models", error.SourceId);
        Assert.Equal(PluginSduiViewModel.SourceFailedDiagnostic, error.DiagnosticKey);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));

        // 其余数据源不受影响，页面仍然完整可用。
        Assert.Single(viewModel.State.RenderItems.OfType<SduiQuotaBarRenderItem>());
        var modelEntry = Assert.Single(
            viewModel.State.RenderItems
                .OfType<SduiMetricGridRenderItem>()
                .SelectMany(grid => grid.Items)
                .ToArray(),
            entry => entry.Label == "模型数");
        Assert.Equal(string.Empty, modelEntry.ValueText);
    }

    [Fact]
    public async Task SaveSettingsBridgeFieldAsync_UpdatesSettingsViewSynchronously_AndRefreshesRenderItems()
    {
        const string newApiBase = "https://api2.example.com/v2";

        var gateway = new FakeSduiGateway();
        await using var viewModel = new PluginSduiViewModel(gateway);
        await viewModel.InitializeAsync(BuildDocument(), DocumentSource);

        var field = Assert.Single(
            viewModel.State.RenderItems.OfType<SduiSettingsBridgeFieldRenderItem>().ToArray(),
            item => string.Join('.', item.Field.Path) == "apiBase").Field;
        Assert.Equal(PluginConfigFieldKind.Text, field.Kind);

        var describesBeforeSave = gateway.DescribeCalls;

        var outcome = await viewModel.SaveSettingsBridgeFieldAsync(SettingsNamespace, field, newApiBase);

        Assert.True(outcome.IsOk, outcome.Error?.DiagnosticKey);
        Assert.Equal(PluginSduiViewModel.SettingsSavedStatus, viewModel.State.StatusMessage);
        Assert.False(viewModel.State.IsExecutingAction);

        var mutation = Assert.Single(gateway.Mutations);
        Assert.Equal(SettingsNamespace, mutation.Namespace);
        var op = Assert.Single(mutation.Ops);
        Assert.Equal(SettingsPathOpKind.Set, op.Op);
        Assert.Equal(new[] { "apiBase" }, op.Path.ToArray());
        Assert.Equal(newApiBase, op.Value!.Value.GetString());
        Assert.Equal(1L, mutation.ExpectedRevision!.Value);

        // 返回值已就地更新到 settings 视图缓存，无需重新 describe（同步更新而非再拉一次）。
        Assert.Equal(describesBeforeSave, gateway.DescribeCalls);

        // 下一次渲染重建即可看到新值（配置桥字段 + 绑定 settings 数据源的指标同步刷新）。
        viewModel.SelectTab("overview");

        var refreshedField = Assert.Single(
            viewModel.State.RenderItems.OfType<SduiSettingsBridgeFieldRenderItem>().ToArray(),
            item => string.Join('.', item.Field.Path) == "apiBase").Field;
        Assert.Contains(newApiBase, refreshedField.CurrentValueJson);

        var entries = viewModel.State.RenderItems.OfType<SduiMetricGridRenderItem>().SelectMany(grid => grid.Items).ToArray();
        Assert.Equal(newApiBase, entries.Single(entry => entry.Label == "接口地址").ValueText);
    }

    [Fact]
    public async Task ResetSettingsBridgeFieldAsync_UpdatesRenderItemsImmediatelyWithoutTabSwitch()
    {
        const string newApiBase = "https://api2.example.com/v2";
        const string defaultApiBase = "https://api.example.com/v1";

        var gateway = new FakeSduiGateway();
        await using var viewModel = new PluginSduiViewModel(gateway);
        await viewModel.InitializeAsync(BuildDocument(), DocumentSource);

        var initialField = Assert.Single(
            viewModel.State.RenderItems.OfType<SduiSettingsBridgeFieldRenderItem>().ToArray(),
            item => string.Join('.', item.Field.Path) == "apiBase").Field;
        Assert.Contains(defaultApiBase, initialField.CurrentValueJson);
        Assert.False(initialField.HasUserOverride);

        // 先修改 apiBase，使该字段带上用户覆盖标记。
        var saveOutcome = await viewModel.SaveSettingsBridgeFieldAsync(SettingsNamespace, initialField, newApiBase);
        Assert.True(saveOutcome.IsOk, saveOutcome.Error?.DiagnosticKey);

        var savedField = Assert.Single(
            viewModel.State.RenderItems.OfType<SduiSettingsBridgeFieldRenderItem>().ToArray(),
            item => string.Join('.', item.Field.Path) == "apiBase").Field;
        Assert.Contains(newApiBase, savedField.CurrentValueJson);
        Assert.True(savedField.HasUserOverride);

        // 关键断言：不调用 SelectTab，直接重置后 RenderItems 立即恢复到默认值并清除覆盖标记。
        var resetOutcome = await viewModel.ResetSettingsBridgeFieldAsync(SettingsNamespace, savedField);
        Assert.True(resetOutcome.IsOk, resetOutcome.Error?.DiagnosticKey);

        var resetField = Assert.Single(
            viewModel.State.RenderItems.OfType<SduiSettingsBridgeFieldRenderItem>().ToArray(),
            item => string.Join('.', item.Field.Path) == "apiBase").Field;
        Assert.Contains(defaultApiBase, resetField.CurrentValueJson);
        Assert.DoesNotContain(newApiBase, resetField.CurrentValueJson);
        Assert.False(resetField.HasUserOverride);
    }

    [Fact]
    public async Task InvokeRpcActionAsync_ToastResultBind_PublishesTopActionResultWithDetailAndElapsed()
    {
        var gateway = new FakeSduiGateway
        {
            RpcResultsByEndpoint = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                [UsageEndpoint] = ParseElement("""{"message":"已完成同步"}"""),
            },
        };
        await using var viewModel = new PluginSduiViewModel(gateway);
        await viewModel.InitializeAsync(
            BuildRpcActionDocument(CreateRpcAction("run", UsageEndpoint, toastResultBind: "result.message")),
            DocumentSource);

        var before = viewModel.State.ActionFeedbackSequence;
        var outcome = await viewModel.InvokeButtonActionAsync("run");

        Assert.True(outcome.IsOk, outcome.Error?.DiagnosticKey);
        var item = Assert.IsType<SduiActionResultRenderItem>(viewModel.State.RenderItems[0]);
        Assert.True(item.Success);
        Assert.Equal(UsageEndpoint, item.Endpoint);
        Assert.Null(item.ErrorCode);
        Assert.InRange(item.ElapsedMs, 0, long.MaxValue);
        Assert.Contains("已完成同步", item.SummaryText, StringComparison.Ordinal);
        Assert.Contains(" ms", item.SummaryText, StringComparison.Ordinal);
        Assert.Contains("已完成同步", viewModel.State.StatusMessage!, StringComparison.Ordinal);
        Assert.Equal(before + 1, viewModel.State.ActionFeedbackSequence);
        Assert.Same(item, viewModel.State.LastActionResult);
    }

    [Fact]
    public async Task InvokeRpcActionAsync_WithoutToastBind_SummarizesTopThreeScalarsAndHidesNestedValues()
    {
        const string nestedSecret = "nested-secret-value";
        const string ignoredValue = "ignored-tail-value";
        var gateway = new FakeSduiGateway
        {
            RpcResultsByEndpoint = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                [UsageEndpoint] = ParseElement($$"""
                {
                  "status": "ok",
                  "meta": { "apiBase": "https://internal.example/v1", "password": "{{nestedSecret}}" },
                  "count": 3,
                  "tail": "{{ignoredValue}}"
                }
                """),
            },
        };
        await using var viewModel = new PluginSduiViewModel(gateway);
        await viewModel.InitializeAsync(
            BuildRpcActionDocument(CreateRpcAction("run", UsageEndpoint, toastResultBind: null)),
            DocumentSource);

        var outcome = await viewModel.InvokeButtonActionAsync("run");

        Assert.True(outcome.IsOk, outcome.Error?.DiagnosticKey);
        var item = Assert.IsType<SduiActionResultRenderItem>(viewModel.State.RenderItems[0]);
        Assert.Contains("status=ok", item.SummaryText, StringComparison.Ordinal);
        Assert.Contains("count=3", item.SummaryText, StringComparison.Ordinal);
        Assert.Contains("meta", item.SummaryText, StringComparison.Ordinal);
        Assert.DoesNotContain("meta=", item.SummaryText, StringComparison.Ordinal);
        Assert.DoesNotContain("https://internal.example/v1", item.SummaryText, StringComparison.Ordinal);
        Assert.DoesNotContain(nestedSecret, item.SummaryText, StringComparison.Ordinal);
        Assert.DoesNotContain(ignoredValue, item.SummaryText, StringComparison.Ordinal);
        Assert.Contains("status=ok", viewModel.State.StatusMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RpcActionResultFeedback_RedactsSecretsFromToastBindAndAutoSummary()
    {
        const string secretToken = "sk-live-SECRET123456789";
        const string password = "hunter2-password";
        const string apiKey = "k-live-apikey-987654";
        var gateway = new FakeSduiGateway
        {
            RpcResultsByEndpoint = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                [ModelsEndpoint] = ParseElement($$"""
                { "message": "token={{secretToken}} saved password={{password}}" }
                """),
                [UsageEndpoint] = ParseElement($$"""
                {
                  "password": "{{password}}",
                  "apiKey": "{{apiKey}}",
                  "token": "{{secretToken}}",
                  "safe": "ok"
                }
                """),
            },
        };
        await using var viewModel = new PluginSduiViewModel(gateway);
        await viewModel.InitializeAsync(
            BuildRpcActionDocument(
                CreateRpcAction("toast", ModelsEndpoint, toastResultBind: "result.message"),
                CreateRpcAction("auto", UsageEndpoint, toastResultBind: null)),
            DocumentSource);

        var toastOutcome = await viewModel.InvokeButtonActionAsync("toast");
        Assert.True(toastOutcome.IsOk, toastOutcome.Error?.DiagnosticKey);
        var toastItem = Assert.IsType<SduiActionResultRenderItem>(viewModel.State.LastActionResult);
        Assert.DoesNotContain(secretToken, toastItem.SummaryText, StringComparison.Ordinal);
        Assert.DoesNotContain(password, toastItem.SummaryText, StringComparison.Ordinal);
        Assert.DoesNotContain(secretToken, viewModel.State.StatusMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain(password, viewModel.State.StatusMessage!, StringComparison.Ordinal);

        var autoOutcome = await viewModel.InvokeButtonActionAsync("auto");
        Assert.True(autoOutcome.IsOk, autoOutcome.Error?.DiagnosticKey);
        var autoItem = Assert.IsType<SduiActionResultRenderItem>(viewModel.State.LastActionResult);
        Assert.Contains("password=***", autoItem.SummaryText, StringComparison.Ordinal);
        Assert.Contains("apiKey=***", autoItem.SummaryText, StringComparison.Ordinal);
        Assert.Contains("token=***", autoItem.SummaryText, StringComparison.Ordinal);

        var feedbackJson = JsonSerializer.Serialize(autoItem) + viewModel.State.StatusMessage;
        Assert.DoesNotContain(secretToken, feedbackJson, StringComparison.Ordinal);
        Assert.DoesNotContain(password, feedbackJson, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, feedbackJson, StringComparison.Ordinal);

        var renderJson = string.Join(
            '\n',
            viewModel.State.RenderItems.Select(renderItem => JsonSerializer.Serialize(renderItem, renderItem.GetType())));
        Assert.DoesNotContain(secretToken, renderJson, StringComparison.Ordinal);
        Assert.DoesNotContain(password, renderJson, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, renderJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeRpcActionAsync_FailurePublishesTopActionResultWithStableCodeAndElapsed()
    {
        var gateway = new FakeSduiGateway();
        gateway.FailingEndpoints.Add(UsageEndpoint);
        await using var viewModel = new PluginSduiViewModel(gateway);
        await viewModel.InitializeAsync(
            BuildRpcActionDocument(CreateRpcAction("run", UsageEndpoint, toastResultBind: null)),
            DocumentSource);

        var outcome = await viewModel.InvokeButtonActionAsync("run");

        Assert.False(outcome.IsOk);
        Assert.Equal(PluginSduiViewModel.SourceFailedDiagnostic, outcome.Error!.DiagnosticKey);
        var item = Assert.IsType<SduiActionResultRenderItem>(viewModel.State.RenderItems[0]);
        Assert.False(item.Success);
        Assert.Equal(UsageEndpoint, item.Endpoint);
        Assert.Equal(PluginSduiViewModel.SourceFailedDiagnostic, item.ErrorCode);
        Assert.Contains("失败", item.SummaryText, StringComparison.Ordinal);
        Assert.Contains(UsageEndpoint, item.SummaryText, StringComparison.Ordinal);
        Assert.Contains(PluginSduiViewModel.SourceFailedDiagnostic, item.SummaryText, StringComparison.Ordinal);
        Assert.Contains(" ms", item.SummaryText, StringComparison.Ordinal);
        Assert.Equal(1, viewModel.State.ActionFeedbackSequence);
        Assert.Equal(item, viewModel.State.LastActionResult);
    }

    [Fact]
    public async Task InitializeAsync_AutoMountsSettingsBridgeTabWhenNamespaceHasFields()
    {
        var gateway = new FakeSduiGateway();
        await using var viewModel = new PluginSduiViewModel(gateway);
        await viewModel.InitializeAsync(BuildSettingsOnlyDocument(), DocumentSource);

        // 自动挂载不改变原激活页签，也不打断原页签布局。
        Assert.Equal("overview", viewModel.State.ActiveTabId);
        Assert.Equal(2, viewModel.State.Tabs.Count);
        var settingsTab = Assert.Single(viewModel.State.Tabs, tab => tab.Label == "设置");
        Assert.StartsWith("auto_settings", settingsTab.Id, StringComparison.Ordinal);

        viewModel.SelectTab(settingsTab.Id);

        var header = Assert.Single(viewModel.State.RenderItems.OfType<SduiSettingsBridgeHeaderRenderItem>());
        Assert.Equal(SettingsNamespace, header.Namespace);
        var fields = viewModel.State.RenderItems.OfType<SduiSettingsBridgeFieldRenderItem>().ToArray();
        Assert.NotEmpty(fields);
        Assert.Contains(fields, field => string.Join('.', field.Field.Path) == "apiBase");
    }

    [Fact]
    public async Task InitializeAsync_SkipsAutoSettingsBridgeWhenNamespaceHasNoFieldsOrIsMissing()
    {
        // 1) 命名空间存在，但 schema 没有任何可配置字段。
        var emptySchemaGateway = new FakeSduiGateway
        {
            SettingsSchemaJson = """{"type":"object","dict":{}}""",
        };
        await using (var viewModel = new PluginSduiViewModel(emptySchemaGateway))
        {
            await viewModel.InitializeAsync(BuildSettingsOnlyDocument(), DocumentSource);
            Assert.Single(viewModel.State.Tabs);
            Assert.DoesNotContain(viewModel.State.Tabs, tab => tab.Label == "设置");
            Assert.DoesNotContain(
                viewModel.State.RenderItems,
                item => item is SduiSettingsBridgeHeaderRenderItem or SduiSettingsBridgeFieldRenderItem);
        }

        // 2) describe 快照里根本没有该命名空间。
        var missingGateway = new FakeSduiGateway { SettingsNamespaceName = "other-namespace" };
        await using (var viewModel = new PluginSduiViewModel(missingGateway))
        {
            await viewModel.InitializeAsync(BuildSettingsOnlyDocument(), DocumentSource);
            Assert.Single(viewModel.State.Tabs);
            Assert.DoesNotContain(viewModel.State.Tabs, tab => tab.Label == "设置");
            Assert.DoesNotContain(
                viewModel.State.RenderItems,
                item => item is SduiSettingsBridgeHeaderRenderItem or SduiSettingsBridgeFieldRenderItem);
        }
    }

    [Fact]
    public async Task RefreshAutoLoadSourcesAsync_RefreshesOnlyAutoLoadSources()
    {
        var gateway = new FakeSduiGateway();
        await using var viewModel = new PluginSduiViewModel(gateway);
        await viewModel.InitializeAsync(BuildAutoLoadDocument(), DocumentSource);

        // 初始化只加载 autoLoad=true 的 usage 数据源。
        Assert.Equal(1, gateway.InvokeCount(UsageEndpoint));
        Assert.Equal(0, gateway.InvokeCount(ModelsEndpoint));
        Assert.False(viewModel.State.IsLoading);

        var outcome = await viewModel.RefreshAutoLoadSourcesAsync();

        Assert.True(outcome.IsOk, outcome.Error?.DiagnosticKey);
        Assert.Equal(2, gateway.InvokeCount(UsageEndpoint));
        Assert.Equal(0, gateway.InvokeCount(ModelsEndpoint));
        Assert.False(viewModel.State.IsLoading);
    }

    [Fact]
    public async Task RefreshAutoLoadSourcesAsync_SkipsWhenRefreshAlreadyInFlight()
    {
        var gateway = new FakeSduiGateway();
        await using var viewModel = new PluginSduiViewModel(gateway);
        await viewModel.InitializeAsync(BuildAutoLoadDocument(), DocumentSource);
        Assert.Equal(1, gateway.InvokeCount(UsageEndpoint));

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.RpcGate = gate;

        var first = viewModel.RefreshAutoLoadSourcesAsync();
        await WaitUntilAsync(() => gateway.InvokeCount(UsageEndpoint) >= 2);
        Assert.True(viewModel.State.IsLoading);

        // 在途期间再次调用必须安静跳过：既不并发重入，也不重复请求数据源。
        var second = await viewModel.RefreshAutoLoadSourcesAsync();

        Assert.True(second.IsOk, second.Error?.DiagnosticKey);
        Assert.Equal(2, gateway.InvokeCount(UsageEndpoint));
        Assert.True(viewModel.State.IsLoading);

        gate.SetResult();
        var firstOutcome = await first.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(firstOutcome.IsOk, firstOutcome.Error?.DiagnosticKey);
        Assert.Equal(2, gateway.InvokeCount(UsageEndpoint));
        Assert.False(viewModel.State.IsLoading);
    }

    [Fact]
    public async Task RefreshAutoLoadSourcesAsync_PropagatesCancellation()
    {
        var gateway = new FakeSduiGateway();
        await using var viewModel = new PluginSduiViewModel(gateway);
        await viewModel.InitializeAsync(BuildAutoLoadDocument(), DocumentSource);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => viewModel.RefreshAutoLoadSourcesAsync(cts.Token));

        Assert.False(viewModel.State.IsLoading);
        Assert.Equal(1, gateway.InvokeCount(UsageEndpoint));
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("等待条件超时。");
            }

            await Task.Delay(10);
        }
    }

    private static PluginSduiDocument BuildAutoLoadDocument() => new()
    {
        SchemaVersion = PluginSduiValidator.CurrentSchemaVersion,
        PluginPackage = "dsh-opencode-go",
        PluginVersion = "1.2.3",
        Title = "自动加载测试",
        Sources =
        [
            new SduiDataSource
            {
                Id = "auto",
                Kind = SduiDataSourceKinds.Rpc,
                Endpoint = UsageEndpoint,
                ArgsTemplate = ParseElement("""{"input":{}}"""),
                AutoLoad = true,
            },
            new SduiDataSource
            {
                Id = "manual",
                Kind = SduiDataSourceKinds.Rpc,
                Endpoint = ModelsEndpoint,
                ArgsTemplate = ParseElement("""{"input":{}}"""),
                AutoLoad = false,
            },
        ],
        Tabs =
        [
            new SduiTabSpec
            {
                Id = "overview",
                Label = "概览",
                Blocks =
                [
                    new SduiBlockSpec
                    {
                        Type = SduiBlockTypes.StatusBanner,
                        TitleBind = "sources.auto.status",
                    },
                ],
            },
        ],
        Actions = [],
    };

    private static PluginSduiDocument BuildRpcActionDocument(params SduiActionSpec[] actions) => new()
    {
        SchemaVersion = PluginSduiValidator.CurrentSchemaVersion,
        PluginPackage = "dsh-opencode-go",
        PluginVersion = "1.2.3",
        Title = "动作反馈测试",
        Sources = [],
        Tabs =
        [
            new SduiTabSpec
            {
                Id = "main",
                Label = "主页面",
                Blocks =
                [
                    new SduiBlockSpec { Type = SduiBlockTypes.StatusBanner, Title = "动作反馈" },
                ],
            },
        ],
        Actions = actions,
    };

    private static SduiActionSpec CreateRpcAction(string id, string endpoint, string? toastResultBind) => new()
    {
        Id = id,
        Kind = SduiActionKinds.Rpc,
        Endpoint = endpoint,
        PayloadTemplate = ParseElement("""{"input":{}}"""),
        ToastResultBind = toastResultBind,
    };

    private static PluginSduiDocument BuildSettingsOnlyDocument() => new()
    {
        SchemaVersion = PluginSduiValidator.CurrentSchemaVersion,
        PluginPackage = "dsh-opencode-go",
        PluginVersion = "1.2.3",
        Title = "配置自动挂载测试",
        SettingsNamespace = SettingsNamespace,
        Sources = [],
        Tabs =
        [
            new SduiTabSpec
            {
                Id = "overview",
                Label = "概览",
                Blocks =
                [
                    new SduiBlockSpec { Type = SduiBlockTypes.StatusBanner, Title = "概览" },
                ],
            },
        ],
        Actions = [],
    };

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static PluginSduiDocument BuildDocument()
    {
        return new PluginSduiDocument
        {
            SchemaVersion = PluginSduiValidator.CurrentSchemaVersion,
            PluginPackage = "dsh-opencode-go",
            PluginVersion = "1.2.3",
            Title = "OpenCode Go 用量",
            Subtitle = "套餐与配额",
            SettingsNamespace = SettingsNamespace,
            Sources =
            [
                new SduiDataSource
                {
                    Id = "usage",
                    Kind = SduiDataSourceKinds.Rpc,
                    Endpoint = UsageEndpoint,
                    ArgsTemplate = ParseElement("""{"input":{}}"""),
                },
                new SduiDataSource
                {
                    Id = "models",
                    Kind = SduiDataSourceKinds.Rpc,
                    Endpoint = ModelsEndpoint,
                    ArgsTemplate = ParseElement("""{"input":{}}"""),
                },
                new SduiDataSource
                {
                    Id = "analytics",
                    Kind = SduiDataSourceKinds.Rpc,
                    Endpoint = AnalyticsEndpoint,
                    ArgsTemplate = ParseElement("""{"range":{"$bind":"ui.analyticsRange"}}"""),
                },
                new SduiDataSource
                {
                    Id = "prefs",
                    Kind = SduiDataSourceKinds.Settings,
                    Namespace = SettingsNamespace,
                },
            ],
            InitialUiState = ParseElement("""{"analyticsRange":"7d"}"""),
            Tabs =
            [
                new SduiTabSpec
                {
                    Id = "overview",
                    Label = "概览",
                    Blocks =
                    [
                        new SduiBlockSpec
                        {
                            Type = SduiBlockTypes.StatusBanner,
                            Tone = "info",
                            TitleBind = "sources.usage.status",
                        },
                        new SduiBlockSpec
                        {
                            Type = SduiBlockTypes.QuotaBar,
                            Label = "本月用量",
                            PercentBind = "sources.usage.percent",
                            StatusBind = "sources.usage.plan",
                            ResetAtBind = "sources.usage.resetAt",
                        },
                        new SduiBlockSpec
                        {
                            Type = SduiBlockTypes.MetricGrid,
                            Items =
                            [
                                new SduiMetricOrKvItem
                                {
                                    Label = "剩余额度",
                                    ValueBind = "sources.usage.limit",
                                    Unit = "USD",
                                    Format = SduiValueFormats.MicroCentsUsd,
                                    Monospace = true,
                                },
                                new SduiMetricOrKvItem
                                {
                                    Label = "模型数",
                                    ValueBind = "sources.models.count",
                                    Format = SduiValueFormats.Number,
                                },
                            ],
                        },
                        new SduiBlockSpec
                        {
                            Type = SduiBlockTypes.KeyValueList,
                            Items =
                            [
                                new SduiMetricOrKvItem { Label = "套餐", ValueBind = "sources.usage.plan" },
                            ],
                        },
                        new SduiBlockSpec
                        {
                            Type = SduiBlockTypes.SegmentedSwitch,
                            Label = "统计区间",
                            StateKey = "analyticsRange",
                            Options =
                            [
                                new SduiOptionItem { Value = "7d", Label = "7 天" },
                                new SduiOptionItem { Value = "30d", Label = "30 天" },
                            ],
                            OnChangeActionId = "applyRange",
                        },
                        new SduiBlockSpec
                        {
                            Type = SduiBlockTypes.MetricGrid,
                            Items =
                            [
                                new SduiMetricOrKvItem { Label = "统计区间", ValueBind = "sources.analytics.range" },
                                new SduiMetricOrKvItem
                                {
                                    Label = "数据点",
                                    ValueBind = "sources.analytics.points",
                                    Format = SduiValueFormats.Number,
                                },
                            ],
                        },
                        new SduiBlockSpec
                        {
                            Type = SduiBlockTypes.ListSection,
                            Title = "最近调用",
                            ItemsBind = "sources.usage.items",
                            ItemTitleBind = "item.title",
                            ItemSubtitleBind = "item.subtitle",
                            ItemBadgeBind = "item.badge",
                            EmptyText = "暂无记录",
                        },
                        new SduiBlockSpec
                        {
                            Type = SduiBlockTypes.MetricGrid,
                            Items =
                            [
                                new SduiMetricOrKvItem { Label = "接口地址", ValueBind = "sources.prefs.value.apiBase" },
                            ],
                        },
                        new SduiBlockSpec
                        {
                            Type = SduiBlockTypes.SettingsBridge,
                            Title = "账号配置",
                            Namespace = SettingsNamespace,
                        },
                        new SduiBlockSpec
                        {
                            Type = SduiBlockTypes.ActionBar,
                            Actions =
                            [
                                new SduiButtonItem { Label = "添加账号", Style = "primary", ActionId = "addAccount" },
                            ],
                        },
                    ],
                },
            ],
            Actions =
            [
                new SduiActionSpec
                {
                    Id = "applyRange",
                    Kind = SduiActionKinds.RefreshSources,
                    Targets = ["analytics"],
                },
                new SduiActionSpec
                {
                    Id = "addAccount",
                    Kind = SduiActionKinds.OpenForm,
                    Title = "添加账号",
                    SubmitAction = "submitAccount",
                    Fields =
                    [
                        new SduiFormField
                        {
                            Key = "label",
                            Label = "账号名称",
                            Type = SduiFormFieldTypes.Text,
                            Required = true,
                            MaxLength = 64,
                        },
                        new SduiFormField
                        {
                            Key = "password",
                            Label = "密码",
                            Type = SduiFormFieldTypes.Password,
                            Required = true,
                            MaxLength = 128,
                        },
                        new SduiFormField
                        {
                            Key = "token",
                            Label = "访问令牌",
                            Type = SduiFormFieldTypes.SecretInput,
                            Required = true,
                            MaxLength = 128,
                        },
                    ],
                },
                new SduiActionSpec
                {
                    Id = "submitAccount",
                    Kind = SduiActionKinds.Rpc,
                    Endpoint = AccountsEndpoint,
                    PayloadTemplate = ParseElement("""
                    {
                      "command": {
                        "kind": "add",
                        "id": { "$gen": "hex32" },
                        "label": { "$bind": "form.label" },
                        "password": { "$bind": "form.password" },
                        "token": { "$bind": "form.token" },
                        "range": { "$bind": "ui.analyticsRange" }
                      },
                      "input": {}
                    }
                    """),
                },
            ],
        };
    }

    /// <summary>
    /// 可控网关桩：按端点返回固定数据、记录全部 RPC / settings 调用，并可用栅栏证明数据源是并发加载的。
    /// </summary>
    private sealed class FakeSduiGateway : DefaultDshGateway
    {
        private const int ConcurrentSourceCount = 4;
        private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(5);

        private readonly Lock _sync = new();
        private readonly List<RpcCall> _rpcCalls = [];
        private readonly List<MutateCall> _mutations = [];
        private readonly HashSet<string> _failingEndpoints = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private JsonElement _baseValue = ParseElement(
            """{"apiBase":"https://api.example.com/v1","maxAccounts":3}""");

        private JsonElement _userValue = ParseElement("""{}""");

        private long _revision = 1;
        private int _describeCalls;
        private int _sourceLoadCount;
        private int _gateArrivals;
        private int _gateReleased;

        public bool ExpectConcurrentSources { get; init; }

        /// <summary>按端点覆盖 RPC 返回值，用于验证动作结果行与自动摘要。</summary>
        public IReadOnlyDictionary<string, JsonElement>? RpcResultsByEndpoint { get; init; }

        /// <summary>覆盖 describe 返回的 Schemastery schema；用于“无可配置字段则跳过自动挂载”。</summary>
        public string? SettingsSchemaJson { get; init; }

        /// <summary>覆盖 describe 返回的命名空间名；用于“命名空间不存在则跳过自动挂载”。</summary>
        public string? SettingsNamespaceName { get; init; }

        /// <summary>非空时后续 RPC 调用会阻塞直到测试释放，用于验证在途并发守卫。</summary>
        public TaskCompletionSource? RpcGate { get; set; }

        public int InvokeCount(string endpoint)
        {
            lock (_sync)
            {
                return _rpcCalls.Count(call => string.Equals(call.Endpoint, endpoint, StringComparison.Ordinal));
            }
        }

        public IReadOnlyList<RpcCall> RpcCalls
        {
            get
            {
                lock (_sync)
                {
                    return _rpcCalls.ToArray();
                }
            }
        }

        public IReadOnlyList<MutateCall> Mutations
        {
            get
            {
                lock (_sync)
                {
                    return _mutations.ToArray();
                }
            }
        }

        public HashSet<string> FailingEndpoints => _failingEndpoints;

        public int DescribeCalls => Volatile.Read(ref _describeCalls);

        public int SourceLoadCount => Volatile.Read(ref _sourceLoadCount);

        public bool AllSourcesArrivedConcurrently => Volatile.Read(ref _gateReleased) == 1;

        public string? LastAnalyticsRange
        {
            get
            {
                lock (_sync)
                {
                    return _rpcCalls.LastOrDefault(call => call.Endpoint == AnalyticsEndpoint) is { } call &&
                           call.Args.ValueKind == JsonValueKind.Object &&
                           call.Args.TryGetProperty("range", out var range) &&
                           range.ValueKind == JsonValueKind.String
                        ? range.GetString()
                        : null;
                }
            }
        }

        public override async Task<Outcome<JsonElement>> InvokePluginRpcAsync(
            string endpoint,
            JsonElement args,
            CancellationToken ct = default)
        {
            lock (_sync)
            {
                _rpcCalls.Add(new RpcCall(endpoint, args.Clone()));
            }

            Interlocked.Increment(ref _sourceLoadCount);
            if (ExpectConcurrentSources)
            {
                await EnterGateAsync();
            }

            if (RpcGate is { } rpcGate)
            {
                await rpcGate.Task.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            }

            if (_failingEndpoints.Contains(endpoint))
            {
                return Outcome<JsonElement>.Fail(
                    DshFailureCode.Unavailable,
                    PluginSduiViewModel.SourceFailedDiagnostic);
            }

            if (RpcResultsByEndpoint is { } overrides && overrides.TryGetValue(endpoint, out var overridden))
            {
                return Outcome<JsonElement>.Ok(overridden.Clone());
            }

            return endpoint switch
            {
                UsageEndpoint => Outcome<JsonElement>.Ok(ParseElement(UsageResultJson)),
                ModelsEndpoint => Outcome<JsonElement>.Ok(ParseElement("""{"count":3,"default":"deepseek-v4"}""")),
                AnalyticsEndpoint => Outcome<JsonElement>.Ok(ParseElement(
                    $$"""{"range":"{{LastAnalyticsRange ?? "7d"}}","points":12}""")),
                AccountsEndpoint => Outcome<JsonElement>.Ok(ParseElement("""{"ok":true,"message":"已添加"}""")),
                _ => Outcome<JsonElement>.Fail(DshFailureCode.Unsupported, "sdui.unknown_endpoint"),
            };
        }

        public override async Task<Outcome<SettingsDescribeSnapshotView>> DescribeSettingsAsync(
            CancellationToken ct = default)
        {
            var callIndex = Interlocked.Increment(ref _describeCalls);

            // 第一次 describe 来自 settings_bridge 前置拉取（顺序执行），之后才是 settings 数据源并发加载。
            if (ExpectConcurrentSources && callIndex >= 2)
            {
                await EnterGateAsync();
            }

            SettingsNamespaceView view;
            lock (_sync)
            {
                view = CreateNamespaceViewUnlocked();
            }

            return Outcome<SettingsDescribeSnapshotView>.Ok(
                new SettingsDescribeSnapshotView(Writable: true, HasDocument: true, Namespaces: [view]));
        }

        public override Task<Outcome<SettingsNamespaceView>> MutateSettingsAsync(
            string ns,
            IReadOnlyList<SettingsPathOp> ops,
            long? expectedRevision = null,
            CancellationToken ct = default)
        {
            SettingsNamespaceView view;
            lock (_sync)
            {
                _mutations.Add(new MutateCall(ns, ops.ToArray(), expectedRevision));
                ApplyOpsUnlocked(ops);
                view = CreateNamespaceViewUnlocked();
            }

            return Task.FromResult(Outcome<SettingsNamespaceView>.Ok(view));
        }

        private async Task EnterGateAsync()
        {
            if (Interlocked.Increment(ref _gateArrivals) >= ConcurrentSourceCount)
            {
                _gate.TrySetResult();
            }

            var completed = await Task.WhenAny(_gate.Task, Task.Delay(GateTimeout));
            if (ReferenceEquals(completed, _gate.Task))
            {
                Volatile.Write(ref _gateReleased, 1);
            }
        }

        private void ApplyOpsUnlocked(IReadOnlyList<SettingsPathOp> ops)
        {
            var user = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (_userValue.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in _userValue.EnumerateObject())
                {
                    user[property.Name] = property.Value.Clone();
                }
            }

            foreach (var op in ops)
            {
                if (op.Path.Count != 1)
                {
                    continue;
                }

                if (op.Op == SettingsPathOpKind.Set && op.Value is { } value)
                {
                    user[op.Path[0]] = value.Clone();
                }
                else if (op.Op == SettingsPathOpKind.Unset)
                {
                    // unset 只清除用户覆盖；读回时回落到 Base 默认值。
                    user.Remove(op.Path[0]);
                }
            }

            _userValue = JsonSerializer.SerializeToElement(user);
            _revision++;
        }

        private SettingsNamespaceView CreateNamespaceViewUnlocked()
        {
            var value = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (_baseValue.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in _baseValue.EnumerateObject())
                {
                    value[property.Name] = property.Value.Clone();
                }
            }

            if (_userValue.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in _userValue.EnumerateObject())
                {
                    value[property.Name] = property.Value.Clone();
                }
            }

            return new(
                Namespace: SettingsNamespaceName ?? SettingsNamespace,
                AutoGenerate: false,
                Schema: ParseElement(SettingsSchemaJson ?? SchemaJson),
                Value: JsonSerializer.SerializeToElement(value),
                Base: _baseValue.Clone(),
                User: _userValue.Clone(),
                Applies: "live",
                Secrets: [],
                Revision: _revision);
        }

        public sealed record RpcCall(string Endpoint, JsonElement Args);

        public sealed record MutateCall(
            string Namespace,
            IReadOnlyList<SettingsPathOp> Ops,
            long? ExpectedRevision);
    }
}
