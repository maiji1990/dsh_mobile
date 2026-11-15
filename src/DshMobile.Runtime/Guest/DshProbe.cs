// Ported from this repo's native/native-bridge/src/main/java/dshmobile/nativebridge/RuntimeController.kt `dsh.probe`
// (GPL-3.0-or-later). Registered in docs/UPSTREAMS.md.

using System.Text.Json;
using DshMobile.Core.Bridge;

namespace DshMobile.Runtime.Guest;

/// <summary>Outcome of one profile probe: the script's own result, or a stable error code with the output tail.</summary>
public sealed record DshProbeOutcome(string Profile, ProbeResult? Result, string? ErrorCode, string? Error);

/// <summary>
/// TASK-005 profile probe through the C# PRoot launcher (MIGRATION row 24): <c>node dsh-probe.mjs &lt;profile&gt;</c> in a
/// fresh guest, exactly the legacy fixed command. The script launches DSH itself for each entry mode and proves it
/// with a real protocol exchange; its last <c>DSHPROBE &lt;json&gt;</c> line is the result. A run without that line is an
/// error with the output tail (legacy <c>INTERNAL</c>), never a pass.
/// </summary>
public static class DshProbe
{
    public const string ResultPrefix = "DSHPROBE ";

    /// <summary>The run produced no <c>DSHPROBE</c> line (crash, timeout, missing script).</summary>
    public const string NoResult = "PROBE_NO_RESULT";

    /// <summary>The result line is not the expected JSON.</summary>
    public const string Malformed = "PROBE_MALFORMED";

    public const string UnknownProfile = "BAD_REQUEST";

    /// <summary>Legacy keeps the last 400 lines.</summary>
    public const int MaxLines = 400;

    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>The fixed guest command for <paramref name="profile"/>; only the shipped profiles are accepted.</summary>
    public static string Command(string profile)
    {
        if (!DshProfiles.All.Contains(profile))
        {
            throw new ArgumentException($"profile must be one of {string.Join(", ", DshProfiles.All)}", nameof(profile));
        }
        return GuestCommandLine.Join(["node", GuestAssetProvisioner.GuestProbe, profile]);
    }

    /// <summary>Reads the outcome from a finished run (legacy: last <c>DSHPROBE</c> line wins, otherwise an error with the tail).</summary>
    public static DshProbeOutcome Parse(string profile, GuestProcessResult run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var lines = run.Output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var line = lines.LastOrDefault(l => l.StartsWith(ResultPrefix, StringComparison.Ordinal));
        if (line is null)
        {
            var tail = string.Join(" | ", lines.Where(l => l.Length > 0).TakeLast(8));
            return new DshProbeOutcome(profile, null, NoResult, $"{run.Error ?? "probe produced no result line"}; tail: {tail}");
        }
        try
        {
            var result = JsonSerializer.Deserialize(line[ResultPrefix.Length..], BridgeJsonContext.Default.ProbeResult);
            return result is null
                ? new DshProbeOutcome(profile, null, Malformed, "empty result")
                : new DshProbeOutcome(profile, result with { Checks = result.Checks ?? [] }, null, null);
        }
        catch (JsonException e)
        {
            return new DshProbeOutcome(profile, null, Malformed, e.Message);
        }
    }
}
