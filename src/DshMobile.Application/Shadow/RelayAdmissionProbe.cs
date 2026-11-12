using System.Diagnostics;
using System.Text.Json;
using DshMobile.Runtime.Automation.Relay;

namespace DshMobile.Application.Shadow;

/// <summary>
/// PR-07 segment 3 shadow (developer action): the live legacy Relay's admission steps — registered capability,
/// self-target scan, argument pre-check — and its response encoding, against the C# <see cref="RelayAdmission"/> and
/// <see cref="RelayEncoding"/>. Requests are well-formed envelopes that admission must reject; each answer is compared
/// byte for byte (only <c>elapsedMs</c> normalised) with the C# rejection.
/// <para>
/// Safety: a rejection here happens before the surface policy, the approval gate and any backend. The probe refuses to
/// write anything unless C# decodes it as valid AND admission rejects it; on top of that every request carries
/// <c>ttlMs: 5000</c>, so even a request that wrongly got past admission is cut off by the mailbox within ~3.75 s with no
/// one having approved it. Answers of <c>E_RATE_LIMITED/entry_flood</c> (Relay's per-minute counter, state C# does not
/// share) are reported Inconclusive, not as a difference.
/// </para>
/// </summary>
public static class RelayAdmissionProbe
{
    public const string Slice = "automation.relay-admission";
    public const long ProbeTtlMs = 5_000;

    /// <summary>(file stem, capability, args JSON); <c>{self}</c> is replaced by this app's package.</summary>
    public static IReadOnlyList<(string Id, string Capability, string Args)> Templates { get; } =
    [
        ("pa-tap", "ui.tap", """{"x":1,"y":2}"""),
        ("pa-shell", "sys.shell", """{"verb":"id"}"""),
        ("pa-unknown", "ui.snapshot", """{"foo":1}"""),
        ("pa-display", "ui.snapshot", """{"display":0}"""),
        ("pa-takes", "ui.node", """{"bogus":true}"""),
        ("pa-idstr", "ui.node", """{"nodeId":"5"}"""),
        ("pa-idfrac", "ui.node", """{"nodeId":1.5}"""),
        ("pa-idnull", "ui.node", """{"nodeId":null}"""),
        ("pa-selarr", "ui.node", """{"selector":[1,"a/b"]}"""),
        ("pa-self", "ui.node", """{"selector":{"package":"{self}"}}"""),
        ("pa-selfx", "ui.snapshot", """{"x":"{self}"}"""),
        ("pa-first", "ui.node", """{"zzz":1,"aaa":2}"""),
    ];

    public static string RequestText(string id, string capability, string args) =>
        $$"""{"v":1,"id":"{{id}}","capability":"{{capability}}","args":{{args}},"ttlMs":{{ProbeTtlMs}}}""";

    public static async Task<ShadowReport> RunAsync(string pilotEntryDir, string selfPackage, IEnumerable<string> registered,
        TimeProvider time, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(selfPackage);
        ArgumentNullException.ThrowIfNull(time);
        ShadowReport Report(ShadowVerdict verdict, bool legacyOk, string? legacyCode, string detail, long ms) =>
            new(Slice, time.GetUtcNow(), verdict, legacyOk, legacyCode, true, null, detail, ms);

        var sw = Stopwatch.StartNew();
        // A fresh limiter: the probe's own requests never reach the C# entry limit; Relay's counter is its own.
        var admission = new RelayAdmission(registered, selfPackage, new RelayRateLimiter(() => 0, RelayRateLimiter.EntryLimit));
        var expected = new Dictionary<RelayProbeRequest, (string Want, RelayResponse Rejection)>();
        foreach (var (id, capability, args) in Templates)
        {
            var text = RequestText(id, capability, args.Replace("{self}", selfPackage, StringComparison.Ordinal));
            if (RelayEnvelope.DecodeRequest(text, id + RelayEnvelope.JsonSuffix) is not RelayDecodeResult.Valid valid)
            {
                return Report(ShadowVerdict.Inconclusive, false, null, $"not run: probe {id} is not a valid envelope", sw.ElapsedMilliseconds);
            }
            if (admission.Check(valid.Request) is not { } rejection)
            {
                return Report(ShadowVerdict.Inconclusive, false, null, $"not run: C# admits probe {id}, refusing to write it", sw.ElapsedMilliseconds);
            }
            expected[new RelayProbeRequest(false, id + RelayEnvelope.JsonSuffix, text, id)] = (RelayEncoding.Encode(rejection), rejection);
        }
        var csharpMs = sw.ElapsedMilliseconds;

        var result = await RelayMailboxExchange.RunAsync(pilotEntryDir, [.. expected.Keys], time, timeout, ct).ConfigureAwait(false);
        if (!result.MailboxPresent)
        {
            return Report(ShadowVerdict.Inconclusive, false, null, $"not run: no Relay mailbox under {result.RunDir} (start the relay first)", csharpMs);
        }
        var mismatches = new List<string>();
        var limited = new List<string>();
        string? firstLegacyCode = null;
        foreach (var (r, text) in result.Answers)
        {
            var want = expected[r].Want;
            if (RelayMailboxExchange.NormalizeElapsed(text) == want)
            {
                continue;
            }
            if (IsEntryFlood(text))
            {
                limited.Add(r.File);
                continue;
            }
            firstLegacyCode ??= RelayEnvelopeProbe.LegacySummary(false, text);
            mismatches.Add($"{r.File}: legacy {RelayMailboxExchange.Excerpt(text, 240)} | C# {RelayMailboxExchange.Excerpt(want, 240)}");
        }
        var total = expected.Count;
        if (mismatches.Count > 0)
        {
            return Report(ShadowVerdict.Diverge, result.Answers.Count == total, firstLegacyCode,
                $"{mismatches.Count}/{result.Answers.Count} answers differ: {string.Join("; ", mismatches.Take(3))}", csharpMs);
        }
        if (result.Answers.Count < total || limited.Count > 0)
        {
            var missing = expected.Keys.Where(r => !result.Answers.ContainsKey(r)).Select(r => r.File);
            return Report(ShadowVerdict.Inconclusive, false, limited.Count > 0 ? RelayError.RateLimited.Code : null,
                $"{result.Answers.Count - limited.Count}/{total} equal; "
                + (limited.Count > 0 ? $"entry flood limit hit for {string.Join(", ", limited)} (wait a minute); " : "")
                + (result.Answers.Count < total ? $"no answer within {timeout.TotalSeconds:0.#}s for {string.Join(", ", missing.Take(6))}" : ""), csharpMs);
        }
        var codes = expected.Values.GroupBy(e => e.Rejection.Error!.Code).Select(g => $"{g.Key} ×{g.Count()}");
        return Report(ShadowVerdict.Agree, true, null, $"{total}/{total} answers byte-identical ({string.Join(", ", codes)})", csharpMs);
    }

    private static bool IsEntryFlood(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                && r.GetString() == RelayError.RateLimited.Code + "/entry_flood";
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
