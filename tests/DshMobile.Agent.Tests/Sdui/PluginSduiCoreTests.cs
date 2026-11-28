using System.Buffers;
using System.Globalization;
using System.Text.Json;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;
using DshMobile.Agent.Sdui;

namespace DshMobile.Agent.Tests.Sdui;

/// <summary>
/// Slice 1B-2：SDUI 文档模型、校验器、求值器、版本化缓存与生成服务的核心单元测试。
/// 覆盖完整 dsh-opencode-go 样式文档、安全拦截、格式化求值、缓存回退/失效/自愈，以及生成成功与失败路径。
/// </summary>
public sealed class PluginSduiCoreTests : IDisposable
{
    private const string PackageName = "dsh-opencode-go";
    private const string PackageVersion = "1.2.3";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"dshm-sdui-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private const string FullDocumentJson = """
    {
      "schemaVersion": 1,
      "pluginPackage": "dsh-opencode-go",
      "pluginVersion": "1.2.3",
      "title": "OpenCode Go 用量",
      "subtitle": "套餐与配额",
      "settingsNamespace": "opencode-go",
      "sources": [
        {
          "id": "usage",
          "kind": "rpc",
          "endpoint": "openCodeGo/usage",
          "argsTemplate": { "input": {} },
          "refreshIntervalSec": 30,
          "autoLoad": true
        },
        {
          "id": "prefs",
          "kind": "settings",
          "namespace": "opencode-go"
        }
      ],
      "initialUiState": { "range": "7d" },
      "tabs": [
        {
          "id": "overview",
          "label": "概览",
          "blocks": [
            { "type": "status_banner", "tone": "info", "titleBind": "sources.usage.status" },
            {
              "type": "metric_grid",
              "items": [
                {
                  "label": "剩余额度",
                  "valueBind": "sources.usage.limit",
                  "unit": "USD",
                  "format": "microCentsUsd",
                  "monospace": true
                }
              ]
            },
            {
              "type": "quota_bar",
              "label": "本月用量",
              "percentBind": "sources.usage.percent",
              "resetAtBind": "sources.usage.resetAt"
            },
            {
              "type": "key_value_list",
              "items": [ { "label": "套餐", "valueBind": "sources.usage.plan" } ]
            },
            {
              "type": "segmented_switch",
              "stateKey": "range",
              "options": [
                { "value": "7d", "label": "7 天" },
                { "value": "30d", "label": "30 天" }
              ],
              "onChangeActionId": "refresh"
            },
            {
              "type": "list_section",
              "itemsBind": "sources.usage.items",
              "itemTitleBind": "item.title",
              "itemSubtitleBind": "item.subtitle",
              "emptyText": "暂无记录"
            },
            {
              "type": "action_bar",
              "actions": [ { "label": "刷新", "style": "primary", "actionId": "refresh" } ]
            },
            { "type": "external_link", "title": "官方文档", "url": "https://example.com/docs" },
            { "type": "settings_bridge", "title": "配置", "namespace": "opencode-go" }
          ]
        }
      ],
      "actions": [
        { "id": "refresh", "kind": "refresh_sources", "targets": [ "usage" ] },
        {
          "id": "save",
          "kind": "settings_mutate",
          "payloadTemplate": { "namespace": "opencode-go", "ops": [] }
        },
        { "id": "open", "kind": "open_url", "url": "https://example.com" },
        {
          "id": "feedback",
          "kind": "open_form",
          "title": "反馈",
          "fields": [ { "key": "message", "label": "内容", "type": "text", "maxLength": 500 } ],
          "submitAction": "save"
        },
        { "id": "pickRange", "kind": "set_ui_state", "stateKey": "range", "stateValue": "7d" }
      ]
    }
    """;

    [Fact]
    public void FullDocument_DeserializesValidatesAndSourceGenSerializes()
    {
        var document = JsonSerializer.Deserialize(FullDocumentJson, PluginSduiJsonContext.Default.PluginSduiDocument);

        Assert.NotNull(document);
        var validation = PluginSduiValidator.Validate(document);
        Assert.True(validation.IsValid, string.Join("；", validation.Errors));

        Assert.Equal(1, document!.SchemaVersion);
        Assert.Equal(PackageName, document.PluginPackage);
        Assert.Equal(PackageVersion, document.PluginVersion);
        Assert.Equal(2, document.Sources.Count);
        Assert.Single(document.Tabs);
        Assert.Equal(9, document.Tabs[0].Blocks.Count);
        Assert.Equal("save", document.Actions.Single(a => a.Kind == SduiActionKinds.OpenForm).SubmitAction);
        Assert.Equal("openCodeGo/usage", document.Sources[0].Endpoint);

        var serialized = JsonSerializer.Serialize(document, PluginSduiJsonContext.Default.PluginSduiDocument);
        Assert.Contains("\"schemaVersion\":1", serialized, StringComparison.Ordinal);
        Assert.Contains("\"pluginPackage\":\"dsh-opencode-go\"", serialized, StringComparison.Ordinal);
        Assert.Contains("\"microCentsUsd\"", serialized, StringComparison.Ordinal);
        Assert.Contains("\"submitAction\":\"save\"", serialized, StringComparison.Ordinal);

        var roundTrip = JsonSerializer.Deserialize(serialized, PluginSduiJsonContext.Default.PluginSduiDocument);
        Assert.NotNull(roundTrip);
        Assert.True(PluginSduiValidator.Validate(roundTrip).IsValid);
        Assert.Equal(9, roundTrip!.Tabs[0].Blocks.Count);
    }

    [Theory]
    [InlineData("credentials/set")]
    [InlineData("settings/mutate")]
    [InlineData("session/prompt")]
    public void Validator_RejectsReservedRpcEndpoints(string endpoint)
    {
        var fromSource = PluginSduiValidator.Validate(CreateValidDocument(sourceEndpoint: endpoint));
        Assert.False(fromSource.IsValid);
        Assert.Contains(fromSource.Errors, error => error.Contains("endpoint", StringComparison.Ordinal));

        var fromAction = PluginSduiValidator.Validate(CreateValidDocument(actionEndpoint: endpoint));
        Assert.False(fromAction.IsValid);
        Assert.Contains(fromAction.Errors, error => error.Contains("endpoint", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("sources.usage[0]")]
    [InlineData("sources.(evil)")]
    [InlineData("script:alert(1)")]
    [InlineData("secrets.token")]
    public void Validator_RejectsIllegalBindPaths(string bindPath)
    {
        var document = CreateValidDocument(payloadTemplate: ParseElement($$"""{"$bind":{{JsonSerializer.Serialize(bindPath)}}}"""));

        var result = PluginSduiValidator.Validate(document);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("$bind", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("random")]
    [InlineData("hex64")]
    [InlineData("")]
    public void Validator_RejectsIllegalGenerators(string generator)
    {
        var document = CreateValidDocument(
            payloadTemplate: ParseElement($$"""{"$gen":{{JsonSerializer.Serialize(generator)}}}"""));

        var result = PluginSduiValidator.Validate(document);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("$gen", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://example.com/docs")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/docs")]
    public void Validator_RejectsNonHttpsUrls(string url)
    {
        var fromBlock = PluginSduiValidator.Validate(CreateValidDocument(externalUrl: url));
        Assert.False(fromBlock.IsValid);
        Assert.Contains(fromBlock.Errors, error => error.Contains("url", StringComparison.Ordinal));

        var fromAction = PluginSduiValidator.Validate(CreateValidDocument(openUrl: url));
        Assert.False(fromAction.IsValid);
        Assert.Contains(fromAction.Errors, error => error.Contains("url", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsTooManyTabsAndBlocks()
    {
        var tooManyTabs = PluginSduiValidator.Validate(CreateValidDocument(tabCount: PluginSduiValidator.MaxTabs + 1));
        Assert.False(tooManyTabs.IsValid);
        Assert.Contains(tooManyTabs.Errors, error => error.Contains("tabs 数量", StringComparison.Ordinal));

        // 基础文档已有 9 个区块，再补 60 个即超过 64 上限。
        var tooManyBlocks = PluginSduiValidator.Validate(CreateValidDocument(extraBlocks: 60));
        Assert.False(tooManyBlocks.IsValid);
        Assert.Contains(tooManyBlocks.Errors, error => error.Contains("区块总数", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsIllegalPackageName()
    {
        var result = PluginSduiValidator.Validate(CreateValidDocument(packageName: "../evil"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("pluginPackage", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluator_ResolvesPathsFormatsValuesAndMaterializesGenerators()
    {
        using var sourcesDocument = JsonDocument.Parse("""
        {
          "usage": {
            "limit": "150000000",
            "percent": "42.5",
            "count": 1234,
            "plan": "Pro",
            "items": [ { "title": "A" }, { "title": "B" } ]
          }
        }
        """);
        var context = new SduiEvalContext { Sources = sourcesDocument.RootElement };

        var limit = PluginSduiEvaluator.ResolvePath("sources.usage.limit", context);
        Assert.Equal("$1.50", PluginSduiEvaluator.FormatValue(limit, SduiValueFormats.MicroCentsUsd));

        var count = PluginSduiEvaluator.ResolvePath("sources.usage.count", context);
        Assert.Equal("1,234", PluginSduiEvaluator.FormatValue(count, SduiValueFormats.Number));

        Assert.Equal("2", PluginSduiEvaluator.FormatValue(PluginSduiEvaluator.ResolvePath("sources.usage.items.length", context)));
        Assert.Equal("B", PluginSduiEvaluator.FormatValue(PluginSduiEvaluator.ResolvePath("sources.usage.items.1.title", context)));
        Assert.Null(PluginSduiEvaluator.ResolvePath("sources.usage.items.9.title", context));

        using var timeDocument = JsonDocument.Parse("""{"at":"2024-01-01T00:00:00Z"}""");
        var timeContext = new SduiEvalContext { Sources = timeDocument.RootElement };
        var relative = PluginSduiEvaluator.FormatValue(
            PluginSduiEvaluator.ResolvePath("sources.at", timeContext),
            SduiValueFormats.RelativeTime,
            now: DateTimeOffset.Parse("2024-01-01T02:00:00Z", CultureInfo.InvariantCulture));
        Assert.Equal("2 小时前", relative);

        using var templateDocument = JsonDocument.Parse("""
        {
          "nonce": { "$gen": "hex32" },
          "id": { "$gen": "uuid" },
          "value": { "$bind": "sources.usage.plan" }
        }
        """);
        var materialized = PluginSduiEvaluator.MaterializeTemplate(
            templateDocument.RootElement,
            context,
            () => new string('a', 32),
            () => "00000000-0000-0000-0000-000000000000");

        Assert.NotNull(materialized);
        var nonce = materialized!.Value.GetProperty("nonce").GetString();
        Assert.NotNull(nonce);
        Assert.Equal(32, nonce!.Length);
        Assert.Equal("00000000-0000-0000-0000-000000000000", materialized.Value.GetProperty("id").GetString());
        Assert.Equal("Pro", materialized.Value.GetProperty("value").GetString());
    }

    [Fact]
    public void Evaluator_FormatsThirteenDigitMillisecondTimestampAsRelativeTime()
    {
        // JavaScript Date.now() 风格的 13 位毫秒时间戳（2025-11-28T05:26:40Z）。
        using var timeDocument = JsonDocument.Parse("""{"at":1764310000000}""");
        var target = timeDocument.RootElement.GetProperty("at");
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_764_310_000_000 + (2 * 60 * 60 * 1000));

        var exception = Record.Exception(() =>
            PluginSduiEvaluator.FormatValue(target, SduiValueFormats.RelativeTime, now: now));

        Assert.Null(exception);
        Assert.Equal(
            "2 小时前",
            PluginSduiEvaluator.FormatValue(target, SduiValueFormats.RelativeTime, now: now));
    }

    [Fact]
    public void Evaluator_FallsBackToRawTextForOutOfRangeTimestampWithoutThrowing()
    {
        using var timeDocument = JsonDocument.Parse("""{"at":999999999999999999}""");
        var target = timeDocument.RootElement.GetProperty("at");

        var exception = Record.Exception(() =>
            PluginSduiEvaluator.FormatValue(target, SduiValueFormats.RelativeTime));

        Assert.Null(exception);
        Assert.Equal(
            "999999999999999999",
            PluginSduiEvaluator.FormatValue(target, SduiValueFormats.RelativeTime));
    }

    [Fact]
    public async Task Store_FallsBackToManifestAndThenHitsVersionedCache()
    {
        var manifest = JsonSerializer.Serialize(CreateValidDocument(), PluginSduiJsonContext.Default.PluginSduiDocument);
        var store = CreateStore(manifest);

        var first = await store.LoadAsync(PackageName, PackageVersion);

        Assert.True(first.Found);
        Assert.Equal(PluginSduiStore.ManifestSource, first.Source);
        Assert.Equal(PackageVersion, first.Document!.PluginVersion);
        Assert.NotNull(first.Document);

        var cachePath = store.TryGetCacheFilePath(PackageName, PackageVersion);
        Assert.NotNull(cachePath);
        Assert.True(File.Exists(cachePath));

        var second = await store.LoadAsync(PackageName, PackageVersion);
        Assert.True(second.Found);
        Assert.Equal(PluginSduiStore.CacheSource, second.Source);
    }

    [Fact]
    public async Task Store_VersionUpgradeDoesNotReuseOldCache()
    {
        var store = CreateStore();
        var saved = await store.SaveGeneratedAsync(CreateValidDocument(pluginVersion: "1.0.0"), PackageName, "1.0.0");
        Assert.True(saved.Found);

        var upgradedManifest = JsonSerializer.Serialize(
            CreateValidDocument(pluginVersion: "2.0.0"),
            PluginSduiJsonContext.Default.PluginSduiDocument);
        var upgradedStore = new PluginSduiStore(_tempDir, new StubPluginOperations { ManifestJson = upgradedManifest });

        var loaded = await upgradedStore.LoadAsync(PackageName, "2.0.0");

        Assert.True(loaded.Found);
        Assert.Equal(PluginSduiStore.ManifestSource, loaded.Source);
        Assert.Equal("2.0.0", loaded.Document!.PluginVersion);
        Assert.NotEqual("1.0.0", loaded.Document.PluginVersion);
    }

    [Fact]
    public async Task Store_DeletesCorruptedCacheAndFallsBackToManifest()
    {
        var store = CreateStore();
        var cachePath = store.TryGetCacheFilePath(PackageName, "1.0.0");
        Assert.NotNull(cachePath);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        await File.WriteAllTextAsync(cachePath, "{ this is not valid json", CancellationToken.None);

        var manifest = JsonSerializer.Serialize(
            CreateValidDocument(pluginVersion: "2.0.0"),
            PluginSduiJsonContext.Default.PluginSduiDocument);
        var reloadedStore = new PluginSduiStore(_tempDir, new StubPluginOperations { ManifestJson = manifest });

        var result = await reloadedStore.LoadAsync(PackageName, "1.0.0");

        Assert.True(result.Found);
        Assert.Equal(PluginSduiStore.ManifestSource, result.Source);
        Assert.False(File.Exists(cachePath));
    }

    [Fact]
    public async Task SaveGenerated_RejectsMismatchedPackageAndDoesNotWriteCache()
    {
        var store = CreateStore();

        var result = await store.SaveGeneratedAsync(CreateValidDocument(), "another-package", PackageVersion);

        Assert.False(result.Found);
        Assert.Equal(PluginSduiStore.InvalidDocumentDiagnostic, result.DiagnosticKey);
        var cachePath = store.TryGetCacheFilePath(PackageName, PackageVersion);
        Assert.False(File.Exists(cachePath));
    }

    [Fact]
    public async Task Generator_SavesFencedJsonAndHidesAndArchivesSession()
    {
        var validJson = JsonSerializer.Serialize(CreateValidDocument(), PluginSduiJsonContext.Default.PluginSduiDocument);
        var gateway = new FakeSduiGateway
        {
            AssistantText = "已完成分析：\n```json\n" + validJson + "\n```\n",
        };
        var hiddenStore = new HiddenSessionStore(_tempDir);
        var store = CreateStore();
        var service = new PluginSduiGeneratorService(gateway, store, hiddenStore, TimeSpan.FromSeconds(5));

        var outcome = await service.GenerateAsync(PackageName, PackageVersion, "opencode-go");

        Assert.True(outcome.IsOk, outcome.Error?.DiagnosticKey);
        Assert.NotNull(outcome.Value);
        Assert.Equal(PackageName, outcome.Value!.PluginPackage);
        Assert.Equal(PackageVersion, outcome.Value.PluginVersion);

        Assert.True(hiddenStore.IsHidden(FakeSduiGateway.SessionId));
        Assert.Contains(FakeSduiGateway.SessionId, gateway.ArchivedSessionIds);
        Assert.Equal(1, gateway.CreateCalls);

        var prompt = Assert.Single(gateway.SentPrompts).Text;
        Assert.Contains("node_modules/" + PackageName, prompt, StringComparison.Ordinal);
        Assert.Contains("schemaVersion", prompt, StringComparison.Ordinal);
        Assert.Contains("submitAction", prompt, StringComparison.Ordinal);
        Assert.Contains("$bind", prompt, StringComparison.Ordinal);
        Assert.Contains("$gen", prompt, StringComparison.Ordinal);
        Assert.Contains("credentials", prompt, StringComparison.Ordinal);

        var cachePath = store.TryGetCacheFilePath(PackageName, PackageVersion);
        Assert.NotNull(cachePath);
        Assert.True(File.Exists(cachePath));
    }

    [Fact]
    public async Task Generator_RetriesOnceInSameSessionWhenFirstOutputFailsValidation()
    {
        var invalidJson = JsonSerializer.Serialize(
            CreateValidDocument(actionEndpoint: "credentials/set"),
            PluginSduiJsonContext.Default.PluginSduiDocument);
        var validJson = JsonSerializer.Serialize(CreateValidDocument(), PluginSduiJsonContext.Default.PluginSduiDocument);
        var gateway = new FakeSduiGateway
        {
            AssistantTexts = [invalidJson, validJson],
        };
        var hiddenStore = new HiddenSessionStore(_tempDir);
        var store = CreateStore();
        var service = new PluginSduiGeneratorService(gateway, store, hiddenStore, TimeSpan.FromSeconds(5));

        var result = await service.GenerateDetailedAsync(PackageName, PackageVersion, "opencode-go");

        Assert.True(result.Found, result.DiagnosticKey);
        Assert.NotNull(result.Document);
        Assert.Equal(PackageName, result.Document!.PluginPackage);
        Assert.Equal(2, gateway.SentPrompts.Count);
        Assert.All(gateway.SentPrompts, prompt => Assert.Equal(FakeSduiGateway.SessionId, prompt.SessionId));
        Assert.Equal(1, gateway.CreateCalls);
        Assert.Equal(2, gateway.LoadEventsCalls);
        Assert.Contains("actions[", gateway.SentPrompts[1].Text, StringComparison.Ordinal);
        Assert.Contains("endpoint", gateway.SentPrompts[1].Text, StringComparison.Ordinal);
        Assert.Contains(FakeSduiGateway.SessionId, gateway.ArchivedSessionIds);

        var cachePath = store.TryGetCacheFilePath(PackageName, PackageVersion);
        Assert.NotNull(cachePath);
        Assert.True(File.Exists(cachePath));
    }

    [Fact]
    public async Task Generator_ReturnsParseFailureForInvalidJsonAndWritesNothing()
    {
        var gateway = new FakeSduiGateway
        {
            AssistantText = "```json\n{ this is not json }\n```",
        };
        var hiddenStore = new HiddenSessionStore(_tempDir);
        var store = CreateStore();
        var service = new PluginSduiGeneratorService(gateway, store, hiddenStore, TimeSpan.FromSeconds(5));

        var outcome = await service.GenerateAsync(PackageName, PackageVersion);

        Assert.False(outcome.IsOk);
        Assert.Equal(PluginSduiGeneratorService.JsonParseFailedDiagnostic, outcome.Error!.DiagnosticKey);
        Assert.True(hiddenStore.IsHidden(FakeSduiGateway.SessionId));
        Assert.Contains(FakeSduiGateway.SessionId, gateway.ArchivedSessionIds);

        var cachePath = store.TryGetCacheFilePath(PackageName, PackageVersion);
        Assert.False(File.Exists(cachePath));
    }

    [Fact]
    public async Task Generator_ReturnsSchemaFailureForReservedEndpointAndWritesNothing()
    {
        var invalidDocument = CreateValidDocument(actionEndpoint: "credentials/set");
        var invalidJson = JsonSerializer.Serialize(invalidDocument, PluginSduiJsonContext.Default.PluginSduiDocument);
        var gateway = new FakeSduiGateway
        {
            AssistantText = "```json\n" + invalidJson + "\n```",
        };
        var hiddenStore = new HiddenSessionStore(_tempDir);
        var store = CreateStore();
        var service = new PluginSduiGeneratorService(gateway, store, hiddenStore, TimeSpan.FromSeconds(5));

        var outcome = await service.GenerateAsync(PackageName, PackageVersion);

        Assert.False(outcome.IsOk);
        Assert.Equal(PluginSduiGeneratorService.SchemaInvalidDiagnostic, outcome.Error!.DiagnosticKey);
        Assert.Contains(FakeSduiGateway.SessionId, gateway.ArchivedSessionIds);

        var cachePath = store.TryGetCacheFilePath(PackageName, PackageVersion);
        Assert.False(File.Exists(cachePath));
    }

    [Fact]
    public async Task Generator_FailsClosedWhenPromptCapabilityIsMissing()
    {
        var gateway = new FakeSduiGateway { PromptCapability = false };
        var store = CreateStore();
        var service = new PluginSduiGeneratorService(gateway, store);

        var outcome = await service.GenerateAsync(PackageName);

        Assert.False(outcome.IsOk);
        Assert.Equal(PluginSduiGeneratorService.GeneratorUnavailableDiagnostic, outcome.Error!.DiagnosticKey);
        Assert.Equal(0, gateway.CreateCalls);
    }

    [Fact]
    public async Task Generator_CancelsAndFailsWhenInputIsRequired()
    {
        var gateway = new FakeSduiGateway
        {
            AssistantText = "```json\n{}\n```",
            WaitKind = SessionWaitOutcomeKind.InputRequired,
        };
        var hiddenStore = new HiddenSessionStore(_tempDir);
        var store = CreateStore();
        var service = new PluginSduiGeneratorService(gateway, store, hiddenStore, TimeSpan.FromSeconds(5));

        var outcome = await service.GenerateAsync(PackageName);

        Assert.False(outcome.IsOk);
        Assert.Equal(PluginSduiGeneratorService.InputRequiredDiagnostic, outcome.Error!.DiagnosticKey);
        Assert.Equal(1, gateway.CancelCalls);
        Assert.Contains(FakeSduiGateway.SessionId, gateway.ArchivedSessionIds);
    }

    [Fact]
    public async Task Generator_ReturnsAgentReportedErrorImmediatelyWithoutRepairOrCache()
    {
        var gateway = new FakeSduiGateway
        {
            AssistantText = """{"error":"package not found"}""",
        };
        var hiddenStore = new HiddenSessionStore(_tempDir);
        var store = CreateStore();
        var service = new PluginSduiGeneratorService(gateway, store, hiddenStore, TimeSpan.FromSeconds(5));

        var result = await service.GenerateDetailedAsync(PackageName, PackageVersion, "opencode-go");

        Assert.False(result.Found);
        Assert.Equal(PluginSduiGeneratorService.AgentReportedErrorDiagnostic, result.DiagnosticKey);
        var validationError = Assert.Single(result.ValidationErrors!);
        Assert.Contains("package not found", validationError, StringComparison.Ordinal);

        // 显式报错不是 Schema 问题：不追加纠偏轮次，也不写入缓存。
        Assert.Single(gateway.SentPrompts);
        Assert.Equal(1, gateway.LoadEventsCalls);
        Assert.Equal(1, gateway.CreateCalls);
        Assert.Contains(FakeSduiGateway.SessionId, gateway.ArchivedSessionIds);

        var cachePath = store.TryGetCacheFilePath(PackageName, PackageVersion);
        Assert.False(File.Exists(cachePath));
    }

    [Fact]
    public async Task Generator_ProbeInputInvalidTriggersRepairInSameSessionAndSaves()
    {
        var validJson = JsonSerializer.Serialize(
            CreateValidDocument(sourceArgsTemplate: ParseElement(
                """{"input":{"range":{"$bind":"ui.range","$default":"7d"}}}""")),
            PluginSduiJsonContext.Default.PluginSduiDocument);
        var gateway = new FakeSduiGateway
        {
            AssistantText = "```json\n" + validJson + "\n```",
            PluginRpcResults = new Queue<Outcome<JsonElement>>(new[]
            {
                Outcome<JsonElement>.Fail(DshFailureCode.Unsupported, "gateway/input-invalid"),
                Outcome<JsonElement>.Ok(ParseElement("""{"usage":1}""")),
            }),
        };
        var hiddenStore = new HiddenSessionStore(_tempDir);
        var store = CreateStore();
        var service = new PluginSduiGeneratorService(gateway, store, hiddenStore, TimeSpan.FromSeconds(5));

        var result = await service.GenerateDetailedAsync(PackageName, PackageVersion, "opencode-go");

        Assert.True(result.Found, result.DiagnosticKey);
        Assert.Equal(1, gateway.CreateCalls);
        Assert.Equal(2, gateway.SentPrompts.Count);
        Assert.Contains("gateway/input-invalid", gateway.SentPrompts[1].Text, StringComparison.Ordinal);
        Assert.Contains("$default", gateway.SentPrompts[1].Text, StringComparison.Ordinal);
        Assert.Equal(2, gateway.PluginRpcCalls.Count);
        Assert.All(gateway.PluginRpcCalls, call => Assert.Equal("openCodeGo/usage", call.Endpoint));
        Assert.Equal(
            "7d",
            gateway.PluginRpcCalls[0].Args.GetProperty("input").GetProperty("range").GetString());
        Assert.True(hiddenStore.IsHidden(FakeSduiGateway.SessionId));
        Assert.Contains(FakeSduiGateway.SessionId, gateway.ArchivedSessionIds);

        var cachePath = store.TryGetCacheFilePath(PackageName, PackageVersion);
        Assert.NotNull(cachePath);
        Assert.True(File.Exists(cachePath));
    }

    [Fact]
    public async Task Generator_ProbeBusinessErrorDoesNotRepairAndSaves()
    {
        var validJson = JsonSerializer.Serialize(
            CreateValidDocument(),
            PluginSduiJsonContext.Default.PluginSduiDocument);
        var gateway = new FakeSduiGateway
        {
            AssistantText = "```json\n" + validJson + "\n```",
            PluginRpcResults = new Queue<Outcome<JsonElement>>(new[]
            {
                Outcome<JsonElement>.Fail(DshFailureCode.Unsupported, "opencode-go/usage-unavailable"),
            }),
        };
        var store = CreateStore();
        var service = new PluginSduiGeneratorService(gateway, store, new HiddenSessionStore(_tempDir), TimeSpan.FromSeconds(5));

        var result = await service.GenerateDetailedAsync(PackageName, PackageVersion, "opencode-go");

        Assert.True(result.Found, result.DiagnosticKey);
        Assert.Single(gateway.SentPrompts);
        Assert.Single(gateway.PluginRpcCalls);
        Assert.Equal(1, gateway.LoadEventsCalls);

        var cachePath = store.TryGetCacheFilePath(PackageName, PackageVersion);
        Assert.NotNull(cachePath);
        Assert.True(File.Exists(cachePath));
    }

    [Fact]
    public async Task Generator_ProbeNeverInvokesActionEndpoints()
    {
        const string ActionEndpoint = "opencodeGoAccounts/execute";
        var validJson = JsonSerializer.Serialize(
            CreateValidDocument(actionEndpoint: ActionEndpoint),
            PluginSduiJsonContext.Default.PluginSduiDocument);
        var gateway = new FakeSduiGateway
        {
            AssistantText = "```json\n" + validJson + "\n```",
            PluginRpcResults = new Queue<Outcome<JsonElement>>(new[]
            {
                Outcome<JsonElement>.Ok(ParseElement("""{"usage":1}""")),
            }),
        };
        var store = CreateStore();
        var service = new PluginSduiGeneratorService(gateway, store, new HiddenSessionStore(_tempDir), TimeSpan.FromSeconds(5));

        var result = await service.GenerateDetailedAsync(PackageName, PackageVersion, "opencode-go");

        Assert.True(result.Found, result.DiagnosticKey);
        var call = Assert.Single(gateway.PluginRpcCalls);
        Assert.Equal("openCodeGo/usage", call.Endpoint);
        Assert.DoesNotContain(gateway.PluginRpcCalls, entry => entry.Endpoint == ActionEndpoint);
    }

    [Fact]
    public async Task Generator_ProbeKeepsFailingAfterMaxRepairRoundsAndDoesNotWriteCache()
    {
        var validJson = JsonSerializer.Serialize(
            CreateValidDocument(),
            PluginSduiJsonContext.Default.PluginSduiDocument);
        var gateway = new FakeSduiGateway
        {
            AssistantText = "```json\n" + validJson + "\n```",
            PluginRpcResults = new Queue<Outcome<JsonElement>>(new[]
            {
                Outcome<JsonElement>.Fail(DshFailureCode.Unsupported, "gateway/input-invalid"),
                Outcome<JsonElement>.Fail(DshFailureCode.Unsupported, "gateway/invalid-input"),
                Outcome<JsonElement>.Fail(DshFailureCode.Unsupported, "gateway/validation-failed"),
            }),
        };
        var store = CreateStore();
        var service = new PluginSduiGeneratorService(gateway, store, new HiddenSessionStore(_tempDir), TimeSpan.FromSeconds(5));

        var result = await service.GenerateDetailedAsync(PackageName, PackageVersion, "opencode-go");

        Assert.False(result.Found);
        Assert.Equal(PluginSduiGeneratorService.ProbeFailedDiagnostic, result.DiagnosticKey);
        Assert.NotNull(result.ValidationErrors);
        Assert.NotEmpty(result.ValidationErrors!);
        Assert.Equal(3, gateway.SentPrompts.Count);
        Assert.Equal(3, gateway.PluginRpcCalls.Count);
        Assert.Contains(FakeSduiGateway.SessionId, gateway.ArchivedSessionIds);

        var cachePath = store.TryGetCacheFilePath(PackageName, PackageVersion);
        Assert.False(File.Exists(cachePath));
    }

    [Fact]
    public async Task GenerationCoordinator_CompletesAfterPageDetached_AndSnapshotExposesSuccess()
    {
        var validJson = JsonSerializer.Serialize(CreateValidDocument(), PluginSduiJsonContext.Default.PluginSduiDocument);
        var gateway = new FakeSduiGateway
        {
            AssistantText = "```json\n" + validJson + "\n```",
            CreateDelay = TimeSpan.FromMilliseconds(100),
        };
        var store = CreateStore();
        using var coordinator = new PluginSduiGenerationCoordinator(
            gateway,
            store,
            new HiddenSessionStore(_tempDir),
            TimeSpan.FromSeconds(10));
        var events = new List<PluginSduiGenerationSnapshot>();
        coordinator.StateChanged += snapshot =>
        {
            lock (events)
            {
                events.Add(snapshot);
            }
        };

        // 页面只发起生成后即“分离”：不等待、不取消协调器任务，后台仍应继续完成。
        var task = coordinator.StartOrJoinAsync(PackageName, PackageVersion, "opencode-go");
        var inFlight = coordinator.GetSnapshot(PackageName);
        Assert.True(inFlight.IsGenerating);
        Assert.Null(inFlight.Result);

        var completed = await task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(completed.IsGenerating);
        Assert.NotNull(completed.Result);
        Assert.True(completed.Result!.Found, completed.Result.DiagnosticKey);
        Assert.Equal(PackageName, completed.Result.Document!.PluginPackage);
        Assert.Equal(PluginSduiStore.CacheSource, completed.Result.Source);
        Assert.NotNull(completed.UpdatedAtUtc);

        var snapshot = coordinator.GetSnapshot(PackageName);
        Assert.False(snapshot.IsGenerating);
        Assert.True(snapshot.Result!.Found);
        Assert.NotNull(snapshot.UpdatedAtUtc);
        Assert.True(File.Exists(store.TryGetCacheFilePath(PackageName, PackageVersion)));

        PluginSduiGenerationSnapshot[] published;
        lock (events)
        {
            published = events.ToArray();
        }

        Assert.NotEmpty(published);
        Assert.True(published[0].IsGenerating);
        Assert.False(published[^1].IsGenerating);
        Assert.True(published[^1].Result!.Found);
    }

    [Fact]
    public async Task GenerationCoordinator_ConcurrentStartOrJoinSharesOneTaskAndOneHelperSession()
    {
        var validJson = JsonSerializer.Serialize(CreateValidDocument(), PluginSduiJsonContext.Default.PluginSduiDocument);
        var gateway = new FakeSduiGateway
        {
            AssistantText = "```json\n" + validJson + "\n```",
            CreateDelay = TimeSpan.FromMilliseconds(100),
        };
        var store = CreateStore();
        using var coordinator = new PluginSduiGenerationCoordinator(
            gateway,
            store,
            new HiddenSessionStore(_tempDir),
            TimeSpan.FromSeconds(10));

        var first = coordinator.StartOrJoinAsync(PackageName, PackageVersion, "opencode-go");
        var second = coordinator.StartOrJoinAsync(PackageName, PackageVersion, "opencode-go");

        Assert.Same(first, second);

        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(results, result => Assert.True(result.Result!.Found));
        Assert.Equal(1, gateway.CreateCalls);
        Assert.Single(gateway.SentPrompts);
        Assert.Contains(FakeSduiGateway.SessionId, gateway.ArchivedSessionIds);
    }

    [Fact]
    public async Task GenerationCoordinator_ClearSnapshotResetsOnlyTargetPackage()
    {
        const string otherPackage = "dsh-other-plugin";
        var firstJson = JsonSerializer.Serialize(CreateValidDocument(), PluginSduiJsonContext.Default.PluginSduiDocument);
        var secondJson = JsonSerializer.Serialize(
            CreateValidDocument(packageName: otherPackage),
            PluginSduiJsonContext.Default.PluginSduiDocument);
        var gateway = new FakeSduiGateway
        {
            AssistantTexts = [firstJson, secondJson],
        };
        var store = CreateStore();
        using var coordinator = new PluginSduiGenerationCoordinator(
            gateway,
            store,
            new HiddenSessionStore(_tempDir),
            TimeSpan.FromSeconds(10));

        var first = await coordinator.StartOrJoinAsync(PackageName, PackageVersion).WaitAsync(TimeSpan.FromSeconds(10));
        var second = await coordinator.StartOrJoinAsync(otherPackage, PackageVersion).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(first.Result!.Found);
        Assert.True(second.Result!.Found);

        PluginSduiGenerationSnapshot? clearedEvent = null;
        coordinator.StateChanged += snapshot =>
        {
            if (string.Equals(snapshot.PackageName, PackageName, StringComparison.Ordinal) &&
                snapshot.Result is null)
            {
                clearedEvent = snapshot;
            }
        };

        coordinator.ClearSnapshot(PackageName);

        var cleared = coordinator.GetSnapshot(PackageName);
        Assert.False(cleared.IsGenerating);
        Assert.Null(cleared.Result);
        Assert.NotNull(clearedEvent);
        Assert.False(clearedEvent!.IsGenerating);
        Assert.Null(clearedEvent.Result);

        // 其它包的快照完全不受影响。
        var untouched = coordinator.GetSnapshot(otherPackage);
        Assert.False(untouched.IsGenerating);
        Assert.True(untouched.Result!.Found);
        Assert.Equal(otherPackage, untouched.Result.Document!.PluginPackage);
    }

    [Fact]
    public async Task GenerationCoordinator_ConvergesUnexpectedExceptionToStableDiagnostic()
    {
        var gateway = new FakeSduiGateway { ThrowOnCreate = true };
        var store = CreateStore();
        using var coordinator = new PluginSduiGenerationCoordinator(
            gateway,
            store,
            new HiddenSessionStore(_tempDir),
            TimeSpan.FromSeconds(10));

        var snapshot = await coordinator.StartOrJoinAsync(PackageName, PackageVersion)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(snapshot.IsGenerating);
        Assert.NotNull(snapshot.Result);
        Assert.False(snapshot.Result!.Found);
        Assert.Equal(PluginSduiGeneratorService.WaitFailedDiagnostic, snapshot.Result.DiagnosticKey);
        Assert.Null(snapshot.Result.Document);
        Assert.Null(snapshot.Result.ValidationErrors);
        Assert.DoesNotContain(
            "secret internal gateway failure",
            JsonSerializer.Serialize(snapshot),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerationCoordinator_DisposeCancelsInFlightGenerationAndSettlesSnapshot()
    {
        var gateway = new FakeSduiGateway { CreateDelay = TimeSpan.FromSeconds(30) };
        var store = CreateStore();
        var coordinator = new PluginSduiGenerationCoordinator(
            gateway,
            store,
            new HiddenSessionStore(_tempDir),
            TimeSpan.FromSeconds(30));

        var task = coordinator.StartOrJoinAsync(PackageName, PackageVersion);
        Assert.True(coordinator.GetSnapshot(PackageName).IsGenerating);

        coordinator.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await task.WaitAsync(TimeSpan.FromSeconds(10)));

        var snapshot = coordinator.GetSnapshot(PackageName);
        Assert.False(snapshot.IsGenerating);
        Assert.Null(snapshot.Result);
        Assert.NotNull(snapshot.UpdatedAtUtc);
    }

    private PluginSduiStore CreateStore(string? manifestJson = null) =>
        new(_tempDir, new StubPluginOperations { ManifestJson = manifestJson });

    private static PluginSduiDocument CreateValidDocument(
        string packageName = PackageName,
        string pluginVersion = PackageVersion,
        int tabCount = 1,
        int extraBlocks = 0,
        string sourceEndpoint = "openCodeGo/usage",
        string actionEndpoint = "openCodeGo/run",
        string externalUrl = "https://example.com/docs",
        string openUrl = "https://example.com",
        JsonElement? payloadTemplate = null,
        JsonElement? sourceArgsTemplate = null)
    {
        var tabs = new List<SduiTabSpec>();
        for (var tabIndex = 0; tabIndex < tabCount; tabIndex++)
        {
            var blocks = new List<SduiBlockSpec>();
            if (tabIndex == 0)
            {
                blocks.AddRange(CreateAllBlockTypes(externalUrl));
                for (var extra = 0; extra < extraBlocks; extra++)
                {
                    blocks.Add(new SduiBlockSpec
                    {
                        Type = SduiBlockTypes.StatusBanner,
                        TitleBind = "sources.usage.status",
                    });
                }
            }

            tabs.Add(new SduiTabSpec
            {
                Id = $"tab-{tabIndex}",
                Label = $"标签 {tabIndex}",
                Blocks = blocks,
            });
        }

        return new PluginSduiDocument
        {
            SchemaVersion = PluginSduiValidator.CurrentSchemaVersion,
            PluginPackage = packageName,
            PluginVersion = pluginVersion,
            Title = "OpenCode Go 用量",
            Subtitle = "套餐与配额",
            SettingsNamespace = "opencode-go",
            Sources =
            [
                new SduiDataSource
                {
                    Id = "usage",
                    Kind = SduiDataSourceKinds.Rpc,
                    Endpoint = sourceEndpoint,
                    ArgsTemplate = sourceArgsTemplate ?? ParseElement("""{"input":{}}"""),
                    RefreshIntervalSec = 30,
                },
                new SduiDataSource
                {
                    Id = "prefs",
                    Kind = SduiDataSourceKinds.Settings,
                    Namespace = "opencode-go",
                },
            ],
            InitialUiState = ParseElement("""{"range":"7d"}"""),
            Tabs = tabs,
            Actions =
            [
                new SduiActionSpec
                {
                    Id = "refresh",
                    Kind = SduiActionKinds.RefreshSources,
                    Targets = ["usage"],
                    PayloadTemplate = payloadTemplate,
                },
                new SduiActionSpec
                {
                    Id = "save",
                    Kind = SduiActionKinds.SettingsMutate,
                    PayloadTemplate = ParseElement("""{"namespace":"opencode-go","ops":[]}"""),
                },
                new SduiActionSpec
                {
                    Id = "open",
                    Kind = SduiActionKinds.OpenUrl,
                    Url = openUrl,
                },
                new SduiActionSpec
                {
                    Id = "badEndpoint",
                    Kind = SduiActionKinds.Rpc,
                    Endpoint = actionEndpoint,
                    PayloadTemplate = ParseElement("""{"input":{}}"""),
                },
                new SduiActionSpec
                {
                    Id = "feedback",
                    Kind = SduiActionKinds.OpenForm,
                    Title = "反馈",
                    Fields =
                    [
                        new SduiFormField
                        {
                            Key = "message",
                            Label = "内容",
                            Type = SduiFormFieldTypes.Text,
                            MaxLength = 500,
                        },
                    ],
                    SubmitAction = "save",
                },
                new SduiActionSpec
                {
                    Id = "pickRange",
                    Kind = SduiActionKinds.SetUiState,
                    StateKey = "range",
                    StateValue = ParseElement("\"7d\""),
                },
            ],
        };
    }

    private static IReadOnlyList<SduiBlockSpec> CreateAllBlockTypes(string externalUrl) =>
    [
        new SduiBlockSpec
        {
            Type = SduiBlockTypes.StatusBanner,
            Tone = "info",
            TitleBind = "sources.usage.status",
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
            ],
        },
        new SduiBlockSpec
        {
            Type = SduiBlockTypes.QuotaBar,
            Label = "本月用量",
            PercentBind = "sources.usage.percent",
            ResetAtBind = "sources.usage.resetAt",
        },
        new SduiBlockSpec
        {
            Type = SduiBlockTypes.KeyValueList,
            Items =
            [
                new SduiMetricOrKvItem
                {
                    Label = "套餐",
                    ValueBind = "sources.usage.plan",
                },
            ],
        },
        new SduiBlockSpec
        {
            Type = SduiBlockTypes.SegmentedSwitch,
            StateKey = "range",
            Options =
            [
                new SduiOptionItem { Value = "7d", Label = "7 天" },
                new SduiOptionItem { Value = "30d", Label = "30 天" },
            ],
            OnChangeActionId = "refresh",
        },
        new SduiBlockSpec
        {
            Type = SduiBlockTypes.ListSection,
            ItemsBind = "sources.usage.items",
            ItemTitleBind = "item.title",
            ItemSubtitleBind = "item.subtitle",
            EmptyText = "暂无记录",
        },
        new SduiBlockSpec
        {
            Type = SduiBlockTypes.ActionBar,
            Actions =
            [
                new SduiButtonItem { Label = "刷新", Style = "primary", ActionId = "refresh" },
            ],
        },
        new SduiBlockSpec
        {
            Type = SduiBlockTypes.ExternalLink,
            Title = "官方文档",
            Url = externalUrl,
        },
        new SduiBlockSpec
        {
            Type = SduiBlockTypes.SettingsBridge,
            Title = "配置",
            Namespace = "opencode-go",
        },
    ];

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>仅实现本组测试需要的插件操作桥接：可按需返回内置清单。</summary>
    private sealed class StubPluginOperations : IPluginOperations
    {
        public string? ManifestJson { get; init; }

        public Task<Outcome<string>> ReadLocalSduiManifestAsync(string packageName, CancellationToken ct = default) =>
            Task.FromResult(ManifestJson is null
                ? Outcome<string>.Fail(DshFailureCode.Unsupported, "plugins.local_sdui_unavailable")
                : Outcome<string>.Ok(ManifestJson));

        public Task<Outcome<IReadOnlyList<PluginInstalledPackageView>>> ListInstalledPackagesAsync(CancellationToken ct = default) =>
            Task.FromResult(Outcome<IReadOnlyList<PluginInstalledPackageView>>.Ok([]));

        public Task<Outcome<Unit>> InstallPackageAsync(string packageSpec, CancellationToken ct = default) =>
            Task.FromResult(Outcome<Unit>.Fail(DshFailureCode.Unsupported, "plugins.install_unavailable"));

        public Task<Outcome<Unit>> UninstallPackageAsync(string packageName, CancellationToken ct = default) =>
            Task.FromResult(Outcome<Unit>.Fail(DshFailureCode.Unsupported, "plugins.uninstall_unavailable"));

        public Task<Outcome<Unit>> SetPluginEnabledAsync(string entryId, string moduleName, bool enabled, CancellationToken ct = default) =>
            Task.FromResult(Outcome<Unit>.Fail(DshFailureCode.Unsupported, "plugins.toggle_unavailable"));

        public Task<Outcome<Unit>> RestartDshHostAsync(CancellationToken ct = default) =>
            Task.FromResult(Outcome<Unit>.Fail(DshFailureCode.Unsupported, "plugins.restart_unavailable"));
    }

    /// <summary>可控的 DSH 网关桩：固定返回一条助手文本，并记录会话创建、提示词、取消与归档调用。</summary>
    private sealed class FakeSduiGateway : DefaultDshGateway
    {
        public const string SessionId = "sdui-helper-session";

        public bool PromptCapability { get; init; } = true;

        public string AssistantText { get; init; } = string.Empty;

        public IReadOnlyList<string> AssistantTexts { get; init; } = [];

        public SessionWaitOutcomeKind WaitKind { get; init; } = SessionWaitOutcomeKind.Idle;

        /// <summary>创建辅助会话的额外延迟，用于观察在途任务与并发合并。</summary>
        public TimeSpan CreateDelay { get; init; }

        /// <summary>模拟网关内部未预期异常，验证协调器收敛为稳定诊断。</summary>
        public bool ThrowOnCreate { get; init; }

        public int CreateCalls { get; private set; }

        public int LoadEventsCalls { get; private set; }

        public int CancelCalls { get; private set; }

        public List<PromptSendRequest> SentPrompts { get; } = [];

        public List<string> ArchivedSessionIds { get; } = [];

        /// <summary>按调用顺序消费插件 RPC 试跑响应；为空时返回 <see cref="DshFailureCode.Unavailable"/>。</summary>
        public Queue<Outcome<JsonElement>>? PluginRpcResults { get; init; }

        /// <summary>记录试跑实际调用的端点与参数，用于验证只读数据源范围。</summary>
        public List<(string Endpoint, JsonElement Args)> PluginRpcCalls { get; } = [];

        public override DshCapabilities Capabilities => DshCapabilities.Empty with { Prompt = PromptCapability };

        public override async Task<Outcome<string>> CreateOrResumeSessionAsync(
            CreateOrResumeSessionRequest request,
            CancellationToken ct = default)
        {
            CreateCalls++;
            if (CreateDelay > TimeSpan.Zero)
            {
                await Task.Delay(CreateDelay, ct).ConfigureAwait(false);
            }

            if (ThrowOnCreate)
            {
                throw new InvalidOperationException("secret internal gateway failure must not leak");
            }

            return Outcome<string>.Ok(SessionId);
        }

        public override Task<Outcome<Unit>> SendPromptAsync(PromptSendRequest request, CancellationToken ct = default)
        {
            SentPrompts.Add(request);
            return Task.FromResult(Outcome<Unit>.Ok(Unit.Value));
        }

        public override Task<Outcome<SessionWaitResultView>> WaitForSessionAsync(
            string sessionId,
            TimeSpan? timeout = null,
            long? afterSeq = null,
            CancellationToken ct = default) =>
            Task.FromResult(Outcome<SessionWaitResultView>.Ok(new SessionWaitResultView(WaitKind)));

        public override Task<Outcome<SessionPageView>> LoadSessionEventsAsync(
            string sessionId,
            long? throughSeq = null,
            long? beforeSeq = null,
            int? maxMessages = null,
            CancellationToken ct = default)
        {
            var sequenceIndex = LoadEventsCalls;
            LoadEventsCalls++;
            var text = AssistantTexts.Count == 0
                ? AssistantText
                : AssistantTexts[Math.Min(sequenceIndex, AssistantTexts.Count - 1)];
            var wireEvent = new SessionWireEventView(
                "assistant/message",
                1,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                BuildAssistantMessageData(text));
            return Task.FromResult(Outcome<SessionPageView>.Ok(new SessionPageView([wireEvent], false, 1)));
        }

        public override Task<Outcome<Unit>> CancelTurnAsync(string sessionId, CancellationToken ct = default)
        {
            CancelCalls++;
            return Task.FromResult(Outcome<Unit>.Ok(Unit.Value));
        }

        public override Task<Outcome<IReadOnlyList<string>>> ArchiveSessionAsync(
            string sessionId,
            bool stopActivity = false,
            CancellationToken ct = default)
        {
            ArchivedSessionIds.Add(sessionId);
            return Task.FromResult(Outcome<IReadOnlyList<string>>.Ok([sessionId]));
        }

        public override Task<Outcome<JsonElement>> InvokePluginRpcAsync(
            string endpoint,
            JsonElement args,
            CancellationToken ct = default)
        {
            PluginRpcCalls.Add((endpoint, args.Clone()));
            if (PluginRpcResults is { Count: > 0 } results)
            {
                return Task.FromResult(results.Dequeue());
            }

            // 默认模拟网关未实现插件 RPC；试跑应视其为不可用而不是输入契约错误。
            return Task.FromResult(
                Outcome<JsonElement>.Fail(DshFailureCode.Unavailable, "test.rpc_unavailable"));
        }

        private static JsonElement BuildAssistantMessageData(string text)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteStartObject("message");
                writer.WriteStartArray("content");
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", text);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            using var document = JsonDocument.Parse(buffer.WrittenMemory);
            return document.RootElement.Clone();
        }
    }
}
