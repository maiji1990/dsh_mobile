using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DshMobile.Application.Shadow;

/// <summary>
/// Switch slice shadow (ADR-0010), read-only: what the legacy Relay published in its entry directory
/// (<c>files/pilot</c>: <c>bin/relay</c>, <c>capabilities.json</c>, <c>USAGE.md</c>) against what the opt-in C# host
/// published in its own (<c>files/pilot-cs</c>). The guest path differs by policy and is rewritten first; then the
/// script and the manual must be byte-identical and the listing identical row by row. Rows can only differ by a fact
/// the two hosts see on their own (a binding, the Shizuku user service), and the report names them.
/// </summary>
public static class RelayGuestFilesShadowCheck
{
    public const string Slice = "automation.relay-guest-files";

    public static ShadowReport Compare(string legacyEntryDir, string csEntryDir, string legacyGuestEntry, string csGuestEntry, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(legacyEntryDir);
        ArgumentNullException.ThrowIfNull(csEntryDir);
        ShadowReport Report(ShadowVerdict verdict, string detail) => new(Slice, at, verdict, verdict != ShadowVerdict.Inconclusive, null, true, null, detail, 0);

        var mismatches = new List<string>();
        var compared = 0;
        foreach (var name in new[] { "bin/relay", "USAGE.md", "capabilities.json" })
        {
            var legacy = Read(legacyEntryDir, name);
            var cs = Read(csEntryDir, name);
            if (legacy is null || cs is null)
            {
                return Report(ShadowVerdict.Inconclusive, $"{name} missing on the {(legacy is null ? "legacy" : "C#")} side (start both relays first)");
            }
            compared++;
            if (name == "bin/relay")
            {
                if (!legacy.AsSpan().SequenceEqual(cs))
                {
                    mismatches.Add($"bin/relay: legacy {Hash(legacy)} C# {Hash(cs)}");
                }
                continue;
            }
            var legacyText = Encoding.UTF8.GetString(legacy);
            var csText = Rewrite(Encoding.UTF8.GetString(cs), csGuestEntry, legacyGuestEntry);
            if (legacyText == csText)
            {
                continue;
            }
            if (name == "USAGE.md")
            {
                mismatches.Add($"USAGE.md: legacy {Hash(legacy)} C# {Hash(Encoding.UTF8.GetBytes(csText))}");
                continue;
            }
            mismatches.AddRange(ListingDifferences(legacyText, csText));
        }
        return mismatches.Count == 0
            ? Report(ShadowVerdict.Agree, $"{compared}/3 guest files identical (bin/relay, USAGE.md, capabilities.json after the guest path rewrite)")
            : Report(ShadowVerdict.Diverge, $"{mismatches.Count} differ: {string.Join("; ", mismatches.Take(6))}");
    }

    /// <summary>The C# file's guest paths written the legacy way (JSON escapes '/' as "\/").</summary>
    public static string Rewrite(string text, string from, string to)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace(from.Replace("/", "\\/", StringComparison.Ordinal), to.Replace("/", "\\/", StringComparison.Ordinal), StringComparison.Ordinal)
            .Replace(from, to, StringComparison.Ordinal);
    }

    /// <summary>Per capability (and the Shizuku block) the fields that differ; the whole text when it does not parse.</summary>
    public static IReadOnlyList<string> ListingDifferences(string legacy, string cs)
    {
        try
        {
            using var a = JsonDocument.Parse(legacy);
            using var b = JsonDocument.Parse(cs);
            var diffs = new List<string>();
            foreach (var key in new[] { "protocol", "entry", "host", "backend" })
            {
                var x = a.RootElement.TryGetProperty(key, out var xv) ? xv.GetRawText() : "(none)";
                var y = b.RootElement.TryGetProperty(key, out var yv) ? yv.GetRawText() : "(none)";
                if (x != y)
                {
                    diffs.Add($"{key}: legacy {Squeeze(x)} C# {Squeeze(y)}");
                }
            }
            var rowsA = Rows(a.RootElement);
            var rowsB = Rows(b.RootElement);
            if (!rowsA.Keys.SequenceEqual(rowsB.Keys))
            {
                diffs.Add($"capability order / set differs: legacy [{string.Join(",", rowsA.Keys)}] C# [{string.Join(",", rowsB.Keys)}]");
            }
            foreach (var (id, row) in rowsA)
            {
                if (!rowsB.TryGetValue(id, out var other))
                {
                    continue;
                }
                var fields = row.Keys.Union(other.Keys).Where(k => row.GetValueOrDefault(k) != other.GetValueOrDefault(k))
                    .Select(k => $"{k} {row.GetValueOrDefault(k) ?? "-"}|{other.GetValueOrDefault(k) ?? "-"}");
                if (fields.Any())
                {
                    diffs.Add($"{id}: {string.Join(", ", fields)}");
                }
            }
            if (diffs.Count == 0)
            {
                diffs.Add("capabilities.json: same values, different text (key order or formatting)");
            }
            return diffs;
        }
        catch (JsonException ex)
        {
            return [$"capabilities.json unparsable: {ex.Message}"];
        }
    }

    private static Dictionary<string, Dictionary<string, string>> Rows(JsonElement root)
    {
        var rows = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        if (root.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in caps.EnumerateArray())
            {
                var id = row.TryGetProperty("id", out var i) ? i.GetString() ?? "?" : "?";
                rows[id] = row.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetRawText(), StringComparer.Ordinal);
            }
        }
        return rows;
    }

    private static string Squeeze(string json)
    {
        var flat = json.Replace("\n", "", StringComparison.Ordinal).Replace("  ", "", StringComparison.Ordinal);
        return flat.Length > 160 ? flat[..160] + "…" : flat;
    }

    private static byte[]? Read(string entryDir, string name)
    {
        try
        {
            var path = Path.Combine([entryDir, .. name.Split('/')]);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes))[..12];
}
