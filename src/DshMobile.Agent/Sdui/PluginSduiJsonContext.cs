using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshMobile.Agent.Sdui;

/// <summary>
/// 插件 SDUI 文档的源生成 JSON 上下文（Slice 1B）。
/// 反射序列化在 TrimMode=full / AOT 下不可用，因此全部读写只经本上下文完成。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(PluginSduiDocument))]
[JsonSerializable(typeof(SduiDataSource))]
[JsonSerializable(typeof(SduiTabSpec))]
[JsonSerializable(typeof(SduiBlockSpec))]
[JsonSerializable(typeof(SduiMetricOrKvItem))]
[JsonSerializable(typeof(SduiOptionItem))]
[JsonSerializable(typeof(SduiButtonItem))]
[JsonSerializable(typeof(SduiCondition))]
[JsonSerializable(typeof(SduiActionSpec))]
[JsonSerializable(typeof(SduiFormField))]
[JsonSerializable(typeof(IReadOnlyList<SduiDataSource>))]
[JsonSerializable(typeof(IReadOnlyList<SduiTabSpec>))]
[JsonSerializable(typeof(IReadOnlyList<SduiBlockSpec>))]
[JsonSerializable(typeof(IReadOnlyList<SduiMetricOrKvItem>))]
[JsonSerializable(typeof(IReadOnlyList<SduiOptionItem>))]
[JsonSerializable(typeof(IReadOnlyList<SduiButtonItem>))]
[JsonSerializable(typeof(IReadOnlyList<SduiActionSpec>))]
[JsonSerializable(typeof(IReadOnlyList<SduiFormField>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class PluginSduiJsonContext : JsonSerializerContext
{
}
