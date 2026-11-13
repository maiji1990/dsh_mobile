// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/log/{JsonLine, RunLog, AuditLog}.kt, spi/RelayRedactor.kt, runtime/RelayTime.kt
// Registered in docs/UPSTREAMS.md. Locked by tests/TestVectors/relay-storage.json.

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DshMobile.Runtime.Automation.Relay.Logging;

/// <summary>Upstream <c>RelayTime</c>: the monotonic clock decides, the wall clock is only shown and recorded.</summary>
public static class RelayClock
{
    public static long MonotonicNow() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    public static long WallNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Upstream <c>timestampFormat()</c>: <c>yyyy-MM-dd'T'HH:mm:ss.SSSXXX</c> in the device time zone.</summary>
    public static string Timestamp(long wallMs, TimeZoneInfo? zone = null)
    {
        var utc = DateTimeOffset.FromUnixTimeMilliseconds(wallMs);
        var local = TimeZoneInfo.ConvertTime(utc, zone ?? TimeZoneInfo.Local);
        var offset = local.Offset == TimeSpan.Zero ? "Z" : local.ToString("zzz", CultureInfo.InvariantCulture);
        return local.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture) + offset;
    }

    /// <summary>Upstream <c>dateStamp</c>: <c>yyyyMMdd</c> in the device time zone.</summary>
    public static string DateStamp(long wallMs, TimeZoneInfo? zone = null) =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(wallMs), zone ?? TimeZoneInfo.Local)
            .ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}

/// <summary>Upstream <c>JsonLine</c>: one compact JSON object per line; null fields are dropped, keys keep their order.</summary>
public static class JsonLine
{
    public static string Of(IEnumerable<KeyValuePair<string, object?>> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var (key, value) in fields)
        {
            if (value is null)
            {
                continue;
            }
            if (!first)
            {
                sb.Append(',');
            }
            first = false;
            Quote(sb, key);
            sb.Append(':');
            Encode(sb, value);
        }
        return sb.Append('}').ToString();
    }

    private static void Encode(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case int or long or short or byte:
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
            case System.Collections.IEnumerable list when value is not string:
                sb.Append('[');
                var first = true;
                foreach (var item in list)
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }
                    first = false;
                    Encode(sb, item);
                }
                sb.Append(']');
                break;
            default:
                Quote(sb, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                break;
        }
    }

    private static void Quote(StringBuilder sb, string text)
    {
        sb.Append('"');
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < ' ')
                    {
                        sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                    break;
            }
        }
        sb.Append('"');
    }
}

public enum LogLevel
{
    Info,
    Warn,
    Error,
}

public enum LogSubsystem
{
    Transport,
    Gate,
    Backend,
    Storage,
    Ui,
}

/// <summary>Upstream <c>LogEvent</c>: (level, template with <c>%{field}</c> placeholders).</summary>
public sealed record LogEvent(string Name, LogLevel Level, string Template)
{
    public static readonly LogEvent ChannelStarted = new("CHANNEL_STARTED", LogLevel.Info, "channel started");
    public static readonly LogEvent ChannelStopped = new("CHANNEL_STOPPED", LogLevel.Info, "channel stopped");
    public static readonly LogEvent ChannelDegraded = new("CHANNEL_DEGRADED", LogLevel.Warn, "channel degraded dirsAvailable=%{dirs}");
    public static readonly LogEvent RuntimeStarting = new("RUNTIME_STARTING", LogLevel.Info, "runtime starting");
    public static readonly LogEvent RuntimeRunning = new("RUNTIME_RUNNING", LogLevel.Info, "runtime running");
    public static readonly LogEvent RuntimeDegraded = new("RUNTIME_DEGRADED", LogLevel.Warn, "runtime degraded step=%{step} cause=%{cause}");
    public static readonly LogEvent RuntimeStopped = new("RUNTIME_STOPPED", LogLevel.Info, "runtime stopped");
    public static readonly LogEvent RequestDropped = new("REQUEST_DROPPED", LogLevel.Warn, "request dropped file=%{file} cause=%{cause}");
    public static readonly LogEvent RequestTooLarge = new("REQUEST_TOO_LARGE", LogLevel.Error, "request rejected oversized bytes=%{bytes}");
    public static readonly LogEvent ResponseWriteFailed = new("RESPONSE_WRITE_FAILED", LogLevel.Error, "response write failed id=%{id}");
    public static readonly LogEvent ResponseOverwritten = new("RESPONSE_OVERWRITTEN", LogLevel.Warn, "pre-existing response replaced id=%{id}");
    public static readonly LogEvent ResponseTooLarge = new("RESPONSE_TOO_LARGE", LogLevel.Warn, "response exceeded size cap id=%{id}");
    public static readonly LogEvent MailboxBackpressure = new("MAILBOX_BACKPRESSURE", LogLevel.Warn, "mailbox backpressure pending=%{pending} evicted=%{evicted} queueDepth=%{queueDepth} oldestAgeMs=%{oldestAgeMs}");
    public static readonly LogEvent ControlStarted = new("CONTROL_STARTED", LogLevel.Info, "control channel started");
    public static readonly LogEvent ControlStopped = new("CONTROL_STOPPED", LogLevel.Info, "control channel stopped");
    public static readonly LogEvent ControlRequestRejected = new("CONTROL_REQUEST_REJECTED", LogLevel.Warn, "control request rejected op=%{op} cause=%{cause}");
    public static readonly LogEvent RequestCancelled = new("REQUEST_CANCELLED", LogLevel.Info, "request cancelled id=%{id}");
    public static readonly LogEvent RequestCancelRequested = new("REQUEST_CANCEL_REQUESTED", LogLevel.Info, "request cancel requested id=%{id}");
    public static readonly LogEvent UnsafePathRejected = new("UNSAFE_PATH_REJECTED", LogLevel.Error, "unsafe path rejected name=%{name}");
    public static readonly LogEvent ApprovalDenied = new("APPROVAL_DENIED", LogLevel.Info, "denied cap=%{cap} code=%{code}");
    public static readonly LogEvent ApprovalResult = new("APPROVAL_RESULT", LogLevel.Info, "approval result cap=%{cap} choice=%{choice}");
    public static readonly LogEvent ApprovalTimeout = new("APPROVAL_TIMEOUT", LogLevel.Warn, "approval timeout cap=%{cap}");
    public static readonly LogEvent OverlayUnavailable = new("OVERLAY_UNAVAILABLE", LogLevel.Warn, "overlay approval unavailable cap=%{cap} cause=%{cause} at=%{at}");
    public static readonly LogEvent NotifyUnavailable = new("NOTIFY_UNAVAILABLE", LogLevel.Warn, "approval notification unavailable cap=%{cap} cause=%{cause}");
    public static readonly LogEvent ApprovalRouted = new("APPROVAL_ROUTED", LogLevel.Info, "approval routed cap=%{cap} postState=%{postState}");
    public static readonly LogEvent ShizukuState = new("SHIZUKU_STATE", LogLevel.Info, "shizuku state binder=%{binder} granted=%{granted}");
    public static readonly LogEvent ShizukuProbeFailed = new("SHIZUKU_PROBE_FAILED", LogLevel.Warn, "shizuku probe failed where=%{where} cause=%{cause}");
    public static readonly LogEvent ShizukuBindFailed = new("SHIZUKU_BIND_FAILED", LogLevel.Warn, "shizuku user service bind failed cause=%{cause}");
    public static readonly LogEvent ShizukuServiceConnected = new("SHIZUKU_SERVICE_CONNECTED", LogLevel.Info, "shizuku user service connected");
    public static readonly LogEvent ShizukuServiceDead = new("SHIZUKU_SERVICE_DEAD", LogLevel.Warn, "shizuku user service disconnected");
    public static readonly LogEvent ProjectionConsentGranted = new("PROJECTION_CONSENT_GRANTED", LogLevel.Info, "screen capture consent granted");
    public static readonly LogEvent ProjectionConsentDenied = new("PROJECTION_CONSENT_DENIED", LogLevel.Info, "screen capture consent denied");
    public static readonly LogEvent ProjectionConsentTimeout = new("PROJECTION_CONSENT_TIMEOUT", LogLevel.Warn, "screen capture consent timed out");
    public static readonly LogEvent ProjectionConsentPending = new("PROJECTION_CONSENT_PENDING", LogLevel.Info, "screen capture consent awaiting user");
    public static readonly LogEvent ProjectionTokenFailed = new("PROJECTION_TOKEN_FAILED", LogLevel.Warn, "media projection token unavailable");
    public static readonly LogEvent ProjectionRevoked = new("PROJECTION_REVOKED", LogLevel.Info, "screen capture stopped by user");
    public static readonly LogEvent ProjectionFrameFailed = new("PROJECTION_FRAME_FAILED", LogLevel.Warn, "projection frame failed cause=%{cause}");
    public static readonly LogEvent ProjectionNoUi = new("PROJECTION_NO_UI", LogLevel.Warn, "screen capture needs the assistant page in front");
    public static readonly LogEvent SweepDone = new("SWEEP_DONE", LogLevel.Info, "sweep done requests=%{requests} responses=%{responses} artifacts=%{artifacts} freed=%{freed}");
    public static readonly LogEvent QuotaExhausted = new("QUOTA_EXHAUSTED", LogLevel.Error, "quota exhausted want=%{want}");
    public static readonly LogEvent ManualClear = new("MANUAL_CLEAR", LogLevel.Info, "manual clear freed=%{freed}");
    public static readonly LogEvent AtomicWriteFailed = new("ATOMIC_WRITE_FAILED", LogLevel.Warn, "atomic write failed file=%{file}");
    public static readonly LogEvent DirsUnavailable = new("DIRS_UNAVAILABLE", LogLevel.Error, "working directories unavailable");
    public static readonly LogEvent LedgerCorrupted = new("LEDGER_CORRUPTED", LogLevel.Warn, "quota ledger corrupted %{detail}");
    public static readonly LogEvent BackendFailure = new("BACKEND_FAILURE", LogLevel.Warn, "backend failure cap=%{cap} code=%{code}");
    public static readonly LogEvent RemoteResultDropped = new("REMOTE_RESULT_DROPPED", LogLevel.Warn, "remote result dropped kind=%{kind} waitedMs=%{waitedMs}");
    public static readonly LogEvent CallDone = new("CALL_DONE", LogLevel.Info, "call done cap=%{cap} result=%{result} ms=%{ms} bytes=%{bytes}");
    public static readonly LogEvent CallArgs = new("CALL_ARGS", LogLevel.Info, "call args cap=%{cap} digest=%{digest}");
    public static readonly LogEvent CallFailed = new("CALL_FAILED", LogLevel.Error, "call failed id=%{id} type=%{type}");
    public static readonly LogEvent CallDeferred = new("CALL_DEFERRED", LogLevel.Warn, "call deferred id=%{id} cause=%{cause}");
    public static readonly LogEvent CallAccepted = new("CALL_ACCEPTED", LogLevel.Info, "request accepted id=%{id}");
    public static readonly LogEvent CallSettled = new("CALL_SETTLED", LogLevel.Info, "request settled ms=%{ms}");
    public static readonly LogEvent ArtifactCreateFailed = new("ARTIFACT_CREATE_FAILED", LogLevel.Error, "artifact create failed cap=%{cap}");
    public static readonly LogEvent AuditWriteFailed = new("AUDIT_WRITE_FAILED", LogLevel.Warn, "audit write failed %{detail}");
    public static readonly LogEvent AuditPurged = new("AUDIT_PURGED", LogLevel.Info, "audit purged files=%{files}");
    public static readonly LogEvent RequestStateCreated = new("REQUEST_STATE_CREATED", LogLevel.Info, "request state created id=%{id} cap=%{cap}");
    public static readonly LogEvent RequestStateTerminal = new("REQUEST_STATE_TERMINAL", LogLevel.Info, "request state terminal id=%{id} phase=%{phase}");
    public static readonly LogEvent RequestRecovered = new("REQUEST_RECOVERED", LogLevel.Warn, "request recovered id=%{id} phase=%{phase}");
    public static readonly LogEvent RequestDispatchGuardFailed = new("REQUEST_DISPATCH_GUARD_FAILED", LogLevel.Error, "dispatch guard failed id=%{id}");
    public static readonly LogEvent RequestDispatchNotExecutable = new("REQUEST_DISPATCH_NOT_EXECUTABLE", LogLevel.Warn, "dispatch refused not executable id=%{id} phase=%{phase}");
    public static readonly LogEvent RequestAdmitted = new("REQUEST_ADMITTED", LogLevel.Info, "request admitted id=%{id}");
    public static readonly LogEvent RequestWaitingUser = new("REQUEST_WAITING_USER", LogLevel.Info, "request waiting for user id=%{id} kind=%{kind}");
    public static readonly LogEvent RequestApprovalDecided = new("REQUEST_APPROVAL_DECIDED", LogLevel.Info, "approval decided id=%{id} verdict=%{verdict}");
    public static readonly LogEvent RequestDispatchArmed = new("REQUEST_DISPATCH_ARMED", LogLevel.Info, "dispatch armed id=%{id}");
    public static readonly LogEvent RequestCompleted = new("REQUEST_COMPLETED", LogLevel.Info, "request completed id=%{id} state=%{state} ms=%{ms}");
    public static readonly LogEvent ResponsePublished = new("RESPONSE_PUBLISHED", LogLevel.Info, "response published id=%{id} bytes=%{bytes}");
    public static readonly LogEvent ApprovalPresenter = new("APPROVAL_PRESENTER", LogLevel.Info, "approval presented via=%{via} waiting=%{waiting}");
    public static readonly LogEvent ControlOp = new("CONTROL_OP", LogLevel.Info, "control op=%{op} result=%{result} ms=%{ms}");
    public static readonly LogEvent AskAnswered = new("ASK_ANSWERED", LogLevel.Info, "ask answered id=%{id} choice=%{choice} ms=%{ms}");
    public static readonly LogEvent AskUnavailable = new("ASK_UNAVAILABLE", LogLevel.Warn, "ask unavailable id=%{id} cause=%{cause} ms=%{ms}");
    public static readonly LogEvent BackendCall = new("BACKEND_CALL", LogLevel.Info, "backend call cap=%{cap} backend=%{backend} result=%{result} ms=%{ms}");
    public static readonly LogEvent RemoteSubmitted = new("REMOTE_SUBMITTED", LogLevel.Info, "remote submitted cap=%{cap} waitMs=%{waitMs}");
    public static readonly LogEvent RemoteGiveUp = new("REMOTE_GIVE_UP", LogLevel.Warn, "remote gave up cap=%{cap} submittedAgoMs=%{ago}");

    /// <summary>Every event in upstream declaration order.</summary>
    public static IReadOnlyList<LogEvent> All { get; } = typeof(LogEvent)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.FieldType == typeof(LogEvent))
        .OrderBy(f => f.MetadataToken)
        .Select(f => (LogEvent)f.GetValue(null)!)
        .ToList();
}

/// <summary>
/// Upstream <c>RunLog</c>: a 512-entry ring plus <c>logs/relay.log</c> (rotated once at 2 MiB to
/// <c>relay-prev.log</c>). Values lose control characters, are cut to 64 chars, pass the redactor and become "-"
/// when blank; a placeholder nobody filled is marked <c>unresolved=&lt;name&gt;</c>; a message is cut to 2000 chars.
/// </summary>
public sealed class RunLog
{
    public const string FileName = "relay.log";
    public const string PreviousFileName = "relay-prev.log";
    public const long MaxFileBytes = 2L * 1024L * 1024L;
    public const int RingCapacity = 512;
    public const int MaxMessageChars = 2000;
    public const int MaxFieldChars = 64;

    public sealed record Entry(long AtWallMs, long AtMonotonicMs, LogLevel Level, LogSubsystem Subsystem, LogEvent Event, string Text, bool Verbose);

    private readonly string _logFile;
    private readonly string _previousFile;
    private readonly Func<bool> _verbose;
    private readonly Func<string, string> _redact;
    private readonly Lock _lock = new();
    private readonly Queue<Entry> _ring = new();

    public RunLog(string logsDir, Func<bool>? verboseEnabled = null, Func<string, string>? redactor = null, string fileName = FileName,
        string previousFileName = PreviousFileName)
    {
        _logFile = Path.Combine(logsDir, fileName);
        _previousFile = Path.Combine(logsDir, previousFileName);
        _verbose = verboseEnabled ?? (() => false);
        _redact = redactor ?? (s => s);
    }

    /// <summary>Observer for each line (the Android host forwards warnings/errors to logcat).</summary>
    public Action<LogLevel, string>? Sink { get; set; }

    public void Info(LogSubsystem subsystem, LogEvent ev, params (string Key, string Value)[] fields) => Write(subsystem, ev, fields, false);

    public void Warn(LogSubsystem subsystem, LogEvent ev, params (string Key, string Value)[] fields) => Write(subsystem, ev, fields, false);

    public void Error(LogSubsystem subsystem, LogEvent ev, params (string Key, string Value)[] fields) => Write(subsystem, ev, fields, false);

    public void Event(LogSubsystem subsystem, LogEvent ev, params (string Key, string Value)[] fields) => Write(subsystem, ev, fields, false);

    public void Verbose(LogSubsystem subsystem, LogEvent ev, params (string Key, string Value)[] fields)
    {
        if (_verbose())
        {
            Write(subsystem, ev, fields, true);
        }
    }

    public IReadOnlyList<Entry> Snapshot()
    {
        lock (_lock)
        {
            return [.. _ring];
        }
    }

    public long UsageBytes()
    {
        try
        {
            return (File.Exists(_logFile) ? new FileInfo(_logFile).Length : 0) + (File.Exists(_previousFile) ? new FileInfo(_previousFile).Length : 0);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>The upstream line format: <c>relay &lt;I|W|E&gt;/&lt;subsystem&gt; &lt;body&gt;</c>.</summary>
    public static string Line(LogEvent ev, LogSubsystem subsystem, string body) =>
        $"relay {Tag(ev.Level)}/{subsystem.ToString().ToLowerInvariant()} {body}";

    public static string Tag(LogLevel level) => level switch
    {
        LogLevel.Info => "I",
        LogLevel.Warn => "W",
        _ => "E",
    };

    /// <summary>Upstream <c>render</c>.</summary>
    public static string Render(LogEvent ev, IEnumerable<(string Key, string Value)> fields, Func<string, string>? redactor = null)
    {
        ArgumentNullException.ThrowIfNull(ev);
        ArgumentNullException.ThrowIfNull(fields);
        var text = ev.Template;
        foreach (var (key, value) in fields)
        {
            text = text.Replace("%{" + key + "}", SafeValue(value, redactor ?? (s => s)), StringComparison.Ordinal);
        }
        if (text.Contains("%{", StringComparison.Ordinal))
        {
            text += " unresolved=" + FirstUnresolved(text);
        }
        return text.Length <= MaxMessageChars ? text : text[..MaxMessageChars];
    }

    /// <summary>Upstream <c>safeValue</c>: drop ISO control chars (C0, DEL, C1), cut to 64, redact, blank → "-".</summary>
    public static string SafeValue(string value, Func<string, string> redact)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sb = new StringBuilder(Math.Min(value.Length, MaxFieldChars));
        foreach (var c in value)
        {
            if (c <= '\u001f' || c is >= '\u007f' and <= '\u009f')
            {
                continue;
            }
            if (sb.Length == MaxFieldChars)
            {
                break;
            }
            sb.Append(c);
        }
        var redacted = redact(sb.ToString());
        return string.IsNullOrWhiteSpace(redacted) ? "-" : redacted;
    }

    private static string? FirstUnresolved(string text)
    {
        var at = 0;
        while ((at = text.IndexOf("%{", at, StringComparison.Ordinal)) >= 0)
        {
            var end = at + 2;
            while (end < text.Length && char.IsAsciiLetter(text[end]))
            {
                end++;
            }
            if (end < text.Length && end > at + 2 && text[end] == '}')
            {
                return text[(at + 2)..end];
            }
            at += 2;
        }
        return "null";
    }

    private void Write(LogSubsystem subsystem, LogEvent ev, (string Key, string Value)[] fields, bool verbose)
    {
        var body = Render(ev, fields, _redact);
        var entry = new Entry(RelayClock.WallNow(), RelayClock.MonotonicNow(), ev.Level, subsystem, ev, body, verbose);
        lock (_lock)
        {
            _ring.Enqueue(entry);
            while (_ring.Count > RingCapacity)
            {
                _ring.Dequeue();
            }
            Append(Line(ev, subsystem, body));
        }
        if (ev.Level != LogLevel.Info || verbose)
        {
            Sink?.Invoke(ev.Level, "relay: " + body);
        }
    }

    private void Append(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logFile)!);
            if (File.Exists(_logFile) && new FileInfo(_logFile).Length > MaxFileBytes)
            {
                if (File.Exists(_previousFile))
                {
                    File.Delete(_previousFile);
                }
                File.Move(_logFile, _previousFile);
            }
            File.AppendAllText(_logFile, line + "\n");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Upstream <c>AuditEntry</c>.</summary>
public sealed record AuditEntry(
    string RequestId,
    string Capability,
    string? Target,
    string SystemState,
    string Tier,
    string Decision,
    string GrantSource,
    string UidRole,
    string Outcome,
    string? Surface,
    string? DegradedFrom,
    string? Reason,
    long LatencyMs,
    long ArtifactBytes,
    string? CanonicalArgs);

/// <summary>
/// Upstream <c>AuditLog</c>: <c>audit-yyyyMMdd[.N].jsonl</c> segments of 2 MiB (20 per day), args only as a salted
/// SHA-256 prefix, retention by a monotonic watermark (a wound-back wall clock cannot keep or drop records), and a
/// 200 MiB store cap that drops whole days, oldest first, never the newest.
/// </summary>
public sealed class AuditLog
{
    public const string Prefix = "audit-";
    public const string Suffix = ".jsonl";
    public const string NullValue = "null";
    public const long DefaultMaxFileBytes = 2L * 1024 * 1024;
    public const int DefaultMaxSegments = 20;
    public const int TailReadBytes = 64 * 1024;
    public const int DefaultRetentionDays = 30;
    public const long DayMs = 24L * 60L * 60L * 1000L;
    public const long DefaultStoreCapBytes = 200L * 1024 * 1024;
    public const string Watermark = ".watermark";

    private readonly string _dir;
    private readonly string _salt;
    private readonly Action<string> _reportFailure;
    private readonly Func<long> _now;
    private readonly Func<long> _mono;
    private readonly long _maxFileBytes;
    private readonly int _maxSegments;
    private readonly long _storeCap;
    private readonly TimeZoneInfo? _zone;
    private readonly Lock _lock = new();

    public AuditLog(string auditDir, string digestSalt, Action<string>? reportFailure = null, Func<long>? now = null, Func<long>? mono = null,
        long maxFileBytes = DefaultMaxFileBytes, int maxSegments = DefaultMaxSegments, long storeCapBytes = DefaultStoreCapBytes, TimeZoneInfo? zone = null)
    {
        _dir = auditDir;
        _salt = digestSalt;
        _reportFailure = reportFailure ?? (_ => { });
        _now = now ?? RelayClock.WallNow;
        _mono = mono ?? RelayClock.MonotonicNow;
        _maxFileBytes = maxFileBytes;
        _maxSegments = maxSegments;
        _storeCap = storeCapBytes;
        _zone = zone;
    }

    /// <summary>The line upstream <c>record</c> appends for <paramref name="entry"/> at wall time <paramref name="wallMs"/>.</summary>
    public string LineFor(AuditEntry entry, long wallMs)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return JsonLine.Of(new KeyValuePair<string, object?>[]
        {
            new("ts", RelayClock.Timestamp(wallMs, _zone)),
            new("req", SafeId(entry.RequestId)),
            new("cap", entry.Capability),
            new("target", entry.Target ?? NullValue),
            new("sys", entry.SystemState),
            new("tier", entry.Tier),
            new("decision", entry.Decision),
            new("source", entry.GrantSource),
            new("uid", entry.UidRole),
            new("result", entry.Outcome),
            new("surface", entry.Surface ?? NullValue),
            new("from", entry.DegradedFrom ?? NullValue),
            new("reason", entry.Reason ?? NullValue),
            new("ms", entry.LatencyMs),
            new("bytes", entry.ArtifactBytes),
            new("argsDigest", entry.CanonicalArgs is { } c ? Digest(c) : NullValue),
        });
    }

    public void Record(AuditEntry entry)
    {
        var line = LineFor(entry, _now());
        bool written;
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(_dir);
                File.AppendAllText(TargetFile(), line + "\n");
            }
            written = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            written = false;
        }
        if (!written)
        {
            _reportFailure("append rejected");
        }
    }

    public string? ArgsDigest(string? canonical) => canonical is null ? null : Digest(canonical);

    public string ArgsDigestOf(string canonical) => Digest(canonical);

    public IReadOnlyList<string> Recent(int limit, int tailBytes = TailReadBytes)
    {
        lock (_lock)
        {
            if (!Directory.Exists(_dir))
            {
                return [];
            }
            var files = new DirectoryInfo(_dir).GetFiles().Where(f => f.Name.StartsWith(Prefix, StringComparison.Ordinal))
                .OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            var output = new List<string>(limit);
            foreach (var file in files)
            {
                if (output.Count >= limit)
                {
                    break;
                }
                var lines = ReadTail(file.FullName, tailBytes);
                for (var i = lines.Count - 1; i >= 0; i--)
                {
                    if (string.IsNullOrWhiteSpace(lines[i]))
                    {
                        continue;
                    }
                    output.Add(lines[i]);
                    if (output.Count >= limit)
                    {
                        break;
                    }
                }
            }
            return output;
        }
    }

    public long UsageBytes()
    {
        try
        {
            lock (_lock)
            {
                return Directory.Exists(_dir)
                    ? new DirectoryInfo(_dir).GetFiles().Where(f => f.Name.StartsWith(Prefix, StringComparison.Ordinal)).Sum(f => f.Length)
                    : 0;
            }
        }
        catch (IOException)
        {
            return 0;
        }
    }

    public int PurgeExpired(int retentionDays = DefaultRetentionDays)
    {
        var removed = 0;
        lock (_lock)
        {
            var trusted = AdvanceTrustedClock();
            if (!Directory.Exists(_dir))
            {
                return 0;
            }
            var files = new DirectoryInfo(_dir).GetFiles().Where(f => f.Name.StartsWith(Prefix, StringComparison.Ordinal)).ToList();
            if (trusted > 0)
            {
                var cutoff = trusted - retentionDays * DayMs;
                foreach (var f in files.Where(f => f.LastWriteTimeUtc.ToUnixMs() < cutoff))
                {
                    if (TryDelete(f.FullName))
                    {
                        removed++;
                    }
                }
            }
            removed += EnforceStoreCap([.. files.Where(f => File.Exists(f.FullName))]);
        }
        return removed;
    }

    /// <summary>Upstream <c>digest</c>: first 8 bytes of SHA-256(salt + "\n" + text), lower hex.</summary>
    public string Digest(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_salt + "\n" + text)).AsSpan(0, 8));

    /// <summary>Upstream <c>safeId</c>: letters, digits, '-' and '_', at most 64.</summary>
    public static string SafeId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sb = new StringBuilder();
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
            {
                sb.Append(c);
                if (sb.Length == 64)
                {
                    break;
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>Upstream <c>segmentFile</c> name.</summary>
    public static string SegmentName(string stamp, int segment) =>
        segment <= 1 ? Prefix + stamp + Suffix : $"{Prefix}{stamp}.{segment.ToString(CultureInfo.InvariantCulture)}{Suffix}";

    private long AdvanceTrustedClock()
    {
        var mark = Path.Combine(_dir, Watermark);
        string[]? parts = null;
        try
        {
            parts = File.ReadAllText(mark).Trim().Split(' ');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        if (parts is not { Length: 2 } || !long.TryParse(parts[0], CultureInfo.InvariantCulture, out var anchorWall)
            || !long.TryParse(parts[1], CultureInfo.InvariantCulture, out var anchorMono))
        {
            WriteWatermark(_now(), _mono());
            return 0;
        }
        var elapsed = _mono() - anchorMono;
        if (elapsed < 0)
        {
            WriteWatermark(_now(), _mono());
            return 0;
        }
        var trusted = anchorWall + elapsed;
        WriteWatermark(trusted, _mono());
        return trusted;
    }

    private void WriteWatermark(long wall, long monoAt)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, Watermark), string.Create(CultureInfo.InvariantCulture, $"{wall} {monoAt}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private int EnforceStoreCap(List<FileInfo> files)
    {
        var total = files.Sum(f => f.Length);
        if (total <= _storeCap)
        {
            return 0;
        }
        var byDay = files.GroupBy(f => DayStamp(f.Name)).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var removed = 0;
        var days = byDay.Keys.OrderBy(d => byDay[d].Min(f => f.LastWriteTimeUtc)).ToList();
        foreach (var day in days.Take(days.Count - 1))
        {
            foreach (var file in byDay[day])
            {
                var size = file.Length;
                if (TryDelete(file.FullName))
                {
                    removed++;
                    total -= size;
                }
                if (total <= _storeCap)
                {
                    return removed;
                }
            }
        }
        return removed;
    }

    private static string DayStamp(string name) => new([.. name[Prefix.Length..].TakeWhile(char.IsDigit)]);

    private string TargetFile()
    {
        var stamp = RelayClock.DateStamp(_now(), _zone);
        var files = Enumerable.Range(1, _maxSegments).Select(i => Path.Combine(_dir, SegmentName(stamp, i))).ToList();
        foreach (var f in files)
        {
            if (!File.Exists(f) || new FileInfo(f).Length <= _maxFileBytes)
            {
                return f;
            }
        }
        TryDelete(files[0]);
        var free = 0;
        while (free + 1 < files.Count && TryMove(files[free + 1], files[free]))
        {
            free++;
        }
        return files[free];
    }

    private static List<string> ReadTail(string file, int bytes)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = fs.Length;
            var start = Math.Max(0, length - bytes);
            fs.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[length - start];
            fs.ReadExactly(buffer);
            var lines = Encoding.UTF8.GetString(buffer).Split('\n').ToList();
            return start > 0 ? lines.Skip(1).ToList() : lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryMove(string from, string to)
    {
        try
        {
            if (!File.Exists(from))
            {
                return false;
            }
            File.Move(from, to, overwrite: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

internal static class DateTimeMs
{
    public static long ToUnixMs(this DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
}
