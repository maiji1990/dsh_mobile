using System.Text;
using System.Text.Json;
using DshMobile.Agent.Engines;
using DshMobile.Agent.Presentation;

namespace DshMobile.App.Chat;

/// <summary>工具调用的人类可读类别，用于分组摘要与操作描述。</summary>
internal enum ToolCategory
{
    /// <summary>读取文件类工具。</summary>
    Read,

    /// <summary>搜索 / 列举类工具。</summary>
    Search,

    /// <summary>写入或编辑文件类工具。</summary>
    Edit,

    /// <summary>执行命令类工具。</summary>
    Command,

    /// <summary>其余无法归类的工具。</summary>
    Other,
}

/// <summary>流式 Live 指示行的状态种类；本地化文本由 Adapter 从 <c>strings.xml</c> 读取。</summary>
internal enum LiveStatusKind
{
    /// <summary>正在思考（模型推理中）。</summary>
    Thinking,

    /// <summary>正在执行（工具运行中）。</summary>
    Executing,
}

/// <summary>
/// 同一 Turn 内连续多个工具调用聚合后的时间线项。
/// <c>ItemId</c> 形如 <c>toolgrp:{turnSeq}:{firstCallId}</c>，在工具流式推进时保持稳定。
/// </summary>
internal sealed record ToolGroupTimelineItem(
    string ItemId,
    long TurnSeq,
    IReadOnlyList<ToolCallRow> Tools,
    IReadOnlyDictionary<ToolCategory, int> CategoryCounts,
    bool HasActive,
    bool HasError,
    IReadOnlyList<string> WorkspaceFiles,
    bool HasEditOperation) : ChatTimelineItem(ItemId);

/// <summary>助手 Turn 末尾的元信息 / 复制入口；仅在存在真实耗时或可复制正文时生成。</summary>
internal sealed record ReplyFooterTimelineItem(
    string ItemId,
    long TurnSeq,
    long? DurationMs,
    string CopyText) : ChatTimelineItem(ItemId);

/// <summary>流式产生中的状态指示行；具体文案由 Adapter 按 <see cref="Kind"/> 本地化。</summary>
internal sealed record LiveStatusTimelineItem(
    string ItemId,
    LiveStatusKind Kind) : ChatTimelineItem(ItemId);

/// <summary>
/// 将 <see cref="ChatSnapshot"/> 投影为 RecyclerView 时间线列表项（UI-S2 / A1）：
/// 连续工具调用聚合、已完成 Turn 的 Footer、流式 Live 指示行，以及待处理审批 / 问答卡片。
/// 本类只做纯托管投影，不触碰 Android 视图与资源。
/// </summary>
internal static class ChatTimelineProjector
{
    /// <summary>摘要文本的固定展示顺序，保证同一分组在不同帧之间文案稳定。</summary>
    private static readonly ToolCategory[] SummaryOrder =
    [
        ToolCategory.Read,
        ToolCategory.Search,
        ToolCategory.Edit,
        ToolCategory.Command,
        ToolCategory.Other,
    ];

    /// <summary>
    /// 执行完整投影。行顺序保持不变，只把同一 Turn 内连续的 <see cref="ToolCallRow"/> 替换为
    /// <see cref="ToolGroupTimelineItem"/>，并在 Turn 末尾追加 Footer、在末尾追加 Live 指示行。
    /// 审批 / 问答卡由固定 RequestSlot 呈现，不进入时间线。
    /// </summary>
    public static IReadOnlyList<ChatTimelineItem> Project(ChatSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var rows = snapshot.Rows;
        var items = new List<ChatTimelineItem>(rows.Count + 2);
        var runningPhase = snapshot.Phase is RunPhase.Starting
            or RunPhase.Streaming
            or RunPhase.WaitingApproval
            or RunPhase.WaitingQuestion;

        // 处于运行中时，最后一个包含助手输出的 Turn 仍可能继续追加内容，因此不能提前结算 Footer。
        long? lastContentTurnSeq = null;
        foreach (var row in rows)
        {
            if (HasAssistantOutput(row))
            {
                lastContentTurnSeq = row.TurnSeq;
            }
        }

        // 按行顺序切分连续的 Turn 块（快照行本身按 TurnSeq 单调排列）。
        var index = 0;
        while (index < rows.Count)
        {
            var turnSeq = rows[index].TurnSeq;
            var turnEnd = index;
            while (turnEnd < rows.Count && rows[turnEnd].TurnSeq == turnSeq)
            {
                turnEnd++;
            }

            var hasAssistantOutput = false;
            var copyBlocks = new List<string>();
            var cursor = index;
            while (cursor < turnEnd)
            {
                if (rows[cursor] is ToolCallRow)
                {
                    var tools = new List<ToolCallRow>();
                    while (cursor < turnEnd && rows[cursor] is ToolCallRow tool)
                    {
                        tools.Add(tool);
                        cursor++;
                    }

                    hasAssistantOutput = true;
                    items.Add(BuildToolGroup(turnSeq, tools));
                    continue;
                }

                var row = rows[cursor];
                if (row is MarkdownBlockRow markdown)
                {
                    hasAssistantOutput = true;
                    if (!string.IsNullOrWhiteSpace(markdown.Markdown))
                    {
                        copyBlocks.Add(markdown.Markdown.Trim());
                    }
                }
                else if (row is ReasoningBlockRow or CommandRow)
                {
                    hasAssistantOutput = true;
                }

                items.Add(new ChatRowTimelineItem(row, snapshot.Revision));
                cursor++;
            }

            var isUnfinishedCurrentTurn = runningPhase && lastContentTurnSeq == turnSeq;
            if (hasAssistantOutput && !isUnfinishedCurrentTurn)
            {
                var durationMs = ResolveTurnDuration(snapshot, turnSeq);
                var copyText = string.Join("\n\n", copyBlocks);
                if (durationMs is not null || copyText.Length > 0)
                {
                    items.Add(new ReplyFooterTimelineItem(
                        $"footer:{turnSeq}",
                        turnSeq,
                        durationMs,
                        copyText));
                }
            }

            index = turnEnd;
        }

        // 流式 Live 指示行紧随已渲染内容。
        if (snapshot.Phase is RunPhase.Starting or RunPhase.Streaming)
        {
            items.Add(new LiveStatusTimelineItem("live:status", ResolveLiveStatus(snapshot)));
        }

        return items;
    }

    /// <summary>根据工具名判断其人类可读类别；规则固定且忽略大小写。</summary>
    public static ToolCategory ClassifyTool(string? toolName)
    {
        var name = toolName?.Trim() ?? string.Empty;
        if (name.Contains("read", StringComparison.OrdinalIgnoreCase)
            || name.Contains("view", StringComparison.OrdinalIgnoreCase)
            || name.Contains("cat", StringComparison.OrdinalIgnoreCase))
        {
            return ToolCategory.Read;
        }

        if (name.Contains("glob", StringComparison.OrdinalIgnoreCase)
            || name.Contains("grep", StringComparison.OrdinalIgnoreCase)
            || name.Contains("find", StringComparison.OrdinalIgnoreCase)
            || name.Contains("list", StringComparison.OrdinalIgnoreCase)
            || name.Contains("search", StringComparison.OrdinalIgnoreCase))
        {
            return ToolCategory.Search;
        }

        if (name.Contains("edit", StringComparison.OrdinalIgnoreCase)
            || name.Contains("write", StringComparison.OrdinalIgnoreCase)
            || name.Contains("patch", StringComparison.OrdinalIgnoreCase))
        {
            return ToolCategory.Edit;
        }

        if (string.Equals(name, "sh", StringComparison.OrdinalIgnoreCase)
            || name.Contains("bash", StringComparison.OrdinalIgnoreCase)
            || name.Contains("shell", StringComparison.OrdinalIgnoreCase)
            || name.Contains("exec", StringComparison.OrdinalIgnoreCase))
        {
            return ToolCategory.Command;
        }

        return ToolCategory.Other;
    }

    /// <summary>生成工具分组摘要，例如 <c>读取 3 个文件 · 编辑 1 个文件 · 运行 2 条命令</c>；零计数不展示。</summary>
    public static string BuildGroupSummaryText(IReadOnlyDictionary<ToolCategory, int> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);

        var parts = new List<string>(SummaryOrder.Length);
        foreach (var category in SummaryOrder)
        {
            if (!counts.TryGetValue(category, out var count) || count <= 0)
            {
                continue;
            }

            parts.Add(category switch
            {
                ToolCategory.Read => $"读取 {count} 个文件",
                ToolCategory.Search => $"搜索 {count} 次",
                ToolCategory.Edit => $"编辑 {count} 个文件",
                ToolCategory.Command => $"运行 {count} 条命令",
                _ => $"执行 {count} 个工具",
            });
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// 汇总工具组内所有真实耗时的毫秒数；没有任何有效耗时时返回 <c>null</c>，
    /// 由界面决定隐藏耗时文本，绝不合成估算值。
    /// </summary>
    public static long? SumToolDurationsMs(IReadOnlyList<ToolCallRow> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var total = 0L;
        var hasDuration = false;
        foreach (var tool in tools)
        {
            if (tool.DurationMs is { } duration && duration > 0)
            {
                total += duration;
                hasDuration = true;
            }
        }
        return hasDuration ? total : null;
    }

    /// <summary>
    /// 生成工具调用的单行人类可读摘要。参数无法解析或本身是裸 JSON 时，
    /// 退化为“运行命令 / 读取文件 / 搜索内容 / 编辑文件 / 工具名”，绝不把 <c>{...}</c> 直接展示给用户。
    /// </summary>
    public static string FormatHumanReadableToolStep(ToolCallRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var summary = ChatDisplayFormatter.FormatToolCallSummary(row.ToolName, row.ArgumentsJson);
        var candidate = summary.SummaryLine?.Trim();
        var category = ClassifyTool(row.ToolName);
        if (!string.IsNullOrWhiteSpace(candidate)
            && !IsRawJsonLike(candidate)
            && !string.Equals(candidate, row.ArgumentsJson?.Trim(), StringComparison.Ordinal))
        {
            var verb = category switch
            {
                ToolCategory.Read => "读取",
                ToolCategory.Search => "搜索",
                ToolCategory.Edit => "编辑",
                ToolCategory.Command => "运行",
                _ => "执行",
            };
            // 命令摘要本身已带 "$ " 提示符，中文动词后不再保留，避免出现“运行 $ ls”这类重复符号。
            if (category == ToolCategory.Command && candidate.StartsWith("$ ", StringComparison.Ordinal))
            {
                candidate = candidate[2..].TrimStart();
            }
            if (candidate.StartsWith(verb, StringComparison.Ordinal))
            {
                return candidate;
            }
            return $"{verb} {candidate}";
        }

        return category switch
        {
            ToolCategory.Read => "读取文件",
            ToolCategory.Search => "搜索内容",
            ToolCategory.Edit => "编辑文件",
            ToolCategory.Command => "运行命令",
            _ => string.IsNullOrWhiteSpace(row.ToolName) ? "执行工具" : row.ToolName.Trim(),
        };
    }

    /// <summary>
    /// 生成展开详情使用的轻量键值参数文本（每行 <c>key: value</c>）。
    /// 若只有一个字段且该值已出现在标题 / 摘要中（例如单一路径、命令或 pattern），
    /// 则返回空字符串以省略参数框，避免重复展示裸 JSON。
    /// </summary>
    public static string FormatBeautifiedToolArguments(ToolCallRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (string.IsNullOrWhiteSpace(row.ArgumentsJson))
        {
            return string.Empty;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(row.ArgumentsJson);
        }
        catch (JsonException)
        {
            // 非 JSON（历史截断或宽松文本）没有可读的键值结构，交给输出区展示，不再伪装成 JSON。
            return string.Empty;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            var properties = root.EnumerateObject().ToList();
            if (properties.Count == 0)
            {
                return string.Empty;
            }

            var summary = ChatDisplayFormatter.FormatToolCallSummary(row.ToolName, row.ArgumentsJson);
            if (properties.Count == 1
                && IsArgumentAlreadyInSummary(properties[0], summary.HeaderLabel, summary.SummaryLine))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(properties.Count * 24);
            foreach (var property in properties)
            {
                if (builder.Length > 0)
                {
                    builder.Append('\n');
                }
                builder.Append(property.Name);
                builder.Append(": ");
                builder.Append(FormatArgumentValue(property.Value));
            }
            return builder.ToString();
        }
    }

    /// <summary>按行截断文本（默认 12 行），超出部分以单独一行省略号标记。</summary>
    public static string TruncateLines(string text, int maxLines = 12)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLines);

        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalized.Split('\n');
        if (lines.Length <= maxLines)
        {
            return normalized;
        }

        var builder = new StringBuilder();
        for (var i = 0; i < maxLines; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }
            builder.Append(lines[i]);
        }
        builder.Append("\n…");
        return builder.ToString();
    }

    private static ToolGroupTimelineItem BuildToolGroup(long turnSeq, List<ToolCallRow> tools)
    {
        var counts = new Dictionary<ToolCategory, int>
        {
            [ToolCategory.Read] = 0,
            [ToolCategory.Search] = 0,
            [ToolCategory.Edit] = 0,
            [ToolCategory.Command] = 0,
            [ToolCategory.Other] = 0,
        };
        var workspaceFiles = new List<string>();
        var seenFiles = new HashSet<string>(StringComparer.Ordinal);
        var hasActive = false;
        var hasError = false;

        foreach (var tool in tools)
        {
            var category = ClassifyTool(tool.ToolName);
            counts[category]++;

            hasActive |= tool.Phase is ToolPhase.Pending or ToolPhase.Running;
            hasError |= tool.Phase is ToolPhase.Error || tool.IsError;

            if (WorkspacePathExtractor.TryExtractWorkspaceFilePath(tool.ArgumentsJson) is { } path
                && seenFiles.Add(path))
            {
                workspaceFiles.Add(path);
            }
        }

        var firstTool = tools[0];
        var firstId = string.IsNullOrWhiteSpace(firstTool.CallId) ? firstTool.ItemId : firstTool.CallId;
        return new ToolGroupTimelineItem(
            $"toolgrp:{turnSeq}:{firstId}",
            turnSeq,
            tools,
            counts,
            hasActive,
            hasError,
            workspaceFiles,
            counts[ToolCategory.Edit] > 0);
    }

    private static LiveStatusKind ResolveLiveStatus(ChatSnapshot snapshot)
    {
        var hasActiveTool = false;
        var hasUnsettledReasoning = false;
        foreach (var row in snapshot.Rows)
        {
            if (row is ToolCallRow { Phase: ToolPhase.Pending or ToolPhase.Running })
            {
                hasActiveTool = true;
            }
            else if (row is ReasoningBlockRow { IsSettled: false })
            {
                hasUnsettledReasoning = true;
            }
        }

        if (hasActiveTool)
        {
            return LiveStatusKind.Executing;
        }

        return hasUnsettledReasoning || snapshot.Phase == RunPhase.Starting
            ? LiveStatusKind.Thinking
            : LiveStatusKind.Executing;
    }

    private static long? ResolveTurnDuration(ChatSnapshot snapshot, long turnSeq)
    {
        if (snapshot.TurnDurationsMs is { } durations
            && durations.TryGetValue(turnSeq, out var durationMs)
            && durationMs >= 0)
        {
            return durationMs;
        }

        return null;
    }

    private static bool HasAssistantOutput(ChatRow row) =>
        row is MarkdownBlockRow or ReasoningBlockRow or ToolCallRow or CommandRow;

    private static bool IsRawJsonLike(string value)
    {
        var trimmed = value.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }

    /// <summary>判断单字段参数是否已经由标题 / 摘要表达，避免展开后重复打印裸 JSON。</summary>
    private static bool IsArgumentAlreadyInSummary(JsonProperty property, string? header, string? summaryLine)
    {
        var value = FormatArgumentValue(property.Value);
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }
        if (value.Contains('\n'))
        {
            return false;
        }
        if (ContainsToken(header, value) || ContainsToken(summaryLine, value))
        {
            return true;
        }

        var tail = ExtractValueTail(value);
        return tail.Length >= 2
            && (ContainsToken(header, tail) || ContainsToken(summaryLine, tail));
    }

    private static bool ContainsToken(string? haystack, string needle) =>
        !string.IsNullOrWhiteSpace(haystack)
        && haystack.Contains(needle, StringComparison.Ordinal);

    private static string ExtractValueTail(string value)
    {
        var normalized = value.Replace('\\', '/').Trim().TrimEnd('/');
        var index = normalized.LastIndexOf('/');
        return index >= 0 && index + 1 < normalized.Length ? normalized[(index + 1)..] : normalized;
    }

    private static string FormatArgumentValue(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var text = value.GetString() ?? string.Empty;
                return text.Replace("\r", string.Empty, StringComparison.Ordinal)
                    .Replace("\n", "\\n", StringComparison.Ordinal)
                    .Trim();
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return value.GetRawText();
            default:
                return CollapseWhitespace(value.GetRawText());
        }
    }

    private static string CollapseWhitespace(string raw)
    {
        var builder = new StringBuilder(raw.Length);
        var pendingSpace = false;
        foreach (var ch in raw)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(ch);
        }
        return builder.ToString();
    }
}
