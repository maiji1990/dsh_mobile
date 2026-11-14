// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/protocol/CapabilityRegistry.kt   (registry order, guideClass, minSdk)
//   src/main/java/interlock/relay/core/runtime/RelayContainer.kt        (CapabilityRow, capabilityRows, capabilitiesJson)
//   libcore org.json JSONStringer indent form (JSONObject.toString(2))
// Registered in docs/UPSTREAMS.md. The text form is locked against a capabilities.json the legacy Relay published on
// the acceptance device (tests/TestVectors/relay-guest/).

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DshMobile.Runtime.Automation.Relay;

/// <summary>
/// The parts of upstream <c>CapabilityRegistry</c> not already ported elsewhere: the registry order the listing uses
/// (not <see cref="RelayProtocol.Capabilities"/>, which is the declaration order), the settings-guide class and the
/// minimum SDK. Category, ceiling, permissions and surfaces come from their existing C# tables.
/// </summary>
public static class RelayCapabilityCatalog
{
    public sealed record Entry(string Wire, string Guide, int MinSdk);

    public const string GuideDeepLinkOk = "DEEP_LINK_OK";
    public const string GuideListOnly = "LIST_ONLY";
    public const string GuideNoSettingsPage = "NO_SETTINGS_PAGE";
    public const string GuideNotRequired = "NOT_REQUIRED";

    public static IReadOnlyList<Entry> All { get; } =
    [
        new("ui.snapshot", GuideListOnly, 29),
        new("screen.capture", GuideNoSettingsPage, 29),
        new("screen.observe", GuideNoSettingsPage, 21),
        new("screen.record", GuideNoSettingsPage, 21),
        new("notify.read", GuideListOnly, 22),
        new("ui.tap", GuideListOnly, 24),
        new("ui.swipe", GuideListOnly, 24),
        new("ui.click", GuideListOnly, 29),
        new("ui.longClick", GuideListOnly, 29),
        new("ui.select", GuideListOnly, 29),
        new("ui.dismiss", GuideListOnly, 29),
        new("ui.scroll", GuideListOnly, 29),
        new("ui.setValue", GuideListOnly, 29),
        new("ui.waitFor", GuideListOnly, 29),
        new("ui.setProgress", GuideListOnly, 29),
        new("ui.imeAction", GuideListOnly, 30),
        new("ui.node", GuideListOnly, 29),
        new("ui.text", GuideListOnly, 24),
        new("ui.key", GuideListOnly, 29),
        new("app.launch", GuideDeepLinkOk, 29),
        new("sys.intent", GuideNotRequired, 29),
        new("sys.shell", GuideNoSettingsPage, 29),
        new("app.stop", GuideNoSettingsPage, 29),
        new("surface.virtual", GuideNoSettingsPage, RelaySurfaces.MinBackendSdk),
        new("clip.read", GuideNoSettingsPage, 29),
        new("clip.write", GuideNoSettingsPage, 29),
        new("contact.read", GuideDeepLinkOk, 29),
        new("contact.write", GuideDeepLinkOk, 29),
        new("cal.read", GuideDeepLinkOk, 29),
        new("cal.write", GuideDeepLinkOk, 29),
        new("loc.read", GuideDeepLinkOk, 29),
        new("media.read", GuideDeepLinkOk, 29),
        new("media.write", GuideNotRequired, 29),
        new("audio.capture", GuideDeepLinkOk, 29),
        new("pkg.query", GuideNotRequired, 29),
        new("app.install", GuideDeepLinkOk, 29),
        new("sys.settings.write", GuideNoSettingsPage, 29),
        new("appops.set", GuideNoSettingsPage, 29),
        new("notify.post", GuideDeepLinkOk, 29),
    ];
}

/// <summary>Upstream <c>ShizukuState.Snapshot</c> as the listing reports it (uid / version -1 while the binder is dead).</summary>
public sealed record ShizukuSnapshot(bool Running, bool Granted, int Uid, int Version)
{
    public static ShizukuSnapshot None { get; } = new(false, false, -1, -1);
}

/// <summary>Upstream <c>RelayContainer.CapabilityRow</c>.</summary>
public sealed record CapabilityRow(
    RelayCapabilityCatalog.Entry Entry,
    SystemGrantState System,
    AccessTier Tier,
    bool Implemented,
    bool Usable,
    bool Enabled,
    IReadOnlyList<string> Gaps,
    SurfaceKind? Surface,
    string? DegradeReason);

/// <summary>The state the trusted-display block of the listing reads.</summary>
public sealed record TrustedDisplayState(bool Alive, int DisplayId, string NodeTree);

/// <summary>Upstream <c>capabilityRows</c> / <c>capabilitiesJson</c>: one source for the guest listing.</summary>
public static class RelayCapabilityListing
{
    /// <summary>Upstream <c>RELAY_PROTOCOL_VERSION</c> written as <c>protocol</c>.</summary>
    public const int Protocol = 1;

    /// <summary>
    /// Upstream <c>capabilityRows</c>. The route is decided first and the system state probed on <em>that</em> route
    /// (a coordinate action on the trusted display does not need accessibility); "implemented" asks the code
    /// (any registered backend), "usable" asks the decided route's backend, the system state, the tier and whether
    /// the preference chain demands a mode not reached yet.
    /// </summary>
    public static IReadOnlyList<CapabilityRow> Rows(SurfacePolicy policy, Func<string, SurfaceKind, ProbeResult> probe,
        Func<string, AccessTier> tierOf, Func<string, SurfaceKind, bool> backendOnRoute, Func<string, bool> implemented)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(tierOf);
        ArgumentNullException.ThrowIfNull(backendOnRoute);
        ArgumentNullException.ThrowIfNull(implemented);
        var rows = new List<CapabilityRow>(RelayCapabilityCatalog.All.Count);
        foreach (var entry in RelayCapabilityCatalog.All)
        {
            var decision = policy.Decide(entry.Wire);
            var probed = probe(entry.Wire, decision.Surface);
            var tier = tierOf(entry.Wire);
            var hasBackend = backendOnRoute(entry.Wire, decision.Surface);
            var modeRequired = decision.RequiresMode is not null;
            var blocks = RelayGatePolicy.SystemBlocks(probed.State);
            var usable = hasBackend && !blocks && tier != AccessTier.Denied && !modeRequired;
            var served = implemented(entry.Wire);
            rows.Add(new CapabilityRow(entry, probed.State, tier, served, usable,
                Enabled: served && tier != AccessTier.Denied && !blocks && !modeRequired,
                probed.Gaps,
                Surface: !modeRequired && usable ? decision.Surface : null,
                DegradeReason: modeRequired ? RelayError.SurfaceModeRequired.Code : decision.Reason));
        }
        return rows;
    }

    /// <summary>
    /// Upstream <c>capabilitiesJson</c>: <c>JSONObject.toString(2) + "\n"</c>. No timestamp on purpose: the text is
    /// also the "changed, so republish" comparison.
    /// </summary>
    public static string Json(IReadOnlyList<CapabilityRow> rows, string guestScript, string hostPackage, ShizukuSnapshot shizuku,
        bool bound, TrustedDisplayState display)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(shizuku);
        ArgumentNullException.ThrowIfNull(display);
        var array = new JsonArray();
        foreach (var row in rows)
        {
            var o = new JsonObject
            {
                ["id"] = row.Entry.Wire,
                ["category"] = RelaySemantics.CategoryOf(row.Entry.Wire).ToString().ToLowerInvariant(),
                ["systemPermission"] = Screaming(row.System.ToString()),
                ["assistantTier"] = Screaming(row.Tier.ToString()),
                ["ceiling"] = Screaming(RelayGatePolicy.CeilingOf(row.Entry.Wire).ToString()),
                ["usable"] = row.Usable,
                ["implemented"] = row.Implemented,
                ["minSdk"] = row.Entry.MinSdk,
                ["surfaces"] = new JsonArray([.. RelaySurfaces.SurfacesOf(row.Entry.Wire).Select(s => (JsonNode)s.Wire())]),
                ["guide"] = row.Entry.Guide,
            };
            if (row.Surface is { } surface)
            {
                o["surface"] = surface.Wire();
            }
            if (row.DegradeReason is { } degrade)
            {
                o["degradeReason"] = degrade;
            }
            if (!row.Implemented)
            {
                o["reason"] = "not_implemented";
            }
            array.Add((JsonNode)o);
        }
        var root = new JsonObject
        {
            ["protocol"] = Protocol,
            ["entry"] = guestScript,
            ["host"] = new JsonObject { ["package"] = hostPackage },
            ["backend"] = new JsonObject
            {
                ["shizuku"] = new JsonObject
                {
                    ["running"] = shizuku.Running,
                    ["authorized"] = shizuku.Granted,
                    ["bound"] = bound,
                    ["shellUid"] = shizuku.Uid,
                    ["version"] = shizuku.Version,
                    ["trustedDisplay"] = new JsonObject
                    {
                        // The display belongs to the user-service process: with the line down, a cached id is a lie.
                        ["alive"] = bound && display.Alive,
                        ["displayId"] = display.DisplayId,
                        ["nodeTree"] = display.NodeTree,
                    },
                },
            },
            ["capabilities"] = array,
        };
        return LibcoreJson.Indented(root, 2) + "\n";
    }

    /// <summary>Kotlin enum name of a C# enum member (<c>SessionConsent</c> → <c>SESSION_CONSENT</c>).</summary>
    public static string Screaming(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
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

/// <summary>libcore <c>JSONStringer(indentSpaces)</c>: what <c>JSONObject.toString(n)</c> writes.</summary>
internal static partial class LibcoreJson
{
    /// <summary>
    /// Every value on its own line, nested values indented by <paramref name="indent"/> more, <c>"key": value</c>,
    /// empty arrays / objects as <c>[]</c> / <c>{}</c>, strings quoted like <see cref="Quote"/>.
    /// </summary>
    public static string Indented(JsonNode node, int indent)
    {
        ArgumentNullException.ThrowIfNull(node);
        var sb = new StringBuilder();
        Write(sb, node, indent, 0);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, JsonNode? node, int indent, int depth)
    {
        switch (node)
        {
            case JsonObject o:
                if (o.Count == 0)
                {
                    sb.Append("{}");
                    return;
                }
                sb.Append('{');
                var first = true;
                foreach (var (key, value) in o)
                {
                    sb.Append(first ? "" : ",").Append('\n').Append(' ', indent * (depth + 1)).Append(Quote(key)).Append(": ");
                    Write(sb, value, indent, depth + 1);
                    first = false;
                }
                sb.Append('\n').Append(' ', indent * depth).Append('}');
                return;
            case JsonArray a:
                if (a.Count == 0)
                {
                    sb.Append("[]");
                    return;
                }
                sb.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    sb.Append(i == 0 ? "" : ",").Append('\n').Append(' ', indent * (depth + 1));
                    Write(sb, a[i], indent, depth + 1);
                }
                sb.Append('\n').Append(' ', indent * depth).Append(']');
                return;
            case null:
                sb.Append("null");
                return;
            case JsonValue v:
                switch (v.GetValueKind())
                {
                    case JsonValueKind.String:
                        sb.Append(Quote(v.GetValue<string>()));
                        return;
                    case JsonValueKind.True:
                        sb.Append("true");
                        return;
                    case JsonValueKind.False:
                        sb.Append("false");
                        return;
                    case JsonValueKind.Number when v.TryGetValue<int>(out var i):
                        sb.Append(i.ToString(CultureInfo.InvariantCulture));
                        return;
                    case JsonValueKind.Number when v.TryGetValue<long>(out var l):
                        sb.Append(l.ToString(CultureInfo.InvariantCulture));
                        return;
                    default:
                        throw new ArgumentException($"unsupported value {v.GetValueKind()}", nameof(node));
                }
            default:
                throw new ArgumentException("unsupported node", nameof(node));
        }
    }
}
