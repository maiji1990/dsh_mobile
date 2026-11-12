using System.Diagnostics;
using System.Text.Json;
using DshMobile.Runtime.Automation.Relay;

namespace DshMobile.Application.Shadow;

/// <summary>One probe request: which channel, the request file name, and its exact text.</summary>
public sealed record RelayProbeCase(bool Control, string File, string Text);

/// <summary>
/// PR-07 segment 2 shadow (developer action): the live legacy Relay against the C# <see cref="RelayEnvelope"/>.
/// Writes a fixed set of requests into the real mailbox (<c>files/pilot/run/inbox</c> and <c>control-inbox</c>) through
/// <see cref="RelayMailboxExchange"/>, and compares each answer, byte for byte (only <c>elapsedMs</c> normalised), with
/// what C# would answer for the same text: <see cref="RelayEncoding.Encode"/> of the mailbox failure (v1) or
/// <see cref="RelayEncoding.EncodeControlRejection"/> with <c>"&lt;code&gt;/&lt;cause&gt;"</c> (v2).
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

    public static async Task<ShadowReport> RunAsync(string pilotEntryDir, TimeProvider time, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pilotEntryDir);
        ArgumentNullException.ThrowIfNull(time);
        ShadowReport Report(ShadowVerdict verdict, bool legacyOk, string? legacyCode, string detail, long ms) =>
            new(Slice, time.GetUtcNow(), verdict, legacyOk, legacyCode, true, null, detail, ms);

        // The C# side first: anything it would accept must never be written into the real mailbox.
        var sw = Stopwatch.StartNew();
        var expected = new Dictionary<RelayProbeRequest, (string Want, string Code, string Cause)>();
        foreach (var c in Cases)
        {
            switch (c.Control ? (object)RelayEnvelope.DecodeControl(c.Text, c.File) : RelayEnvelope.DecodeRequest(c.Text, c.File))
            {
                case RelayDecodeResult.Rejected r:
                    expected[new RelayProbeRequest(false, c.File, c.Text, r.Id)] =
                        (RelayEncoding.Encode(RelayResponse.Failure(r.Id, r.Error, 0)), r.Error.Code, r.Cause);
                    break;
                case ControlDecodeResult.Rejected r:
                    expected[new RelayProbeRequest(true, c.File, c.Text, r.Id)] =
                        (RelayEncoding.EncodeControlRejection(r.Id, $"{r.Error.Code}/{r.Cause}"), r.Error.Code, r.Cause);
                    break;
                default:
                    return Report(ShadowVerdict.Inconclusive, false, null, $"not run: C# accepts probe {c.File}, refusing to write it", sw.ElapsedMilliseconds);
            }
        }
        var csharpMs = sw.ElapsedMilliseconds;

        var result = await RelayMailboxExchange.RunAsync(pilotEntryDir, [.. expected.Keys], time, timeout, ct).ConfigureAwait(false);
        if (!result.MailboxPresent)
        {
            return Report(ShadowVerdict.Inconclusive, false, null, $"not run: no Relay mailbox under {result.RunDir} (start the relay first)", csharpMs);
        }
        var mismatches = new List<string>();
        string? firstLegacyCode = null;
        foreach (var (r, text) in result.Answers)
        {
            var want = expected[r].Want;
            if (RelayMailboxExchange.NormalizeElapsed(text) != want)
            {
                firstLegacyCode ??= LegacySummary(r.Control, text);
                mismatches.Add($"{r.File}: legacy {RelayMailboxExchange.Excerpt(text)} | C# {RelayMailboxExchange.Excerpt(want)}");
            }
        }
        var total = expected.Count;
        var v1 = expected.Keys.Count(r => !r.Control);
        if (mismatches.Count > 0)
        {
            return Report(ShadowVerdict.Diverge, result.Answers.Count == total, firstLegacyCode,
                $"{mismatches.Count}/{result.Answers.Count} answers differ: {string.Join("; ", mismatches.Take(3))}", csharpMs);
        }
        if (result.Answers.Count < total)
        {
            var missing = expected.Keys.Where(r => !result.Answers.ContainsKey(r)).Select(r => r.File);
            return Report(ShadowVerdict.Inconclusive, false, null, result.Answers.Count == 0
                ? $"0/{total} answered within {timeout.TotalSeconds:0.#}s: Relay is not serving the mailbox (start the relay); requests taken back"
                : $"{result.Answers.Count}/{total} answered within {timeout.TotalSeconds:0.#}s, all equal; no answer for {string.Join(", ", missing.Take(6))}", csharpMs);
        }
        return Report(ShadowVerdict.Agree, true, null,
            $"{total}/{total} answers byte-identical (v1 ×{v1}, v2 ×{total - v1}; causes: {string.Join(", ", expected.Values.Select(e => e.Cause).Distinct())})", csharpMs);
    }

    /// <summary>What legacy said, for the report: v1 <c>error.code</c>, v2 <c>cause</c>.</summary>
    internal static string LegacySummary(bool control, string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (control)
            {
                return root.TryGetProperty("cause", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "(no cause)";
            }
            return root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object
                && e.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString()! : "(no error.code)";
        }
        catch (JsonException)
        {
            return "unparsable answer";
        }
    }
}
