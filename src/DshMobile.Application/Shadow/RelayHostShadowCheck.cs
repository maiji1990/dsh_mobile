using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DshMobile.Runtime.Automation.Relay;

namespace DshMobile.Application.Shadow;

/// <summary>
/// PR-07 steps 1–4 shadow (developer action): the same requests go to the legacy Relay's mailbox and to the opt-in C#
/// Relay host's own mailbox, one call at a time (legacy first, then C#), and the answers are compared after
/// normalization. Each relay asks for approval on its own card: every call here, on both sides, needs the user's
/// answer, so the check cannot make either relay do anything the user did not allow.
/// <para>
/// The calls act only on the debug test target (<see cref="TargetPackage"/>, a separate app with Material 3 controls),
/// plus reads whose data never leaves the device (a mismatch in contact / calendar data is reported as a hash, never
/// as content). Both relays act on the same live screen, so a write whose answer depends on state the previous side
/// changed (a scroll that reached the end) can legitimately differ: those are compared by shape only.
/// </para>
/// </summary>
public static class RelayHostShadowCheck
{
    public const string Slice = "automation.relay-host";

    /// <summary>The debug test target's package (tests/DshMobile.TestTarget). Never this app: Relay refuses its own UI.</summary>
    public const string TargetPackage = "org.example.dshmobile.testtarget";

    public const long CallTtlMs = 60_000;

    /// <summary>How an answer pair is compared.</summary>
    public enum Compare
    {
        /// <summary>Every byte after normalization; mismatches show an excerpt.</summary>
        Exact,

        /// <summary>Every byte after normalization; mismatches show only a hash (personal data).</summary>
        Private,

        /// <summary>ok / error code and the set of data keys (timing- or state-dependent payloads).</summary>
        Shape,
    }

    /// <summary>One call: capability, args (JSON; <c>{tap}</c> is filled from the tap button's bounds), comparison.</summary>
    public sealed record HostCall(string Capability, string Args, Compare Mode, bool Fresh = false)
    {
        public string Label => Args == "{}" ? Capability : $"{Capability} {Args}";
    }

    /// <summary>The marker texts the test target shows (see tests/DshMobile.TestTarget/MainActivity.cs).</summary>
    public const string TitleText = "dshm-target";
    public const string TapText = "dshm-tap";
    public const string InputDesc = "dshm-input";
    public const string SeekDesc = "dshm-seek";
    public const string ListDesc = "dshm-list";
    public const string ResetText = "dshm-reset";

    /// <summary>Puts the test target back to its start state (empty input, progress 0, list at top).</summary>
    public const string ResetArgs = """{"selector":{"text":"dshm-reset"}}""";

    /// <summary>
    /// The run, in order: launch the target, the read path, node actions, the coordinate path, Direct, then stop the
    /// target (a Shizuku-backed call when Shizuku is granted). Covers write path (both halves), Direct, Shizuku and the
    /// host's gate / approval / routing on the C# side.
    /// </summary>
    public static IReadOnlyList<HostCall> Calls { get; } =
    [
        new("app.launch", $$"""{"package":"{{TargetPackage}}"}""", Compare.Exact),
        new("ui.snapshot", "{}", Compare.Exact),
        new("ui.node", $$$"""{"selector":{"text":"{{{TapText}}}"}}""", Compare.Exact),
        new("ui.waitFor", $$"""{"selector":{"text":"{{TitleText}}"},"timeoutMs":0}""", Compare.Exact),
        new("ui.click", $$$"""{"selector":{"text":"{{{TapText}}}"}}""", Compare.Exact),
        new("ui.setValue", $$"""{"selector":{"desc":"{{InputDesc}}"},"text":"dshm shadow"}""", Compare.Exact, Fresh: true),
        new("ui.setProgress", $$"""{"selector":{"desc":"{{SeekDesc}}"},"percent":50}""", Compare.Exact, Fresh: true),
        new("ui.scroll", $$"""{"selector":{"desc":"{{ListDesc}}"},"direction":"forward"}""", Compare.Exact, Fresh: true),
        new("ui.tap", "{tap}", Compare.Exact),
        new("ui.swipe", "{swipe}", Compare.Exact),
        new("screen.capture", "{}", Compare.Shape),
        new("pkg.query", """{"keyword":"dshmobile"}""", Compare.Exact),
        new("contact.read", """{"limit":3}""", Compare.Private),
        new("cal.read", """{"limit":3}""", Compare.Private),
        new("notify.post", """{"title":"dshm-shadow","text":"relay host shadow"}""", Compare.Shape),
        new("app.stop", $$"""{"package":"{{TargetPackage}}"}""", Compare.Exact),
    ];

    private static readonly HashSet<string> Waiting = new(StringComparer.Ordinal)
    {
        RelayError.GateAwaitingConsent.Code, RelayError.GateWaitingTurn.Code,
    };

    private static readonly HashSet<string> Volatile = new(StringComparer.Ordinal) { "id", "elapsedMs", "waitedMs", "durationMs", "readBackRounds" };

    private static int s_running;

    public static async Task<ShadowReport> RunAsync(string legacyEntryDir, string csEntryDir, TimeProvider time, TimeSpan timeout,
        TimeSpan? settle, CancellationToken ct)
    {
        var pause = settle ?? TimeSpan.FromMilliseconds(400);
        ArgumentNullException.ThrowIfNull(time);
        var sw = Stopwatch.StartNew();
        ShadowReport Report(ShadowVerdict verdict, bool legacyOk, string detail) =>
            new(Slice, time.GetUtcNow(), verdict, legacyOk, null, true, null, detail, sw.ElapsedMilliseconds);

        var legacyRun = Path.Combine(legacyEntryDir, "run");
        var csRun = Path.Combine(csEntryDir, "run");
        if (!MailboxPresent(legacyRun))
        {
            return Report(ShadowVerdict.Inconclusive, false, $"not run: no legacy Relay mailbox under {legacyRun} (start the relay first)");
        }
        if (!MailboxPresent(csRun))
        {
            return Report(ShadowVerdict.Inconclusive, false, $"not run: no C# Relay mailbox under {csRun} (start the C# relay first)");
        }
        if (Interlocked.Exchange(ref s_running, 1) == 1)
        {
            return Report(ShadowVerdict.Inconclusive, false, "not run: another relay host check is still running");
        }
        try
        {
            var deadline = time.GetUtcNow() + timeout;
            var equal = new List<string>();
            var differ = new List<string>();
            var unsure = new List<string>();
            string? tapArgs = null;
            string? swipeArgs = null;
            foreach (var call in Calls)
            {
                var args = call.Args switch
                {
                    "{tap}" => tapArgs,
                    "{swipe}" => swipeArgs,
                    _ => call.Args,
                };
                if (args is null)
                {
                    unsure.Add($"{call.Capability}: skipped (the tap button's bounds were not read)");
                    continue;
                }
                // A state-dependent call starts from the target's start state on each side (found on the device: the
                // second side saw the first side's text / progress and answered differently for that reason alone).
                if (call.Fresh && !await ResetAsync(legacyRun, time, deadline, pause, ct).ConfigureAwait(false))
                {
                    unsure.Add($"{call.Capability}: legacy could not reset the test target first");
                    continue;
                }
                var legacy = await CallAsync(legacyRun, call.Capability, args, time, deadline, ct).ConfigureAwait(false);
                if (legacy is null)
                {
                    unsure.Add($"{call.Capability}: legacy gave no final answer before the deadline (answer the card)");
                    break;
                }
                // Let the screen settle after a legacy write before C# acts on the same screen.
                await Task.Delay(call.Capability == "app.launch" ? pause * 4 : pause, time, ct).ConfigureAwait(false);
                if (call.Fresh && !await ResetAsync(csRun, time, deadline, pause, ct).ConfigureAwait(false))
                {
                    unsure.Add($"{call.Capability}: C# could not reset the test target first");
                    continue;
                }
                var cs = await CallAsync(csRun, call.Capability, args, time, deadline, ct).ConfigureAwait(false);
                if (cs is null)
                {
                    unsure.Add($"{call.Capability}: C# gave no final answer before the deadline (answer the C# card)");
                    break;
                }
                await Task.Delay(pause, time, ct).ConfigureAwait(false);
                if (call.Capability == "ui.node" && TapPoint(legacy) is { } p)
                {
                    tapArgs = $$"""{"x":{{p.X}},"y":{{p.Y}}}""";
                    swipeArgs = $$"""{"fromX":{{p.X}},"fromY":{{p.Y}},"toX":{{p.X}},"toY":{{p.Y}},"durationMs":60}""";
                }
                var label = call.Capability == "ui.tap" || call.Capability == "ui.swipe" ? call.Capability : call.Label;
                var (a, b) = (Normalize(legacy, call.Mode), Normalize(cs, call.Mode));
                if (a == b)
                {
                    equal.Add($"{label}: equal ({Summary(legacy)})");
                }
                else
                {
                    differ.Add(call.Mode == Compare.Private
                        ? $"{label}: legacy {Summary(legacy)} #{Hash(a)} | C# {Summary(cs)} #{Hash(b)}"
                        : $"{label}: {DiffExcerpt(a, b)}");
                }
            }
            var detail = string.Join("; ", differ.Concat(unsure).Concat(equal));
            if (differ.Count > 0)
            {
                return Report(ShadowVerdict.Diverge, true, $"{differ.Count} differ: {detail}");
            }
            if (unsure.Count > 0 || equal.Count == 0)
            {
                return Report(ShadowVerdict.Inconclusive, equal.Count > 0, detail);
            }
            return Report(ShadowVerdict.Agree, true, $"{equal.Count}/{Calls.Count} equal: {detail}");
        }
        finally
        {
            System.Threading.Volatile.Write(ref s_running, 0);
        }
    }

    /// <summary>Clicks the target's reset button through one relay (same ui.click consent as the click call).</summary>
    private static async Task<bool> ResetAsync(string run, TimeProvider time, DateTimeOffset deadline, TimeSpan pause, CancellationToken ct)
    {
        var reply = await CallAsync(run, "ui.click", ResetArgs, time, deadline, ct).ConfigureAwait(false);
        await Task.Delay(pause, time, ct).ConfigureAwait(false);
        return reply is not null && Summary(reply) == "ok";
    }

    private static bool MailboxPresent(string run) =>
        Directory.Exists(Path.Combine(run, "inbox")) && Directory.Exists(Path.Combine(run, "outbox"));

    /// <summary>
    /// The comparable form of one answer: volatile fields (<c>id</c>, <c>elapsedMs</c>, <c>waitedMs</c>) dropped, the
    /// C# host's guest path written as the legacy one; <see cref="Compare.Shape"/> keeps only ok / code and data keys.
    /// </summary>
    public static string Normalize(string reply, Compare mode)
    {
        ArgumentNullException.ThrowIfNull(reply);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(reply);
        }
        catch (JsonException)
        {
            return "unparsable:" + reply;
        }
        if (root is not JsonObject o)
        {
            return "not-an-object:" + reply;
        }
        if (mode == Compare.Shape)
        {
            var ok = o["ok"]?.GetValueKind() == JsonValueKind.True;
            var code = (o["error"] as JsonObject)?["code"]?.ToString();
            var keys = o["data"] is JsonObject d ? string.Join(",", d.Select(kv => kv.Key).Order(StringComparer.Ordinal)) : "";
            return ok ? "ok:" + keys : "err:" + code;
        }
        Strip(o);
        return o.ToJsonString().Replace("/opt/pilot-cs", "/opt/pilot", StringComparison.Ordinal)
            .Replace("/pilot-cs/", "/pilot/", StringComparison.Ordinal);
    }

    private static void Strip(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).Where(Volatile.Contains).ToList())
                {
                    o.Remove(key);
                }
                foreach (var kv in o)
                {
                    Strip(kv.Value);
                }
                break;
            case JsonArray a:
                foreach (var item in a)
                {
                    Strip(item);
                }
                break;
        }
    }

    /// <summary>ok, or the error code: safe to show for any capability.</summary>
    public static string Summary(string reply)
    {
        try
        {
            using var doc = JsonDocument.Parse(reply);
            var root = doc.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                return "ok";
            }
            return root.TryGetProperty("error", out var e) && e.TryGetProperty("code", out var c) ? c.GetString() ?? "?" : "no error code";
        }
        catch (JsonException)
        {
            return "unparsable";
        }
    }

    /// <summary>The centre of the first node box (left/top/right/bottom) in an ok answer's data.</summary>
    public static (int X, int Y)? TapPoint(string reply)
    {
        try
        {
            return JsonNode.Parse(reply) is JsonObject { } o && o["ok"]?.GetValueKind() == JsonValueKind.True ? FindBox(o["data"]) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (int X, int Y)? FindBox(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o when o["left"] is JsonValue l && o["top"] is JsonValue t && o["right"] is JsonValue r && o["bottom"] is JsonValue b:
                return ((l.GetValue<int>() + r.GetValue<int>()) / 2, (t.GetValue<int>() + b.GetValue<int>()) / 2);
            case JsonObject o:
                foreach (var kv in o)
                {
                    if (FindBox(kv.Value) is { } p)
                    {
                        return p;
                    }
                }
                return null;
            case JsonArray a:
                foreach (var item in a)
                {
                    if (FindBox(item) is { } p)
                    {
                        return p;
                    }
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>Both sides from a little before the first differing character, so the difference itself is visible.</summary>
    public static string DiffExcerpt(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i])
        {
            i++;
        }
        var start = Math.Max(0, i - 80);
        string Part(string s) => (start > 0 ? "…" : "") + RelayMailboxExchange.Excerpt(s[Math.Min(start, s.Length)..], 260);
        return $"first difference at {i.ToString(CultureInfo.InvariantCulture)}: legacy {Part(a)} | C# {Part(b)}";
    }

    private static string Hash(string s) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..12];

    /// <summary>One call through a mailbox; resent (new id) while that relay only says the user has not answered yet.</summary>
    private static async Task<string?> CallAsync(string run, string capability, string args, TimeProvider time, DateTimeOffset deadline,
        CancellationToken ct)
    {
        while (time.GetUtcNow() < deadline)
        {
            var id = "hs-" + Guid.NewGuid().ToString("N")[..12];
            var text = $$"""{"v":1,"id":"{{id}}","capability":"{{capability}}","args":{{args}},"ttlMs":{{CallTtlMs.ToString(CultureInfo.InvariantCulture)}}}""";
            var inbox = Path.Combine(run, "inbox", id + ".json");
            var tmp = Path.Combine(run, "inbox", "." + id + ".tmp");
            await File.WriteAllTextAsync(tmp, text, ct).ConfigureAwait(false);
            File.Move(tmp, inbox);
            var outbox = Path.Combine(run, "outbox", id + ".json");
            var answerBy = Min(time.GetUtcNow() + TimeSpan.FromSeconds(45), deadline);
            string? reply = null;
            while (reply is null && time.GetUtcNow() < answerBy)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), time, ct).ConfigureAwait(false);
                if (File.Exists(outbox))
                {
                    try
                    {
                        reply = await File.ReadAllTextAsync(outbox, ct).ConfigureAwait(false);
                        File.Delete(outbox);
                    }
                    catch (IOException)
                    {
                        reply = null; // being renamed into place: next round
                    }
                }
            }
            if (reply is null)
            {
                TryDelete(inbox); // never claimed: take it back
                return null;
            }
            if (!IsWaiting(reply))
            {
                return reply;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), time, ct).ConfigureAwait(false);
        }
        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static bool IsWaiting(string reply)
    {
        try
        {
            using var doc = JsonDocument.Parse(reply);
            return doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("code", out var c)
                && c.GetString() is { } code && Waiting.Contains(code);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
