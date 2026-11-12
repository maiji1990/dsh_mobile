using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DshMobile.Runtime.Automation.Relay;

namespace DshMobile.Application.Shadow;

/// <summary>
/// What Android tells this app directly about the facts the Relay system-state probe reads. Whether the accessibility
/// service or the notification listener is <em>bound</em> lives in the legacy process and is not here: the shadow
/// accepts either answer where binding alone would change it, and says so.
/// </summary>
public sealed record RelayDeviceFacts(
    int SdkInt,
    bool A11yListed,
    bool NotificationListenerListed,
    IReadOnlySet<string> GrantedPermissions,
    bool MicrophoneBlocked);

/// <summary>
/// PR-07 segment 5 shadow, both halves read-only with respect to the user's decisions.
/// <para>
/// <b>Passive</b> (<see cref="Compare"/>): Relay publishes, per registered capability, the system state it probed on
/// the decided route (<c>systemPermission</c>), the stored tier (<c>assistantTier</c>), the ceiling and <c>usable</c>
/// (upstream <c>RelayContainer.capabilityRows</c>). The C# probe, tier store and policy compute the same from the
/// same facts and must agree.
/// </para>
/// <para>
/// <b>Live</b> (<see cref="RunProbeAsync"/>): two read-only <c>ui.*</c> calls are put into the mailbox only when the
/// gate must refuse them for a missing system permission. That means the accessibility service is not listed in
/// Settings, Relay itself published <c>MANUAL_ONLY</c> for both, the tier is not ALWAYS, and the C# chain (admission →
/// surface → probe → gate) predicts exactly that refusal. Relay's answer is compared byte for byte. The refusal comes
/// before any prompt or backend. Even a request that wrongly got through would carry <c>ttlMs 5000</c>, which leaves
/// the gate about 2.25 s, under the 5 s a prompt needs, so it cannot reach the user. Relay writes one audit record per
/// refusal.
/// </para>
/// </summary>
public static class RelayGateShadowCheck
{
    public const string Slice = "automation.relay-gate";
    public const long ProbeTtlMs = 5_000;

    /// <summary>Upstream <c>ttlMs × 0.75</c> mailbox budget minus the 1.5 s approval reply margin.</summary>
    private const long ProbeGateBudgetMs = ProbeTtlMs * 3 / 4 - 1_500;

    public static IReadOnlyList<(string Id, string Capability, string Args)> ProbeTemplates { get; } =
    [
        ("pg-snapshot", "ui.snapshot", "{}"),
        ("pg-node", "ui.node", """{"nodeId":0}"""),
    ];

    public static ShadowReport Compare(string? capabilitiesJson, RelayDeviceFacts device, Func<string, string?> readRelayPref,
        IReadOnlyCollection<string> registered, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(readRelayPref);
        ArgumentNullException.ThrowIfNull(registered);
        ShadowReport Report(ShadowVerdict verdict, bool legacyOk, string? legacyCode, string detail) =>
            new(Slice, at, verdict, legacyOk, legacyCode, true, null, detail + Context(device), 0);

        if (Load(capabilitiesJson, device, readRelayPref) is not { } env)
        {
            return Report(ShadowVerdict.Inconclusive, false, null, LoadProblem(capabilitiesJson));
        }
        if (env.Unknown is { } why)
        {
            return Report(ShadowVerdict.Inconclusive, false, null, why);
        }
        var mismatches = new List<string>();
        var bindingDependent = new List<string>();
        var seen = new List<string>();
        foreach (var row in env.Rows.EnumerateArray())
        {
            var id = RelaySurfaceShadowCheck.Str(row, "id");
            if (id is null || !RelayProtocol.IsCapability(id))
            {
                mismatches.Add($"row with unknown id {id ?? "(none)"}");
                continue;
            }
            seen.Add(id);
            var decision = env.Policy.Decide(id);
            var candidates = Candidates(id, decision.Surface, env.Facts);
            var legacyState = RelaySurfaceShadowCheck.Str(row, "systemPermission");
            var matched = candidates.FirstOrDefault(c => Upper(c.State) == legacyState);
            if (matched is null)
            {
                mismatches.Add($"{id} systemPermission@{decision.Surface.Wire()}: legacy {legacyState ?? "none"} C# {string.Join("|", candidates.Select(c => Upper(c.State)).Distinct())}");
            }
            else if (!ReferenceEquals(matched, candidates[0]))
            {
                bindingDependent.Add($"{id} {legacyState}");
            }
            var tier = env.Tiers.TierOf(id);
            if (RelaySurfaceShadowCheck.Str(row, "assistantTier") != Upper(tier))
            {
                mismatches.Add($"{id} assistantTier: legacy {RelaySurfaceShadowCheck.Str(row, "assistantTier") ?? "none"} C# {Upper(tier)}");
            }
            var ceiling = RelayGatePolicy.CeilingOf(id);
            if (RelaySurfaceShadowCheck.Str(row, "ceiling") != Upper(ceiling))
            {
                mismatches.Add($"{id} ceiling: legacy {RelaySurfaceShadowCheck.Str(row, "ceiling") ?? "none"} C# {Upper(ceiling)}");
            }
            // usable needs the backend's own readiness too, which C# does not see; one way is checkable: whatever the
            // gate would refuse (system blocks, tier denied) or the policy cannot run (mode required) is not usable.
            var state = (matched ?? candidates[0]).State;
            var refused = RelayGatePolicy.SystemBlocks(state) || tier == AccessTier.Denied || decision.RequiresMode is not null;
            if (refused && RelaySurfaceShadowCheck.Bool(row, "usable") == true)
            {
                mismatches.Add($"{id}: legacy usable, C# gate refuses ({Upper(state)}, {Upper(tier)})");
            }
        }
        var missing = registered.Where(w => !seen.Contains(w)).ToList();
        var extra = seen.Where(w => !registered.Contains(w)).ToList();
        if (missing.Count > 0 || extra.Count > 0)
        {
            mismatches.Add($"capability rows: missing [{string.Join(",", missing)}] extra [{string.Join(",", extra)}]");
        }
        if (mismatches.Count > 0)
        {
            return Report(ShadowVerdict.Diverge, true, mismatches[0], $"{mismatches.Count} differ: {string.Join("; ", mismatches.Take(4))}");
        }
        if (seen.Count == 0)
        {
            return Report(ShadowVerdict.Inconclusive, true, null, "capabilities.json lists no capability");
        }
        var states = string.Join(", ", seen.GroupBy(id => RelaySurfaceShadowCheck.Str(Row(env.Rows, id), "systemPermission"))
            .Select(g => $"{g.Key} ×{g.Count()}"));
        var tiers = string.Join(", ", seen.GroupBy(id => Upper(env.Tiers.TierOf(id))).Select(g => $"{g.Key} ×{g.Count()}"));
        var binding = bindingDependent.Count == 0
            ? ""
            : $"; {bindingDependent.Count} only explained by a binding C# cannot see ({string.Join(", ", bindingDependent.Take(4))})";
        return Report(ShadowVerdict.Agree, true, null, $"{seen.Count}/{seen.Count} rows agree on system state, tier and ceiling (states {states}; tiers {tiers}){binding}");
    }

    public static string ProbeRequestText(string id, string capability, string args) =>
        $$"""{"v":1,"id":"{{id}}","capability":"{{capability}}","args":{{args}},"ttlMs":{{ProbeTtlMs}}}""";

    public static async Task<ShadowReport> RunProbeAsync(string pilotEntryDir, string? capabilitiesJson, RelayDeviceFacts device,
        Func<string, string?> readRelayPref, string selfPackage, IEnumerable<string> registered, TimeProvider time, TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(readRelayPref);
        ArgumentNullException.ThrowIfNull(selfPackage);
        ArgumentNullException.ThrowIfNull(time);
        var sw = Stopwatch.StartNew();
        ShadowReport Report(ShadowVerdict verdict, bool legacyOk, string? legacyCode, string detail) =>
            new(Slice, time.GetUtcNow(), verdict, legacyOk, legacyCode, true, null, "live probe: " + detail, sw.ElapsedMilliseconds);

        if (device.A11yListed)
        {
            return Report(ShadowVerdict.Inconclusive, false, null,
                "not run: the accessibility service is listed in Settings, so the gate would not refuse these calls");
        }
        if (Load(capabilitiesJson, device, readRelayPref) is not { } env)
        {
            return Report(ShadowVerdict.Inconclusive, false, null, "not run: " + LoadProblem(capabilitiesJson));
        }
        if (env.Unknown is { } why)
        {
            return Report(ShadowVerdict.Inconclusive, false, null, "not run: " + why);
        }
        var stage = new RelayExecutionStage(env.Policy, w => RelayBackendSupport.IsImplemented(w, env.BackendFacts),
            new RelayRateLimiter(() => 0, RelayRateLimiter.ExecutionLimit), selfPackage);
        var admission = new RelayAdmission(registered, selfPackage, new RelayRateLimiter(() => 0, RelayRateLimiter.EntryLimit));
        var gate = new RelayGate(env.Tiers, () => Environment.TickCount64);
        var expected = new Dictionary<RelayProbeRequest, string>();
        foreach (var (id, capability, args) in ProbeTemplates)
        {
            var legacyState = RelaySurfaceShadowCheck.Str(Row(env.Rows, capability), "systemPermission");
            if (legacyState != "MANUAL_ONLY")
            {
                return Report(ShadowVerdict.Inconclusive, false, null, $"not run: Relay publishes {capability} as {legacyState ?? "absent"}, not MANUAL_ONLY");
            }
            if (env.Tiers.TierOf(capability) == AccessTier.Always)
            {
                return Report(ShadowVerdict.Inconclusive, false, null, $"not run: {capability} is set to ALWAYS");
            }
            var text = ProbeRequestText(id, capability, args);
            if (RelayEnvelope.DecodeRequest(text, id + RelayEnvelope.JsonSuffix) is not RelayDecodeResult.Valid valid)
            {
                return Report(ShadowVerdict.Inconclusive, false, null, $"not run: probe {id} is not a valid envelope");
            }
            if (admission.Check(valid.Request) is { } early)
            {
                return Report(ShadowVerdict.Inconclusive, false, null, $"not run: C# admission rejects probe {id} ({early.Error?.Code})");
            }
            var step = stage.BeforeGate(valid.Request);
            if (step.Rejection is { } before)
            {
                return Report(ShadowVerdict.Inconclusive, false, null, $"not run: C# refuses probe {id} before the gate ({before.Error?.Code})");
            }
            var system = RelaySystemProbe.Probe(capability, step.Decision.Surface, env.Facts);
            var decided = gate.DecideWithoutPrompt(new GateCall(capability, args, system, ProbeGateBudgetMs));
            if (decided is not { Allowed: false } || decided.Error != RelayError.GateSystemMissing)
            {
                return Report(ShadowVerdict.Inconclusive, false, null, $"not run: C# gate does not predict system_missing for {id}");
            }
            var rejection = stage.AfterGate(valid.Request, step.Decision, decided)!;
            expected[new RelayProbeRequest(false, id + RelayEnvelope.JsonSuffix, text, id)] = RelayEncoding.Encode(rejection);
        }
        var csharpMs = sw.ElapsedMilliseconds;

        var result = await RelayMailboxExchange.RunAsync(pilotEntryDir, [.. expected.Keys], time, timeout, ct).ConfigureAwait(false);
        if (!result.MailboxPresent)
        {
            return Report(ShadowVerdict.Inconclusive, false, null, $"not run: no Relay mailbox under {result.RunDir} (start the relay first)");
        }
        var mismatches = new List<string>();
        string? firstLegacyCode = null;
        var limited = 0;
        foreach (var (r, text) in result.Answers)
        {
            var want = expected[r];
            if (RelayMailboxExchange.NormalizeElapsed(text) == want)
            {
                continue;
            }
            if (text.Contains("\"reason\":\"E_RATE_LIMITED/entry_flood\"", StringComparison.Ordinal))
            {
                limited++;
                continue;
            }
            firstLegacyCode ??= RelayEnvelopeProbe.LegacySummary(false, text);
            mismatches.Add($"{r.File}: legacy {RelayMailboxExchange.Excerpt(text, 240)} | C# {RelayMailboxExchange.Excerpt(want, 240)}");
        }
        var total = expected.Count;
        if (mismatches.Count > 0)
        {
            return Report(ShadowVerdict.Diverge, result.Answers.Count == total, firstLegacyCode,
                $"{mismatches.Count}/{result.Answers.Count} answers differ: {string.Join("; ", mismatches)}") with { CSharpMs = csharpMs };
        }
        if (result.Answers.Count < total || limited > 0)
        {
            return Report(ShadowVerdict.Inconclusive, false, limited > 0 ? RelayError.RateLimited.Code : null,
                $"{result.Answers.Count - limited}/{total} equal" + (limited > 0 ? "; entry flood limit hit (wait a minute)" : "")
                + (result.Answers.Count < total ? $"; no answer within {timeout.TotalSeconds:0.#}s" : "")) with { CSharpMs = csharpMs };
        }
        return Report(ShadowVerdict.Agree, true, null,
            $"{total}/{total} answers byte-identical (E_GATE_SYSTEM_MISSING/system_missing, before any prompt)") with { CSharpMs = csharpMs };
    }

    private sealed record Env(JsonElement Rows, RelaySystemFacts Facts, RelayBackendFacts BackendFacts, SurfacePolicy Policy, RelayTierStore Tiers,
        string? Unknown);

    private static Env? Load(string? capabilitiesJson, RelayDeviceFacts device, Func<string, string?> readRelayPref)
    {
        if (capabilitiesJson is null)
        {
            return null;
        }
        JsonElement root;
        try
        {
            root = JsonElement.Parse(capabilitiesJson);
        }
        catch (JsonException)
        {
            return null;
        }
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("capabilities", out var rows) || rows.ValueKind != JsonValueKind.Array
            || !RelaySurfaceShadowCheck.TryShizuku(root, out var running, out var bound, out var alive, out var displayId))
        {
            return null;
        }
        // Same rule as the surface shadow: unbound with a display number on record leaves the trusted display unknown.
        var unknown = running && !bound && displayId >= 0
            ? $"Shizuku running but unbound with display {displayId}: trusted display state unknown"
            : null;
        // Upstream: shellAvailable = Shizuku server running; trustedDisplayReady = running && display alive.
        var facts = new RelaySystemFacts(device.SdkInt, A11yBound: false, device.A11yListed, ShellAvailable: running,
            TrustedDisplayAvailable: running && alive, device.NotificationListenerListed, device.GrantedPermissions, device.MicrophoneBlocked);
        var backend = new RelayBackendFacts(device.SdkInt, running, alive);
        var policy = SurfacePolicy.For(RelaySurfaces.ParsePreference(readRelayPref(RelaySurfaces.PreferenceKey)), backend);
        return new Env(rows, facts, backend, policy, new RelayTierStore(readRelayPref), unknown);
    }

    private static string LoadProblem(string? capabilitiesJson) => capabilitiesJson is null
        ? "no capabilities.json (start the relay first)"
        : "capabilities.json unparsable or lacks capabilities[] / backend.shizuku";

    /// <summary>
    /// The C# answer first, then the answers a binding C# cannot see would give (accessibility service bound while
    /// Settings lags, notification listener connected while unlisted). Legacy must match one of them.
    /// </summary>
    private static List<ProbeResult> Candidates(string id, SurfaceKind route, RelaySystemFacts facts)
    {
        var list = new List<ProbeResult> { RelaySystemProbe.Probe(id, route, facts) };
        list.Add(RelaySystemProbe.Probe(id, route, facts with { A11yBound = true }));
        if (!facts.NotificationListener)
        {
            list.Add(RelaySystemProbe.Probe(id, route, facts with { NotificationListener = true }));
        }
        return list;
    }

    private static JsonElement Row(JsonElement rows, string id) =>
        rows.EnumerateArray().FirstOrDefault(r => RelaySurfaceShadowCheck.Str(r, "id") == id);

    private static string Context(RelayDeviceFacts d) =>
        $" [sdk {d.SdkInt}, accessibility {(d.A11yListed ? "listed" : "not listed")}, " +
        $"listener {(d.NotificationListenerListed ? "listed" : "not listed")}, {d.GrantedPermissions.Count} permissions granted" +
        (d.MicrophoneBlocked ? ", microphone blocked" : "") + "]";

    /// <summary>Enum name → upstream UPPER_SNAKE (SessionOnly → SESSION_ONLY).</summary>
    public static string Upper<T>(T value) where T : struct, Enum
    {
        var name = value.ToString();
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                sb.Append('_');
            }
            sb.Append(char.ToUpperInvariant(name[i]));
        }
        return sb.ToString();
    }
}
