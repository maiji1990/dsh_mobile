// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/interlock/InterlockTargets.kt
//   src/main/java/interlock/relay/core/interlock/ConsentSurfaces.kt
// Registered in docs/UPSTREAMS.md. Locked by tests/TestVectors/relay-approval.json.
//
// Security: these decide what the approval card says the user is approving, and which calls touch a system consent
// surface (those are always asked again and never get a session grant). Keep both in lockstep with upstream.

using System.Globalization;
using System.Text.Json;

namespace DshMobile.Runtime.Automation.Relay.Interlock;

/// <summary>Upstream <c>InterlockTargets</c>: the (target, detail) pair shown on the approval card and kept in the audit.</summary>
public static class InterlockTargets
{
    public const int MaxDetailChars = 80;
    public const int MaxValueChars = 24;

    public const string KeySelector = "selector";
    public const string KeyNodeId = "nodeId";
    private static readonly string[] NodeTargetKeys = ["package", "class", "id", "desc", "text", "index"];

    private static readonly HashSet<string> ScreenBound = new(StringComparer.Ordinal)
    {
        "audio.capture", "cal.read", "clip.read", "contact.read", "loc.read", "media.read", "media.write", "notify.read",
        "app.install", "pkg.query", "ui.node", "ui.click", "ui.longClick", "ui.select", "ui.dismiss", "ui.scroll", "ui.setValue",
        "ui.setProgress", "ui.imeAction", "ui.waitFor", "ui.tap", "ui.swipe", "ui.text", "ui.key", "ui.snapshot",
        "screen.capture", "screen.observe", "screen.record",
    };

    private static string Take(string s, int n) => s.Length <= n ? s : s[..n];

    private static string? NonEmpty(string s) => s.Length == 0 ? null : s;

    private static string Opt(JsonElement args, string name) => OrgJson.OptString(args, name);

    /// <summary>libcore <c>optString(name, fallback)</c>: fallback only when absent (a JSON null reads "null").</summary>
    private static string OptOr(JsonElement args, string name, string fallback) =>
        args.TryGetProperty(name, out var v) ? OrgJson.JavaString(v) : fallback;

    /// <summary><c>String.valueOf(args.opt(name))</c>: "null" when absent.</summary>
    private static string OptAny(JsonElement args, string name) => args.TryGetProperty(name, out var v) ? OrgJson.JavaString(v) : "null";

    /// <summary><c>optInt(name, fallback)</c>: fallback when absent or not convertible.</summary>
    private static int OptIntOr(JsonElement args, string name, int fallback)
    {
        if (!args.TryGetProperty(name, out var v))
        {
            return fallback;
        }
        return v.ValueKind switch
        {
            JsonValueKind.Number => OrgJson.OptInt(args, name),
            JsonValueKind.String when OrgJson.JavaParseDouble(v.GetString()!) is { } d => OrgJson.JavaDoubleToInt(d),
            _ => fallback,
        };
    }

    /// <summary>Java <c>%02d</c>: zero-pad to width 2, the sign counting toward the width.</summary>
    private static string Pad2(int v)
    {
        var s = v.ToString(CultureInfo.InvariantCulture);
        return s.Length >= 2 ? s : (v < 0 ? s : "0" + s);
    }

    /// <summary>Upstream <c>InterlockTargets.of</c>: (target, detail) for one capability's args.</summary>
    public static (string? Target, string? Detail) Of(string capability, JsonElement args, Func<int, string> charCount)
    {
        ArgumentNullException.ThrowIfNull(charCount);
        switch (capability)
        {
            case "sys.shell":
            {
                var verb = Opt(args, "verb");
                var rest = OrgJson.OptArray(args, "args") is { } a
                    ? string.Join(" ", a.EnumerateArray().Select(OrgJson.JavaString))
                    : "";
                return (NonEmpty(verb) is { } v ? Take(v, MaxDetailChars) : null, Take(verb + (rest.Length == 0 ? "" : " " + rest), MaxDetailChars));
            }
            case "sys.intent":
            {
                var template = Opt(args, "template");
                var detail = template switch
                {
                    "alarm.set" => Pad2(OptIntOr(args, "hour", -1)) + ":" + Pad2(OptIntOr(args, "minute", -1)),
                    "timer.set" => "length=" + OptAny(args, "length") + "s",
                    "settings.open" => Opt(args, "page"),
                    "app.info" => Opt(args, "package"),
                    "dial" => Opt(args, "number"),
                    "web.open" => Opt(args, "url"),
                    _ => "",
                };
                return (NonEmpty(template) is { } t ? Take(t, MaxDetailChars) : null, Take(detail, MaxDetailChars));
            }
            case "sys.settings.write":
            {
                var key = Opt(args, "key");
                var layer = Opt(args, "namespace");
                if (OrgJson.KotlinTrim(layer).Length == 0)
                {
                    layer = "secure";
                }
                return (NonEmpty(key) is { } k ? Take(layer + "/" + k, MaxDetailChars) : null, Take(Opt(args, "value"), MaxDetailChars));
            }
            case "appops.set":
                return (PackageTarget(args), Take(Opt(args, "op") + " -> " + Opt(args, "mode"), MaxDetailChars));
            case "app.stop" or "pkg.query" or "app.launch":
                return (PackageTarget(args), null);
            case "surface.virtual":
                return (NonEmpty(Opt(args, "action")) is { } act ? Take(act, MaxDetailChars) : null,
                    Take(OrgJson.OptInt(args, "width").ToString(CultureInfo.InvariantCulture) + "x" + OrgJson.OptInt(args, "height").ToString(CultureInfo.InvariantCulture), MaxDetailChars));
            case "clip.write" or "ui.text":
                return (null, charCount(Opt(args, "text").Length));
            case "contact.write":
                return (NonEmpty(Opt(args, "name")) is { } n ? Take(n, MaxDetailChars) : null, Take(Opt(args, "number"), MaxDetailChars));
            case "cal.write" or "notify.post":
                return (PackageTarget(args), Take(Opt(args, "title"), MaxDetailChars));
            case "media.write":
                return (NonEmpty(Opt(args, "file")) is { } f ? Take(f, MaxDetailChars) : null,
                    NonEmpty(OptOr(args, "kind", "image")) is { } kind ? Take(kind, MaxDetailChars) : null);
            case "app.install":
                return (NonEmpty(Opt(args, "upload")) is { } u ? Take(u, MaxDetailChars) : null, null);
            case "ui.click" or "ui.longClick" or "ui.select" or "ui.dismiss" or "ui.scroll" or "ui.setValue" or "ui.node"
                or "ui.waitFor" or "ui.setProgress" or "ui.imeAction":
                string? nodeDetail = capability switch
                {
                    "ui.setValue" => charCount(Opt(args, "text").Length),
                    "ui.setProgress" => args.TryGetProperty("value", out _) ? Take("value=" + OptAny(args, "value"), MaxDetailChars)
                        : args.TryGetProperty("percent", out _) ? Take("percent=" + OptAny(args, "percent") + "%", MaxDetailChars)
                        : null,
                    _ => null,
                };
                return (NodeTarget(args), nodeDetail);
            default:
                return (PackageTarget(args), null);
        }
    }

    private static string? PackageTarget(JsonElement args) => NonEmpty(Opt(args, "package")) is { } p ? Take(p, MaxDetailChars) : null;

    private static string? NodeTarget(JsonElement args)
    {
        if (args.TryGetProperty(KeyNodeId, out _))
        {
            return Take(KeyNodeId + "=" + OptAny(args, KeyNodeId), MaxDetailChars);
        }
        if (OrgJson.OptObject(args, KeySelector) is not { } selector)
        {
            return null;
        }
        var parts = NodeTargetKeys.Where(k => selector.TryGetProperty(k, out _))
            .Select(k => k + "=" + Take(OptAny(selector, k), MaxValueChars));
        var joined = string.Join(" ", parts);
        return joined.Length == 0 ? null : Take(joined, MaxDetailChars);
    }

    /// <summary>Upstream <c>isVolatileTarget</c>: answers to these must not be reused late (the screen may have moved on).</summary>
    public static bool IsVolatileTarget(string capability, JsonElement args) =>
        VolatileFor(capability, args.TryGetProperty(KeyNodeId, out _) || args.TryGetProperty(KeySelector, out _));

    public static bool VolatileFor(string capability, bool hasTreeOrPointTarget) => ScreenBound.Contains(capability) || hasTreeOrPointTarget;
}

/// <summary>
/// Upstream <c>ConsentSurfaces</c>: system consent screens (permission controllers, installers, pickers, System UI and the
/// vendor equivalents). Acting on them is always asked again and never granted for the session.
/// </summary>
public static class ConsentSurfaces
{
    private const int UidSystem = 1000;

    /// <summary>Package → uid (null when unknown). The Android host wires PackageManager; the default knows nothing.</summary>
    public static Func<string, int?> UidResolver { get; set; } = _ => null;

    private static readonly HashSet<string> ConsentPackages = new(StringComparer.Ordinal)
    {
        "com.android.permissioncontroller", "com.google.android.permissioncontroller", "com.android.packageinstaller",
        "com.google.android.packageinstaller", "com.android.providers.media.module", "com.android.providers.telephony",
        "com.android.emulator.telephony.provider", "com.android.externalstorage", "com.android.intentresolver", "com.android.systemui",
    };

    private static readonly string[] ConsentPackagePrefixes =
    [
        "com.samsung.android.permissioncontroller", "com.huawei.systemmanager", "com.hihonor.systemmanager", "com.miui.securitycore",
        "com.miui.permcenter", "com.coloros.safecenter", "com.oplus.safecenter", "com.vivo.permissionmanager", "com.iqoo.secure",
    ];

    public static bool IsSystemOwned(string? packageName)
    {
        var pkg = packageName is null ? null : OrgJson.KotlinTrim(packageName);
        if (string.IsNullOrEmpty(pkg))
        {
            return false;
        }
        try
        {
            return UidResolver(pkg) == UidSystem;
        }
        catch (Exception)
        {
            return false; // runCatching
        }
    }

    private static string? Lower(string? s) => s is null ? null : OrgJson.KotlinTrim(s.ToLowerInvariant());

    private static bool Listed(string pkg) => ConsentPackages.Contains(pkg) || ConsentPackagePrefixes.Any(p => pkg.StartsWith(p, StringComparison.Ordinal));

    public static bool IsConsentPackage(string? packageName)
    {
        var pkg = Lower(packageName);
        if (string.IsNullOrEmpty(pkg))
        {
            return false;
        }
        return IsSystemOwned(pkg) || Listed(pkg);
    }

    private static bool ConsentViewId(string? viewId)
    {
        if (viewId is null)
        {
            return false;
        }
        var id = viewId.ToLowerInvariant();
        return id.Contains("permission_allow", StringComparison.Ordinal) || id.Contains("permission_deny", StringComparison.Ordinal)
            || id.Contains("permission_button", StringComparison.Ordinal) || id.Contains("button_allow", StringComparison.Ordinal);
    }

    public static bool LooksLikeConsentControl(string? packageName, string? viewId)
    {
        var pkg = Lower(packageName) ?? "";
        return (pkg.Length > 0 && Listed(pkg)) || ConsentViewId(viewId);
    }

    /// <summary>A <c>relative</c> target is only an anchor: the real node is decided at execution, so assume the strictest case.</summary>
    public static bool LooksLikeConsentFromArgs(JsonElement args)
    {
        if (args.TryGetProperty("relative", out _))
        {
            return true;
        }
        var selector = OrgJson.OptObject(args, InterlockTargets.KeySelector) ?? args;
        var pkg = OrgJson.OptString(selector, "package");
        return pkg.Length > 0 && IsConsentPackage(pkg);
    }

    public static bool LooksLikeConsentNode(string? packageName, string? viewId) => IsConsentPackage(packageName) || ConsentViewId(viewId);
}
