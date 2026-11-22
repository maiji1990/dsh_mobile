using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshMobile.Agent.Protocol;

/// <summary>
/// Minimal RPC envelope DTOs verifying System.Text.Json Source Generation under TrimMode=full (PR-00).
/// Full Remote API / mux DTOs are added in PR-01 and PR-02.
/// </summary>
public sealed record ProtocolHandshakeProbe(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("rpcId")] string RpcId,
    [property: JsonPropertyName("method")] string Method);

/// <summary>
/// Source-generated JSON serialization context for <see cref="DshMobile.Agent.Protocol"/>.
/// Reflection-based serialization is forbidden under TrimMode=full.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProtocolHandshakeProbe))]
[JsonSerializable(typeof(JsonElement))]
public partial class AgentProtocolJsonContext : JsonSerializerContext;
