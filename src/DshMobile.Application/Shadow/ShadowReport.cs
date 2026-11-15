using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshMobile.Application.Shadow;

// Report format of the migration's shadow comparisons (shadow-reports.jsonl). Since PR-10 (ADR-0018) there is no legacy
// implementation left to compare with; the same format carries the C# self-checks that remain, such as the installed-tree
// check after an import (runtime.layer-install), so earlier device evidence and new reports read alike. For such a
// check the "legacy" fields describe the reference side (the installed tree).

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

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(ShadowReport))]
internal sealed partial class ShadowJsonContext : JsonSerializerContext;
