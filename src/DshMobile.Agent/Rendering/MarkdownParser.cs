using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;

namespace DshMobile.Agent.Rendering;

/// <summary>
/// Pure-managed Markdown AST parser backed by Markdig (PR-00 Trim/AOT verification baseline).
/// Full block IR and Android Spannable rendering are built in PR-03 / PR-04.
/// </summary>
public static class MarkdownParser
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseAutoLinks()
        .UseEmphasisExtras()
        .Build();

    /// <summary>Parses <paramref name="markdown"/> into a Markdig <see cref="MarkdownDocument"/>.</summary>
    public static MarkdownDocument Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        return Markdown.Parse(markdown, Pipeline);
    }

    /// <summary>Summarizes top-level block kinds for trim-safe smoke verification.</summary>
    public static MarkdownBlockSummary InspectSummary(string markdown)
    {
        var doc = Parse(markdown);
        int headings = 0, codeBlocks = 0, tables = 0, lists = 0, paragraphs = 0;
        foreach (var block in doc)
        {
            switch (block)
            {
                case HeadingBlock:
                    headings++;
                    break;
                case FencedCodeBlock:
                case CodeBlock:
                    codeBlocks++;
                    break;
                case Table:
                    tables++;
                    break;
                case ListBlock:
                    lists++;
                    break;
                case ParagraphBlock:
                    paragraphs++;
                    break;
            }
        }
        return new MarkdownBlockSummary(headings, codeBlocks, tables, lists, paragraphs);
    }
}

/// <summary>Counts of parsed top-level Markdown blocks.</summary>
public sealed record MarkdownBlockSummary(int Headings, int CodeBlocks, int Tables, int Lists, int Paragraphs);
