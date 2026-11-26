namespace DshMobile.Core.Files;

/// <summary>
/// Unified Diff 审查统计（UI-S5）：新增行数、删除行数与 Hunk 区块数，全部由已解析的
/// <see cref="GitDiffLine"/> 得出，不解析原始文本、不做任何猜测。
/// </summary>
public sealed record UnifiedDiffStats(int AddedLines, int RemovedLines, int HunkCount)
{
    public static readonly UnifiedDiffStats Empty = new(0, 0, 0);

    /// <summary>新增 + 删除行数（用于副标题的变更规模提示）。</summary>
    public int ChangedLines => AddedLines + RemovedLines;
}

/// <summary>清洗后的 Diff 审查视图数据：行列表已去噪、Hunk 头已规范化，UI 线程零解析直接绑定。</summary>
public sealed record UnifiedDiffReviewResult(IReadOnlyList<GitDiffLine> Lines, UnifiedDiffStats Stats)
{
    public static readonly UnifiedDiffReviewResult Empty = new([], UnifiedDiffStats.Empty);

    public bool IsEmpty => Lines.Count == 0;
}

/// <summary>
/// Diff 清洗与统计辅助类（UI-S5，纯 C#、零 Android 引用，供 <c>DshMobile.Core.Tests</c> 直接单测）：
/// <list type="number">
/// <item>过滤 <c>diff --git</c>、<c>index …</c>、<c>--- a/…</c>、<c>+++ b/…</c>、<c>old/new file mode</c> 等
/// git 元数据噪音行，只保留可读的正文与 Hunk 分隔条；</item>
/// <item>把 Hunk 头规范化为单一 <c>@@</c> 前缀（杜绝 UI 标记列叠加产生的 <c>@@@@ -1 +1 @@</c>）；</item>
/// <item>按行类别精确统计 <c>+N</c> / <c>-M</c>。</item>
/// </list>
/// 只依据 <see cref="GitDiffLineKind"/> 判断类别，因此 Hunk 内以 <c>---</c> / <c>+++</c> 开头的真实
/// 删除/新增正文（类别为 <see cref="GitDiffLineKind.Removed"/> / <see cref="GitDiffLineKind.Added"/>）
/// 不会被当成文件头噪音删除。
/// </summary>
public static class UnifiedDiffReview
{
    /// <summary>git 输出中仅用于描述文件变化的元数据前缀（区分大小写，与 git 输出一致）。</summary>
    private static readonly string[] GitMetadataPrefixes =
    [
        "diff --git ",
        "diff --cc ",
        "diff --combined ",
        "index ",
        "--- ",
        "+++ ",
        "new file mode ",
        "deleted file mode ",
        "old mode ",
        "new mode ",
        "similarity index ",
        "dissimilarity index ",
        "rename from ",
        "rename to ",
        "copy from ",
        "copy to ",
        "\\ No newline at end of file",
        "Binary files ",
        "GIT binary patch",
    ];

    /// <summary>判断一行 git 元数据是否只是噪音（文件头 / index / 权限 / 重命名 / 二进制声明），应从审查视图隐藏。</summary>
    public static bool IsGitMetadataNoise(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return true;
        }
        var text = content.TrimStart();
        foreach (var prefix in GitMetadataPrefixes)
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 把 Hunk 头规范化为单一 <c>@@</c> 前缀：当上游或旧 UI 已经拼出 <c>@@@@ -1 +1 @@</c> 时收敛为
    /// <c>@@ -1 +1 @@</c>；合法输入（<c>@@ -10,3 +10,4 @@</c> 或组合 diff 的 <c>@@@ … @@@</c>）保持原样。
    /// </summary>
    public static string NormalizeHunkHeader(string? content)
    {
        var text = (content ?? string.Empty).Trim();
        while (text.StartsWith("@@@@", StringComparison.Ordinal))
        {
            text = text[2..];
        }
        return text;
    }

    /// <summary>去噪并规范化 Hunk 头；非 Hunk 的 <see cref="GitDiffLineKind.Meta"/> 噪音行被丢弃。</summary>
    public static IReadOnlyList<GitDiffLine> Clean(IReadOnlyList<GitDiffLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var cleaned = new List<GitDiffLine>(lines.Count);
        foreach (var line in lines)
        {
            switch (line.Kind)
            {
                case GitDiffLineKind.HunkHeader:
                    // Hunk 分隔条不带符号列；正文只保留单一 "@@" 前缀。
                    cleaned.Add(line with
                    {
                        Marker = string.Empty,
                        Content = NormalizeHunkHeader(line.Content),
                    });
                    break;
                case GitDiffLineKind.Meta when IsGitMetadataNoise(line.Content):
                    // 文件头噪音：隐藏，但正文 Meta（未跟踪文件提示、截断原因等）继续展示。
                    break;
                default:
                    cleaned.Add(line);
                    break;
            }
        }
        return cleaned;
    }

    /// <summary>统计新增 / 删除 / Hunk 数量（应在 <see cref="Clean"/> 结果或原始解析结果上调用均可）。</summary>
    public static UnifiedDiffStats Count(IReadOnlyList<GitDiffLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var added = 0;
        var removed = 0;
        var hunks = 0;
        foreach (var line in lines)
        {
            switch (line.Kind)
            {
                case GitDiffLineKind.Added:
                    added++;
                    break;
                case GitDiffLineKind.Removed:
                    removed++;
                    break;
                case GitDiffLineKind.HunkHeader:
                    hunks++;
                    break;
            }
        }
        return new UnifiedDiffStats(added, removed, hunks);
    }

    /// <summary>一次性完成清洗与统计，供 Diff 审查页与单测共用。</summary>
    public static UnifiedDiffReviewResult Build(IReadOnlyList<GitDiffLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var cleaned = Clean(lines);
        return new UnifiedDiffReviewResult(cleaned, Count(cleaned));
    }
}
