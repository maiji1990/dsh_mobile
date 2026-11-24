using System.Globalization;

namespace DshMobile.Application.Runtime;

/// <summary>Android 历史进程退出原因分类（N-08-04）。</summary>
public enum HistoricalExitKind
{
    None,
    MemoryLimited,
    Other,
}

/// <summary>去敏后的进程退出分类结论：绝不持久化或向上传递原始系统描述文本。</summary>
public sealed record HistoricalExitAssessment(
    HistoricalExitKind Kind,
    DateTimeOffset? TimestampUtc,
    string? SafeDiagnosticKey);

/// <summary>
/// Android 17 <c>ApplicationExitInfo</c> 死因分类器（N-08-04）：
/// 识别 <c>MemoryLimiter:AnonSwap</c> 并仅保留枚举与安全诊断键，不保存原始系统描述。
/// </summary>
public static class HistoricalExitClassifier
{
    public const string SafeMemoryLimiterKey = "android.exit.memory_limiter_anon_swap";

    /// <summary>
    /// 对单条历史退出记录分类；<paramref name="rawDescription"/> 仅在内存做关键字匹配，绝不写入返回对象。
    /// </summary>
    public static HistoricalExitAssessment Classify(
        int reasonCode,
        string? rawDescription,
        DateTimeOffset? timestampUtc = null)
    {
        if (!string.IsNullOrEmpty(rawDescription) &&
            (rawDescription.Contains("MemoryLimiter:AnonSwap", StringComparison.OrdinalIgnoreCase) ||
             rawDescription.Contains("MemoryLimiter", StringComparison.OrdinalIgnoreCase)))
        {
            return new HistoricalExitAssessment(
                HistoricalExitKind.MemoryLimited,
                timestampUtc,
                SafeMemoryLimiterKey);
        }

        if (reasonCode <= 0)
        {
            return new HistoricalExitAssessment(HistoricalExitKind.None, timestampUtc, null);
        }

        return new HistoricalExitAssessment(
            HistoricalExitKind.Other,
            timestampUtc,
            "android.exit.reason_" + reasonCode.ToString(CultureInfo.InvariantCulture));
    }
}
