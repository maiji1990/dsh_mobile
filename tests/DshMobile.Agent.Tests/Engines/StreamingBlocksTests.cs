using DshMobile.Agent.Engines;

namespace DshMobile.Agent.Tests.Engines;

public sealed class StreamingBlocksTests
{
    [Fact]
    public void Append_SealsClosedBlocksAndHardSlicesLongOpenCodeFenceAt16Lines()
    {
        var blocks = new StreamingBlocks();
        blocks.Append("# Heading\n\n- item 1\n  - nested item\n\n```csharp\n");
        for (var i = 1; i <= 40; i++)
        {
            blocks.Append($"var x{i} = {i};\n");
        }

        // Code fence is STILL unclosed, yet >16 lines forced settled slices!
        Assert.True(blocks.SettledBlocks.Count >= 4);
        Assert.NotNull(blocks.TailBlock);
        Assert.True(blocks.CurrentTailLineCount <= StreamingBlocks.MaxTailLines);

        var codeSlices = blocks.SettledBlocks.Where(b => b.Markdown.StartsWith("```csharp", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(codeSlices);
        Assert.All(codeSlices, slice =>
        {
            Assert.EndsWith("```", slice.Markdown.TrimEnd(), StringComparison.Ordinal);
        });
        Assert.True(blocks.TailBlock!.ContinuedFromPrevious);

        var nestedList = blocks.SettledBlocks.Single(b => b.Markdown.Contains("nested item", StringComparison.Ordinal));
        Assert.Equal(2, nestedList.ListDepth);

        blocks.Append("```\n");
        blocks.FlushFinal();
        Assert.Null(blocks.TailBlock);
    }
}
