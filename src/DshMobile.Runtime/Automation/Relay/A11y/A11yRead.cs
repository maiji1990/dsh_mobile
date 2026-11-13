// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/exec/a11y/A11yBackend.kt (snapshot, collect, screenData, noActiveWindow,
//     suspendedByHost, selfWindowVisible, the host-window rule in execute, hostTree)
//   src/main/java/interlock/relay/core/exec/a11y/NodeActions.kt (findByIdPreorder, resolve, absoluteTarget,
//     relativeTarget, boxesPreorder, boxOf, readNode, staleNodeIdWarning, ambiguous, describe, rangeData,
//     rangeTypeName, actionName, ownerOf)
//   src/main/java/interlock/relay/core/exec/a11y/DisplayTree.kt (DisplayTree, retryOnce)
// Registered in docs/UPSTREAMS.md. Read path only (ui.snapshot / ui.node); the write path stays in the legacy Relay.
// Locked by tests/TestVectors/relay-a11y-read.json and the on-device shadow automation.relay-a11y-read.
//
// Security: nodeId numbering here, in NodeSelector and in the snapshot must be the same preorder (null children
// skipped, depth/node caps) — a mismatch turns a snapshot id into a click on a different node that still reports
// success. The host-window rule (HostWindowRule) is what keeps the agent from pressing its own approval.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DshMobile.Runtime.Automation.Relay.Exec;

namespace DshMobile.Runtime.Automation.Relay.A11y;

/// <summary>Upstream <c>AccessibilityNodeInfo.RangeInfo</c>: the numbers a slider or progress bar reports.</summary>
public sealed record A11yRange(int Type, float Min, float Max, float Current);

/// <summary>
/// The parts of an <c>AccessibilityNodeInfo</c> the read path uses. <see cref="INodeView.Children"/> must be the
/// non-null <see cref="GetChild"/> results with their child index as <see cref="INodeView.SiblingIndex"/>, the same
/// nodes <see cref="GetChild"/> returns, so that every walk numbers the tree the same way.
/// </summary>
public interface IA11yNode : INodeView
{
    Bounds BoundsInScreen { get; }

    int ChildCount { get; }

    /// <summary>The child at <paramref name="index"/>, or null when the platform gives none (a window being torn down).</summary>
    IA11yNode? GetChild(int index);

    bool IsScrollable { get; }

    bool IsClickable { get; }

    bool IsEditable { get; }

    bool IsChecked { get; }

    bool IsCheckable { get; }

    bool IsEnabled { get; }

    bool IsSelected { get; }

    bool IsVisibleToUser { get; }

    bool IsFocused { get; }

    /// <summary><c>actionList</c> ids in the order the node reports them.</summary>
    IReadOnlyList<int> ActionIds { get; }

    A11yRange? Range { get; }
}

/// <summary>Upstream <c>DisplayTree</c>: which display a call reads, its pixel frame and where the root comes from.</summary>
public sealed class A11yTree(int displayId, Bounds screen, int densityDpi, Func<IA11yNode?> rootProvider)
{
    public int DisplayId { get; } = displayId;

    public Bounds Screen { get; } = screen;

    public int DensityDpi { get; } = densityDpi;

    public IA11yNode? Root() => rootProvider();

    /// <summary>Upstream <c>DisplayTree.retryOnce</c>: a null root right after a page switch is read once more.</summary>
    public static Func<IA11yNode?> RetryOnce(Func<IA11yNode?> first)
    {
        ArgumentNullException.ThrowIfNull(first);
        return () => first() ?? first();
    }
}

/// <summary>Android constants the read path names (AccessibilityNodeInfo / android.R.id / RangeInfo).</summary>
public static class A11yConstants
{
    public const int DefaultDisplay = 0;

    public const int ActionSelect = 0x00000004;
    public const int ActionClick = 0x00000010;
    public const int ActionLongClick = 0x00000020;
    public const int ActionScrollForward = 0x00001000;
    public const int ActionScrollBackward = 0x00002000;
    public const int ActionDismiss = 0x00100000;
    public const int ActionSetText = 0x00200000;

    /// <summary><c>AccessibilityAction.ACTION_SET_PROGRESS.id</c> = <c>android.R.id.accessibilityActionSetProgress</c>.</summary>
    public const int ActionSetProgress = 0x0102003d;

    /// <summary><c>AccessibilityAction.ACTION_IME_ENTER.id</c> = <c>android.R.id.accessibilityActionImeEnter</c> (API 30; -1 below).</summary>
    public const int ActionImeEnter = 0x01020054;

    public const int RangeTypeInt = 0;
    public const int RangeTypeFloat = 1;
    public const int RangeTypePercent = 2;
    public const int RangeTypeIndeterminate = 3;
}

/// <summary>Upstream <c>A11yBackend</c>'s rule that the agent never drives this app's own windows.</summary>
public static class HostWindowRule
{
    /// <summary>
    /// <c>controlsInput &amp;&amp; onHostDisplay &amp;&amp; selfWindowVisible</c>: a writing capability on the
    /// host display while any window whose root belongs to <paramref name="selfPackage"/> is listed.
    /// </summary>
    public static bool Suspends(string capability, SurfaceKind surface, IEnumerable<string?> windowRootPackages, string selfPackage)
    {
        ArgumentNullException.ThrowIfNull(windowRootPackages);
        return A11yRules.WritingCapabilities.Contains(capability)
            && surface != SurfaceKind.BackendTrusted
            && windowRootPackages.Any(p => p == selfPackage);
    }

    /// <summary>Upstream <c>suspendedByHost</c>.</summary>
    public static BackendResult.Failed SuspendedByHost() =>
        new(RelayError.GateSuspendedByHost, "host window is in front: retry after the user leaves this screen");
}

/// <summary>Upstream <c>A11yBackend.snapshot</c> / <c>collect</c> / <c>screenData</c>.</summary>
public static class A11ySnapshot
{
    public const int MaxNodes = 600;
    public const int MaxDepth = 40;

    public static BackendResult Snapshot(A11yTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var root = tree.Root();
        if (root is null)
        {
            return NodeReader.NoActiveWindow(tree.DisplayId);
        }
        var nodes = new JsonArray();
        Collect(root, nodes, 0, tree.Screen);
        var data = new JsonObject();
        // JSONObject.put(name, null) removes the name: a root without a package has no "package" key.
        if (root.PackageName is { } package)
        {
            data["package"] = package;
        }
        data["screen"] = ScreenData(tree);
        data["nodes"] = nodes;
        data["truncated"] = nodes.Count >= MaxNodes;
        return new BackendResult.Ok(data);
    }

    public static JsonObject ScreenData(A11yTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var o = new JsonObject
        {
            ["width"] = tree.Screen.Width,
            ["height"] = tree.Screen.Height,
        };
        if (tree.DensityDpi > 0)
        {
            o["densityDpi"] = tree.DensityDpi;
        }
        o["display"] = tree.DisplayId;
        return o;
    }

    private static void Collect(IA11yNode? node, JsonArray output, int depth, Bounds screen)
    {
        if (node is null || output.Count >= MaxNodes || depth > MaxDepth)
        {
            return;
        }
        var visible = A11yRules.NormalizeBounds(node.BoundsInScreen, screen);
        var o = new JsonObject
        {
            ["nodeId"] = output.Count,
            ["class"] = node.ClassName ?? "",
        };
        if (node.IsScrollable)
        {
            o["scrollable"] = true;
        }
        if (node.Text is { Length: > 0 } text)
        {
            o["text"] = text;
        }
        if (node.ContentDescription is { Length: > 0 } desc)
        {
            o["desc"] = desc;
        }
        PutBounds(o, visible);
        if (node.IsClickable)
        {
            o["clickable"] = true;
        }
        if (node.IsEditable)
        {
            o["editable"] = true;
        }
        if (node.IsChecked)
        {
            o["checked"] = true;
        }
        output.Add(o);
        for (var i = 0; i < node.ChildCount; i++)
        {
            Collect(node.GetChild(i), output, depth + 1, screen);
        }
    }

    internal static void PutBounds(JsonObject o, Bounds? visible)
    {
        if (visible is not { } v)
        {
            o["offscreen"] = true;
            return;
        }
        o["left"] = v.Left;
        o["top"] = v.Top;
        o["right"] = v.Right;
        o["bottom"] = v.Bottom;
    }
}

/// <summary>Upstream <c>NodeActions</c>, read side: target resolution, <c>ui.node</c> and <c>ownerOf</c>.</summary>
public sealed partial class NodeReader(A11yTree tree)
{
    private const int MaxRelativeNth = 20;
    private const int MaxCandidates = 8;

    private readonly A11yTree _tree = tree ?? throw new ArgumentNullException(nameof(tree));

    private abstract record Target
    {
        public sealed record Bad(RelayError Error, string Reason) : Target;

        public sealed record Many(IReadOnlyList<NodeMatch> Matches) : Target;

        public sealed record One(IA11yNode Node, int NodeId, MatchMode Mode) : Target;
    }

    private Bounds Screen => _tree.Screen;

    private IA11yNode? Root() => _tree.Root();

    /// <summary>Upstream <c>readNode</c>.</summary>
    public BackendResult ReadNode(JsonElement args) => Resolve(args) switch
    {
        Target.Bad bad => new BackendResult.Failed(bad.Error, bad.Reason),
        Target.Many many => Ambiguous(many.Matches),
        Target.One one => ReadOk(args, one),
        _ => throw new InvalidOperationException(),
    };

    private BackendResult.Ok ReadOk(JsonElement args, Target.One one)
    {
        var body = new JsonObject
        {
            ["matchMode"] = one.Mode == MatchMode.Exact ? "exact" : "contains",
            ["nodeId"] = one.NodeId,
            ["node"] = Describe(one.Node),
            ["treeDisplay"] = _tree.DisplayId,
        };
        if (StaleNodeIdWarning(args, one) is { } warning)
        {
            body["treeWarning"] = warning;
        }
        return new BackendResult.Ok(body);
    }

    /// <summary>Upstream <c>ownerOf</c>: (package, view id) of the node at this preorder id, read only.</summary>
    public TargetOwner? OwnerOf(int nodeId) =>
        FindByIdPreorder(Root(), nodeId) is { } node ? new TargetOwner(node.PackageName, node.ViewId) : null;

    /// <summary>Upstream <c>noActiveWindow</c> (the same words every node call uses for an unreadable tree).</summary>
    public static BackendResult.Failed NoActiveWindow(int displayId) => new(RelayError.BackendUnavailable, NoTree(displayId));

    private static string NoTree(int displayId) =>
        "no window tree to read on display " + displayId.ToString(CultureInfo.InvariantCulture)
        + " after a second read: no window holds focus there - retry once one does";

    /// <summary>Upstream <c>findByIdPreorder</c>.</summary>
    public static IA11yNode? FindByIdPreorder(IA11yNode? root, int nodeId)
    {
        if (root is null || nodeId < 0)
        {
            return null;
        }
        var counter = 0;
        IA11yNode? found = null;
        bool Walk(IA11yNode node, int depth)
        {
            if (depth > NodeSelector.MaxDepth || counter >= NodeSelector.MaxNodes)
            {
                return false;
            }
            if (counter == nodeId)
            {
                found = node;
                return true;
            }
            counter++;
            for (var i = 0; i < node.ChildCount; i++)
            {
                if (node.GetChild(i) is { } child && Walk(child, depth + 1))
                {
                    return true;
                }
            }
            return false;
        }
        Walk(root, 0);
        return found;
    }

    private Target Resolve(JsonElement args)
    {
        if (!args.TryGetProperty(A11yRules.KeyRelative, out _))
        {
            return AbsoluteTarget(args);
        }
        var anchor = AbsoluteTarget(args);
        return anchor is Target.One one ? RelativeTarget(one, OrgJson.OptObject(args, A11yRules.KeyRelative)) : anchor;
    }

    private Target AbsoluteTarget(JsonElement args)
    {
        // Only the two target keys go to the selector parser (its key table is strict).
        var target = new JsonObject();
        foreach (var key in new[] { NodeSelector.KeySelector, NodeSelector.KeyNodeId })
        {
            if (args.ValueKind == JsonValueKind.Object && Last(args, key) is { } value)
            {
                target[key] = JsonNode.Parse(value.GetRawText());
            }
        }
        var spec = NodeSelector.Parse(JsonSerializer.SerializeToElement(target));
        switch (spec)
        {
            case SelectorSpec.Invalid invalid:
                return new Target.Bad(RelayError.TransportMalformed, invalid.Reason);
            case SelectorSpec.ByNodeId byId:
            {
                if (Root() is not { } currentRoot)
                {
                    return new Target.Bad(RelayError.BackendUnavailable, NoTree(_tree.DisplayId));
                }
                return FindByIdPreorder(currentRoot, byId.NodeId) is { } node
                    ? new Target.One(node, byId.NodeId, MatchMode.Exact)
                    : new Target.Bad(RelayError.NodeNotFound,
                        $"no node with nodeId={byId.NodeId.ToString(CultureInfo.InvariantCulture)} in the current tree: "
                        + "node ids come from ui.snapshot and shift when the screen changes");
            }
            case SelectorSpec.Parsed parsed:
            {
                if (Root() is not { } top)
                {
                    return new Target.Bad(RelayError.BackendUnavailable, NoTree(_tree.DisplayId));
                }
                var matches = NodeSelector.FindAll(top, parsed.Selector);
                if (matches.Count == 0)
                {
                    return new Target.Bad(RelayError.NodeNotFound, "selector matched no node");
                }
                if (matches.Count > 1)
                {
                    return new Target.Many(matches);
                }
                var first = matches[0];
                return first.Node is IA11yNode node
                    ? new Target.One(node, first.NodeId, first.Mode)
                    : new Target.Bad(RelayError.NodeNotFound, "matched node is no longer in the tree");
            }
            default:
                throw new InvalidOperationException();
        }
    }

    private Target RelativeTarget(Target.One anchor, JsonElement? rel)
    {
        const string side = A11yRules.KeySide;
        const string nthKey = A11yRules.KeyNth;
        const string same = A11yRules.KeySameClass;
        if (rel is not { } r)
        {
            return new Target.Bad(RelayError.TransportMalformed,
                $"{A11yRules.KeyRelative} must be an object with {side} and optional {nthKey}, {same}");
        }
        foreach (var p in r.EnumerateObject())
        {
            if (p.Name is not (side or nthKey or same))
            {
                return new Target.Bad(RelayError.TransportMalformed, $"unknown key in {A11yRules.KeyRelative}: {p.Name}");
            }
        }
        var sideName = OrgJson.OptString(r, side).ToLowerInvariant();
        if (RelativeGeometry.SideOf(sideName) is not { } s)
        {
            return new Target.Bad(RelayError.TransportMalformed, $"relative.{side} must be right|left|above|below, got '{sideName}'");
        }
        var nth = r.TryGetProperty(nthKey, out _) ? OrgJson.OptInt(r, nthKey) : 1;
        if (nth < 1 || nth > MaxRelativeNth)
        {
            return new Target.Bad(RelayError.TransportMalformed,
                $"relative.{nthKey} must be within 1..{MaxRelativeNth.ToString(CultureInfo.InvariantCulture)}");
        }
        var sameClass = OptBoolean(r, same);
        if (Root() is not { } top)
        {
            return new Target.Bad(RelayError.BackendUnavailable, NoTree(_tree.DisplayId));
        }
        var anchorId = anchor.NodeId.ToString(CultureInfo.InvariantCulture);
        if (FindByIdPreorder(top, anchor.NodeId) is not { } anchorNode)
        {
            return new Target.Bad(RelayError.NodeNotFound,
                $"anchor nodeId={anchorId} is gone from the current tree; node ids shift when the screen changes");
        }
        var candidates = BoxesPreorder(top).Select(e => BoxOf(e.Id, e.Node)).ToList();
        var scope = sameClass ? " with the anchor's class" : "";
        var ordinal = nth > 1 ? $" no {nth.ToString(CultureInfo.InvariantCulture)}-th candidate there" : " read ui.snapshot or try another side";
        switch (RelativeGeometry.Pick(BoxOf(anchor.NodeId, anchorNode), candidates, s, nth, sameClass))
        {
            case RelativeGeometry.Outcome.Pick pick:
            {
                if (FindByIdPreorder(top, pick.Box.Ref) is not { } node)
                {
                    return new Target.Bad(RelayError.NodeNotFound, $"the node on the {sideName} side is gone from the current tree");
                }
                if (BoxOf(pick.Box.Ref, node) != pick.Box)
                {
                    return new Target.Bad(RelayError.NodeNotFound,
                        $"the node at position {pick.Box.Ref.ToString(CultureInfo.InvariantCulture)} changed while it was being located: "
                        + "re-read with ui.snapshot before targeting it again");
                }
                return new Target.One(node, pick.Box.Ref, MatchMode.Exact);
            }
            case RelativeGeometry.Outcome.Ambiguous amb:
                return new Target.Many([.. amb.Boxes
                    .Select(b => FindByIdPreorder(top, b.Ref) is { } n ? new NodeMatch(new Positioned(n, 0), b.Ref, 0, MatchMode.Exact) : null)
                    .OfType<NodeMatch>()]);
            default:
                return new Target.Bad(RelayError.NodeNotFound, $"no node on the {sideName} of nodeId={anchorId}{scope}:{ordinal}");
        }
    }

    /// <summary>
    /// Upstream wraps a relative candidate in a fresh <c>AccessibilityNodeView(it)</c>, whose sibling index is 0
    /// whatever the node's real position: the ambiguity text says "siblingIndex 0" for those, and so does this.
    /// </summary>
    private sealed class Positioned(IA11yNode node, int siblingIndex) : INodeView
    {
        public IA11yNode Node { get; } = node;

        public string? Text => Node.Text;

        public string? ContentDescription => Node.ContentDescription;

        public string? ViewId => Node.ViewId;

        public string? ClassName => Node.ClassName;

        public string? PackageName => Node.PackageName;

        public int SiblingIndex { get; } = siblingIndex;

        public IReadOnlyList<INodeView> Children => Node.Children;
    }

    private static List<(int Id, IA11yNode Node)> BoxesPreorder(IA11yNode root)
    {
        var output = new List<(int, IA11yNode)>();
        var counter = 0;
        void Walk(IA11yNode node, int depth)
        {
            if (depth > NodeSelector.MaxDepth || counter >= NodeSelector.MaxNodes)
            {
                return;
            }
            output.Add((counter++, node));
            for (var i = 0; i < node.ChildCount; i++)
            {
                if (node.GetChild(i) is { } child)
                {
                    Walk(child, depth + 1);
                }
            }
        }
        Walk(root, 0);
        return output;
    }

    private RelativeGeometry.Box BoxOf(int id, IA11yNode node)
    {
        var raw = node.BoundsInScreen;
        var box = A11yRules.NormalizeBounds(raw, Screen) ?? raw;
        return new RelativeGeometry.Box(id, box.Left, box.Top, box.Right, box.Bottom, node.ClassName);
    }

    private string? StaleNodeIdWarning(JsonElement args, Target.One target) =>
        A11yRules.StaleNodeIdReason(
            askedByNodeId: args.TryGetProperty(NodeSelector.KeyNodeId, out _),
            visibleToUser: target.Node.IsVisibleToUser,
            hasOnScreenArea: A11yRules.NormalizeBounds(target.Node.BoundsInScreen, _tree.Screen) is not null,
            displayId: _tree.DisplayId);

    private static BackendResult.Failed Ambiguous(IReadOnlyList<NodeMatch> matches)
    {
        var preview = string.Join("; ", matches.Take(MaxCandidates).Select(match =>
        {
            var node = match.Node switch
            {
                Positioned p => p.Node,
                IA11yNode n => n,
                _ => null,
            };
            var label = node is null ? "" : (node.Text is { Length: > 0 } t ? t : node.ContentDescription is { Length: > 0 } d ? d : node.ClassName ?? "");
            return $"#{match.NodeId.ToString(CultureInfo.InvariantCulture)} {label} (siblingIndex {match.Node.SiblingIndex.ToString(CultureInfo.InvariantCulture)})";
        }));
        return new BackendResult.Failed(RelayError.NodeAmbiguous,
            $"selector matched {matches.Count.ToString(CultureInfo.InvariantCulture)} nodes: re-send with one of the candidate nodeIds "
            + "(every node capability accepts {\"nodeId\":N}), or add a distinguishing selector field. "
            + $"\"{NodeSelector.KeyIndex}\" only separates candidates that share a parent. "
            + $"candidates: {preview}");
    }

    /// <summary>Upstream <c>describe</c>: actionable flags come from the action list, never from the framework booleans.</summary>
    public JsonObject Describe(IA11yNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var o = new JsonObject
        {
            ["display"] = _tree.DisplayId,
            ["class"] = node.ClassName ?? "",
        };
        if (node.Text is { Length: > 0 } text)
        {
            o["text"] = text;
        }
        if (node.ContentDescription is { Length: > 0 } desc)
        {
            o["desc"] = desc;
        }
        if (node.ViewId is { Length: > 0 } id)
        {
            o["id"] = id;
        }
        if (node.PackageName is { } package)
        {
            o["package"] = package;
        }
        var actions = node.ActionIds;
        o["enabled"] = node.IsEnabled;
        o["checkable"] = node.IsCheckable;
        o["checked"] = node.IsChecked;
        o["clickable"] = actions.Contains(A11yConstants.ActionClick);
        o["longClickable"] = actions.Contains(A11yConstants.ActionLongClick);
        o["scrollable"] = actions.Contains(A11yConstants.ActionScrollForward) || actions.Contains(A11yConstants.ActionScrollBackward);
        o["selected"] = node.IsSelected;
        o["visible"] = node.IsVisibleToUser;
        o["focused"] = node.IsFocused;
        o["editable"] = node.IsEditable;
        o["actions"] = new JsonArray([.. actions.Select(a => (JsonNode)ActionName(a))]);
        A11ySnapshot.PutBounds(o, A11yRules.NormalizeBounds(node.BoundsInScreen, Screen));
        if (node.Range is { } range)
        {
            o["range"] = RangeData(range);
        }
        return o;
    }

    /// <summary>Upstream <c>rangeData</c>: floats are boxed into JSONObject, so they print as <c>Float.toString</c>.</summary>
    public static JsonObject RangeData(A11yRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        return new JsonObject
        {
            ["min"] = JavaFloat(range.Min),
            ["max"] = JavaFloat(range.Max),
            ["current"] = JavaFloat(range.Current),
            ["type"] = RangeTypeName(range.Type),
        };
    }

    /// <summary>
    /// A float as libcore's <c>JSONObject.numberToString(Float)</c> prints it, as a raw number node: integral values
    /// as the long, others as the shortest digits that read back as the same float (Float.toString's digits).
    /// </summary>
    public static JsonNode JavaFloat(float f) => JsonNode.Parse(f.ToString("R", CultureInfo.InvariantCulture))!;

    public static string RangeTypeName(int type) => type switch
    {
        A11yConstants.RangeTypeInt => "int",
        A11yConstants.RangeTypeFloat => "float",
        A11yConstants.RangeTypePercent => "percent",
        A11yConstants.RangeTypeIndeterminate => "indeterminate",
        _ => $"unknown({type.ToString(CultureInfo.InvariantCulture)})",
    };

    public static string ActionName(int action) => action switch
    {
        A11yConstants.ActionClick => "click",
        A11yConstants.ActionLongClick => "longClick",
        A11yConstants.ActionSelect => "select",
        A11yConstants.ActionDismiss => "dismiss",
        A11yConstants.ActionScrollForward => "scrollForward",
        A11yConstants.ActionScrollBackward => "scrollBackward",
        A11yConstants.ActionSetText => "setValue",
        A11yConstants.ActionSetProgress => "setProgress",
        A11yConstants.ActionImeEnter => "imeAction",
        _ => $"action({action.ToString(CultureInfo.InvariantCulture)})",
    };

    /// <summary>libcore <c>optBoolean</c>: a Boolean, or the strings "true"/"false" in any case; otherwise false.</summary>
    private static bool OptBoolean(JsonElement obj, string name) => Last(obj, name) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.String } s => string.Equals(s.GetString(), "true", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>The value libcore keeps for a repeated name (the last one).</summary>
    private static JsonElement? Last(JsonElement obj, string name)
    {
        JsonElement? found = null;
        foreach (var p in obj.EnumerateObject())
        {
            if (p.Name == name)
            {
                found = p.Value;
            }
        }
        return found;
    }
}
