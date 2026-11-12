using System.Text.Json;
using DshMobile.Runtime.Automation.Relay;

namespace DshMobile.Application.Shadow;

/// <summary>
/// PR-07 segment 4 shadow (read-only): the live legacy Relay's execution-surface decision against the C#
/// <see cref="SurfacePolicy"/> / <see cref="RelayBackendSupport"/>. Relay publishes its decision for every registered
/// capability in <c>files/pilot/capabilities.json</c> (upstream <c>RelayContainer.capabilitiesJson</c>, generated from
/// <c>capabilityRows()</c>: <c>policy.decide</c> per row). The C# side decides from the same environment facts (the
/// stored surface preference, the Android SDK level, and the Shizuku / trusted-display state Relay wrote into the same
/// file) and must agree on the declared surfaces, on <c>implemented</c>, on <c>degradeReason</c> and, where Relay says
/// the row is usable, on <c>surface</c>. Nothing is written anywhere: no mailbox request, no approval, no preference.
/// </summary>
public static class RelaySurfaceShadowCheck
{
    public const string Slice = "automation.relay-surface";

    public static ShadowReport Compare(string? capabilitiesJson, string? preferenceRaw, int sdkInt, IReadOnlyCollection<string> registered, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(registered);
        var preference = RelaySurfaces.ParsePreference(preferenceRaw);
        ShadowReport Report(ShadowVerdict verdict, bool legacyOk, string? legacyCode, string detail) =>
            new(Slice, at, verdict, legacyOk, legacyCode, true, null, $"{detail} [preference {preference.Name()}, sdk {sdkInt}]", 0);

        if (capabilitiesJson is null)
        {
            return Report(ShadowVerdict.Inconclusive, false, null, "no capabilities.json (start the relay first)");
        }
        JsonElement root;
        try
        {
            root = JsonElement.Parse(capabilitiesJson);
        }
        catch (JsonException ex)
        {
            return Report(ShadowVerdict.Inconclusive, false, null, "capabilities.json unparsable: " + ex.Message);
        }
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("capabilities", out var rows) || rows.ValueKind != JsonValueKind.Array
            || !TryShizuku(root, out var running, out var bound, out var alive, out var displayId))
        {
            return Report(ShadowVerdict.Inconclusive, false, null, "capabilities.json lacks capabilities[] or backend.shizuku");
        }
        // Relay writes alive = bound && displayAlive(); its policy reads displayAlive() alone. Unbound with a display
        // number on record is the one state where the two can differ: no basis to compare, say so.
        if (running && !bound && displayId >= 0)
        {
            return Report(ShadowVerdict.Inconclusive, false, null, $"Shizuku running but unbound with display {displayId}: trusted display state unknown");
        }
        var facts = new RelayBackendFacts(sdkInt, running, alive);
        var policy = SurfacePolicy.For(preference, facts);

        var mismatches = new List<string>();
        var seen = new List<string>();
        var surfaceCompared = 0;
        foreach (var row in rows.EnumerateArray())
        {
            var id = Str(row, "id");
            if (id is null || !RelayProtocol.IsCapability(id))
            {
                mismatches.Add($"row with unknown id {id ?? "(none)"}");
                continue;
            }
            seen.Add(id);
            var declared = row.TryGetProperty("surfaces", out var s) && s.ValueKind == JsonValueKind.Array
                ? string.Join(",", s.EnumerateArray().Select(x => x.GetString()))
                : "(missing)";
            var want = string.Join(",", RelaySurfaces.SurfacesOf(id).Select(k => k.Wire()));
            if (declared != want)
            {
                mismatches.Add($"{id} surfaces: legacy [{declared}] C# [{want}]");
            }
            var implemented = RelayBackendSupport.IsImplemented(id, facts);
            if (Bool(row, "implemented") != implemented)
            {
                mismatches.Add($"{id} implemented: legacy {Bool(row, "implemented")?.ToString() ?? "?"} C# {implemented}");
            }
            var decision = policy.Decide(id);
            var modeRequired = decision.RequiresMode is not null;
            var wantDegrade = modeRequired ? RelayError.SurfaceModeRequired.Code : decision.Reason;
            if (Str(row, "degradeReason") != wantDegrade)
            {
                mismatches.Add($"{id} degradeReason: legacy {Str(row, "degradeReason") ?? "none"} C# {wantDegrade ?? "none"}");
            }
            var usable = Bool(row, "usable") == true;
            var legacySurface = Str(row, "surface");
            if (usable && modeRequired)
            {
                mismatches.Add($"{id}: legacy usable, C# says mode {decision.RequiresMode!.Value.Wire()} required");
            }
            else if (usable)
            {
                surfaceCompared++;
                if (legacySurface != decision.Surface.Wire())
                {
                    mismatches.Add($"{id} surface: legacy {legacySurface ?? "none"} C# {decision.Surface.Wire()}");
                }
            }
            else if (legacySurface is not null)
            {
                mismatches.Add($"{id}: legacy names surface {legacySurface} on an unusable row");
            }
        }
        var missing = registered.Where(w => !seen.Contains(w)).ToList();
        var extra = seen.Where(w => !registered.Contains(w)).ToList();
        if (missing.Count > 0 || extra.Count > 0)
        {
            mismatches.Add($"capability rows: missing [{string.Join(",", missing)}] extra [{string.Join(",", extra)}]");
        }
        var shizuku = running ? $"Shizuku running, display {(alive ? "alive" : "down")}" : "no Shizuku";
        if (mismatches.Count > 0)
        {
            return Report(ShadowVerdict.Diverge, true, mismatches[0], $"{mismatches.Count} differ ({shizuku}): {string.Join("; ", mismatches.Take(4))}");
        }
        if (seen.Count == 0)
        {
            return Report(ShadowVerdict.Inconclusive, true, null, "capabilities.json lists no capability");
        }
        var decisions = string.Join(", ", seen.Select(id => $"{id}→{Describe(policy.Decide(id))}"));
        var surfaceNote = surfaceCompared == seen.Count
            ? "surface compared on every row"
            : $"surface compared on {surfaceCompared}/{seen.Count} rows (the others are not usable now, e.g. accessibility off)";
        return Report(ShadowVerdict.Agree, true, null, $"{seen.Count}/{seen.Count} rows agree ({shizuku}; {surfaceNote}): {decisions}");
    }

    private static string Describe(SurfaceDecision d) =>
        d.RequiresMode is { } m ? $"requires {m.Wire()}"
        : d.DegradedFrom is { } f ? $"{d.Surface.Wire()} (from {f.Wire()}: {d.Reason})"
        : d.Surface.Wire();

    internal static bool TryShizuku(JsonElement root, out bool running, out bool bound, out bool alive, out long displayId)
    {
        running = bound = alive = false;
        displayId = -1;
        if (!root.TryGetProperty("backend", out var b) || b.ValueKind != JsonValueKind.Object
            || !b.TryGetProperty("shizuku", out var s) || s.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        running = Bool(s, "running") == true;
        bound = Bool(s, "bound") == true;
        if (s.TryGetProperty("trustedDisplay", out var t) && t.ValueKind == JsonValueKind.Object)
        {
            alive = Bool(t, "alive") == true;
            if (t.TryGetProperty("displayId", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var n))
            {
                displayId = n;
            }
        }
        return true;
    }

    internal static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    internal static bool? Bool(JsonElement o, string name) =>
        o.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False ? p.GetBoolean() : null;
}
