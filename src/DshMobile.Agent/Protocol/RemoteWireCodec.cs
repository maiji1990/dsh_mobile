using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DshMobile.Agent.Protocol;

/// <summary>
/// Source-generated wire codec and strict RFC 2046 multipart parser for Route B `/api` and `/api/remote.mux`.
/// Uses <see cref="AgentProtocolJsonContext"/> exclusively (zero reflection under TrimMode=full).
/// </summary>
public static class RemoteWireCodec
{
    public static readonly JsonElement EmptyArgs = CreateEmptyArgs();

    private static JsonElement CreateEmptyArgs()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    public static string EncodeClientRequest(string rpcId, string endpoint, JsonElement? args = null)
    {
        var req = new RemoteClientRequest(
            "client-request",
            rpcId,
            endpoint,
            new RemoteArgsPayload(args ?? EmptyArgs));
        return JsonSerializer.Serialize(req, AgentProtocolJsonContext.Default.RemoteClientRequest);
    }

    public static RemoteServerResponse DecodeServerResponse(ReadOnlySpan<byte> utf8Json)
    {
        var response = JsonSerializer.Deserialize(utf8Json, AgentProtocolJsonContext.Default.RemoteServerResponse)
            ?? throw new JsonException("server-response payload was null");
        if (response.Type != "server-response")
        {
            throw new JsonException($"unexpected RPC response type '{response.Type}'");
        }
        return response;
    }

    public static string EncodeMuxOpen(string streamId, string endpoint, JsonElement? args = null)
    {
        var frame = new MuxOpenFrame("open", streamId, endpoint, new RemoteArgsPayload(args ?? EmptyArgs));
        return JsonSerializer.Serialize(frame, AgentProtocolJsonContext.Default.MuxOpenFrame);
    }

    public static string EncodeMuxCancel(string streamId)
    {
        var frame = new MuxControlFrame("cancel", streamId);
        return JsonSerializer.Serialize(frame, AgentProtocolJsonContext.Default.MuxControlFrame);
    }

    public static string EncodeMuxEnd(string streamId)
    {
        var frame = new MuxControlFrame("end", streamId);
        return JsonSerializer.Serialize(frame, AgentProtocolJsonContext.Default.MuxControlFrame);
    }

    public static string EncodeMuxItem(string streamId, JsonElement value)
    {
        var frame = new MuxItemFrame("item", streamId, value);
        return JsonSerializer.Serialize(frame, AgentProtocolJsonContext.Default.MuxItemFrame);
    }

    public static MuxInboundFrame DecodeMuxInbound(ReadOnlySpan<byte> utf8Json)
    {
        var frame = JsonSerializer.Deserialize(utf8Json, AgentProtocolJsonContext.Default.MuxInboundFrame)
            ?? throw new JsonException("mux inbound frame was null");
        if (frame.Type is not ("item" or "end" or "error") || string.IsNullOrEmpty(frame.StreamId))
        {
            throw new JsonException("invalid mux inbound frame");
        }
        return frame;
    }

    public static RemoteEventsReadyPayload? TryDecodeEventsReady(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return value.Deserialize(AgentProtocolJsonContext.Default.RemoteEventsReadyPayload);
    }

    public static RemoteForwardedEventPayload? TryDecodeForwardedEvent(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return value.Deserialize(AgentProtocolJsonContext.Default.RemoteForwardedEventPayload);
    }

    public static JsonElement EncodeEventResultArgs(RemoteEventResultArgs args)
    {
        return JsonSerializer.SerializeToElement(args, AgentProtocolJsonContext.Default.RemoteEventResultArgs);
    }

    /// <summary>
    /// Parses a `multipart/form-data` binary RPC response produced by `@deepseek-ai/dsh-client-connection`:
    /// one `metadata` JSON part plus `bytes-*` binary parts.
    /// </summary>
    public static (RemoteBinaryResponseEnvelope Envelope, Dictionary<string, ReadOnlyMemory<byte>> Parts) DecodeMultipartResponse(
        MediaTypeHeaderValue contentType,
        ReadOnlyMemory<byte> body)
    {
        var boundary = contentType.Parameters
            .FirstOrDefault(p => string.Equals(p.Name, "boundary", StringComparison.OrdinalIgnoreCase))
            ?.Value?.Trim('"');
        if (string.IsNullOrEmpty(boundary))
        {
            throw new InvalidDataException("multipart/form-data response missing boundary");
        }

        var parts = ParseMultipartParts(body, boundary);
        if (!parts.Remove("metadata", out var metadataBytes))
        {
            throw new InvalidDataException("multipart/form-data response missing metadata part");
        }

        var envelope = JsonSerializer.Deserialize(metadataBytes.Span, AgentProtocolJsonContext.Default.RemoteBinaryResponseEnvelope)
            ?? throw new InvalidDataException("multipart metadata JSON was null");
        if (envelope.Type != "server-response")
        {
            throw new InvalidDataException($"unexpected multipart response type '{envelope.Type}'");
        }

        return (envelope, parts);
    }

    private static Dictionary<string, ReadOnlyMemory<byte>> ParseMultipartParts(ReadOnlyMemory<byte> body, string boundary)
    {
        var dashBoundary = Encoding.ASCII.GetBytes("--" + boundary);
        var span = body.Span;
        var result = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);

        var pos = span.IndexOf(dashBoundary);
        if (pos < 0)
        {
            throw new InvalidDataException("multipart boundary not found in body");
        }

        while (true)
        {
            pos += dashBoundary.Length;
            if (pos + 1 < span.Length && span[pos] == (byte)'-' && span[pos + 1] == (byte)'-')
            {
                break; // closing `--boundary--`
            }

            if (pos + 1 < span.Length && span[pos] == (byte)'\r' && span[pos + 1] == (byte)'\n')
            {
                pos += 2;
            }
            else if (pos < span.Length && span[pos] == (byte)'\n')
            {
                pos += 1;
            }
            else
            {
                throw new InvalidDataException("malformed multipart boundary delimiter");
            }

            var headerEnd = FindHeaderSeparator(span[pos..], out var sepLen);
            if (headerEnd < 0)
            {
                throw new InvalidDataException("malformed multipart part headers");
            }

            var headersText = Encoding.UTF8.GetString(span.Slice(pos, headerEnd));
            var partName = ExtractPartName(headersText)
                ?? throw new InvalidDataException("multipart part missing Content-Disposition name");

            var contentStart = pos + headerEnd + sepLen;
            var nextBoundaryOffset = span[contentStart..].IndexOf(dashBoundary);
            if (nextBoundaryOffset < 0)
            {
                throw new InvalidDataException("unterminated multipart part");
            }

            var contentEnd = contentStart + nextBoundaryOffset;
            if (contentEnd >= contentStart + 2 && span[contentEnd - 2] == (byte)'\r' && span[contentEnd - 1] == (byte)'\n')
            {
                contentEnd -= 2;
            }
            else if (contentEnd >= contentStart + 1 && span[contentEnd - 1] == (byte)'\n')
            {
                contentEnd -= 1;
            }

            if (!result.TryAdd(partName, body[contentStart..contentEnd]))
            {
                throw new InvalidDataException($"duplicate multipart part '{partName}'");
            }

            pos = contentStart + nextBoundaryOffset;
        }

        return result;
    }

    private static int FindHeaderSeparator(ReadOnlySpan<byte> span, out int separatorLength)
    {
        ReadOnlySpan<byte> crlfCrlf = "\r\n\r\n"u8;
        ReadOnlySpan<byte> lfLf = "\n\n"u8;
        var crlfIdx = span.IndexOf(crlfCrlf);
        var lfIdx = span.IndexOf(lfLf);
        if (crlfIdx >= 0 && (lfIdx < 0 || crlfIdx <= lfIdx))
        {
            separatorLength = 4;
            return crlfIdx;
        }
        if (lfIdx >= 0)
        {
            separatorLength = 2;
            return lfIdx;
        }
        separatorLength = 0;
        return -1;
    }

    private static string? ExtractPartName(string headersText)
    {
        foreach (var line in headersText.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (!trimmed.StartsWith("Content-Disposition:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            foreach (var segment in trimmed.Split(';'))
            {
                var part = segment.Trim();
                if (part.StartsWith("name=", StringComparison.OrdinalIgnoreCase))
                {
                    return part["name=".Length..].Trim().Trim('"');
                }
            }
        }
        return null;
    }
}
