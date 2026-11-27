using System.Text.Json;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;

namespace DshMobile.Agent.Tests.Engines;

public sealed class PluginSettingsSchemaParserTests
{
    private static SettingsNamespaceView BuildNamespaceView(
        string schemaJson,
        string valueJson,
        string? baseJson = null,
        string? userJson = null,
        IReadOnlyList<SettingsSecretSlotView>? secrets = null,
        long revision = 1)
    {
        using var schemaDoc = JsonDocument.Parse(schemaJson);
        using var valueDoc = JsonDocument.Parse(valueJson);
        JsonElement? baseEl = null;
        if (baseJson is not null)
        {
            using var baseDoc = JsonDocument.Parse(baseJson);
            baseEl = baseDoc.RootElement.Clone();
        }
        JsonElement? userEl = null;
        if (userJson is not null)
        {
            using var userDoc = JsonDocument.Parse(userJson);
            userEl = userDoc.RootElement.Clone();
        }

        return new SettingsNamespaceView(
            Namespace: "demo-plugin",
            AutoGenerate: true,
            Schema: schemaDoc.RootElement.Clone(),
            Value: valueDoc.RootElement.Clone(),
            Base: baseEl,
            User: userEl,
            Applies: "live",
            Secrets: secrets ?? [],
            Revision: revision);
    }

    [Fact]
    public void ParseFields_MapsAllOfficialSchemasteryKinds_NestedObjects_AndProtectsSecrets()
    {
        var schemaJson = """
            {
              "type": "object",
              "dict": {
                "enabled": {
                  "type": "boolean",
                  "meta": { "description": { "zh": "开启功能", "en": "Enable feature" } }
                },
                "maxRetries": {
                  "type": "natural",
                  "meta": { "max": 10, "description": { "zh": "最大重试次数", "en": "Max retries" } }
                },
                "mode": {
                  "type": "union",
                  "meta": { "description": { "zh": "运行模式", "en": "Mode" } },
                  "list": [
                    { "type": "const", "value": "fast", "meta": { "description": { "zh": "快速", "en": "Fast" } } },
                    { "type": "const", "value": "safe", "meta": { "description": { "zh": "安全", "en": "Safe" } } }
                  ]
                },
                "apiKey": {
                  "type": "string",
                  "meta": { "role": "secret", "description": { "zh": "接口密钥", "en": "API key" } }
                },
                "proxy": {
                  "type": "object",
                  "dict": {
                    "url": {
                      "type": "string",
                      "meta": { "description": { "zh": "代理地址", "en": "Proxy URL" } }
                    },
                    "nestedL2": {
                      "type": "object",
                      "dict": {
                        "deepObj": {
                          "type": "object",
                          "dict": {
                            "leaf": { "type": "string" }
                          }
                        }
                      }
                    }
                  }
                },
                "tags": {
                  "type": "array",
                  "inner": { "type": "string" },
                  "meta": { "description": "标签列表" }
                },
                "hiddenField": {
                  "type": "string",
                  "meta": { "hidden": true }
                }
              }
            }
            """;

        var valueJson = """
            {
              "enabled": true,
              "maxRetries": 3,
              "mode": "fast",
              "apiKey": "SHOULD_NEVER_BE_SHOWN",
              "proxy": {
                "url": "https://proxy.local",
                "nestedL2": {
                  "deepObj": { "leaf": "deep" }
                }
              },
              "tags": ["a", "b"]
            }
            """;

        var baseJson = """
            {
              "enabled": false,
              "maxRetries": 1,
              "mode": "safe",
              "proxy": { "url": "" },
              "tags": []
            }
            """;

        var userJson = """
            {
              "enabled": true,
              "proxy": { "url": "https://proxy.local" }
            }
            """;

        var nsView = BuildNamespaceView(
            schemaJson,
            valueJson,
            baseJson,
            userJson,
            secrets: [new SettingsSecretSlotView(["apiKey"], IsSet: true)]);

        var fields = PluginSettingsSchemaParser.ParseFields(nsView, profileWritable: true, preferredLocale: "zh");

        // 跳过 hiddenField，共 7 个字段
        Assert.Equal(7, fields.Count);

        var enabled = Assert.Single(fields, f => f.KeyDisplay == "enabled");
        Assert.Equal(PluginConfigFieldKind.Boolean, enabled.Kind);
        Assert.Equal("true", enabled.CurrentValueJson);
        Assert.Equal("false", enabled.BaseValueJson);
        Assert.True(enabled.HasUserOverride);
        Assert.Equal("开启功能", enabled.Description);

        var retries = Assert.Single(fields, f => f.KeyDisplay == "maxRetries");
        Assert.Equal(PluginConfigFieldKind.Number, retries.Kind);
        Assert.Equal(0, retries.Min);
        Assert.Equal(10, retries.Max);
        Assert.Equal(1, retries.Step);
        Assert.False(retries.HasUserOverride);

        var mode = Assert.Single(fields, f => f.KeyDisplay == "mode");
        Assert.Equal(PluginConfigFieldKind.SingleSelect, mode.Kind);
        Assert.Equal(2, mode.Options.Count);
        Assert.Equal("\"fast\"", mode.Options[0].RawValueJson);
        Assert.Equal("快速", mode.Options[0].DisplayLabel);

        // 密钥槽位绝不回显明文
        var apiKey = Assert.Single(fields, f => f.KeyDisplay == "apiKey");
        Assert.Equal(PluginConfigFieldKind.Secret, apiKey.Kind);
        Assert.Null(apiKey.CurrentValueJson);
        Assert.Null(apiKey.BaseValueJson);
        Assert.True(apiKey.IsSecretSet);

        // 嵌套路径 ["proxy", "url"]
        var proxyUrl = Assert.Single(fields, f => f.KeyDisplay == "proxy.url");
        Assert.Equal(["proxy", "url"], proxyUrl.Path);
        Assert.Equal(PluginConfigFieldKind.Text, proxyUrl.Kind);
        Assert.True(proxyUrl.HasUserOverride);

        // 超过 3 层嵌套自动降级为 JsonFallback
        var deepFallback = Assert.Single(fields, f => f.KeyDisplay == "proxy.nestedL2.deepObj");
        Assert.Equal(PluginConfigFieldKind.JsonFallback, deepFallback.Kind);
        Assert.Contains("\"leaf\"", deepFallback.CurrentValueJson!, StringComparison.Ordinal);

        // array 降级为 JsonFallback
        var tags = Assert.Single(fields, f => f.KeyDisplay == "tags");
        Assert.Equal(PluginConfigFieldKind.JsonFallback, tags.Kind);
        Assert.Contains("\"a\"", tags.CurrentValueJson!, StringComparison.Ordinal);

        // 非密钥全量重置操作集合绝不包含 apiKey
        var resetOps = PluginSettingsSchemaParser.BuildResetNonSecretOps(fields);
        Assert.Equal(2, resetOps.Count);
        Assert.DoesNotContain(resetOps, op => op.Path.SequenceEqual(["apiKey"]));
        Assert.Contains(resetOps, op => op.Op == SettingsPathOpKind.Unset && op.Path.SequenceEqual(["enabled"]));
        Assert.Contains(resetOps, op => op.Op == SettingsPathOpKind.Unset && op.Path.SequenceEqual(["proxy", "url"]));
    }

    [Fact]
    public void TryEncodeFieldValue_ValidatesBoundsOptionsSecretsAndJsonFallback()
    {
        var numField = new PluginConfigFieldView(
            ["timeout"], "timeout", "Timeout", null, PluginConfigFieldKind.Number,
            "5", "5", false, false, [], Min: 1, Max: 60, Step: 1, ReadOnly: false);

        Assert.True(PluginSettingsSchemaParser.TryEncodeFieldValue(numField, "30", out var numVal, out _));
        Assert.Equal(30, numVal.GetDouble());
        Assert.False(PluginSettingsSchemaParser.TryEncodeFieldValue(numField, "0", out _, out var minErr));
        Assert.Equal("settings.number_below_min", minErr);
        Assert.False(PluginSettingsSchemaParser.TryEncodeFieldValue(numField, "999", out _, out var maxErr));
        Assert.Equal("settings.number_above_max", maxErr);
        Assert.False(PluginSettingsSchemaParser.TryEncodeFieldValue(numField, "NaN", out _, out var nanErr));
        Assert.Equal("settings.invalid_number", nanErr);

        var selectField = new PluginConfigFieldView(
            ["mode"], "mode", "Mode", null, PluginConfigFieldKind.SingleSelect,
            "\"fast\"", null, false, false,
            [new PluginConfigSelectOptionView("\"fast\"", "快速"), new PluginConfigSelectOptionView("\"safe\"", "安全")]);
        Assert.True(PluginSettingsSchemaParser.TryEncodeFieldValue(selectField, "safe", out var selVal, out _));
        Assert.Equal("safe", selVal.GetString());
        Assert.False(PluginSettingsSchemaParser.TryEncodeFieldValue(selectField, "unknown", out _, out var optErr));
        Assert.Equal("settings.invalid_option", optErr);

        var secretField = new PluginConfigFieldView(
            ["apiKey"], "apiKey", "API Key", null, PluginConfigFieldKind.Secret,
            null, null, false, false, []);
        Assert.False(PluginSettingsSchemaParser.TryEncodeFieldValue(secretField, "   ", out _, out var secErr));
        Assert.Equal("settings.secret_empty", secErr);
        Assert.True(PluginSettingsSchemaParser.TryEncodeFieldValue(secretField, "sk-new-secret", out var secVal, out _));
        Assert.Equal("sk-new-secret", secVal.GetString());

        var jsonField = new PluginConfigFieldView(
            ["tags"], "tags", "Tags", null, PluginConfigFieldKind.JsonFallback,
            "[]", null, false, false, []);
        Assert.True(PluginSettingsSchemaParser.TryEncodeFieldValue(jsonField, """["x","y"]""", out var arrVal, out _));
        Assert.Equal(JsonValueKind.Array, arrVal.ValueKind);
        Assert.False(PluginSettingsSchemaParser.TryEncodeFieldValue(jsonField, "{not-json", out _, out var jsonErr));
        Assert.Equal("settings.invalid_json", jsonErr);
    }

    [Fact]
    public void ParseFields_WhenProfileNotWritable_MarksAllFieldsReadOnly()
    {
        var nsView = BuildNamespaceView(
            """{"type":"object","dict":{"flag":{"type":"boolean"}}}""",
            """{"flag":true}""");

        var fields = PluginSettingsSchemaParser.ParseFields(nsView, profileWritable: false);
        var field = Assert.Single(fields);
        Assert.True(field.ReadOnly);
        Assert.False(PluginSettingsSchemaParser.TryEncodeFieldValue(field, "false", out _, out var err));
        Assert.Equal("settings.field_readonly", err);
    }

    [Fact]
    public void ParseFields_ResolvesSchemasteryToJsonUidRefsEnvelope_NestedObjectsUnionsTransformsAndCycles()
    {
        // 对齐官方 @deepseek-ai/schemastery 3.x schema.toJSON() 真实输出的 { uid, refs } 扁平引用表格式
        var envelopeJson = """
            {
              "uid": 10,
              "refs": {
                "10": {
                  "uid": 10,
                  "type": "object",
                  "dict": {
                    "maxParallelToolCalls": 11,
                    "mode": 12,
                    "nested": 15,
                    "apiSecret": 17,
                    "selfCycle": 10
                  },
                  "meta": {}
                },
                "11": {
                  "uid": 11,
                  "type": "number",
                  "meta": { "min": 1, "step": 1, "default": 10, "description": { "zh": "并发工具调用上限" } }
                },
                "12": {
                  "uid": 12,
                  "type": "union",
                  "list": [13, 14],
                  "meta": { "description": { "zh": "执行模式" } }
                },
                "13": {
                  "uid": 13,
                  "type": "const",
                  "value": "one-shot",
                  "meta": { "description": { "zh": "单次" } }
                },
                "14": {
                  "uid": 14,
                  "type": "const",
                  "value": "continuable",
                  "meta": { "description": { "zh": "可延续" } }
                },
                "15": {
                  "uid": 15,
                  "type": "transform",
                  "inner": 16,
                  "meta": {}
                },
                "16": {
                  "uid": 16,
                  "type": "object",
                  "dict": {
                    "enabled": 18
                  },
                  "meta": {}
                },
                "17": {
                  "uid": 17,
                  "type": "string",
                  "meta": { "role": "secret", "description": { "zh": "私密令牌" } }
                },
                "18": {
                  "uid": 18,
                  "type": "boolean",
                  "meta": { "description": { "zh": "启用子项" } }
                }
              }
            }
            """;

        var valueJson = """
            {
              "maxParallelToolCalls": 10,
              "mode": "one-shot",
              "nested": { "enabled": true }
            }
            """;

        var nsView = BuildNamespaceView(
            envelopeJson,
            valueJson,
            baseJson: """{"maxParallelToolCalls":10,"mode":"one-shot","nested":{"enabled":false}}""",
            userJson: """{"nested":{"enabled":true}}""",
            secrets: [new SettingsSecretSlotView(["apiSecret"], IsSet: true)]);

        Assert.True(PluginSettingsSchemaParser.HasConfigurableFields(nsView));
        var fields = PluginSettingsSchemaParser.ParseFields(nsView, profileWritable: true, preferredLocale: "zh");

        // 跳过递归环 selfCycle，正确展开 4 个字段
        Assert.Equal(4, fields.Count);

        var maxParallel = Assert.Single(fields, f => f.KeyDisplay == "maxParallelToolCalls");
        Assert.Equal(PluginConfigFieldKind.Number, maxParallel.Kind);
        Assert.Equal(1, maxParallel.Min);
        Assert.Equal(1, maxParallel.Step);
        Assert.Equal("10", maxParallel.CurrentValueJson);
        Assert.Equal("并发工具调用上限", maxParallel.Description);

        var mode = Assert.Single(fields, f => f.KeyDisplay == "mode");
        Assert.Equal(PluginConfigFieldKind.SingleSelect, mode.Kind);
        Assert.Equal(2, mode.Options.Count);
        Assert.Equal("单次", mode.Options[0].DisplayLabel);
        Assert.Equal("可延续", mode.Options[1].DisplayLabel);

        var nestedEnabled = Assert.Single(fields, f => f.KeyDisplay == "nested.enabled");
        Assert.Equal(PluginConfigFieldKind.Boolean, nestedEnabled.Kind);
        Assert.Equal("true", nestedEnabled.CurrentValueJson);
        Assert.True(nestedEnabled.HasUserOverride);

        var secret = Assert.Single(fields, f => f.KeyDisplay == "apiSecret");
        Assert.Equal(PluginConfigFieldKind.Secret, secret.Kind);
        Assert.Null(secret.CurrentValueJson);
        Assert.True(secret.IsSecretSet);
    }
}
