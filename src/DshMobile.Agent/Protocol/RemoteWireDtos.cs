using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshMobile.Agent.Protocol;

/// <summary>
/// Minimal RPC envelope DTO verifying System.Text.Json Source Generation under TrimMode=full (PR-00).
/// </summary>
public sealed record ProtocolHandshakeProbe(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("rpcId")] string RpcId,
    [property: JsonPropertyName("method")] string Method);

/// <summary>Wrapper carrying exact named wire arguments (`{ "args": { ... } }`).</summary>
public sealed record RemoteArgsPayload(
    [property: JsonPropertyName("args")] JsonElement Args);

/// <summary>Unary HTTP RPC request envelope for `POST /api/&lt;endpoint&gt;`.</summary>
public sealed record RemoteClientRequest(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("rpcId")] string RpcId,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("payload")] RemoteArgsPayload Payload);

/// <summary>Structured Remote RPC failure object (`code`, `message`, `details`/`data`).</summary>
public sealed record RemoteRpcError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("details")] JsonElement? Details = null,
    [property: JsonPropertyName("data")] JsonElement? Data = null);

/// <summary>Outcome of a unary RPC (`ok: true` with `value`, or `ok: false` with `error`).</summary>
public sealed record RemoteRpcResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("value")] JsonElement? Value = null,
    [property: JsonPropertyName("error")] RemoteRpcError? Error = null);

/// <summary>Unary HTTP RPC JSON response envelope (`type: "server-response"`).</summary>
public sealed record RemoteServerResponse(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("rpcId")] string RpcId,
    [property: JsonPropertyName("result")] RemoteRpcResult Result);

/// <summary>Metadata entry describing one binary part inside a `multipart/form-data` RPC response.</summary>
public sealed record RemoteBinaryAttachmentMeta(
    [property: JsonPropertyName("path")] IReadOnlyList<JsonElement> Path,
    [property: JsonPropertyName("codec")] string Codec,
    [property: JsonPropertyName("part")] string Part);

/// <summary>Metadata JSON part of a `multipart/form-data` unary RPC response.</summary>
public sealed record RemoteBinaryResponseEnvelope(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("rpcId")] string RpcId,
    [property: JsonPropertyName("result")] RemoteRpcResult Result,
    [property: JsonPropertyName("attachments")] IReadOnlyList<RemoteBinaryAttachmentMeta>? Attachments = null);

/// <summary>Client-to-Host WebSocket frame opening a logical stream on `/api/remote.mux`.</summary>
public sealed record MuxOpenFrame(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("streamId")] string StreamId,
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("payload")] RemoteArgsPayload Payload);

/// <summary>Client-to-Host WebSocket control frame (`cancel` or `end`) on `/api/remote.mux`.</summary>
public sealed record MuxControlFrame(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("streamId")] string StreamId);

/// <summary>Client-to-Host WebSocket uplink item frame on `/api/remote.mux`.</summary>
public sealed record MuxItemFrame(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("streamId")] string StreamId,
    [property: JsonPropertyName("value")] JsonElement? Value = null);

/// <summary>Host-to-Client WebSocket frame (`item`, `end`, or `error`) on `/api/remote.mux`.</summary>
public sealed record MuxInboundFrame(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("streamId")] string StreamId,
    [property: JsonPropertyName("value")] JsonElement? Value = null,
    [property: JsonPropertyName("error")] RemoteRpcError? Error = null);

/// <summary>Host facts included in the `$events` opening `ready` frame.</summary>
public sealed record RemoteHostInfo(
    [property: JsonPropertyName("home")] string? Home = null);

/// <summary>First item emitted on the `$events` logical stream proving Host event readiness.</summary>
public sealed record RemoteEventsReadyPayload(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("clientId")] string? ClientId = null,
    [property: JsonPropertyName("host")] RemoteHostInfo? Host = null);

/// <summary>Forwarded `$events` item (`emit` or `waterfall`) emitted after `ready`.</summary>
public sealed record RemoteForwardedEventPayload(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("event")] string? Event = null,
    [property: JsonPropertyName("args")] JsonElement? Args = null,
    [property: JsonPropertyName("eventId")] string? EventId = null,
    [property: JsonPropertyName("agentId")] string? AgentId = null);

/// <summary>Rejection payload returned via `$events/result` for a forwarded waterfall request.</summary>
public sealed record RemoteEventRejection(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("code")] string? Code = null,
    [property: JsonPropertyName("details")] JsonElement? Details = null);

/// <summary>Outcome variant (`next`, `result`, or `rejected`) for `$events/result`.</summary>
public sealed record RemoteEventOutcome(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("value")] JsonElement? Value = null,
    [property: JsonPropertyName("error")] RemoteEventRejection? Error = null);

/// <summary>Arguments for `POST /api/$events/result`.</summary>
public sealed record RemoteEventResultArgs(
    [property: JsonPropertyName("clientId")] string ClientId,
    [property: JsonPropertyName("eventId")] string EventId,
    [property: JsonPropertyName("outcome")] RemoteEventOutcome Outcome);
