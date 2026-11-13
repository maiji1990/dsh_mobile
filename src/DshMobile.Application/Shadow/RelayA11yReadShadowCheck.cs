using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DshMobile.Runtime.Automation.Relay;
using DshMobile.Runtime.Automation.Relay.A11y;
using DshMobile.Runtime.Automation.Relay.Exec;

namespace DshMobile.Application.Shadow;

/// <summary>
/// PR-07 segment 7 shadow (developer action): the legacy Relay's accessibility read path (<c>ui.snapshot</c> /
/// <c>ui.node</c>) against the C# <see cref="A11ySnapshot"/> / <see cref="NodeReader"/>, on the same live tree.
/// <para>
/// Each call is bracketed: C# reads the tree, the identical call goes through the real mailbox (and through the normal
/// approval: the first call of each capability shows the user a card), C# reads again. The legacy <c>data</c> must equal
/// the C# encoding byte for byte. When the two C# reads differ the screen changed meanwhile: a legacy answer equal to
/// either read still counts as agreement, one equal to neither is Inconclusive, never a false difference.
/// </para>
/// <para>
/// Safety: only the two read capabilities are ever sent; both are refused nothing by this check and both still need the
/// user's approval, so the check cannot make Relay do anything the user did not allow. A user who declines makes the
/// run Inconclusive.
/// </para>
/// </summary>
public static class RelayA11yReadShadowCheck
{
    public const string Slice = "automation.relay-a11y-read";
    public const long CallTtlMs = 60_000;

    private static readonly HashSet<string> Waiting = new(StringComparer.Ordinal)
    {
        RelayError.GateAwaitingConsent.Code, RelayError.GateWaitingTurn.Code,
    };

    /// <summary>Failures the backend itself produces: these are compared like answers; any other failure is the gate's.</summary>
    private static readonly HashSet<string> BackendCodes = new(StringComparer.Ordinal)
    {
        RelayError.BackendUnavailable.Code, RelayError.NodeNotFound.Code, RelayError.NodeAmbiguous.Code, RelayError.TransportMalformed.Code,
    };

    /// <summary>One read call: the capability, its args, and how C# answers it from a tree.</summary>
    public sealed record ReadCall(string Capability, string Args)
    {
        public BackendResult Answer(A11yTree tree)
        {
            ArgumentNullException.ThrowIfNull(tree);
            return Capability == "ui.snapshot"
                ? A11ySnapshot.Snapshot(tree)
                : new NodeReader(tree).ReadNode(JsonElement.Parse(Args));
        }

        public string Label => Capability == "ui.snapshot" ? Capability : $"{Capability} {Args}";
    }

    /// <summary>C#'s answer in comparable form: <c>ok:data</c> or <c>CODE:reason</c>.</summary>
    public static string Comparable(BackendResult result) => result switch
    {
        BackendResult.Ok ok => "ok:" + RelayEncoding.EncodeData(ok.Data),
        BackendResult.Failed f => f.Error.Code + ":" + f.Reason,
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    /// <summary>
    /// The legacy answer in the same form, the <c>data</c> taken as the exact bytes libcore wrote; null when it is not a
    /// backend answer (a gate refusal, a malformed reply).
    /// </summary>
    public static string? LegacyComparable(string reply, out string summary)
    {
        ArgumentNullException.ThrowIfNull(reply);
        try
        {
            using var doc = JsonDocument.Parse(reply);
            var root = doc.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                summary = "ok";
                return "ok:" + (root.TryGetProperty("data", out var data) ? data.GetRawText() : "{}");
            }
            var code = root.TryGetProperty("error", out var e) && e.TryGetProperty("code", out var c) ? c.GetString() : null;
            summary = code ?? "no error code";
            if (code is null || !BackendCodes.Contains(code))
            {
                return null;
            }
            return code + ":" + (root.TryGetProperty("reason", out var r) ? r.GetString() : null);
        }
        catch (JsonException)
        {
            summary = "unparsable reply";
            return null;
        }
    }

    /// <summary>
    /// The calls for one run, picked from a C# snapshot of the screen: the snapshot itself, node 0, the first clickable
    /// node, a selector on the first text that occurs once, and the node below the clickable one.
    /// </summary>
    public static IReadOnlyList<ReadCall> PickCalls(A11yTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var calls = new List<ReadCall> { new("ui.snapshot", "{}"), new("ui.node", """{"nodeId":0}""") };
        if (A11ySnapshot.Snapshot(tree) is not BackendResult.Ok { Data: var data } || data["nodes"] is not JsonArray nodes)
        {
            return calls;
        }
        var list = nodes.OfType<JsonObject>().ToList();
        if (list.FirstOrDefault(n => n["clickable"] is not null && (int)n["nodeId"]! > 0) is { } clickable)
        {
            var id = ((int)clickable["nodeId"]!).ToString(CultureInfo.InvariantCulture);
            calls.Add(new ReadCall("ui.node", $$"""{"nodeId":{{id}}}"""));
            calls.Add(new ReadCall("ui.node", "{\"nodeId\":" + id + ",\"relative\":{\"side\":\"below\"}}"));
        }
        var texts = list.Select(n => (string?)n["text"]).OfType<string>().ToList();
        if (texts.FirstOrDefault(t => texts.Count(x => x == t) == 1) is { } unique)
        {
            var selector = new JsonObject { ["selector"] = new JsonObject { ["text"] = unique } };
            calls.Add(new ReadCall("ui.node", selector.ToJsonString()));
        }
        return calls;
    }

    public static async Task<ShadowReport> RunAsync(string pilotEntryDir, Func<A11yTree?> hostTree, TimeProvider time, TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hostTree);
        ArgumentNullException.ThrowIfNull(time);
        var sw = Stopwatch.StartNew();
        if (Interlocked.Exchange(ref s_running, 1) == 1)
        {
            // Two runs would share the one session consent and the same calls: their verdicts would mean nothing.
            return new ShadowReport(Slice, time.GetUtcNow(), ShadowVerdict.Inconclusive, false, null, true, null,
                "not run: another accessibility read check is still running", 0);
        }
        try
        {
            return await RunCoreAsync(pilotEntryDir, hostTree, time, timeout, sw, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or IOException or UnauthorizedAccessException))
        {
            // A reader fault (the live tree, a platform call) must end the check visibly, never leave it hanging.
            var at = ex.StackTrace?.Split('\n', 2)[0].Trim();
            return new ShadowReport(Slice, time.GetUtcNow(), ShadowVerdict.Inconclusive, false, null, false, ex.GetType().Name,
                $"the C# reader failed: {ex.GetType().Name}: {ex.Message}{(at is null ? "" : " " + at)}", sw.ElapsedMilliseconds);
        }
        finally
        {
            Volatile.Write(ref s_running, 0);
        }
    }

    /// <summary>Attempts per call when the screen moved or the service was gone around it.</summary>
    public const int MaxAttempts = 3;

    private static int s_running;

    private static string Excerpt(string comparable) =>
        comparable.StartsWith("ok:", StringComparison.Ordinal) ? "ok" : RelayMailboxExchange.Excerpt(comparable, 120);

    private static async Task<ShadowReport> RunCoreAsync(string pilotEntryDir, Func<A11yTree?> hostTree, TimeProvider time, TimeSpan timeout,
        Stopwatch sw, CancellationToken ct)
    {
        ShadowReport Report(ShadowVerdict verdict, bool legacyOk, string? legacyCode, string detail) =>
            new(Slice, time.GetUtcNow(), verdict, legacyOk, legacyCode, true, null, detail, sw.ElapsedMilliseconds);

        var run = Path.Combine(pilotEntryDir, "run");
        if (!Directory.Exists(Path.Combine(run, "inbox")) || !Directory.Exists(Path.Combine(run, "outbox")))
        {
            return Report(ShadowVerdict.Inconclusive, false, null, $"not run: no Relay mailbox under {run} (start the relay first)");
        }
        if (hostTree() is not { } first)
        {
            return Report(ShadowVerdict.Inconclusive, false, null, "not run: the accessibility service is not connected (the user turns it on)");
        }
        var deadline = time.GetUtcNow() + timeout;
        var lines = new List<string>();
        var differ = new List<string>();
        var unsure = new List<string>();
        var stop = false;
        foreach (var call in PickCalls(first))
        {
            // A call is retried (no card the second time: consent is already given for the session) when the screen
            // moved under it or the service was momentarily gone; only a stable, comparable read can be a difference.
            string? retryReason = null;
            for (var attempt = 1; attempt <= MaxAttempts && !stop; attempt++)
            {
                retryReason = null;
                var before = hostTree() is { } t0 ? Comparable(call.Answer(t0)) : null;
                var reply = await CallAsync(run, call, time, deadline, ct).ConfigureAwait(false);
                var after = hostTree() is { } t1 ? Comparable(call.Answer(t1)) : null;
                if (reply is null)
                {
                    unsure.Add($"{call.Label}: no final answer before the deadline (approve the card, or choose 本次会话允许)");
                    stop = true;
                    break;
                }
                if (LegacyComparable(reply, out var summary) is not { } legacy)
                {
                    unsure.Add($"{call.Label}: legacy answered {summary}, not a backend result");
                    // Declined or refused by the gate: the remaining calls would be too.
                    stop = summary is not ("ok" or "no error code" or "unparsable reply");
                    break;
                }
                if (before is null || after is null)
                {
                    // The service was unbound around the call (the user turned it off, or UiAutomation such as
                    // `uiautomator dump` took accessibility over): neither side read the tree, nothing to compare.
                    retryReason = $"the accessibility service was not connected around the call (legacy {Excerpt(legacy)})";
                    continue;
                }
                if (legacy == before || legacy == after)
                {
                    lines.Add($"{call.Label}: equal{(before != after ? " (screen changed during the call)" : "")}{Size(legacy)}" +
                        (attempt > 1 ? $" (attempt {attempt})" : ""));
                }
                else if (before == after)
                {
                    differ.Add($"{call.Label}: legacy {RelayMailboxExchange.Excerpt(legacy, 300)} | C# {RelayMailboxExchange.Excerpt(before, 300)}");
                }
                else
                {
                    retryReason = "the screen kept changing (C# reads before and after differ, legacy equals neither)";
                    continue;
                }
                break;
            }
            if (retryReason is not null)
            {
                unsure.Add($"{call.Label}: {retryReason}, {MaxAttempts} attempts");
            }
            if (stop)
            {
                break;
            }
        }
        var detail = string.Join("; ", differ.Concat(unsure).Concat(lines));
        if (differ.Count > 0)
        {
            return Report(ShadowVerdict.Diverge, true, null, $"{differ.Count} differ: {detail}");
        }
        if (unsure.Count > 0 || lines.Count == 0)
        {
            return Report(ShadowVerdict.Inconclusive, lines.Count > 0, null, detail);
        }
        return Report(ShadowVerdict.Agree, true, null, $"{lines.Count}/{lines.Count} byte-identical: {detail}");
    }

    private static string Size(string comparable)
    {
        if (!comparable.StartsWith("ok:", StringComparison.Ordinal))
        {
            return $" ({comparable[..comparable.IndexOf(':', StringComparison.Ordinal)]})";
        }
        var nodes = comparable.Split("\"nodeId\":").Length - 1;
        return $" ({comparable.Length - 3} bytes, {nodes.ToString(CultureInfo.InvariantCulture)} nodeId)";
    }

    /// <summary>One call through the real mailbox; resent (new id) while Relay only says the user has not answered yet.</summary>
    private static async Task<string?> CallAsync(string run, ReadCall call, TimeProvider time, DateTimeOffset deadline, CancellationToken ct)
    {
        while (time.GetUtcNow() < deadline)
        {
            var id = "ar-" + Guid.NewGuid().ToString("N")[..12];
            var text = $$"""{"v":1,"id":"{{id}}","capability":"{{call.Capability}}","args":{{call.Args}},"ttlMs":{{CallTtlMs}}}""";
            var inbox = Path.Combine(run, "inbox", id + ".json");
            var tmp = Path.Combine(run, "inbox", "." + id + ".tmp");
            await File.WriteAllTextAsync(tmp, text, ct).ConfigureAwait(false);
            File.Move(tmp, inbox);
            var outbox = Path.Combine(run, "outbox", id + ".json");
            var answerBy = Min(time.GetUtcNow() + TimeSpan.FromSeconds(30), deadline);
            string? reply = null;
            while (reply is null && time.GetUtcNow() < answerBy)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), time, ct).ConfigureAwait(false);
                if (File.Exists(outbox))
                {
                    reply = await File.ReadAllTextAsync(outbox, ct).ConfigureAwait(false);
                    File.Delete(outbox);
                }
            }
            if (reply is null)
            {
                File.Delete(inbox); // never claimed: take it back
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
