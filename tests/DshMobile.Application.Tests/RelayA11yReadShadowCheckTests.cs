using System.Text.Json;
using DshMobile.Application.Shadow;
using DshMobile.Runtime.Automation.Relay;
using DshMobile.Runtime.Automation.Relay.A11y;
using DshMobile.Runtime.Automation.Relay.Exec;

namespace DshMobile.Application.Tests;

/// <summary>PR-07 segment 7: the accessibility read shadow against a fake Relay mailbox.</summary>
public sealed class RelayA11yReadShadowCheckTests : IDisposable
{
    private readonly string _pilot = Path.Combine(Path.GetTempPath(), "dshm-a11yread-" + Guid.NewGuid().ToString("N"), "pilot");

    private string Run => Path.Combine(_pilot, "run");

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_pilot)!;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class Node(string cls, Bounds b, string? text = null, bool clickable = false, params Node[] kids) : IA11yNode
    {
        public string? Text { get; } = text;

        public string? ContentDescription => null;

        public string? ViewId => null;

        public string? ClassName { get; } = cls;

        public string? PackageName => "com.example";

        public int SiblingIndex { get; set; }

        public IReadOnlyList<INodeView> Children => kids;

        public Bounds BoundsInScreen { get; } = b;

        public int ChildCount => kids.Length;

        public IA11yNode? GetChild(int index) => kids[index];

        public bool IsScrollable => false;

        public bool IsClickable { get; } = clickable;

        public bool IsEditable => false;

        public bool IsChecked => false;

        public bool IsCheckable => false;

        public bool IsEnabled => true;

        public bool IsSelected => false;

        public bool IsVisibleToUser => true;

        public bool IsFocused => false;

        public IReadOnlyList<int> ActionIds { get; } = clickable ? [A11yConstants.ActionClick] : [];

        public A11yRange? Range => null;
    }

    private static A11yTree Tree(string title)
    {
        var kids = new[]
        {
            new Node("android.widget.TextView", new Bounds(0, 0, 500, 100), title),
            new Node("android.widget.Button", new Bounds(0, 120, 500, 220), "Go", clickable: true),
            new Node("android.widget.TextView", new Bounds(0, 240, 500, 340), "Below"),
        };
        for (var i = 0; i < kids.Length; i++)
        {
            kids[i].SiblingIndex = i;
        }
        var root = new Node("android.widget.FrameLayout", new Bounds(0, 0, 1080, 2400), null, false, kids);
        return new A11yTree(0, new Bounds(0, 0, 1080, 2400), 480, () => root);
    }

    private void MakeMailbox()
    {
        Directory.CreateDirectory(Path.Combine(Run, "inbox"));
        Directory.CreateDirectory(Path.Combine(Run, "outbox"));
    }

    private static string Reply(string id, BackendResult result) => result switch
    {
        BackendResult.Ok ok => $$"""{"v":1,"id":"{{id}}","ok":true,"elapsedMs":7,"surface":"foreground","degraded":false,"data":{{RelayEncoding.EncodeData(ok.Data)}},"retryPolicy":"safe_retry","effectState":"verified"}""",
        BackendResult.Failed f => Error(id, f.Error.Code, f.Reason),
        _ => throw new InvalidOperationException(),
    };

    private static string Error(string id, string code, string? reason) =>
        $$"""{"v":1,"id":"{{id}}","ok":false,"elapsedMs":7,"reason":{{JsonSerializer.Serialize(reason)}},"error":{"code":"{{code}}","retryable":false,"exitCode":1}""" + "}";

    /// <summary>Claims requests and answers with <paramref name="answer"/>(id, capability, args) — null leaves it unanswered.</summary>
    private async Task FakeRelay(Func<string, string, JsonElement, string?> answer, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var f in Directory.GetFiles(Path.Combine(Run, "inbox"), "*.json"))
            {
                if (Path.GetFileName(f).StartsWith('.'))
                {
                    continue;
                }
                string text;
                try
                {
                    text = File.ReadAllText(f);
                    File.Delete(f);
                }
                catch (IOException)
                {
                    continue; // briefly locked (e.g. being scanned): picked up on the next poll
                }
                var request = JsonElement.Parse(text);
                var id = request.GetProperty("id").GetString()!;
                if (answer(id, request.GetProperty("capability").GetString()!, request.GetProperty("args")) is { } reply)
                {
                    var target = Path.Combine(Run, "outbox", id + ".json");
                    File.WriteAllText(target + ".tmp", reply);
                    File.Move(target + ".tmp", target, overwrite: true);
                }
            }
            try
            {
                await Task.Delay(20, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static BackendResult CSharp(A11yTree tree, string capability, JsonElement args) =>
        new RelayA11yReadShadowCheck.ReadCall(capability, args.GetRawText()).Answer(tree);

    private async Task<ShadowReport> Check(Func<A11yTree?> host, Func<string, string, JsonElement, string?>? answer, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource();
        var relay = answer is null ? Task.CompletedTask : Task.Run(() => FakeRelay(answer, cts.Token));
        try
        {
            return await RelayA11yReadShadowCheck.RunAsync(_pilot, host, TimeProvider.System, timeout, CancellationToken.None);
        }
        finally
        {
            await cts.CancelAsync();
            await relay;
        }
    }

    [Fact]
    public void PickCalls_CoversSnapshotIdsSelectorAndRelative()
    {
        var calls = RelayA11yReadShadowCheck.PickCalls(Tree("Title")).Select(c => c.Label).ToList();
        Assert.Equal(
        [
            "ui.snapshot",
            """ui.node {"nodeId":0}""",
            """ui.node {"nodeId":2}""",
            """ui.node {"nodeId":2,"relative":{"side":"below"}}""",
            """ui.node {"selector":{"text":"Title"}}""",
        ], calls);
    }

    [Fact]
    public async Task RelayAnsweringLikeCSharp_Agrees_AfterConsentWaits()
    {
        MakeMailbox();
        var tree = Tree("Title");
        var waits = new Dictionary<string, int>();
        var report = await Check(() => tree, (id, cap, args) =>
        {
            // The first two sends of each capability only say the user has not answered yet.
            waits[cap] = waits.GetValueOrDefault(cap) + 1;
            return waits[cap] <= 2 ? Error(id, RelayError.GateAwaitingConsent.Code, "awaiting") : Reply(id, CSharp(tree, cap, args));
        }, TimeSpan.FromSeconds(30));
        Assert.Equal(ShadowVerdict.Agree, report.Verdict);
        Assert.StartsWith("5/5 byte-identical", report.Detail, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.Combine(Run, "inbox")));
        Assert.Empty(Directory.GetFiles(Path.Combine(Run, "outbox")));
    }

    [Fact]
    public async Task OneByteOff_Diverges()
    {
        MakeMailbox();
        var tree = Tree("a/b");
        // A writer that does not escape "/" is exactly the kind of difference this must catch.
        var report = await Check(() => tree, (id, cap, args) => Reply(id, CSharp(tree, cap, args)).Replace("a\\/b", "a/b", StringComparison.Ordinal),
            TimeSpan.FromSeconds(20));
        Assert.Equal(ShadowVerdict.Diverge, report.Verdict);
        Assert.Contains("ui.snapshot: legacy ok:", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserDeclines_IsInconclusive_AndStops()
    {
        MakeMailbox();
        var tree = Tree("Title");
        var sent = 0;
        var report = await Check(() => tree, (id, _, _) =>
        {
            sent++;
            return Error(id, RelayError.GateHostDenied.Code, "denied");
        }, TimeSpan.FromSeconds(20));
        Assert.Equal(ShadowVerdict.Inconclusive, report.Verdict);
        Assert.Contains("legacy answered E_GATE_HOST_DENIED", report.Detail, StringComparison.Ordinal);
        Assert.Equal(1, sent);
    }

    [Fact]
    public async Task ScreenChangingUnderneath_IsInconclusive_NotADifference()
    {
        MakeMailbox();
        var n = 0;
        var report = await Check(() => Tree("Title " + n++), (id, cap, args) => Reply(id, CSharp(Tree("other"), cap, args)),
            TimeSpan.FromSeconds(20));
        Assert.Equal(ShadowVerdict.Inconclusive, report.Verdict);
        Assert.Contains("the screen kept changing", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoService_OrNoMailbox_IsNotRun()
    {
        var noMailbox = await Check(() => Tree("x"), null, TimeSpan.FromSeconds(1));
        Assert.Equal(ShadowVerdict.Inconclusive, noMailbox.Verdict);
        Assert.Contains("no Relay mailbox", noMailbox.Detail, StringComparison.Ordinal);
        MakeMailbox();
        var noService = await Check(() => null, null, TimeSpan.FromSeconds(1));
        Assert.Contains("accessibility service is not connected", noService.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void PickWaitForCalls_AllAnswerAtTheFirstPoll()
    {
        var calls = RelayA11yReadShadowCheck.PickWaitForCalls(Tree("Title")).Select(c => c.Args).ToList();
        Assert.Equal(
        [
            """{"nodeId":0,"timeoutMs":0}""",
            """{"selector":{"text":"dshm-shadow: no such text"},"absent":true,"timeoutMs":0}""",
            """{"selector":{"text":"dshm-shadow: no such text"},"timeoutMs":0}""",
            """{"nodeId":0,"relative":{"side":"above"},"absent":true,"timeoutMs":0}""",
            """{"nodeId":0,"timeoutMs":15001}""",
            """{"selector":{"text":"Title"}}""",
            """{"selector":{"text":"Title"},"absent":true,"timeoutMs":0}""",
            """{"nodeId":2,"text":"dshm-shadow: no such text","timeoutMs":0}""",
            """{"nodeId":2,"checked":true,"timeoutMs":0}""",
        ], calls);
        // Each is answered without a single poll delay on a still screen.
        foreach (var call in RelayA11yReadShadowCheck.PickWaitForCalls(Tree("Title")))
        {
            if (call.Answer(Tree("Title")) is BackendResult.Ok ok)
            {
                Assert.Equal(1, (int)ok.Data["polls"]!);
            }
        }
    }

    [Fact]
    public async Task WaitFor_LegacyWaitedLonger_StillAgrees_ButOtherPollsDiverge()
    {
        MakeMailbox();
        var tree = Tree("Title");
        var agree = await RunWaitFor(() => tree, (id, cap, args) =>
            Reply(id, CSharp(tree, cap, args)).Replace("\"waitedMs\":0", "\"waitedMs\":7", StringComparison.Ordinal));
        Assert.Equal(ShadowVerdict.Agree, agree.Verdict);
        Assert.StartsWith("9/9 byte-identical", agree.Detail, StringComparison.Ordinal);
        Assert.Contains("E_WAIT_TIMEOUT", agree.Detail, StringComparison.Ordinal);

        var differ = await RunWaitFor(() => tree, (id, cap, args) =>
            Reply(id, CSharp(tree, cap, args)).Replace("\"polls\":1", "\"polls\":2", StringComparison.Ordinal));
        Assert.Equal(ShadowVerdict.Diverge, differ.Verdict);
        Assert.Equal(RelayA11yReadShadowCheck.WaitForSlice, differ.Slice);
    }

    private async Task<ShadowReport> RunWaitFor(Func<A11yTree?> host, Func<string, string, JsonElement, string?> answer)
    {
        using var cts = new CancellationTokenSource();
        var relay = Task.Run(() => FakeRelay(answer, cts.Token));
        try
        {
            return await RelayA11yReadShadowCheck.RunWaitForAsync(_pilot, host, TimeProvider.System, TimeSpan.FromSeconds(30), CancellationToken.None);
        }
        finally
        {
            await cts.CancelAsync();
            await relay;
        }
    }

    [Fact]
    public async Task ServiceGoneAroundOneCall_IsRetried_NotADifference()
    {
        // Seen on a device: `uiautomator dump` unbinds every accessibility service for ~2 s. Both sides then lack a
        // tree (legacy answers E_BACKEND_UNAVAILABLE, C# reads null): that must be retried, never called a difference.
        MakeMailbox();
        var tree = Tree("Title");
        var gone = false;
        var outages = 0;
        var report = await Check(() => gone ? null : tree, (id, cap, args) =>
        {
            if (cap == "ui.node" && outages == 0 && args.TryGetProperty("nodeId", out var n) && n.GetInt32() == 2)
            {
                outages++;
                gone = true; // the C# read after this call sees no service either
                return Error(id, RelayError.BackendUnavailable.Code, "no backend reachable for the foreground surface");
            }
            gone = false;
            return Reply(id, CSharp(tree, cap, args));
        }, TimeSpan.FromSeconds(30));
        Assert.Equal(ShadowVerdict.Agree, report.Verdict);
        Assert.Contains("""ui.node {"nodeId":2}: equal""", report.Detail, StringComparison.Ordinal);
        Assert.Contains("(attempt ", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServiceGoneForEveryAttempt_IsInconclusive()
    {
        MakeMailbox();
        var tree = Tree("Title");
        var calls = 0;
        var report = await Check(() => calls > 0 ? null : tree, (id, _, _) =>
        {
            calls++;
            return Error(id, RelayError.BackendUnavailable.Code, "no backend reachable for the foreground surface");
        }, TimeSpan.FromSeconds(30));
        Assert.Equal(ShadowVerdict.Inconclusive, report.Verdict);
        Assert.Contains("the accessibility service was not connected around the call", report.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("differ", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverlappingRun_IsRefused()
    {
        MakeMailbox();
        var tree = Tree("Title");
        // The first run blocks on an unanswered call; the second must not start.
        var first = RelayA11yReadShadowCheck.RunAsync(_pilot, () => tree, TimeProvider.System, TimeSpan.FromSeconds(3), CancellationToken.None);
        await Task.Delay(300);
        var second = await RelayA11yReadShadowCheck.RunAsync(_pilot, () => tree, TimeProvider.System, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Contains("another accessibility read check is still running", second.Detail, StringComparison.Ordinal);
        var firstReport = await first;
        Assert.Contains("no final answer", firstReport.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReaderFault_EndsTheCheckWithAReport_NotAHang()
    {
        MakeMailbox();
        var report = await Check(() => throw new InvalidCastException("boom"), null, TimeSpan.FromSeconds(1));
        Assert.Equal(ShadowVerdict.Inconclusive, report.Verdict);
        Assert.Contains("the C# reader failed: InvalidCastException: boom", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoAnswer_TakesTheRequestBack()
    {
        MakeMailbox();
        var report = await Check(() => Tree("Title"), (_, _, _) => null, TimeSpan.FromSeconds(1));
        Assert.Equal(ShadowVerdict.Inconclusive, report.Verdict);
        Assert.Contains("no final answer", report.Detail, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.Combine(Run, "inbox")));
    }
}
