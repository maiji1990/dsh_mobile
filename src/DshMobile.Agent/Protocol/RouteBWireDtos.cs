using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshMobile.Agent.Protocol;

public sealed record WireSessionListRequest(
    [property: JsonPropertyName("cursor")] string? Cursor = null);

public sealed record WireSessionListArgs(
    [property: JsonPropertyName("_request")] WireSessionListRequest Request);

public sealed record WireSessionProjectionValues(
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("agentPreset")] string? AgentPreset = null);

public sealed record WireSessionProjections(
    [property: JsonPropertyName("values")] WireSessionProjectionValues? Values = null);

public sealed record WireSessionSummary(
    [property: JsonPropertyName("agentAvailable")] bool AgentAvailable,
    [property: JsonPropertyName("formatStatus")] string? FormatStatus,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("updatedAt")] long UpdatedAt,
    [property: JsonPropertyName("running")] bool Running,
    [property: JsonPropertyName("blank")] bool Blank,
    [property: JsonPropertyName("parentSessionId")] string? ParentSessionId = null,
    [property: JsonPropertyName("origin")] string? Origin = null,
    [property: JsonPropertyName("cwd")] string? Cwd = null,
    [property: JsonPropertyName("projections")] WireSessionProjections? Projections = null);

public sealed record WireSessionListValue(
    [property: JsonPropertyName("items")] IReadOnlyList<WireSessionSummary> Items);

public sealed record WireSessionCreateRequest(
    [property: JsonPropertyName("workspaceId")] string? WorkspaceId = null,
    [property: JsonPropertyName("cwd")] string? Cwd = null,
    [property: JsonPropertyName("sessionId")] string? SessionId = null,
    [property: JsonPropertyName("agentPreset")] string? AgentPreset = null);

public sealed record WireSessionCreateArgs(
    [property: JsonPropertyName("request")] WireSessionCreateRequest Request);

public sealed record WireSessionCreateValue(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("agentPreset")] string? AgentPreset = null);

public sealed record WireSessionIdRequest(
    [property: JsonPropertyName("sessionId")] string SessionId);

public sealed record WireSessionStateArgs(
    [property: JsonPropertyName("request")] WireSessionIdRequest Request);

public sealed record WireSessionQueuedItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("placement")] string Placement,
    [property: JsonPropertyName("rpcId")] string? RpcId = null);

public sealed record WireSessionStateValue(
    [property: JsonPropertyName("cursor")] long Cursor,
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("running")] bool Running,
    [property: JsonPropertyName("queue")] IReadOnlyList<WireSessionQueuedItem>? Queue = null,
    [property: JsonPropertyName("pendingInput")] IReadOnlyList<string>? PendingInput = null);

public sealed record WireSessionAddress(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("sessionId")] string SessionId);

public sealed record WireSessionPageRequest(
    [property: JsonPropertyName("address")] WireSessionAddress Address,
    [property: JsonPropertyName("throughSeq")] long? ThroughSeq = null,
    [property: JsonPropertyName("beforeSeq")] long? BeforeSeq = null,
    [property: JsonPropertyName("maxMessages")] int? MaxMessages = null);

public sealed record WireSessionPageArgs(
    [property: JsonPropertyName("request")] WireSessionPageRequest Request);

public sealed record WireSessionWireEvent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("time")] long Time,
    [property: JsonPropertyName("data")] JsonElement Data,
    [property: JsonPropertyName("ignorable")] bool? Ignorable = null);

public sealed record WireSessionHistoryRecord(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("event")] WireSessionWireEvent Event);

public sealed record WireSessionEventsPage(
    [property: JsonPropertyName("records")] IReadOnlyList<WireSessionHistoryRecord> Records,
    [property: JsonPropertyName("hasMore")] bool HasMore,
    [property: JsonPropertyName("cursor")] long Cursor);

public sealed record WireSessionFollowRequest(
    [property: JsonPropertyName("address")] WireSessionAddress Address,
    [property: JsonPropertyName("maxMessages")] int? MaxMessages = null,
    [property: JsonPropertyName("activate")] bool? Activate = null,
    [property: JsonPropertyName("assistantStream")] bool? AssistantStream = null);

public sealed record WireSessionFollowArgs(
    [property: JsonPropertyName("request")] WireSessionFollowRequest Request);

public sealed record WireSessionHeader(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("createdAt")] long CreatedAt,
    [property: JsonPropertyName("cwd")] string? Cwd = null);

public sealed record WireSessionFollowFrame(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("header")] WireSessionHeader? Header = null,
    [property: JsonPropertyName("cursor")] long? Cursor = null,
    [property: JsonPropertyName("records")] IReadOnlyList<WireSessionHistoryRecord>? Records = null,
    [property: JsonPropertyName("hasMore")] bool? HasMore = null,
    [property: JsonPropertyName("event")] WireSessionWireEvent? Event = null,
    [property: JsonPropertyName("frame")] JsonElement? Frame = null);

public sealed record WireSessionWaitRequest(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("timeoutMs")] int? TimeoutMs = null,
    [property: JsonPropertyName("afterSeq")] long? AfterSeq = null,
    [property: JsonPropertyName("maxMessages")] int? MaxMessages = null);

public sealed record WireSessionWaitArgs(
    [property: JsonPropertyName("request")] WireSessionWaitRequest Request);

public sealed record WireSessionWaitOutput(
    [property: JsonPropertyName("records")] IReadOnlyList<WireSessionHistoryRecord> Records,
    [property: JsonPropertyName("truncated")] bool Truncated);

public sealed record WireSessionWaitValue(
    [property: JsonPropertyName("reason")] string? Reason = null,
    [property: JsonPropertyName("state")] WireSessionStateValue? State = null,
    [property: JsonPropertyName("output")] WireSessionWaitOutput? Output = null);

public sealed record WireSessionRenameRequest(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("title")] string Title);

public sealed record WireSessionRenameArgs(
    [property: JsonPropertyName("request")] WireSessionRenameRequest Request);

public sealed record WireSessionRenameValue(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("seq")] long Seq);

public sealed record WireSessionForkRequest(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("atSeq")] long? AtSeq = null,
    [property: JsonPropertyName("allowMigration")] bool? AllowMigration = null);

public sealed record WireSessionForkArgs(
    [property: JsonPropertyName("request")] WireSessionForkRequest Request);

public sealed record WireSessionForkValue(
    [property: JsonPropertyName("sessionId")] string SessionId);

public sealed record WirePromptPartDto(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("mediaType")] string? MediaType = null,
    [property: JsonPropertyName("data")] string? Data = null,
    [property: JsonPropertyName("name")] string? Name = null);

public sealed record WireSessionPromptRequest(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("content")] IReadOnlyList<WirePromptPartDto> Content,
    [property: JsonPropertyName("clientTimeZone")] string? ClientTimeZone = null);

public sealed record WireSessionPromptArgs(
    [property: JsonPropertyName("request")] WireSessionPromptRequest Request);

public sealed record WireSessionPromptValue(
    [property: JsonPropertyName("accepted")] bool Accepted);

public sealed record WireSessionCancelArgs(
    [property: JsonPropertyName("request")] WireSessionIdRequest Request);

public sealed record WireSessionCancelValue(
    [property: JsonPropertyName("accepted")] bool Accepted);

public sealed record WireCommandInputDescriptor(
    [property: JsonPropertyName("hint")] string Hint,
    [property: JsonPropertyName("attachments")] bool? Attachments = null);

public sealed record WireCommandDescriptor(
    [property: JsonPropertyName("definitionId")] string? DefinitionId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("input")] WireCommandInputDescriptor? Input = null);

public sealed record WireCommandResultDto(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("sourceEventSeq")] long? SourceEventSeq = null);

public sealed record WireCommandExecutionDto(
    [property: JsonPropertyName("commandId")] string CommandId,
    [property: JsonPropertyName("result")] WireCommandResultDto Result);

public sealed record WireCommandsListArgs(
    [property: JsonPropertyName("agentId")] string AgentId);

public sealed record WireCommandsExecuteArgs(
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("line")] string Line,
    [property: JsonPropertyName("submittedAttachments")] IReadOnlyList<WirePromptPartDto> SubmittedAttachments);

public sealed record WireModelSelection(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("reasoningEffort")] string? ReasoningEffort = null);

public sealed record WireSessionSelectModelRequest(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("reasoningEffort")] string? ReasoningEffort = null);

public sealed record WireSessionSelectModelArgs(
    [property: JsonPropertyName("request")] WireSessionSelectModelRequest Request);

public sealed record WireSessionSelectModelValue(
    [property: JsonPropertyName("selected")] WireModelSelection Selected);

public sealed record WireModelReasoningEffort(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description = null);

public sealed record WireModelReasoning(
    [property: JsonPropertyName("efforts")] IReadOnlyList<WireModelReasoningEffort> Efforts,
    [property: JsonPropertyName("defaultEffort")] string? DefaultEffort = null);

public sealed record WireModelCatalogModel(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("reasoning")] WireModelReasoning? Reasoning = null);

public sealed record WireModelProviderGroup(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("models")] IReadOnlyList<WireModelCatalogModel> Models);

public sealed record WireModelCatalogFailure(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("message")] string Message);

public sealed record WireModelCatalog(
    [property: JsonPropertyName("default")] WireModelSelection Default,
    [property: JsonPropertyName("routableProviders")] IReadOnlyList<string> RoutableProviders,
    [property: JsonPropertyName("groups")] IReadOnlyList<WireModelProviderGroup> Groups,
    [property: JsonPropertyName("failures")] IReadOnlyList<WireModelCatalogFailure> Failures);

public sealed record WireCredentialsDescribeArgs(
    [property: JsonPropertyName("refs")] IReadOnlyList<string> Refs);

public sealed record WireCredentialInfo(
    [property: JsonPropertyName("configured")] bool Configured,
    [property: JsonPropertyName("source")] string? Source = null,
    [property: JsonPropertyName("writable")] bool Writable = false,
    [property: JsonPropertyName("masked")] string? Masked = null);

public sealed record WireCredentialsSetArgs(
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("value")] string Value);

public sealed record WireCredentialsUnsetArgs(
    [property: JsonPropertyName("ref")] string Ref);

public sealed record WirePluginLocalizedMeta(
    [property: JsonPropertyName("name")] JsonElement? Name = null,
    [property: JsonPropertyName("description")] JsonElement? Description = null);

public sealed record WirePluginInventoryEntry(
    [property: JsonPropertyName("entryId")] string EntryId,
    [property: JsonPropertyName("moduleName")] string ModuleName,
    [property: JsonPropertyName("meta")] WirePluginLocalizedMeta? Meta = null,
    [property: JsonPropertyName("enabled")] bool Enabled = false,
    [property: JsonPropertyName("fiberPhase")] string? FiberPhase = null,
    [property: JsonPropertyName("diagnostic")] string? Diagnostic = null);

public sealed record WirePluginInventorySnapshot(
    [property: JsonPropertyName("managementAvailable")] bool? ManagementAvailable,
    [property: JsonPropertyName("entries")] IReadOnlyList<WirePluginInventoryEntry> Entries);

public sealed record WireAskUserQuestionOption(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("description")] string? Description = null);

public sealed record WireAskUserQuestionItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("detail")] string? Detail = null,
    [property: JsonPropertyName("header")] string? Header = null,
    [property: JsonPropertyName("options")] IReadOnlyList<WireAskUserQuestionOption>? Options = null,
    [property: JsonPropertyName("multiSelect")] bool? MultiSelect = null);

public sealed record WireUserQuestionRequestPayload(
    [property: JsonPropertyName("callId")] string? CallId = null,
    [property: JsonPropertyName("questions")] IReadOnlyList<WireAskUserQuestionItem>? Questions = null);

public sealed record WireAskUserQuestionAnswerItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("selected")] IReadOnlyList<string> Selected,
    [property: JsonPropertyName("custom")] string? Custom = null);

public sealed record WireAskUserQuestionAnswer(
    [property: JsonPropertyName("answers")] IReadOnlyList<WireAskUserQuestionAnswerItem> Answers);

public sealed record WireUserQuestionsAnswerArgs(
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("callId")] string CallId,
    [property: JsonPropertyName("answer")] WireAskUserQuestionAnswer Answer);

public sealed record WireApprovalRequestPayload(
    [property: JsonPropertyName("toolName")] string? ToolName = null,
    [property: JsonPropertyName("callId")] string? CallId = null,
    [property: JsonPropertyName("reason")] string? Reason = null);

public sealed record WireLlmFailureDto(
    [property: JsonPropertyName("code")] string? Code = null,
    [property: JsonPropertyName("message")] string? Message = null);

public sealed record WireTurnEndReasonDto(
    [property: JsonPropertyName("kind")] string? Kind = null,
    [property: JsonPropertyName("error")] WireLlmFailureDto? Error = null);

public sealed record WireTurnEndDataDto(
    [property: JsonPropertyName("turn")] int? Turn = null,
    [property: JsonPropertyName("reason")] WireTurnEndReasonDto? Reason = null);
