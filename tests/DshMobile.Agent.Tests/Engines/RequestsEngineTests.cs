using System.Text.Json;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;
using Microsoft.Extensions.Time.Testing;

namespace DshMobile.Agent.Tests.Engines;

public sealed class RequestsEngineTests
{
    private sealed class FakeInteractionGateway : DefaultDshGateway
    {
        public int ApprovalReplyCount;
        public ApprovalDecision? LastApprovalDecision;
        public Func<string, string, ApprovalDecision, CancellationToken, Task<Outcome<Unit>>>? OnReplyApproval { get; set; }
        public int QuestionReplyCount;

        public override Task<Outcome<Unit>> ReplyApprovalAsync(
            string clientId, string eventId, ApprovalDecision decision, CancellationToken ct = default)
        {
            Interlocked.Increment(ref ApprovalReplyCount);
            LastApprovalDecision = decision;
            return OnReplyApproval is not null
                ? OnReplyApproval(clientId, eventId, decision, ct)
                : Task.FromResult(Outcome<Unit>.Ok(Unit.Value));
        }

        public override Task<Outcome<Unit>> ReplyOpenQuestionAsync(
            string clientId, string eventId, UserQuestionAnswerBatch answer, CancellationToken ct = default)
        {
            Interlocked.Increment(ref QuestionReplyCount);
            return Task.FromResult(Outcome<Unit>.Ok(Unit.Value));
        }
    }

    [Fact]
    public async Task Approval_BacktracksArgumentsExplicitlyMarksMissing_AndDeduplicatesConcurrentClicks()
    {
        var scope = new SessionKey("rt-1", "web", "s-req");
        var time = new FakeTimeProvider();
        var gw = new FakeInteractionGateway();
        await using var transcripts = new TranscriptEngine(time);
        var handle = transcripts.GetOrCreateSession(scope);

        handle.PostFollowUpdate(scope, new SessionFollowEventUpdate(new SessionWireEventView(
            "tool/call", 1, 100,
            JsonElement.Parse("""{"callId":"call-with-args","name":"bash","arguments":"{\"cmd\":\"echo Bearer tok12345\"}"}"""))));
        await handle.WaitForIdleDrainAsync();

        await using var engine = new RequestsEngine(gw, gw, transcripts, time, TimeSpan.FromMilliseconds(1500));

        // 1. Ingest with matching callId -> ArgumentsReady == true and secret redacted
        engine.IngestApprovalRequest(scope, "client-1", new ApprovalRequestView("ev-1", "s-req", "bash", "call-with-args"));
        // 2. Ingest with missing callId -> ArgumentsReady == false with explicit diagnostic
        engine.IngestApprovalRequest(scope, "client-1", new ApprovalRequestView("ev-2", "s-req", "fs_write", "call-unknown"));

        var snap = engine.Snapshot();
        var card1 = Assert.Single(snap, c => c.EventId == "ev-1");
        Assert.True(card1.ArgumentsReady);
        Assert.DoesNotContain("tok12345", card1.SanitizedArgumentsSummary!, StringComparison.Ordinal);

        var card2 = Assert.Single(snap, c => c.EventId == "ev-2");
        Assert.False(card2.ArgumentsReady);
        Assert.Equal("approval.arguments_unavailable", card2.DiagnosticKey);

        // Concurrent click from notification + UI card executes RPC at most ONCE!
        var tcs = new TaskCompletionSource<Outcome<Unit>>(TaskCreationOptions.RunContinuationsAsynchronously);
        gw.OnReplyApproval = (_, _, _, _) => tcs.Task;

        var taskA = engine.DecideApprovalImmediateAsync("ev-1", ApprovalDecision.AllowedOnce);
        var taskB = engine.DecideApprovalImmediateAsync("ev-1", ApprovalDecision.Rejected);
        Assert.Same(taskA, taskB);

        tcs.SetResult(Outcome<Unit>.Ok(Unit.Value));
        Assert.True((await taskA).IsOk);
        Assert.Equal(1, gw.ApprovalReplyCount);
        Assert.Equal(ApprovalDecision.AllowedOnce, gw.LastApprovalDecision);

        // Reconnect replay of already settled ev-1 must NOT resurrect card ("防诈尸")
        engine.IngestApprovalRequest(scope, "client-1", new ApprovalRequestView("ev-1", "s-req", "bash", "call-with-args"));
        Assert.DoesNotContain(engine.Snapshot(), c => c.EventId == "ev-1");
    }

    [Fact]
    public async Task Approval_HoldToUndo_SupportsUndoAndFlushHeldOnHostStop()
    {
        var scope = new SessionKey("rt-1", "web", "s-hold");
        var time = new FakeTimeProvider();
        var gw = new FakeInteractionGateway();
        await using var transcripts = new TranscriptEngine(time);
        await using var engine = new RequestsEngine(gw, gw, transcripts, time, TimeSpan.FromMilliseconds(1500));

        engine.IngestApprovalRequest(scope, "client-1", new ApprovalRequestView("ev-hold", "s-hold", "bash", "call-h"));
        Assert.True(engine.HoldApprovalDecision("ev-hold", ApprovalDecision.AllowedOnce));
        Assert.Equal(PendingRequestState.Held, Assert.Single(engine.Snapshot()).State);
        Assert.Equal(0, gw.ApprovalReplyCount);

        // Undo inside window returns to Waiting with zero RPC sent
        Assert.True(engine.UndoHold("ev-hold"));
        Assert.Equal(PendingRequestState.Waiting, Assert.Single(engine.Snapshot()).State);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(0, gw.ApprovalReplyCount);

        // Hold again and FlushHeldAsync (e.g. navigation away / HostStopping) dispatches immediately!
        Assert.True(engine.HoldApprovalDecision("ev-hold", ApprovalDecision.Rejected));
        await engine.FlushHeldAsync();
        Assert.Equal(1, gw.ApprovalReplyCount);
        Assert.Equal(ApprovalDecision.Rejected, gw.LastApprovalDecision);
        Assert.Empty(engine.Snapshot());
    }

    [Fact]
    public async Task Question_CancelledByServer_RemovesCardImmediatelyAndRejectsLateAnswer()
    {
        var scope = new SessionKey("rt-1", "web", "s-q");
        var gw = new FakeInteractionGateway();
        await using var transcripts = new TranscriptEngine();
        await using var engine = new RequestsEngine(gw, gw, transcripts);

        engine.IngestQuestionRequest(scope, "client-1", new UserQuestionRequestView(
            "ev-q1", "s-q", "call-q1",
            [new UserQuestionItemView("q1", "Proceed?", null, null, [new UserQuestionOptionView("Yes")], false)]));
        Assert.Single(engine.Snapshot());

        Assert.True(engine.CancelRequest("ev-q1"));
        Assert.Empty(engine.Snapshot());

        var lateOutcome = await engine.AnswerQuestionAsync(
            "ev-q1",
            new UserQuestionAnswerBatch([new UserQuestionAnswerItemView("q1", ["Yes"])]));
        Assert.False(lateOutcome.IsOk);
        Assert.Equal(DshFailureCode.StaleRequest, lateOutcome.Error!.Code);
        Assert.Equal(0, gw.QuestionReplyCount);
    }

    [Fact]
    public async Task Approval_NetworkFailureTransitionsToTransportLost_AndReconnectReplayRearmsCard()
    {
        var scope = new SessionKey("rt-1", "web", "s-neterr");
        var gw = new FakeInteractionGateway
        {
            OnReplyApproval = (_, _, _, _) => Task.FromException<Outcome<Unit>>(new HttpRequestException("socket reset")),
        };
        await using var transcripts = new TranscriptEngine();
        await using var engine = new RequestsEngine(gw, gw, transcripts);

        engine.IngestApprovalRequest(scope, "client-1", new ApprovalRequestView("ev-net", "s-neterr", "bash", "c-1"));
        var failed = await engine.DecideApprovalImmediateAsync("ev-net", ApprovalDecision.AllowedOnce);
        Assert.False(failed.IsOk);
        Assert.Equal(PendingRequestState.TransportLost, Assert.Single(engine.Snapshot()).State);

        // Server reconnect replay re-arms TransportLost card back to Waiting -> subsequent approval succeeds
        engine.IngestApprovalRequest(scope, "client-1", new ApprovalRequestView("ev-net", "s-neterr", "bash", "c-1"));
        Assert.Equal(PendingRequestState.Waiting, Assert.Single(engine.Snapshot()).State);

        gw.OnReplyApproval = (_, _, _, _) => Task.FromResult(Outcome<Unit>.Ok(Unit.Value));
        var recovered = await engine.DecideApprovalImmediateAsync("ev-net", ApprovalDecision.AllowedOnce);
        Assert.True(recovered.IsOk);
        Assert.Empty(engine.Snapshot());
    }

    [Fact]
    public async Task Approval_HoldUndoNearDeadline_OldTimerCallbackDoesNotSend()
    {
        var scope = new SessionKey("rt-1", "web", "s-race");
        var time = new FakeTimeProvider();
        var gw = new FakeInteractionGateway();
        await using var transcripts = new TranscriptEngine(time);
        await using var engine = new RequestsEngine(gw, gw, transcripts, time, TimeSpan.FromMilliseconds(1500));

        engine.IngestApprovalRequest(scope, "client-1", new ApprovalRequestView("ev-race", "s-race", "bash", "c-r"));
        Assert.True(engine.HoldApprovalDecision("ev-race", ApprovalDecision.AllowedOnce));

        // Advance right before expiry, undo, then advance past original expiry
        time.Advance(TimeSpan.FromMilliseconds(1400));
        Assert.True(engine.UndoHold("ev-race"));
        time.Advance(TimeSpan.FromMilliseconds(500));

        await Task.Yield();
        Assert.Equal(0, gw.ApprovalReplyCount);
        Assert.Equal(PendingRequestState.Waiting, Assert.Single(engine.Snapshot()).State);
    }

    [Fact]
    public async Task Approval_LateToolCall_BackfillsArgumentsReadyAndToolName()
    {
        var scope = new SessionKey("rt-1", "web", "s-backfill");
        var gw = new FakeInteractionGateway();
        await using var transcripts = new TranscriptEngine(coalesceWindow: TimeSpan.FromMilliseconds(5));
        await using var engine = new RequestsEngine(gw, gw, transcripts);

        // 先到 approval/request，此时 transcript 尚无 tool/call 记录
        engine.IngestApprovalRequest(scope, "client-1", new ApprovalRequestView("ev-bf", "s-backfill", "unknown", "call-bf-1"));
        var initial = Assert.Single(engine.Snapshot());
        Assert.False(initial.ArgumentsReady);
        Assert.Equal("unknown", initial.ToolName);

        // 随后到达 tool/call 事件应触发回调并回填审批卡片（F9）
        var handle = transcripts.GetOrCreateSession(scope);
        handle.PostFollowUpdate(scope, new SessionFollowEventUpdate(new SessionWireEventView(
            "tool/call",
            10,
            1000,
            JsonElement.Parse("""{"callId":"call-bf-1","name":"bash","arguments":"{\"command\":\"pwd\"}"}"""))));
        await handle.WaitForIdleDrainAsync();

        var updated = Assert.Single(engine.Snapshot());
        Assert.True(updated.ArgumentsReady);
        Assert.Equal("bash", updated.ToolName);
        Assert.Contains("pwd", updated.SanitizedArgumentsSummary, StringComparison.Ordinal);
        Assert.Null(updated.DiagnosticKey);
    }
}
