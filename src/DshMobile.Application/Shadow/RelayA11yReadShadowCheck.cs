using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
public static partial class RelayA11yReadShadowCheck
{
    public const string Slice = "automation.relay-a11y-read";

    /// <summary>PR-07 segment 8: <c>ui.waitFor</c> through the same bracketed comparison (one more approval card).</summary>
    public const string WaitForSlice = "automation.relay-a11y-waitfor";
    public const long CallTtlMs = 60_000;

    private static readonly HashSet<string> Waiting = new(StringComparer.Ordinal)
    {
        RelayError.GateAwaitingConsent.Code, RelayError.GateWaitingTurn.Code,
    };

    /// <summary>Failures the backend itself produces: these are compared like answers; any other failure is the gate's.</summary>
    private static readonly HashSet<string> BackendCodes = new(StringComparer.Ordinal)
    {
        RelayError.BackendUnavailable.Code, RelayError.NodeNotFound.Code, RelayError.NodeAmbiguous.Code, RelayError.TransportMalformed.Code,
        RelayError.WaitTimeout.Code,
    };

    /// <summary>One read call: the capability, its args, and how C# answers it from a tree.</summary>
    public sealed record ReadCall(string Capability, string Args)
    {
        public Task<BackendResult> AnswerAsync(A11yTree tree, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(tree);
            return Capability switch
            {
                "ui.snapshot" => Task.FromResult(A11ySnapshot.Snapshot(tree)),
                // Legacy hands its backend the request ttl less what the call has spent; every call here asks for at
                // most the default wait, far below that, so cappedToBudget is false on both sides.
                "ui.waitFor" => new NodeReader(tree).WaitForAsync(JsonElement.Parse(Args), CallTtlMs, TimeProvider.System, ct),
                _ => Task.FromResult(new NodeReader(tree).ReadNode(JsonElement.Parse(Args))),
            };
        }

        public BackendResult Answer(A11yTree tree) => AnswerAsync(tree).GetAwaiter().GetResult();

        public string Label => Capability == "ui.snapshot" ? Capability : $"{Capability} {Args}";
    }

    /// <summary>C#'s answer in comparable form: <c>ok:data</c> or <c>CODE:reason</c>.</summary>
    public static string Comparable(BackendResult result) => result switch
    {
        BackendResult.Ok ok => "ok:" + Timeless(RelayEncoding.EncodeData(ok.Data)),
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
                return "ok:" + Timeless(root.TryGetProperty("data", out var data) ? data.GetRawText() : "{}");
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
    /// <c>waitedMs</c> (ui.waitFor) is how long that run happened to wait: the one field two correct implementations
    /// cannot agree on. Every other byte, <c>polls</c> included, is compared.
    /// </summary>
    private static string Timeless(string data) => WaitedMs().Replace(data, "\"waitedMs\":0");

    [GeneratedRegex("\"waitedMs\":-?[0-9]+")]
    private static partial Regex WaitedMs();

    /// <summary>The marker text the ui.waitFor calls look for and must never find.</summary>
    public const string AbsentText = "dshm-shadow: no such text";

    /// <summary>
    /// The ui.waitFor calls for one run (all answer at the first poll, so polls is 1 on both sides): satisfied by id and
    /// by unique text, absence judged (gone) and refused (still visible, relative), a timeout for a missing node, text
    /// and checked mismatches on the first clickable node, and an out-of-range timeout.
    /// </summary>
    public static IReadOnlyList<ReadCall> PickWaitForCalls(A11yTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var calls = new List<ReadCall>
        {
            new("ui.waitFor", """{"nodeId":0,"timeoutMs":0}"""),
            new("ui.waitFor", new JsonObject { ["selector"] = new JsonObject { ["text"] = AbsentText }, ["absent"] = true, ["timeoutMs"] = 0 }.ToJsonString()),
            new("ui.waitFor", new JsonObject { ["selector"] = new JsonObject { ["text"] = AbsentText }, ["timeoutMs"] = 0 }.ToJsonString()),
            new("ui.waitFor", """{"nodeId":0,"relative":{"side":"above"},"absent":true,"timeoutMs":0}"""),
            new("ui.waitFor", """{"nodeId":0,"timeoutMs":15001}"""),
        };
        if (A11ySnapshot.Snapshot(tree) is not BackendResult.Ok { Data: var data } || data["nodes"] is not JsonArray nodes)
        {
            return calls;
        }
        var list = nodes.OfType<JsonObject>().ToList();
        var texts = list.Select(n => (string?)n["text"]).OfType<string>().ToList();
        if (texts.FirstOrDefault(t => texts.Count(x => x == t) == 1) is { } unique)
        {
            calls.Add(new ReadCall("ui.waitFor", new JsonObject { ["selector"] = new JsonObject { ["text"] = unique } }.ToJsonString()));
            calls.Add(new ReadCall("ui.waitFor",
                new JsonObject { ["selector"] = new JsonObject { ["text"] = unique }, ["absent"] = true, ["timeoutMs"] = 0 }.ToJsonString()));
        }
        if (list.FirstOrDefault(n => n["clickable"] is not null && (int)n["nodeId"]! > 0) is { } clickable)
        {
            var id = (int)clickable["nodeId"]!;
            calls.Add(new ReadCall("ui.waitFor", new JsonObject { ["nodeId"] = id, ["text"] = AbsentText, ["timeoutMs"] = 0 }.ToJsonString()));
            calls.Add(new ReadCall("ui.waitFor", new JsonObject { ["nodeId"] = id, ["checked"] = true, ["timeoutMs"] = 0 }.ToJsonString()));
        }
        return calls;
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

    public static Task<ShadowReport> RunAsync(string pilotEntryDir, Func<A11yTree?> hostTree, TimeProvider time, TimeSpan timeout,
        CancellationToken ct) =>
        RunSliceAsync(Slice, PickCalls, pilotEntryDir, hostTree, time, timeout, ct);

    /// <summary>PR-07 segment 8: the ui.waitFor calls through the same comparison.</summary>
    public static Task<ShadowReport> RunWaitForAsync(string pilotEntryDir, Func<A11yTree?> hostTree, TimeProvider time, TimeSpan timeout,
        CancellationToken ct) =>
        RunSliceAsync(WaitForSlice, PickWaitForCalls, pilotEntryDir, hostTree, time, timeout, ct);

    private static async Task<ShadowReport> RunSliceAsync(string slice, Func<A11yTree, IReadOnlyList<ReadCall>> pick, string pilotEntryDir,
        Func<A11yTree?> hostTree, TimeProvider time, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hostTree);
        ArgumentNullException.ThrowIfNull(time);
        var sw = Stopwatch.StartNew();
        if (Interlocked.Exchange(ref s_running, 1) == 1)
        {
            // Two runs would share the one session consent and the same calls: their verdicts would mean nothing.
            return new ShadowReport(slice, time.GetUtcNow(), ShadowVerdict.Inconclusive, false, null, true, null,
                "not run: another accessibility read check is still running", 0);
        }
        try
        {
            return await RunCoreAsync(slice, pick, pilotEntryDir, hostTree, time, timeout, sw, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or IOException or UnauthorizedAccessException))
        {
            // A reader fault (the live tree, a platform call) must end the check visibly, never leave it hanging.
            var at = ex.StackTrace?.Split('\n', 2)[0].Trim();
            return new ShadowReport(slice, time.GetUtcNow(), ShadowVerdict.Inconclusive, false, null, false, ex.GetType().Name,
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

    private static async Task<ShadowReport> RunCoreAsync(string slice, Func<A11yTree, IReadOnlyList<ReadCall>> pick, string pilotEntryDir,
        Func<A11yTree?> hostTree, TimeProvider time, TimeSpan timeout, Stopwatch sw, CancellationToken ct)
    {
        ShadowReport Report(ShadowVerdict verdict, bool legacyOk, string? legacyCode, string detail) =>
            new(slice, time.GetUtcNow(), verdict, legacyOk, legacyCode, true, null, detail, sw.ElapsedMilliseconds);

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
        foreach (var call in pick(first))
        {
            // A call is retried (no card the second time: consent is already given for the session) when the screen
            // moved under it or the service was momentarily gone; only a stable, comparable read can be a difference.
            string? retryReason = null;
            for (var attempt = 1; attempt <= MaxAttempts && !stop; attempt++)
            {
                retryReason = null;
                var before = hostTree() is { } t0 ? Comparable(await call.AnswerAsync(t0, ct).ConfigureAwait(false)) : null;
                var reply = await CallAsync(run, call, time, deadline, ct).ConfigureAwait(false);
                var after = hostTree() is { } t1 ? Comparable(await call.AnswerAsync(t1, ct).ConfigureAwait(false)) : null;
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
