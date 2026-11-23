using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;
using DshMobile.Agent.Presentation;
using DshMobile.Agent.Rendering;

namespace DshMobile.Agent.Tests.Presentation;

public sealed class ChatViewModelAndMarkdownTests
{
    [Fact]
    public async Task MarkdownRenderCache_ParsesBlocksAndRejectsStaleRevision()
    {
        var cache = new MarkdownRenderCache();
        const string md = """
            # 标题

            包含 **加粗**、*斜体*、`内联代码` 和 [链接](https://example.com)。

            ```diff
            @@ -1,2 +1,2 @@
            -old
            +new
            ```

            | A | B |
            | - | - |
            | 1 | 2 |
            """;

        var parsed = await cache.ParseAsync("row-1", revision: 2, md);
        Assert.NotNull(parsed);
        Assert.Contains(parsed.Segments, s => s is MarkdownTextSegment { Kind: MarkdownSegmentKind.Heading, HeadingLevel: 1 });
        Assert.Contains(parsed.Segments, s => s is MarkdownDiffSegment d && d.Lines.Count == 3);
        Assert.Contains(parsed.Segments, s => s is MarkdownTableSegment t && t.Headers.Count == 2 && t.Rows.Count == 1);

        // Older revision for the same itemId must be ignored so stale background tasks never overwrite newer blocks.
        var stale = await cache.ParseAsync("row-1", revision: 1, "stale text");
        Assert.Null(stale);
        Assert.True(cache.IsStale("row-1", 1));
        Assert.False(cache.IsStale("row-1", 2));
    }

    [Fact]
    public async Task ChatViewModel_ConnectsReconcilesSendsAndHandlesApprovalHold()
    {
        var gw = new StubChatGateway();
        await using var transcripts = new TranscriptEngine(TimeProvider.System, TimeSpan.FromMilliseconds(5));
        await using var requests = new RequestsEngine(gw, gw, transcripts);
        var sends = new SendStateMachine(gw, gw);
        var reconciler = new HistoryReconciler(gw, gw);
        await using var vm = new ChatViewModel(gw, transcripts, requests, sends, reconciler);

        var observed = new List<ChatUiState>();
        vm.StateChanged += s => observed.Add(s);
        vm.SetUiVisible(true);

        await vm.ConnectAndAttachAsync();
        Assert.True(vm.State.IsConnected);
        Assert.Equal("sess-1", vm.State.ActiveSession.SessionId);
        Assert.Equal("deepseek/deepseek-chat", vm.State.ModelLabel);
        Assert.NotEmpty(vm.State.SlashCommands);

        await vm.SendTextAsync("帮我检查启动性能");
        Assert.Single(gw.SentPrompts);
        Assert.Equal("帮我检查启动性能", gw.SentPrompts[0].Text);

        gw.EmitForwarded(new ApprovalGatewayEvent(new ApprovalRequestView("ev-1", "sess-1", "bash", "call-1")));
        Assert.Single(vm.State.PendingRequests);
        Assert.Equal(PendingRequestState.Waiting, vm.State.PendingRequests[0].State);

        await vm.AnswerApprovalAsync("ev-1", ApprovalDecision.AllowedOnce);
        Assert.Equal(PendingRequestState.Held, vm.State.PendingRequests[0].State);

        await vm.UndoApprovalAsync("ev-1");
        Assert.Equal(PendingRequestState.Waiting, vm.State.PendingRequests[0].State);

        vm.SetUiVisible(false);
        await vm.NotifyHostStoppingAsync();
        Assert.False(vm.State.IsConnected);
    }

    [Fact]
    public async Task ChatViewModel_MapsSessionError_NormalizesSlashPrefix_AndCancelsWithdrawnRequest()
    {
        var gw = new StubChatGateway();
        await using var transcripts = new TranscriptEngine(coalesceWindow: TimeSpan.FromMilliseconds(5));
        await using var requests = new RequestsEngine(gw, gw, transcripts);
        var sends = new SendStateMachine(gw, gw);
        var reconciler = new HistoryReconciler(gw, gw);
        await using var vm = new ChatViewModel(gw, transcripts, requests, sends, reconciler);

        await vm.ConnectAndAttachAsync("sess-1");

        // F3: api-session/error 透传凭据缺失分类码而非 InvalidState
        gw.EmitForwarded(new SessionErrorGatewayEvent("sess-1", "MISSING_CREDENTIAL: no api key configured"));
        Assert.Equal("session_error.credentials_missing", vm.State.FailureCode);

        // F9 cancel: 网关撤回请求立即移除待审批卡片
        gw.EmitForwarded(new ApprovalGatewayEvent(new ApprovalRequestView("ev-cancel-1", "sess-1", "bash", "c-1")));
        Assert.Single(vm.State.PendingRequests);
        gw.EmitForwarded(new ForwardedEventCancelledGatewayEvent("ev-cancel-1"));
        Assert.Empty(vm.State.PendingRequests);

        // F10: 无前导 '/' 的命令名自动补齐 '/' 后下发执行
        await vm.RunSlashCommandAsync("compact");
        Assert.Equal("/compact", Assert.Single(gw.ExecutedCommands));
    }

    private sealed class StubChatGateway : DefaultDshGateway
    {
        public List<PromptSendRequest> SentPrompts { get; } = [];
        public List<string> ExecutedCommands { get; } = [];
        public override event Action<ForwardedGatewayEvent>? ForwardedEvent;

        public override DshCapabilities Capabilities { get; } = new(
            Prompt: true,
            PromptImages: true,
            SlashCommands: true,
            StreamingEvents: true,
            Approvals: true,
            UserQuestions: true,
            ResumeKnownSession: true,
            ListSessions: true,
            HistoryReplay: true,
            CancelSession: true,
            SwitchModelInSession: true,
            ManageCredentials: true,
            ServerPromptIdempotency: true,
            PluginInventory: true,
            PluginMutation: false,
            GitOperations: false);

        public void EmitForwarded(ForwardedGatewayEvent evt) => ForwardedEvent?.Invoke(evt);

        public override Task<Outcome<DshCapabilities>> ProbeCapabilitiesAsync(CancellationToken ct = default) =>
            Task.FromResult(Outcome<DshCapabilities>.Ok(Capabilities));

        public override Task<Outcome<IReadOnlyList<SessionSummaryView>>> ListSessionsAsync(CancellationToken ct = default) =>
            Task.FromResult(Outcome<IReadOnlyList<SessionSummaryView>>.Ok([
                new SessionSummaryView("sess-1", 1000, false, false, true),
            ]));

        public override Task<Outcome<string>> CreateOrResumeSessionAsync(CreateOrResumeSessionRequest request, CancellationToken ct = default) =>
            Task.FromResult(Outcome<string>.Ok(request.SessionId ?? "sess-1"));

        public override Task<Outcome<ModelCatalogView>> GetModelCatalogAsync(CancellationToken ct = default) =>
            Task.FromResult(Outcome<ModelCatalogView>.Ok(new ModelCatalogView(
                new ModelSelectionSpec("deepseek", "deepseek-chat"),
                ["deepseek"],
                [],
                [])));

        public override Task<Outcome<IReadOnlyList<SlashCommandDescriptorView>>> ListCommandsAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(Outcome<IReadOnlyList<SlashCommandDescriptorView>>.Ok([
                new SlashCommandDescriptorView("/compact", "压缩会话"),
            ]));

        public override Task<Outcome<SessionPageView>> LoadSessionEventsAsync(string sessionId, long? throughSeq = null, long? beforeSeq = null, int? maxMessages = null, CancellationToken ct = default) =>
            Task.FromResult(Outcome<SessionPageView>.Ok(new SessionPageView([], false, 0)));

        public override Task<Outcome<IAsyncDisposable>> FollowSessionAsync(string sessionId, Func<SessionFollowUpdate, ValueTask> onUpdate, int? maxMessages = null, bool activate = true, CancellationToken ct = default) =>
            Task.FromResult(Outcome<IAsyncDisposable>.Ok(new NoopAsyncDisposable()));

        public override Task<Outcome<Unit>> SendPromptAsync(PromptSendRequest request, CancellationToken ct = default)
        {
            SentPrompts.Add(request);
            return Task.FromResult(Outcome<Unit>.Ok(Unit.Value));
        }

        public override Task<Outcome<SlashCommandExecutionView?>> ExecuteCommandAsync(
            string sessionId,
            string commandLine,
            IReadOnlyList<PromptAttachmentImage>? images = null,
            CancellationToken ct = default)
        {
            ExecutedCommands.Add(commandLine);
            return Task.FromResult(Outcome<SlashCommandExecutionView?>.Ok(
                new SlashCommandExecutionView("cmd-1", true, "ok")));
        }

        private sealed class NoopAsyncDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
