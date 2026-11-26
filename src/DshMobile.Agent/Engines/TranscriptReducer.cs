using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Protocol;

namespace DshMobile.Agent.Engines;

/// <summary>由 `tool/call` 事件解析并脱敏后的工具调用元数据，供 `RequestsEngine` 使用（N-03-07）。</summary>
public sealed record ToolInvocationSummary(
    string CallId,
    string ToolName,
    string SanitizedArguments);

/// <summary>
/// 单线程确定性 Transcript 状态机与 Turn 前缀缓存归约器（N-03-01 .. N-03-03）。
/// </summary>
public sealed class TranscriptReducer
{
    public const int DefaultMaxDeferredParts = 64;
    public const int MaxRetainedNotices = 64;
    public const int MaxRetainedSubagents = 256;
    public const int MaxRetainedToolLedger = 512;

    private readonly SessionKey _scope;
    private readonly int _maxDeferredParts;
    private readonly Action<ToolInvocationSummary>? _onToolInvocation;
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
    private long? _earliestSeq;
    private long _nextSyntheticTurnSeq = 1;
    private long _subagentNoticeSeq;
    private RunPhase _phase = RunPhase.Idle;
    private string? _failureCode;
    private string? _sessionTitle;
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
    private long _arrivalOrder;
    private IReadOnlyDictionary<long, long>? _detachedTurnDurationsMs;
    private IReadOnlyList<ChatRow> _lastDerivedRows = [];

    public TranscriptReducer(
        SessionKey scope,
        int maxDeferredParts = DefaultMaxDeferredParts,
        Action<ToolInvocationSummary>? onToolInvocation = null)
    {
        if (string.IsNullOrWhiteSpace(scope.SessionId))
        {
            throw new ArgumentException("SessionKey.SessionId must not be empty.", nameof(scope));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDeferredParts);
        _scope = scope;
        _maxDeferredParts = maxDeferredParts;
        _onToolInvocation = onToolInvocation;
    }

    public SessionKey Scope => _scope;
    public long CursorSeq => _cursorSeq;
    public bool NeedsCalibration => _needsCalibration;
    public int DeferredCount => _deferredParts.Count;
    public bool HasLiveEvents => !_isDetachedCache && (_cursorSeq >= 0 || _settledTurnRows.Count > 0 || _activeTurn is not null);

    /// <summary>对最新不可变工具台账快照做线程安全的无锁读取。</summary>
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

    /// <summary>单调递增的到达序号，用于活动 Turn 内跨类型节点的稳定保序（不持久化）。</summary>
    private long NextArrivalOrder() => ++_arrivalOrder;

    /// <summary>校验子代理归属；在改变状态前拒绝外部会话的更新。</summary>
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

        // 必须在 seq 比较或 EnsureActiveTurn 之前清空离线缓存，避免实时回放追加到缓存行。
        if (_isDetachedCache)
        {
            ClearDetachedCacheInternal();
        }

        // 持久化 seq 幂等
        if (wireEvent.Seq <= _cursorSeq)
        {
            return false;
        }

        _cursorSeq = wireEvent.Seq;
        if (_earliestSeq is null || wireEvent.Seq < _earliestSeq.Value)
        {
            _earliestSeq = wireEvent.Seq;
        }
        _dirty = true;

        var arrivalOrder = NextArrivalOrder();
        var eventTimeUnixMs = wireEvent.TimeUnixMs > 0 ? wireEvent.TimeUnixMs : (long?)null;

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

                _activeTurn = new TurnAccumulator(assignedTurnSeq, wireEvent.Seq, eventTimeUnixMs);
                _phase = RunPhase.Streaming;
                _failureCode = null;
                break;

            case "turn/end":
                var reason = RemoteProtocolCodec.ParseTurnEndReason(wireEvent.Data);
                var endTurn = EnsureActiveTurn(wireEvent.Seq, eventTimeUnixMs);
                endTurn.NoteWireEvent(arrivalOrder, eventTimeUnixMs);
                endTurn.SettledAtUnixMs = eventTimeUnixMs;
                endTurn.CloseActiveReasoning(eventTimeUnixMs);
                if (endTurn.ActiveAttempt is { } activeAtt)
                {
                    _settledAttemptIds.Add(activeAtt.AttemptId);
                    endTurn.ActiveAttempt = null;
                }
                if (reason.IsFailure)
                {
                    _phase = reason.Kind == TurnEndReasonKind.Aborted ? RunPhase.Cancelled : RunPhase.Failed;
                    _failureCode = reason.ErrorCode ?? reason.RawKind;
                    endTurn.AddNotice(new NoticeRow(
                        $"notice:turnend:{wireEvent.Seq}",
                        endTurn.TurnSeq,
                        _failureCode,
                        $"turn.failed.{reason.RawKind}",
                        IsError: true), arrivalOrder);
                }
                else
                {
                    _phase = RunPhase.Completed;
                    _failureCode = null;
                }
                SealActiveTurnIfPresent(markCompleted: true);
                break;

            case "user/message":
                // 仅将真实用户输入渲染为右侧气泡；技能/记忆/审批注入的合成上下文消息跳过渲染（F12）。
                var uSourceKind = ExtractUserMessageSourceKind(wireEvent.Data);
                if (uSourceKind is not null && !string.Equals(uSourceKind, "user", StringComparison.Ordinal))
                {
                    break;
                }
                var uMsgId = ExtractMessageId(wireEvent.Data) ?? $"user:{wireEvent.Seq}";
                var uText = ExtractTextFromMessageData(wireEvent.Data);
                var imgCount = CountImagesInMessageData(wireEvent.Data);
                if (uSourceKind is null && imgCount == 0 && IsInjectedReminderText(uText))
                {
                    break;
                }
                var userTurn = EnsureActiveTurn(wireEvent.Seq, eventTimeUnixMs);
                userTurn.NoteWireEvent(arrivalOrder, eventTimeUnixMs);
                userTurn.AddUserMessage(new UserMessageRow(
                    $"msg:{uMsgId}",
                    userTurn.TurnSeq,
                    uMsgId,
                    uText,
                    imgCount,
                    IsPending: false), arrivalOrder);
                break;

            case "assistant/message":
                var aTurn = EnsureActiveTurn(wireEvent.Seq, eventTimeUnixMs);
                var aMsgId = ExtractMessageId(wireEvent.Data) ?? $"asst:{wireEvent.Seq}";
                aTurn.AssistantMessageId = aMsgId;
                // 先用消息到达前的最后真实事件时间作为推理片段起点，再应用终帧并记录完成时间。
                ApplyAssistantMessage(aTurn, wireEvent.Data, arrivalOrder, eventTimeUnixMs);
                aTurn.NoteWireEvent(arrivalOrder, eventTimeUnixMs);
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
                    settledCallTurn.NoteWireEvent(arrivalOrder, eventTimeUnixMs);
                    settledCallTurn.UpsertToolCall(callId, toolName, sanitized, ToolPhase.Running, arrivalOrder, eventTimeUnixMs);
                    RefreshSettledTurnRows(settledSeqForCall, settledCallTurn);
                }
                else
                {
                    var tcTurn = EnsureActiveTurn(wireEvent.Seq, eventTimeUnixMs);
                    tcTurn.NoteWireEvent(arrivalOrder, eventTimeUnixMs);
                    _callIdToTurnSeq[callId] = tcTurn.TurnSeq;
                    tcTurn.UpsertToolCall(callId, toolName, sanitized, ToolPhase.Running, arrivalOrder, eventTimeUnixMs);
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
                    // 直接回填已结算 Turn，不创建合成 Turn，也不覆盖 Turn 1！
                    settledTurn.NoteWireEvent(arrivalOrder, eventTimeUnixMs);
                    ApplyToolResult(settledTurn, wireEvent.Data, wireEvent.Seq, arrivalOrder, eventTimeUnixMs);
                    RefreshSettledTurnRows(ownerTurnSeq, settledTurn);
                }
                else if (_activeTurn is not null)
                {
                    _activeTurn.NoteWireEvent(arrivalOrder, eventTimeUnixMs);
                    ApplyToolResult(_activeTurn, wireEvent.Data, wireEvent.Seq, arrivalOrder, eventTimeUnixMs);
                }
                else
                {
                    // 未知调用的独立迟到 tool/result：安全记录为不冲突的通知，绝不覆盖 Turn 或阶段。
                    AppendSessionNotice(new NoticeRow(
                        $"tool-late:{wireEvent.Seq}",
                        Math.Max(1, _nextSyntheticTurnSeq - 1),
                        "tool.late_result",
                        resultCallId ?? $"seq:{wireEvent.Seq}",
                        IsError: false));
                }
                break;

            case "command/run":
                var crTurn = EnsureActiveTurn(wireEvent.Seq, eventTimeUnixMs);
                crTurn.NoteWireEvent(arrivalOrder, eventTimeUnixMs);
                var cmdId = TryGetString(wireEvent.Data, "commandId") ?? $"cmd:{wireEvent.Seq}";
                var cmdName = TryGetString(wireEvent.Data, "name") ?? "command";
                var cmdArgs = TryGetString(wireEvent.Data, "args");
                crTurn.UpsertCommand(cmdId, cmdName, cmdArgs is null ? null : SecretRedactor.RedactAndTruncate(cmdArgs, 200), null, null, arrivalOrder, eventTimeUnixMs);
                break;

            case "command/done":
                var cdTurn = EnsureActiveTurn(wireEvent.Seq, eventTimeUnixMs);
                cdTurn.NoteWireEvent(arrivalOrder, eventTimeUnixMs);
                var doneId = TryGetString(wireEvent.Data, "commandId") ?? $"cmd:{wireEvent.Seq}";
                var succeeded = TryGetString(wireEvent.Data, "kind") == "success";
                var text = TryGetString(wireEvent.Data, "text");
                cdTurn.CompleteCommand(doneId, succeeded, text is null ? null : SecretRedactor.RedactAndTruncate(text, 400), arrivalOrder, eventTimeUnixMs);
                break;

            case "session/title":
                var nextTitle = TryGetString(wireEvent.Data, "title")?.Trim();
                if (!string.IsNullOrEmpty(nextTitle))
                {
                    _sessionTitle = nextTitle;
                }
                break;

            case "agent-preset/selected":
            case "agent/inbox/spliced":
            case "approval/policy":
            case "approval/asked":
            case "approval/decided":
            case "assistant/attempt":
            case "compaction/end":
            case "compaction/prune":
            case "compaction/start":
            case "compaction/summary":
            case "deliverables/presented":
            case "developer/message":
            case "feedback/message-delete":
            case "feedback/message-put":
            case "feedback/record":
            case "goal/change":
            case "hook/invoked":
            case "hook/result":
            case "image/offload":
            case "job/registered":
            case "job/settled":
            case "llm/retry":
            case "llm/retry-started":
            case "model/selection":
            case "permission/preset":
            case "plan/mode":
            case "request/context":
            case "request/header":
            case "sandbox/mode":
            case "schedule/change":
            case "session-log-deepseek/delivery-accepted":
            case "session/end-seed":
            case "session/title-llm-request":
            case "step/start":
            case "step/end":
            case "subagent/catalog":
            case "subagent/descriptor":
            case "subagent/model-selection-policy":
            case "system/message":
            case "team/member":
            case "team/message/delivered":
            case "team/message/queued":
            case "team/mode":
            case "team/task":
            case "todo/write":
            case "tool-workflow/agent-end":
            case "tool-workflow/agent-start":
            case "tool-workflow/run-end":
            case "tool-workflow/run-start":
            case "tool/ptc-dispatch":
            case "tool/ptc-dispatch-start":
            case "web/deepseek-search-llm-request":
            case "working-directory/change":
            case "workspace/changes":
                break;

            default:
                if (!wireEvent.Ignorable)
                {
                    if (_activeTurn is not null)
                    {
                        _activeTurn.NoteWireEvent(arrivalOrder, eventTimeUnixMs);
                        _activeTurn.AddUnknownEvent(new UnknownEventRow(
                            $"unk:{wireEvent.Seq}",
                            _activeTurn.TurnSeq,
                            wireEvent.Type), arrivalOrder);
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
            // 已结算/已结束尝试的迟到流帧绝不能把 _phase 回退为 Streaming。
            return false;
        }

        if (_isDetachedCache && frame.FrameType == "start")
        {
            ClearDetachedCacheInternal();
        }

        var arrivalOrder = NextArrivalOrder();
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
                    // 有界缓冲，不创建合成 Turn，也不在 turn/end 后把 _phase 拉回 Streaming。
                    BufferDeferredChunk(new DeferredStreamChunk(frame.AttemptId, frame.Revision, index, arrivalOrder, frame.RawFrame.Clone()));
                    return false;
                }

                return ApplyChunkToAttempt(_activeTurn, _activeTurn.ActiveAttempt, index, arrivalOrder, frame.RawFrame);

            case "end":
                if (_activeTurn?.ActiveAttempt is { } active && active.AttemptId == frame.AttemptId)
                {
                    _activeTurn.AssistantMarkdown.FlushFinal();
                    _activeTurn.CloseActiveReasoning(_activeTurn.LastEventTimeUnixMs);
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

    /// <summary>加载离线缓存且不标记实时连续性（`N-03-05`）。</summary>
    public void LoadDetachedCache(
        long cursorSeq,
        RunPhase phase,
        IReadOnlyList<ChatRow> cachedRows,
        IReadOnlyDictionary<long, long>? turnDurationsMs = null)
    {
        ArgumentNullException.ThrowIfNull(cachedRows);
        ResetInternal();
        _isDetachedCache = true;
        _cursorSeq = cursorSeq;
        _phase = phase;
        _detachedTurnDurationsMs = turnDurationsMs;
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
        _earliestSeq = null;
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

    /// <summary>将冷读分页加载的更早历史事件无重影前置到已结算轮次前缀中，保持活跃流与增量游标不变（N-05-03）。</summary>
    public bool PrependOlderEvents(IReadOnlyList<SessionWireEventView> olderEvents, bool stillHasMore)
    {
        ArgumentNullException.ThrowIfNull(olderEvents);
        var upperBound = _earliestSeq;
        var candidates = olderEvents
            .Where(e => e.Seq >= 0 && (upperBound is null || e.Seq < upperBound.Value))
            .GroupBy(e => e.Seq)
            .Select(g => g.First())
            .OrderBy(e => e.Seq)
            .ToList();

        _isPartialHistory = stillHasMore;
        _dirty = true;

        if (candidates.Count == 0)
        {
            return false;
        }

        var prefix = new TranscriptReducer(_scope, _maxDeferredParts, _onToolInvocation);
        foreach (var ev in candidates)
        {
            prefix.ApplyWireEvent(_scope, ev);
        }
        prefix.SealActiveTurnIfPresent(markCompleted: true);

        if (_sessionTitle is null && prefix._sessionTitle is not null)
        {
            _sessionTitle = prefix._sessionTitle;
        }

        if (prefix._settledTurnRows.Count > 0)
        {
            var insertCount = prefix._settledTurnRows.Count;
            foreach (var key in _settledTurnIndexBySeq.Keys.ToArray())
            {
                _settledTurnIndexBySeq[key] += insertCount;
            }
            _settledTurnRows.InsertRange(0, prefix._settledTurnRows);

            var nextNegativeSeq = -1L;
            for (var i = 0; i < insertCount; i++)
            {
                var turn = prefix._settledTurnsBySeq.Values.ElementAtOrDefault(i);
                var originalSeq = prefix._settledTurnIndexBySeq.FirstOrDefault(kv => kv.Value == i).Key;
                var targetSeq = !_settledTurnsBySeq.ContainsKey(originalSeq) && _activeTurn?.TurnSeq != originalSeq
                    ? originalSeq
                    : nextNegativeSeq--;
                _settledTurnIndexBySeq[targetSeq] = i;
                if (turn is not null)
                {
                    _settledTurnsBySeq[targetSeq] = turn;
                }
            }
        }

        foreach (var (callId, turnSeq) in prefix._callIdToTurnSeq)
        {
            _callIdToTurnSeq.TryAdd(callId, turnSeq);
        }
        foreach (var (callId, summary) in prefix._toolCallsById)
        {
            if (!_toolCallsById.ContainsKey(callId))
            {
                RecordToolInvocation(callId, summary.ToolName, summary.SanitizedArguments);
            }
        }
        if (prefix._sessionNotices.Count > 0)
        {
            _sessionNotices.InsertRange(0, prefix._sessionNotices);
        }

        _earliestSeq = candidates[0].Seq;
        return true;
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
    /// 使用 Turn 级缓存前缀派生不可变 <see cref="ChatSnapshot"/>，只重算活动尾 Turn
    /// （<see cref="StreamingBlocks"/> 的 16 行上限保证尾块派发为 O(1)）。
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
                SnapshotCounters(publishes, skippedWhileHidden, historyVisits),
                _sessionTitle,
                _earliestSeq,
                BuildTurnDurations());
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
            SnapshotCounters(publishes, skippedWhileHidden, historyVisits),
            _sessionTitle,
            _earliestSeq,
            BuildTurnDurations());
    }

    private IReadOnlyDictionary<long, long>? BuildTurnDurations()
    {
        if (_isDetachedCache)
        {
            return _detachedTurnDurationsMs;
        }
        Dictionary<long, long>? durations = null;
        foreach (var (turnSeq, turn) in _settledTurnsBySeq)
        {
            if (turn.StartedAtUnixMs is { } started &&
                turn.SettledAtUnixMs is { } completed &&
                completed >= started)
            {
                durations ??= new Dictionary<long, long>();
                durations[turnSeq] = completed - started;
            }
        }
        return durations;
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
        _activeTurn.CloseActiveReasoning(_activeTurn.SettledAtUnixMs);
        _completedTurnTailChars += _activeTurn.AssistantMarkdown.TotalTailCharsReparsed;
        _completedTurnTailReparses += _activeTurn.AssistantMarkdown.TailReevalCount;

        var finalizedRows = BuildTurnRows(_activeTurn, isSettled: true);
        _rowWrites += finalizedRows.Length;

        var seq = _activeTurn.TurnSeq;
        if (_settledTurnIndexBySeq.ContainsKey(seq))
        {
            // 绝不覆盖已结算 Turn；若发生冲突则分配唯一槽位。
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

    private TurnAccumulator EnsureActiveTurn(long fallbackSeq, long? startedAtUnixMs = null)
    {
        if (_activeTurn is not null)
        {
            return _activeTurn;
        }
        if (_isDetachedCache)
        {
            ClearDetachedCacheInternal();
        }
        // 无 turn/start 时也不伪造时间：只有调用方拿到的真实 wire 事件时间才作为起点。
        _activeTurn = new TurnAccumulator(AllocateUniqueTurnSeq(), fallbackSeq, startedAtUnixMs);
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
        _earliestSeq = null;
        _nextSyntheticTurnSeq = 1;
        _arrivalOrder = 0;
        _detachedTurnDurationsMs = null;
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
        _earliestSeq = null;
        _arrivalOrder = 0;
        _detachedTurnDurationsMs = null;
        _completedTurnTailChars = 0;
        _completedTurnTailReparses = 0;
        _phase = RunPhase.Idle;
        _failureCode = null;
        _sessionTitle = null;
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
        _onToolInvocation?.Invoke(summary);
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

    private bool ApplyChunkToAttempt(TurnAccumulator turn, AttemptStreamState attempt, int index, long arrivalOrder, JsonElement chunkWrapper)
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
                    turn.MarkStreamedMarkdown();
                    turn.AppendMarkdown(textDelta, arrivalOrder);
                    _dirty = true;
                    return true;
                }
                return false;

            case "reasoning-delta":
                var rDelta = TryGetString(chunk, "text");
                if (!string.IsNullOrEmpty(rDelta))
                {
                    turn.MarkStreamedReasoning();
                    turn.AppendReasoning(rDelta, arrivalOrder);
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
                    turn.AppendToolCallDelta(id, name, argDelta, arrivalOrder, turn.LastEventTimeUnixMs);
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
            ApplyChunkToAttempt(turn, turn.ActiveAttempt, d.Index, d.ArrivalOrder, d.RawFrame);
        }
    }

    private static ChatRow[] BuildTurnRows(TurnAccumulator turn, bool isSettled)
    {
        var markdownBlocks = new Dictionary<(int BlockIndex, int ChunkIndex), StreamingMarkdownBlock>();
        foreach (var block in turn.AssistantMarkdown.SnapshotAllBlocks())
        {
            markdownBlocks[(block.BlockIndex, block.ChunkIndex)] = block;
        }

        var messageId = turn.AssistantMessageId ?? $"turn:{turn.TurnSeq}";
        var rows = new List<ChatRow>(turn.Timeline.Count);
        foreach (var node in turn.Timeline)
        {
            switch (node.Kind)
            {
                case TurnNodeKind.User when node.User is { } user:
                    rows.Add(user);
                    break;

                case TurnNodeKind.Reasoning when node.Reasoning is { } reasoning:
                    rows.Add(new ReasoningBlockRow(
                        turn.ReasoningItemId(reasoning),
                        turn.TurnSeq,
                        messageId,
                        reasoning.SegmentIndex,
                        reasoning.Text.ToString(),
                        isSettled,
                        reasoning.StartedAtUnixMs,
                        reasoning.CompletedAtUnixMs));
                    break;

                case TurnNodeKind.Markdown when node.MarkdownKey is { } key &&
                    markdownBlocks.TryGetValue(key, out var block):
                    rows.Add(new MarkdownBlockRow(
                        $"md:{turn.TurnSeq}:{messageId}:{block.BlockIndex}:{block.ChunkIndex}",
                        turn.TurnSeq,
                        messageId,
                        block.BlockIndex,
                        block.ChunkIndex,
                        block.Markdown,
                        block.IsSettled,
                        block.ListDepth,
                        block.ContinuedFromPrevious,
                        block.ContinuesToNext));
                    break;

                case TurnNodeKind.Tool when node.Tool is { } tool:
                    rows.Add(new ToolCallRow(
                        $"tool:{tool.CallId}",
                        turn.TurnSeq,
                        tool.CallId,
                        tool.ToolName,
                        tool.SanitizedArguments,
                        tool.Phase,
                        tool.OutputSummary,
                        tool.IsError,
                        tool.StartedAtUnixMs,
                        tool.CompletedAtUnixMs));
                    break;

                case TurnNodeKind.Command when node.Command is { } command:
                    rows.Add(new CommandRow(
                        $"cmd:{command.CommandId}",
                        turn.TurnSeq,
                        command.CommandId,
                        command.Name,
                        command.Args,
                        command.Succeeded,
                        command.ResultText));
                    break;

                case TurnNodeKind.Notice when node.Notice is { } notice:
                    rows.Add(notice);
                    break;

                case TurnNodeKind.Unknown when node.Unknown is { } unknown:
                    rows.Add(unknown);
                    break;
            }
        }
        return rows.ToArray();
    }

    private static void ApplyToolResult(
        TurnAccumulator turn,
        JsonElement data,
        long seq,
        long arrivalOrder,
        long? completedAtUnixMs)
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
        turn.CompleteToolCall(
            callId,
            isError ? ToolPhase.Error : ToolPhase.Completed,
            summary,
            isError,
            arrivalOrder,
            completedAtUnixMs);
    }

    /// <summary>
    /// 应用服务端最终 assistant/message：已有流式内容时保留其真实到达序，只补全终帧新增片段；
    /// 同一 Turn 的多条 assistant/message 按到达顺序逐段追加，只有时间线尚无推理/正文/工具节点时才整体重建。
    /// </summary>
    private void ApplyAssistantMessage(
        TurnAccumulator turn,
        JsonElement data,
        long arrivalOrder,
        long? eventTimeUnixMs)
    {
        // 先取出并复位“当前段”的 live 流式标记，避免上一条消息的状态影响本条消息。
        var (hasStreamedReasoning, hasStreamedMarkdown) = turn.ConsumeStreamedFlags();
        var content = TryGetMessageContent(data);
        if (content is null)
        {
            // 兼容没有 content 数组的旧结构：只有在缺少流式内容时才用聚合文本补全。
            if (turn.Reasonings.Count == 0)
            {
                var finalReasoning = ExtractBlocksText(data, "reasoning");
                if (!string.IsNullOrEmpty(finalReasoning))
                {
                    turn.AppendReasoning(finalReasoning, arrivalOrder, eventTimeUnixMs, isFinal: true);
                }
            }
            if (turn.AssistantMarkdown.SettledCount == 0 && turn.AssistantMarkdown.TailKey is null)
            {
                var finalText = ExtractBlocksText(data, "text");
                if (!string.IsNullOrEmpty(finalText))
                {
                    turn.ResetMarkdown(finalText, arrivalOrder);
                }
            }
            turn.CompleteCurrentReasoning(eventTimeUnixMs);
            return;
        }

        var parts = content.Value.EnumerateArray()
            .Where(part => TryGetString(part, "type") is "reasoning" or "text" or "tool-call")
            .ToArray();
        if (parts.Length == 0)
        {
            return;
        }

        // 只有该 Turn 完全没有推理/正文/工具节点（冷读或回放的开始）时才整体重建；
        // 否则保留前一条 assistant/message 的片段，按到达顺序追加本段内容。
        var hasInterleavedNode = turn.Timeline.Any(node =>
            node.Kind is TurnNodeKind.Reasoning or TurnNodeKind.Markdown or TurnNodeKind.Tool);
        if (!hasInterleavedNode)
        {
            turn.BeginFinalAssistantRebuild();
        }

        var seenToolCallIds = new HashSet<string>(StringComparer.Ordinal);
        var appendedTextPart = false;
        foreach (var part in parts)
        {
            var partType = TryGetString(part, "type");
            switch (partType)
            {
                case "reasoning":
                    if (hasStreamedReasoning)
                    {
                        break;
                    }
                    var reasoningText = TryGetString(part, "text");
                    if (!string.IsNullOrEmpty(reasoningText))
                    {
                        turn.AppendReasoning(
                            reasoningText,
                            NextArrivalOrder(),
                            eventTimeUnixMs,
                            isFinal: true);
                    }
                    break;

                case "text":
                    if (hasStreamedMarkdown)
                    {
                        break;
                    }
                    var textPart = TryGetString(part, "text");
                    if (!string.IsNullOrEmpty(textPart))
                    {
                        if (appendedTextPart)
                        {
                            turn.AppendMarkdown("\n", NextArrivalOrder());
                        }
                        turn.AppendMarkdown(textPart, NextArrivalOrder());
                        appendedTextPart = true;
                    }
                    break;

                case "tool-call":
                    var callId = TryGetString(part, "id");
                    if (string.IsNullOrEmpty(callId) || !seenToolCallIds.Add(callId))
                    {
                        break;
                    }
                    var name = TryGetString(part, "name") ?? "unknown";
                    var rawArgs = TryGetString(part, "arguments") ?? "{}";
                    var sanitized = SecretRedactor.RedactAndTruncate(rawArgs);
                    RecordToolInvocation(callId, name, sanitized);
                    _callIdToTurnSeq[callId] = turn.TurnSeq;
                    turn.UpsertToolCall(
                        callId,
                        name,
                        sanitized,
                        ToolPhase.Running,
                        NextArrivalOrder(),
                        eventTimeUnixMs);
                    break;
            }
        }

        // 终帧未重述、但此前已通过 tool/call 或流式增量创建的工具状态不能丢失。
        foreach (var toolState in turn.Tools)
        {
            if (seenToolCallIds.Contains(toolState.CallId))
            {
                continue;
            }
            turn.EnsureToolTimelineNode(toolState, NextArrivalOrder());
        }

        turn.AssistantMarkdown.FlushFinal();
        turn.CompleteCurrentReasoning(eventTimeUnixMs);
    }

    private static JsonElement? TryGetMessageContent(JsonElement data)
    {
        var container = data.TryGetProperty("message", out var message) ? message : data;
        return container.ValueKind == JsonValueKind.Object &&
               container.TryGetProperty("content", out var content) &&
               content.ValueKind == JsonValueKind.Array
            ? content
            : null;
    }

    private static string? ExtractUserMessageSourceKind(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (data.TryGetProperty("source", out var directSrc) && directSrc.ValueKind == JsonValueKind.Object)
        {
            return TryGetString(directSrc, "kind");
        }
        if (data.TryGetProperty("message", out var nestedMsg) &&
            nestedMsg.ValueKind == JsonValueKind.Object &&
            nestedMsg.TryGetProperty("source", out var nestedSrc) &&
            nestedSrc.ValueKind == JsonValueKind.Object)
        {
            return TryGetString(nestedSrc, "kind");
        }
        return null;
    }

    private static bool IsInjectedReminderText(string text) =>
        !string.IsNullOrEmpty(text) &&
        text.TrimStart().StartsWith("<system-reminder>", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>提前到达、等待 start 帧的流式分片；保留收到时的到达序号以维持真实顺序。</summary>
    private sealed record DeferredStreamChunk(
        string AttemptId,
        long Revision,
        int Index,
        long ArrivalOrder,
        JsonElement RawFrame);

    private sealed class AttemptStreamState(string attemptId, long revision)
    {
        public string AttemptId { get; } = attemptId;
        public long Revision { get; } = revision;
        public HashSet<int> SeenChunkIndices { get; } = [];
    }

    private sealed class ToolState(string callId, string toolName, string args, long? startedAtUnixMs = null)
    {
        public string CallId { get; } = callId;
        public string ToolName { get; set; } = toolName;
        public string SanitizedArguments { get; set; } = args;
        public StringBuilder RawDeltaBuilder { get; } = new();
        public ToolPhase Phase { get; set; } = ToolPhase.Pending;
        public string? OutputSummary { get; set; }
        public bool IsError { get; set; }
        public long? StartedAtUnixMs { get; set; } = startedAtUnixMs;
        public long? CompletedAtUnixMs { get; set; }
    }

    private sealed class CommandState(string commandId, string name, string? args, long? startedAtUnixMs = null)
    {
        public string CommandId { get; } = commandId;
        public string Name { get; } = name;
        public string? Args { get; } = args;
        public bool? Succeeded { get; set; }
        public string? ResultText { get; set; }
        public long? StartedAtUnixMs { get; set; } = startedAtUnixMs;
        public long? CompletedAtUnixMs { get; set; }
    }

    private sealed class ReasoningState(int segmentIndex, long? startedAtUnixMs)
    {
        public int SegmentIndex { get; } = segmentIndex;
        public StringBuilder Text { get; } = new();
        public long? StartedAtUnixMs { get; set; } = startedAtUnixMs;
        public long? CompletedAtUnixMs { get; set; }
    }

    private enum TurnNodeKind
    {
        User,
        Reasoning,
        Markdown,
        Tool,
        Command,
        Notice,
        Unknown,
    }

    /// <summary>Turn 内部时间线节点，按到达序号稳定排序，承载跨类型的真实交错顺序。</summary>
    private sealed class TurnTimelineNode(TurnNodeKind kind, long arrivalOrder)
    {
        public TurnNodeKind Kind { get; } = kind;
        public long ArrivalOrder { get; } = arrivalOrder;
        public UserMessageRow? User { get; set; }
        public ReasoningState? Reasoning { get; set; }
        public (int BlockIndex, int ChunkIndex)? MarkdownKey { get; set; }
        public ToolState? Tool { get; set; }
        public CommandState? Command { get; set; }
        public NoticeRow? Notice { get; set; }
        public UnknownEventRow? Unknown { get; set; }
    }

    private sealed class TurnAccumulator(long turnSeq, long firstSeq, long? startedAtUnixMs)
    {
        public long TurnSeq { get; } = turnSeq;
        public long FirstSeq { get; } = firstSeq;
        public long? StartedAtUnixMs { get; } = startedAtUnixMs;
        public long? SettledAtUnixMs { get; set; }
        public long? LastEventTimeUnixMs { get; private set; } = startedAtUnixMs;
        public string? AssistantMessageId { get; set; }
        public AttemptStreamState? ActiveAttempt { get; set; }
        public List<UserMessageRow> UserMessages { get; } = [];
        public List<ReasoningState> Reasonings { get; } = [];
        public StreamingBlocks AssistantMarkdown { get; } = new();
        public List<ToolState> Tools { get; } = [];
        public List<CommandState> Commands { get; } = [];
        public List<NoticeRow> Notices { get; } = [];
        public List<UnknownEventRow> UnknownEvents { get; } = [];
        public List<TurnTimelineNode> Timeline { get; } = [];
        private readonly Dictionary<string, ToolState> _toolMap = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CommandState> _cmdMap = new(StringComparer.Ordinal);
        private readonly HashSet<(int BlockIndex, int ChunkIndex)> _markdownKeys = [];
        private ReasoningState? _activeReasoning;
        private int _nextReasoningSegmentIndex;
        private bool _hasStreamedReasoning;
        private bool _hasStreamedMarkdown;

        /// <summary>标记当前 assistant/message 段已收到 live 推理增量，终帧到达时据此去重。</summary>
        public void MarkStreamedReasoning() => _hasStreamedReasoning = true;

        /// <summary>标记当前 assistant/message 段已收到 live 正文增量，终帧到达时据此去重。</summary>
        public void MarkStreamedMarkdown() => _hasStreamedMarkdown = true;

        /// <summary>读取并复位当前段落的 live 流式标记；上一条消息的状态不得泄漏到下一条消息。</summary>
        public (bool Reasoning, bool Markdown) ConsumeStreamedFlags()
        {
            var flags = (_hasStreamedReasoning, _hasStreamedMarkdown);
            _hasStreamedReasoning = false;
            _hasStreamedMarkdown = false;
            return flags;
        }

        public void NoteWireEvent(long arrivalOrder, long? timeUnixMs)
        {
            _ = arrivalOrder;
            if (timeUnixMs is { } time)
            {
                LastEventTimeUnixMs = time;
            }
        }

        public void AddUserMessage(UserMessageRow row, long arrivalOrder)
        {
            CloseActiveReasoning(null);
            Timeline.RemoveAll(node => node.Kind == TurnNodeKind.User && node.User?.MessageId == row.MessageId);
            UserMessages.RemoveAll(message => message.MessageId == row.MessageId);
            UserMessages.Add(row);
            AddNode(new TurnTimelineNode(TurnNodeKind.User, arrivalOrder) { User = row });
        }

        public void AppendReasoning(
            string delta,
            long arrivalOrder,
            long? completedAtUnixMs = null,
            bool isFinal = false)
        {
            if (string.IsNullOrEmpty(delta))
            {
                return;
            }
            // 推理片段与正文尾块交错时先封口旧尾块，避免工具前的正文被合并进后续块。
            SealMarkdownTail();
            if (_activeReasoning is null)
            {
                var segment = new ReasoningState(_nextReasoningSegmentIndex++, LastEventTimeUnixMs);
                Reasonings.Add(segment);
                AddNode(new TurnTimelineNode(TurnNodeKind.Reasoning, arrivalOrder) { Reasoning = segment });
                _activeReasoning = segment;
            }
            _activeReasoning.Text.Append(delta);
            if (isFinal && completedAtUnixMs is { } completed)
            {
                _activeReasoning.CompletedAtUnixMs = completed;
            }
        }

        public void CloseActiveReasoning(long? completedAtUnixMs)
        {
            if (_activeReasoning is null)
            {
                return;
            }
            if (completedAtUnixMs is { } completed)
            {
                _activeReasoning.CompletedAtUnixMs = completed;
            }
            _activeReasoning = null;
        }

        /// <summary>用真实终帧时间补齐最近一段推理的完成时间；没有真实时间时保持 <c>null</c>。</summary>
        public void CompleteCurrentReasoning(long? completedAtUnixMs)
        {
            CloseActiveReasoning(completedAtUnixMs);
            if (completedAtUnixMs is { } completed &&
                Reasonings.Count > 0 &&
                Reasonings[^1].CompletedAtUnixMs is null)
            {
                // 正文先开始会关闭推理段；终帧到达时仍需补记该段的完成时间。
                Reasonings[^1].CompletedAtUnixMs = completed;
            }
        }

        public void AppendMarkdown(string delta, long arrivalOrder)
        {
            if (string.IsNullOrEmpty(delta))
            {
                return;
            }
            // 正文开始意味着当前推理片段结束；流式分片没有独立的真实时间，完成时间保持缺省。
            CloseActiveReasoning(null);
            var settledBefore = AssistantMarkdown.SettledCount;
            AssistantMarkdown.Append(delta);
            RegisterNewMarkdownNodes(arrivalOrder, settledBefore);
        }

        public void ResetMarkdown(string text, long arrivalOrder)
        {
            Timeline.RemoveAll(node => node.Kind == TurnNodeKind.Markdown);
            _markdownKeys.Clear();
            AssistantMarkdown.Reset(text);
            RegisterAllMarkdownNodes(arrivalOrder);
        }

        public void BeginFinalAssistantRebuild()
        {
            CloseActiveReasoning(null);
            Timeline.RemoveAll(node =>
                node.Kind is TurnNodeKind.Reasoning or TurnNodeKind.Markdown or TurnNodeKind.Tool);
            _markdownKeys.Clear();
            Reasonings.Clear();
            _nextReasoningSegmentIndex = 0;
            AssistantMarkdown.Reset(string.Empty);
        }

        public string ReasoningItemId(ReasoningState state) =>
            state.SegmentIndex == 0
                ? $"reason:{TurnSeq}"
                : $"reason:{TurnSeq}:{state.SegmentIndex}";

        public void AddNotice(NoticeRow notice, long arrivalOrder)
        {
            CloseActiveReasoning(null);
            Notices.Add(notice);
            AddNode(new TurnTimelineNode(TurnNodeKind.Notice, arrivalOrder) { Notice = notice });
        }

        public void AddUnknownEvent(UnknownEventRow unknown, long arrivalOrder)
        {
            CloseActiveReasoning(null);
            UnknownEvents.Add(unknown);
            AddNode(new TurnTimelineNode(TurnNodeKind.Unknown, arrivalOrder) { Unknown = unknown });
        }

        public void UpsertToolCall(
            string callId,
            string toolName,
            string sanitizedArgs,
            ToolPhase targetPhase,
            long arrivalOrder,
            long? startedAtUnixMs)
        {
            CloseActiveReasoning(null);
            SealMarkdownTail();
            if (!_toolMap.TryGetValue(callId, out var state))
            {
                state = new ToolState(callId, toolName, sanitizedArgs, startedAtUnixMs);
                _toolMap[callId] = state;
                Tools.Add(state);
            }
            else
            {
                if (!state.Phase.IsTerminal())
                {
                    state.ToolName = toolName;
                    state.SanitizedArguments = sanitizedArgs;
                }
                state.StartedAtUnixMs ??= startedAtUnixMs;
            }
            state.Phase = ToolPhaseTransitions.Advance(state.Phase, targetPhase);
            EnsureToolTimelineNode(state, arrivalOrder);
        }

        public void AppendToolCallDelta(
            string callId,
            string? name,
            string delta,
            long arrivalOrder,
            long? startedAtUnixMs)
        {
            CloseActiveReasoning(null);
            SealMarkdownTail();
            if (!_toolMap.TryGetValue(callId, out var state))
            {
                state = new ToolState(callId, name ?? "unknown", string.Empty, startedAtUnixMs);
                _toolMap[callId] = state;
                Tools.Add(state);
                AddNode(new TurnTimelineNode(TurnNodeKind.Tool, arrivalOrder) { Tool = state });
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

        public void CompleteToolCall(
            string callId,
            ToolPhase terminalPhase,
            string? summary,
            bool isError,
            long arrivalOrder,
            long? completedAtUnixMs)
        {
            CloseActiveReasoning(null);
            if (!_toolMap.TryGetValue(callId, out var state))
            {
                state = new ToolState(callId, "unknown", string.Empty);
                _toolMap[callId] = state;
                Tools.Add(state);
                AddNode(new TurnTimelineNode(TurnNodeKind.Tool, arrivalOrder) { Tool = state });
            }
            if (state.Phase.IsTerminal())
            {
                state.CompletedAtUnixMs ??= completedAtUnixMs;
                return;
            }
            state.Phase = ToolPhaseTransitions.Advance(state.Phase, terminalPhase);
            state.OutputSummary = summary;
            state.IsError = isError;
            state.CompletedAtUnixMs = completedAtUnixMs;
        }

        public void EnsureToolTimelineNode(ToolState state, long arrivalOrder)
        {
            CloseActiveReasoning(null);
            if (Timeline.Any(node => node.Kind == TurnNodeKind.Tool && ReferenceEquals(node.Tool, state)))
            {
                return;
            }
            AddNode(new TurnTimelineNode(TurnNodeKind.Tool, arrivalOrder) { Tool = state });
        }

        public string GetToolCallArguments(string callId) =>
            _toolMap.TryGetValue(callId, out var state) ? state.RawDeltaBuilder.ToString() : string.Empty;

        public string GetToolName(string callId) =>
            _toolMap.TryGetValue(callId, out var state) ? state.ToolName : "unknown";

        public void UpsertCommand(
            string id,
            string name,
            string? args,
            bool? succeeded,
            string? text,
            long arrivalOrder,
            long? startedAtUnixMs)
        {
            CloseActiveReasoning(null);
            if (!_cmdMap.TryGetValue(id, out var command))
            {
                command = new CommandState(id, name, args, startedAtUnixMs);
                _cmdMap[id] = command;
                Commands.Add(command);
                AddNode(new TurnTimelineNode(TurnNodeKind.Command, arrivalOrder) { Command = command });
            }
            else
            {
                command.StartedAtUnixMs ??= startedAtUnixMs;
            }
            if (succeeded.HasValue)
            {
                command.Succeeded = succeeded;
                command.ResultText = text;
            }
        }

        public void CompleteCommand(
            string id,
            bool succeeded,
            string? text,
            long arrivalOrder,
            long? completedAtUnixMs)
        {
            CloseActiveReasoning(null);
            if (!_cmdMap.TryGetValue(id, out var command))
            {
                command = new CommandState(id, "command", null);
                _cmdMap[id] = command;
                Commands.Add(command);
                AddNode(new TurnTimelineNode(TurnNodeKind.Command, arrivalOrder) { Command = command });
            }
            command.Succeeded = succeeded;
            command.ResultText = text;
            command.CompletedAtUnixMs = completedAtUnixMs;
        }

        /// <summary>若存在未封口正文尾块则先封口，保证工具/推理节点前后的正文各自成块。</summary>
        private void SealMarkdownTail()
        {
            if (AssistantMarkdown.TailKey is not null)
            {
                AssistantMarkdown.FlushFinal();
            }
        }

        private void RegisterNewMarkdownNodes(long arrivalOrder, int settledBefore)
        {
            var settled = AssistantMarkdown.SettledBlocks;
            for (var index = settledBefore; index < settled.Count; index++)
            {
                AddMarkdownNode(settled[index].BlockIndex, settled[index].ChunkIndex, arrivalOrder);
            }
            if (AssistantMarkdown.TailKey is { } tailKey)
            {
                AddMarkdownNode(tailKey.BlockIndex, tailKey.ChunkIndex, arrivalOrder);
            }
        }

        private void RegisterAllMarkdownNodes(long arrivalOrder)
        {
            foreach (var block in AssistantMarkdown.SettledBlocks)
            {
                AddMarkdownNode(block.BlockIndex, block.ChunkIndex, arrivalOrder);
            }
            if (AssistantMarkdown.TailKey is { } tailKey)
            {
                AddMarkdownNode(tailKey.BlockIndex, tailKey.ChunkIndex, arrivalOrder);
            }
        }

        private void AddMarkdownNode(int blockIndex, int chunkIndex, long arrivalOrder)
        {
            if (!_markdownKeys.Add((blockIndex, chunkIndex)))
            {
                return;
            }
            AddNode(new TurnTimelineNode(TurnNodeKind.Markdown, arrivalOrder)
            {
                MarkdownKey = (blockIndex, chunkIndex),
            });
        }

        private void AddNode(TurnTimelineNode node)
        {
            if (Timeline.Count == 0 || Timeline[^1].ArrivalOrder <= node.ArrivalOrder)
            {
                Timeline.Add(node);
                return;
            }
            var insertAt = Timeline.Count;
            while (insertAt > 0 && Timeline[insertAt - 1].ArrivalOrder > node.ArrivalOrder)
            {
                insertAt--;
            }
            Timeline.Insert(insertAt, node);
        }
    }
}
