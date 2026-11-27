using System.Text.Json;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;
using DshMobile.Agent.Presentation;

namespace DshMobile.Agent.Tests.Engines;

/// <summary>
/// 覆盖归档/取消归档、真实本地缓存删除、本机永久隐藏存储、TranscriptEngine 卸载及 ViewModel 联动的端到端单测。
/// </summary>
public sealed class SessionArchiveAndLocalStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"dshm-arch-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task KeptTranscriptStore_DeletesByKeyAndBySessionIdSafely()
    {
        var store = new KeptTranscriptStore(_tempDir);
        var keyA1 = new SessionKey("local", "web", "sess-a");
        var keyA2 = new SessionKey("remote-1", "web", "sess-a");
        var keyB = new SessionKey("local", "web", "sess-b");

        await SaveSampleAsync(store, keyA1, "会话 A1");
        await SaveSampleAsync(store, keyA2, "会话 A2");
        await SaveSampleAsync(store, keyB, "会话 B");

        Assert.True(await store.DeleteAsync(keyB));
        Assert.False(await store.DeleteAsync(keyB));
        Assert.Null(await store.TryLoadAsync(keyB));

        var deletedCount = await store.DeleteBySessionIdAsync("sess-a");
        Assert.Equal(2, deletedCount);
        Assert.Equal(0, await store.DeleteBySessionIdAsync("sess-a"));
        Assert.Empty(await store.ListRecentAsync());
    }

    [Fact]
    public async Task HiddenSessionStore_PersistsAtomically_ToleratesCorruptFile_AndIsThreadSafe()
    {
        var storePath = Path.Combine(_tempDir, HiddenSessionStore.DefaultFileName);

        // 1. 文件不存在时容错
        var store = new HiddenSessionStore(_tempDir);
        Assert.Equal(storePath, store.FilePath);
        await store.LoadAsync();
        Assert.Empty(store.GetHiddenIdsSnapshot());

        // 2. 并发 HideAsync 线程安全且重启可恢复
        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => store.HideAsync($"sess-{i}")));
        Assert.Equal(16, store.GetHiddenIdsSnapshot().Count);
        Assert.True(store.IsHidden("sess-5"));
        Assert.Empty(Directory.EnumerateFiles(_tempDir, "*.tmp"));

        var reloaded = new HiddenSessionStore(_tempDir);
        await reloaded.LoadAsync();
        Assert.Equal(16, reloaded.GetHiddenIdsSnapshot().Count);
        Assert.True(reloaded.IsHidden("sess-15"));

        // 3. UnhideAsync 落盘生效
        Assert.True(await reloaded.UnhideAsync("sess-5"));
        Assert.False(await reloaded.UnhideAsync("sess-5"));
        var reloadedAfterUnhide = new HiddenSessionStore(_tempDir);
        await reloadedAfterUnhide.LoadAsync();
        Assert.False(reloadedAfterUnhide.IsHidden("sess-5"));
        Assert.Equal(15, reloadedAfterUnhide.GetHiddenIdsSnapshot().Count);

        // 4. 文件损坏时安静容忍不抛异常
        var corruptDir = Path.Combine(_tempDir, "corrupt");
        Directory.CreateDirectory(corruptDir);
        await File.WriteAllTextAsync(Path.Combine(corruptDir, HiddenSessionStore.DefaultFileName), "{not valid json!!!");
        var corruptStore = new HiddenSessionStore(corruptDir);
        await corruptStore.LoadAsync();
        Assert.Empty(corruptStore.GetHiddenIdsSnapshot());
    }

    [Fact]
    public async Task TranscriptEngine_RemovesSessionHandlesAndReleasesResources()
    {
        await using var engine = new TranscriptEngine(coalesceWindow: TimeSpan.FromMilliseconds(5));
        var k1 = new SessionKey("local", "web", "sess-1");
        var k2 = new SessionKey("remote", "web", "sess-1");
        var k3 = new SessionKey("local", "web", "sess-2");

        _ = engine.GetOrCreateSession(k1);
        _ = engine.GetOrCreateSession(k2);
        _ = engine.GetOrCreateSession(k3);

        Assert.True(await engine.RemoveSessionAsync(k3));
        Assert.False(await engine.RemoveSessionAsync(k3));
        Assert.False(engine.TryGetSession(k3, out _));

        Assert.Equal(2, await engine.RemoveSessionsByIdAsync("sess-1"));
        Assert.False(engine.TryGetSession(k1, out _));
        Assert.False(engine.TryGetSession(k2, out _));
    }

    [Fact]
    public async Task SessionsViewModel_ArchiveUnarchiveFilterAndOnlineOfflineDelete_WorkEndToEnd()
    {
        var keptStore = new KeptTranscriptStore(_tempDir);
        var hiddenStore = new HiddenSessionStore(_tempDir);
        await hiddenStore.HideAsync("sess-hidden");
        await SaveSampleAsync(keptStore, new SessionKey("local", "web", "sess-idle"), "空闲会话");
        await SaveSampleAsync(keptStore, new SessionKey("local", "web", "sess-active"), "运行中会话");
        await SaveSampleAsync(keptStore, new SessionKey("local", "web", "sess-offline-only"), "仅本地缓存会话");

        var gw = new StubArchiveGateway();
        await using var transcripts = new TranscriptEngine(coalesceWindow: TimeSpan.FromMilliseconds(5));
        _ = transcripts.GetOrCreateSession(new SessionKey("local", "web", "sess-idle"));
        await using var requests = new RequestsEngine(gw, gw, transcripts);
        await using var vm = new SessionsViewModel(gw, requests, keptStore, hiddenStore, transcripts);

        var removedEvents = new List<string>();
        vm.SessionArchivedOrRemoved += id => removedEvents.Add(id);

        // 1. 离线模式：删除缓存会话仅删本地文件，不调远端
        await vm.LoadCachedSessionsAsync();
        Assert.Equal(3, vm.State.EffectiveCachedSessions.Count);
        var offlineDelete = await vm.DeleteSessionAsync("sess-offline-only");
        Assert.True(offlineDelete.IsOk);
        Assert.Equal(0, gw.ArchiveCallCount);
        Assert.DoesNotContain(vm.State.EffectiveCachedSessions, s => s.SessionId == "sess-offline-only");
        Assert.Null(await keptStore.TryLoadAsync(new SessionKey("local", "web", "sess-offline-only")));
        Assert.False(hiddenStore.IsHidden("sess-offline-only"));

        // 2. 在线刷新：拉取归档基线并按 All / Running / Archived 筛选
        await vm.RefreshAsync();
        Assert.True(vm.State.CanArchiveSessions);
        Assert.Equal(1, vm.State.ArchivedCount);
        Assert.Equal(2, vm.State.Sessions.Count); // 默认 All 排除已归档的 sess-pre-archived
        Assert.DoesNotContain(vm.State.Sessions, s => s.SessionId == "sess-pre-archived");

        vm.SetFilterMode(SessionFilterMode.Running);
        Assert.Equal("sess-active", Assert.Single(vm.State.Sessions).SessionId);

        vm.SetFilterMode(SessionFilterMode.Archived);
        var archivedItem = Assert.Single(vm.State.Sessions);
        Assert.Equal("sess-pre-archived", archivedItem.SessionId);
        Assert.True(archivedItem.IsArchived);

        // 3. 取消归档
        var unarch = await vm.UnarchiveSessionAsync("sess-pre-archived");
        Assert.True(unarch.IsOk);
        Assert.Equal(0, vm.State.ArchivedCount);
        Assert.Empty(vm.State.Sessions);

        vm.SetFilterMode(SessionFilterMode.All);
        Assert.Equal(3, vm.State.Sessions.Count);

        // 4. 归档空闲会话：清理本地缓存与 TranscriptEngine handle，触发联动事件
        var archIdle = await vm.ArchiveSessionAsync("sess-idle");
        Assert.True(archIdle.IsOk);
        Assert.Equal(1, vm.State.ArchivedCount);
        Assert.Null(await keptStore.TryLoadAsync(new SessionKey("local", "web", "sess-idle")));
        Assert.False(transcripts.TryGetSession(new SessionKey("local", "web", "sess-idle"), out _));
        Assert.Contains("sess-idle", removedEvents);
        Assert.Equal("sess-idle", vm.State.LastRemovedSessionId);

        // 5. 删除运行中会话：未传 stopActivity 透传 SessionActive，不删缓存不隐藏
        var activeDeleteFail = await vm.DeleteSessionAsync("sess-active", stopActivity: false);
        Assert.False(activeDeleteFail.IsOk);
        Assert.Equal(DshFailureCode.SessionActive, activeDeleteFail.Error!.Code);
        Assert.False(hiddenStore.IsHidden("sess-active"));
        Assert.NotNull(await keptStore.TryLoadAsync(new SessionKey("local", "web", "sess-active")));

        // 6. 用户确认停活后重试删除：成功归档、删缓存、写永久隐藏，刷新后不会复活
        var activeDeleteOk = await vm.DeleteSessionAsync("sess-active", stopActivity: true);
        Assert.True(activeDeleteOk.IsOk);
        Assert.True(hiddenStore.IsHidden("sess-active"));
        Assert.Null(await keptStore.TryLoadAsync(new SessionKey("local", "web", "sess-active")));
        Assert.DoesNotContain(vm.State.Sessions, s => s.SessionId == "sess-active");

        await vm.RefreshAsync();
        Assert.DoesNotContain(vm.State.Sessions, s => s.SessionId == "sess-active");
        vm.SetFilterMode(SessionFilterMode.Archived);
        Assert.DoesNotContain(vm.State.Sessions, s => s.SessionId == "sess-active");

        // 7. 增量 workspace/follow 帧实时同步归档状态
        await gw.EmitWorkspaceFrameAsync(new WorkspaceArchivedChangedFrame(["sess-idle", "sess-pre-archived"]));
        Assert.Equal(2, vm.State.ArchivedCount);
    }

    [Fact]
    public async Task ChatViewModel_SkipsHiddenAndArchived_HandlesArchiveDeleteAndSessionRemovedEvent()
    {
        var keptStore = new KeptTranscriptStore(_tempDir);
        var hiddenStore = new HiddenSessionStore(_tempDir);
        await SaveSampleAsync(keptStore, new SessionKey("local", "web", "sess-hidden"), "已隐藏的最近会话", savedAtUnixMs: 9000);
        await SaveSampleAsync(keptStore, new SessionKey("local", "web", "sess-idle"), "可见缓存会话", savedAtUnixMs: 5000);
        await hiddenStore.HideAsync("sess-hidden");

        var gw = new StubArchiveGateway();
        await using var transcripts = new TranscriptEngine(coalesceWindow: TimeSpan.FromMilliseconds(5));
        await using var requests = new RequestsEngine(gw, gw, transcripts);
        var sends = new SendStateMachine(gw, gw);
        var reconciler = new HistoryReconciler(gw, gw);
        await using var chatVm = new ChatViewModel(gw, transcripts, requests, sends, reconciler, keptStore, hiddenStore: hiddenStore);

        // 1. 冷启缓存加载必须跳过已隐藏会话
        var loaded = await chatVm.LoadMostRecentCachedSessionAsync();
        Assert.True(loaded);
        Assert.Equal("sess-idle", chatVm.State.ActiveSession.SessionId);

        // 2. 自动连接跳过已归档的 sess-pre-archived（即使 updatedAt 更高）与已隐藏会话
        gw.BumpSessionUpdatedAt("sess-pre-archived", 999_999);
        await chatVm.ConnectAndAttachAsync();
        Assert.True(chatVm.State.IsConnected);
        Assert.Equal("sess-idle", chatVm.State.ActiveSession.SessionId);
        Assert.False(chatVm.State.IsArchived);

        // 3. 当前会话归档与取消归档
        var archived = await chatVm.ArchiveCurrentSessionAsync();
        Assert.True(archived.IsOk);
        Assert.True(chatVm.State.IsArchived);
        Assert.Null(await keptStore.TryLoadAsync(new SessionKey("local", "web", "sess-idle")));

        var unarchived = await chatVm.UnarchiveCurrentSessionAsync();
        Assert.True(unarchived.IsOk);
        Assert.False(chatVm.State.IsArchived);

        // 4. 收到远端 SessionRemovedGatewayEvent 时标记状态并释放流跟随
        gw.EmitForwarded(new SessionRemovedGatewayEvent("sess-idle"));
        Assert.Equal("chat.status.session_removed", chatVm.State.StatusKey);
        Assert.Equal(DshFailureCode.SessionNotFound.ToString(), chatVm.State.FailureCode);
        Assert.True(gw.FollowDisposedCount > 0);

        // 5. 切到运行中会话，验证 DeleteCurrentSessionAsync 透传 SessionActive 后停活删除
        await chatVm.ConnectAndAttachAsync("sess-active");
        Assert.Equal("sess-active", chatVm.State.ActiveSession.SessionId);

        var delFail = await chatVm.DeleteCurrentSessionAsync(stopActivity: false);
        Assert.False(delFail.IsOk);
        Assert.Equal(DshFailureCode.SessionActive, delFail.Error!.Code);

        var delOk = await chatVm.DeleteCurrentSessionAsync(stopActivity: true);
        Assert.True(delOk.IsOk);
        Assert.Equal("pending", chatVm.State.ActiveSession.SessionId);
        Assert.True(hiddenStore.IsHidden("sess-active"));
    }

    [Fact]
    public async Task ChatViewModel_SurvivesSetUiVisibleAndReopen_AfterSessionsViewModelArchivesActiveSession()
    {
        var keptStore = new KeptTranscriptStore(_tempDir);
        var hiddenStore = new HiddenSessionStore(_tempDir);
        var gw = new StubArchiveGateway();
        await using var transcripts = new TranscriptEngine(coalesceWindow: TimeSpan.FromMilliseconds(5));
        await using var requests = new RequestsEngine(gw, gw, transcripts);
        var sends = new SendStateMachine(gw, gw);
        var reconciler = new HistoryReconciler(gw, gw);
        await using var sessionsVm = new SessionsViewModel(gw, requests, keptStore, hiddenStore, transcripts);
        await using var chatVm = new ChatViewModel(gw, transcripts, requests, sends, reconciler, keptStore, hiddenStore: hiddenStore);

        // 1. 模拟打开会话进入聊天页（持有 TranscriptHandle 且订阅可见态），随后返回会话列表（SetUiVisible(false)）。
        chatVm.SetUiVisible(true);
        await chatVm.ConnectAndAttachAsync("sess-idle");
        Assert.Equal("sess-idle", chatVm.State.ActiveSession.SessionId);
        Assert.False(chatVm.State.IsArchived);
        chatVm.SetUiVisible(false);

        // 2. 模拟在会话列表页归档该会话（卸载并释放内存 TranscriptHandle）。
        await sessionsVm.RefreshAsync();
        var archOutcome = await sessionsVm.ArchiveSessionAsync("sess-idle");
        Assert.True(archOutcome.IsOk);
        Assert.False(transcripts.TryGetSession(new SessionKey("local", "web", "sess-idle"), out _));

        // 3. 模拟再次从「已归档」列表点开该会话：ChatFragment.OnStart 先 SetUiVisible(true)，随后 ConnectAndAttachAsync；
        //    绝不可向已释放的 CoalescingPublisher 抛出 ObjectDisposedException。
        chatVm.SetUiVisible(true);
        await chatVm.ConnectAndAttachAsync("sess-idle");
        Assert.True(chatVm.State.IsConnected);
        Assert.Equal("sess-idle", chatVm.State.ActiveSession.SessionId);
        Assert.True(chatVm.State.IsArchived);
    }

    private static Task SaveSampleAsync(KeptTranscriptStore store, SessionKey scope, string title, long savedAtUnixMs = 0)
    {
        _ = savedAtUnixMs;
        return store.SaveAsync(new ChatSnapshot(
            Revision: 1,
            Session: scope,
            CursorSeq: 2,
            Phase: RunPhase.Completed,
            IsDetachedCache: false,
            IsPartialHistory: false,
            FailureCode: null,
            Rows: [new UserMessageRow("msg:u1", 1, "u1", $"内容-{title}", 0, false)],
            Counters: TranscriptCounters.Zero,
            SessionTitle: title));
    }

    private sealed class StubArchiveGateway : DefaultDshGateway
    {
        private readonly List<SessionSummaryView> _sessions =
        [
            new("sess-active", 3000, Running: true, Blank: false, AgentAvailable: true, Title: "运行中会话"),
            new("sess-idle", 2000, Running: false, Blank: false, AgentAvailable: true, Title: "空闲会话"),
            new("sess-pre-archived", 1000, Running: false, Blank: false, AgentAvailable: true, Title: "预归档会话"),
            new("sess-hidden", 4000, Running: false, Blank: false, AgentAvailable: true, Title: "隐藏会话"),
        ];

        private readonly HashSet<string> _archived = new(StringComparer.Ordinal) { "sess-pre-archived" };
        private Func<WorkspaceFollowFrame, ValueTask>? _workspaceFrameCallback;

        public int ArchiveCallCount { get; private set; }
        public int FollowDisposedCount { get; private set; }
        public override event Action<ForwardedGatewayEvent>? ForwardedEvent;

        public void EmitForwarded(ForwardedGatewayEvent evt) => ForwardedEvent?.Invoke(evt);

        public void BumpSessionUpdatedAt(string sessionId, long updatedAt)
        {
            for (var i = 0; i < _sessions.Count; i++)
            {
                if (_sessions[i].SessionId == sessionId)
                {
                    _sessions[i] = _sessions[i] with { UpdatedAtUnixMs = updatedAt };
                }
            }
        }

        public ValueTask EmitWorkspaceFrameAsync(WorkspaceFollowFrame frame) =>
            _workspaceFrameCallback is { } cb ? cb(frame) : ValueTask.CompletedTask;

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
            SwitchModelInSession: false,
            ManageCredentials: false,
            ServerPromptIdempotency: true,
            PluginInventory: false,
            PluginMutation: false,
            GitOperations: false,
            ArchiveSessions: true);

        public override Task<Outcome<DshCapabilities>> ProbeCapabilitiesAsync(CancellationToken ct = default) =>
            Task.FromResult(Outcome<DshCapabilities>.Ok(Capabilities));

        public override Task<Outcome<IReadOnlyList<SessionSummaryView>>> ListSessionsAsync(CancellationToken ct = default) =>
            Task.FromResult(Outcome<IReadOnlyList<SessionSummaryView>>.Ok(_sessions.ToArray()));

        public override Task<Outcome<string>> CreateOrResumeSessionAsync(CreateOrResumeSessionRequest request, CancellationToken ct = default) =>
            Task.FromResult(Outcome<string>.Ok(request.SessionId ?? "sess-new"));

        public override Task<Outcome<IReadOnlyList<string>>> GetArchivedSessionIdsAsync(CancellationToken ct = default) =>
            Task.FromResult(Outcome<IReadOnlyList<string>>.Ok(_archived.ToArray()));

        public override Task<Outcome<IReadOnlyList<string>>> ArchiveSessionAsync(string sessionId, bool stopActivity = false, CancellationToken ct = default)
        {
            ArchiveCallCount++;
            var target = _sessions.FirstOrDefault(s => s.SessionId == sessionId);
            if (target is null)
            {
                return Task.FromResult(Outcome<IReadOnlyList<string>>.Fail(DshFailureCode.SessionNotFound, "session/not-found"));
            }
            if (target.Running && !stopActivity)
            {
                return Task.FromResult(Outcome<IReadOnlyList<string>>.Fail(DshFailureCode.SessionActive, "workspace/session-active"));
            }

            for (var i = 0; i < _sessions.Count; i++)
            {
                if (_sessions[i].SessionId == sessionId)
                {
                    _sessions[i] = _sessions[i] with { Running = false };
                }
            }
            _archived.Add(sessionId);
            return Task.FromResult(Outcome<IReadOnlyList<string>>.Ok(_archived.ToArray()));
        }

        public override Task<Outcome<IReadOnlyList<string>>> UnarchiveSessionAsync(string sessionId, CancellationToken ct = default)
        {
            _archived.Remove(sessionId);
            return Task.FromResult(Outcome<IReadOnlyList<string>>.Ok(_archived.ToArray()));
        }

        public override Task<Outcome<IAsyncDisposable>> FollowWorkspaceAsync(Func<WorkspaceFollowFrame, ValueTask> onFrame, CancellationToken ct = default)
        {
            _workspaceFrameCallback = onFrame;
            return Task.FromResult(Outcome<IAsyncDisposable>.Ok(new TrackingDisposable(() => _workspaceFrameCallback = null)));
        }

        public override Task<Outcome<SessionPageView>> LoadSessionEventsAsync(string sessionId, long? throughSeq = null, long? beforeSeq = null, int? maxMessages = null, CancellationToken ct = default) =>
            Task.FromResult(Outcome<SessionPageView>.Ok(new SessionPageView([], HasMore: false, Cursor: 0)));

        public override async Task<Outcome<IAsyncDisposable>> FollowSessionAsync(
            string sessionId,
            Func<SessionFollowUpdate, ValueTask> onUpdate,
            int? maxMessages = null,
            bool activate = true,
            CancellationToken ct = default)
        {
            await onUpdate(new SessionFollowSnapshotUpdate(
                sessionId,
                Cursor: 2,
                HasMore: false,
                Events:
                [
                    new SessionWireEventView("user/message", 1, 100, JsonElement.Parse("""{"id":"u1","source":{"kind":"user"},"content":[{"type":"text","text":"hello"}]}""")),
                    new SessionWireEventView("assistant/message", 2, 101, JsonElement.Parse("""{"id":"a1","content":[{"type":"text","text":"world"}]}""")),
                ]));
            return Outcome<IAsyncDisposable>.Ok(new TrackingDisposable(() => FollowDisposedCount++));
        }

        private sealed class TrackingDisposable(Action onDispose) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                onDispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
