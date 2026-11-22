using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshMobile.Agent.Protocol;

/// <summary>
/// Source-generated JSON serialization context for <see cref="DshMobile.Agent.Protocol"/>.
/// Reflection-based serialization is forbidden under TrimMode=full.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProtocolHandshakeProbe))]
[JsonSerializable(typeof(RemoteArgsPayload))]
[JsonSerializable(typeof(RemoteClientRequest))]
[JsonSerializable(typeof(RemoteRpcError))]
[JsonSerializable(typeof(RemoteRpcResult))]
[JsonSerializable(typeof(RemoteServerResponse))]
[JsonSerializable(typeof(RemoteBinaryAttachmentMeta))]
[JsonSerializable(typeof(RemoteBinaryResponseEnvelope))]
[JsonSerializable(typeof(MuxOpenFrame))]
[JsonSerializable(typeof(MuxControlFrame))]
[JsonSerializable(typeof(MuxItemFrame))]
[JsonSerializable(typeof(MuxInboundFrame))]
[JsonSerializable(typeof(RemoteHostInfo))]
[JsonSerializable(typeof(RemoteEventsReadyPayload))]
[JsonSerializable(typeof(RemoteForwardedEventPayload))]
[JsonSerializable(typeof(RemoteEventRejection))]
[JsonSerializable(typeof(RemoteEventOutcome))]
[JsonSerializable(typeof(RemoteEventResultArgs))]
[JsonSerializable(typeof(RpcId))]
[JsonSerializable(typeof(JsonElement))]
public partial class AgentProtocolJsonContext : JsonSerializerContext;
