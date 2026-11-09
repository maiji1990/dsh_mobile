using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshMobile.Core.Bridge;

public static class BridgeProtocol
{
    public const int Version = 1;
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(30);
}

/// <summary>Known protocol v1 methods (docs/PROVIDER_CONTRACTS.md 1.6). Append only.</summary>
public static class BridgeMethods
{
    public const string Info = "bridge.info";
    public const string Ping = "bridge.ping";
    public const string DshboxVersionsCompare = "dshbox.versions.compare";
}

public sealed record BridgeRequest(
    [property: JsonPropertyName("protocol")] int Protocol,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("deadlineMs")] int DeadlineMs,
    [property: JsonPropertyName("payload")] JsonElement? Payload);

public sealed record BridgeResponse(
    [property: JsonPropertyName("protocol")] int Protocol,
    [property: JsonPropertyName("requestId")] string? RequestId,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("payload")] JsonElement? Payload)
{
    /// <summary>A response synthesized on the C# side (timeout, transport failure); never sent by Native.</summary>
    public static BridgeResponse Local(string requestId, string code, string message) =>
        new(BridgeProtocol.Version, requestId, false, code, message, null);
}

public sealed record BridgeEvent(
    [property: JsonPropertyName("protocol")] int Protocol,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("payload")] JsonElement? Payload);

/// <summary>Payload of <see cref="BridgeMethods.Info"/>.</summary>
public sealed record BridgeInfo(
    [property: JsonPropertyName("bridgeVersion")] string BridgeVersion,
    [property: JsonPropertyName("protocol")] int Protocol,
    [property: JsonPropertyName("upstream")] Dictionary<string, string> Upstream,
    [property: JsonPropertyName("kotlinVersion")] string KotlinVersion,
    [property: JsonPropertyName("sdkInt")] int SdkInt,
    [property: JsonPropertyName("abis")] string[] Abis,
    [property: JsonPropertyName("deviceModel")] string DeviceModel);

public sealed record VersionsCompareRequest(
    [property: JsonPropertyName("a")] string A,
    [property: JsonPropertyName("b")] string B);

public sealed record VersionsCompareResult(
    [property: JsonPropertyName("result")] int Result);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(BridgeRequest))]
[JsonSerializable(typeof(BridgeResponse))]
[JsonSerializable(typeof(BridgeEvent))]
[JsonSerializable(typeof(BridgeInfo))]
[JsonSerializable(typeof(VersionsCompareRequest))]
[JsonSerializable(typeof(VersionsCompareResult))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class BridgeJsonContext : JsonSerializerContext;
