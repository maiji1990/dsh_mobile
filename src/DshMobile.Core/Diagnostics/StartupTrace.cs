using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DshMobile.Core.Diagnostics;

/// <summary>
/// One startup run's timing trail: a non-secret run id, a monotonic origin (the moment the user asked to start) and a
/// text sink. Observability only - it never changes control flow, never waits, never touches the guest, and every
/// failure of the sink is swallowed so tracing cannot break a start (docs/STARTUP_PERF.md).
/// </summary>
/// <remarks>
/// A record is one line <c>startup-perf {JSON}</c>: <c>schema</c>, <c>runId</c>, <c>stage</c>, <c>elapsedMs</c> (the
/// span), <c>sinceStartMs</c> (from the origin) and <c>result</c>. Detail values are restricted to numbers, booleans and
/// short code-like strings; anything else is written as <c>[redacted]</c>, so a token, URL or prompt cannot leak even by
/// mistake. Times come from one process-wide monotonic clock (<see cref="Stopwatch"/>), are computed on the recording
/// thread and are only formatted when the sink exists.
/// </remarks>
public sealed class StartupTrace
{
    /// <summary>The stable prefix of a trace line; tooling/startup-perf.mjs parses exactly this.</summary>
    public const string LogPrefix = "startup-perf";

    /// <summary>Record format version.</summary>
    public const int Schema = 1;

    public const string Ok = "ok";
    public const string Failed = "failed";
    public const string Canceled = "canceled";
    public const string Busy = "busy";
    public const string Refused = "refused";
    public const string Incomplete = "incomplete";

    /// <summary>What a detail value becomes when it is not a number, a boolean or a code-like string.</summary>
    public const string Redacted = "[redacted]";

    private static readonly StartupTrace None = new("", null, static () => 0d);

    private readonly Action<string>? _sink;
    private readonly Func<double> _clock;
    private readonly double _origin;

    private StartupTrace(string runId, Action<string>? sink, Func<double> clock)
    {
        RunId = runId;
        _sink = sink;
        _clock = clock;
        _origin = clock();
    }

    /// <summary>A trace that records nothing; every call stays a no-op, so call sites do not need branches.</summary>
    public static StartupTrace Disabled => None;

    /// <summary>The run id of this trace (random, non-secret, 12 hex characters).</summary>
    public string RunId { get; }

    /// <summary>False for <see cref="Disabled"/>: no formatting or sink call happens.</summary>
    public bool Enabled => _sink is not null;

    /// <summary>Starts a trace whose origin is now, writing records to <paramref name="sink"/>.</summary>
    /// <param name="sink">Usually the app's own log line writer (Logcat tag <c>DshMobile</c>).</param>
    /// <param name="runId">For tests / a resumed run; null (the normal case) generates a fresh id.</param>
    /// <param name="clock">Monotonic milliseconds; null uses <see cref="Stopwatch"/>.</param>
    public static StartupTrace Start(Action<string> sink, string? runId = null, Func<double>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        return new StartupTrace(runId ?? NewRunId(), sink, clock ?? MonotonicMs);
    }

    /// <summary>A new non-secret run id (12 hex characters).</summary>
    public static string NewRunId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>The current monotonic time in milliseconds from the trace origin (not wall clock).</summary>
    public double Mark() => _clock();

    /// <summary>Milliseconds since the origin, for call sites that cannot use a span.</summary>
    public double SinceStartMs => _clock() - _origin;

    /// <summary>Starts a span; end it exactly once with <see cref="StartupSpan.End"/>.</summary>
    public StartupSpan Measure(string stage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        return new StartupSpan(this, stage, _clock());
    }

    /// <summary>Records a span that started at <paramref name="startedMs"/> and ends now.</summary>
    public void EmitSpan(string stage, double startedMs, string result = Ok, IReadOnlyDictionary<string, object?>? details = null)
        => Emit(stage, startedMs, _clock(), result, details);

    /// <summary>
    /// Records a span that has already ended (<paramref name="elapsedMs"/> measured by the caller). Used where only the
    /// duration is available, e.g. the first output of a process pumped on a pool thread.
    /// </summary>
    public void EmitElapsed(string stage, double elapsedMs, string result = Ok, IReadOnlyDictionary<string, object?>? details = null)
    {
        if (_sink is null)
        {
            return;
        }
        var now = _clock();
        Emit(stage, now - Math.Max(elapsedMs, 0d), now, result, details);
    }

    internal void Emit(string stage, double startedMs, double endedMs, string result, IReadOnlyDictionary<string, object?>? details)
    {
        if (_sink is null)
        {
            return;
        }
        try
        {
            var elapsed = endedMs - startedMs;
            var line = Format(stage, elapsed < 0d ? 0d : elapsed, endedMs - _origin, result, details);
            _sink(line);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A trace must never change the outcome of a start: a broken sink is invisible.
        }
    }

    private string Format(string stage, double elapsedMs, double sinceStartMs, string result, IReadOnlyDictionary<string, object?>? details)
    {
        using var buffer = new MemoryStream(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", Schema);
            w.WriteString("runId", RunId);
            w.WriteString("stage", stage);
            w.WriteNumber("elapsedMs", Round(elapsedMs));
            w.WriteNumber("sinceStartMs", Round(sinceStartMs));
            w.WriteString("result", result);
            w.WriteStartObject("details");
            if (details is not null)
            {
                foreach (var (key, value) in details)
                {
                    WriteDetail(w, key, value);
                }
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return LogPrefix + " " + Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static double Round(double ms) => Math.Round(ms, 3, MidpointRounding.AwayFromZero);

    private static void WriteDetail(Utf8JsonWriter w, string key, object? value)
    {
        if (!IsCode(key, 32))
        {
            return; // keys are code constants; an unexpected key is dropped rather than written
        }
        switch (value)
        {
            case bool b:
                w.WriteBoolean(key, b);
                return;
            case int i:
                w.WriteNumber(key, i);
                return;
            case long l:
                w.WriteNumber(key, l);
                return;
            case short s:
                w.WriteNumber(key, s);
                return;
            case byte bt:
                w.WriteNumber(key, bt);
                return;
            case double d when double.IsFinite(d):
                w.WriteNumber(key, Round(d));
                return;
            case float f when float.IsFinite(f):
                w.WriteNumber(key, Round(f));
                return;
            case decimal m:
                w.WriteNumber(key, (double)m);
                return;
            case string text when IsDetailText(text):
                w.WriteString(key, text);
                return;
            default:
                w.WriteString(key, Redacted);
                return;
        }
    }

    /// <summary>
    /// Detail strings are error codes or a few fixed words, never free text: an uppercase code (<c>RUNTIME_NOT_INSTALLED</c>)
    /// or one of the lowercase names below. A token, URL or prompt can never match, so it is written as
    /// <see cref="Redacted"/> even if a call site passes one by mistake.
    /// </summary>
    private static bool IsDetailText(string? text) =>
        IsCode(text, 32) && (char.IsAsciiLetterUpper(text![0]) || text is "stdout" or "stderr" or "none" or "unknown");

    /// <summary>Short ASCII token: letters, digits and <c>. _ -</c> only. Deliberately rejects URLs, prompts and keys.</summary>
    private static bool IsCode(string? text, int maxLength)
    {
        if (text is null || text.Length == 0 || text.Length > maxLength)
        {
            return false;
        }
        foreach (var ch in text)
        {
            var ok = char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-';
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }

    private static double MonotonicMs() => Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;
}

/// <summary>One running span; <see cref="End"/> records it once, <see cref="Dispose"/> is the fallback that records an unfinished span.</summary>
public sealed class StartupSpan(StartupTrace trace, string stage, double startedMs) : IDisposable
{
    private int _ended;

    /// <summary>The stage id this span records.</summary>
    public string Stage { get; } = stage;

    /// <summary>True once a record was written (a second end is ignored).</summary>
    public bool Ended => Volatile.Read(ref _ended) != 0;

    /// <summary>Records the span with a result (<see cref="StartupTrace.Ok"/> / <c>failed</c> / <c>canceled</c> / ...).</summary>
    public void End(string result = StartupTrace.Ok, IReadOnlyDictionary<string, object?>? details = null)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
        {
            return;
        }
        trace.Emit(Stage, startedMs, trace.Mark(), result, details);
    }

    /// <summary>Ends the span as <see cref="StartupTrace.Incomplete"/> when the caller forgot to end it.</summary>
    public void Dispose() => End(StartupTrace.Incomplete);
}

/// <summary>Span helpers that keep the call sites readable: a disabled trace runs the body with no extra work.</summary>
public static class StartupTraceExtensions
{
    /// <summary>Runs <paramref name="body"/> inside a span; records ok / failed / canceled.</summary>
    public static async Task<T> TraceAsync<T>(
        this StartupTrace? trace,
        string stage,
        Func<Task<T>> body,
        Func<T, IReadOnlyDictionary<string, object?>?>? details = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (trace is not { Enabled: true })
        {
            return await body().ConfigureAwait(false);
        }
        var span = trace.Measure(stage);
        try
        {
            var result = await body().ConfigureAwait(false);
            span.End(StartupTrace.Ok, details?.Invoke(result));
            return result;
        }
        catch (OperationCanceledException)
        {
            span.End(StartupTrace.Canceled);
            throw;
        }
        catch
        {
            span.End(StartupTrace.Failed);
            throw;
        }
    }

    /// <summary>Runs <paramref name="body"/> inside a span; records ok / failed / canceled.</summary>
    public static async Task TraceAsync(this StartupTrace? trace, string stage, Func<Task> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (trace is not { Enabled: true })
        {
            await body().ConfigureAwait(false);
            return;
        }
        var span = trace.Measure(stage);
        try
        {
            await body().ConfigureAwait(false);
            span.End();
        }
        catch (OperationCanceledException)
        {
            span.End(StartupTrace.Canceled);
            throw;
        }
        catch
        {
            span.End(StartupTrace.Failed);
            throw;
        }
    }

    /// <summary>Runs a synchronous step inside a span (used for precondition / argv checks).</summary>
    public static T TraceSync<T>(this StartupTrace? trace, string stage, Func<T> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (trace is not { Enabled: true })
        {
            return body();
        }
        var span = trace.Measure(stage);
        try
        {
            var result = body();
            span.End();
            return result;
        }
        catch
        {
            span.End(StartupTrace.Failed);
            throw;
        }
    }
}
