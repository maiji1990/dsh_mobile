using System.Text.Json;
using DshMobile.Runtime.Automation.Relay.A11y;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// PR-07 step 5: the C# accessibility pure logic over tests/TestVectors/relay-a11y.json. The legacy Kotlin
/// LegacyRelayA11yParityTest drives the real upstream NodeSelector / RelativeGeometry / TrustedNodeTree /
/// staleNodeIdReason / A11yBackend argument checks over the same vectors.
/// </summary>
public sealed class RelayA11yParityTests
{
    private static readonly JsonElement V = Repo.Vectors("relay-a11y.json");

    private sealed class FakeNode(string key, string? text, string? desc, string? viewId, string? className, string? packageName, int siblingIndex)
        : INodeView
    {
        public readonly List<INodeView> Kids = [];

        public string Key { get; } = key;

        public string? Text { get; } = text;

        public string? ContentDescription { get; } = desc;

        public string? ViewId { get; } = viewId;

        public string? ClassName { get; } = className;

        public string? PackageName { get; } = packageName;

        public int SiblingIndex { get; } = siblingIndex;

        public IReadOnlyList<INodeView> Children => Kids;
    }

    private static string? Opt(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static FakeNode Build(JsonElement n, int siblingIndex)
    {
        var node = new FakeNode(n.GetProperty("k").GetString()!, Opt(n, "t"), Opt(n, "d"), Opt(n, "id"), Opt(n, "cls"), Opt(n, "pkg"), siblingIndex);
        var i = 0;
        foreach (var c in n.GetProperty("children").EnumerateArray())
        {
            node.Kids.Add(Build(c, i++));
        }
        return node;
    }

    private static readonly Lazy<Dictionary<string, FakeNode>> Trees = new(() =>
    {
        var trees = V.GetProperty("trees").EnumerateObject().ToDictionary(p => p.Name, p => Build(p.Value, 0));
        var gen = V.GetProperty("generatedTrees");
        FakeNode? deep = null;
        for (var i = gen.GetProperty("deep").GetInt32() - 1; i >= 0; i--)
        {
            var n = new FakeNode($"d{i}", $"d{i}", null, null, null, "com.app", 0);
            if (deep is not null)
            {
                n.Kids.Add(deep);
            }
            deep = n;
        }
        trees["deep"] = deep!;
        var wide = new FakeNode("wroot", null, null, null, null, "com.app", 0);
        for (var i = 0; i < gen.GetProperty("wide").GetInt32(); i++)
        {
            wide.Kids.Add(new FakeNode($"w{i}", $"w{i}", null, null, null, "com.app", i));
        }
        trees["wide"] = wide;
        return trees;
    });

    private static Selector Sel(JsonElement selectorObject)
    {
        var args = JsonDocument.Parse("{\"selector\":" + selectorObject.GetRawText() + "}").RootElement;
        return Assert.IsType<SelectorSpec.Parsed>(NodeSelector.Parse(args)).Selector;
    }

    [Fact]
    public void Parse_MatchesSharedVectors()
    {
        var n = 0;
        foreach (var c in V.GetProperty("parse").EnumerateArray())
        {
            var got = NodeSelector.Parse(c.GetProperty("args"));
            var want = c.GetProperty("expect");
            var where = c.GetProperty("args").GetRawText();
            switch (want.GetProperty("kind").GetString())
            {
                case "invalid":
                    Assert.True(got is SelectorSpec.Invalid i && i.Reason == want.GetProperty("reason").GetString(), $"{where}: {got}");
                    break;
                case "node":
                    Assert.True(got is SelectorSpec.ByNodeId b && b.NodeId == want.GetProperty("nodeId").GetInt32(), $"{where}: {got}");
                    break;
                default:
                    var s = want.GetProperty("selector");
                    int? index = s.TryGetProperty("index", out var ix) ? ix.GetInt32() : null;
                    var expected = new Selector(Opt(s, "text"), Opt(s, "desc"), Opt(s, "id"), Opt(s, "class"), index, Opt(s, "package"));
                    Assert.True(got is SelectorSpec.Parsed p && p.Selector == expected, $"{where}: {got}");
                    break;
            }
            n++;
        }
        Assert.True(n > 40);
    }

    [Fact]
    public void FindAndFindById_MatchSharedVectors()
    {
        foreach (var c in V.GetProperty("find").EnumerateArray())
        {
            var root = Trees.Value[c.GetProperty("tree").GetString()!];
            var limit = c.TryGetProperty("limit", out var l) ? l.GetInt32() : NodeSelector.DefaultMatchLimit;
            var hits = NodeSelector.FindAll(root, Sel(c.GetProperty("selector")), limit, c.GetProperty("allowContains").GetBoolean());
            var got = hits.Select(h => $"{h.NodeId}/{h.Depth}/{h.Mode.ToString().ToUpperInvariant()}");
            var want = c.GetProperty("expect").EnumerateArray().Select(e => $"{e[0].GetInt32()}/{e[1].GetInt32()}/{e[2].GetString()}");
            Assert.True(want.SequenceEqual(got), $"{c.GetRawText()}: [{string.Join(", ", got)}]");
        }
        foreach (var c in V.GetProperty("findById").EnumerateArray())
        {
            var node = NodeSelector.FindById(Trees.Value[c.GetProperty("tree").GetString()!], c.GetProperty("nodeId").GetInt32()) as FakeNode;
            Assert.True(Opt(c, "expect") == node?.Key, c.GetRawText());
        }
        Assert.Null(NodeSelector.FindById(null, 0));
        Assert.Empty(NodeSelector.FindAll(null, new Selector(Text: "x")));
        Assert.Empty(NodeSelector.FindAll(Trees.Value["main"], new Selector()));
    }

    private static RelativeGeometry.Box Box(JsonElement b) => new(b[0].GetInt32(), b[1].GetInt32(), b[2].GetInt32(), b[3].GetInt32(), b[4].GetInt32(),
        b[5].ValueKind == JsonValueKind.Null ? null : b[5].GetString());

    [Fact]
    public void Geometry_MatchesSharedVectors()
    {
        foreach (var c in V.GetProperty("geometry").EnumerateArray())
        {
            var side = RelativeGeometry.SideOf(c.GetProperty("side").GetString()!)!.Value;
            var outcome = RelativeGeometry.Pick(Box(c.GetProperty("anchor")), [.. c.GetProperty("candidates").EnumerateArray().Select(Box)], side,
                c.GetProperty("nth").GetInt32(), c.GetProperty("sameClass").GetBoolean());
            var want = c.GetProperty("expect");
            var ok = want.GetProperty("kind").GetString() switch
            {
                "pick" => outcome is RelativeGeometry.Outcome.Pick p && p.Box.Ref == want.GetProperty("ref").GetInt32() && p.GapPx == want.GetProperty("gap").GetInt32(),
                "none" => outcome is RelativeGeometry.Outcome.None,
                _ => outcome is RelativeGeometry.Outcome.Ambiguous a && a.GapPx == want.GetProperty("gap").GetInt32()
                    && a.Boxes.Select(b => b.Ref).SequenceEqual(want.GetProperty("refs").EnumerateArray().Select(r => r.GetInt32())),
            };
            Assert.True(ok, $"{c.GetRawText()}: {outcome}");
        }
        foreach (var c in V.GetProperty("sideOf").EnumerateArray())
        {
            var got = RelativeGeometry.SideOf(c[0].GetString()!);
            Assert.Equal(c[1].ValueKind == JsonValueKind.Null ? null : c[1].GetString(), got?.ToString().ToUpperInvariant());
        }
    }

    [Fact]
    public void TrustedTreeAndStaleReason_MatchSharedVectors()
    {
        var tree = new TrustedNodeTree();
        foreach (var s in V.GetProperty("trusted").EnumerateArray())
        {
            if (s.GetProperty("op").GetString() == "observe")
            {
                tree.Observe(s.GetProperty("displayId").GetInt32(), s.GetProperty("found").GetBoolean());
            }
            else
            {
                Assert.Equal(s.GetProperty("expect").GetString(), tree.State(s.GetProperty("alive").GetInt32()));
            }
        }
        foreach (var c in V.GetProperty("stale").EnumerateArray())
        {
            Assert.Equal(Opt(c, "expect"), A11yRules.StaleNodeIdReason(c.GetProperty("askedByNodeId").GetBoolean(), c.GetProperty("visible").GetBoolean(),
                c.GetProperty("area").GetBoolean(), c.GetProperty("displayId").GetInt32()));
        }
    }

    private static string[] Strings(JsonElement a) => [.. a.EnumerateArray().Select(e => e.GetString()!)];

    [Fact]
    public void ArgumentChecks_MatchSharedVectors()
    {
        foreach (var c in V.GetProperty("argsError").EnumerateArray())
        {
            Assert.True(Opt(c, "expect") == A11yRules.ArgsError(c.GetProperty("capability").GetString()!, c.GetProperty("args")), c.GetRawText());
        }
        foreach (var c in V.GetProperty("badArgShape").EnumerateArray())
        {
            Assert.True(Opt(c, "expect") == A11yRules.BadArgShape(c.GetProperty("args"), Strings(c.GetProperty("required")), Strings(c.GetProperty("allowed"))),
                c.GetRawText());
        }
        foreach (var c in V.GetProperty("badCoordinate").EnumerateArray())
        {
            var limits = c.GetProperty("limits").EnumerateArray().Select(l => (l[0].GetString()!, l[1].GetInt32())).ToList();
            var got = A11yRules.BadCoordinate(c.GetProperty("args"), limits);
            Assert.True(Opt(c, "expect") == got, $"{c.GetRawText()}: {got}");
        }
        static Bounds B(JsonElement a) => new(a[0].GetInt32(), a[1].GetInt32(), a[2].GetInt32(), a[3].GetInt32());
        foreach (var c in V.GetProperty("bounds").EnumerateArray())
        {
            var want = c.GetProperty("expect");
            Assert.Equal(want.ValueKind == JsonValueKind.Null ? null : B(want), A11yRules.NormalizeBounds(B(c.GetProperty("raw")), B(c.GetProperty("screen"))));
        }
    }
}
