using System.Text.Json;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;

namespace DshMobile.Agent.Tests.Engines;

public sealed class TranscriptFaultAndReconcileTests : IDisposable
{
    private readonly string _cacheDir = Directory.CreateTempSubdirectory("dshm-kept-").FullName;

    public void Dispose() => Directory.Delete(_cacheDir, recursive: true);

    private sealed class FakeGateway : DefaultDshGateway
    {
        public Func<string, int?, CancellationToken, Task<Outcome<SessionPageView>>>? OnLoadEvents { get; set; }
        public Func<string, long?, long?, int?, CancellationToken, Task<Outcome<SessionPageView>>>? OnLoadEventsFull { get; set; }
        public Func<string, Func<SessionFollowUpdate, ValueTask>, int?, bool, CancellationToken, Task<Outcome<IAsyncDisposable>>>? OnFollow { get; set; }
        public Func<PromptSendRequest, CancellationToken, Task<Outcome<Unit>>>? OnSendPrompt { get; set; }

        public int SendPromptCalls;
        public List<string> SentRequestIds { get; } = [];

        public override Task<Outcome<SessionPageView>> LoadSessionEventsAsync(
            string sessionId, long? throughSeq = null, long? beforeSeq = null, int? maxMessages = null, CancellationToken ct = default) =>
            OnLoadEventsFull is not null
                ? OnLoadEventsFull(sessionId, throughSeq, beforeSeq, maxMessages, ct)
                : OnLoadEvents is not null
                    ? OnLoadEvents(sessionId, maxMessages, ct)
                    : Task.FromResult(Outcome<SessionPageView>.Ok(new SessionPageView([], false, 0)));

        public override Task<Outcome<IAsyncDisposable>> FollowSessionAsync(
            string sessionId, Func<SessionFollowUpdate, ValueTask> onUpdate, int? maxMessages = null, bool activate = true, CancellationToken ct = default) =>
            OnFollow is not null
                ? OnFollow(sessionId, onUpdate, maxMessages, activate, ct)
                : Task.FromResult<Outcome<IAsyncDisposable>>(Outcome<IAsyncDisposable>.Ok(new DummyDisposable()));

        public override Task<Outcome<Unit>> SendPromptAsync(PromptSendRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref SendPromptCalls);
            SentRequestIds.Add(request.RequestId);
            return OnSendPrompt is not null
                ? OnSendPrompt(request, ct)
                : Task.FromResult(Outcome<Unit>.Ok(Unit.Value));
        }

        private sealed class DummyDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void Reducer_IsolatesSessionScope_EnforcesMonotonicToolPhase_AndHandlesDeferredOverflow()
    {
        var scope = new SessionKey("rt-1", "web", "s-1");
        var otherScope = new SessionKey("rt-1", "web", "s-2");
        var reducer = new TranscriptReducer(scope, maxDeferredParts: 4);

        // Wrong scope event dropped
        Assert.False(reducer.ApplyWireEvent(otherScope, new SessionWireEventView(
            "turn/start", 1, 100, JsonElement.Parse("""{"turn":1}"""))));
        Assert.Equal(1, reducer.DeriveSnapshot(0, 0, 0).Counters.DroppedOutOfScopeCount);

        // Unvalidated foreign subagent rejected; validated child accepted
        Assert.False(reducer.ApplySubagentActivity(scope, "child-x", "other-parent", "working"));
        Assert.True(reducer.ApplySubagentActivity(scope, "child-1", "s-1", "working"));

        // Tool call -> Completed -> late delta cannot regress phase from Completed back to Running
        Assert.True(reducer.ApplyWireEvent(scope, new SessionWireEventView(
            "turn/start", 1, 100, JsonElement.Parse("""{"turn":1}"""))));
        Assert.True(reducer.ApplyWireEvent(scope, new SessionWireEventView(
            "tool/call", 2, 101, JsonElement.Parse("""{"callId":"c-1","name":"bash","arguments":"{\"cmd\":\"echo sk-secret123456\"}"}"""))));
        Assert.True(reducer.ApplyWireEvent(scope, new SessionWireEventView(
            "tool/result", 3, 102, JsonElement.Parse("""{"callId":"c-1","message":{"isError":false,"content":[{"type":"text","text":"done"}]}}"""))));
        Assert.True(reducer.ApplyWireEvent(scope, new SessionWireEventView(
            "tool/call", 4, 103, JsonElement.Parse("""{"callId":"c-1","name":"bash","arguments":"late"}"""))));

        Assert.True(reducer.TryGetToolInvocation("c-1", out var summary));
        Assert.DoesNotContain("sk-secret123456", summary.SanitizedArguments, StringComparison.Ordinal);

        // Deferred parts overflow (> 4 without start frame) sets NeedsCalibration
        for (var i = 0; i < 5; i++)
        {
            using var chunkDoc = JsonDocument.Parse(
                "{\"type\":\"chunk\",\"attemptId\":\"missing\",\"revision\":1,\"index\":" + i + ",\"chunk\":{\"type\":\"text-delta\",\"text\":\"x\"}}");
            reducer.ApplyAssistantStreamFrame(scope, new SessionFollowAssistantStreamUpdate("chunk", "missing", 1, chunkDoc.RootElement.Clone()));
        }
        Assert.True(reducer.NeedsCalibration);
        var snap = reducer.DeriveSnapshot(0, 0, 0);
        Assert.Equal(1, snap.Counters.DeferredOverflowCount);
        var toolRow = Assert.Single(snap.Rows.OfType<ToolCallRow>());
        Assert.Equal(ToolPhase.Completed, toolRow.Phase);
    }

    [Fact]
    public async Task KeptTranscriptStore_AndHistoryReconciler_PreserveSanitizedCacheAndWatermarkMerge()
    {
        var store = new KeptTranscriptStore(_cacheDir);
        var scope = new SessionKey("rt-1", "web", "s-cache");

        var initialSnap = new ChatSnapshot(
            Revision: 3,
            Session: scope,
            CursorSeq: 5,
            Phase: RunPhase.Idle,
            IsDetachedCache: false,
            IsPartialHistory: false,
            FailureCode: null,
            Rows:
            [
                new UserMessageRow("msg:1", 1, "1", "My key is sk-liveSecretToken99", 0, false),
                new MarkdownBlockRow("md:1", 1, "a1", 0, 0, "Cached reply", true, 0, false, false),
            ],
            Counters: TranscriptCounters.Zero);

        await store.SaveAsync(initialSnap);
        var loaded = await store.TryLoadAsync(scope);
        Assert.NotNull(loaded);
        Assert.DoesNotContain("sk-liveSecretToken99", JsonSerializer.Serialize(loaded), StringComparison.Ordinal);

        await using var engine = new TranscriptEngine();
        var detachedHandle = engine.CreateDetached(scope, loaded!);
        var detached = await detachedHandle.WaitForIdleDrainAsync();
        Assert.True(detached.IsDetachedCache);
        Assert.Equal(2, detached.Rows.Count);

        // Now attach HistoryReconciler: subscribe first, buffer live seq 11 while cold snapshot returns seq 1..10
        var gw = new FakeGateway
        {
            OnFollow = async (_, onUpdate, _, _, _) =>
            {
                await onUpdate(new SessionFollowEventUpdate(new SessionWireEventView(
                    "assistant/message", 11, 200,
                    JsonElement.Parse("""{"id":"a-live","content":[{"type":"text","text":"Live new text"}]}"""))));
                return Outcome<IAsyncDisposable>.Ok(new AsyncNoop());
            },
            OnLoadEvents = (_, _, _) => Task.FromResult(Outcome<SessionPageView>.Ok(new SessionPageView(
                [
                    new SessionWireEventView("turn/start", 9, 190, JsonElement.Parse("""{"turn":2}""")),
                    new SessionWireEventView("user/message", 10, 195, JsonElement.Parse("""{"id":"u-2","content":[{"type":"text","text":"Hello live"}]}""")),
                ],
                HasMore: false,
                Cursor: 10))),
        };

        var reconciler = new HistoryReconciler(gw, gw);
        var attached = await reconciler.AttachAndReconcileAsync(detachedHandle);
        Assert.True(attached.IsOk);

        var liveSnap = await detachedHandle.WaitForIdleDrainAsync();
        Assert.False(liveSnap.IsDetachedCache);
        Assert.Equal(11, liveSnap.CursorSeq);
        Assert.Contains(liveSnap.Rows.OfType<MarkdownBlockRow>(), m => m.Markdown == "Live new text");
        await attached.Value!.DisposeAsync();
    }

    [Fact]
    public async Task SendStateMachine_ProtectsDuplicateExecutionOnUnknownOutcome()
    {
        var gw = new FakeGateway();
        var sm = new SendStateMachine(gw, gw);

        // 1. Route B idempotent retry keeps exact same RequestId
        var opB = sm.CreateDraft("s-1", "Run check", requestId: "req-idem-1");
        gw.OnSendPrompt = (_, _) => Task.FromResult(Outcome<Unit>.Fail(DshFailureCode.TransportLost, "dropped"));
        var unknownB = await sm.DispatchAsync(opB.ClientOpId, new PromptSendRequest("req-idem-1", "s-1", "Run check"));
        Assert.Equal(OutboundSendPhase.UnknownOutcome, unknownB.Phase);

        gw.OnSendPrompt = (_, _) => Task.FromResult(Outcome<Unit>.Ok(Unit.Value));
        var recoveredB = await sm.RecoverUnknownOutcomeAsync(
            opB.ClientOpId,
            new PromptSendRequest("req-idem-1", "s-1", "Run check"),
            DshCapabilities.Empty with { ServerPromptIdempotency = true });
        Assert.Equal(OutboundSendPhase.Enqueued, recoveredB.Phase);
        Assert.Equal(["req-idem-1", "req-idem-1"], gw.SentRequestIds);

        // 2. Non-idempotent transport on UnknownOutcome NEVER auto-resends; checks history first
        gw.SentRequestIds.Clear();
        gw.SendPromptCalls = 0;
        var opA = sm.CreateDraft("s-1", "Deploy", requestId: "req-noidem-2");
        gw.OnSendPrompt = (_, _) => Task.FromResult(Outcome<Unit>.Fail(DshFailureCode.Timeout, "timeout"));
        await sm.DispatchAsync(opA.ClientOpId, new PromptSendRequest("req-noidem-2", "s-1", "Deploy"));

        // Case 2a: Not in history -> requires explicit user confirmation (0 extra SendPrompt calls!)
        gw.OnLoadEvents = (_, _, _) => Task.FromResult(Outcome<SessionPageView>.Ok(new SessionPageView([], false, 0)));
        var needConfirm = await sm.RecoverUnknownOutcomeAsync(
            opA.ClientOpId,
            new PromptSendRequest("req-noidem-2", "s-1", "Deploy"),
            DshCapabilities.Empty with { ServerPromptIdempotency = false });
        Assert.Equal(OutboundSendPhase.RequiresUserConfirmation, needConfirm.Phase);
        Assert.Equal(1, gw.SendPromptCalls);

        // Case 2b: Already in history (`rpcId` found) -> resolves as Enqueued without resending!
        var opA2 = sm.CreateDraft("s-2", "Deploy 2", requestId: "req-noidem-3");
        gw.OnSendPrompt = (_, _) => Task.FromResult(Outcome<Unit>.Fail(DshFailureCode.TransportLost, "drop"));
        await sm.DispatchAsync(opA2.ClientOpId, new PromptSendRequest("req-noidem-3", "s-2", "Deploy 2"));

        gw.OnLoadEvents = (_, _, _) => Task.FromResult(Outcome<SessionPageView>.Ok(new SessionPageView(
            [
                new SessionWireEventView("user/message", 7, 100, JsonElement.Parse("""{"id":"m7","source":{"kind":"user-rpc","rpcId":"req-noidem-3"}}""")),
            ],
            false,
            7)));
        var foundInHistory = await sm.RecoverUnknownOutcomeAsync(
            opA2.ClientOpId,
            new PromptSendRequest("req-noidem-3", "s-2", "Deploy 2"),
            DshCapabilities.Empty with { ServerPromptIdempotency = false });
        Assert.Equal(OutboundSendPhase.Enqueued, foundInHistory.Phase);
        Assert.Equal(7, foundInHistory.MatchedTurnSeq);
    }

    [Fact]
    public void Reducer_LateToolResultAndStreamAfterTurnEnd_BackfillsSettledTurnWithoutOverwritingHistory()
    {
        var scope = new SessionKey("rt-1", "web", "s-late");
        var reducer = new TranscriptReducer(scope);

        Assert.True(reducer.ApplyWireEvent(scope, new SessionWireEventView(
            "turn/start", 1, 100, JsonElement.Parse("""{"turn":1}"""))));
        Assert.True(reducer.ApplyWireEvent(scope, new SessionWireEventView(
            "user/message", 2, 101, JsonElement.Parse("""{"id":"u-1","content":[{"type":"text","text":"turn 1 user"}]}"""))));
        Assert.True(reducer.ApplyWireEvent(scope, new SessionWireEventView(
            "tool/call", 3, 102, JsonElement.Parse("""{"callId":"c-1","name":"bash","arguments":"{\"cmd\":\"ls\"}"}"""))));
        Assert.True(reducer.ApplyWireEvent(scope, new SessionWireEventView(
            "assistant/message", 4, 103, JsonElement.Parse("""{"id":"a-1","content":[{"type":"text","text":"turn 1 reply"}]}"""))));
        Assert.True(reducer.ApplyWireEvent(scope, new SessionWireEventView(
            "turn/end", 5, 104, JsonElement.Parse("""{"turn":1,"reason":{"kind":"completed"}}"""))));

        // Late tool/result arrives AFTER turn/end -> must backfill settled turn 1 without creating synthetic Turn 1 or flipping Phase
        Assert.True(reducer.ApplyWireEvent(scope, new SessionWireEventView(
            "tool/result", 6, 105, JsonElement.Parse("""{"callId":"c-1","message":{"isError":false,"content":[{"type":"text","text":"ok"}]}}"""))));

        var afterLateTool = reducer.DeriveSnapshot(0, 0, 0);
        Assert.Equal(RunPhase.Completed, afterLateTool.Phase);
        var backfilledTool = Assert.Single(afterLateTool.Rows.OfType<ToolCallRow>());
        Assert.Equal(1, backfilledTool.TurnSeq);
        Assert.Equal(ToolPhase.Completed, backfilledTool.Phase);
        Assert.Single(afterLateTool.Rows.OfType<UserMessageRow>());

        // Late chunk referencing already settled attempt (or orphan chunk without start) must NOT overwrite Turn 1 or drag Phase to Streaming
        Assert.False(reducer.ApplyAssistantStreamFrame(scope, new SessionFollowAssistantStreamUpdate(
            "chunk",
            "att-settled",
            1,
            JsonElement.Parse("""{"type":"chunk","attemptId":"att-settled","revision":1,"index":0,"chunk":{"type":"text-delta","text":"late"}}"""))));
        Assert.Equal(RunPhase.Completed, reducer.DeriveSnapshot(0, 0, 0).Phase);

        // Starting Turn 2 must preserve ALL Turn 1 rows cleanly!
        Assert.True(reducer.ApplyWireEvent(scope, new SessionWireEventView(
            "turn/start", 7, 106, JsonElement.Parse("""{"turn":2}"""))));
        Assert.True(reducer.ApplyWireEvent(scope, new SessionWireEventView(
            "user/message", 8, 107, JsonElement.Parse("""{"id":"u-2","content":[{"type":"text","text":"turn 2 user"}]}"""))));

        var turn2Snap = reducer.DeriveSnapshot(0, 0, 0);
        Assert.Contains(turn2Snap.Rows.OfType<UserMessageRow>(), u => u.TurnSeq == 1 && u.Text == "turn 1 user");
        Assert.Contains(turn2Snap.Rows.OfType<MarkdownBlockRow>(), m => m.TurnSeq == 1 && m.Markdown == "turn 1 reply");
        Assert.Contains(turn2Snap.Rows.OfType<ToolCallRow>(), t => t.TurnSeq == 1 && t.CallId == "c-1" && t.Phase == ToolPhase.Completed);
        Assert.Contains(turn2Snap.Rows.OfType<UserMessageRow>(), u => u.TurnSeq == 2 && u.Text == "turn 2 user");
    }

    [Fact]
    public async Task SendStateMachine_DeduplicatesConcurrentDispatch_AndBlocksConfirmationBypass()
    {
        var gw = new FakeGateway();
        var sm = new SendStateMachine(gw, gw);

        // 1. Concurrent DispatchAsync on the same clientOpId calls SendPromptAsync only ONCE
        var sendTcs = new TaskCompletionSource<Outcome<Unit>>(TaskCreationOptions.RunContinuationsAsynchronously);
        gw.OnSendPrompt = (_, _) => sendTcs.Task;

        var op = sm.CreateDraft("s-conc", "Concurrent prompt", requestId: "req-conc-1");
        var req = new PromptSendRequest("req-conc-1", "s-conc", "Concurrent prompt");
        var taskA = sm.DispatchAsync(op.ClientOpId, req);
        var taskB = sm.DispatchAsync(op.ClientOpId, req);

        Assert.Equal(1, gw.SendPromptCalls);
        sendTcs.SetResult(Outcome<Unit>.Ok(Unit.Value));
        var results = await Task.WhenAll(taskA, taskB);
        Assert.All(results, r => Assert.Equal(OutboundSendPhase.Enqueued, r.Phase));
        Assert.Equal(1, gw.SendPromptCalls);

        // 2. RequiresUserConfirmation & UnknownOutcome CANNOT be bypassed via DispatchAsync
        gw.SendPromptCalls = 0;
        gw.OnSendPrompt = (_, _) => Task.FromResult(Outcome<Unit>.Fail(DshFailureCode.TransportLost, "drop"));
        var opConfirm = sm.CreateDraft("s-conf", "Needs confirmation", requestId: "req-conf-1");
        var confReq = new PromptSendRequest("req-conf-1", "s-conf", "Needs confirmation");
        var unknownEntry = await sm.DispatchAsync(opConfirm.ClientOpId, confReq);
        Assert.Equal(OutboundSendPhase.UnknownOutcome, unknownEntry.Phase);

        var blockedUnknown = await sm.DispatchAsync(opConfirm.ClientOpId, confReq);
        Assert.Equal(OutboundSendPhase.UnknownOutcome, blockedUnknown.Phase);
        Assert.Equal(1, gw.SendPromptCalls);

        gw.OnLoadEvents = (_, _, _) => Task.FromResult(Outcome<SessionPageView>.Ok(new SessionPageView([], false, 0)));
        var needConfirm = await sm.RecoverUnknownOutcomeAsync(
            opConfirm.ClientOpId, confReq, DshCapabilities.Empty with { ServerPromptIdempotency = false });
        Assert.Equal(OutboundSendPhase.RequiresUserConfirmation, needConfirm.Phase);

        // Bypassing via DispatchAsync must stay blocked (0 extra RPCs)
        var blockedConfirm = await sm.DispatchAsync(opConfirm.ClientOpId, confReq);
        Assert.Equal(OutboundSendPhase.RequiresUserConfirmation, blockedConfirm.Phase);
        Assert.Equal(1, gw.SendPromptCalls);

        // Only explicit ConfirmResendAsync sends (reusing the op's RequestId)
        gw.OnSendPrompt = (_, _) => Task.FromResult(Outcome<Unit>.Ok(Unit.Value));
        var confirmed = await sm.ConfirmResendAsync(opConfirm.ClientOpId, confReq);
        Assert.Equal(OutboundSendPhase.Enqueued, confirmed.Phase);
        Assert.Equal(2, gw.SendPromptCalls);
        Assert.Equal("req-conf-1", confirmed.RequestId);
    }

    [Fact]
    public async Task KeptTranscriptStore_RedactsAllSecretKindsAndCommandArgsOnDisk_AndReportsCorruptFiles()
    {
        var store = new KeptTranscriptStore(_cacheDir);
        var scope = new SessionKey("rt-1", "web", "s-secrets");

        var missing = await store.LoadDetailedAsync(scope);
        Assert.Equal(KeptTranscriptLoadStatus.NotFound, missing.Status);

        var sshKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nabcdefsecretbody\n-----END OPENSSH PRIVATE KEY-----";
        var snap = new ChatSnapshot(
            Revision: 1,
            Session: scope,
            CursorSeq: 9,
            Phase: RunPhase.Idle,
            IsDetachedCache: false,
            IsPartialHistory: false,
            FailureCode: null,
            Rows:
            [
                new UserMessageRow("u:1", 1, "u1", $"github=ghp_1234567890abcdef aws=AKIA1234567890EXAMPLE {sshKey}", 0, false),
                new CommandRow("cmd:1", 1, "c-cmd-1", "curl", "--password hunter2 --apiKey=abc123 --secret xyz token=tok-999", false, "token=tok-999"),
                new NoticeRow("n:1", 1, "auth.warn", "Bearer eyJverySecretToken.payload.sig", true),
            ],
            Counters: TranscriptCounters.Zero);

        await store.SaveAsync(snap);

        var files = Directory.GetFiles(_cacheDir, "*.json", SearchOption.AllDirectories);
        var savedFile = Assert.Single(files);
        var rawDiskText = await File.ReadAllTextAsync(savedFile);
        Assert.DoesNotContain("ghp_1234567890abcdef", rawDiskText, StringComparison.Ordinal);
        Assert.DoesNotContain("AKIA1234567890EXAMPLE", rawDiskText, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefsecretbody", rawDiskText, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", rawDiskText, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", rawDiskText, StringComparison.Ordinal);
        Assert.DoesNotContain("xyz", rawDiskText, StringComparison.Ordinal);
        Assert.DoesNotContain("tok-999", rawDiskText, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJverySecretToken", rawDiskText, StringComparison.Ordinal);

        var loadedDetailed = await store.LoadDetailedAsync(scope);
        Assert.Equal(KeptTranscriptLoadStatus.Loaded, loadedDetailed.Status);
        Assert.NotNull(loadedDetailed.Record);

        // Corrupt JSON reports Corrupt cleanly
        await File.WriteAllTextAsync(savedFile, "{not valid json");
        var corruptDetailed = await store.LoadDetailedAsync(scope);
        Assert.Equal(KeptTranscriptLoadStatus.Corrupt, corruptDetailed.Status);
    }

    [Fact]
    public void Reducer_CalibrationResetsToolLedgerAndValidatedSubagentIds()
    {
        var scope = new SessionKey("rt-1", "web", "s-recal");
        var reducer = new TranscriptReducer(scope);

        Assert.True(reducer.ApplyWireEvent(scope, new SessionWireEventView(
            "turn/start", 1, 100, JsonElement.Parse("""{"turn":1}"""))));
        Assert.True(reducer.ApplyWireEvent(scope, new SessionWireEventView(
            "tool/call", 2, 101, JsonElement.Parse("""{"callId":"c-old","name":"bash","arguments":"{\"cmd\":\"old\"}"}"""))));
        Assert.True(reducer.ApplySubagentActivity(scope, "child-old", "s-recal", "working"));
        Assert.True(reducer.TryGetToolInvocation("c-old", out _));

        // Calibration resets session state -> stale tool callIds and old subagent whitelist must be cleared!
        reducer.ApplyReconciledSnapshot(
            10,
            [
                new SessionWireEventView("turn/start", 9, 200, JsonElement.Parse("""{"turn":2}""")),
                new SessionWireEventView("user/message", 10, 201, JsonElement.Parse("""{"id":"u-new","content":[{"type":"text","text":"clean"}]}""")),
            ]);

        Assert.False(reducer.TryGetToolInvocation("c-old", out _));
        Assert.False(reducer.ApplySubagentActivity(scope, "child-old", null, "working"));
    }

    [Fact]
    public async Task TranscriptEngine_AndReconciler_HandleMismatchedFollowScope_GapVerification_LiveClear_AndPumpFaults()
    {
        var scope = new SessionKey("rt-1", "web", "s-main");
        await using var engine = new TranscriptEngine();

        // 1. Detached cache is auto-cleared before direct live wire events; CreateDetached never overwrites live state
        var kept = new KeptTranscriptRecord(
            1, scope, 5, RunPhase.Idle, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            [new KeptChatRow(KeptRowKind.User, "u-cached", 1, "cached user")]);
        var handle = engine.CreateDetached(scope, kept);
        Assert.True((await handle.WaitForIdleDrainAsync()).IsDetachedCache);

        handle.PostFollowUpdate(scope, new SessionFollowEventUpdate(new SessionWireEventView(
            "user/message", 6, 100, JsonElement.Parse("""{"id":"u-live","content":[{"type":"text","text":"live msg"}]}"""))));
        var liveAfterEvent = await handle.WaitForIdleDrainAsync();
        Assert.False(liveAfterEvent.IsDetachedCache);
        Assert.Single(liveAfterEvent.Rows.OfType<UserMessageRow>(), u => u.Text == "live msg");

        // CreateDetached called again while live must NOT resurrect cached rows
        engine.CreateDetached(scope, kept);
        var stillLive = await handle.WaitForIdleDrainAsync();
        Assert.False(stillLive.IsDetachedCache);
        Assert.Single(stillLive.Rows.OfType<UserMessageRow>(), u => u.Text == "live msg");

        // 2. FollowSnapshot validates snap.SessionId and preserves snap.HasMore
        handle.PostFollowUpdate(scope, new SessionFollowSnapshotUpdate(
            "s-WRONG",
            1,
            false,
            [new SessionWireEventView("user/message", 1, 10, JsonElement.Parse("""{"id":"bad","content":[{"type":"text","text":"hijack"}]}"""))]));
        var afterBadSnap = await handle.WaitForIdleDrainAsync();
        Assert.True(afterBadSnap.Counters.DroppedOutOfScopeCount >= 1);
        Assert.DoesNotContain(afterBadSnap.Rows.OfType<UserMessageRow>(), u => u.Text == "hijack");

        handle.PostFollowUpdate(scope, new SessionFollowSnapshotUpdate(
            "s-main",
            8,
            true,
            [new SessionWireEventView("user/message", 8, 200, JsonElement.Parse("""{"id":"u-partial","content":[{"type":"text","text":"partial snap"}]}"""))]));
        var partialSnap = await handle.WaitForIdleDrainAsync();
        Assert.True(partialSnap.IsPartialHistory);

        // 3. Multiple subagent activity updates emit distinct ItemIds (no Duplicate key / Key crash)
        handle.PostSubagentActivity(scope, "child-1", "s-main", "running");
        handle.PostSubagentActivity(scope, "child-1", "s-main", "done");
        var subSnap = await handle.WaitForIdleDrainAsync();
        var subNotices = subSnap.Rows.OfType<NoticeRow>().Where(n => n.ItemId.StartsWith("sub:child-1:", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, subNotices.Count);
        Assert.Equal(2, subNotices.Select(n => n.ItemId).Distinct(StringComparer.Ordinal).Count());

        // 4. Pump fault isolation: fault action triggers PipelineError & IsPartialHistory without killing pump
        DshFailure? observedFailure = null;
        handle.PipelineError += f => observedFailure = f;
        handle.EnqueueFaultTestAction(_ => throw new InvalidOperationException("pump fault test"));
        handle.PostFollowUpdate(scope, new SessionFollowEventUpdate(new SessionWireEventView(
            "user/message", 9, 201, JsonElement.Parse("""{"id":"u-after-fault","content":[{"type":"text","text":"still alive"}]}"""))));
        var afterFault = await handle.WaitForIdleDrainAsync();
        Assert.NotNull(observedFailure);
        Assert.True(afterFault.IsPartialHistory);
        Assert.Contains(afterFault.Rows.OfType<UserMessageRow>(), u => u.Text == "still alive");

        // 5. HistoryReconciler gap detection & backfill + Follow disposal on LoadSessionEventsAsync failure
        var gapScope = new SessionKey("rt-1", "web", "s-gap");
        var gapHandle = engine.GetOrCreateSession(gapScope);
        var loadCalls = 0;
        long? observedThroughSeq = null;
        var gapGw = new FakeGateway
        {
            OnFollow = async (_, onUpdate, _, _, _) =>
            {
                await onUpdate(new SessionFollowEventUpdate(new SessionWireEventView(
                    "assistant/message", 13, 303,
                    JsonElement.Parse("""{"id":"a-13","content":[{"type":"text","text":"seq 13"}]}"""))));
                return Outcome<IAsyncDisposable>.Ok(new AsyncNoop());
            },
            OnLoadEventsFull = (_, throughSeq, _, _, _) =>
            {
                loadCalls++;
                if (loadCalls == 1)
                {
                    return Task.FromResult(Outcome<SessionPageView>.Ok(new SessionPageView(
                        [
                            new SessionWireEventView("turn/start", 9, 300, JsonElement.Parse("""{"turn":1}""")),
                            new SessionWireEventView("user/message", 10, 301, JsonElement.Parse("""{"id":"u-10","content":[{"type":"text","text":"seq 10"}]}""")),
                        ],
                        HasMore: false,
                        Cursor: 10)));
                }
                observedThroughSeq = throughSeq;
                return Task.FromResult(Outcome<SessionPageView>.Ok(new SessionPageView(
                    [
                        new SessionWireEventView("tool/call", 11, 302, JsonElement.Parse("""{"callId":"c-11","name":"bash","arguments":"{}"}""")),
                        new SessionWireEventView("tool/result", 12, 302, JsonElement.Parse("""{"callId":"c-11","message":{"isError":false,"content":[{"type":"text","text":"done"}]}}""")),
                    ],
                    HasMore: false,
                    Cursor: 12)));
            },
        };

        var gapReconciler = new HistoryReconciler(gapGw, gapGw);
        var gapRes = await gapReconciler.AttachAndReconcileAsync(gapHandle);
        Assert.True(gapRes.IsOk);
        var gapSnap = await gapHandle.WaitForIdleDrainAsync();
        Assert.Equal(2, loadCalls);
        Assert.Equal(13, observedThroughSeq);
        Assert.False(gapSnap.IsPartialHistory);
        Assert.Equal(13, gapSnap.CursorSeq);
        await gapRes.Value!.DisposeAsync();

        // Disposal check when LoadSessionEventsAsync throws non-canceled exception
        var failHandle = engine.GetOrCreateSession(new SessionKey("rt-1", "web", "s-throw"));
        var trackFollow = new TrackableDisposable();
        var throwGw = new FakeGateway
        {
            OnFollow = (_, _, _, _, _) => Task.FromResult<Outcome<IAsyncDisposable>>(Outcome<IAsyncDisposable>.Ok(trackFollow)),
            OnLoadEventsFull = (_, _, _, _, _) => throw new InvalidOperationException("load exploded"),
        };
        var failReconciler = new HistoryReconciler(throwGw, throwGw);
        var failRes = await failReconciler.AttachAndReconcileAsync(failHandle);
        Assert.False(failRes.IsOk);
        Assert.True(trackFollow.Disposed);
    }

    [Fact]
    public void TranscriptReducer_FiltersInternalAndSyntheticMessages_AndAppliesSessionTitle()
    {
        var scope = new SessionKey("rt-1", "web", "s-filter");
        var reducer = new TranscriptReducer(scope);

        // 1. 合成 user/message（source.kind != "user" 或纯 <system-reminder>）不应进入聊天气泡（F12）
        Assert.True(reducer.ApplyWireEvent(scope, new SessionWireEventView(
            "turn/start", 1, 100, JsonElement.Parse("""{"turn":1}"""))));
        Assert.True(reducer.ApplyWireEvent(scope, new SessionWireEventView(
            "user/message",
            2,
            101,
            JsonElement.Parse("""{"id":"syn-1","source":{"kind":"skill-catalog"},"content":[{"type":"text","text":"<system-reminder>internal</system-reminder>"}]}"""))));
        Assert.True(reducer.ApplyWireEvent(scope, new SessionWireEventView(
            "user/message",
            3,
            102,
            JsonElement.Parse("""{"id":"real-1","source":{"kind":"user"},"content":[{"type":"text","text":"真实提问"}]}"""))));

        // 2. 运行时内部事件不应产生 UnknownEventRow 或 NoticeRow 刷屏（F5 / F11）
        foreach (var (evType, idx) in new[]
        {
            ("agent/inbox/spliced", 4L),
            ("session/end-seed", 5L),
            ("session-log-deepseek/delivery-accepted", 6L),
            ("deliverables/presented", 7L),
            ("workspace/changes", 8L),
            ("job/registered", 9L),
            ("job/settled", 10L),
            ("request/context", 11L),
            ("llm/retry", 12L),
        })
        {
            Assert.True(reducer.ApplyWireEvent(scope, new SessionWireEventView(evType, idx, 102 + idx, JsonElement.Parse("{}"))));
        }

        // 3. session/title 同步更新快照标题（F13）
        Assert.True(reducer.ApplyWireEvent(scope, new SessionWireEventView(
            "session/title", 13, 120, JsonElement.Parse("""{"title":"排查启动链路"}"""))));

        var snap = reducer.DeriveSnapshot(0, 0, 0);
        Assert.Equal("排查启动链路", snap.SessionTitle);
        var userMsg = Assert.Single(snap.Rows.OfType<UserMessageRow>());
        Assert.Equal("真实提问", userMsg.Text);
        Assert.Empty(snap.Rows.OfType<UnknownEventRow>());
        Assert.Empty(snap.Rows.OfType<NoticeRow>());
    }

    private sealed class TrackableDisposable : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AsyncNoop : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
