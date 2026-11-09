using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace DshMobile.Core.Bridge;

/// <summary>Trim/AOT-safe (source-generated) encoding of protocol v1 messages.</summary>
public static class BridgeCodec
{
    public static string EncodeRequest(BridgeRequest request) =>
        JsonSerializer.Serialize(request, BridgeJsonContext.Default.BridgeRequest);

    public static JsonElement ToPayload<T>(T value, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.SerializeToElement(value, typeInfo);

    public static T? ReadPayload<T>(BridgeResponse response, JsonTypeInfo<T> typeInfo) =>
        response.Payload is { ValueKind: JsonValueKind.Object or JsonValueKind.Array } p
            ? p.Deserialize(typeInfo)
            : default;

    /// <summary>
    /// Classifies an inbound native message. Returns false for anything that is not a well-formed
    /// protocol v1 response or event (callers count it as malformed; it is never treated as success).
    /// </summary>
    public static bool TryDecodeInbound(string json, out BridgeResponse? response, out BridgeEvent? evt)
    {
        response = null;
        evt = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("protocol", out var proto)
                || proto.ValueKind != JsonValueKind.Number
                || proto.GetInt32() != BridgeProtocol.Version)
            {
                return false;
            }

            if (root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "event")
            {
                evt = root.Deserialize(BridgeJsonContext.Default.BridgeEvent);
                return evt is { Event.Length: > 0 };
            }

            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            response = root.Deserialize(BridgeJsonContext.Default.BridgeResponse);
            // ok=true must carry code OK, and vice versa; anything else is a contract violation.
            return response is not null && response.Ok == (response.Code == BridgeErrorCodes.Ok);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }
}
