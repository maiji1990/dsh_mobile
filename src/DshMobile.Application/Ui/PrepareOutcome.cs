using System.Text.RegularExpressions;
using DshMobile.Runtime;
using DshMobile.Runtime.Layers;

namespace DshMobile.Application.Ui;

/// <summary>What the bundled-DSH step of <c>runtime.prepare</c> did.</summary>
public enum PrepareKind
{
    /// <summary>The bundled DSH layer was installed (it was newer, or no layer was installed).</summary>
    Installed,
    /// <summary>The installed DSH is the same version or newer, so it was kept as is (legacy version arbitration).</summary>
    Kept,
    /// <summary>The layer install failed; legacy prepare still returns ok, so the UI must not show success.</summary>
    ProvisionFailed,
    /// <summary>base / node are not installed: nothing was done.</summary>
    LayersMissing,
    /// <summary>The APK carries no DSH layer.</summary>
    NoBaseline,
    /// <summary>A message this parser does not know.</summary>
    Unknown,
}

/// <summary>
/// Reads the step text of a successful <c>runtime.prepare</c> (<see cref="BundledDsh.StepText"/>,
/// <see cref="RuntimePreparer"/>) so the runtime page can say what happened instead of "done" (AGENTS.md rule 5):
/// the legacy prepare reports ok even when the DSH layer was kept or failed to install, and that behaviour is pinned by
/// the vectors, so it is classified here rather than changed.
/// </summary>
public sealed partial record PrepareOutcome(PrepareKind Kind, string? Version, string? Code, string? Detail)
{
    /// <summary>Whether the operation should be shown as failed.</summary>
    public bool Failed => Kind is PrepareKind.ProvisionFailed or PrepareKind.LayersMissing or PrepareKind.NoBaseline;

    public static PrepareOutcome Parse(string? message)
    {
        var text = message ?? "";
        const string failed = "DSH provision failed: ";
        var at = text.IndexOf(failed, StringComparison.Ordinal);
        if (at >= 0)
        {
            var rest = text[(at + failed.Length)..].Trim();
            var space = rest.IndexOf(' ');
            return space < 0
                ? new PrepareOutcome(PrepareKind.ProvisionFailed, null, rest, null)
                : new PrepareOutcome(PrepareKind.ProvisionFailed, null, rest[..space], rest[(space + 1)..]);
        }
        if (text.Contains(RuntimePreparer.LayersMissing, StringComparison.Ordinal))
        {
            return new PrepareOutcome(PrepareKind.LayersMissing, null, RuntimeErrorCodes.NotInstalled, RuntimePreparer.LayersMissing);
        }
        if (text.Contains(BundledDsh.NoBaseline, StringComparison.Ordinal))
        {
            return new PrepareOutcome(PrepareKind.NoBaseline, null, DshLayerErrorCodes.BundleNotFound, BundledDsh.NoBaseline);
        }
        if (KeptRegex().Match(text) is { Success: true } kept)
        {
            return new PrepareOutcome(PrepareKind.Kept, kept.Groups[1].Value, null, null);
        }
        if (InstalledRegex().Match(text) is { Success: true } installed)
        {
            return new PrepareOutcome(PrepareKind.Installed, installed.Groups[1].Value, null, null);
        }
        return new PrepareOutcome(PrepareKind.Unknown, null, null, message);
    }

    [GeneratedRegex(@"DSH kept at (\S+)")]
    private static partial Regex KeptRegex();

    [GeneratedRegex(@"DSH (\S+) installed")]
    private static partial Regex InstalledRegex();
}
