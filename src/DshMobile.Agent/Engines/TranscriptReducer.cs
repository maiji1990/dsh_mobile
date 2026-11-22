using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Protocol;

namespace DshMobile.Agent.Engines;

/// <summary>Sanitized tool invocation metadata resolved from `tool/call` events for `RequestsEngine` (N-03-07).</summary>
public sealed record ToolInvocationSummary(
    string CallId,
    string ToolName,
    string SanitizedArguments);

/// <summary>
/// Single-threaded deterministic transcript state machine &amp; Turn-prefix caching reducer (N-03-01 .. N-03-03).
/// </summary>
public sealed class TranscriptReducer
{
    public const int DefaultMaxDeferredParts = 64;
    public const int MaxRetainedNotices = 64;
    public const int MaxRetainedSubagents = 256;
    public const int MaxRetainedToolLedger = 512;

    private readonly SessionKey _scope;
    private readonly int _maxDeferredParts;
    private readonly List<IReadOnlyList<ChatRow>> _settledTurnRows = [];
    private readonly Dictionary<long, int> _settledTurnIndexBySeq = [];
    private readonly Dictionary<long, TurnAccumulator> _settledTurnsBySeq = [];
    private readonly Dictionary<string, long> _callIdToTurnSeq = new(StringComparer.Ordinal);
    private readonly HashSet<string> _settledAttemptIds = new(StringComparer.Ordinal);
    private readonly List<DeferredStreamChunk> _deferredParts = [];
    private readonly Dictionary<string, ToolInvocationSummary> _toolCallsById = new(StringComparer.Ordinal);
    private readonly Queue<string> _toolCallFifo = [];
    private readonly HashSet<string> _validatedSubagentIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _subagentFifo = [];
    private readonly List<NoticeRow> _sessionNotices = [];

    private ImmutableDictionary<string, ToolInvocationSummary> _toolCallsSnapshot =
        ImmutableDictionary<string, ToolInvocationSummary>.Empty.WithComparers(StringComparer.Ordinal);

    private TurnAccumulator? _activeTurn;
    private long _revision;
    private long _cursorSeq = -1;
    private long _nextSyntheticTurnSeq = 1;
    private long _subagentNoticeSeq;
    private RunPhase _phase = RunPhase.Idle;
    private string? _failureCode;
    private bool _isDetachedCache;
    private bool _isPartialHistory;
    private bool _needsCalibration;
    private bool _dirty;

    private long _derives;
    private long _rowWrites;
    private long _deferredOverflowCount;
    private long _droppedOutOfScopeCount;
    private long _prefixTurnReuseHits;
    private long _completedTurnTailChars;
    private long _completedTurnTailReparses;
    private IReadOnlyList<ChatRow> _lastDerivedRows = [];

    public TranscriptReducer(SessionKey scope, int maxDeferredParts = DefaultMaxDeferredParts)
    {
        if (string.IsNullOrWhiteSpace(scope.SessionId))
        {
            throw new ArgumentException("SessionKey.SessionId must not be empty.", nameof(scope));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDeferredParts);
        _scope = scope;
        _maxDeferredParts = maxDeferredParts;
    }

    public SessionKey Scope => _scope;
    public long CursorSeq => _cursorSeq;
    public bool NeedsCalibration => _needsCalibration;
    public int DeferredCount => _deferredParts.Count;
    public bool HasLiveEvents => !_isDetachedCache && (_cursorSeq >= 0 || _settledTurnRows.Count > 0 || _activeTurn is not null);

    /// <summary>Thread-safe lock-free read against the latest immutable tool ledger snapshot.</summary>
    public bool TryGetToolInvocation(string callId, out ToolInvocationSummary summary)
    {
        if (string.IsNullOrEmpty(callId))
        {
            summary = default!;
            return false;
        }
        return Volatile.Read(ref _toolCallsSnapshot).TryGetValue(callId, out summary!);
    }

    public void RecordOutOfScopeDrop()
    {
        _droppedOutOfScopeCount++;
        _dirty = true;
    }

    /// <summary>Validates subagent lineage; rejects foreign session updates before mutating state.</summary>
    public bool RegisterValidatedSubagent(string childSessionId, string? parentSessionId)
    {
        if (string.IsNullOrWhiteSpace(childSessionId) ||
            !string.Equals(parentSessionId, _scope.SessionId, StringComparison.Ordinal))
        {
            _droppedOutOfScopeCount++;
            return false;
        }

        if (_validatedSubagentIds.Add(childSessionId))
        {
            _subagentFifo.Enqueue(childSessionId);
            while (_validatedSubagentIds.Count > MaxRetainedSubagents && _subagentFifo.TryDequeue(out var oldest))
            {
                _validatedSubagentIds.Remove(oldest);
            }
        }
        return true;
    }

    public bool ApplySubagentActivity(SessionKey eventScope, string childSessionId, string? parentSessionId, string statusLine)
    {
        if (eventScope != _scope ||
            (!_validatedSubagentIds.Contains(childSessionId) && !RegisterValidatedSubagent(childSessionId, parentSessionId)))
        {
            _droppedOutOfScopeCount++;
            return false;
        }

        var seq = Interlocked.Increment(ref _subagentNoticeSeq);
        AppendSessionNotice(new NoticeRow(
            $"sub:{childSessionId}:{seq}",
            _activeTurn?.TurnSeq ?? Math.Max(1, _nextSyntheticTurnSeq - 1),
            "subagent.activity",
            SecretRedactor.RedactAndTruncate(statusLine, 240),
            IsError: false));
        _dirty = true;
        return true;
    }

    public bool ApplyWireEvent(SessionKey eventScope, SessionWireEventView wireEvent)
    {
        ArgumentNullException.ThrowIfNull(wireEvent);
        if (eventScope != _scope)
        {
            _droppedOutOfScopeCount++;
            return false;
        }

        // Detached cache must be cleared BEFORE seq comparison or EnsureActiveTurn so live replay never appends to cache rows.
        if (_isDetachedCache)
        {
            ClearDetachedCacheInternal();
        }

        // Durable seq idempotency
        if (wireEvent.Seq <= _cursorSeq)
        {
            return false;
        }

        _cursorSeq = wireEvent.Seq;
        _dirty = true;

        switch (wireEvent.Type)
        {
            case "turn/start":
                SealActiveTurnIfPresent(markCompleted: true);
                var rawTurnNum = TryGetInt64(wireEvent.Data, "turn");
                long assignedTurnSeq;
                if (rawTurnNum is { } explicitNum && !_settledTurnIndexBySeq.ContainsKey(explicitNum))
                {
                    assignedTurnSeq = explicitNum;
                    if (explicitNum >= _nextSyntheticTurnSeq)
                    {
                        _nextSyntheticTurnSeq = explicitNum + 1;
                    }
                }
                else
                {
                    assignedTurnSeq = AllocateUniqueTurnSeq();
                }

                _activeTurn = new TurnAccumulator(assignedTurnSeq, wireEvent.Seq);
                _phase = RunPhase.Streaming;
                _failureCode = null;
                break;

            case "turn/end":
                var reason = RemoteProtocolCodec.ParseTurnEndReason(wireEvent.Data);
                var endTurn = EnsureActiveTurn(wireEvent.Seq);
                if (endTurn.ActiveAttempt is { } activeAtt)
                {
                    _settledAttemptIds.Add(activeAtt.AttemptId);
                    endTurn.ActiveAttempt = null;
                }
                if (reason.IsFailure)
                {
                    _phase = reason.Kind == TurnEndReasonKind.Aborted ? RunPhase.Cancelled : RunPhase.Failed;
                    _failureCode = reason.ErrorCode ?? reason.RawKind;
                    endTurn.Notices.Add(new NoticeRow(
                        $"notice:turnend:{wireEvent.Seq}",
                        endTurn.TurnSeq,
                        _failureCode,
                        $"turn.failed.{reason.RawKind}",
                        IsError: true));
                }
                else
                {
                    _phase = RunPhase.Completed;
                    _failureCode = null;
                }
                SealActiveTurnIfPresent(markCompleted: true);
                break;

            case "user/message":
                var userTurn = EnsureActiveTurn(wireEvent.Seq);
                var uMsgId = ExtractMessageId(wireEvent.Data) ?? $"user:{wireEvent.Seq}";
                var uText = ExtractTextFromMessageData(wireEvent.Data);
                var imgCount = CountImagesInMessageData(wireEvent.Data);
                userTurn.UserMessages.RemoveAll(u => u.MessageId == uMsgId);
                userTurn.UserMessages.Add(new UserMessageRow(
                    $"msg:{uMsgId}",
                    userTurn.TurnSeq,
                    uMsgId,
                    uText,
                    imgCount,
                    IsPending: false));
                break;

            case "assistant/message":
                var aTurn = EnsureActiveTurn(wireEvent.Seq);
                var aMsgId = ExtractMessageId(wireEvent.Data) ?? $"asst:{wireEvent.Seq}";
                aTurn.AssistantMessageId = aMsgId;
                var finalReasoning = ExtractBlocksText(wireEvent.Data, "reasoning");
                if (!string.IsNullOrEmpty(finalReasoning))
                {
                    aTurn.Reasoning.Clear();
                    aTurn.Reasoning.Append(finalReasoning);
                }
                var finalText = ExtractBlocksText(wireEvent.Data, "text");
                if (!string.IsNullOrEmpty(finalText))
                {
                    aTurn.AssistantMarkdown.Reset(finalText);
                    aTurn.AssistantMarkdown.FlushFinal();
                }
                ExtractDurableToolCalls(aTurn, wireEvent.Data);
                if (aTurn.ActiveAttempt is { } att)
                {
                    _settledAttemptIds.Add(att.AttemptId);
                    aTurn.ActiveAttempt = null;
                }
                break;

            case "tool/call":
                var callId = TryGetString(wireEvent.Data, "callId") ?? $"call:{wireEvent.Seq}";
                var toolName = TryGetString(wireEvent.Data, "name") ?? "unknown";
                var rawArgs = TryGetString(wireEvent.Data, "arguments") ?? "{}";
                var sanitized = SecretRedactor.RedactAndTruncate(rawArgs);
                RecordToolInvocation(callId, toolName, sanitized);

                if (_activeTurn is null &&
                    _callIdToTurnSeq.TryGetValue(callId, out var settledSeqForCall) &&
                    _settledTurnsBySeq.TryGetValue(settledSeqForCall, out var settledCallTurn))
                {
                    settledCallTurn.UpsertToolCall(callId, toolName, sanitized, ToolPhase.Running);
                    RefreshSettledTurnRows(settledSeqForCall, settledCallTurn);
                }
                else
                {
                    var tcTurn = EnsureActiveTurn(wireEvent.Seq);
                    _callIdToTurnSeq[callId] = tcTurn.TurnSeq;
                    tcTurn.UpsertToolCall(callId, toolName, sanitized, ToolPhase.Running);
                }
                break;

            case "tool/result":
                var resultCallId = TryGetString(wireEvent.Data, "callId");
                if (resultCallId is null &&
                    wireEvent.Data.TryGetProperty("message", out var nestedMsg) &&
                    nestedMsg.ValueKind == JsonValueKind.Object)
                {
                    resultCallId = TryGetString(nestedMsg, "toolCallId");
                }
                if (_activeTurn is null &&
                    !string.IsNullOrEmpty(resultCallId) &&
                    _callIdToTurnSeq.TryGetValue(resultCallId, out var ownerTurnSeq) &&
                    _settledTurnsBySeq.TryGetValue(ownerTurnSeq, out var settledTurn))
                {
                    // Backfill directly into settled Turn without creating a synthetic Turn or overwriting Turn 1!
                    ApplyToolResult(settledTurn, wireEvent.Data, wireEvent.Seq);
                    RefreshSettledTurnRows(ownerTurnSeq, settledTurn);
                }
                else if (_activeTurn is not null)
                {
                    ApplyToolResult(_activeTurn, wireEvent.Data, wireEvent.Seq);
                }
                else
                {
                    // Standalone late tool/result for unknown call: record safely as non-colliding notice, never overwrite Turns or phase.
                    AppendSessionNotice(new NoticeRow(
                        $"tool-late:{wireEvent.Seq}",
                        Math.Max(1, _nextSyntheticTurnSeq - 1),
                        "tool.late_result",
                        resultCallId ?? $"seq:{wireEvent.Seq}",
                        IsError: false));
                }
                break;

            case "command/run":
                var crTurn = EnsureActiveTurn(wireEvent.Seq);
                var cmdId = TryGetString(wireEvent.Data, "commandId") ?? $"cmd:{wireEvent.Seq}";
                var cmdName = TryGetString(wireEvent.Data, "name") ?? "command";
                var cmdArgs = TryGetString(wireEvent.Data, "args");
                crTurn.UpsertCommand(cmdId, cmdName, cmdArgs is null ? null : SecretRedactor.RedactAndTruncate(cmdArgs, 200), null, null);
                break;

            case "command/done":
                var cdTurn = EnsureActiveTurn(wireEvent.Seq);
                var doneId = TryGetString(wireEvent.Data, "commandId") ?? $"cmd:{wireEvent.Seq}";
                var succeeded = TryGetString(wireEvent.Data, "kind") == "success";
                var text = TryGetString(wireEvent.Data, "text");
                cdTurn.CompleteCommand(doneId, succeeded, text is null ? null : SecretRedactor.RedactAndTruncate(text, 400));
                break;

            case "permission/preset":
            case "sandbox/mode":
            case "approval/policy":
            case "approval/asked":
            case "approval/decided":
            case "step/start":
            case "step/end":
            case "request/header":
            case "developer/message":
            case "system/message":
            case "assistant/attempt":
                break;

            default:
                if (!wireEvent.Ignorable)
                {
                    if (_activeTurn is not null)
                    {
                        _activeTurn.UnknownEvents.Add(new UnknownEventRow(
                            $"unk:{wireEvent.Seq}",
                            _activeTurn.TurnSeq,
                            wireEvent.Type));
                    }
                    else
                    {
                        AppendSessionNotice(new NoticeRow(
                            $"unk:{wireEvent.Seq}",
                            Math.Max(1, _nextSyntheticTurnSeq - 1),
                            "event.unknown",
                            wireEvent.Type,
                            IsError: false));
                    }
                }
                break;
        }

        return true;
    }

    public bool ApplyAssistantStreamFrame(SessionKey eventScope, SessionFollowAssistantStreamUpdate frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (eventScope != _scope)
        {
            _droppedOutOfScopeCount++;
            return false;
        }

        if (_settledAttemptIds.Contains(frame.AttemptId))
        {
            // Late stream frames for already settled/ended attempts must NEVER regress _phase back to Streaming.
            return false;
        }

        if (_isDetachedCache && frame.FrameType == "start")
        {
            ClearDetachedCacheInternal();
        }

        switch (frame.FrameType)
        {
            case "start":
                var startTurn = EnsureActiveTurn(_cursorSeq + 1);
                if (startTurn.ActiveAttempt is { } existing &&
                    existing.AttemptId == frame.AttemptId &&
                    existing.Revision >= frame.Revision)
                {
                    return false;
                }
                startTurn.ActiveAttempt = new AttemptStreamState(frame.AttemptId, frame.Revision);
                startTurn.AssistantMessageId ??= $"live:{frame.AttemptId}";
                _phase = RunPhase.Streaming;
                _dirty = true;
                DrainDeferredChunksForAttempt(startTurn, frame.AttemptId, frame.Revision);
                return true;

            case "chunk":
                var index = (int)(TryGetInt64(frame.RawFrame, "index") ?? -1);
                if (_activeTurn is null ||
                    _activeTurn.ActiveAttempt is null ||
                    _activeTurn.ActiveAttempt.AttemptId != frame.AttemptId ||
                    frame.Revision > _activeTurn.ActiveAttempt.Revision)
                {
                    // Buffer boundedly without creating synthetic turns or pulling _phase to Streaming after turn/end.
                    BufferDeferredChunk(new DeferredStreamChunk(frame.AttemptId, frame.Revision, index, frame.RawFrame.Clone()));
                    return false;
                }

                return ApplyChunkToAttempt(_activeTurn, _activeTurn.ActiveAttempt, index, frame.RawFrame);

            case "end":
                if (_activeTurn?.ActiveAttempt is { } active && active.AttemptId == frame.AttemptId)
                {
                    _activeTurn.AssistantMarkdown.FlushFinal();
                    _settledAttemptIds.Add(active.AttemptId);
                    _activeTurn.ActiveAttempt = null;
                    _dirty = true;
                    return true;
                }
                _settledAttemptIds.Add(frame.AttemptId);
                return false;

            default:
                return false;
        }
    }

    /// <summary>Hydrates offline detached cache without marking live continuity (`N-03-05`).</summary>
    public void LoadDetachedCache(long cursorSeq, RunPhase phase, IReadOnlyList<ChatRow> cachedRows)
    {
        ArgumentNullException.ThrowIfNull(cachedRows);
        ResetInternal();
        _isDetachedCache = true;
        _cursorSeq = cursorSeq;
        _phase = phase;
        _lastDerivedRows = cachedRows.ToArray();
        _settledTurnRows.Add(_lastDerivedRows);

        foreach (var row in cachedRows)
        {
            if (row is ToolCallRow tc)
            {
                RecordToolInvocation(tc.CallId, tc.ToolName, SecretRedactor.RedactAndTruncate(tc.ArgumentsJson));
            }
        }
        _dirty = true;
    }

    public void ApplyReconciledSnapshot(
        long cursorSeq,
        IReadOnlyList<SessionWireEventView> events,
        bool isPartialHistory = false,
        string? failureCode = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ResetInternal();
        _isPartialHistory = isPartialHistory;
        _failureCode = failureCode;
        foreach (var ev in events.OrderBy(e => e.Seq))
        {
            ApplyWireEvent(_scope, ev);
        }
        if (cursorSeq > _cursorSeq)
        {
            _cursorSeq = cursorSeq;
        }
        _needsCalibration = false;
        _dirty = true;
    }

    public void MarkPartialHistory(string failureCode)
    {
        _isPartialHistory = true;
        _failureCode = failureCode;
        _dirty = true;
    }

    public TranscriptCounters SnapshotCounters(long publishes, long skippedWhileHidden, long historyVisits) =>
        new(
            Publishes: publishes,
            Derives: _derives,
            TailReparses: _completedTurnTailReparses + (_activeTurn?.AssistantMarkdown.TailReevalCount ?? 0),
            TailChars: _completedTurnTailChars + (_activeTurn?.AssistantMarkdown.TotalTailCharsReparsed ?? 0),
            RowWrites: _rowWrites,
            HistoryVisits: historyVisits,
            SkippedWhileHidden: skippedWhileHidden,
            DeferredOverflowCount: _deferredOverflowCount,
            DroppedOutOfScopeCount: _droppedOutOfScopeCount,
            PrefixTurnReuseHits: _prefixTurnReuseHits);

    /// <summary>
    /// Derives the immutable <see cref="ChatSnapshot"/> using Turn-level cached prefixes and only recomputing the
    /// active tail turn (16-line <see cref="StreamingBlocks"/> cap guarantees O(1) tail derivation).
    /// </summary>
    public ChatSnapshot DeriveSnapshot(long publishes, long skippedWhileHidden, long historyVisits)
    {
        if (!_dirty && _revision > 0)
        {
            return new ChatSnapshot(
                _revision,
                _scope,
                _cursorSeq,
                _phase,
                _isDetachedCache,
                _isPartialHistory,
                _failureCode,
                _lastDerivedRows,
                SnapshotCounters(publishes, skippedWhileHidden, historyVisits));
        }

        _derives++;
        _revision++;
        _dirty = false;

        var rows = new List<ChatRow>(_settledTurnRows.Count * 2 + 8);
        if (_settledTurnRows.Count > 0)
        {
            _prefixTurnReuseHits += _settledTurnRows.Count;
            foreach (var settledRows in _settledTurnRows)
            {
                rows.AddRange(settledRows);
            }
        }

        if (_activeTurn is not null)
        {
            var tailRows = BuildTurnRows(_activeTurn, isSettled: false);
            _rowWrites += tailRows.Length;
            rows.AddRange(tailRows);
        }

        if (_sessionNotices.Count > 0)
        {
            rows.AddRange(_sessionNotices);
        }

        _lastDerivedRows = rows.ToArray();
        return new ChatSnapshot(
            _revision,
            _scope,
            _cursorSeq,
            _phase,
            _isDetachedCache,
            _isPartialHistory,
            _failureCode,
            _lastDerivedRows,
            SnapshotCounters(publishes, skippedWhileHidden, historyVisits));
    }

    private void SealActiveTurnIfPresent(bool markCompleted)
    {
        if (_activeTurn is null)
        {
            return;
        }
        if (markCompleted)
        {
            _activeTurn.AssistantMarkdown.FlushFinal();
        }
        _completedTurnTailChars += _activeTurn.AssistantMarkdown.TotalTailCharsReparsed;
        _completedTurnTailReparses += _activeTurn.AssistantMarkdown.TailReevalCount;

        var finalizedRows = BuildTurnRows(_activeTurn, isSettled: true);
        _rowWrites += finalizedRows.Length;

        var seq = _activeTurn.TurnSeq;
        if (_settledTurnIndexBySeq.ContainsKey(seq))
        {
            // Never overwrite an already-settled Turn; allocate a unique slot if a collision ever occurred.
            seq = AllocateUniqueTurnSeq();
        }

        _settledTurnIndexBySeq[seq] = _settledTurnRows.Count;
        _settledTurnRows.Add(finalizedRows);
        _settledTurnsBySeq[seq] = _activeTurn;
        foreach (var call in _activeTurn.Tools)
        {
            _callIdToTurnSeq[call.CallId] = seq;
        }
        _activeTurn = null;
    }

    private void RefreshSettledTurnRows(long settledTurnSeq, TurnAccumulator settledTurn)
    {
        if (!_settledTurnIndexBySeq.TryGetValue(settledTurnSeq, out var idx))
        {
            return;
        }
        var rebuilt = BuildTurnRows(settledTurn, isSettled: true);
        _rowWrites += rebuilt.Length;
        _settledTurnRows[idx] = rebuilt;
        _dirty = true;
    }

    private long AllocateUniqueTurnSeq()
    {
        while (_settledTurnIndexBySeq.ContainsKey(_nextSyntheticTurnSeq) ||
               (_activeTurn is not null && _activeTurn.TurnSeq == _nextSyntheticTurnSeq))
        {
            _nextSyntheticTurnSeq++;
        }
        return _nextSyntheticTurnSeq++;
    }

    private TurnAccumulator EnsureActiveTurn(long fallbackSeq)
    {
        if (_activeTurn is not null)
        {
            return _activeTurn;
        }
        if (_isDetachedCache)
        {
            ClearDetachedCacheInternal();
        }
        _activeTurn = new TurnAccumulator(AllocateUniqueTurnSeq(), fallbackSeq);
        return _activeTurn;
    }

    private void ClearDetachedCacheInternal()
    {
        _settledTurnRows.Clear();
        _settledTurnIndexBySeq.Clear();
        _settledTurnsBySeq.Clear();
        _callIdToTurnSeq.Clear();
        _toolCallsById.Clear();
        _toolCallFifo.Clear();
        Volatile.Write(ref _toolCallsSnapshot, ImmutableDictionary<string, ToolInvocationSummary>.Empty.WithComparers(StringComparer.Ordinal));
        _lastDerivedRows = [];
        _cursorSeq = -1;
        _nextSyntheticTurnSeq = 1;
        _isDetachedCache = false;
        _dirty = true;
    }

    private void ResetInternal()
    {
        _settledTurnRows.Clear();
        _settledTurnIndexBySeq.Clear();
        _settledTurnsBySeq.Clear();
        _callIdToTurnSeq.Clear();
        _settledAttemptIds.Clear();
        _deferredParts.Clear();
        _sessionNotices.Clear();
        _toolCallsById.Clear();
        _toolCallFifo.Clear();
        Volatile.Write(ref _toolCallsSnapshot, ImmutableDictionary<string, ToolInvocationSummary>.Empty.WithComparers(StringComparer.Ordinal));
        _validatedSubagentIds.Clear();
        _subagentFifo.Clear();

        _activeTurn = null;
        _nextSyntheticTurnSeq = 1;
        _cursorSeq = -1;
        _completedTurnTailChars = 0;
        _completedTurnTailReparses = 0;
        _phase = RunPhase.Idle;
        _failureCode = null;
        _isDetachedCache = false;
        _isPartialHistory = false;
        _needsCalibration = false;
        _lastDerivedRows = [];
        _dirty = true;
    }

    private void RecordToolInvocation(string callId, string toolName, string sanitizedArgs)
    {
        var summary = new ToolInvocationSummary(callId, toolName, sanitizedArgs);
        if (!_toolCallsById.ContainsKey(callId))
        {
            _toolCallFifo.Enqueue(callId);
        }
        _toolCallsById[callId] = summary;

        var snapshot = _toolCallsSnapshot.SetItem(callId, summary);
        while (_toolCallsById.Count > MaxRetainedToolLedger && _toolCallFifo.TryDequeue(out var oldest))
        {
            _toolCallsById.Remove(oldest);
            snapshot = snapshot.Remove(oldest);
        }
        Volatile.Write(ref _toolCallsSnapshot, snapshot);
    }

    private void AppendSessionNotice(NoticeRow notice)
    {
        _sessionNotices.Add(notice);
        _rowWrites++;
        if (_sessionNotices.Count > MaxRetainedNotices)
        {
            _sessionNotices.RemoveAt(0);
        }
    }

    private bool ApplyChunkToAttempt(TurnAccumulator turn, AttemptStreamState attempt, int index, JsonElement chunkWrapper)
    {
        if (index >= 0 && !attempt.SeenChunkIndices.Add(index))
        {
            return false;
        }

        if (!chunkWrapper.TryGetProperty("chunk", out var chunk) || chunk.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var chunkType = TryGetString(chunk, "type");
        switch (chunkType)
        {
            case "text-delta":
                var textDelta = TryGetString(chunk, "text");
                if (!string.IsNullOrEmpty(textDelta))
                {
                    turn.AssistantMarkdown.Append(textDelta);
                    _dirty = true;
                    return true;
                }
                return false;

            case "reasoning-delta":
                var rDelta = TryGetString(chunk, "text");
                if (!string.IsNullOrEmpty(rDelta))
                {
                    turn.Reasoning.Append(rDelta);
                    _dirty = true;
                    return true;
                }
                return false;

            case "tool-call-delta":
                var id = TryGetString(chunk, "id") ?? string.Empty;
                var name = TryGetString(chunk, "name");
                var argDelta = TryGetString(chunk, "argumentsDelta") ?? string.Empty;
                if (!string.IsNullOrEmpty(id))
                {
                    turn.AppendToolCallDelta(id, name, argDelta);
                    var combined = turn.GetToolCallArguments(id);
                    RecordToolInvocation(id, name ?? turn.GetToolName(id), SecretRedactor.RedactAndTruncate(combined));
                    _callIdToTurnSeq[id] = turn.TurnSeq;
                    _dirty = true;
                    return true;
                }
                return false;

            default:
                return false;
        }
    }

    private void BufferDeferredChunk(DeferredStreamChunk item)
    {
        _deferredParts.Add(item);
        if (_deferredParts.Count > _maxDeferredParts)
        {
            _deferredParts.Clear();
            _deferredOverflowCount++;
            _needsCalibration = true;
        }
    }

    private void DrainDeferredChunksForAttempt(TurnAccumulator turn, string attemptId, long revision)
    {
        if (_deferredParts.Count == 0 || turn.ActiveAttempt is null)
        {
            return;
        }

        var matching = _deferredParts
            .Where(d => d.AttemptId == attemptId && d.Revision == revision)
            .OrderBy(d => d.Index)
            .ToList();

        _deferredParts.RemoveAll(d => d.AttemptId == attemptId && d.Revision <= revision);
        foreach (var d in matching)
        {
            ApplyChunkToAttempt(turn, turn.ActiveAttempt, d.Index, d.RawFrame);
        }
    }

    private static ChatRow[] BuildTurnRows(TurnAccumulator turn, bool isSettled)
    {
        var list = new List<ChatRow>(8);
        list.AddRange(turn.UserMessages);

        if (turn.Reasoning.Length > 0)
        {
            list.Add(new ReasoningBlockRow(
                $"reason:{turn.TurnSeq}",
                turn.TurnSeq,
                turn.AssistantMessageId ?? $"turn:{turn.TurnSeq}",
                0,
                turn.Reasoning.ToString(),
                isSettled));
        }

        var mdBlocks = turn.AssistantMarkdown.SnapshotAllBlocks();
        var msgId = turn.AssistantMessageId ?? $"turn:{turn.TurnSeq}";
        for (var i = 0; i < mdBlocks.Count; i++)
        {
            var b = mdBlocks[i];
            list.Add(new MarkdownBlockRow(
                $"md:{turn.TurnSeq}:{msgId}:{b.BlockIndex}:{b.ChunkIndex}",
                turn.TurnSeq,
                msgId,
                b.BlockIndex,
                b.ChunkIndex,
                b.Markdown,
                b.IsSettled,
                b.ListDepth,
                b.ContinuedFromPrevious,
                b.ContinuesToNext));
        }

        for (var i = 0; i < turn.Tools.Count; i++)
        {
            var t = turn.Tools[i];
            list.Add(new ToolCallRow(
                $"tool:{t.CallId}",
                turn.TurnSeq,
                t.CallId,
                t.ToolName,
                t.SanitizedArguments,
                t.Phase,
                t.OutputSummary,
                t.IsError));
        }

        for (var i = 0; i < turn.Commands.Count; i++)
        {
            var c = turn.Commands[i];
            list.Add(new CommandRow(
                $"cmd:{c.CommandId}",
                turn.TurnSeq,
                c.CommandId,
                c.Name,
                c.Args,
                c.Succeeded,
                c.ResultText));
        }

        list.AddRange(turn.Notices);
        list.AddRange(turn.UnknownEvents);
        return list.ToArray();
    }

    private static void ApplyToolResult(TurnAccumulator turn, JsonElement data, long seq)
    {
        string? callId = TryGetString(data, "callId");
        var isError = false;
        string? summary = null;
        if (data.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.Object)
        {
            callId ??= TryGetString(msgEl, "toolCallId");
            if (msgEl.TryGetProperty("isError", out var errProp) && errProp.ValueKind == JsonValueKind.True)
            {
                isError = true;
            }
            summary = SecretRedactor.RedactAndTruncate(ExtractTextFromMessageData(msgEl));
        }
        if (data.TryGetProperty("error", out var errObj) && errObj.ValueKind == JsonValueKind.Object)
        {
            isError = true;
        }
        callId ??= $"call:{seq}";
        turn.CompleteToolCall(callId, isError ? ToolPhase.Error : ToolPhase.Completed, summary, isError);
    }

    private void ExtractDurableToolCalls(TurnAccumulator turn, JsonElement data)
    {
        if (!data.TryGetProperty("message", out var msgEl) ||
            !msgEl.TryGetProperty("content", out var arr) ||
            arr.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (var item in arr.EnumerateArray())
        {
            if (TryGetString(item, "type") != "tool-call")
            {
                continue;
            }
            var callId = TryGetString(item, "id");
            var name = TryGetString(item, "name") ?? "unknown";
            var rawArgs = TryGetString(item, "arguments") ?? "{}";
            if (!string.IsNullOrEmpty(callId))
            {
                var sanitized = SecretRedactor.RedactAndTruncate(rawArgs);
                RecordToolInvocation(callId, name, sanitized);
                _callIdToTurnSeq[callId] = turn.TurnSeq;
                turn.UpsertToolCall(callId, name, sanitized, ToolPhase.Running);
            }
        }
    }

    private static string? ExtractMessageId(JsonElement data)
    {
        if (TryGetString(data, "id") is { } direct)
        {
            return direct;
        }
        if (data.TryGetProperty("message", out var msg) && TryGetString(msg, "id") is { } nested)
        {
            return nested;
        }
        return null;
    }

    private static string ExtractTextFromMessageData(JsonElement data)
    {
        var target = data.TryGetProperty("message", out var nested) ? nested : data;
        return ExtractBlocksText(target, "text");
    }

    private static string ExtractBlocksText(JsonElement messageOrData, string blockType)
    {
        var container = messageOrData.TryGetProperty("message", out var msg) ? msg : messageOrData;
        if (!container.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }
        var sb = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object &&
                TryGetString(part, "type") == blockType &&
                TryGetString(part, "text") is { } t)
            {
                if (sb.Length > 0 && blockType == "text")
                {
                    sb.Append('\n');
                }
                sb.Append(t);
            }
        }
        return sb.ToString();
    }

    private static int CountImagesInMessageData(JsonElement data)
    {
        var container = data.TryGetProperty("message", out var msg) ? msg : data;
        if (!container.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }
        var count = 0;
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object && TryGetString(part, "type") == "image")
            {
                count++;
            }
        }
        return count;
    }

    public static string SanitizeSummary(string raw, int maxChars = 200) =>
        SecretRedactor.RedactAndTruncate(raw, maxChars);

    private static string? TryGetString(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static long? TryGetInt64(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
            ? n
            : null;

    private sealed record DeferredStreamChunk(string AttemptId, long Revision, int Index, JsonElement RawFrame);

    private sealed class AttemptStreamState(string attemptId, long revision)
    {
        public string AttemptId { get; } = attemptId;
        public long Revision { get; } = revision;
        public HashSet<int> SeenChunkIndices { get; } = [];
    }

    private sealed class ToolState(string callId, string toolName, string args)
    {
        public string CallId { get; } = callId;
        public string ToolName { get; set; } = toolName;
        public string SanitizedArguments { get; set; } = args;
        public StringBuilder RawDeltaBuilder { get; } = new();
        public ToolPhase Phase { get; set; } = ToolPhase.Pending;
        public string? OutputSummary { get; set; }
        public bool IsError { get; set; }
    }

    private sealed class CommandState(string commandId, string name, string? args)
    {
        public string CommandId { get; } = commandId;
        public string Name { get; } = name;
        public string? Args { get; } = args;
        public bool? Succeeded { get; set; }
        public string? ResultText { get; set; }
    }

    private sealed class TurnAccumulator(long turnSeq, long firstSeq)
    {
        public long TurnSeq { get; } = turnSeq;
        public long FirstSeq { get; } = firstSeq;
        public string? AssistantMessageId { get; set; }
        public AttemptStreamState? ActiveAttempt { get; set; }
        public List<UserMessageRow> UserMessages { get; } = [];
        public StringBuilder Reasoning { get; } = new();
        public StreamingBlocks AssistantMarkdown { get; } = new();
        public List<ToolState> Tools { get; } = [];
        public List<CommandState> Commands { get; } = [];
        public List<NoticeRow> Notices { get; } = [];
        public List<UnknownEventRow> UnknownEvents { get; } = [];
        private readonly Dictionary<string, ToolState> _toolMap = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CommandState> _cmdMap = new(StringComparer.Ordinal);

        public void UpsertToolCall(string callId, string toolName, string sanitizedArgs, ToolPhase targetPhase)
        {
            if (!_toolMap.TryGetValue(callId, out var state))
            {
                state = new ToolState(callId, toolName, sanitizedArgs);
                _toolMap[callId] = state;
                Tools.Add(state);
            }
            else if (!state.Phase.IsTerminal())
            {
                state.ToolName = toolName;
                state.SanitizedArguments = sanitizedArgs;
            }
            state.Phase = ToolPhaseTransitions.Advance(state.Phase, targetPhase);
        }

        public void AppendToolCallDelta(string callId, string? name, string delta)
        {
            if (!_toolMap.TryGetValue(callId, out var state))
            {
                state = new ToolState(callId, name ?? "unknown", string.Empty);
                _toolMap[callId] = state;
                Tools.Add(state);
            }
            if (state.Phase.IsTerminal())
            {
                return;
            }
            if (!string.IsNullOrEmpty(name))
            {
                state.ToolName = name;
            }
            state.RawDeltaBuilder.Append(delta);
            state.SanitizedArguments = SecretRedactor.RedactAndTruncate(state.RawDeltaBuilder.ToString());
            state.Phase = ToolPhaseTransitions.Advance(state.Phase, ToolPhase.Running);
        }

        public string GetToolCallArguments(string callId) =>
            _toolMap.TryGetValue(callId, out var st) ? st.RawDeltaBuilder.ToString() : string.Empty;

        public string GetToolName(string callId) =>
            _toolMap.TryGetValue(callId, out var st) ? st.ToolName : "unknown";

        public void CompleteToolCall(string callId, ToolPhase terminalPhase, string? summary, bool isError)
        {
            if (!_toolMap.TryGetValue(callId, out var state))
            {
                state = new ToolState(callId, "unknown", string.Empty);
                _toolMap[callId] = state;
                Tools.Add(state);
            }
            if (state.Phase.IsTerminal())
            {
                return;
            }
            state.Phase = ToolPhaseTransitions.Advance(state.Phase, terminalPhase);
            state.OutputSummary = summary;
            state.IsError = isError;
        }

        public void UpsertCommand(string id, string name, string? args, bool? succeeded, string? text)
        {
            if (!_cmdMap.TryGetValue(id, out var cmd))
            {
                cmd = new CommandState(id, name, args);
                _cmdMap[id] = cmd;
                Commands.Add(cmd);
            }
            if (succeeded.HasValue)
            {
                cmd.Succeeded = succeeded;
                cmd.ResultText = text;
            }
        }

        public void CompleteCommand(string id, bool succeeded, string? text)
        {
            if (!_cmdMap.TryGetValue(id, out var cmd))
            {
                cmd = new CommandState(id, "command", null);
                _cmdMap[id] = cmd;
                Commands.Add(cmd);
            }
            cmd.Succeeded = succeeded;
            cmd.ResultText = text;
        }
    }
}
