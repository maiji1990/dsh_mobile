using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Protocol;

namespace DshMobile.Agent.Tests.Protocol;

public sealed class RemoteWireCodecTests
{
    [Fact]
    public void RpcId_RoundTripsStringAndNumericForms_UsingSourceGenContext()
    {
        var strId = RpcId.FromString("rpc-42");
        var numId = RpcId.FromInt64(9001);

        var strJson = JsonSerializer.Serialize(strId, AgentProtocolJsonContext.Default.RpcId);
        var numJson = JsonSerializer.Serialize(numId, AgentProtocolJsonContext.Default.RpcId);

        Assert.Equal("\"rpc-42\"", strJson);
        Assert.Equal("9001", numJson);

        var parsedStr = JsonSerializer.Deserialize(strJson, AgentProtocolJsonContext.Default.RpcId);
        var parsedNum = JsonSerializer.Deserialize(numJson, AgentProtocolJsonContext.Default.RpcId);

        Assert.NotNull(parsedStr);
        Assert.NotNull(parsedNum);
        Assert.False(parsedStr.IsNumeric);
        Assert.Equal("rpc-42", parsedStr.StringValue);
        Assert.True(parsedNum.IsNumeric);
        Assert.Equal(9001L, parsedNum.NumberValue);

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize("true", AgentProtocolJsonContext.Default.RpcId));
    }

    [Fact]
    public void RemoteRpcError_DecodesCodeMessageDetailsAndData_UsingSourceGen()
    {
        const string json = """
            {"code":"E_AUTH","message":"bad token","details":{"retryable":true},"data":{"hint":"reauth"}}
            """;

        var error = JsonSerializer.Deserialize(json, AgentProtocolJsonContext.Default.RemoteRpcError);
        Assert.NotNull(error);
        Assert.Equal("E_AUTH", error.Code);
        Assert.Equal("bad token", error.Message);
        Assert.True(error.Details!.Value.GetProperty("retryable").GetBoolean());
        Assert.Equal("reauth", error.Data!.Value.GetProperty("hint").GetString());
    }

    [Fact]
    public void EncodeAndDecodeUnaryRequestAndResponse_UsesStrictShape()
    {
        using var argsDoc = JsonDocument.Parse("""{"sessionId":"s-1","limit":10}""");
        var encoded = RemoteWireCodec.EncodeClientRequest("rpc-1", "session.list", argsDoc.RootElement);
        using var parsed = JsonDocument.Parse(encoded);
        Assert.Equal("client-request", parsed.RootElement.GetProperty("type").GetString());
        Assert.Equal("rpc-1", parsed.RootElement.GetProperty("rpcId").GetString());
        Assert.Equal("session.list", parsed.RootElement.GetProperty("method").GetString());
        Assert.Equal("s-1", parsed.RootElement.GetProperty("payload").GetProperty("args").GetProperty("sessionId").GetString());

        var responseBytes = """
            {"type":"server-response","rpcId":"rpc-1","result":{"ok":true,"value":{"count":2}}}
            """u8.ToArray();
        var decoded = RemoteWireCodec.DecodeServerResponse(responseBytes);
        Assert.Equal("rpc-1", decoded.RpcId);
        Assert.True(decoded.Result.Ok);
        Assert.Equal(2, decoded.Result.Value!.Value.GetProperty("count").GetInt32());
    }

    [Fact]
    public void DecodeMultipartResponse_ExtractsMetadataAndBinaryParts()
    {
        const string boundary = "----dsh-boundary-123";
        var metadata = """
            {"type":"server-response","rpcId":"rpc-7","result":{"ok":true,"value":{"mime":"image/png"}},"attachments":[{"path":["data"],"codec":"uint8array","part":"bytes-0"}]}
            """;
        var binary = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        using var ms = new MemoryStream();
        void WriteAscii(string s) => ms.Write(Encoding.UTF8.GetBytes(s));
        WriteAscii($"--{boundary}\r\nContent-Disposition: form-data; name=\"metadata\"\r\nContent-Type: application/json\r\n\r\n{metadata}\r\n");
        WriteAscii($"--{boundary}\r\nContent-Disposition: form-data; name=\"bytes-0\"\r\nContent-Type: application/octet-stream\r\n\r\n");
        ms.Write(binary);
        WriteAscii($"\r\n--{boundary}--\r\n");

        var contentType = MediaTypeHeaderValue.Parse($"multipart/form-data; boundary=\"{boundary}\"");
        var (envelope, parts) = RemoteWireCodec.DecodeMultipartResponse(contentType, ms.ToArray());

        Assert.Equal("rpc-7", envelope.RpcId);
        Assert.True(envelope.Result.Ok);
        Assert.Single(envelope.Attachments!);
        Assert.True(parts.TryGetValue("bytes-0", out var decodedBytes));
        Assert.Equal(binary, decodedBytes.ToArray());
    }

    [Fact]
    public void DecodeForwardedEvent_HandlesWaterfallRequestAndCancelFrames()
    {
        // F9: 真实网关 waterfall 帧使用 `request` 字段承载 toolName/callId/reason
        var waterfallJson = JsonElement.Parse("""
            {
              "type": "waterfall",
              "event": "approval/request",
              "eventId": "wf-ev-1",
              "agentId": "sess-9",
              "request": {
                "toolName": "bash",
                "callId": "call-wf-9",
                "reason": "needs shell"
              }
            }
            """);
        var waterfallPayload = RemoteWireCodec.TryDecodeForwardedEvent(waterfallJson);
        Assert.NotNull(waterfallPayload);
        var mapped = RemoteProtocolCodec.DecodeForwardedEvent(waterfallPayload!);
        var ap = Assert.IsType<ApprovalGatewayEvent>(mapped);
        Assert.Equal("wf-ev-1", ap.Request.EventId);
        Assert.Equal("sess-9", ap.Request.AgentId);
        Assert.Equal("bash", ap.Request.ToolName);
        Assert.Equal("call-wf-9", ap.Request.CallId);
        Assert.Equal("needs shell", ap.Request.Reason);

        var cancelJson = JsonElement.Parse("""
            {"type":"cancel","eventId":"wf-ev-1"}
            """);
        var cancelPayload = RemoteWireCodec.TryDecodeForwardedEvent(cancelJson);
        Assert.NotNull(cancelPayload);
        var cancelled = Assert.IsType<ForwardedEventCancelledGatewayEvent>(RemoteProtocolCodec.DecodeForwardedEvent(cancelPayload!));
        Assert.Equal("wf-ev-1", cancelled.EventId);

        Assert.True(RemoteProtocolCodec.IsKnownSessionEventType("agent/inbox/spliced"));
        Assert.True(RemoteProtocolCodec.IsKnownSessionEventType("session/title"));
        Assert.True(RemoteProtocolCodec.IsKnownSessionEventType("workspace/changes"));
    }
}
