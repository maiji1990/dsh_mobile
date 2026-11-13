using System.Text.Json;
using DshMobile.Runtime.Automation.Relay;
using DshMobile.Runtime.Automation.Relay.A11y;
using DshMobile.Runtime.Automation.Relay.Exec;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// PR-07 segment 7: the accessibility read path (ui.snapshot / ui.node) over tests/TestVectors/relay-a11y-read.json.
/// The legacy JVM tests cannot build AccessibilityNodeInfo, so these cases are C#-only; the live legacy Relay is
/// compared byte for byte on the device (automation.relay-a11y-read).
/// </summary>
public sealed class RelayA11yReadParityTests
{
    private static readonly JsonElement V = Repo.Vectors("relay-a11y-read.json");

    internal sealed class FakeNode : IA11yNode
    {
        private readonly List<FakeNode?> _kids = [];

        /// <summary>One link of the generated "deep" chain (too deep for a JSON fixture).</summary>
        public FakeNode(string? text, FakeNode? child)
        {
            ClassName = "L";
            Text = text;
            PackageName = "com.example";
            BoundsInScreen = new Bounds(0, 0, 10, 10);
            ActionIds = [];
            if (child is not null)
            {
                _kids.Add(child);
            }
        }

        public FakeNode(JsonElement n, int siblingIndex)
        {
            SiblingIndex = siblingIndex;
            ClassName = Str(n, "cls");
            Text = Str(n, "text");
            ContentDescription = Str(n, "desc");
            ViewId = Str(n, "id");
            PackageName = Str(n, "pkg");
            var b = n.GetProperty("b");
            BoundsInScreen = new Bounds(b[0].GetInt32(), b[1].GetInt32(), b[2].GetInt32(), b[3].GetInt32());
            var flags = n.GetProperty("flags").EnumerateArray().Select(f => f.GetString()).ToHashSet();
            IsScrollable = flags.Contains("scrollable");
            IsClickable = flags.Contains("clickable");
            IsEditable = flags.Contains("editable");
            IsChecked = flags.Contains("checked");
            IsCheckable = flags.Contains("checkable");
            IsEnabled = flags.Contains("enabled");
            IsSelected = flags.Contains("selected");
            IsVisibleToUser = flags.Contains("visible");
            IsFocused = flags.Contains("focused");
            ActionIds = [.. n.GetProperty("actions").EnumerateArray().Select(a => a.GetInt32())];
            if (n.TryGetProperty("range", out var r))
            {
                Range = new A11yRange(r.GetProperty("type").GetInt32(), (float)r.GetProperty("min").GetDouble(),
                    (float)r.GetProperty("max").GetDouble(), (float)r.GetProperty("current").GetDouble());
            }
            var i = 0;
            foreach (var k in n.GetProperty("kids").EnumerateArray())
            {
                _kids.Add(k.ValueKind == JsonValueKind.Null ? null : new FakeNode(k, i));
                i++;
            }
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public string? Text { get; }

        public string? ContentDescription { get; }

        public string? ViewId { get; }

        public string? ClassName { get; }

        public string? PackageName { get; }

        public int SiblingIndex { get; }

        public IReadOnlyList<INodeView> Children => [.. _kids.OfType<FakeNode>()];

        public Bounds BoundsInScreen { get; }

        public int ChildCount => _kids.Count;

        public IA11yNode? GetChild(int index) => _kids[index];

        public bool IsScrollable { get; }

        public bool IsClickable { get; }

        public bool IsEditable { get; }

        public bool IsChecked { get; }

        public bool IsCheckable { get; }

        public bool IsEnabled { get; }

        public bool IsSelected { get; }

        public bool IsVisibleToUser { get; }

        public bool IsFocused { get; }

        public IReadOnlyList<int> ActionIds { get; }

        public A11yRange? Range { get; }
    }

    private static A11yTree TreeOf(JsonElement c)
    {
        var name = c.GetProperty("tree");
        FakeNode? root = null;
        if (name.GetString() == "deep")
        {
            for (var i = V.GetProperty("generatedTrees").GetProperty("deep").GetProperty("length").GetInt32() - 1; i >= 0; i--)
            {
                root = new FakeNode(i == 0 ? null : i.ToString(System.Globalization.CultureInfo.InvariantCulture), root);
            }
        }
        else if (name.ValueKind != JsonValueKind.Null)
        {
            root = new FakeNode(V.GetProperty("trees").GetProperty(name.GetString()!), 0);
        }
        var s = c.GetProperty("screen");
        return new A11yTree(c.GetProperty("display").GetInt32(), new Bounds(s[0].GetInt32(), s[1].GetInt32(), s[2].GetInt32(), s[3].GetInt32()),
            c.GetProperty("dpi").GetInt32(), () => root);
    }

    private static void AssertResult(JsonElement expect, BackendResult actual, string label)
    {
        if (expect.GetProperty("ok").GetBoolean())
        {
            var ok = Assert.IsType<BackendResult.Ok>(actual);
            Assert.True(expect.GetProperty("data").GetString() == RelayEncoding.EncodeData(ok.Data),
                $"{label}\n want {expect.GetProperty("data").GetString()}\n got  {RelayEncoding.EncodeData(ok.Data)}");
        }
        else
        {
            var failed = Assert.IsType<BackendResult.Failed>(actual);
            Assert.Equal(expect.GetProperty("code").GetString(), failed.Error.Code);
            Assert.Equal(expect.GetProperty("reason").GetString(), failed.Reason);
        }
    }

    [Fact]
    public void Snapshot_MatchesTheVectorTextByteForByte()
    {
        var n = 0;
        foreach (var c in V.GetProperty("snapshot").EnumerateArray())
        {
            AssertResult(c.GetProperty("expect"), A11ySnapshot.Snapshot(TreeOf(c)), $"snapshot {c.GetProperty("tree")}");
            n++;
        }
        Assert.Equal(5, n);
    }

    [Fact]
    public void ReadNode_MatchesTheVectorTextByteForByte()
    {
        var n = 0;
        foreach (var c in V.GetProperty("node").EnumerateArray())
        {
            var args = JsonDocument.Parse(c.GetProperty("args").GetString()!).RootElement;
            AssertResult(c.GetProperty("expect"), new NodeReader(TreeOf(c)).ReadNode(args), $"node {c.GetProperty("tree")} {c.GetProperty("args")}");
            n++;
        }
        Assert.Equal(29, n);
    }

    [Fact]
    public void HostWindowRule_SuspendsOnlyWritesOnTheHostDisplay()
    {
        var self = V.GetProperty("self").GetString()!;
        var n = 0;
        foreach (var c in V.GetProperty("hostWindow").EnumerateArray())
        {
            var surface = RelaySurfaces.FromWire(c.GetProperty("surface").GetString())!.Value;
            var windows = c.GetProperty("windows").EnumerateArray().Select(w => w.ValueKind == JsonValueKind.Null ? null : w.GetString()).ToList();
            Assert.True(c.GetProperty("expect").GetBoolean() == HostWindowRule.Suspends(c.GetProperty("capability").GetString()!, surface, windows, self),
                $"{c.GetProperty("capability")} on {c.GetProperty("surface")}");
            n++;
        }
        Assert.Equal(11, n);
        Assert.Equal("E_TASK_SUSPENDED_BY_HOST", HostWindowRule.SuspendedByHost().Error.Code);
    }

    [Fact]
    public void RangeFloats_PrintLikeFloatToString()
    {
        foreach (var c in V.GetProperty("javaFloat").EnumerateArray())
        {
            var data = new System.Text.Json.Nodes.JsonObject { ["v"] = NodeReader.JavaFloat((float)c.GetProperty("f").GetDouble()) };
            Assert.Equal($"{{\"v\":{c.GetProperty("java").GetString()}}}", RelayEncoding.EncodeData(data));
        }
    }

    [Fact]
    public void OwnerOf_ReadsPackageAndViewIdAtThePreorderId()
    {
        var c = V.GetProperty("snapshot")[0];
        var reader = new NodeReader(TreeOf(c));
        Assert.Equal(new TargetOwner("com.example", "com.example:id/ok"), reader.OwnerOf(2));
        Assert.Null(reader.OwnerOf(12));
        Assert.Null(reader.OwnerOf(-1));
    }
}
