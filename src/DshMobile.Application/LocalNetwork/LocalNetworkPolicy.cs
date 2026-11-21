using System.Net;

namespace DshMobile.Application.LocalNetwork;

/// <summary>
/// What Android 17's Local Network Protection (LNP) requires of this app right now (ADR-0032).
/// </summary>
public enum LocalNetworkDecision
{
    /// <summary>Android 17 below, or an app that targets 36 or lower: the system keeps the old behaviour, so do not ask.</summary>
    NotRequired,

    /// <summary>The permission is declared and granted: LAN traffic from the app UID (including the PRoot guest) is allowed.</summary>
    Granted,

    /// <summary>The permission is declared but not granted: LAN TCP will time out until the user allows it.</summary>
    NeedsPermission,

    /// <summary>Targeting 37+ without declaring the permission: a build defect; refuse to pretend a timeout is a network fault.</summary>
    Undeclared,
}

/// <summary>
/// Pure LNP decision (ADR-0032). No Android types so it is unit-testable; <c>DshMobile.Android.LocalNetwork</c> feeds it
/// the build/package facts and turns the decision into a permission request and UI copy.
/// </summary>
public static class LocalNetworkPolicy
{
    /// <summary>First API level that enforces LNP for apps targeting it.</summary>
    public const int MinimumApiLevel = 37;

    /// <summary>
    /// The single decision point. <paramref name="declared"/> is whether the merged manifest asks for
    /// <c>ACCESS_LOCAL_NETWORK</c>; <paramref name="granted"/> is <c>checkSelfPermission</c>.
    /// </summary>
    public static LocalNetworkDecision Decide(int sdkInt, int targetSdk, bool declared, bool granted)
    {
        if (sdkInt < MinimumApiLevel || targetSdk < MinimumApiLevel)
        {
            return LocalNetworkDecision.NotRequired;
        }
        if (!declared)
        {
            return LocalNetworkDecision.Undeclared;
        }
        return granted ? LocalNetworkDecision.Granted : LocalNetworkDecision.NeedsPermission;
    }

    /// <summary>True when the app has to do something about LNP (either ask, or surface the build defect).</summary>
    public static bool RequiresAction(LocalNetworkDecision decision) =>
        decision is LocalNetworkDecision.NeedsPermission or LocalNetworkDecision.Undeclared;

    /// <summary>
    /// Whether <paramref name="host"/> is a literal address LNP gates: private IPv4 (<c>10/8</c>, <c>172.16/12</c>,
    /// <c>192.168/16</c>, <c>169.254/16</c>), IPv6 ULA (<c>fc00::/7</c>) or link-local (<c>fe80::/10</c>), or a
    /// <c>.local</c> (mDNS) name. Hostnames that are not literals return false: their address is only known after DNS,
    /// and only the permission state — not this guess — decides whether the connection works.
    /// </summary>
    public static bool IsPrivateHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }
        var value = host.Trim();
        if (value.StartsWith('[') && value.EndsWith(']'))
        {
            value = value[1..^1]; // [fe80::1]
        }
        if (value.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!IPAddress.TryParse(value, out var address))
        {
            return false;
        }
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254);
        }
        return (bytes[0] & 0xFE) == 0xFC // fc00::/7 unique local
            || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80); // fe80::/10 link-local
    }
}

/// <summary>Stable codes for the LAN permission (ADR-0032; rule 5: a limitation is reported, never faked as success).</summary>
public static class LocalNetworkCodes
{
    /// <summary>LNP is active, the user did not allow the permission: LAN TCP from this UID will time out.</summary>
    public const string PermissionDenied = "LAN_PERMISSION_DENIED";

    /// <summary>Targeting 37+ without declaring the permission (the build is wrong, not the network).</summary>
    public const string PermissionUndeclared = "LAN_PERMISSION_UNDECLARED";
}
