using DshMobile.Runtime.Automation;
using DshMobile.Runtime.Automation.Relay;

namespace DshMobile.Application.Shadow;

/// <summary>What the legacy <c>relay.*</c> response reported (the parts the C# side can establish on its own).</summary>
public sealed record UiToolsLegacyView(
    string Method,
    bool Registered,
    bool Conflict,
    bool AccessibilityEnabled,
    IReadOnlyList<string> Allowed,
    string GuestCli,
    string ServerName);

/// <summary>
/// PR-07 first segment, read-only shadow of the <c>ui_*</c> registration and status: after each legacy
/// <c>relay.start / stop / status</c>, the C# port reads the same patch layer and accessibility setting and must reach
/// the same conclusions. Registered additionally requires the file to be byte-identical to the C# <c>Render()</c>, so
/// a drift in the rendered layer shows up even when both sides see the marker. Static facts (capability
/// table, guest CLI path, MCP server name) are compared too.
/// </summary>
public static class UiToolsShadowCheck
{
    public const string Slice = "automation.ui-tools";

    /// <summary>
    /// What the legacy Relay registers (ADR-0008: upstream's full table, <c>RelayCapabilities.BuiltIn</c>), sorted
    /// ordinally the way <c>relay.status</c> reports it.
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedWire = [.. RelayProtocol.Capabilities.Order(StringComparer.Ordinal)];

    public const string GuestCli = "/opt/pilot/bin/relay";

    public static ShadowReport Compare(UiToolsLegacyView legacy, string userDataDir, string? enabledAccessibilityServices,
        string packageName, long csharpMs, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        var registered = UiToolsRegistration.IsRegistered(userDataDir);
        var conflict = UiToolsRegistration.HasConflict(userDataDir);
        var identical = registered && File.ReadAllText(UiToolsRegistration.OverlayFile(userDataDir)) == UiToolsRegistration.Render();
        var a11y = AccessibilitySetting.IsEnabled(enabledAccessibilityServices, packageName, AccessibilitySetting.RelayServiceClass);
        var checks = new (string Name, string Legacy, string CSharp)[]
        {
            ("registered", legacy.Registered.ToString(), registered.ToString()),
            ("conflict", legacy.Conflict.ToString(), conflict.ToString()),
            ("layerBytes", legacy.Registered ? "render" : "-", registered ? (identical ? "render" : "different") : "-"),
            ("accessibility", legacy.AccessibilityEnabled.ToString(), a11y.ToString()),
            ("allowed", string.Join(',', legacy.Allowed.Order(StringComparer.Ordinal)), string.Join(',', AllowedWire)),
            ("guestCli", legacy.GuestCli, GuestCli),
            ("serverName", legacy.ServerName, UiToolsRegistration.ServerName),
        };
        var diffs = checks.Where(c => c.Legacy != c.CSharp).ToList();
        var detail = $"after {legacy.Method}: " + (diffs.Count == 0
            ? $"registered={registered} conflict={conflict} a11y={a11y}{(identical ? ", layer byte-identical" : "")}, static facts equal"
            : string.Join("; ", diffs.Select(d => $"{d.Name} legacy={d.Legacy} csharp={d.CSharp}")));
        return new ShadowReport(Slice, at, diffs.Count == 0 ? ShadowVerdict.Agree : ShadowVerdict.Diverge, true, null, true, null, detail, csharpMs);
    }
}
