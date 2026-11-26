using System.Text.Json;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;

namespace DshMobile.Agent.Tests.Engines;

/// <summary>
/// 锁定 UI-S2 的时间线保序与真实耗时透传：live 流式交错、冷读多条 assistant/message、
/// TimeUnixMs 透传、重连快照去重，以及旧版 kept 缓存的向后兼容。
/// </summary>
public sealed class TranscriptOrderingAndTimingTests : IDisposable
{
    private const string LegacyV1Json = """
        {
          "schemaVersion": 1,
          "scope": { "runtimeId": "rt-order", "profileId": "web", "sessionId": "s-legacy" },
          "cursorSeq": 7,
          "phase": 5,
          "savedAtUnixMs": 1700000000000,
          "rows": [
            { "kind": 0, "itemId": "u:1", "turnSeq": 1, "primaryText": "旧用户消息" },
            { "kind": 2, "itemId": "reason:1", "turnSeq": 1, "primaryText": "旧推理", "isError": false },
            {
              "kind": 3,
              "itemId": "tool:c1",
              "turnSeq": 1,
              "primaryText": "旧工具输出",
              "secondaryText": "{}",
              "codeOrToolName": "bash",
              "isError": false
            }
          ],
          "sessionTitle": "旧缓存标题"
        }
        """;

    private readonly string _cacheDir = Directory.CreateTempSubdirectory("dshm-order-cache-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_cacheDir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响断言结果。
        }
    }

    [Fact]
    public void LiveStreaming_PreservesInterleavedReasoningTextAndToolSegments()
    {
        var scope = new SessionKey("rt-order", "web", "s-live");
        var reducer = new TranscriptReducer(scope);

        Assert.True(reducer.ApplyWireEvent(scope, Wire("turn/start", 1, 1_000, """{"turn":1}""")));
        Assert.True(reducer.ApplyAssistantStreamFrame(
            scope,
            StreamFrame("start", "att-live", 1, """{"type":"start","attemptId":"att-live","revision":1}""")));
        Assert.True(reducer.ApplyAssistantStreamFrame(scope, ReasoningChunk("att-live", 1, 0, "工具前思考")));
        Assert.True(reducer.ApplyAssistantStreamFrame(scope, TextChunk("att-live", 1, 1, "工具前正文\n")));
        Assert.True(reducer.ApplyWireEvent(scope, Wire(
            "tool/call", 2, 2_000, """{"callId":"c-1","name":"bash","arguments":"{}"}""")));
        Assert.True(reducer.ApplyWireEvent(scope, Wire(
            "tool/result", 3, 3_000, """{"callId":"c-1","message":{"isError":false,"content":[{"type":"text","text":"ok"}]}}""")));
        Assert.True(reducer.ApplyAssistantStreamFrame(scope, ReasoningChunk("att-live", 1, 2, "工具后思考")));
        Assert.True(reducer.ApplyAssistantStreamFrame(scope, TextChunk("att-live", 1, 3, "工具后正文\n")));

        var snapshot = reducer.DeriveSnapshot(0, 0, 0);

        // 工具前的正文必须在工具调用前封口，工具后的正文不得回滚进同一个尾块。
        Assert.Equal(
            new[]
            {
                "reasoning:工具前思考",
                "markdown:工具前正文",
                "tool:c-1",
                "reasoning:工具后思考",
                "markdown:工具后正文",
            },
            snapshot.Rows.Select(Describe));
    }

    [Fact]
    public void ColdReplay_MultipleAssistantMessagesInSingleTurn_KeepEverySegment()
    {
        var scope = new SessionKey("rt-order", "web", "s-cold");
        var reducer = new TranscriptReducer(scope);

        Assert.True(reducer.ApplyWireEvent(scope, Wire("turn/start", 1, 1_000, """{"turn":1}""")));
        Assert.True(reducer.ApplyWireEvent(scope, Wire(
            "user/message", 2, 1_100, """{"id":"u-1","content":[{"type":"text","text":"请执行"}]}""")));
        Assert.True(reducer.ApplyWireEvent(scope, Wire(
            "assistant/message",
            3,
            2_000,
            """
            {
              "id": "a-1",
              "content": [
                { "type": "reasoning", "text": "R1" },
                { "type": "tool-call", "id": "c-1", "name": "bash", "arguments": "{}" }
              ]
            }
            """)));
        Assert.True(reducer.ApplyWireEvent(scope, Wire(
            "tool/call", 4, 2_100, """{"callId":"c-1","name":"bash","arguments":"{}"}""")));
        Assert.True(reducer.ApplyWireEvent(scope, Wire(
            "tool/result", 5, 2_500, """{"callId":"c-1","message":{"isError":false,"content":[{"type":"text","text":"ok"}]}}""")));
        Assert.True(reducer.ApplyWireEvent(scope, Wire(
            "assistant/message",
            6,
            3_000,
            """
            {
              "id": "a-2",
              "content": [
                { "type": "reasoning", "text": "R2" },
                { "type": "text", "text": "T2" },
                { "type": "tool-call", "id": "c-2", "name": "read", "arguments": "{}" }
              ]
            }
            """)));
        Assert.True(reducer.ApplyWireEvent(scope, Wire(
            "tool/call", 7, 3_100, """{"callId":"c-2","name":"read","arguments":"{}"}""")));
        Assert.True(reducer.ApplyWireEvent(scope, Wire(
            "tool/result", 8, 3_500, """{"callId":"c-2","message":{"isError":false,"content":[{"type":"text","text":"ok"}]}}""")));
        Assert.True(reducer.ApplyWireEvent(scope, Wire(
            "assistant/message",
            9,
            4_000,
            """{"id":"a-3","content":[{"type":"reasoning","text":"R3"},{"type":"text","text":"T3"}]}""")));
        Assert.True(reducer.ApplyWireEvent(scope, Wire(
            "turn/end", 10, 5_000, """{"turn":1,"reason":{"kind":"completed"}}""")));

        var snapshot = reducer.DeriveSnapshot(0, 0, 0);

        // 同一 Turn 的多条 assistant/message 必须逐段保留，不能被后一条整体覆盖。
        Assert.Equal(
            new[]
            {
                "user",
                "reasoning:R1",
                "tool:c-1",
                "reasoning:R2",
                "markdown:T2",
                "tool:c-2",
                "reasoning:R3",
                "markdown:T3",
            },
            snapshot.Rows.Select(Describe));
        Assert.Equal(new[] { "R1", "R2", "R3" }, snapshot.Rows.OfType<ReasoningBlockRow>().Select(row => row.Text));
    }

    [Fact]
    public void TimeUnixMs_FlowsIntoReasoningToolRowsAndTurnDurations()
    {
        var scope = new SessionKey("rt-order", "web", "s-timing");
        var reducer = new TranscriptReducer(scope);

        Assert.True(reducer.ApplyWireEvent(scope, Wire("turn/start", 1, 1_000, """{"turn":1}""")));
        Assert.True(reducer.ApplyAssistantStreamFrame(
            scope,
            StreamFrame("start", "att-time", 1, """{"type":"start","attemptId":"att-time","revision":1}""")));
        Assert.True(reducer.ApplyAssistantStreamFrame(scope, ReasoningChunk("att-time", 1, 0, "带时间的推理")));
        Assert.True(reducer.ApplyAssistantStreamFrame(scope, TextChunk("att-time", 1, 1, "带时间的正文\n")));
        Assert.True(reducer.ApplyWireEvent(scope, Wire(
            "tool/call", 2, 2_000, """{"callId":"c-time","name":"bash","arguments":"{}"}""")));
        Assert.True(reducer.ApplyWireEvent(scope, Wire(
            "tool/result", 3, 3_500, """{"callId":"c-time","message":{"isError":false,"content":[{"type":"text","text":"done"}]}}""")));
        Assert.True(reducer.ApplyWireEvent(scope, Wire(
            "assistant/message",
            4,
            4_000,
            """{"id":"a-time","content":[{"type":"reasoning","text":"带时间的推理"},{"type":"text","text":"带时间的正文"}]}""")));
        Assert.True(reducer.ApplyWireEvent(scope, Wire(
            "turn/end", 5, 5_000, """{"turn":1,"reason":{"kind":"completed"}}""")));

        var snapshot = reducer.DeriveSnapshot(0, 0, 0);

        var reasoning = Assert.Single(snapshot.Rows.OfType<ReasoningBlockRow>());
        Assert.Equal(1_000L, reasoning.StartedAtUnixMs);
        Assert.Equal(4_000L, reasoning.CompletedAtUnixMs);
        Assert.Equal(3_000L, reasoning.DurationMs);

        var tool = Assert.Single(snapshot.Rows.OfType<ToolCallRow>());
        Assert.Equal(2_000L, tool.StartedAtUnixMs);
        Assert.Equal(3_500L, tool.CompletedAtUnixMs);
        Assert.Equal(1_500L, tool.DurationMs);

        Assert.NotNull(snapshot.TurnDurationsMs);
        Assert.Equal(4_000L, snapshot.TurnDurationsMs![1]);
    }

    [Fact]
    public void MissingWireTimestamps_KeepReasoningToolAndTurnDurationsNull()
    {
        var scope = new SessionKey("rt-order", "web", "s-no-time");
        var reducer = new TranscriptReducer(scope);

        // TimeUnixMs = 0 表示服务端没有提供有效时间，绝不能退化为客户端当前时间。
        Assert.True(reducer.ApplyWireEvent(scope, Wire("turn/start", 1, 0, """{"turn":1}""")));
        Assert.True(reducer.ApplyAssistantStreamFrame(
            scope,
            StreamFrame("start", "att-none", 1, """{"type":"start","attemptId":"att-none","revision":1}""")));
        Assert.True(reducer.ApplyAssistantStreamFrame(scope, ReasoningChunk("att-none", 1, 0, "无时间推理")));
        Assert.True(reducer.ApplyAssistantStreamFrame(scope, TextChunk("att-none", 1, 1, "无时间正文\n")));
        Assert.True(reducer.ApplyWireEvent(scope, Wire(
            "tool/call", 2, 0, """{"callId":"c-none","name":"bash","arguments":"{}"}""")));
        Assert.True(reducer.ApplyWireEvent(scope, Wire(
            "tool/result", 3, 0, """{"callId":"c-none","message":{"isError":false,"content":[{"type":"text","text":"ok"}]}}""")));
        Assert.True(reducer.ApplyWireEvent(scope, Wire(
            "assistant/message",
            4,
            0,
            """{"id":"a-none","content":[{"type":"reasoning","text":"无时间推理"},{"type":"text","text":"无时间正文"}]}""")));
        Assert.True(reducer.ApplyWireEvent(scope, Wire(
            "turn/end", 5, 0, """{"turn":1,"reason":{"kind":"completed"}}""")));

        var snapshot = reducer.DeriveSnapshot(0, 0, 0);

        var reasoning = Assert.Single(snapshot.Rows.OfType<ReasoningBlockRow>());
        Assert.Null(reasoning.StartedAtUnixMs);
        Assert.Null(reasoning.CompletedAtUnixMs);
        Assert.Null(reasoning.DurationMs);

        var tool = Assert.Single(snapshot.Rows.OfType<ToolCallRow>());
        Assert.Null(tool.StartedAtUnixMs);
        Assert.Null(tool.CompletedAtUnixMs);
        Assert.Null(tool.DurationMs);

        Assert.Null(snapshot.TurnDurationsMs);
    }

    [Fact]
    public void ApplyReconciledSnapshot_SortsDeduplicatesAndPreservesInterleaving()
    {
        var scope = new SessionKey("rt-order", "web", "s-reconcile");
        var reducer = new TranscriptReducer(scope);

        // 故意打乱顺序并注入重复 seq，验证重连时按 seq 排序、重复帧去重。
        var events = new List<SessionWireEventView>
        {
            Wire("assistant/message", 9, 3_000,
                """{"id":"a-2","content":[{"type":"reasoning","text":"R2"},{"type":"text","text":"T2"}]}"""),
            Wire("tool/result", 6, 2_600,
                """{"callId":"c-1","message":{"isError":false,"content":[{"type":"text","text":"ok"}]}}"""),
            Wire("user/message", 2, 1_100, """{"id":"u-1","content":[{"type":"text","text":"问题"}]}"""),
            Wire("turn/start", 1, 1_000, """{"turn":1}"""),
            Wire("tool/call", 5, 2_500, """{"callId":"c-1","name":"bash","arguments":"{}"}"""),
            Wire("assistant/message", 4, 2_000,
                """{"id":"a-1","content":[{"type":"reasoning","text":"R1"},{"type":"tool-call","id":"c-1","name":"bash","arguments":"{}"}]}"""),
            Wire("turn/start", 1, 1_000, """{"turn":1}"""),
            Wire("assistant/message", 4, 2_000,
                """{"id":"a-1","content":[{"type":"reasoning","text":"R1"},{"type":"tool-call","id":"c-1","name":"bash","arguments":"{}"}]}"""),
        };

        reducer.ApplyReconciledSnapshot(9, events);
        var snapshot = reducer.DeriveSnapshot(0, 0, 0);

        Assert.Equal(
            new[] { "user", "reasoning:R1", "tool:c-1", "reasoning:R2", "markdown:T2" },
            snapshot.Rows.Select(Describe));

        // 同一快照再次应用必须幂等，不能产生重复行。
        reducer.ApplyReconciledSnapshot(9, events);
        var replayed = reducer.DeriveSnapshot(0, 0, 0);
        Assert.Equal(snapshot.Rows.Select(Describe), replayed.Rows.Select(Describe));
    }

    [Fact]
    public async Task KeptTranscriptStore_LoadsLegacyV1WithoutTimingFields_AndRoundTripsNewFields()
    {
        var store = new KeptTranscriptStore(_cacheDir);
        await File.WriteAllTextAsync(Path.Combine(_cacheDir, "legacy.kept.json"), LegacyV1Json);

        var legacyLoaded = await store.TryLoadMostRecentAsync();
        Assert.NotNull(legacyLoaded);
        var legacyRecord = legacyLoaded!.Record;
        Assert.Equal("s-legacy", legacyRecord.Scope.SessionId);
        Assert.Equal(KeptTranscriptStore.CurrentSchemaVersion, legacyRecord.SchemaVersion);
        Assert.Null(legacyRecord.TurnDurationsMs);
        Assert.All(legacyRecord.Rows, row =>
        {
            Assert.Null(row.StartedAtUnixMs);
            Assert.Null(row.CompletedAtUnixMs);
        });

        var legacyRows = KeptTranscriptStore.ToDetachedChatRows(legacyRecord);
        var legacyReasoning = Assert.Single(legacyRows.OfType<ReasoningBlockRow>());
        Assert.Null(legacyReasoning.StartedAtUnixMs);
        Assert.Null(legacyReasoning.CompletedAtUnixMs);
        Assert.Null(legacyReasoning.DurationMs);
        var legacyTool = Assert.Single(legacyRows.OfType<ToolCallRow>());
        Assert.Null(legacyTool.StartedAtUnixMs);
        Assert.Null(legacyTool.CompletedAtUnixMs);
        Assert.Null(legacyTool.DurationMs);

        // 新字段与 TurnDurationsMs 必须完整往返持久化。
        var newScope = new SessionKey("rt-order", "web", "s-new");
        var snapshot = new ChatSnapshot(
            Revision: 1,
            Session: newScope,
            CursorSeq: 10,
            Phase: RunPhase.Completed,
            IsDetachedCache: false,
            IsPartialHistory: false,
            FailureCode: null,
            Rows:
            [
                new ReasoningBlockRow("reason:1", 1, "a-1", 0, "新推理", true, 1_000, 2_500),
                new ToolCallRow("tool:c1", 1, "c1", "bash", "{}", ToolPhase.Completed, "完成", false, 1_500, 3_000),
            ],
            Counters: TranscriptCounters.Zero,
            SessionTitle: "新缓存标题",
            TurnDurationsMs: new Dictionary<long, long> { [1] = 4_000 });

        await store.SaveAsync(snapshot);
        var reloadedDetail = await store.LoadDetailedAsync(newScope);
        Assert.Equal(KeptTranscriptLoadStatus.Loaded, reloadedDetail.Status);
        var reloaded = Assert.IsType<KeptTranscriptRecord>(reloadedDetail.Record);
        Assert.Equal(4_000L, reloaded.TurnDurationsMs![1]);

        var reloadedRows = KeptTranscriptStore.ToDetachedChatRows(reloaded);
        var reasoning = Assert.Single(reloadedRows.OfType<ReasoningBlockRow>());
        Assert.Equal(1_000L, reasoning.StartedAtUnixMs);
        Assert.Equal(2_500L, reasoning.CompletedAtUnixMs);
        Assert.Equal(1_500L, reasoning.DurationMs);
        var tool = Assert.Single(reloadedRows.OfType<ToolCallRow>());
        Assert.Equal(1_500L, tool.StartedAtUnixMs);
        Assert.Equal(3_000L, tool.CompletedAtUnixMs);
        Assert.Equal(1_500L, tool.DurationMs);
    }

    private static SessionWireEventView Wire(string type, long seq, long timeUnixMs, string dataJson) =>
        new(type, seq, timeUnixMs, JsonElement.Parse(dataJson));

    private static SessionFollowAssistantStreamUpdate StreamFrame(
        string frameType,
        string attemptId,
        long revision,
        string rawJson)
    {
        using var document = JsonDocument.Parse(rawJson);
        return new SessionFollowAssistantStreamUpdate(frameType, attemptId, revision, document.RootElement.Clone());
    }

    private static SessionFollowAssistantStreamUpdate ReasoningChunk(
        string attemptId,
        long revision,
        int index,
        string text) =>
        StreamFrame("chunk", attemptId, revision, ChunkJson(attemptId, revision, index, "reasoning-delta", text));

    private static SessionFollowAssistantStreamUpdate TextChunk(
        string attemptId,
        long revision,
        int index,
        string text) =>
        StreamFrame("chunk", attemptId, revision, ChunkJson(attemptId, revision, index, "text-delta", text));

    private static string ChunkJson(string attemptId, long revision, int index, string chunkType, string text) =>
        $$"""{"type":"chunk","attemptId":"{{attemptId}}","revision":{{revision}},"index":{{index}},"chunk":{"type":"{{chunkType}}","text":{{JsonSerializer.Serialize(text)}} } }""";

    private static string Describe(ChatRow row) =>
        row switch
        {
            UserMessageRow => "user",
            ReasoningBlockRow reasoning => $"reasoning:{reasoning.Text}",
            MarkdownBlockRow markdown => $"markdown:{markdown.Markdown}",
            ToolCallRow tool => $"tool:{tool.CallId}",
            CommandRow command => $"command:{command.CommandId}",
            NoticeRow notice => $"notice:{notice.DiagnosticKey}",
            UnknownEventRow unknown => $"unknown:{unknown.EventType}",
            _ => row.GetType().Name,
        };
}
