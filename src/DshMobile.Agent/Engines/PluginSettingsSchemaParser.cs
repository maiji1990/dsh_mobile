using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Protocol;

namespace DshMobile.Agent.Engines;

/// <summary>
/// 纯托管官方 Schemastery 轻量配置视图映射器（ADR-0034，零第三方 Schema 引擎、Trim/AOT 安全）：
/// 仅消费 DSH 官方 <c>@deepseek-ai/dsh-api-settings-controller</c> 的 <see cref="SettingsNamespaceView"/>，
/// 展开顶层及最多 3 层嵌套字段，确保 <c>role('secret')</c> 槽位永不回显明文。
/// </summary>
public static class PluginSettingsSchemaParser
{
    private const int MaxNestingDepth = 3;
    private const int MaxFormattedJsonBytes = 32 * 1024;

    /// <summary>将单个官方命名空间快照解析为有序的强类型配置字段列表。</summary>
    public static IReadOnlyList<PluginConfigFieldView> ParseFields(
        SettingsNamespaceView namespaceView,
        bool profileWritable = true,
        string preferredLocale = "zh")
    {
        ArgumentNullException.ThrowIfNull(namespaceView);
        if (namespaceView.Schema.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        if (!SchemaGraph.TryCreate(namespaceView.Schema, out var graph, out var rootRaw, out var rootUid))
        {
            return [];
        }

        var secretMap = BuildSecretLookup(namespaceView.Secrets);
        var fields = new List<PluginConfigFieldView>();
        var rootNode = UnwrapTransform(graph, rootRaw, out _);
        var visitedUids = new HashSet<string>(StringComparer.Ordinal);
        if (rootUid is not null)
        {
            visitedUids.Add(rootUid);
        }

        WalkContainerNode(
            graph: graph,
            containerNode: rootNode,
            parentPath: [],
            depth: 0,
            namespaceView: namespaceView,
            secretMap: secretMap,
            profileWritable: profileWritable,
            preferredLocale: preferredLocale,
            visitedUids: visitedUids,
            output: fields);

        return fields;
    }

    /// <summary>判断某命名空间是否包含至少一个可展示/可配置字段。</summary>
    public static bool HasConfigurableFields(SettingsNamespaceView namespaceView)
    {
        ArgumentNullException.ThrowIfNull(namespaceView);
        return ParseFields(namespaceView, profileWritable: true).Count > 0;
    }

    /// <summary>
    /// 校验并将用户在表单中输入的文本/值编码为合法 <see cref="JsonElement"/>，
    /// 用于组装 <see cref="SettingsPathOp.Set"/>。绝不在错误键中携带用户输入的明文。
    /// </summary>
    public static bool TryEncodeFieldValue(
        PluginConfigFieldView field,
        string? rawInput,
        out JsonElement encodedValue,
        out string? errorKey)
    {
        ArgumentNullException.ThrowIfNull(field);
        encodedValue = default;
        errorKey = null;

        if (field.ReadOnly)
        {
            errorKey = "settings.field_readonly";
            return false;
        }

        switch (field.Kind)
        {
            case PluginConfigFieldKind.Boolean:
            {
                var trimmed = rawInput?.Trim();
                if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase))
                {
                    encodedValue = EncodeBoolean(true);
                    return true;
                }
                if (string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase))
                {
                    encodedValue = EncodeBoolean(false);
                    return true;
                }
                errorKey = "settings.invalid_boolean";
                return false;
            }

            case PluginConfigFieldKind.Number:
            {
                var trimmed = rawInput?.Trim();
                if (string.IsNullOrEmpty(trimmed) ||
                    !double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var num) ||
                    !double.IsFinite(num))
                {
                    errorKey = "settings.invalid_number";
                    return false;
                }
                if (field.Min.HasValue && num < field.Min.Value)
                {
                    errorKey = "settings.number_below_min";
                    return false;
                }
                if (field.Max.HasValue && num > field.Max.Value)
                {
                    errorKey = "settings.number_above_max";
                    return false;
                }
                encodedValue = EncodeNumber(num);
                return true;
            }

            case PluginConfigFieldKind.SingleSelect:
            {
                var trimmed = rawInput?.Trim() ?? string.Empty;
                foreach (var option in field.Options)
                {
                    if (string.Equals(option.RawValueJson, trimmed, StringComparison.Ordinal) ||
                        string.Equals(UnquoteJsonString(option.RawValueJson), trimmed, StringComparison.Ordinal))
                    {
                        if (TryParseJsonElement(option.RawValueJson, out encodedValue))
                        {
                            return true;
                        }
                    }
                }
                errorKey = "settings.invalid_option";
                return false;
            }

            case PluginConfigFieldKind.Secret:
            {
                if (string.IsNullOrWhiteSpace(rawInput))
                {
                    errorKey = "settings.secret_empty";
                    return false;
                }
                encodedValue = EncodeString(rawInput);
                return true;
            }

            case PluginConfigFieldKind.Text:
            {
                encodedValue = EncodeString(rawInput ?? string.Empty);
                return true;
            }

            case PluginConfigFieldKind.JsonFallback:
            {
                if (string.IsNullOrWhiteSpace(rawInput))
                {
                    errorKey = "settings.invalid_json";
                    return false;
                }
                if (!TryParseJsonElement(rawInput, out encodedValue))
                {
                    errorKey = "settings.invalid_json";
                    return false;
                }
                return true;
            }

            default:
                errorKey = "settings.unsupported_field_kind";
                return false;
        }
    }

    /// <summary>生成安全的非密钥全量重置操作集合（仅对非 <see cref="PluginConfigFieldKind.Secret"/> 且已覆盖的字段发 <c>unset</c>）。</summary>
    public static IReadOnlyList<SettingsPathOp> BuildResetNonSecretOps(IReadOnlyList<PluginConfigFieldView> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var ops = new List<SettingsPathOp>();
        foreach (var field in fields)
        {
            if (field.Kind == PluginConfigFieldKind.Secret || !field.HasUserOverride)
            {
                continue;
            }
            ops.Add(SettingsPathOp.Unset(field.Path));
        }
        return ops;
    }

    /// <summary>构造空的 JSON 对象 <c>{}</c>（用于无密钥槽位时的 <c>settings/replace</c> 全量重置）。</summary>
    public static JsonElement CreateEmptyObjectElement()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    /// <summary>编码布尔值为独立的 <see cref="JsonElement"/>。</summary>
    public static JsonElement EncodeBoolean(bool value)
    {
        using var doc = JsonDocument.Parse(value ? "true" : "false");
        return doc.RootElement.Clone();
    }

    /// <summary>编码有限浮点数/整数为独立的 <see cref="JsonElement"/>。</summary>
    public static JsonElement EncodeNumber(double value)
    {
        var buffer = new ArrayBufferWriter<byte>(32);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteNumberValue(value);
        }
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    /// <summary>编码字符串为独立的 <see cref="JsonElement"/>。</summary>
    public static JsonElement EncodeString(string value)
    {
        var buffer = new ArrayBufferWriter<byte>(Math.Max(32, value.Length * 2));
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStringValue(value);
        }
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    private static void WalkContainerNode(
        SchemaGraph graph,
        JsonElement containerNode,
        IReadOnlyList<string> parentPath,
        int depth,
        SettingsNamespaceView namespaceView,
        Dictionary<string, bool> secretMap,
        bool profileWritable,
        string preferredLocale,
        HashSet<string> visitedUids,
        List<PluginConfigFieldView> output)
    {
        if (TryGetObjectDict(containerNode, out var dictElement))
        {
            WalkObjectProperties(
                graph: graph,
                dictElement: dictElement,
                parentPath: parentPath,
                depth: depth,
                namespaceView: namespaceView,
                secretMap: secretMap,
                profileWritable: profileWritable,
                preferredLocale: preferredLocale,
                visitedUids: visitedUids,
                output: output);
            return;
        }

        // 支持顶层或嵌套的 intersect([object, ...]) 组合结构，按分支顺序合并对象属性。
        if (string.Equals(TryGetStringProperty(containerNode, "type"), "intersect", StringComparison.Ordinal) &&
            containerNode.TryGetProperty("list", out var listEl) &&
            listEl.ValueKind == JsonValueKind.Array)
        {
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in listEl.EnumerateArray())
            {
                if (!graph.TryResolveNode(item, out var branchRaw, out var branchUid))
                {
                    continue;
                }
                var addedUid = branchUid is not null && visitedUids.Add(branchUid);
                if (branchUid is not null && !addedUid)
                {
                    continue;
                }
                try
                {
                    var branch = UnwrapTransform(graph, branchRaw, out _);
                    if (TryGetObjectDict(branch, out var branchDict))
                    {
                        WalkObjectProperties(
                            graph: graph,
                            dictElement: branchDict,
                            parentPath: parentPath,
                            depth: depth,
                            namespaceView: namespaceView,
                            secretMap: secretMap,
                            profileWritable: profileWritable,
                            preferredLocale: preferredLocale,
                            visitedUids: visitedUids,
                            output: output,
                            dedupeKeys: seenKeys);
                    }
                }
                finally
                {
                    if (addedUid && branchUid is not null)
                    {
                        visitedUids.Remove(branchUid);
                    }
                }
            }
        }
    }

    private static void WalkObjectProperties(
        SchemaGraph graph,
        JsonElement dictElement,
        IReadOnlyList<string> parentPath,
        int depth,
        SettingsNamespaceView namespaceView,
        Dictionary<string, bool> secretMap,
        bool profileWritable,
        string preferredLocale,
        HashSet<string> visitedUids,
        List<PluginConfigFieldView> output,
        HashSet<string>? dedupeKeys = null)
    {
        foreach (var prop in dictElement.EnumerateObject())
        {
            if (dedupeKeys is not null && !dedupeKeys.Add(prop.Name))
            {
                continue;
            }

            if (!graph.TryResolveNode(prop.Value, out var resolvedProp, out var propUid))
            {
                continue;
            }

            var addedUid = propUid is not null && visitedUids.Add(propUid);
            if (propUid is not null && !addedUid)
            {
                continue;
            }

            try
            {
                var path = AppendPath(parentPath, prop.Name);
                var unwrapped = UnwrapTransform(graph, resolvedProp, out var outerMeta);
                var effectiveMeta = ResolveEffectiveMeta(unwrapped, outerMeta);

                if (GetMetaBool(effectiveMeta, "hidden") == true)
                {
                    continue;
                }

                var pathKey = BuildPathKey(path);
                var isDeclaredSecret = secretMap.TryGetValue(pathKey, out var secretSlotSet) ||
                                       string.Equals(GetMetaString(effectiveMeta, "role"), "secret", StringComparison.OrdinalIgnoreCase);

                if (!isDeclaredSecret &&
                    depth + 1 < MaxNestingDepth &&
                    TryGetObjectDict(unwrapped, out var nestedDict) &&
                    HasAnyProperty(nestedDict))
                {
                    WalkObjectProperties(
                        graph: graph,
                        dictElement: nestedDict,
                        parentPath: path,
                        depth: depth + 1,
                        namespaceView: namespaceView,
                        secretMap: secretMap,
                        profileWritable: profileWritable,
                        preferredLocale: preferredLocale,
                        visitedUids: visitedUids,
                        output: output);
                    continue;
                }

                var field = BuildLeafField(
                    graph: graph,
                    node: unwrapped,
                    meta: effectiveMeta,
                    path: path,
                    namespaceView: namespaceView,
                    isSecret: isDeclaredSecret,
                    secretSlotSet: secretSlotSet,
                    profileWritable: profileWritable,
                    preferredLocale: preferredLocale);

                output.Add(field);
            }
            finally
            {
                if (addedUid && propUid is not null)
                {
                    visitedUids.Remove(propUid);
                }
            }
        }
    }

    private static PluginConfigFieldView BuildLeafField(
        SchemaGraph graph,
        JsonElement node,
        JsonElement? meta,
        string[] path,
        SettingsNamespaceView namespaceView,
        bool isSecret,
        bool secretSlotSet,
        bool profileWritable,
        string preferredLocale)
    {
        var keyDisplay = string.Join('.', path);
        var titleText = meta is { ValueKind: JsonValueKind.Object } mTitle && mTitle.TryGetProperty("title", out var titleEl)
            ? RemoteProtocolCodec.ResolveLocalizedText(titleEl, preferredLocale)
            : null;
        var descriptionText = meta is { ValueKind: JsonValueKind.Object } mDesc && mDesc.TryGetProperty("description", out var descEl)
            ? RemoteProtocolCodec.ResolveLocalizedText(descEl, preferredLocale)
            : null;

        var label = !string.IsNullOrWhiteSpace(titleText)
            ? titleText!.Trim()
            : FormatFriendlyFieldLabel(path);
        var description = string.IsNullOrWhiteSpace(descriptionText)
            ? null
            : SecretRedactor.RedactAndTruncate(descriptionText.Trim(), 300);

        var readOnly = !profileWritable ||
                       GetMetaBool(meta, "disabled") == true ||
                       GetMetaBool(meta, "readOnly") == true;

        var hasUserOverride = namespaceView.User is { ValueKind: JsonValueKind.Object } userObj &&
                              TryNavigatePath(userObj, path, out _);

        if (isSecret)
        {
            // 密钥字段单向写入，绝不回显明文或默认值。
            return new PluginConfigFieldView(
                Path: path,
                KeyDisplay: keyDisplay,
                Label: label,
                Description: description,
                Kind: PluginConfigFieldKind.Secret,
                CurrentValueJson: null,
                BaseValueJson: null,
                HasUserOverride: hasUserOverride || secretSlotSet,
                IsSecretSet: secretSlotSet,
                Options: [],
                ReadOnly: readOnly);
        }

        var nodeType = TryGetStringProperty(node, "type") ?? string.Empty;
        var kind = ClassifyNode(graph, node, nodeType, preferredLocale, out var options, out var min, out var max, out var step, out var isConstReadOnly);
        if (isConstReadOnly)
        {
            readOnly = true;
        }

        // 读取 meta 上的数值边界（优先 meta，其次节点根级别）
        if (kind == PluginConfigFieldKind.Number)
        {
            min ??= GetMetaDouble(meta, "min") ?? TryGetDoubleProperty(node, "min");
            max ??= GetMetaDouble(meta, "max") ?? TryGetDoubleProperty(node, "max");
            step ??= GetMetaDouble(meta, "step") ?? TryGetDoubleProperty(node, "step");
        }

        string? currentJson = null;
        if (TryNavigatePath(namespaceView.Value, path, out var currentEl))
        {
            currentJson = FormatValueJson(currentEl, kind == PluginConfigFieldKind.JsonFallback);
        }

        string? baseJson = null;
        if (namespaceView.Base is { ValueKind: JsonValueKind.Object } baseObj &&
            TryNavigatePath(baseObj, path, out var baseEl))
        {
            baseJson = FormatValueJson(baseEl, kind == PluginConfigFieldKind.JsonFallback);
        }

        return new PluginConfigFieldView(
            Path: path,
            KeyDisplay: keyDisplay,
            Label: label,
            Description: description,
            Kind: kind,
            CurrentValueJson: currentJson,
            BaseValueJson: baseJson,
            HasUserOverride: hasUserOverride,
            IsSecretSet: false,
            Options: options,
            Min: min,
            Max: max,
            Step: step,
            ReadOnly: readOnly);
    }

    private static PluginConfigFieldKind ClassifyNode(
        SchemaGraph graph,
        JsonElement node,
        string nodeType,
        string preferredLocale,
        out IReadOnlyList<PluginConfigSelectOptionView> options,
        out double? min,
        out double? max,
        out double? step,
        out bool isConstReadOnly)
    {
        options = [];
        min = null;
        max = null;
        step = null;
        isConstReadOnly = false;

        switch (nodeType)
        {
            case "boolean":
                return PluginConfigFieldKind.Boolean;

            case "number":
                return PluginConfigFieldKind.Number;

            case "natural":
                min = 0;
                step = 1;
                return PluginConfigFieldKind.Number;

            case "percent":
                min = 0;
                max = 1;
                step = 0.01;
                return PluginConfigFieldKind.Number;

            case "string":
            case "path":
                return PluginConfigFieldKind.Text;

            case "const":
                isConstReadOnly = true;
                return PluginConfigFieldKind.Text;

            case "union":
                if (TryExtractConstUnionOptions(graph, node, preferredLocale, out var parsedOptions))
                {
                    options = parsedOptions;
                    return PluginConfigFieldKind.SingleSelect;
                }
                return PluginConfigFieldKind.JsonFallback;

            case "array":
            case "dict":
            case "object":
            case "intersect":
            default:
                return PluginConfigFieldKind.JsonFallback;
        }
    }

    private static bool TryExtractConstUnionOptions(
        SchemaGraph graph,
        JsonElement unionNode,
        string preferredLocale,
        out IReadOnlyList<PluginConfigSelectOptionView> options)
    {
        options = [];
        if (!unionNode.TryGetProperty("list", out var listEl) ||
            listEl.ValueKind != JsonValueKind.Array ||
            listEl.GetArrayLength() == 0)
        {
            return false;
        }

        var result = new List<PluginConfigSelectOptionView>(listEl.GetArrayLength());
        foreach (var rawItem in listEl.EnumerateArray())
        {
            if (!graph.TryResolveNode(rawItem, out var item, out _))
            {
                return false;
            }
            var itemType = TryGetStringProperty(item, "type");
            if (!string.Equals(itemType, "const", StringComparison.Ordinal) ||
                !item.TryGetProperty("value", out var valEl))
            {
                return false;
            }

            var rawValueJson = valEl.GetRawText();
            var literalDisplay = valEl.ValueKind == JsonValueKind.String
                ? valEl.GetString() ?? rawValueJson
                : rawValueJson;

            string? optDesc = null;
            if (item.TryGetProperty("meta", out var itemMeta) &&
                itemMeta.ValueKind == JsonValueKind.Object &&
                itemMeta.TryGetProperty("description", out var descEl))
            {
                optDesc = RemoteProtocolCodec.ResolveLocalizedText(descEl, preferredLocale);
            }

            var displayLabel = !string.IsNullOrWhiteSpace(optDesc) ? optDesc!.Trim() : literalDisplay;
            var description = !string.IsNullOrWhiteSpace(optDesc) && !string.Equals(displayLabel, literalDisplay, StringComparison.Ordinal)
                ? literalDisplay
                : null;

            result.Add(new PluginConfigSelectOptionView(rawValueJson, displayLabel, description));
        }

        if (result.Count == 0)
        {
            return false;
        }

        options = result;
        return true;
    }

    private static JsonElement UnwrapTransform(SchemaGraph graph, JsonElement node, out JsonElement? outerMeta)
    {
        outerMeta = null;
        var current = node;
        var guard = 0;
        while (guard++ < 6 &&
               current.ValueKind == JsonValueKind.Object &&
               string.Equals(TryGetStringProperty(current, "type"), "transform", StringComparison.Ordinal) &&
               current.TryGetProperty("inner", out var innerRef) &&
               graph.TryResolveNode(innerRef, out var inner, out _))
        {
            if (outerMeta is null && current.TryGetProperty("meta", out var m) && m.ValueKind == JsonValueKind.Object)
            {
                outerMeta = m;
            }
            current = inner;
        }
        return current;
    }

    /// <summary>
    /// 轻量 Schemastery 3.x <c>schema.toJSON()</c> 引用表解析器：
    /// 兼容带 <c>{ uid, refs }</c> 扁平引用的官方序列化信封，以及直接内联节点树。
    /// </summary>
    private readonly struct SchemaGraph
    {
        private readonly Dictionary<string, JsonElement>? _refs;

        private SchemaGraph(Dictionary<string, JsonElement>? refs)
        {
            _refs = refs;
        }

        public static bool TryCreate(JsonElement rootSchema, out SchemaGraph graph, out JsonElement rootNode, out string? rootUid)
        {
            graph = default;
            rootNode = default;
            rootUid = null;
            if (rootSchema.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (rootSchema.TryGetProperty("refs", out var refsEl) &&
                refsEl.ValueKind == JsonValueKind.Object &&
                rootSchema.TryGetProperty("uid", out var uidEl))
            {
                var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var entry in refsEl.EnumerateObject())
                {
                    if (entry.Value.ValueKind == JsonValueKind.Object)
                    {
                        map[entry.Name] = entry.Value;
                    }
                }
                graph = new SchemaGraph(map);
                return graph.TryResolveNode(uidEl, out rootNode, out rootUid);
            }

            graph = new SchemaGraph(null);
            rootNode = rootSchema;
            return true;
        }

        public bool TryResolveNode(JsonElement candidate, out JsonElement resolved, out string? uidKey)
        {
            resolved = default;
            uidKey = null;

            if (candidate.ValueKind == JsonValueKind.Object)
            {
                resolved = candidate;
                return true;
            }

            if (_refs is null)
            {
                return false;
            }

            string? key = candidate.ValueKind switch
            {
                JsonValueKind.Number => candidate.GetRawText(),
                JsonValueKind.String => candidate.GetString(),
                _ => null,
            };

            if (string.IsNullOrEmpty(key) || !_refs.TryGetValue(key, out resolved))
            {
                return false;
            }

            uidKey = key;
            return true;
        }
    }

    private static JsonElement? ResolveEffectiveMeta(JsonElement unwrapped, JsonElement? outerMeta)
    {
        if (unwrapped.ValueKind == JsonValueKind.Object &&
            unwrapped.TryGetProperty("meta", out var innerMeta) &&
            innerMeta.ValueKind == JsonValueKind.Object)
        {
            return innerMeta;
        }
        return outerMeta;
    }

    private static bool TryGetObjectDict(JsonElement node, out JsonElement dictEl)
    {
        dictEl = default;
        if (node.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        var type = TryGetStringProperty(node, "type");
        if (!string.Equals(type, "object", StringComparison.Ordinal))
        {
            return false;
        }
        return node.TryGetProperty("dict", out dictEl) && dictEl.ValueKind == JsonValueKind.Object;
    }

    private static bool HasAnyProperty(JsonElement obj)
    {
        foreach (var _ in obj.EnumerateObject())
        {
            return true;
        }
        return false;
    }

    private static Dictionary<string, bool> BuildSecretLookup(IReadOnlyList<SettingsSecretSlotView> secrets)
    {
        var map = new Dictionary<string, bool>(secrets.Count, StringComparer.Ordinal);
        foreach (var secret in secrets)
        {
            if (secret.Path.Count == 0)
            {
                continue;
            }
            var key = BuildPathKey(secret.Path);
            if (map.TryGetValue(key, out var prev))
            {
                map[key] = prev || secret.IsSet;
            }
            else
            {
                map[key] = secret.IsSet;
            }
        }
        return map;
    }

    private static string BuildPathKey(IReadOnlyList<string> path) =>
        string.Join('\u001f', path);

    private static string[] AppendPath(IReadOnlyList<string> parentPath, string segment)
    {
        var next = new string[parentPath.Count + 1];
        for (var i = 0; i < parentPath.Count; i++)
        {
            next[i] = parentPath[i];
        }
        next[parentPath.Count] = segment;
        return next;
    }

    private static bool TryNavigatePath(JsonElement root, IReadOnlyList<string> path, out JsonElement found)
    {
        found = root;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var segment in path)
        {
            if (found.ValueKind != JsonValueKind.Object || !found.TryGetProperty(segment, out found))
            {
                found = default;
                return false;
            }
        }
        return found.ValueKind != JsonValueKind.Undefined;
    }

    private static string FormatValueJson(JsonElement element, bool indented)
    {
        if (!indented)
        {
            return element.GetRawText();
        }

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            element.WriteTo(writer);
        }

        var text = Encoding.UTF8.GetString(buffer.WrittenSpan);
        return text.Length > MaxFormattedJsonBytes
            ? text[..MaxFormattedJsonBytes]
            : text;
    }

    private static string FormatFriendlyFieldLabel(string[] path)
    {
        var segments = new string[path.Length];
        for (var i = 0; i < path.Length; i++)
        {
            segments[i] = HumanizeSegment(path[i]);
        }
        return string.Join(" / ", segments);
    }

    private static string HumanizeSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return segment;
        }

        var sb = new StringBuilder(segment.Length + 4);
        for (var i = 0; i < segment.Length; i++)
        {
            var ch = segment[i];
            if (ch is '_' or '-')
            {
                sb.Append(' ');
                continue;
            }
            if (i > 0 && char.IsUpper(ch) && char.IsLower(segment[i - 1]))
            {
                sb.Append(' ');
            }
            sb.Append(i == 0 ? char.ToUpperInvariant(ch) : ch);
        }
        return sb.ToString();
    }

    private static string UnquoteJsonString(string rawJson)
    {
        if (rawJson.Length >= 2 && rawJson[0] == '"' && rawJson[^1] == '"')
        {
            if (TryParseJsonElement(rawJson, out var el) && el.ValueKind == JsonValueKind.String)
            {
                return el.GetString() ?? rawJson;
            }
        }
        return rawJson;
    }

    private static bool TryParseJsonElement(string rawJson, out JsonElement element)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            element = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }

    private static string? TryGetStringProperty(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static double? TryGetDoubleProperty(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n)
            ? n
            : null;

    private static string? GetMetaString(JsonElement? meta, string name) =>
        meta is { ValueKind: JsonValueKind.Object } m ? TryGetStringProperty(m, name) : null;

    private static bool? GetMetaBool(JsonElement? meta, string name) =>
        meta is { ValueKind: JsonValueKind.Object } m &&
        m.TryGetProperty(name, out var v) &&
        v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    private static double? GetMetaDouble(JsonElement? meta, string name) =>
        meta is { ValueKind: JsonValueKind.Object } m ? TryGetDoubleProperty(m, name) : null;
}
