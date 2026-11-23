using System.Text.Json;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;
using DshMobile.Agent.Presentation;
using DshMobile.Agent.Tests.Quality;

namespace DshMobile.Agent.Tests.Presentation;

public sealed class InboxAndNotificationTests
{
    private sealed class FakeGateway : DefaultDshGateway
    {
        public int ApprovalReplyCount;
        public ApprovalDecision? LastDecision;
        public Func<string, string, ApprovalDecision, CancellationToken, Task<Outcome<Unit>>>? OnReplyApproval { get; set; }

        public override Task<Outcome<Unit>> ReplyApprovalAsync(
            string clientId,
            string eventId,
            ApprovalDecision decision,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref ApprovalReplyCount);
            LastDecision = decision;
            return OnReplyApproval is not null
                ? OnReplyApproval(clientId, eventId, decision, ct)
                : Task.FromResult(Outcome<Unit>.Ok(Unit.Value));
        }
    }

    private sealed class FakeNotificationSurface : IBackgroundRequestNotificationSurface
    {
        public bool Enabled { get; set; } = true;
        public List<AgentRequestNotificationPayload> Shown { get; } = [];
        public List<(string EventId, int NotificationId)> CancelledEvents { get; } = [];
        public int CancelAllCount { get; private set; }

        public bool CanShow() => Enabled;

        public void ShowOrUpdate(AgentRequestNotificationPayload payload) => Shown.Add(payload);

        public void CancelForEvent(string eventId, int notificationId) => CancelledEvents.Add((eventId, notificationId));

        public void CancelAll() => CancelAllCount++;
    }

    [Fact]
    public async Task InboxViewModel_AggregatesCrossSessionApprovalsAndQuestions_AndResolvesTitles()
    {
        var gw = new FakeGateway();
        await using var transcripts = new TranscriptEngine();
        await using var requests = new RequestsEngine(gw, gw, transcripts);

        var titles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sess-alpha"] = "重构架构",
        };
        await using var inbox = new InboxViewModel(requests, sid => titles.TryGetValue(sid, out var t) ? t : null);

        Assert.Equal(0, inbox.State.TotalCount);

        var scopeA = new SessionKey("local", "web", "sess-alpha");
        var scopeB = new SessionKey("local", "web", "sess-beta");
        requests.IngestApprovalRequest(scopeA, "c-1", new ApprovalRequestView("ev-a1", "sess-alpha", "bash", "call-a1"));
        requests.IngestQuestionRequest(scopeB, "c-1", new UserQuestionRequestView(
            "ev-b1",
            "sess-beta",
            "call-b1",
            [new UserQuestionItemView("q1", "确认继续部署？", null, "部署确认", [new UserQuestionOptionView("是")], false)]));

        Assert.Equal(2, inbox.State.TotalCount);
        Assert.Equal(1, inbox.State.ApprovalCount);
        Assert.Equal(1, inbox.State.QuestionCount);

        var itemA = Assert.Single(inbox.State.Items, i => i.EventId == "ev-a1");
        Assert.Equal("重构架构", itemA.SessionTitle);
        Assert.Equal("bash", itemA.Headline);
        Assert.False(itemA.ArgumentsReady);

        var itemB = Assert.Single(inbox.State.Items, i => i.EventId == "ev-b1");
        Assert.Equal("sess-beta", itemB.SessionTitle);
        Assert.Equal("部署确认", itemB.Headline);

        titles["sess-beta"] = "上线检查";
        inbox.RefreshTitles();
        Assert.Equal("上线检查", Assert.Single(inbox.State.Items, i => i.EventId == "ev-b1").SessionTitle);

        requests.CancelRequest("ev-a1");
        Assert.Equal(1, inbox.State.TotalCount);
        Assert.Equal(0, inbox.State.ApprovalCount);
    }

    [Fact]
    public async Task BackgroundNotificationCoordinator_OnlyNotifiesInBackground_RedactsSecrets_AndClaimsAtomically()
    {
        var gw = new FakeGateway();
        await using var transcripts = new TranscriptEngine(coalesceWindow: TimeSpan.FromMilliseconds(5));
        await using var requests = new RequestsEngine(gw, gw, transcripts);
        var surface = new FakeNotificationSurface();
        await using var coord = new BackgroundRequestNotificationCoordinator(requests, surface, sid => $"会话-{sid}");

        var scope = new SessionKey("local", "web", "s-bg");
        var handle = transcripts.GetOrCreateSession(scope);
        handle.PostFollowUpdate(scope, new SessionFollowEventUpdate(new SessionWireEventView(
            "tool/call",
            1,
            100,
            JsonElement.Parse("""{"callId":"c-sec","name":"bash","arguments":"{\"cmd\":\"curl -H 'Authorization: Bearer sk-supersecret123456'\"}"}"""))));
        await handle.WaitForIdleDrainAsync();

        // 1. 前台期间绝不发通知
        coord.SetAppInBackground(false);
        requests.IngestApprovalRequest(scope, "c-1", new ApprovalRequestView("ev-fg", "s-bg", "bash", "c-missing"));
        Assert.Empty(surface.Shown);
        requests.CancelRequest("ev-fg");

        // 2. 切入后台后收到请求 -> 发通知且必须脱敏，绝不含原始 sk- 密钥
        coord.SetAppInBackground(true);
        requests.IngestApprovalRequest(scope, "c-1", new ApprovalRequestView("ev-bg", "s-bg", "bash", "c-sec"));
        var notif = Assert.Single(surface.Shown);
        Assert.False(notif.IsUpdate);
        Assert.DoesNotContain("sk-supersecret123456", notif.SanitizedSummary, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", notif.SanitizedSummary, StringComparison.Ordinal);

        // 3. 错误随机数或重放拒绝放行
        Assert.False(coord.TryClaimNotificationAction("ev-bg", ApprovalDecision.AllowedOnce, "wrong-nonce", out _));
        Assert.Equal(0, gw.ApprovalReplyCount);

        // 4. 后台通知快捷操作与页内卡片并发认领 -> 仅生效一次
        var tcs = new TaskCompletionSource<Outcome<Unit>>(TaskCreationOptions.RunContinuationsAsynchronously);
        gw.OnReplyApproval = (_, _, _, _) => tcs.Task;

        Assert.True(coord.TryClaimNotificationAction("ev-bg", ApprovalDecision.Rejected, notif.OneTimeNonce, out var notifTask));
        var cardTask = requests.DecideApprovalImmediateAsync("ev-bg", ApprovalDecision.AllowedOnce);
        Assert.Same(notifTask, cardTask);

        // 同一 nonce 不可重放
        Assert.False(coord.TryClaimNotificationAction("ev-bg", ApprovalDecision.AllowedOnce, notif.OneTimeNonce, out _));

        tcs.SetResult(Outcome<Unit>.Ok(Unit.Value));
        Assert.True((await notifTask).IsOk);
        Assert.Equal(1, gw.ApprovalReplyCount);
        Assert.Equal(ApprovalDecision.Rejected, gw.LastDecision);
        Assert.Empty(requests.Snapshot());
    }

    [Fact]
    public async Task BackgroundNotificationCoordinator_UpdatesOnLateToolCall_AndClearsOnForeground()
    {
        var gw = new FakeGateway();
        await using var transcripts = new TranscriptEngine(coalesceWindow: TimeSpan.FromMilliseconds(5));
        await using var requests = new RequestsEngine(gw, gw, transcripts);
        var surface = new FakeNotificationSurface();
        await using var coord = new BackgroundRequestNotificationCoordinator(requests, surface);

        coord.SetAppInBackground(true);
        var scope = new SessionKey("local", "web", "s-late");
        requests.IngestApprovalRequest(scope, "c-1", new ApprovalRequestView("ev-late", "s-late", "unknown", "c-late"));
        var first = Assert.Single(surface.Shown);
        Assert.False(first.IsUpdate);

        var handle = transcripts.GetOrCreateSession(scope);
        handle.PostFollowUpdate(scope, new SessionFollowEventUpdate(new SessionWireEventView(
            "tool/call",
            1,
            100,
            JsonElement.Parse("""{"callId":"c-late","name":"fs_write","arguments":"{\"path\":\"src/app.cs\"}"}"""))));
        await handle.WaitForIdleDrainAsync();

        Assert.Equal(2, surface.Shown.Count);
        var updated = surface.Shown[1];
        Assert.True(updated.IsUpdate);
        Assert.Equal("fs_write", updated.SanitizedToolOrTopic);
        Assert.Equal(first.OneTimeNonce, updated.OneTimeNonce);

        // 切回前台清理全部通知并吊销 nonce
        coord.SetAppInBackground(false);
        Assert.True(surface.CancelAllCount >= 1);
        Assert.False(coord.TryClaimNotificationAction("ev-late", ApprovalDecision.AllowedOnce, first.OneTimeNonce, out _));
    }

    [Theory]
    [InlineData("""{"file_path":"/root/projects/src/Main.cs"}""", "src/Main.cs")]
    [InlineData("""{"path":"README.md"}""", "README.md")]
    [InlineData("""{"args":{"target_file":"/root/projects/docs/a.md"}}""", "docs/a.md")]
    [InlineData("""{"file_path":"/etc/passwd"}""", null)]
    [InlineData("""{"file_path":"/root/projects/.dsh/credentials.yaml"}""", null)]
    [InlineData("""{"path":".dsh/settings.json"}""", null)]
    [InlineData("""{"path":"../outside.txt"}""", null)]
    [InlineData("""{"file_path":"/root/projects"}""", null)]
    [InlineData("not-json", null)]
    public void WorkspacePathExtractor_ValidatesWorkspaceBoundsAndRejectsDotDsh(string argsJson, string? expected)
    {
        var actual = WorkspacePathExtractor.TryExtractWorkspaceFilePath(argsJson);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void AgentNotificationReceiver_IsNotExported_UsesImmutablePendingIntents_AndIsIsolatedFromRelay()
    {
        var root = RepoLocator.FindRepoRoot();
        var notifySrc = File.ReadAllText(Path.Combine(root, "src/DshMobile.Android/Notifications/AgentRequestNotifications.cs"));
        var codeOnly = string.Join('\n', notifySrc.Split('\n').Select(line =>
        {
            var idx = line.IndexOf("//", StringComparison.Ordinal);
            return idx >= 0 ? line[..idx] : line;
        }));

        Assert.Contains("Exported = false", codeOnly, StringComparison.Ordinal);
        Assert.Contains("PendingIntentFlags.Immutable", codeOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("PendingIntentFlags.Mutable", codeOnly, StringComparison.Ordinal);
        Assert.Contains("\"dshmobile_agent_requests\"", codeOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("dshmobile_relay_approval", codeOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("RelayApprovalReceiver", codeOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("InterlockQueue", codeOnly, StringComparison.Ordinal);
    }
}
