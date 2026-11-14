using System.Globalization;
using System.Text.Json;
using DshMobile.Runtime.Automation.Relay;
using DshMobile.Runtime.Automation.Relay.A11y;
using DshMobile.Runtime.Automation.Relay.Exec;
using DshMobile.Runtime.Automation.Relay.Interlock;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// PR-07: the node-level write path of the accessibility backend over tests/TestVectors/relay-a11y-write.json. The
/// world model is the vector file's (fresh copy per root read, "accepts" / "on" ops with lagMs, a clock that only
/// moves in the delays the code asks for); expectations come from an independent reading of upstream NodeActions.
/// </summary>
public sealed class RelayA11yWriteParityTests
{
    private static readonly JsonElement V = Repo.Vectors("relay-a11y-write.json");

    private static readonly Dictionary<int, string> Names = new()
    {
        [A11yConstants.ActionClick] = "click", [A11yConstants.ActionLongClick] = "longClick", [A11yConstants.ActionSelect] = "select",
        [A11yConstants.ActionDismiss] = "dismiss", [A11yConstants.ActionScrollForward] = "scrollForward",
        [A11yConstants.ActionScrollBackward] = "scrollBackward", [A11yConstants.ActionSetText] = "setValue",
        [A11yConstants.ActionSetProgress] = "setProgress", [A11yConstants.ActionImeEnter] = "imeAction",
    };

    /// <summary>The mutable screen; every root read hands out an immutable copy (like AccessibilityNodeInfo).</summary>
    internal sealed class World
    {
        private readonly List<(long When, int Seq, Action Run)> _pending = [];
        private int _seq;

        public World(JsonElement? tree, long? hostAt, string self)
        {
            Tree = tree is { } t ? new Live(t) : null;
            HostAt = hostAt;
            Self = self;
        }

        public Live? Tree { get; private set; }

        public long T { get; set; }

        public long? HostAt { get; }

        public string Self { get; }

        public List<string> Dispatches { get; } = [];

        public Snap? Root()
        {
            var due = _pending.Where(p => p.When <= T).OrderBy(p => p.When).ThenBy(p => p.Seq).ToList();
            _pending.RemoveAll(p => p.When <= T);
            foreach (var p in due)
            {
                p.Run();
            }
            if (Tree is null)
            {
                return null;
            }
            var root = new Snap(this, Tree, [], 0);
            if (HostAt is { } h && T >= h)
            {
                root.PackageName = Self;
            }
            return root;
        }

        public Live? NodeAt(IReadOnlyList<int> path)
        {
            var n = Tree;
            foreach (var i in path)
            {
                if (n is null || i >= n.Kids.Count)
                {
                    return null;
                }
                n = n.Kids[i];
            }
            return n;
        }

        public bool Perform(Snap snap, int action, A11yActionArgs? args)
        {
            Dispatches.Add(Names.TryGetValue(action, out var name) ? name : action.ToString(CultureInfo.InvariantCulture));
            if (NodeAt(snap.Path) is not { } live || !live.Accepts.Contains(action))
            {
                return false;
            }
            if (live.On.TryGetValue(action, out var ops))
            {
                foreach (var op in ops)
                {
                    Schedule(snap.Path, op, args);
                }
            }
            return true;
        }

        private void Schedule(int[] path, JsonElement op, A11yActionArgs? args)
        {
            _seq++;
            var when = T + (op.TryGetProperty("lagMs", out var lag) ? lag.GetInt64() : 0);
            _pending.Add((when, _seq, () =>
            {
                if (NodeAt(path) is not { } n)
                {
                    return;
                }
                switch (op.GetProperty("op").GetString())
                {
                    case "rangeAdd":
                    {
                        var by = (float)op.GetProperty("by").GetDouble();
                        var r = n.Range!;
                        var cur = Math.Min(Math.Max(r.Current + by, r.Min), r.Max);
                        n.Range = r with { Current = cur };
                        if (op.TryGetProperty("dropAtEnd", out var drop) && drop.GetBoolean() && (cur >= r.Max || cur <= r.Min))
                        {
                            n.Actions.Remove(by > 0 ? A11yConstants.ActionScrollForward : A11yConstants.ActionScrollBackward);
                        }
                        break;
                    }
                    case "rangeSet":
                    {
                        var step = op.GetProperty("step").GetDouble();
                        var r = n.Range!;
                        var v = (float)(Math.Round(args!.ProgressValue!.Value / step) * step);
                        n.Range = r with { Current = Math.Min(Math.Max(v, r.Min), r.Max) };
                        break;
                    }
                    case "setText":
                    {
                        var text = args!.SetText!;
                        n.Text = op.TryGetProperty("maxLen", out var max) && text.Length > max.GetInt32() ? text[..max.GetInt32()] : text;
                        break;
                    }
                    case "toggle":
                        if (!n.Flags.Remove("checked"))
                        {
                            n.Flags.Add("checked");
                        }
                        break;
                    case "setId":
                        n.Id = op.GetProperty("id").GetString();
                        break;
                    case "remove":
                        NodeAt(path[..^1])!.Kids.RemoveAt(path[^1]);
                        break;
                    default:
                        throw new InvalidOperationException(op.ToString());
                }
            }));
        }
    }

    internal sealed class Live
    {
        public Live(JsonElement n)
        {
            Cls = Str(n, "cls");
            Text = Str(n, "text");
            Desc = Str(n, "desc");
            Id = Str(n, "id");
            Pkg = Str(n, "pkg");
            var b = n.GetProperty("b");
            Bounds = new Bounds(b[0].GetInt32(), b[1].GetInt32(), b[2].GetInt32(), b[3].GetInt32());
            Flags = [.. n.GetProperty("flags").EnumerateArray().Select(f => f.GetString()!)];
            Actions = [.. n.GetProperty("actions").EnumerateArray().Select(a => a.GetInt32())];
            Accepts = [.. n.GetProperty("accepts").EnumerateArray().Select(a => a.GetInt32())];
            if (n.TryGetProperty("on", out var on))
            {
                foreach (var p in on.EnumerateObject())
                {
                    On[int.Parse(p.Name, CultureInfo.InvariantCulture)] = [.. p.Value.EnumerateArray()];
                }
            }
            if (n.TryGetProperty("range", out var r))
            {
                Range = new A11yRange(r.GetProperty("type").GetInt32(), (float)r.GetProperty("min").GetDouble(),
                    (float)r.GetProperty("max").GetDouble(), (float)r.GetProperty("current").GetDouble());
            }
            Kids = [.. n.GetProperty("kids").EnumerateArray().Select(k => k.ValueKind == JsonValueKind.Null ? null : new Live(k))];
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public string? Cls { get; }

        public string? Text { get; set; }

        public string? Desc { get; }

        public string? Id { get; set; }

        public string? Pkg { get; }

        public Bounds Bounds { get; }

        public HashSet<string> Flags { get; }

        public List<int> Actions { get; }

        public HashSet<int> Accepts { get; }

        public Dictionary<int, List<JsonElement>> On { get; } = [];

        public A11yRange? Range { get; set; }

        public List<Live?> Kids { get; }
    }

    internal sealed class Snap : IA11yNode
    {
        private readonly World _world;
        private readonly List<Snap?> _kids;

        public Snap(World world, Live n, int[] path, int siblingIndex)
        {
            _world = world;
            Path = path;
            SiblingIndex = siblingIndex;
            ClassName = n.Cls;
            Text = n.Text;
            ContentDescription = n.Desc;
            ViewId = n.Id;
            PackageName = n.Pkg;
            BoundsInScreen = n.Bounds;
            IsScrollable = n.Flags.Contains("scrollable");
            IsClickable = n.Flags.Contains("clickable");
            IsEditable = n.Flags.Contains("editable");
            IsChecked = n.Flags.Contains("checked");
            IsCheckable = n.Flags.Contains("checkable");
            IsEnabled = n.Flags.Contains("enabled");
            IsSelected = n.Flags.Contains("selected");
            IsVisibleToUser = n.Flags.Contains("visible");
            IsFocused = n.Flags.Contains("focused");
            ActionIds = [.. n.Actions];
            Range = n.Range;
            _kids = [.. n.Kids.Select((k, i) => k is null ? null : new Snap(world, k, [.. path, i], i))];
        }

        public int[] Path { get; }

        public string? Text { get; }

        public string? ContentDescription { get; }

        public string? ViewId { get; }

        public string? ClassName { get; }

        public string? PackageName { get; set; }

        public int SiblingIndex { get; }

        public IReadOnlyList<INodeView> Children => [.. _kids.OfType<Snap>()];

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

        public bool PerformAction(int action, A11yActionArgs? args) => _world.Perform(this, action, args);
    }

    private static async Task<BackendResult> RunAsync(NodeReader reader, string capability, JsonElement args, A11yWriteEnv env) => capability switch
    {
        "ui.click" => await reader.ClickAsync(args, env),
        "ui.longClick" => reader.LongClick(args, env),
        "ui.select" => reader.Select(args, env),
        "ui.dismiss" => reader.Dismiss(args, env),
        "ui.scroll" => await reader.ScrollAsync(args, env, CancellationToken.None),
        "ui.setValue" => reader.SetValue(args, env),
        "ui.setProgress" => await reader.SetProgressAsync(args, env, CancellationToken.None),
        "ui.imeAction" => reader.ImeAction(args, env),
        _ => throw new InvalidOperationException(capability),
    };

    [Fact]
    public async Task EveryWriteCase_MatchesTheVectorTextByteForByte_WithTheSameClockAndDispatches()
    {
        var self = V.GetProperty("self").GetString()!;
        var s = V.GetProperty("frame").GetProperty("screen");
        var screen = new Bounds(s[0].GetInt32(), s[1].GetInt32(), s[2].GetInt32(), s[3].GetInt32());
        var n = 0;
        foreach (var c in V.GetProperty("cases").EnumerateArray())
        {
            var hostAt = c.TryGetProperty("hostAtMs", out var h) ? h.GetInt64() : (long?)null;
            var world = new World(c.GetProperty("tree").ValueKind == JsonValueKind.Null ? null : V.GetProperty("tree"), hostAt, self);
            var tree = new A11yTree(0, screen, 480, world.Root);
            var env = new A11yWriteEnv(c.GetProperty("budgetMs").GetInt64(), self,
                node => ConsentSurfaces.LooksLikeConsentControl(node.PackageName, node.ViewId),
                () => world.T, (ms, _) =>
                {
                    world.T += ms;
                    return Task.CompletedTask;
                }, c.GetProperty("sdk").GetInt32());
            var capability = c.GetProperty("capability").GetString()!;
            var args = JsonDocument.Parse(c.GetProperty("args").GetString()!).RootElement;
            var label = $"{capability} {c.GetProperty("args").GetString()} budget {c.GetProperty("budgetMs")}";
            var result = await RunAsync(new NodeReader(tree), capability, args, env);
            var expect = c.GetProperty("expect");
            if (expect.GetProperty("ok").GetBoolean())
            {
                var ok = Assert.IsType<BackendResult.Ok>(result);
                Assert.True(expect.GetProperty("data").GetString() == RelayEncoding.EncodeData(ok.Data),
                    $"{label}\n want {expect.GetProperty("data").GetString()}\n got  {RelayEncoding.EncodeData(ok.Data)}");
            }
            else
            {
                var failed = Assert.IsType<BackendResult.Failed>(result);
                Assert.True(expect.GetProperty("code").GetString() == failed.Error.Code, $"{label}: {failed.Error.Code} {failed.Reason}");
                Assert.True(expect.GetProperty("reason").GetString() == failed.Reason, $"{label}\n want {expect.GetProperty("reason")}\n got  {failed.Reason}");
            }
            Assert.True(c.GetProperty("endMs").GetInt64() == world.T, $"{label}: clock {world.T}");
            Assert.Equal(c.GetProperty("dispatches").EnumerateArray().Select(d => d.ToString()), world.Dispatches);
            n++;
        }
        Assert.Equal(62, n);
    }

    [Fact]
    public void FloatToString_MatchesJava()
    {
        foreach (var c in V.GetProperty("javaFloat").EnumerateArray())
        {
            Assert.Equal(c.GetProperty("java").GetString(), OrgJson.JavaFloat((float)c.GetProperty("f").GetDouble()));
        }
    }

    [Fact]
    public async Task Cancellation_StopsAScrollBetweenSteps()
    {
        var world = new World(V.GetProperty("tree"), null, "org.example.dshmobile");
        var tree = new A11yTree(0, new Bounds(0, 0, 1080, 2400), 480, world.Root);
        using var cts = new CancellationTokenSource();
        var env = new A11yWriteEnv(15000, "org.example.dshmobile", _ => false, () => world.T, (ms, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }, 37);
        var args = JsonDocument.Parse("""{"nodeId":11,"direction":"forward","times":3}""").RootElement;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NodeReader(tree).ScrollAsync(args, env, cts.Token));
        Assert.Single(world.Dispatches);
    }
}
