using System.Text.Json;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;
using DshMobile.Agent.Presentation;

namespace DshMobile.Agent.Tests.Presentation;

public sealed class CredentialsAndSessionsViewModelTests : IDisposable
{
    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), $"dshm-pr05-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_cacheDir))
        {
            Directory.Delete(_cacheDir, recursive: true);
        }
    }

    [Fact]
    public async Task CredentialsViewModel_DiscoversSetsClearsAndPersistsCustomRef_WithoutExposingPlaintext()
    {
        var customRefsPath = Path.Combine(_cacheDir, "custom-credential-refs.txt");
        var gw = new StubPr05Gateway();
        await using var vm = new CredentialsViewModel(gw, customRefsPath);

        await vm.RefreshAsync();
        Assert.True(vm.State.CanManageCredentials);
        Assert.False(vm.State.HasAnyConfigured);
        Assert.Contains(vm.State.Entries, e => e.Ref == "DEEPSEEK_API_KEY");
        Assert.Contains(vm.State.Entries, e => e.Ref == "CUSTOM_PROVIDER_KEY");

        await vm.SaveCredentialAsync("DEEPSEEK_API_KEY", "sk-live-secret-123456");
        Assert.True(vm.State.HasAnyConfigured);
        Assert.Equal("credentials.status.saved", vm.State.StatusKey);
        var savedEntry = vm.State.Entries.First(e => e.Ref == "DEEPSEEK_API_KEY");
        Assert.True(savedEntry.Configured);
        Assert.Equal("••••••••", savedEntry.Masked);
        Assert.DoesNotContain("sk-live-secret", JsonSerializer.Serialize(vm.State.Entries), StringComparison.Ordinal);

        // F16: 新增自定义引用保存后立即出现在列表，并在重启重建 ViewModel 后仍可查看与清除（文件只存引用名、绝不存密钥）
        await vm.SaveCredentialAsync("DSH_TEST_REF", "sk-custom-987654");
        var customEntry = Assert.Single(vm.State.Entries, e => e.Ref == "DSH_TEST_REF");
        Assert.True(customEntry.Configured);
        Assert.DoesNotContain("sk-custom-987654", await File.ReadAllTextAsync(customRefsPath), StringComparison.Ordinal);

        await using var vm2 = new CredentialsViewModel(gw, customRefsPath);
        await vm2.RefreshAsync();
        Assert.Contains(vm2.State.Entries, e => e.Ref == "DSH_TEST_REF" && e.Configured);

        await vm2.ClearCredentialAsync("DSH_TEST_REF");
        Assert.Contains(vm2.State.Entries, e => e.Ref == "DSH_TEST_REF" && !e.Configured);

        await vm2.ClearCredentialAsync("DEEPSEEK_API_KEY");
        Assert.False(vm2.State.HasAnyConfigured);
        Assert.Equal("credentials.status.cleared", vm2.State.StatusKey);
    }

    [Fact]
    public async Task ChatViewModel_CacheFirstBoot_ModelSwitch_AndOlderPagination_WorkWithoutDuplicates()
    {
        // N-05-04: 预先落盘单条会话缓存，模拟冷启不连宿主先显示真实历史
        var store = new KeptTranscriptStore(_cacheDir);
        var cachedScope = new SessionKey("local", "web", "sess-cached");
        await store.SaveAsync(new ChatSnapshot(
            Revision: 1,
            Session: cachedScope,
            CursorSeq: 12,
            Phase: RunPhase.Completed,
            IsDetachedCache: false,
            IsPartialHistory: true,
            FailureCode: null,
            Rows: [new UserMessageRow("msg:u1", 2, "u1", "缓存里的问题", 0, false)],
            Counters: TranscriptCounters.Zero,
            SessionTitle: "旧会话标题"));

        var gw = new StubPr05Gateway { CatalogAvailable = false };
        await using var transcripts = new TranscriptEngine(coalesceWindow: TimeSpan.FromMilliseconds(5));
        await using var requests = new RequestsEngine(gw, gw, transcripts);
        var sends = new SendStateMachine(gw, gw);
        var reconciler = new HistoryReconciler(gw, gw);
        await using var vm = new ChatViewModel(gw, transcripts, requests, sends, reconciler, store);

        var loadedCache = await vm.LoadMostRecentCachedSessionAsync();
        Assert.True(loadedCache);
        Assert.Equal("sess-cached", vm.State.ActiveSession.SessionId);
        Assert.Equal("旧会话标题", vm.State.SessionTitle);
        Assert.True(vm.State.Transcript.IsDetachedCache);
        Assert.Single(vm.State.Transcript.Rows);

        // F17: 连接后直接校准已有非空会话，绝不调用 session/create 覆写为空白会话
        await vm.ConnectAndAttachAsync();
        Assert.True(vm.State.IsConnected);
        Assert.Equal(0, gw.CreateCalls);
        Assert.Equal("sess-cached", vm.State.ActiveSession.SessionId);
        Assert.False(vm.State.Transcript.IsDetachedCache);
        Assert.Equal(2, vm.State.Transcript.Rows.Count); // 1 条用户消息 + 1 条助手消息（来自服务端窗口）
        Assert.True(vm.State.NeedsCredentialOnboarding);   // 无可用模型且无已配置凭据

        // F19: 同一会话忽略引导后，重新刷新就绪态不再重复弹窗
        vm.DismissCredentialOnboarding();
        Assert.False(vm.State.NeedsCredentialOnboarding);
        await vm.RefreshCredentialReadinessAsync();
        Assert.False(vm.State.NeedsCredentialOnboarding);

        // N-05-03 冷读分页前置加载更早历史
        Assert.True(vm.State.Transcript.IsPartialHistory);
        await vm.LoadOlderHistoryAsync();
        Assert.False(vm.State.Transcript.IsPartialHistory);
        Assert.Equal(4, vm.State.Transcript.Rows.Count);
        Assert.Contains(vm.State.Transcript.Rows.OfType<UserMessageRow>(), r => r.Text == "更早历史问题");

        // N-05-02 模型与 Reasoning Effort 切换
        gw.CatalogAvailable = true;
        await vm.SelectModelAsync(new ModelSelectionSpec("deepseek-official", "deepseek-reasoner", "high"));
        Assert.Equal("deepseek-official/deepseek-reasoner · high", vm.State.ModelLabel);
        Assert.Equal("high", vm.State.CurrentModel?.ReasoningEffort);
    }

    [Fact]
    public async Task ChatViewModel_SkipsOnboarding_WhenModelCatalogAlreadyHasUsableModels()
    {
        var gw = new StubPr05Gateway { CatalogAvailable = true };
        await using var transcripts = new TranscriptEngine(coalesceWindow: TimeSpan.FromMilliseconds(5));
        await using var requests = new RequestsEngine(gw, gw, transcripts);
        var sends = new SendStateMachine(gw, gw);
        var reconciler = new HistoryReconciler(gw, gw);
        await using var vm = new ChatViewModel(gw, transcripts, requests, sends, reconciler);

        // F15: 只要模型目录存在可用模型（如 DeepSeek Account 或未列出的自定义 Provider Ref），就不得误弹缺少凭据引导
        await vm.ConnectAndAttachAsync("sess-cached");
        Assert.True(vm.State.IsConnected);
        Assert.Equal(0, gw.CreateCalls);
        Assert.False(vm.State.NeedsCredentialOnboarding);
    }

    [Fact]
    public async Task SessionsViewModel_PassesThroughOriginCwdAndPreset_WithoutDroppingMetadata()
    {
        var gw = new StubPr05Gateway();
        await using var transcripts = new TranscriptEngine(coalesceWindow: TimeSpan.FromMilliseconds(5));
        await using var requests = new RequestsEngine(gw, gw, transcripts);
        await using var sessionsVm = new SessionsViewModel(gw, requests);

        await sessionsVm.RefreshAsync();

        // UI-S4: 列表项必须保留网关返回的 Origin、Cwd 与 AgentPreset，供来源分组与副标题展示。
        var local = sessionsVm.State.Sessions.First(s => s.SessionId == "sess-cached");
        Assert.Equal("web", local.Origin);
        Assert.Equal("/root/projects", local.Cwd);
        Assert.Equal("build", local.AgentPreset);

        var remote = sessionsVm.State.Sessions.First(s => s.SessionId == "sess-other");
        Assert.Equal("remote", remote.Origin);
        Assert.Equal("/root/proj", remote.Cwd);
        Assert.Null(remote.AgentPreset);

        // 搜索仍可按标题/ID/工作区过滤，且过滤后来源字段不丢失。
        sessionsVm.SetSearchQuery("projects");
        var filtered = Assert.Single(sessionsVm.State.Sessions);
        Assert.Equal("sess-cached", filtered.SessionId);
        Assert.Equal("web", filtered.Origin);
    }

    [Fact]
    public async Task SessionsViewModel_AggregatesBadgesSearchRenameForkAndCancelGating()
    {
        var gw = new StubPr05Gateway();
        await using var transcripts = new TranscriptEngine(coalesceWindow: TimeSpan.FromMilliseconds(5));
        await using var requests = new RequestsEngine(gw, gw, transcripts);
        await using var sessionsVm = new SessionsViewModel(gw, requests);

        await sessionsVm.RefreshAsync();
        Assert.Equal(2, sessionsVm.State.Sessions.Count);
        Assert.Equal("排查构建失败", sessionsVm.State.Sessions[0].DisplayTitle);

        // Pending 审批计数应联动映射为角标
        requests.IngestApprovalRequest(new SessionKey("local", "web", "sess-cached"), "c-1",
            new ApprovalRequestView("ev-s-1", "sess-cached", "bash", "call-s1"));
        Assert.Equal(1, sessionsVm.State.Sessions.First(s => s.SessionId == "sess-cached").PendingRequestCount);

        // 搜索过滤
        sessionsVm.SetSearchQuery("排查");
        var filtered = Assert.Single(sessionsVm.State.Sessions);
        Assert.Equal("sess-cached", filtered.SessionId);
        sessionsVm.SetSearchQuery(string.Empty);

        // 重命名 + Fork
        var renamed = await sessionsVm.RenameSessionAsync("sess-cached", "新会话名字");
        Assert.True(renamed.IsOk);
        Assert.Equal("新会话名字", sessionsVm.State.Sessions.First(s => s.SessionId == "sess-cached").DisplayTitle);

        var forked = await sessionsVm.ForkSessionAsync("sess-cached");
        Assert.True(forked.IsOk);
        Assert.Equal(3, sessionsVm.State.Sessions.Count);

        // 网关不支持 CancelSession 时返回 Unsupported，绝不假成功
        gw.CancelSupported = false;
        await sessionsVm.RefreshAsync();
        var cancelRes = await sessionsVm.CancelSessionAsync("sess-other");
        Assert.False(cancelRes.IsOk);
        Assert.Equal(DshFailureCode.Unsupported, cancelRes.Error!.Code);
    }

    private sealed class StubPr05Gateway : DefaultDshGateway
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);
        private readonly List<SessionSummaryView> _sessions =
        [
            new("sess-cached", 2000, false, false, true,
                Title: "排查构建失败", Cwd: "/root/projects", AgentPreset: "build", Origin: "web"),
            new("sess-other", 1000, true, false, true,
                ParentSessionId: "sess-cached", Cwd: "/root/proj", Origin: "remote"),
        ];

        public bool CancelSupported { get; set; } = true;
        public bool CatalogAvailable { get; set; } = true;
        public int CreateCalls { get; private set; }
        public override event Action<ForwardedGatewayEvent>? ForwardedEvent
        {
            add { }
            remove { }
        }

        public override DshCapabilities Capabilities => new(
            Prompt: true,
            PromptImages: true,
            SlashCommands: true,
            StreamingEvents: true,
            Approvals: true,
            UserQuestions: true,
            ResumeKnownSession: true,
            ListSessions: true,
            HistoryReplay: true,
            CancelSession: CancelSupported,
            SwitchModelInSession: true,
            ManageCredentials: true,
            ServerPromptIdempotency: true,
            PluginInventory: false,
            PluginMutation: false,
            GitOperations: false);

        public override Task<Outcome<DshCapabilities>> ProbeCapabilitiesAsync(CancellationToken ct = default) =>
            Task.FromResult(Outcome<DshCapabilities>.Ok(Capabilities));

        public override Task<Outcome<IReadOnlyList<string>>> DiscoverCredentialRefsAsync(CancellationToken ct = default) =>
            Task.FromResult(Outcome<IReadOnlyList<string>>.Ok(["DEEPSEEK_API_KEY", "CUSTOM_PROVIDER_KEY"]));

        public override Task<Outcome<IReadOnlyDictionary<string, CredentialStatusView>>> DescribeCredentialsAsync(
            IReadOnlyList<string> refs,
            CancellationToken ct = default)
        {
            IReadOnlyDictionary<string, CredentialStatusView> result = refs.ToDictionary(
                r => r,
                r => _secrets.ContainsKey(r)
                    ? new CredentialStatusView(r, true, "file", true, "••••••••")
                    : new CredentialStatusView(r, false, null, true, null),
                StringComparer.Ordinal);
            return Task.FromResult(Outcome<IReadOnlyDictionary<string, CredentialStatusView>>.Ok(result));
        }

        public override Task<Outcome<Unit>> SetCredentialAsync(string credentialRef, string value, CancellationToken ct = default)
        {
            _secrets[credentialRef] = value;
            return Task.FromResult(Outcome<Unit>.Ok(Unit.Value));
        }

        public override Task<Outcome<Unit>> UnsetCredentialAsync(string credentialRef, CancellationToken ct = default)
        {
            _secrets.Remove(credentialRef);
            return Task.FromResult(Outcome<Unit>.Ok(Unit.Value));
        }

        public override Task<Outcome<IReadOnlyList<SessionSummaryView>>> ListSessionsAsync(CancellationToken ct = default) =>
            Task.FromResult(Outcome<IReadOnlyList<SessionSummaryView>>.Ok(_sessions.ToArray()));

        public override Task<Outcome<string>> CreateOrResumeSessionAsync(CreateOrResumeSessionRequest request, CancellationToken ct = default)
        {
            CreateCalls++;
            return Task.FromResult(Outcome<string>.Ok(request.SessionId ?? "sess-new"));
        }

        public override Task<Outcome<string>> RenameSessionAsync(string sessionId, string title, CancellationToken ct = default)
        {
            for (var i = 0; i < _sessions.Count; i++)
            {
                if (_sessions[i].SessionId == sessionId)
                {
                    _sessions[i] = _sessions[i] with { Title = title };
                }
            }
            return Task.FromResult(Outcome<string>.Ok(title));
        }

        public override Task<Outcome<string>> ForkSessionAsync(string sessionId, long? atSeq = null, CancellationToken ct = default)
        {
            var forkedId = "sess-forked";
            _sessions.Add(new SessionSummaryView(forkedId, 3000, false, false, true, ParentSessionId: sessionId));
            return Task.FromResult(Outcome<string>.Ok(forkedId));
        }

        public override Task<Outcome<ModelCatalogView>> GetModelCatalogAsync(CancellationToken ct = default) =>
            Task.FromResult(Outcome<ModelCatalogView>.Ok(new ModelCatalogView(
                new ModelSelectionSpec("deepseek-official", "deepseek-chat"),
                CatalogAvailable ? ["deepseek-official"] : [],
                CatalogAvailable
                    ? [
                        new ModelProviderGroupView("deepseek-official", "DeepSeek", [
                            new ModelInfoView("deepseek-chat", "DeepSeek Chat", "通用模型", []),
                            new ModelInfoView("deepseek-reasoner", "DeepSeek Reasoner", "推理模型",
                                [new ModelEffortView("medium", "中"), new ModelEffortView("high", "高")], "medium"),
                        ]),
                    ]
                    : [],
                [])));

        public override Task<Outcome<ModelSelectionSpec>> SelectSessionModelAsync(
            string sessionId,
            ModelSelectionSpec selection,
            CancellationToken ct = default) =>
            Task.FromResult(Outcome<ModelSelectionSpec>.Ok(selection));

        public override Task<Outcome<SessionPageView>> LoadSessionEventsAsync(
            string sessionId,
            long? throughSeq = null,
            long? beforeSeq = null,
            int? maxMessages = null,
            CancellationToken ct = default)
        {
            if (beforeSeq is <= 10)
            {
                return Task.FromResult(Outcome<SessionPageView>.Ok(new SessionPageView(
                [
                    new SessionWireEventView("turn/start", 1, 100, JsonElement.Parse("""{"turn":1}""")),
                    new SessionWireEventView("user/message", 2, 101, JsonElement.Parse("""{"id":"old-u","source":{"kind":"user"},"content":[{"type":"text","text":"更早历史问题"}]}""")),
                    new SessionWireEventView("assistant/message", 3, 102, JsonElement.Parse("""{"id":"old-a","content":[{"type":"text","text":"更早历史回答"}]}""")),
                    new SessionWireEventView("turn/end", 4, 103, JsonElement.Parse("""{"turn":1,"reason":{"kind":"completed"}}""")),
                ], HasMore: false, Cursor: 4)));
            }
            return Task.FromResult(Outcome<SessionPageView>.Ok(new SessionPageView([], HasMore: false, Cursor: 12)));
        }

        public override async Task<Outcome<IAsyncDisposable>> FollowSessionAsync(
            string sessionId,
            Func<SessionFollowUpdate, ValueTask> onUpdate,
            int? maxMessages = null,
            bool activate = true,
            CancellationToken ct = default)
        {
            await onUpdate(new SessionFollowSnapshotUpdate(
                sessionId,
                Cursor: 12,
                HasMore: true,
                Events:
                [
                    new SessionWireEventView("turn/start", 10, 200, JsonElement.Parse("""{"turn":2}""")),
                    new SessionWireEventView("user/message", 11, 201, JsonElement.Parse("""{"id":"u1","source":{"kind":"user"},"content":[{"type":"text","text":"缓存里的问题"}]}""")),
                    new SessionWireEventView("assistant/message", 12, 202, JsonElement.Parse("""{"id":"a1","content":[{"type":"text","text":"实时恢复的回答"}]}""")),
                ]));
            return Outcome<IAsyncDisposable>.Ok(new NoopDisposable());
        }

        private sealed class NoopDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
