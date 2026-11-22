using DshMobile.Agent.Domain;

namespace DshMobile.Agent.Engines;

/// <summary>
/// Implements the "subscribe first -> load snapshot -> contiguous watermark merge -> secondary gap verification"
/// reconciler (`N-03-05`), handing over smoothly from offline <see cref="KeptTranscriptStore"/> cache to live events.
/// </summary>
public sealed class HistoryReconciler(ISessionGateway sessionGateway, IEventGateway eventGateway)
{
    private readonly ISessionGateway _sessions = sessionGateway ?? throw new ArgumentNullException(nameof(sessionGateway));
    private readonly IEventGateway _events = eventGateway ?? throw new ArgumentNullException(nameof(eventGateway));

    public async Task<Outcome<IAsyncDisposable>> AttachAndReconcileAsync(
        TranscriptHandle handle,
        int maxMessages = 100,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var bufferedAfterSubscribe = new List<SessionFollowUpdate>();
        var reconcileDone = false;
        var bufferLock = new Lock();

        // 1. Subscribe BEFORE fetching cold history so live frames arriving during snapshot read are never lost.
        var followOutcome = await _events.FollowSessionAsync(
            handle.Scope.SessionId,
            update =>
            {
                lock (bufferLock)
                {
                    if (!reconcileDone)
                    {
                        bufferedAfterSubscribe.Add(update);
                        return ValueTask.CompletedTask;
                    }
                }
                handle.PostFollowUpdate(handle.Scope, update);
                return ValueTask.CompletedTask;
            },
            maxMessages: maxMessages,
            activate: true,
            ct: ct).ConfigureAwait(false);

        if (!followOutcome.IsOk)
        {
            handle.PostPartialHistoryFailure(followOutcome.Error!.Code.ToString());
            return followOutcome;
        }

        try
        {
            // 2. Pull cold history page; never fabricate missing history if load fails.
            var pageOutcome = await _sessions.LoadSessionEventsAsync(
                handle.Scope.SessionId,
                maxMessages: maxMessages,
                ct: ct).ConfigureAwait(false);

            if (!pageOutcome.IsOk)
            {
                handle.PostPartialHistoryFailure(pageOutcome.Error!.Code.ToString());
                lock (bufferLock)
                {
                    reconcileDone = true;
                    foreach (var buffered in bufferedAfterSubscribe)
                    {
                        handle.PostFollowUpdate(handle.Scope, buffered);
                    }
                    bufferedAfterSubscribe.Clear();
                }
                return followOutcome;
            }

            // 3. Watermark merge + contiguous sequence gap check.
            var page = pageOutcome.Value!;
            var merged = new Dictionary<long, SessionWireEventView>(page.Events.Count);
            foreach (var ev in page.Events)
            {
                merged[ev.Seq] = ev;
            }

            List<SessionFollowUpdate> replayUpdates;
            lock (bufferLock)
            {
                reconcileDone = true;
                replayUpdates = [.. bufferedAfterSubscribe];
                bufferedAfterSubscribe.Clear();
            }

            var postMergeUpdates = new List<SessionFollowUpdate>();
            var hasMoreFromSnap = false;
            long maxBufferedSeq = page.Cursor;

            foreach (var u in replayUpdates)
            {
                if (u is SessionFollowSnapshotUpdate snap)
                {
                    if (snap.HasMore)
                    {
                        hasMoreFromSnap = true;
                    }
                    foreach (var ev in snap.Events)
                    {
                        merged[ev.Seq] = ev;
                        if (ev.Seq > maxBufferedSeq)
                        {
                            maxBufferedSeq = ev.Seq;
                        }
                    }
                }
                else if (u is SessionFollowEventUpdate evUp)
                {
                    if (evUp.Event.Seq > page.Cursor)
                    {
                        merged[evUp.Event.Seq] = evUp.Event;
                    }
                    if (evUp.Event.Seq > maxBufferedSeq)
                    {
                        maxBufferedSeq = evUp.Event.Seq;
                    }
                }
                else
                {
                    postMergeUpdates.Add(u);
                }
            }

            // Detect missing seq gap between cold page.Cursor and buffered live events (e.g. cold ended at 10, live started at 13 -> missing 11..12).
            var contiguousCursor = ComputeContiguousCursor(page.Cursor, maxBufferedSeq, merged);
            if (contiguousCursor < maxBufferedSeq)
            {
                // One secondary verification pass bounded by observed watermark (`N-03-05`).
                var verifyOutcome = await _sessions.LoadSessionEventsAsync(
                    handle.Scope.SessionId,
                    throughSeq: maxBufferedSeq,
                    maxMessages: maxMessages,
                    ct: ct).ConfigureAwait(false);
                if (verifyOutcome.IsOk)
                {
                    foreach (var ev in verifyOutcome.Value!.Events)
                    {
                        merged[ev.Seq] = ev;
                    }
                    contiguousCursor = ComputeContiguousCursor(page.Cursor, maxBufferedSeq, merged);
                }
            }

            var stillHasGap = contiguousCursor < maxBufferedSeq;
            var eventsToCommit = merged.Values
                .Where(e => !stillHasGap || e.Seq <= contiguousCursor)
                .OrderBy(e => e.Seq)
                .ToArray();

            handle.PostReconciledSnapshot(
                contiguousCursor,
                eventsToCommit,
                isPartialHistory: page.HasMore || hasMoreFromSnap || stillHasGap,
                failureCode: stillHasGap ? "history.sequence_gap" : null);

            foreach (var extra in postMergeUpdates)
            {
                handle.PostFollowUpdate(handle.Scope, extra);
            }

            return followOutcome;
        }
        catch (OperationCanceledException)
        {
            await DisposeFollowQuietlyAsync(followOutcome.Value!).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await DisposeFollowQuietlyAsync(followOutcome.Value!).ConfigureAwait(false);
            var failure = DshFailureMapper.FromException(ex);
            handle.PostPartialHistoryFailure(failure.Code.ToString());
            return Outcome<IAsyncDisposable>.Fail(failure);
        }
    }

    private static async Task DisposeFollowQuietlyAsync(IAsyncDisposable handle)
    {
        try
        {
            await handle.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
        }
    }

    private static long ComputeContiguousCursor(long startCursor, long targetMaxSeq, Dictionary<long, SessionWireEventView> merged)
    {
        var cursor = startCursor;
        while (cursor < targetMaxSeq && merged.ContainsKey(cursor + 1))
        {
            cursor++;
        }
        return cursor;
    }
}
