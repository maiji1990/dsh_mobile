using System.Collections.Concurrent;
using System.Threading.Channels;
using DshMobile.Agent.Domain;

namespace DshMobile.Agent.Engines;

/// <summary>
/// Manages per-session single-writer <see cref="Channel{T}"/> pipelines, <see cref="CoalescingPublisher"/>,
/// detached Cache-First handles, and cross-session scope isolation (N-03-01 .. N-03-05).
/// </summary>
public sealed class TranscriptEngine : IAsyncDisposable
{
    public const int MaxRetainedSessions = 32;

    private readonly ConcurrentDictionary<SessionKey, TranscriptHandle> _handles = new();
    private readonly Lock _evictLock = new();
    private readonly Queue<SessionKey> _lruOrder = [];
    private readonly TimeProvider _time;
    private readonly TimeSpan _coalesceWindow;
    private int _disposed;

    public TranscriptEngine(TimeProvider? time = null, TimeSpan? coalesceWindow = null)
    {
        _time = time ?? TimeProvider.System;
        _coalesceWindow = coalesceWindow ?? CoalescingPublisher.DefaultWindow;
    }

    public event Action<SessionKey, ToolInvocationSummary>? ToolInvocationRecorded;

    /// <summary>当某个会话的内存句柄从引擎中移除（卸载或 LRU 淘汰）前触发，供订阅方同步解绑悬空引用。</summary>
    public event Action<SessionKey>? SessionRemoved;

    public TranscriptHandle GetOrCreateSession(SessionKey scope, int maxDeferredParts = TranscriptReducer.DefaultMaxDeferredParts)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var created = false;
        var handle = _handles.GetOrAdd(scope, k =>
        {
            created = true;
            return new TranscriptHandle(k, _time, _coalesceWindow, maxDeferredParts, summary => ToolInvocationRecorded?.Invoke(k, summary));
        });
        if (created)
        {
            SessionKey? evictedKey = null;
            TranscriptHandle? evictedHandle = null;
            lock (_evictLock)
            {
                _lruOrder.Enqueue(scope);
                while (_handles.Count > MaxRetainedSessions && _lruOrder.TryDequeue(out var cand))
                {
                    if (cand != scope && !(_handles.TryGetValue(cand, out var h) && h.HasActiveSubscribers))
                    {
                        if (_handles.TryRemove(cand, out evictedHandle))
                        {
                            evictedKey = cand;
                        }
                        break;
                    }
                }
            }
            if (evictedKey is not null && evictedHandle is not null)
            {
                SessionRemoved?.Invoke(evictedKey.Value);
                _ = evictedHandle.DisposeAsync().AsTask();
            }
        }
        return handle;
    }

    public bool TryGetSession(SessionKey scope, out TranscriptHandle? handle) =>
        _handles.TryGetValue(scope, out handle);

    /// <summary>卸载指定作用域的内存会话句柄并释放底层管道资源，不存在时返回 false。</summary>
    public async Task<bool> RemoveSessionAsync(SessionKey key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_handles.TryRemove(key, out var handle))
        {
            return false;
        }

        SessionRemoved?.Invoke(key);
        await handle.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>按会话 ID 卸载所有匹配的内存会话句柄并释放资源，返回卸载数量。</summary>
    public async Task<int> RemoveSessionsByIdAsync(string sessionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return 0;
        }

        var normalizedId = sessionId.Trim();
        var matchingKeys = _handles.Keys
            .Where(scope => string.Equals(scope.SessionId, normalizedId, StringComparison.Ordinal))
            .ToArray();

        var removedCount = 0;
        foreach (var scope in matchingKeys)
        {
            ct.ThrowIfCancellationRequested();
            if (_handles.TryRemove(scope, out var handle))
            {
                SessionRemoved?.Invoke(scope);
                await handle.DisposeAsync().ConfigureAwait(false);
                removedCount++;
            }
        }

        return removedCount;
    }

    /// <summary>
    /// Creates and hydrates a detached offline handle using the same reducer pipeline (`N-03-05`).
    /// Does not overwrite an already-live session handle.
    /// </summary>
    public TranscriptHandle CreateDetached(SessionKey scope, KeptTranscriptRecord kept)
    {
        ArgumentNullException.ThrowIfNull(kept);
        var handle = GetOrCreateSession(scope);
        handle.LoadDetachedCacheIfNotLive(kept);
        return handle;
    }

    public bool TryGetToolInvocation(SessionKey scope, string callId, out ToolInvocationSummary summary)
    {
        if (_handles.TryGetValue(scope, out var handle))
        {
            return handle.TryGetToolInvocation(callId, out summary);
        }
        summary = default!;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        foreach (var kv in _handles)
        {
            await kv.Value.DisposeAsync().ConfigureAwait(false);
        }
        _handles.Clear();
    }
}

public sealed class TranscriptHandle : IAsyncDisposable
{
    private readonly TranscriptReducer _reducer;
    private readonly CoalescingPublisher _publisher;
    private readonly Channel<Action<TranscriptReducer>> _mailbox;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pumpTask;
    private readonly Lock _snapshotLock = new();

    private ChatSnapshot _currentSnapshot;
    private long _historyVisits;
    private int _disposed;

    internal TranscriptHandle(
        SessionKey scope,
        TimeProvider time,
        TimeSpan coalesceWindow,
        int maxDeferredParts,
        Action<ToolInvocationSummary>? onToolInvocation = null)
    {
        Scope = scope;
        _reducer = new TranscriptReducer(scope, maxDeferredParts, onToolInvocation);
        _currentSnapshot = new ChatSnapshot(
            Revision: 0,
            Session: scope,
            CursorSeq: -1,
            Phase: RunPhase.Idle,
            IsDetachedCache: false,
            IsPartialHistory: false,
            FailureCode: null,
            Rows: [],
            Counters: TranscriptCounters.Zero);

        _publisher = new CoalescingPublisher(
            PublishSnapshotAsync,
            coalesceWindow,
            time,
            ex => OnPipelineError("publisher.error", ex));
        _mailbox = Channel.CreateUnbounded<Action<TranscriptReducer>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        _pumpTask = Task.Run(PumpAsync);
    }

    public SessionKey Scope { get; }

    /// <summary>当前句柄是否已进入释放态；用于订阅方避免向已卸载句柄重新挂载订阅。</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public bool HasActiveSubscribers => _publisher.SubscriberCount > 0;

    public ChatSnapshot CurrentSnapshot
    {
        get
        {
            lock (_snapshotLock)
            {
                return _currentSnapshot;
            }
        }
    }

    public event Action<ChatSnapshot>? SnapshotPublished;
    public event Action<SessionKey>? CalibrationRequested;
    public event Action<DshFailure>? PipelineError;

    public IDisposable Subscribe(Action<ChatSnapshot> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        var inner = _publisher.Subscribe();
        Action<ChatSnapshot> wrapped = snapshot => observer(snapshot);
        SnapshotPublished += wrapped;
        observer(CurrentSnapshot);
        return new ObserverRegistration(this, inner, wrapped);
    }

    /// <summary>Thread-safe lookup via reducer's immutable snapshot ledger.</summary>
    public bool TryGetToolInvocation(string callId, out ToolInvocationSummary summary) =>
        _reducer.TryGetToolInvocation(callId, out summary);

    public async Task<ChatSnapshot> WaitForIdleDrainAsync(CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<ChatSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(r =>
        {
            var snap = r.DeriveSnapshot(_publisher.Publishes, _publisher.SkippedWhileHidden, Volatile.Read(ref _historyVisits));
            lock (_snapshotLock)
            {
                _currentSnapshot = snap;
            }
            tcs.TrySetResult(snap);
        }, notifyPublisher: false);
        return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>将冷读分页拉取的更早历史事件前置注入当前会话，并触发快照发布（N-05-03）。</summary>
    public void PostPrependedPage(SessionKey eventScope, IReadOnlyList<SessionWireEventView> olderEvents, bool stillHasMore)
    {
        ArgumentNullException.ThrowIfNull(olderEvents);
        if (eventScope != Scope)
        {
            return;
        }
        Enqueue(r =>
        {
            r.PrependOlderEvents(olderEvents, stillHasMore);
            _publisher.NotifyDirty();
        }, notifyPublisher: false);
    }

    public void PostFollowUpdate(SessionKey eventScope, SessionFollowUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (eventScope != Scope)
        {
            Enqueue(r =>
            {
                r.RecordOutOfScopeDrop();
                _publisher.NotifyDirty();
            }, notifyPublisher: false);
            return;
        }

        Enqueue(r =>
        {
            var changed = false;
            var triggerFlush = false;
            switch (update)
            {
                case SessionFollowSnapshotUpdate snap:
                    if (!string.IsNullOrEmpty(snap.SessionId) &&
                        !string.Equals(snap.SessionId, Scope.SessionId, StringComparison.Ordinal))
                    {
                        r.RecordOutOfScopeDrop();
                        _publisher.NotifyDirty();
                        return;
                    }
                    Interlocked.Increment(ref _historyVisits);
                    r.ApplyReconciledSnapshot(snap.Cursor, snap.Events, isPartialHistory: snap.HasMore);
                    changed = true;
                    triggerFlush = true;
                    break;
                case SessionFollowEventUpdate ev:
                    changed = r.ApplyWireEvent(eventScope, ev.Event);
                    if (ev.Event.Type == "turn/end")
                    {
                        triggerFlush = true;
                    }
                    break;
                case SessionFollowAssistantStreamUpdate st:
                    changed = r.ApplyAssistantStreamFrame(eventScope, st);
                    if (st.FrameType == "end")
                    {
                        triggerFlush = true;
                    }
                    break;
            }

            if (r.NeedsCalibration)
            {
                CalibrationRequested?.Invoke(Scope);
            }
            if (changed)
            {
                _publisher.NotifyDirty();
                if (triggerFlush)
                {
                    _publisher.Flush();
                }
            }
        }, notifyPublisher: false);
    }

    public void PostReconciledSnapshot(
        long cursorSeq,
        IReadOnlyList<SessionWireEventView> events,
        bool isPartialHistory = false,
        string? failureCode = null)
    {
        Enqueue(r =>
        {
            Interlocked.Increment(ref _historyVisits);
            r.ApplyReconciledSnapshot(cursorSeq, events, isPartialHistory, failureCode);
            _publisher.NotifyDirty();
            _publisher.Flush();
        }, notifyPublisher: false);
    }

    public void PostPartialHistoryFailure(string failureCode)
    {
        Enqueue(r =>
        {
            r.MarkPartialHistory(failureCode);
            _publisher.NotifyDirty();
            _publisher.Flush();
        }, notifyPublisher: false);
    }

    public void PostSubagentActivity(SessionKey eventScope, string childSessionId, string? parentSessionId, string statusLine)
    {
        Enqueue(r =>
        {
            if (r.ApplySubagentActivity(eventScope, childSessionId, parentSessionId, statusLine))
            {
                _publisher.NotifyDirty();
            }
        }, notifyPublisher: false);
    }

    public void LoadDetachedCacheIfNotLive(KeptTranscriptRecord kept)
    {
        ArgumentNullException.ThrowIfNull(kept);
        Enqueue(r =>
        {
            if (r.HasLiveEvents)
            {
                return;
            }
            var rows = KeptTranscriptStore.ToDetachedChatRows(kept);
            r.LoadDetachedCache(kept.CursorSeq, kept.Phase, rows, kept.TurnDurationsMs);
            _publisher.NotifyDirty();
            _publisher.Flush();
        }, notifyPublisher: false);
    }

    public void FlushPublisher() => _publisher.Flush();

    internal void EnqueueFaultTestAction(Action<TranscriptReducer> action) => Enqueue(action, notifyPublisher: false);

    private void Enqueue(Action<TranscriptReducer> work, bool notifyPublisher)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        if (_mailbox.Writer.TryWrite(work) && notifyPublisher)
        {
            _publisher.NotifyDirty();
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var action in _mailbox.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                try
                {
                    action(_reducer);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Never allow one malformed item or subscriber failure to silently kill the session pipeline.
                    OnPipelineError("pump.item_error", ex);
                    _reducer.MarkPartialHistory("engine.reducer_error");
                    _publisher.NotifyDirty();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on disposal.
        }
    }

    private async ValueTask PublishSnapshotAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        var tcs = new TaskCompletionSource<ChatSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_mailbox.Writer.TryWrite(r =>
        {
            var snap = r.DeriveSnapshot(_publisher.Publishes, _publisher.SkippedWhileHidden, Volatile.Read(ref _historyVisits));
            tcs.TrySetResult(snap);
        }))
        {
            return;
        }

        try
        {
            var snap = await tcs.Task.WaitAsync(_cts.Token).ConfigureAwait(false);
            lock (_snapshotLock)
            {
                _currentSnapshot = snap;
            }
            SnapshotPublished?.Invoke(snap);
        }
        catch (OperationCanceledException)
        {
            // Ignore during teardown.
        }
    }

    private void OnPipelineError(string diagnosticKey, Exception ex)
    {
        _ = ex;
        PipelineError?.Invoke(new DshFailure(DshFailureCode.ProtocolMismatch, diagnosticKey, Retryable: false));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _mailbox.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);
        await _publisher.DisposeAsync().ConfigureAwait(false);
        await _pumpTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _cts.Dispose();
    }

    private sealed class ObserverRegistration(
        TranscriptHandle owner,
        IDisposable publisherSub,
        Action<ChatSnapshot> handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.SnapshotPublished -= handler;
                publisherSub.Dispose();
            }
        }
    }
}
