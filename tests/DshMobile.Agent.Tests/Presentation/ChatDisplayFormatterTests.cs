using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;
using DshMobile.Agent.Presentation;

namespace DshMobile.Agent.Tests.Presentation;

public sealed class ChatDisplayFormatterTests
{
    [Fact]
    public void FormatShortSessionTitle_FormatsUuidAndBlankTitles_KeepsNamedTitles()
    {
        Assert.Equal("PR09 OK reply", ChatDisplayFormatter.FormatShortSessionTitle("s1", "PR09 OK reply"));
        Assert.Equal("未命名会话 · #bcce5e", ChatDisplayFormatter.FormatShortSessionTitle("bcce5e42-1234-4abc-9def-0123456789ab", null));
        Assert.Equal("未命名会话 · #bcce5e", ChatDisplayFormatter.FormatShortSessionTitle("session-bcce5e4c-67c8-45b6-a018-81babfcd2d26", "session-bcce5e4c-67c8-45b6-a018-81babfcd2d26"));
        Assert.Equal("未命名会话 · #bcce5e", ChatDisplayFormatter.FormatShortSessionTitle("s1", "bcce5e42-1234-4abc-9def-0123456789ab"));
        Assert.Equal("未命名会话 · #1", ChatDisplayFormatter.FormatShortSessionTitle("sess-1", "   "));
    }

    [Fact]
    public void FormatCompactModelBadge_StripsProviderPrefixAndAppendsEffort()
    {
        var spec = new ModelSelectionSpec("deepseek-official", "deepseek-flash", "max");
        Assert.Equal("deepseek-flash · max", ChatDisplayFormatter.FormatCompactModelBadge("deepseek-official/deepseek-flash · max", spec, "切换模型"));
        Assert.Equal("deepseek-chat", ChatDisplayFormatter.FormatCompactModelBadge("deepseek-official/deepseek-chat", null, "切换模型"));
        Assert.Equal("切换模型", ChatDisplayFormatter.FormatCompactModelBadge(null, null, "切换模型"));
    }

    [Fact]
    public void FormatToolCallSummary_ExtractsHumanReadableFieldsAndFormatsDetailJson()
    {
        var bashSummary = ChatDisplayFormatter.FormatToolCallSummary(
            "bash",
            """{"description":"List user guide docs","command":"cd /opt/dshapp &&\nls user/guide"}""");

        Assert.Equal("bash · List user guide docs", bashSummary.HeaderLabel);
        Assert.Equal("$ cd /opt/dshapp && ls user/guide", bashSummary.SummaryLine);
        Assert.Contains("&&", bashSummary.DetailJson);
        Assert.DoesNotContain("\\u0026", bashSummary.DetailJson);

        var truncatedBash = ChatDisplayFormatter.FormatToolCallSummary(
            "bash",
            """{"description": "Find registered command definitions", "command": "cd /opt/dshapp/runtime && grep -rn \"foo\"…""");
        Assert.Equal("bash · Find registered command definitions", truncatedBash.HeaderLabel);
        Assert.StartsWith("$ cd /opt/dshapp/runtime && grep", truncatedBash.SummaryLine);

        var readSummary = ChatDisplayFormatter.FormatToolCallSummary(
            "read",
            """{"file_path":"/root/projects/docs/commands.md"}""");

        Assert.Equal("read · commands.md", readSummary.HeaderLabel);
        Assert.Equal("/root/projects/docs/commands.md", readSummary.SummaryLine);

        var malformed = ChatDisplayFormatter.FormatToolCallSummary("custom", "{not-json");
        Assert.Equal("执行工具 · custom", malformed.HeaderLabel);
        Assert.Equal("{not-json", malformed.SummaryLine);
        Assert.Equal("{not-json", malformed.DetailJson);
    }

    [Fact]
    public async Task KeptTranscriptStore_ListRecentAsync_OrdersNewestFirstAndSkipsCorrupt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dshm-kept-list-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var t0 = new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
            var store1 = new KeptTranscriptStore(dir, time: new FixedTimeProvider(t0));
            var store2 = new KeptTranscriptStore(dir, time: new FixedTimeProvider(t0.AddMinutes(5)));

            await store1.SaveAsync(new ChatSnapshot(
                Revision: 1,
                Session: new SessionKey("local", "web", "sess-older"),
                CursorSeq: 1,
                Phase: RunPhase.Idle,
                IsDetachedCache: false,
                IsPartialHistory: false,
                FailureCode: null,
                Rows: [new UserMessageRow("msg:u1", 1, "u1", "hi", 0, false)],
                Counters: TranscriptCounters.Zero,
                SessionTitle: "Older session"));

            await store2.SaveAsync(new ChatSnapshot(
                Revision: 2,
                Session: new SessionKey("local", "web", "sess-newer"),
                CursorSeq: 2,
                Phase: RunPhase.Idle,
                IsDetachedCache: false,
                IsPartialHistory: false,
                FailureCode: null,
                Rows: [new UserMessageRow("msg:u2", 1, "u2", "hello", 0, false)],
                Counters: TranscriptCounters.Zero,
                SessionTitle: "Newer session"));

            await File.WriteAllTextAsync(Path.Combine(dir, "corrupt.kept.json"), "{broken json");

            var recent = await store2.ListRecentAsync(maxCount: 10);
            Assert.Equal(2, recent.Count);
            Assert.Equal("sess-newer", recent[0].Scope.SessionId);
            Assert.Equal("sess-older", recent[1].Scope.SessionId);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
