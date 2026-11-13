// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/exec/a11y/NodeSelector.kt
//   src/main/java/interlock/relay/core/exec/a11y/RelativeGeometry.kt
//   src/main/java/interlock/relay/core/exec/a11y/TrustedNodeTree.kt
//   src/main/java/interlock/relay/core/exec/a11y/NodeActions.kt (normalizeBounds, staleNodeIdReason, KEY_* constants)
//   src/main/java/interlock/relay/core/exec/a11y/A11yBackend.kt (capability sets, ARG_SPECS, argsError, badArgShape,
//     badCoordinate, numberArg)
// Registered in docs/UPSTREAMS.md. Locked by tests/TestVectors/relay-a11y.json.
//
// Security: nodeId numbering (preorder, depth/node caps) must match ui.snapshot exactly — a mismatch is a click on a
// different node that still reports success. Keep these in lockstep with upstream.

using System.Globalization;
using System.Text.Json;

namespace DshMobile.Runtime.Automation.Relay.A11y;

/// <summary>Upstream <c>NodeView</c>: the read-only face of one accessibility node.</summary>
public interface INodeView
{
    string? Text { get; }

    string? ContentDescription { get; }

    string? ViewId { get; }

    string? ClassName { get; }

    string? PackageName { get; }

    /// <summary>Position among the parent's children (0 for the root).</summary>
    int SiblingIndex { get; }

    IReadOnlyList<INodeView> Children { get; }
}

public sealed record Selector(
    string? Text = null,
    string? ContentDescription = null,
    string? ViewId = null,
    string? ClassName = null,
    int? Index = null,
    string? PackageHint = null)
{
    public bool Unconstrained => Text is null && ContentDescription is null && ViewId is null && ClassName is null && Index is null && PackageHint is null;
}

public enum MatchMode
{
    Exact,
    Contains,
}

public sealed record NodeMatch(INodeView Node, int NodeId, int Depth, MatchMode Mode);

public abstract record SelectorSpec
{
    public sealed record Parsed(Selector Selector) : SelectorSpec;

    public sealed record ByNodeId(int NodeId) : SelectorSpec;

    public sealed record Invalid(string Reason) : SelectorSpec;
}

/// <summary>Upstream <c>NodeSelector</c>.</summary>
public static class NodeSelector
{
    public const string KeySelector = "selector";
    public const string KeyNodeId = "nodeId";
    public const string KeyText = "text";
    public const string KeyDesc = "desc";
    public const string KeyId = "id";
    public const string KeyClass = "class";
    public const string KeyIndex = "index";
    public const string KeyPackage = "package";

    /// <summary>Declaration order matters: it is the order the error messages list them in.</summary>
    public static readonly IReadOnlyList<string> SelectorKeys = [KeyText, KeyDesc, KeyId, KeyClass, KeyIndex, KeyPackage];

    private static readonly string[] TopLevelKeys = [KeySelector, KeyNodeId];

    public const int MaxNodes = 600;
    public const int MaxDepth = 40;
    public const int DefaultMatchLimit = 20;

    private static readonly string[] ClassTails = ["."];
    private static readonly string[] IdTails = [":", "/"];

    private static string Join(IEnumerable<string> keys) => string.Join(", ", keys);

    private static string? Str(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool NotBlank(string? s) => s is not null && OrgJson.KotlinTrim(s).Length > 0;

    public static SelectorSpec Parse(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
        {
            return new SelectorSpec.Invalid("no selector given");
        }
        foreach (var p in args.EnumerateObject())
        {
            if (!TopLevelKeys.Contains(p.Name))
            {
                return new SelectorSpec.Invalid($"unknown key: {p.Name}, accepted keys: {Join(TopLevelKeys)}");
            }
        }
        if (args.TryGetProperty(KeyNodeId, out _))
        {
            if (args.TryGetProperty(KeySelector, out _))
            {
                return new SelectorSpec.Invalid($"{KeyNodeId} and {KeySelector} are mutually exclusive: send one shape");
            }
            return Integer(args, KeyNodeId, MaxNodes) is { } nodeId
                ? new SelectorSpec.ByNodeId(nodeId)
                : new SelectorSpec.Invalid($"{KeyNodeId} must be a whole number in [0,{MaxNodes}) taken from ui.snapshot");
        }
        if (!args.TryGetProperty(KeySelector, out var spec))
        {
            return new SelectorSpec.Invalid(
                $"no selector given: expected a \"{KeySelector}\" object or a \"{KeyNodeId}\" number, accepted keys: {Join(TopLevelKeys)}");
        }
        if (spec.ValueKind != JsonValueKind.Object)
        {
            return new SelectorSpec.Invalid($"{KeySelector} must be an object holding any of {Join(SelectorKeys)}");
        }
        return ParseSelector(spec);
    }

    private static SelectorSpec ParseSelector(JsonElement spec)
    {
        var any = false;
        foreach (var p in spec.EnumerateObject())
        {
            any = true;
            if (!SelectorKeys.Contains(p.Name))
            {
                return new SelectorSpec.Invalid($"unknown key in {KeySelector}: {p.Name}, accepted keys: {Join(SelectorKeys)}");
            }
        }
        if (!any)
        {
            return new SelectorSpec.Invalid($"empty {KeySelector}: give at least one of {Join(SelectorKeys)}");
        }
        string? text = null, desc = null, viewId = null, className = null, packageHint = null;
        int? index = null;
        foreach (var p in spec.EnumerateObject())
        {
            switch (p.Name)
            {
                case KeyIndex:
                    index = Integer(spec, KeyIndex, MaxNodes);
                    if (index is null)
                    {
                        return new SelectorSpec.Invalid($"{KeySelector}.{KeyIndex} must be a whole number in [0,{MaxNodes})");
                    }
                    break;
                default:
                    var s = Str(spec, p.Name);
                    if (!NotBlank(s))
                    {
                        return new SelectorSpec.Invalid($"{KeySelector}.{p.Name} must be a non-empty string");
                    }
                    switch (p.Name)
                    {
                        case KeyText: text = s; break;
                        case KeyDesc: desc = s; break;
                        case KeyId: viewId = s; break;
                        case KeyClass: className = s; break;
                        default: packageHint = s; break;
                    }
                    break;
            }
        }
        return new SelectorSpec.Parsed(new Selector(text, desc, viewId, className, index, packageHint));
    }

    public static bool Matches(INodeView node, Selector selector, bool allowContains = false) => MatchModeOf(node, selector, allowContains) is not null;

    /// <summary>Strict matches first; only when none and <paramref name="allowContains"/>, the case-insensitive containment pass.</summary>
    public static IReadOnlyList<NodeMatch> FindAll(INodeView? root, Selector selector, int limit = DefaultMatchLimit, bool allowContains = false)
    {
        var strict = Collect(root, selector, limit, allowContains: false);
        if (strict.Count > 0 || !allowContains)
        {
            return strict;
        }
        return Collect(root, selector, limit, allowContains: true);
    }

    public static INodeView? FindById(INodeView? root, int nodeId)
    {
        INodeView? found = null;
        Traverse(root, (node, id, _) =>
        {
            if (id != nodeId)
            {
                return false;
            }
            found = node;
            return true;
        });
        return found;
    }

    private static List<NodeMatch> Collect(INodeView? root, Selector selector, int limit, bool allowContains)
    {
        var hits = new List<NodeMatch>();
        Traverse(root, (node, nodeId, depth) =>
        {
            if (MatchModeOf(node, selector, allowContains) is { } mode)
            {
                hits.Add(new NodeMatch(node, nodeId, depth, mode));
            }
            return hits.Count >= limit;
        });
        return hits;
    }

    /// <summary>Preorder; a node deeper than <see cref="MaxDepth"/> is skipped uncounted; at most <see cref="MaxNodes"/> are numbered.</summary>
    public static void Traverse(INodeView? root, Func<INodeView, int, int, bool> visit)
    {
        ArgumentNullException.ThrowIfNull(visit);
        var visited = 0;
        bool Descend(INodeView node, int depth)
        {
            if (depth > MaxDepth || visited >= MaxNodes)
            {
                return false;
            }
            if (visit(node, visited++, depth))
            {
                return true;
            }
            foreach (var child in node.Children)
            {
                if (Descend(child, depth + 1))
                {
                    return true;
                }
            }
            return false;
        }
        if (root is not null)
        {
            Descend(root, 0);
        }
    }

    private static MatchMode? MatchModeOf(INodeView node, Selector selector, bool allowContains)
    {
        if (selector.Unconstrained)
        {
            return null;
        }
        if (selector.Index is { } i && node.SiblingIndex != i)
        {
            return null;
        }
        if (selector.PackageHint is not null && node.PackageName != selector.PackageHint)
        {
            return null;
        }
        if (selector.ClassName is not null && !QualifiedNameMatches(node.ClassName, selector.ClassName, ClassTails))
        {
            return null;
        }
        if (selector.ViewId is not null && !QualifiedNameMatches(node.ViewId, selector.ViewId, IdTails))
        {
            return null;
        }
        var strict = TextEquals(node.Text, selector.Text) && TextEquals(node.ContentDescription, selector.ContentDescription);
        if (strict)
        {
            return MatchMode.Exact;
        }
        var loose = ContainsText(node.Text, selector.Text) && ContainsText(node.ContentDescription, selector.ContentDescription);
        return loose && allowContains ? MatchMode.Contains : null;
    }

    private static bool QualifiedNameMatches(string? actual, string wanted, string[] tails)
    {
        if (actual is null)
        {
            return false;
        }
        if (actual == wanted)
        {
            return true;
        }
        return tails.Any(sep => actual.Contains(sep, StringComparison.Ordinal) && actual[(actual.LastIndexOf(sep, StringComparison.Ordinal) + sep.Length)..] == wanted);
    }

    private static bool TextEquals(string? actual, string? wanted) => wanted is null || actual == wanted;

    private static bool ContainsText(string? actual, string? wanted) => wanted is null || (actual is not null && KotlinContainsIgnoreCase(actual, wanted));

    /// <summary>Kotlin <c>contains(other, ignoreCase = true)</c>: char-wise, equal if same, same upper case or same lower case.</summary>
    internal static bool KotlinContainsIgnoreCase(string s, string other)
    {
        for (var start = 0; start + other.Length <= s.Length; start++)
        {
            var all = true;
            for (var k = 0; k < other.Length; k++)
            {
                char a = s[start + k], b = other[k];
                if (a != b && char.ToUpperInvariant(a) != char.ToUpperInvariant(b) && char.ToLowerInvariant(a) != char.ToLowerInvariant(b))
                {
                    all = false;
                    break;
                }
            }
            if (all)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A JSON number that is a whole value in [0, bound).</summary>
    private static int? Integer(JsonElement source, string key, int bound)
    {
        if (!source.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Number)
        {
            return null;
        }
        var raw = double.Parse(v.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture);
        if (!double.IsFinite(raw) || raw % 1.0 != 0.0)
        {
            return null;
        }
        var value = OrgJson.JavaDoubleToInt(raw);
        return raw == value && value >= 0 && value < bound ? value : null;
    }
}

/// <summary>Upstream <c>RelativeGeometry</c>: pick the box on one side of an anchor.</summary>
public static class RelativeGeometry
{
    public enum Side
    {
        Right,
        Left,
        Above,
        Below,
    }

    public sealed record Box(int Ref, int Left, int Top, int Right, int Bottom, string? ClassName);

    public abstract record Outcome
    {
        public sealed record Pick(Box Box, int GapPx) : Outcome;

        public sealed record None : Outcome;

        public sealed record Ambiguous(IReadOnlyList<Box> Boxes, int GapPx) : Outcome;
    }

    private const int TiePx = 2;

    private static int TieTolerance(Box anchor) => Math.Max(TiePx, Math.Max(anchor.Right - anchor.Left, anchor.Bottom - anchor.Top) / 20);

    public static Side? SideOf(string value) => value.ToLowerInvariant() switch
    {
        "right" => Side.Right,
        "left" => Side.Left,
        "above" or "up" => Side.Above,
        "below" or "down" => Side.Below,
        _ => null,
    };

    public static Outcome Pick(Box anchor, IReadOnlyList<Box> candidates, Side side, int nth, bool sameClass)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(candidates);
        var pool = candidates.Where(b => b.Ref != anchor.Ref)
            .Where(b => !sameClass || b.ClassName == anchor.ClassName)
            .Where(b => OnSide(b, anchor, side))
            .ToList();
        if (pool.Count == 0)
        {
            return new Outcome.None();
        }
        var aligned = pool.Where(b => CrossOverlap(b, anchor, side) > 0).ToList();
        var ranked = (aligned.Count > 0 ? aligned : pool)
            .Select(b => (Box: b, Gap: Gap(b, anchor, side), Cross: CrossOverlap(b, anchor, side)))
            .OrderBy(r => r.Gap).ThenBy(r => -r.Cross).ThenBy(r => r.Box.Ref)
            .ToList();
        if (nth < 1 || nth > ranked.Count)
        {
            return new Outcome.None();
        }
        var target = ranked[nth - 1];
        var tolerance = TieTolerance(anchor);
        var tied = ranked.Where(r => Math.Abs(r.Gap - target.Gap) <= tolerance && Math.Abs(r.Cross - target.Cross) <= tolerance).ToList();
        if (tied.Count > 1)
        {
            return new Outcome.Ambiguous([.. tied.Select(r => r.Box)], target.Gap);
        }
        return new Outcome.Pick(target.Box, target.Gap);
    }

    private static bool OnSide(Box b, Box anchor, Side side) => side switch
    {
        Side.Right => b.Left >= anchor.Right,
        Side.Left => b.Right <= anchor.Left,
        Side.Below => b.Top >= anchor.Bottom,
        _ => b.Bottom <= anchor.Top,
    };

    private static int Gap(Box b, Box anchor, Side side) => side switch
    {
        Side.Right => b.Left - anchor.Right,
        Side.Left => anchor.Left - b.Right,
        Side.Below => b.Top - anchor.Bottom,
        _ => anchor.Top - b.Bottom,
    };

    private static int CrossOverlap(Box b, Box anchor, Side side) => side is Side.Right or Side.Left
        ? Math.Min(b.Bottom, anchor.Bottom) - Math.Max(b.Top, anchor.Top)
        : Math.Min(b.Right, anchor.Right) - Math.Max(b.Left, anchor.Left);
}

/// <summary>Upstream <c>TrustedNodeTree</c>: what the last look at the trusted display's tree found.</summary>
public sealed class TrustedNodeTree
{
    public const string StateLastSeen = "last-seen";
    public const string StateLastEmpty = "last-empty";
    public const string StateUntested = "untested";

    private sealed record Observation(int DisplayId, bool TreeFound);

    private Observation? _last;

    public void Observe(int displayId, bool treeFound) => Volatile.Write(ref _last, new Observation(displayId, treeFound));

    public string State(int aliveDisplayId)
    {
        if (aliveDisplayId < 0)
        {
            Volatile.Write(ref _last, null);
            return StateUntested;
        }
        var seen = Volatile.Read(ref _last);
        if (seen is null || seen.DisplayId != aliveDisplayId)
        {
            return StateUntested;
        }
        return seen.TreeFound ? StateLastSeen : StateLastEmpty;
    }
}

/// <summary>android.graphics.Rect as the a11y code uses it.</summary>
public readonly record struct Bounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

/// <summary>Pure pieces of upstream <c>NodeActions</c> / <c>A11yBackend</c>.</summary>
public static class A11yRules
{
    // NodeActions companion keys
    public const string KeyDirection = "direction";
    public const string KeyTimes = "times";
    public const string KeyUntil = "until";
    public const string KeyRelative = "relative";
    public const string KeySide = "side";
    public const string KeyNth = "nth";
    public const string KeySameClass = "sameClass";
    public const string KeyText = "text";
    public const string KeyChecked = "checked";
    public const string KeyAbsent = "absent";
    public const string KeyTimeoutMs = "timeoutMs";
    public const string KeyPercent = "percent";
    public const string KeyValue = "value";

    // A11yBackend companion keys
    public const string KeyKey = "key";
    public const string KeyX = "x";
    public const string KeyY = "y";
    public const string KeyFromX = "fromX";
    public const string KeyFromY = "fromY";
    public const string KeyToX = "toX";
    public const string KeyToY = "toY";
    public const string KeyDurationMs = "durationMs";

    public const long TapDurationMs = 60;
    public const long SwipeDurationMs = 260;
    public const long MinGestureMs = 50;
    public const long MaxGestureMs = 3_000;
    public const string MissingNoWindow = "no-window-on-display";

    public static readonly IReadOnlyList<string> PointKeys = [KeyX, KeyY];
    public static readonly IReadOnlyList<string> TargetKeys = [NodeSelector.KeySelector, NodeSelector.KeyNodeId, KeyRelative];
    public static readonly IReadOnlyList<string> StrokeKeys = [KeyFromX, KeyFromY, KeyToX, KeyToY];
    public static readonly IReadOnlyList<string> SwipeKeys = [.. StrokeKeys, KeyDurationMs];

    public static readonly IReadOnlySet<string> CoordinateCapabilities = new HashSet<string>(StringComparer.Ordinal) { "ui.tap", "ui.swipe", "ui.text", "ui.key" };
    public static readonly IReadOnlySet<string> NodeReadCapabilities = new HashSet<string>(StringComparer.Ordinal) { "ui.snapshot", "ui.node", "ui.waitFor" };

    public static readonly IReadOnlySet<string> NodeWriteCapabilities = new HashSet<string>(StringComparer.Ordinal)
    {
        "ui.click", "ui.longClick", "ui.select", "ui.dismiss", "ui.scroll", "ui.setValue", "ui.setProgress", "ui.imeAction",
    };

    public static readonly IReadOnlySet<string> TreeCapabilities = new HashSet<string>(NodeReadCapabilities.Concat(NodeWriteCapabilities), StringComparer.Ordinal);
    public static readonly IReadOnlySet<string> WritingCapabilities = new HashSet<string>(CoordinateCapabilities.Concat(NodeWriteCapabilities), StringComparer.Ordinal);

    public static readonly IReadOnlyDictionary<string, int> GlobalActions = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["back"] = 1, // AccessibilityService.GLOBAL_ACTION_BACK
        ["home"] = 2, // GLOBAL_ACTION_HOME
        ["recents"] = 3, // GLOBAL_ACTION_RECENTS
    };

    /// <summary>Upstream <c>ArgSpec</c>; <see cref="Allowed"/> keeps declaration order (it is listed in error text).</summary>
    public sealed record ArgSpec(IReadOnlyList<string> Allowed, IReadOnlyList<string> Required);

    private static ArgSpec Spec(IReadOnlyList<string>? allowed = null, IReadOnlyList<string>? required = null) => new(allowed ?? [], required ?? []);

    public static readonly IReadOnlyDictionary<string, ArgSpec> ArgSpecs = new Dictionary<string, ArgSpec>(StringComparer.Ordinal)
    {
        ["ui.snapshot"] = Spec(),
        ["screen.capture"] = Spec(),
        ["ui.tap"] = Spec(PointKeys, PointKeys),
        ["ui.swipe"] = Spec(SwipeKeys, StrokeKeys),
        ["ui.text"] = Spec([KeyText], [KeyText]),
        ["ui.click"] = Spec(TargetKeys),
        ["ui.longClick"] = Spec(TargetKeys),
        ["ui.select"] = Spec(TargetKeys),
        ["ui.dismiss"] = Spec(TargetKeys),
        ["ui.node"] = Spec(TargetKeys),
        ["ui.imeAction"] = Spec(TargetKeys),
        ["ui.scroll"] = Spec([.. TargetKeys, KeyDirection, KeyTimes, KeyUntil], [KeyDirection]),
        ["ui.setValue"] = Spec([.. TargetKeys, KeyText], [KeyText]),
        ["ui.setProgress"] = Spec([.. TargetKeys, KeyPercent, KeyValue]),
        ["ui.waitFor"] = Spec([.. TargetKeys, KeyText, KeyChecked, KeyAbsent, KeyTimeoutMs]),
        ["ui.key"] = Spec([KeyKey], [KeyKey]),
    };

    /// <summary>Upstream <c>argsError</c>'s reason (TRANSPORT_MALFORMED), or null.</summary>
    public static string? ArgsError(string capability, JsonElement args)
    {
        if (!ArgSpecs.TryGetValue(capability, out var spec))
        {
            return null;
        }
        foreach (var p in args.EnumerateObject())
        {
            if (!spec.Allowed.Contains(p.Name))
            {
                return ArgErrors.Unknown(p.Name, spec.Allowed);
            }
        }
        foreach (var key in spec.Required)
        {
            if (OrgJson.KotlinTrim(OrgJson.OptString(args, key)).Length == 0)
            {
                return "missing arg: " + key;
            }
        }
        return null;
    }

    /// <summary>Upstream <c>badArgShape</c>.</summary>
    public static string? BadArgShape(JsonElement args, IReadOnlyList<string> required, IReadOnlyList<string> allowed)
    {
        foreach (var p in args.EnumerateObject())
        {
            if (!allowed.Contains(p.Name))
            {
                return ArgErrors.Unknown(p.Name, allowed);
            }
        }
        foreach (var key in required)
        {
            if (!args.TryGetProperty(key, out _))
            {
                return "missing arg: " + key;
            }
        }
        return null;
    }

    /// <summary>Upstream <c>badCoordinate</c>: every key a finite number in [0, limit).</summary>
    public static string? BadCoordinate(JsonElement args, IReadOnlyList<(string Key, int Limit)> limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        foreach (var (key, limit) in limits)
        {
            if (NumberArg(args, key) is not { } value)
            {
                return ArgErrors.NotNumber(key, args.TryGetProperty(key, out var raw) ? raw : default);
            }
            if (value < 0 || value >= limit)
            {
                return $"{key} out of screen [0,{limit.ToString(CultureInfo.InvariantCulture)}): {OrgJson.JavaDouble(value)}";
            }
        }
        return null;
    }

    /// <summary>Upstream <c>numberArg</c>: a JSON number (never a numeric string), finite.</summary>
    public static double? NumberArg(JsonElement args, string key)
    {
        if (!args.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Number)
        {
            return null;
        }
        var d = double.Parse(v.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture);
        return double.IsFinite(d) ? d : null;
    }

    /// <summary>Upstream <c>normalizeBounds</c>: reorder inverted edges, clip to the screen, null when no area is left.</summary>
    public static Bounds? NormalizeBounds(Bounds raw, Bounds screen)
    {
        var left = Math.Max(Math.Min(raw.Left, raw.Right), screen.Left);
        var top = Math.Max(Math.Min(raw.Top, raw.Bottom), screen.Top);
        var right = Math.Min(Math.Max(raw.Left, raw.Right), screen.Right);
        var bottom = Math.Min(Math.Max(raw.Top, raw.Bottom), screen.Bottom);
        var b = new Bounds(left, top, right, bottom);
        return b.Width > 0 && b.Height > 0 ? b : null;
    }

    /// <summary>Upstream <c>staleNodeIdReason</c>.</summary>
    public static string? StaleNodeIdReason(bool askedByNodeId, bool visibleToUser, bool hasOnScreenArea, int displayId)
    {
        if (!askedByNodeId || (visibleToUser && hasOnScreenArea))
        {
            return null;
        }
        return "resolved by nodeId, but this node is not visible on display " + displayId.ToString(CultureInfo.InvariantCulture)
            + ": this id is a position in the tree this call just read, so if it came from an earlier snapshot the page "
            + "may have scrolled or refreshed and this position now holds a different node (an id taken "
            + "from another display always means a different tree). Take a fresh ui.snapshot on the display "
            + "you mean to act on and use the nodeId from it - do not act on this result as if it were the "
            + "intended node.";
    }
}
