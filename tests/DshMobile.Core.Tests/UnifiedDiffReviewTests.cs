using DshMobile.Core.Files;

namespace DshMobile.Core.Tests;

/// <summary>
/// UI-S5：Diff 清洗 / <c>+N -M</c> 统计 / Hunk 头去重的回归测试，
/// 锁定 <c>@@@@ -1 +1 @@</c> 与 hunk 内 <c>---</c>/<c>+++</c> 正文被误删两个缺陷不再出现。
/// </summary>
public sealed class UnifiedDiffReviewTests
{
    private const string SampleDiff = """
        diff --git a/src/app.cs b/src/app.cs
        index 1111111..2222222 100644
        --- a/src/app.cs
        +++ b/src/app.cs
        @@ -10,3 +10,4 @@ public class App
         context line
        -removed line
        +added line 1
        +added line 2
        """;

    [Fact]
    public void ParseUnifiedDiff_HunkHeader_HasSinglePrefix_AndEmptyMarker()
    {
        var doc = WorkspaceGitService.ParseUnifiedDiff("src/app.cs", SampleDiff);
        var hunk = Assert.Single(doc.Lines, l => l.Kind == GitDiffLineKind.HunkHeader);

        // 根因回归：标记列与正文不得各自再带 "@@"，否则 UI 相邻渲染会拼成 "@@@@ -10,3 +10,4 @@"
        Assert.Equal(string.Empty, hunk.Marker);
        Assert.Equal("@@ -10,3 +10,4 @@ public class App", hunk.Content);
        Assert.DoesNotContain("@@@@", hunk.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeHunkHeader_CollapsesDuplicatedPrefix_ButKeepsValidAndCombinedHeaders()
    {
        Assert.Equal("@@ -1 +1 @@", UnifiedDiffReview.NormalizeHunkHeader("@@@@ -1 +1 @@"));
        Assert.Equal("@@ -1 +1 @@", UnifiedDiffReview.NormalizeHunkHeader("@@@@@@ -1 +1 @@"));
        Assert.Equal("@@ -1 +1 @@", UnifiedDiffReview.NormalizeHunkHeader("  @@ -1 +1 @@  "));
        // 组合 diff（merge）的 "@@@" 是合法格式，不能被当作重复前缀削掉
        Assert.Equal("@@@ -1,2 -1,2 +1,3 @@@", UnifiedDiffReview.NormalizeHunkHeader("@@@ -1,2 -1,2 +1,3 @@@"));
    }

    [Fact]
    public void Build_FiltersGitMetadata_AndCountsAddedRemovedAndHunks()
    {
        var doc = WorkspaceGitService.ParseUnifiedDiff("src/app.cs", SampleDiff);
        var review = UnifiedDiffReview.Build(doc.Lines);

        Assert.Equal(2, review.Stats.AddedLines);
        Assert.Equal(1, review.Stats.RemovedLines);
        Assert.Equal(1, review.Stats.HunkCount);

        // diff --git / index / --- a/ / +++ b/ 四行元数据被隐藏，Hunk 与正文保留
        Assert.Equal(5, review.Lines.Count);
        Assert.DoesNotContain(review.Lines, l => l.Kind == GitDiffLineKind.Meta);
        Assert.DoesNotContain(review.Lines, l => l.Content.StartsWith("diff --git", StringComparison.Ordinal));
        Assert.DoesNotContain(review.Lines, l => l.Content.StartsWith("index ", StringComparison.Ordinal));
        Assert.DoesNotContain(review.Lines, l => l.Content.StartsWith("--- a/", StringComparison.Ordinal));
        Assert.DoesNotContain(review.Lines, l => l.Content.StartsWith("+++ b/", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_KeepsInHunkContentLinesStartingWithTripleDashOrPlus()
    {
        const string diff = """
            diff --git a/notes.txt b/notes.txt
            index 1111111..2222222 100644
            --- a/notes.txt
            +++ b/notes.txt
            @@ -1,2 +1,2 @@
            --- marker like removed content
            +++ marker like added content
            """;

        var doc = WorkspaceGitService.ParseUnifiedDiff("notes.txt", diff);
        var review = UnifiedDiffReview.Build(doc.Lines);

        // 仅文件头两行是元数据；Hunk 内以 "--- " / "+++ " 开头的是真实正文
        Assert.Contains(review.Lines, l => l.Kind == GitDiffLineKind.Removed && l.Content == "-- marker like removed content");
        Assert.Contains(review.Lines, l => l.Kind == GitDiffLineKind.Added && l.Content == "++ marker like added content");
        Assert.Equal(1, review.Stats.AddedLines);
        Assert.Equal(1, review.Stats.RemovedLines);
    }

    [Fact]
    public void Build_OnMetadataOnlyDiff_IsEmptyWithZeroStats()
    {
        var review = UnifiedDiffReview.Build(
        [
            new GitDiffLine(GitDiffLineKind.Meta, null, null, " ", "diff --git a/a b/a"),
            new GitDiffLine(GitDiffLineKind.Meta, null, null, " ", "new file mode 100644"),
        ]);

        Assert.True(review.IsEmpty);
        Assert.Equal(0, review.Stats.AddedLines);
        Assert.Equal(0, review.Stats.RemovedLines);
        Assert.Equal(0, review.Stats.HunkCount);
    }
}
