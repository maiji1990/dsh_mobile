using System.Collections.Concurrent;
using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DshMobile.Agent.Rendering;

public enum MarkdownSpanKind
{
    Plain,
    Strong,
    Emphasis,
    Strikethrough,
    InlineCode,
    Link,
}

public sealed record MarkdownInlineSpan(
    int Start,
    int Length,
    MarkdownSpanKind Kind,
    string? Url = null);

public enum MarkdownSegmentKind
{
    Paragraph,
    Heading,
    Quote,
    ListItem,
    CodeBlock,
    DiffBlock,
    TableBlock,
    ThematicBreak,
}

public abstract record MarkdownSegment(MarkdownSegmentKind Kind);

public sealed record MarkdownTextSegment(
    MarkdownSegmentKind Kind,
    string PlainText,
    IReadOnlyList<MarkdownInlineSpan> Spans,
    int HeadingLevel = 0,
    int ListDepth = 0,
    string? ListPrefix = null) : MarkdownSegment(Kind);

public sealed record MarkdownCodeSegment(
    string? Language,
    string Code) : MarkdownSegment(MarkdownSegmentKind.CodeBlock);

public enum DiffLineKind
{
    Context,
    Added,
    Removed,
    HunkHeader,
}

public sealed record MarkdownDiffLine(DiffLineKind LineKind, string Text);

public sealed record MarkdownDiffSegment(
    IReadOnlyList<MarkdownDiffLine> Lines) : MarkdownSegment(MarkdownSegmentKind.DiffBlock);

public sealed record MarkdownTableSegment(
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows) : MarkdownSegment(MarkdownSegmentKind.TableBlock);

public sealed record MarkdownThematicBreakSegment() : MarkdownSegment(MarkdownSegmentKind.ThematicBreak);

public sealed record MarkdownRenderResult(
    string ItemId,
    long Revision,
    string ContentFingerprint,
    IReadOnlyList<MarkdownSegment> Segments);

/// <summary>
/// 将 <see cref="MarkdownParser"/> 产出的 Markdig AST 转换为轻量纯托管 IR（N-04-02），
/// 并按 <c>ItemId</c> 与单调版本号缓存后台解析结果，确保旧任务不会覆盖新版本。
/// </summary>
public sealed class MarkdownRenderCache
{
    private const int DefaultMaxEntries = 512;

    private readonly int _maxEntries;
    private readonly ConcurrentDictionary<string, MarkdownRenderResult> _byFingerprint = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _latestRevisionByItem = new(StringComparer.Ordinal);
    private readonly Lock _fifoLock = new();
    private readonly Queue<string> _fifo = [];

    public MarkdownRenderCache(int maxEntries = DefaultMaxEntries)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        _maxEntries = maxEntries;
    }

    public static string ComputeFingerprint(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ulong hash = 14695981039346656037UL;
        foreach (var ch in markdown)
        {
            hash ^= ch;
            hash *= 1099511628211UL;
        }
        return $"{markdown.Length}:{hash:x16}";
    }

    public bool TryGetCached(string itemId, string markdown, long revision, out MarkdownRenderResult? result)
    {
        ArgumentException.ThrowIfNullOrEmpty(itemId);
        ArgumentNullException.ThrowIfNull(markdown);
        var fp = ComputeFingerprint(markdown);
        if (_byFingerprint.TryGetValue(fp, out var cached))
        {
            _latestRevisionByItem.AddOrUpdate(itemId, revision, (_, prev) => Math.Max(prev, revision));
            result = cached with { ItemId = itemId, Revision = revision };
            return true;
        }
        result = null;
        return false;
    }

    /// <summary>
    /// 后台解析 Markdown 块并写入缓存；若调用期间该 <paramref name="itemId"/> 已有更高版本提交，则丢弃过期写回。
    /// </summary>
    public async Task<MarkdownRenderResult?> ParseAsync(
        string itemId,
        long revision,
        string markdown,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(itemId);
        ArgumentNullException.ThrowIfNull(markdown);

        _latestRevisionByItem.AddOrUpdate(itemId, revision, (_, prev) => Math.Max(prev, revision));
        var fp = ComputeFingerprint(markdown);
        if (_byFingerprint.TryGetValue(fp, out var cached))
        {
            return IsStale(itemId, revision) ? null : cached with { ItemId = itemId, Revision = revision };
        }

        var segments = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return BuildSegments(markdown);
        }, ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        var built = new MarkdownRenderResult(itemId, revision, fp, segments);
        StoreCached(fp, built);
        return IsStale(itemId, revision) ? null : built;
    }

    public bool IsStale(string itemId, long revision) =>
        _latestRevisionByItem.TryGetValue(itemId, out var latest) && revision < latest;

    private void StoreCached(string fingerprint, MarkdownRenderResult result)
    {
        if (!_byFingerprint.TryAdd(fingerprint, result))
        {
            return;
        }
        lock (_fifoLock)
        {
            _fifo.Enqueue(fingerprint);
            while (_byFingerprint.Count > _maxEntries && _fifo.TryDequeue(out var oldest))
            {
                _byFingerprint.TryRemove(oldest, out _);
            }
        }
    }

    public static IReadOnlyList<MarkdownSegment> BuildSegments(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var doc = MarkdownParser.Parse(markdown);
        var segments = new List<MarkdownSegment>();
        AppendContainerBlocks(doc, segments, listDepth: 0, inQuote: false);
        return segments;
    }

    private static void AppendContainerBlocks(
        ContainerBlock container,
        List<MarkdownSegment> segments,
        int listDepth,
        bool inQuote)
    {
        foreach (var block in container)
        {
            switch (block)
            {
                case HeadingBlock heading:
                {
                    var (text, spans) = ExtractInlines(heading.Inline);
                    segments.Add(new MarkdownTextSegment(
                        MarkdownSegmentKind.Heading,
                        text,
                        spans,
                        HeadingLevel: heading.Level));
                    break;
                }
                case FencedCodeBlock fenced:
                {
                    var lang = string.IsNullOrWhiteSpace(fenced.Info) ? null : fenced.Info.Trim();
                    var code = fenced.Lines.ToString().TrimEnd('\r', '\n');
                    segments.Add(CreateCodeOrDiffSegment(lang, code));
                    break;
                }
                case CodeBlock codeBlock:
                {
                    var code = codeBlock.Lines.ToString().TrimEnd('\r', '\n');
                    segments.Add(CreateCodeOrDiffSegment(null, code));
                    break;
                }
                case Table table:
                {
                    segments.Add(ExtractTable(table));
                    break;
                }
                case ThematicBreakBlock:
                {
                    segments.Add(new MarkdownThematicBreakSegment());
                    break;
                }
                case QuoteBlock quote:
                {
                    AppendContainerBlocks(quote, segments, listDepth, inQuote: true);
                    break;
                }
                case ListBlock list:
                {
                    var order = 1;
                    if (list.IsOrdered && int.TryParse(list.OrderedStart, out var parsedStart) && parsedStart > 0)
                    {
                        order = parsedStart;
                    }
                    foreach (var child in list)
                    {
                        if (child is not ListItemBlock item)
                        {
                            continue;
                        }
                        var prefix = list.IsOrdered ? $"{order++}. " : "• ";
                        AppendListItem(item, segments, listDepth + 1, prefix);
                    }
                    break;
                }
                case ParagraphBlock paragraph:
                {
                    var (text, spans) = ExtractInlines(paragraph.Inline);
                    if (text.Length > 0)
                    {
                        segments.Add(new MarkdownTextSegment(
                            inQuote ? MarkdownSegmentKind.Quote : MarkdownSegmentKind.Paragraph,
                            text,
                            spans));
                    }
                    break;
                }
                case ContainerBlock nested:
                {
                    AppendContainerBlocks(nested, segments, listDepth, inQuote);
                    break;
                }
            }
        }
    }

    private static void AppendListItem(
        ListItemBlock item,
        List<MarkdownSegment> segments,
        int depth,
        string prefix)
    {
        var usedPrefix = false;
        foreach (var child in item)
        {
            switch (child)
            {
                case ParagraphBlock p:
                {
                    var (text, spans) = ExtractInlines(p.Inline);
                    segments.Add(new MarkdownTextSegment(
                        MarkdownSegmentKind.ListItem,
                        text,
                        spans,
                        ListDepth: depth,
                        ListPrefix: usedPrefix ? "  " : prefix));
                    usedPrefix = true;
                    break;
                }
                case ListBlock nestedList:
                {
                    AppendContainerBlocks(nestedList, segments, depth, inQuote: false);
                    break;
                }
                case ContainerBlock nestedContainer:
                {
                    AppendContainerBlocks(nestedContainer, segments, depth, inQuote: false);
                    break;
                }
                case LeafBlock leaf when leaf is FencedCodeBlock or CodeBlock:
                {
                    var lang = (leaf as FencedCodeBlock)?.Info?.Trim();
                    var code = leaf.Lines.ToString().TrimEnd('\r', '\n');
                    segments.Add(CreateCodeOrDiffSegment(string.IsNullOrWhiteSpace(lang) ? null : lang, code));
                    break;
                }
            }
        }
    }

    private static MarkdownSegment CreateCodeOrDiffSegment(string? language, string code)
    {
        if (string.Equals(language, "diff", StringComparison.OrdinalIgnoreCase)
            || string.Equals(language, "patch", StringComparison.OrdinalIgnoreCase))
        {
            var rawLines = code.Split('\n');
            var diffLines = new List<MarkdownDiffLine>(rawLines.Length);
            foreach (var raw in rawLines)
            {
                var line = raw.TrimEnd('\r');
                var kind = line.StartsWith("@@", StringComparison.Ordinal)
                    ? DiffLineKind.HunkHeader
                    : line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal)
                        ? DiffLineKind.Added
                        : line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal)
                            ? DiffLineKind.Removed
                            : DiffLineKind.Context;
                diffLines.Add(new MarkdownDiffLine(kind, line));
            }
            return new MarkdownDiffSegment(diffLines);
        }
        return new MarkdownCodeSegment(language, code);
    }

    private static MarkdownTableSegment ExtractTable(Table table)
    {
        var headers = new List<string>();
        var rows = new List<IReadOnlyList<string>>();
        foreach (var child in table)
        {
            if (child is not TableRow row)
            {
                continue;
            }
            var cells = new List<string>(row.Count);
            foreach (var cellObj in row)
            {
                if (cellObj is TableCell cell)
                {
                    cells.Add(ExtractTableCellText(cell));
                }
            }
            if (row.IsHeader && headers.Count == 0)
            {
                headers.AddRange(cells);
            }
            else
            {
                rows.Add(cells);
            }
        }
        return new MarkdownTableSegment(headers, rows);
    }

    private static string ExtractTableCellText(TableCell cell)
    {
        var sb = new StringBuilder();
        foreach (var child in cell)
        {
            if (child is ParagraphBlock p)
            {
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }
                sb.Append(ExtractInlines(p.Inline).Text);
            }
        }
        return sb.ToString();
    }

    private static (string Text, IReadOnlyList<MarkdownInlineSpan> Spans) ExtractInlines(ContainerInline? container)
    {
        if (container is null)
        {
            return (string.Empty, []);
        }
        var sb = new StringBuilder();
        var spans = new List<MarkdownInlineSpan>();
        AppendInlineChildren(container, sb, spans);
        return (sb.ToString(), spans);
    }

    private static void AppendInlineChildren(ContainerInline container, StringBuilder sb, List<MarkdownInlineSpan> spans)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    sb.Append(lit.Content.AsSpan());
                    break;
                case CodeInline code:
                {
                    var start = sb.Length;
                    sb.Append(code.Content);
                    if (sb.Length > start)
                    {
                        spans.Add(new MarkdownInlineSpan(start, sb.Length - start, MarkdownSpanKind.InlineCode));
                    }
                    break;
                }
                case LineBreakInline lb:
                    sb.Append(lb.IsHard ? '\n' : ' ');
                    break;
                case LinkInline link:
                {
                    var start = sb.Length;
                    if (link.FirstChild is not null)
                    {
                        AppendInlineChildren(link, sb, spans);
                    }
                    else if (!string.IsNullOrEmpty(link.Url))
                    {
                        sb.Append(link.Url);
                    }
                    if (sb.Length > start && !string.IsNullOrWhiteSpace(link.Url))
                    {
                        spans.Add(new MarkdownInlineSpan(start, sb.Length - start, MarkdownSpanKind.Link, link.Url));
                    }
                    break;
                }
                case AutolinkInline auto:
                {
                    var start = sb.Length;
                    sb.Append(auto.Url);
                    if (sb.Length > start)
                    {
                        spans.Add(new MarkdownInlineSpan(start, sb.Length - start, MarkdownSpanKind.Link, auto.Url));
                    }
                    break;
                }
                case EmphasisInline em:
                {
                    var start = sb.Length;
                    AppendInlineChildren(em, sb, spans);
                    if (sb.Length > start)
                    {
                        var kind = em.DelimiterChar == '~'
                            ? MarkdownSpanKind.Strikethrough
                            : em.DelimiterCount >= 2
                                ? MarkdownSpanKind.Strong
                                : MarkdownSpanKind.Emphasis;
                        spans.Add(new MarkdownInlineSpan(start, sb.Length - start, kind));
                    }
                    break;
                }
                case ContainerInline nested:
                    AppendInlineChildren(nested, sb, spans);
                    break;
            }
        }
    }
}
