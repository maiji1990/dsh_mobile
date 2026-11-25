namespace DshMobile.Runtime.Diagnostics;

/// <summary>
/// Node <c>NODE_DEBUG_NATIVE=COMPILE_CACHE</c> 的 stderr 聚合器。Node 的原生日志会包含模块路径、缓存文件名甚至
/// 用户工作区 URL；这里逐行读取后立刻丢弃原文，只保留固定类别的计数，绝不把原文写入日志或 JSONL。
/// </summary>
public sealed class CompileCacheDebugAggregator
{
    /// <summary>Node 原生调试分类名；只在本聚合轮次设置。</summary>
    public const string DebugFlag = "COMPILE_CACHE";

    private const string ReadingMarker = "[compile cache] reading cache from";
    private const string HitMarker = " success, size=";
    private const string RenameMarker = "[compile cache] Renaming ";
    private const string RejectMarker = "was rejected";

    public int Readings { get; private set; }

    public int Hits { get; private set; }

    public int Writes { get; private set; }

    public int Rejects { get; private set; }

    public int ReadFailures { get; private set; }

    public int Misses => Math.Max(0, Readings - Hits);

    public double? HitRate => Readings == 0 ? null : Math.Round((double)Hits / Readings, 4, MidpointRounding.AwayFromZero);

    /// <summary>消费一行 stderr；只做固定子串计数。行内容不会被保存、拼接或写回。</summary>
    public void Observe(string line)
    {
        if (string.IsNullOrEmpty(line) || !line.Contains("[compile cache]", StringComparison.Ordinal))
        {
            return;
        }
        // Node 的 “reading cache from ...” 与随后的 “ success, size=...” 可能在同一行（原生日志第一段不带换行），
        // 因此不能匹配一个标记就提前返回：同一行必须同时计入 reading 与 hit。
        if (line.Contains(ReadingMarker, StringComparison.Ordinal))
        {
            Readings++;
        }
        if (line.Contains(HitMarker, StringComparison.Ordinal))
        {
            Hits++;
        }
        if (line.Contains(RenameMarker, StringComparison.Ordinal))
        {
            // Renaming 发生在临时文件写入成功之后；把它作为“已提交写入尝试”的聚合计数。
            Writes++;
        }
        if (line.Contains(RejectMarker, StringComparison.Ordinal))
        {
            Rejects++;
        }
        if (line.Contains("reading header failed", StringComparison.Ordinal)
            || line.Contains("magic number mismatch", StringComparison.Ordinal)
            || line.Contains("code size mismatch", StringComparison.Ordinal)
            || line.Contains("code hash mismatch", StringComparison.Ordinal)
            || line.Contains("cache size mismatch", StringComparison.Ordinal)
            || line.Contains("cache hash mismatch", StringComparison.Ordinal))
        {
            ReadFailures++;
        }
    }

    public CompileCacheDebugCounts Counts() => new(Readings, Hits, Misses, Writes, Rejects, ReadFailures, HitRate);
}

/// <summary>COMPILE_CACHE 聚合计数；只有数字。</summary>
public sealed record CompileCacheDebugCounts(
    int Readings,
    int Hits,
    int Misses,
    int Writes,
    int Rejects,
    int ReadFailures,
    double? HitRate);
