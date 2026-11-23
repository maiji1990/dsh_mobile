using DshMobile.Agent.Domain;

namespace DshMobile.Agent.Engines;

/// <summary>Strong scope identity preventing cross-runtime/profile/session event bleed (N-03-01).</summary>
public readonly record struct SessionKey(
    string RuntimeId,
    string ProfileId,
    string SessionId);

public readonly record struct MessageKey(
    SessionKey Session,
    string MessageId);

public enum ToolPhase
{
    Pending,
    Running,
    Completed,
    Error,
    Cancelled,
}

public enum RunPhase
{
    Idle,
    Starting,
    Streaming,
    WaitingApproval,
    WaitingQuestion,
    Completed,
    Failed,
    Cancelled,
}

public static class ToolPhaseTransitions
{
    public static bool IsTerminal(this ToolPhase phase) =>
        phase is ToolPhase.Completed or ToolPhase.Error or ToolPhase.Cancelled;

    /// <summary>Enforces strict monotonic transition; terminal states cannot regress.</summary>
    public static ToolPhase Advance(ToolPhase current, ToolPhase target)
    {
        if (current.IsTerminal())
        {
            return current;
        }
        return (int)target >= (int)current ? target : current;
    }
}

public abstract record MessagePart;

public sealed record TextMessagePart(string Text, bool IsSettled) : MessagePart;

public sealed record ReasoningMessagePart(string Text, bool IsSettled) : MessagePart;

public sealed record ToolCallMessagePart(
    string CallId,
    string ToolName,
    string ArgumentsJson,
    ToolPhase Phase,
    string? OutputSummary = null,
    bool IsError = false) : MessagePart;

public sealed record ImageMessagePart(PromptAttachmentImage Image) : MessagePart;

public sealed record UnknownMessagePart(string RawType, string DiagnosticKey) : MessagePart;

public abstract record ChatRow(string ItemId, long TurnSeq);

public sealed record UserMessageRow(
    string ItemId,
    long TurnSeq,
    string MessageId,
    string Text,
    int ImageCount,
    bool IsPending) : ChatRow(ItemId, TurnSeq);

public sealed record MarkdownBlockRow(
    string ItemId,
    long TurnSeq,
    string MessageId,
    int BlockIndex,
    int ChunkIndex,
    string Markdown,
    bool IsSettled,
    int ListDepth,
    bool ContinuedFromPrevious,
    bool ContinuesToNext) : ChatRow(ItemId, TurnSeq);

public sealed record ReasoningBlockRow(
    string ItemId,
    long TurnSeq,
    string MessageId,
    int BlockIndex,
    string Text,
    bool IsSettled) : ChatRow(ItemId, TurnSeq);

public sealed record ToolCallRow(
    string ItemId,
    long TurnSeq,
    string CallId,
    string ToolName,
    string ArgumentsJson,
    ToolPhase Phase,
    string? OutputSummary,
    bool IsError) : ChatRow(ItemId, TurnSeq);

public sealed record CommandRow(
    string ItemId,
    long TurnSeq,
    string CommandId,
    string Name,
    string? Args,
    bool? Succeeded,
    string? ResultText) : ChatRow(ItemId, TurnSeq);

public sealed record NoticeRow(
    string ItemId,
    long TurnSeq,
    string Code,
    string DiagnosticKey,
    bool IsError) : ChatRow(ItemId, TurnSeq);

public sealed record UnknownEventRow(
    string ItemId,
    long TurnSeq,
    string EventType) : ChatRow(ItemId, TurnSeq);

/// <summary>Deterministic performance & invariant counters locked by `Stream6000` (N-03-08).</summary>
public sealed record TranscriptCounters(
    long Publishes,
    long Derives,
    long TailReparses,
    long TailChars,
    long RowWrites,
    long HistoryVisits,
    long SkippedWhileHidden,
    long DeferredOverflowCount,
    long DroppedOutOfScopeCount,
    long PrefixTurnReuseHits)
{
    public static readonly TranscriptCounters Zero = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

public sealed record ChatSnapshot(
    long Revision,
    SessionKey Session,
    long CursorSeq,
    RunPhase Phase,
    bool IsDetachedCache,
    bool IsPartialHistory,
    string? FailureCode,
    IReadOnlyList<ChatRow> Rows,
    TranscriptCounters Counters,
    string? SessionTitle = null,
    long? EarliestSeq = null);
