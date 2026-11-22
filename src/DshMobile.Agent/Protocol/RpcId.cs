using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshMobile.Agent.Protocol;

/// <summary>
/// Wire identifier supporting both string and numeric JSON RPC IDs under System.Text.Json Source Generation (N-01-04).
/// </summary>
[JsonConverter(typeof(RpcIdJsonConverter))]
public sealed record RpcId
{
    public string? StringValue { get; }
    public long? NumberValue { get; }
    public bool IsNumeric { get; }

    public RpcId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        StringValue = value;
        NumberValue = null;
        IsNumeric = false;
    }

    public RpcId(long value)
    {
        StringValue = null;
        NumberValue = value;
        IsNumeric = true;
    }

    public static RpcId FromString(string value) => new(value);

    public static RpcId FromInt64(long value) => new(value);

    public override string ToString() =>
        IsNumeric
            ? (NumberValue ?? 0).ToString(CultureInfo.InvariantCulture)
            : StringValue ?? string.Empty;

    internal sealed class RpcIdJsonConverter : JsonConverter<RpcId>
    {
        public override RpcId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                JsonTokenType.String => new RpcId(reader.GetString() ?? string.Empty),
                JsonTokenType.Number when reader.TryGetInt64(out var num) => new RpcId(num),
                _ => throw new JsonException($"Unsupported RpcId JSON token '{reader.TokenType}'."),
            };
        }

        public override void Write(Utf8JsonWriter writer, RpcId value, JsonSerializerOptions options)
        {
            if (value.IsNumeric)
            {
                writer.WriteNumberValue(value.NumberValue ?? 0);
            }
            else
            {
                writer.WriteStringValue(value.StringValue ?? string.Empty);
            }
        }
    }
}
