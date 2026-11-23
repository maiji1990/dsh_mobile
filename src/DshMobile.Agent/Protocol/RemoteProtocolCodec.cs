using System.Collections.Frozen;
using System.Text.Json;
using DshMobile.Agent.Domain;

namespace DshMobile.Agent.Protocol;

/// <summary>
/// Pure SourceGen wire-to-domain decoders and encoders for Route B controllers (N-02-03, N-02-04).
/// Tolerates unknown wire fields and unknown event types without throwing.
/// </summary>
public static class RemoteProtocolCodec
{
    private static readonly FrozenSet<string> KnownSessionEventTypes = FrozenSet.ToFrozenSet(
        [
            "agent-preset/selected",
            "agent/inbox/spliced",
            "approval/asked",
            "approval/decided",
            "approval/policy",
            "assistant/attempt",
            "assistant/message",
            "command/done",
            "command/run",
            "compaction/end",
            "compaction/prune",
            "compaction/start",
            "compaction/summary",
            "deliverables/presented",
            "developer/message",
            "feedback/message-delete",
            "feedback/message-put",
            "feedback/record",
            "goal/change",
            "hook/invoked",
            "hook/result",
            "image/offload",
            "job/registered",
            "job/settled",
            "llm/retry",
            "llm/retry-started",
            "model/selection",
            "permission/preset",
            "plan/mode",
            "request/context",
            "request/header",
            "sandbox/mode",
            "schedule/change",
            "session-log-deepseek/delivery-accepted",
            "session/end-seed",
            "session/title",
            "session/title-llm-request",
            "step/end",
            "step/start",
            "subagent/catalog",
            "subagent/descriptor",
            "subagent/model-selection-policy",
            "system/message",
            "team/member",
            "team/message/delivered",
            "team/message/queued",
            "team/mode",
            "team/task",
            "todo/write",
            "tool-workflow/agent-end",
            "tool-workflow/agent-start",
            "tool-workflow/run-end",
            "tool-workflow/run-start",
            "tool/call",
            "tool/ptc-dispatch",
            "tool/ptc-dispatch-start",
            "tool/result",
            "turn/end",
            "turn/start",
            "user/message",
            "web/deepseek-search-llm-request",
            "working-directory/change",
            "workspace/changes",
        ],
        StringComparer.Ordinal);

    public static bool IsKnownSessionEventType(string eventType) =>
        KnownSessionEventTypes.Contains(eventType);

    public static JsonElement SerializeSessionList(string? cursor = null) =>
        JsonSerializer.SerializeToElement(
            new WireSessionListArgs(new WireSessionListRequest(cursor)),
            AgentProtocolJsonContext.Default.WireSessionListArgs);

    public static JsonElement SerializeSessionCreate(CreateOrResumeSessionRequest request)
    {
        var dto = new WireSessionCreateArgs(new WireSessionCreateRequest(
            WorkspaceId: string.IsNullOrWhiteSpace(request.WorkspaceId) ? null : request.WorkspaceId,
            Cwd: string.IsNullOrWhiteSpace(request.Cwd) ? null : request.Cwd,
            SessionId: string.IsNullOrWhiteSpace(request.SessionId) ? null : request.SessionId,
            AgentPreset: string.IsNullOrWhiteSpace(request.AgentPreset) ? null : request.AgentPreset));
        return JsonSerializer.SerializeToElement(dto, AgentProtocolJsonContext.Default.WireSessionCreateArgs);
    }

    public static JsonElement SerializeSessionPrompt(PromptSendRequest request, string clientTimeZone)
    {
        var parts = new List<WirePromptPartDto>(1 + (request.Images?.Count ?? 0));
        if (!string.IsNullOrEmpty(request.Text))
        {
            parts.Add(new WirePromptPartDto("text", Text: request.Text));
        }
        if (request.Images is not null)
        {
            foreach (var img in request.Images)
            {
                parts.Add(new WirePromptPartDto(
                    "image",
                    MediaType: img.MediaType,
                    Data: img.Base64Data,
                    Name: string.IsNullOrWhiteSpace(img.Name) ? null : img.Name));
            }
        }

        var mode = request.Mode == PromptDeliveryMode.Steer ? "steer" : "queue";
        var dto = new WireSessionPromptArgs(new WireSessionPromptRequest(
            request.RequestId,
            request.SessionId,
            mode,
            parts,
            clientTimeZone));
        return JsonSerializer.SerializeToElement(dto, AgentProtocolJsonContext.Default.WireSessionPromptArgs);
    }

    public static JsonElement SerializeCommandsExecute(
        string sessionId,
        string line,
        IReadOnlyList<PromptAttachmentImage>? images)
    {
        var attachments = new List<WirePromptPartDto>(images?.Count ?? 0);
        if (images is not null)
        {
            foreach (var img in images)
            {
                attachments.Add(new WirePromptPartDto(
                    "image",
                    MediaType: img.MediaType,
                    Data: img.Base64Data,
                    Name: string.IsNullOrWhiteSpace(img.Name) ? null : img.Name));
            }
        }
        var dto = new WireCommandsExecuteArgs(sessionId, line, attachments);
        return JsonSerializer.SerializeToElement(dto, AgentProtocolJsonContext.Default.WireCommandsExecuteArgs);
    }

    public static TurnEndReasonInfo ParseTurnEndReason(JsonElement turnEndData)
    {
        if (turnEndData.ValueKind != JsonValueKind.Object)
        {
            return new TurnEndReasonInfo(TurnEndReasonKind.Unknown, "invalid");
        }

        var parsed = turnEndData.Deserialize(AgentProtocolJsonContext.Default.WireTurnEndDataDto);
        var kind = parsed?.Reason?.Kind;
        if (string.IsNullOrWhiteSpace(kind))
        {
            return new TurnEndReasonInfo(TurnEndReasonKind.Unknown, "missing");
        }

        return kind switch
        {
            "completed" => new TurnEndReasonInfo(TurnEndReasonKind.Completed, kind),
            "aborted" => new TurnEndReasonInfo(TurnEndReasonKind.Aborted, kind),
            "blocked" => new TurnEndReasonInfo(TurnEndReasonKind.Blocked, kind),
            "error" => new TurnEndReasonInfo(
                TurnEndReasonKind.Error,
                kind,
                parsed?.Reason?.Error?.Code,
                parsed?.Reason?.Error?.Message),
            "max-tokens" => new TurnEndReasonInfo(TurnEndReasonKind.MaxTokens, kind),
            "interrupted" => new TurnEndReasonInfo(TurnEndReasonKind.Interrupted, kind),
            "forked" => new TurnEndReasonInfo(TurnEndReasonKind.Forked, kind),
            _ => new TurnEndReasonInfo(TurnEndReasonKind.Unknown, kind),
        };
    }

    public static SessionWireEventView MapWireEvent(WireSessionWireEvent wire) =>
        new(
            wire.Type,
            wire.Seq,
            wire.Time,
            wire.Data.Clone(),
            wire.Ignorable == true,
            IsKnownSessionEventType(wire.Type));

    public static SessionStateView MapState(WireSessionStateValue state) =>
        new(
            state.Cursor,
            state.Active,
            state.Running,
            state.Queue?.Count ?? 0,
            state.PendingInput ?? []);

    public static SessionFollowUpdate DecodeFollowFrame(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return new SessionFollowUnknownUpdate("unexpected-frame-kind");
        }
        var frame = value.Deserialize(AgentProtocolJsonContext.Default.WireSessionFollowFrame);
        if (frame is null || string.IsNullOrWhiteSpace(frame.Type))
        {
            return new SessionFollowUnknownUpdate("missing-type");
        }

        return frame.Type switch
        {
            "snapshot" => new SessionFollowSnapshotUpdate(
                frame.Header?.Id ?? string.Empty,
                frame.Cursor ?? 0,
                frame.HasMore ?? false,
                (frame.Records ?? []).Select(r => MapWireEvent(r.Event)).ToArray()),
            "event" when frame.Event is not null =>
                new SessionFollowEventUpdate(MapWireEvent(frame.Event)),
            "assistant-stream" when frame.Frame is { ValueKind: JsonValueKind.Object } streamEl =>
                new SessionFollowAssistantStreamUpdate(
                    TryGetString(streamEl, "type") ?? "unknown",
                    TryGetString(streamEl, "attemptId") ?? string.Empty,
                    TryGetInt64(streamEl, "revision") ?? 0,
                    streamEl.Clone()),
            _ => new SessionFollowUnknownUpdate(frame.Type),
        };
    }

    public static ForwardedGatewayEvent DecodeForwardedEvent(RemoteForwardedEventPayload payload)
    {
        if (payload.Type == "cancel" && !string.IsNullOrEmpty(payload.EventId))
        {
            return new ForwardedEventCancelledGatewayEvent(payload.EventId);
        }

        var evtName = payload.Event ?? string.Empty;
        if (evtName == "approval/request" && !string.IsNullOrEmpty(payload.EventId))
        {
            // 网关 `waterfall` 帧载体位于 `request` 字段，兼容测试/旧包装中的 `args` 字段。
            var reqEl = ExtractFirstObject(payload.Request ?? payload.Args);
            var req = reqEl?.Deserialize(AgentProtocolJsonContext.Default.WireApprovalRequestPayload);
            return new ApprovalGatewayEvent(new ApprovalRequestView(
                payload.EventId,
                payload.AgentId ?? string.Empty,
                req?.ToolName ?? "unknown",
                req?.CallId,
                req?.Reason));
        }

        if (evtName == "user-questions/request" && !string.IsNullOrEmpty(payload.EventId))
        {
            var reqEl = ExtractFirstObject(payload.Request ?? payload.Args);
            var req = reqEl?.Deserialize(AgentProtocolJsonContext.Default.WireUserQuestionRequestPayload);
            var items = (req?.Questions ?? [])
                .Select(q => new UserQuestionItemView(
                    q.Id,
                    q.Question,
                    q.Detail,
                    q.Header,
                    (q.Options ?? []).Select(o => new UserQuestionOptionView(o.Label, o.Description)).ToArray(),
                    q.MultiSelect ?? false))
                .ToArray();
            return new UserQuestionGatewayEvent(new UserQuestionRequestView(
                payload.EventId,
                payload.AgentId ?? string.Empty,
                req?.CallId,
                items));
        }

        if (evtName == "api-session/status" &&
            TryGetArrayElement(payload.Args, 0) is { ValueKind: JsonValueKind.String } sIdEl &&
            TryGetArrayElement(payload.Args, 1) is { ValueKind: JsonValueKind.True or JsonValueKind.False } runEl)
        {
            return new SessionStatusGatewayEvent(sIdEl.GetString() ?? string.Empty, runEl.GetBoolean());
        }

        if (evtName == "api-session/activity" &&
            TryGetArrayElement(payload.Args, 0) is { ValueKind: JsonValueKind.String } actSidEl &&
            TryGetArrayElement(payload.Args, 1) is { ValueKind: JsonValueKind.Number } tsEl &&
            tsEl.TryGetInt64(out var ts))
        {
            return new SessionActivityGatewayEvent(actSidEl.GetString() ?? string.Empty, ts);
        }

        if (evtName == "api-session/error" &&
            TryGetArrayElement(payload.Args, 0) is { ValueKind: JsonValueKind.String } errSidEl &&
            TryGetArrayElement(payload.Args, 1) is { ValueKind: JsonValueKind.String } msgEl)
        {
            return new SessionErrorGatewayEvent(errSidEl.GetString() ?? string.Empty, msgEl.GetString() ?? string.Empty);
        }

        if (evtName == "api-session/removed" &&
            TryGetArrayElement(payload.Args, 0) is { ValueKind: JsonValueKind.String } remSidEl)
        {
            return new SessionRemovedGatewayEvent(remSidEl.GetString() ?? string.Empty);
        }

        return new UnknownGatewayEvent(evtName, payload.Type);
    }

    public static string MapApprovalDecisionWireValue(ApprovalDecision decision) =>
        decision switch
        {
            ApprovalDecision.AllowedOnce => "allowed-once",
            ApprovalDecision.Cancelled => "cancelled",
            _ => "rejected",
        };

    private static JsonElement? ExtractFirstObject(JsonElement? args)
    {
        if (args is not { } el)
        {
            return null;
        }
        if (el.ValueKind == JsonValueKind.Object)
        {
            return el;
        }
        if (el.ValueKind == JsonValueKind.Array && el.GetArrayLength() > 0 && el[0].ValueKind == JsonValueKind.Object)
        {
            return el[0];
        }
        return null;
    }

    private static JsonElement? TryGetArrayElement(JsonElement? element, int index)
    {
        if (element is not { ValueKind: JsonValueKind.Array } arr || index < 0 || index >= arr.GetArrayLength())
        {
            return null;
        }
        return arr[index];
    }

    private static string? TryGetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? TryGetInt64(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    public static string? ResolveLocalizedText(JsonElement? element, string preferredLocale = "zh")
    {
        if (element is not { } el)
        {
            return null;
        }
        if (el.ValueKind == JsonValueKind.String)
        {
            return el.GetString();
        }
        if (el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (TryGetString(el, preferredLocale) is { } localized)
        {
            return localized;
        }
        if (TryGetString(el, "en") is { } en)
        {
            return en;
        }
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                return prop.Value.GetString();
            }
        }
        return null;
    }
}
