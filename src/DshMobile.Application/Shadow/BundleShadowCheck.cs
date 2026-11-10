using System.Text.Json;
using System.Text.Json.Serialization;
using DshMobile.Runtime;
using DshMobile.Runtime.Bundle;

namespace DshMobile.Application.Shadow;

public enum ShadowVerdict
{
    /// <summary>Both accepted, or both rejected with the same upstream code.</summary>
    Agree,
    /// <summary>Different outcome: a migration blocker until explained.</summary>
    Diverge,
    /// <summary>The legacy outcome depends on something the C# check does not cover (e.g. SANDBOX_RUNNING, extraction).</summary>
    Inconclusive,
}

public sealed record ShadowReport(
    [property: JsonPropertyName("slice")] string Slice,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("verdict")] ShadowVerdict Verdict,
    [property: JsonPropertyName("legacyOk")] bool LegacyOk,
    [property: JsonPropertyName("legacyCode")] string? LegacyCode,
    [property: JsonPropertyName("csharpOk")] bool CSharpOk,
    [property: JsonPropertyName("csharpCode")] string? CSharpCode,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("csharpMs")] long CSharpMs);

/// <summary>Where shadow reports go: a JSONL file (device evidence, migration/parity) and the diagnostics log.</summary>
public interface IShadowReportSink
{
    void Report(ShadowReport report);
}

public sealed class FileShadowReportSink(string path) : IShadowReportSink
{
    private readonly Lock _gate = new();

    public void Report(ShadowReport report)
    {
        var line = JsonSerializer.Serialize(report, ShadowJsonContext.Default.ShadowReport) + "\n";
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, line);
        }
    }
}

/// <summary>
/// Compares the legacy import outcome with the C# <see cref="RuntimeBundleInspector"/> on the same ZIP (PR-02 shadow).
/// The legacy result is always the one that counts; this only observes.
/// </summary>
public static class BundleShadowCheck
{
    public const string Slice = "runtime.bundle-verify";

    /// <summary>Upstream codes whose cause the inspector re-checks; any other legacy failure is out of its scope.</summary>
    private static readonly HashSet<string> CoveredCodes = new(StringComparer.Ordinal)
    {
        BundleErrorCodes.Empty, BundleErrorCodes.UnsafePath, BundleErrorCodes.NoProfile, BundleErrorCodes.BadProfile,
        BundleErrorCodes.MissingLayer, BundleErrorCodes.Sha256Mismatch,
    };

    public static ShadowReport Compare(RuntimeInstallResult legacy, BundleInspection csharp, long csharpMs, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(csharp);
        var legacyCode = legacy.Ok ? null : legacy.UpstreamCode ?? legacy.Code;
        ShadowVerdict verdict;
        string detail;
        if (legacy.Ok && csharp.Ok)
        {
            verdict = ShadowVerdict.Agree;
            detail = "both accepted; " + string.Join(", ", csharp.Layers.Select(l => $"{l.Layer}={(l.Verified ? "sha256 ok" : "unverified")}"));
        }
        else if (!legacy.Ok && !csharp.Ok)
        {
            verdict = string.Equals(legacyCode, csharp.ErrorCode, StringComparison.Ordinal) ? ShadowVerdict.Agree
                : legacyCode is not null && CoveredCodes.Contains(legacyCode) ? ShadowVerdict.Diverge
                : ShadowVerdict.Inconclusive;
            detail = $"both rejected: legacy={legacyCode} csharp={csharp.ErrorCode}";
        }
        else if (!legacy.Ok)
        {
            // Legacy failed for a reason the read-only check cannot see (sandbox running, disk full, extraction).
            verdict = legacyCode is not null && CoveredCodes.Contains(legacyCode) ? ShadowVerdict.Diverge : ShadowVerdict.Inconclusive;
            detail = $"legacy rejected ({legacyCode}: {legacy.Message}), csharp accepted";
        }
        else
        {
            verdict = ShadowVerdict.Diverge;
            detail = $"legacy accepted, csharp rejected ({csharp.ErrorCode}: {csharp.Message})";
        }
        return new ShadowReport(Slice, at, verdict, legacy.Ok, legacyCode, csharp.Ok, csharp.ErrorCode, detail, csharpMs);
    }
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(ShadowReport))]
internal sealed partial class ShadowJsonContext : JsonSerializerContext;
