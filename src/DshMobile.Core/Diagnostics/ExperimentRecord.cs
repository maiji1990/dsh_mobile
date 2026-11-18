using System.Text;
using System.Text.Json;

namespace DshMobile.Core.Diagnostics;

/// <summary>
/// One experiment run (E1-E4) as a single JSON line. Observability/diagnostic only: no token, header, URL query, prompt,
/// environment value or user path may ever land in these fields (docs/STARTUP_EXPERIMENTS.md). Metrics that Android
/// cannot read reliably stay <c>null</c> - they are never filled with a guess (AGENTS.md rule 5).
/// </summary>
public sealed record ExperimentRecord
{
    public const int Schema = 1;

    public required string Experiment { get; init; }

    public required string Variant { get; init; }

    /// <summary><c>exit</c> (host call to process exit) or <c>web</c> (host call to HTTP ready). Never added together.</summary>
    public required string Kind { get; init; }

    public required string RunId { get; init; }

    public required int Iteration { get; init; }

    /// <summary>The endpoint the measurement stops at: process exit, or the first HTTP status.</summary>
    public double? DurationMs { get; init; }

    /// <summary><c>Process.Start</c> alone, separated from the guest work.</summary>
    public double? SpawnMs { get; init; }

    /// <summary>Time until the guest printed its fixed marker; null when the variant has no marker or it never appeared.</summary>
    public double? GuestFirstMarkerMs { get; init; }

    public double? HttpReadyMs { get; init; }

    public int? HttpStatus { get; init; }

    public double? UserCpuMs { get; init; }

    public double? SystemCpuMs { get; init; }

    public long? PeakRssKb { get; init; }

    public int? ExitCode { get; init; }

    public bool TimedOut { get; init; }

    /// <summary><c>ok</c> | <c>failed</c> | <c>timeout</c> | <c>refused</c> | <c>setup-error</c>.</summary>
    public required string Result { get; init; }

    /// <summary><c>off</c> | <c>cold</c> | <c>warm</c> | <c>unknown</c>.</summary>
    public string CacheMode { get; init; } = "unknown";

    public int? CacheFileCount { get; init; }

    public long? CacheBytes { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>A short fixed code/word (see <see cref="StartupTrace"/>); never free text.</summary>
    public string? Note { get; init; }

    public const string Ok = "ok";
    public const string Failed = "failed";
    public const string Timeout = "timeout";
    public const string Refused = "refused";
    public const string SetupError = "setup-error";

    public const string KindExit = "exit";
    public const string KindWeb = "web";

    /// <summary>The one line written to the JSONL sink; same fields, stable order, no reflection.</summary>
    public string ToJsonLine()
    {
        using var buffer = new MemoryStream(512);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", Schema);
            WriteText(w, "experiment", Experiment);
            WriteText(w, "variant", Variant);
            WriteText(w, "kind", Kind);
            WriteText(w, "runId", RunId);
            w.WriteNumber("iteration", Iteration);
            WriteNumber(w, "durationMs", DurationMs);
            WriteNumber(w, "spawnMs", SpawnMs);
            WriteNumber(w, "guestFirstMarkerMs", GuestFirstMarkerMs);
            WriteNumber(w, "httpReadyMs", HttpReadyMs);
            WriteInt(w, "httpStatus", HttpStatus);
            WriteNumber(w, "userCpuMs", UserCpuMs);
            WriteNumber(w, "systemCpuMs", SystemCpuMs);
            WriteLong(w, "peakRssKb", PeakRssKb);
            WriteInt(w, "exitCode", ExitCode);
            w.WriteBoolean("timedOut", TimedOut);
            WriteText(w, "result", Result);
            WriteText(w, "cacheMode", CacheMode);
            WriteInt(w, "cacheFileCount", CacheFileCount);
            WriteLong(w, "cacheBytes", CacheBytes);
            w.WriteString("startedAt", StartedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture));
            if (Note is null)
            {
                w.WriteNull("note");
            }
            else
            {
                WriteText(w, "note", Note);
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteText(Utf8JsonWriter w, string name, string value) =>
        w.WriteString(name, IsToken(value) ? value : StartupTrace.Redacted);

    private static void WriteNumber(Utf8JsonWriter w, string name, double? value)
    {
        if (value is { } v && double.IsFinite(v))
        {
            w.WriteNumber(name, Math.Round(v, 3, MidpointRounding.AwayFromZero));
        }
        else
        {
            w.WriteNull(name);
        }
    }

    private static void WriteInt(Utf8JsonWriter w, string name, int? value)
    {
        if (value is { } v)
        {
            w.WriteNumber(name, v);
        }
        else
        {
            w.WriteNull(name);
        }
    }

    private static void WriteLong(Utf8JsonWriter w, string name, long? value)
    {
        if (value is { } v)
        {
            w.WriteNumber(name, v);
        }
        else
        {
            w.WriteNull(name);
        }
    }

    /// <summary>Short ASCII token; free text (a path, a prompt, a token) can never match.</summary>
    private static bool IsToken(string text)
    {
        if (text.Length is 0 or > 48)
        {
            return false;
        }
        foreach (var ch in text)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-'))
            {
                return false;
            }
        }
        return true;
    }
}
