using System.Collections.Frozen;
using System.Text.Json;

namespace DshMobile.Agent.Domain;

public enum PromptDeliveryMode
{
    Queue,
    Steer,
}

public enum TurnEndReasonKind
{
    Completed,
    Aborted,
    Blocked,
    Error,
    MaxTokens,
    Interrupted,
    Forked,
    Unknown,
}

public sealed record TurnEndReasonInfo(
    TurnEndReasonKind Kind,
    string RawKind,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public bool IsFailure => Kind is TurnEndReasonKind.Error or TurnEndReasonKind.Aborted or TurnEndReasonKind.Blocked or TurnEndReasonKind.Interrupted;
}

public enum ApprovalDecision
{
    AllowedOnce,
    Rejected,
    Cancelled,
}

public sealed record ImageUploadLimitsSpec(
    int MaxImageBytes,
    int MaxImagesPerMessage,
    int MaxMessageImageBytes,
    IReadOnlySet<string> AllowedMediaTypes)
{
    public static readonly FrozenSet<string> DefaultMediaTypes = FrozenSet.ToFrozenSet(
        ["image/png", "image/jpeg", "image/webp", "image/gif"],
        StringComparer.Ordinal);

    public static readonly ImageUploadLimitsSpec Default = new(
        MaxImageBytes: 5 * 1024 * 1024,
        MaxImagesPerMessage: 8,
        MaxMessageImageBytes: 20 * 1024 * 1024,
        AllowedMediaTypes: DefaultMediaTypes);
}

public sealed record PromptAttachmentImage(
    string MediaType,
    string Base64Data,
    string? Name = null);

public sealed record PromptSendRequest(
    string RequestId,
    string SessionId,
    string Text,
    PromptDeliveryMode Mode = PromptDeliveryMode.Queue,
    IReadOnlyList<PromptAttachmentImage>? Images = null,
    string? ClientTimeZone = null,
    bool AllowEmptyContinuation = false);

public sealed record CreateOrResumeSessionRequest(
    string? SessionId = null,
    string? WorkspaceId = null,
    string? Cwd = null,
    string? AgentPreset = null);

public sealed record SessionSummaryView(
    string SessionId,
    long UpdatedAtUnixMs,
    bool Running,
    bool Blank,
    bool AgentAvailable,
    string? ParentSessionId = null,
    string? Origin = null,
    string? Cwd = null,
    string? FormatStatus = null,
    string? Title = null,
    string? AgentPreset = null);

public sealed record SessionWireEventView(
    string Type,
    long Seq,
    long TimeUnixMs,
    JsonElement Data,
    bool Ignorable = false,
    bool IsKnownType = true);

public sealed record SessionPageView(
    IReadOnlyList<SessionWireEventView> Events,
    bool HasMore,
    long Cursor);

public sealed record SessionStateView(
    long Cursor,
    bool Active,
    bool Running,
    int QueuedCount,
    IReadOnlyList<string> PendingInput);

public enum SessionWaitOutcomeKind
{
    Idle,
    InputRequired,
    Timeout,
    AmbiguousEmptyResponse,
}

public sealed record SessionWaitResultView(
    SessionWaitOutcomeKind Kind,
    SessionStateView? State = null,
    IReadOnlyList<SessionWireEventView>? OutputEvents = null,
    bool OutputTruncated = false);

public sealed record SlashCommandDescriptorView(
    string Name,
    string Description,
    string? InputHint = null,
    bool AcceptsAttachments = false,
    string? DefinitionId = null);

public sealed record SlashCommandExecutionView(
    string CommandId,
    bool Succeeded,
    string? Text = null,
    long? SourceEventSeq = null);

public sealed record ModelSelectionSpec(
    string Provider,
    string Model,
    string? ReasoningEffort = null);

public sealed record ModelEffortView(
    string Id,
    string Name,
    string? Description = null);

public sealed record ModelInfoView(
    string Id,
    string Name,
    string? Description,
    IReadOnlyList<ModelEffortView> ReasoningEfforts,
    string? DefaultReasoningEffort = null);

public sealed record ModelProviderGroupView(
    string Id,
    string Name,
    IReadOnlyList<ModelInfoView> Models);

public sealed record ModelProviderFailureView(
    string Id,
    string Name,
    string Message);

public sealed record ModelCatalogView(
    ModelSelectionSpec Default,
    IReadOnlyList<string> RoutableProviders,
    IReadOnlyList<ModelProviderGroupView> Groups,
    IReadOnlyList<ModelProviderFailureView> Failures);

public sealed record CredentialStatusView(
    string Ref,
    bool Configured,
    string? Source,
    bool Writable,
    string? Masked = null);

public sealed record PluginInventoryEntryView(
    string EntryId,
    string ModuleName,
    bool Enabled,
    string? FiberPhase,
    string? DisplayName = null,
    string? Diagnostic = null);

public sealed record PluginInventoryView(
    bool ManagementAvailable,
    IReadOnlyList<PluginInventoryEntryView> Entries);

public sealed record ApprovalRequestView(
    string EventId,
    string AgentId,
    string ToolName,
    string? CallId = null,
    string? Reason = null);

public sealed record UserQuestionOptionView(
    string Label,
    string? Description = null);

public sealed record UserQuestionItemView(
    string Id,
    string Question,
    string? Detail,
    string? Header,
    IReadOnlyList<UserQuestionOptionView> Options,
    bool MultiSelect);

public sealed record UserQuestionRequestView(
    string EventId,
    string AgentId,
    string? CallId,
    IReadOnlyList<UserQuestionItemView> Questions);

public sealed record UserQuestionAnswerItemView(
    string Id,
    IReadOnlyList<string> Selected,
    string? Custom = null);

public sealed record UserQuestionAnswerBatch(
    IReadOnlyList<UserQuestionAnswerItemView> Answers);

public abstract record SessionFollowUpdate;

public sealed record SessionFollowSnapshotUpdate(
    string SessionId,
    long Cursor,
    bool HasMore,
    IReadOnlyList<SessionWireEventView> Events) : SessionFollowUpdate;

public sealed record SessionFollowEventUpdate(
    SessionWireEventView Event) : SessionFollowUpdate;

public sealed record SessionFollowAssistantStreamUpdate(
    string FrameType,
    string AttemptId,
    long Revision,
    JsonElement RawFrame) : SessionFollowUpdate;

public sealed record SessionFollowUnknownUpdate(
    string RawType) : SessionFollowUpdate;

public abstract record ForwardedGatewayEvent;

public sealed record ApprovalGatewayEvent(ApprovalRequestView Request) : ForwardedGatewayEvent;

public sealed record UserQuestionGatewayEvent(UserQuestionRequestView Request) : ForwardedGatewayEvent;

public sealed record ForwardedEventCancelledGatewayEvent(string EventId) : ForwardedGatewayEvent;

public sealed record SessionStatusGatewayEvent(string SessionId, bool Running) : ForwardedGatewayEvent;

public sealed record SessionActivityGatewayEvent(string SessionId, long UpdatedAtUnixMs) : ForwardedGatewayEvent;

public sealed record SessionErrorGatewayEvent(string SessionId, string Message) : ForwardedGatewayEvent;

public sealed record SessionRemovedGatewayEvent(string SessionId) : ForwardedGatewayEvent;

public sealed record UnknownGatewayEvent(string EventName, string RawMode) : ForwardedGatewayEvent;

/// <summary>Normalizes local or caller-supplied time zone IDs into valid IANA identifiers for `session/prompt`.</summary>
public static class IanaTimeZoneResolver
{
    public static string Resolve(string? requestedOrNull = null, TimeZoneInfo? localTimeZone = null)
    {
        if (!string.IsNullOrWhiteSpace(requestedOrNull))
        {
            if (TryNormalizeToIana(requestedOrNull.Trim(), out var explicitIana))
            {
                return explicitIana;
            }
            throw new ArgumentException($"Invalid time zone '{requestedOrNull}'.", nameof(requestedOrNull));
        }

        var tz = localTimeZone ?? TimeZoneInfo.Local;
        if (TryNormalizeToIana(tz.Id, out var localIana))
        {
            return localIana;
        }
        return "UTC";
    }

    public static bool TryNormalizeToIana(string candidate, out string ianaId)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            ianaId = string.Empty;
            return false;
        }
        if (candidate.Equals("UTC", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("Etc/UTC", StringComparison.OrdinalIgnoreCase))
        {
            ianaId = "UTC";
            return true;
        }
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(candidate, out var converted))
        {
            ianaId = converted;
            return true;
        }
        if (TimeZoneInfo.TryFindSystemTimeZoneById(candidate, out var info) &&
            (info.HasIanaId || candidate.Contains('/', StringComparison.Ordinal)))
        {
            ianaId = candidate;
            return true;
        }
        ianaId = string.Empty;
        return false;
    }
}

/// <summary>Validates prompt image attachments strictly against MIME allowlist and host byte limits.</summary>
public static class ImageAttachmentValidator
{
    public static DshFailure? Validate(
        IReadOnlyList<PromptAttachmentImage>? images,
        ImageUploadLimitsSpec? limits = null)
    {
        if (images is null || images.Count == 0)
        {
            return null;
        }

        var spec = limits ?? ImageUploadLimitsSpec.Default;
        if (images.Count > spec.MaxImagesPerMessage)
        {
            return new DshFailure(DshFailureCode.InvalidState, "attachment.too_many_images");
        }

        long totalBytes = 0;
        for (var i = 0; i < images.Count; i++)
        {
            var img = images[i];
            if (!spec.AllowedMediaTypes.Contains(img.MediaType))
            {
                return new DshFailure(DshFailureCode.InvalidState, "attachment.unsupported_media_type");
            }
            if (string.IsNullOrWhiteSpace(img.Base64Data))
            {
                return new DshFailure(DshFailureCode.InvalidState, "attachment.empty_data");
            }

            byte[] decoded;
            try
            {
                decoded = Convert.FromBase64String(img.Base64Data);
            }
            catch (FormatException)
            {
                return new DshFailure(DshFailureCode.InvalidState, "attachment.invalid_base64");
            }

            if (decoded.Length == 0)
            {
                return new DshFailure(DshFailureCode.InvalidState, "attachment.empty_data");
            }
            if (decoded.Length > spec.MaxImageBytes)
            {
                return new DshFailure(DshFailureCode.InvalidState, "attachment.image_too_large");
            }
            totalBytes += decoded.Length;
            if (totalBytes > spec.MaxMessageImageBytes)
            {
                return new DshFailure(DshFailureCode.InvalidState, "attachment.total_images_too_large");
            }
        }

        return null;
    }
}
