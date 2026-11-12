using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DshMobile.Runtime.Automation.Relay;

namespace DshMobile.Application.Shadow;

/// <summary>One probe request: which channel, the request file name, and its exact text.</summary>
public sealed record RelayProbeCase(bool Control, string File, string Text);

/// <summary>
/// PR-07 segment 2 shadow (developer action): the live legacy Relay against the C# <see cref="RelayEnvelope"/>.
/// Writes a fixed set of requests into the real mailbox (<c>files/pilot/run/inbox</c> and <c>control-inbox</c>), the
/// way the guest CLI does (<c>.&lt;name&gt;.part</c>, then rename), waits for Relay's answers and compares them with
/// what the C# decoder says about the same text: v1 <c>error.code</c>, v2 <c>cause</c> (<c>"&lt;code&gt;/&lt;cause&gt;"</c>).
/// <para>
/// Safety: every probe request is malformed on purpose and is rejected while the envelope is decoded, before any
/// approval, gate or backend (the shared vectors mark them <c>probe</c> and a test keeps them rejections; the probe
/// itself refuses to write anything the C# decoder would accept). Only files named after the probe cases are created
/// or deleted; unanswered requests are taken back out of the inbox.
/// </para>
/// </summary>
public static class RelayEnvelopeProbe
{
    public const string Slice = "automation.relay-envelope";

    private static readonly string Digest = string.Concat(Enumerable.Repeat("a1", 32));

    /// <summary>Same texts as the <c>probe</c> cases in tests/TestVectors/relay-envelope.json (checked by a test).</summary>
    public static IReadOnlyList<RelayProbeCase> Cases { get; } =
    [
        new(false, "pv1-version.json", """{"v":2,"id":"pv1-version","capability":"ui.snapshot","args":{}}"""),
        new(false, "pv1-mismatch.json", """{"v":1,"id":"other","capability":"ui.snapshot","args":{}}"""),
        new(false, "pv1-badid.json", """{"v":1,"id":"a/b","capability":"ui.snapshot","args":{}}"""),
        new(false, "pv1-cap.json", """{"v":1,"id":"pv1-cap","capability":"ui.hack","args":{}}"""),
        new(false, "pv1-noargs.json", """{"v":1,"id":"pv1-noargs","capability":"ui.snapshot"}"""),
        new(false, "pv1-array.json", "[]"),
        new(false, "pv1-big.json", """{"v":1,"id":"pv1-big","capability":"ui.snapshot","args":{}}""".PadRight(RelayEnvelope.MaxJsonChars + 1)),
        new(false, "pv1-deep.json", """{"v":1,"id":"pv1-deep","capability":"ui.snapshot","args":{"x":""" + new string('[', 23) + new string(']', 23) + "}}"),
        new(true, "pv2-digest.json", $$"""{"v":2,"id":"pv2-digest","op":"submit","capability":"ui.node","args":{},"payloadDigest":"{{Digest.ToUpperInvariant()}}"}"""),
        new(true, "pv2-cap.json", $$"""{"v":2,"id":"pv2-cap","op":"submit","capability":"sys.root","args":{},"payloadDigest":"{{Digest}}"}"""),
        new(true, "pv2-target.json", """{"v":2,"id":"pv2-target","op":"status","targetId":"a b"}"""),
        new(true, "pv2-op.json", """{"v":2,"id":"pv2-op","op":"exec"}"""),
        new(true, "pv2-version.json", """{"v":1,"id":"pv2-version","op":"health"}"""),
        new(true, "pv2-array.json", "[1,2]"),
        new(true, "pv2-big.json", """{"v":2,"id":"pv2-big","op":"health"}""".PadRight(RelayEnvelope.MaxControlChars + 1)),
        new(true, "pv2-ask1.json", """{"v":2,"id":"pv2-ask1","op":"ask","question":"q","options":["a"]}"""),
    ];

    private sealed record Expected(RelayProbeCase Case, string Id, string Code, string Cause);

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task<ShadowReport> RunAsync(string pilotEntryDir, TimeProvider time, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pilotEntryDir);
        ArgumentNullException.ThrowIfNull(time);
        var run = Path.Combine(pilotEntryDir, "run");
        var inbox = Path.Combine(run, "inbox");
        var outbox = Path.Combine(run, "outbox");
        var controlInbox = Path.Combine(run, "control-inbox");
        var controlOutbox = Path.Combine(run, "control-outbox");
        ShadowReport Report(ShadowVerdict verdict, bool legacyOk, string? legacyCode, string detail, long ms) =>
            new(Slice, time.GetUtcNow(), verdict, legacyOk, legacyCode, true, null, detail, ms);

        // The C# side first: anything it would accept must never be written into the real mailbox.
        var sw = Stopwatch.StartNew();
        var expected = new List<Expected>(Cases.Count);
        foreach (var c in Cases)
        {
            switch (c.Control ? (object)RelayEnvelope.DecodeControl(c.Text, c.File) : RelayEnvelope.DecodeRequest(c.Text, c.File))
            {
                case RelayDecodeResult.Rejected r:
                    expected.Add(new Expected(c, r.Id, r.Error.Code, r.Cause));
                    break;
                case ControlDecodeResult.Rejected r:
                    expected.Add(new Expected(c, r.Id, r.Error.Code, r.Cause));
                    break;
                default:
                    return Report(ShadowVerdict.Inconclusive, false, null, $"not run: C# accepts probe {c.File}, refusing to write it", sw.ElapsedMilliseconds);
            }
        }
        var csharpMs = sw.ElapsedMilliseconds;

        if (!Directory.Exists(inbox) || !Directory.Exists(outbox) || !Directory.Exists(controlInbox) || !Directory.Exists(controlOutbox))
        {
            return Report(ShadowVerdict.Inconclusive, false, null, $"not run: no Relay mailbox under {run} (start the relay first)", csharpMs);
        }

        string InboxOf(Expected e) => Path.Combine(e.Case.Control ? controlInbox : inbox, e.Case.File);
        string ResponseOf(Expected e) => Path.Combine(e.Case.Control ? controlOutbox : outbox, e.Id + RelayEnvelope.JsonSuffix);

        var answers = new Dictionary<Expected, string>();
        try
        {
            foreach (var e in expected)
            {
                File.Delete(ResponseOf(e)); // a stale answer from an earlier probe must not count
            }
            foreach (var e in expected)
            {
                var dir = Path.GetDirectoryName(InboxOf(e))!;
                var part = Path.Combine(dir, "." + e.Case.File + ".part"); // Relay skips dot names
                await File.WriteAllTextAsync(part, e.Case.Text, Utf8, ct).ConfigureAwait(false);
                File.Move(part, InboxOf(e), overwrite: true);
            }

            var deadline = time.GetUtcNow() + timeout;
            while (answers.Count < expected.Count && time.GetUtcNow() < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), time, ct).ConfigureAwait(false);
                foreach (var e in expected.Where(e => !answers.ContainsKey(e)))
                {
                    if (TryRead(ResponseOf(e)) is { } text)
                    {
                        answers[e] = text;
                    }
                }
            }
        }
        finally
        {
            foreach (var e in expected)
            {
                TryDelete(InboxOf(e)); // still unclaimed: take it back
                TryDelete(Path.Combine(Path.GetDirectoryName(InboxOf(e))!, "." + e.Case.File + ".part"));
                TryDelete(ResponseOf(e));
            }
        }

        var mismatches = new List<string>();
        string? firstLegacyCode = null;
        foreach (var (e, text) in answers)
        {
            var (ok, legacy) = Check(e, text);
            if (!ok)
            {
                firstLegacyCode ??= legacy;
                mismatches.Add($"{e.Case.File}: legacy {legacy}, C# {Want(e)}");
            }
        }
        var v1 = expected.Count(e => !e.Case.Control);
        var v2 = expected.Count - v1;
        if (mismatches.Count > 0)
        {
            return Report(ShadowVerdict.Diverge, answers.Count == expected.Count, firstLegacyCode,
                $"{mismatches.Count}/{answers.Count} answers differ: {string.Join("; ", mismatches.Take(4))}", csharpMs);
        }
        if (answers.Count < expected.Count)
        {
            var missing = expected.Where(e => !answers.ContainsKey(e)).Select(e => e.Case.File);
            return Report(ShadowVerdict.Inconclusive, false, null, answers.Count == 0
                ? $"0/{expected.Count} answered within {timeout.TotalSeconds:0.#}s: Relay is not serving the mailbox (start the relay); requests taken back"
                : $"{answers.Count}/{expected.Count} answered within {timeout.TotalSeconds:0.#}s, all equal; no answer for {string.Join(", ", missing.Take(6))}", csharpMs);
        }
        return Report(ShadowVerdict.Agree, true, null,
            $"{expected.Count}/{expected.Count} rejections equal (v1 error.code ×{v1}, v2 code/cause ×{v2}; causes: {string.Join(", ", expected.Select(e => e.Cause).Distinct())})", csharpMs);
    }

    private static string Want(Expected e) => e.Case.Control ? $"{e.Code}/{e.Cause}" : e.Code;

    /// <summary>Relay's answer against the C# rejection; returns what legacy said for the report.</summary>
    private static (bool Ok, string Legacy) Check(Expected e, string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
            var okFalse = root.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.False;
            if (e.Case.Control)
            {
                var cause = root.TryGetProperty("cause", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                var op = root.TryGetProperty("op", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                var legacy = $"{cause ?? "(no cause)"}{(okFalse ? "" : " ok!=false")}{(id == e.Id ? "" : $" id={id}")}{(op == "unknown" ? "" : $" op={op}")}";
                return (okFalse && id == e.Id && op == "unknown" && cause == Want(e), legacy);
            }
            var code = root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object
                && err.TryGetProperty("code", out var cd) && cd.ValueKind == JsonValueKind.String ? cd.GetString() : null;
            var v1Legacy = $"{code ?? "(no error.code)"}{(okFalse ? "" : " ok!=false")}{(id == e.Id ? "" : $" id={id}")}";
            return (okFalse && id == e.Id && code == e.Code, v1Legacy);
        }
        catch (JsonException)
        {
            return (false, "unparsable answer");
        }
    }

    private static string? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path, Utf8) : null;
        }
        catch (IOException)
        {
            return null; // being renamed into place: next round
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort; never fails the probe
        }
    }
}
