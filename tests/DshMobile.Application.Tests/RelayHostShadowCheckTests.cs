using System.Text.Json;
using DshMobile.Application.Shadow;

namespace DshMobile.Application.Tests;

/// <summary>PR-07 steps 1–4: the dual-mailbox relay host shadow against two fake mailboxes.</summary>
public sealed class RelayHostShadowCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dshm-hostshadow-" + Guid.NewGuid().ToString("N"));

    private string Legacy => Path.Combine(_root, "pilot");

    private string Cs => Path.Combine(_root, "pilot-cs");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void MakeMailbox(string entry)
    {
        Directory.CreateDirectory(Path.Combine(entry, "run", "inbox"));
        Directory.CreateDirectory(Path.Combine(entry, "run", "outbox"));
    }

    /// <summary>A fake relay: answers each request in its inbox with <paramref name="answer"/>(capability, args, id).</summary>
    private static async Task ServeAsync(string entry, Func<string, JsonElement, string, string> answer, CancellationToken ct)
    {
        var inbox = Path.Combine(entry, "run", "inbox");
        var outbox = Path.Combine(entry, "run", "outbox");
        while (!ct.IsCancellationRequested)
        {
            foreach (var file in Directory.GetFiles(inbox, "*.json"))
            {
                string text;
                try
                {
                    text = await File.ReadAllTextAsync(file, ct);
                    File.Delete(file);
                }
                catch (IOException)
                {
                    continue;
                }
                using var doc = JsonDocument.Parse(text);
                var id = doc.RootElement.GetProperty("id").GetString()!;
                var reply = answer(doc.RootElement.GetProperty("capability").GetString()!, doc.RootElement.GetProperty("args"), id);
                var tmp = Path.Combine(outbox, "." + id + ".tmp");
                await File.WriteAllTextAsync(tmp, reply, ct);
                File.Move(tmp, Path.Combine(outbox, id + ".json"));
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

    private static string Ok(string id, string data, int elapsed = 3) => $$"""{"v":1,"id":"{{id}}","ok":true,"data":{{data}},"elapsedMs":{{elapsed}}}""";

    private static string Err(string id, string code) => $$"""{"v":1,"id":"{{id}}","ok":false,"error":{"code":"{{code}}"},"reason":"r","elapsedMs":1}""";

    /// <summary>The same answer from both sides, but for the volatile fields and the guest path.</summary>
    private static Func<string, JsonElement, string, string> Relay(string guestRoot, int elapsed, Func<string, string?>? overrideData = null) =>
        (cap, args, id) => overrideData?.Invoke(cap) is { } d ? Ok(id, d, elapsed) : cap switch
        {
            "ui.node" => Ok(id, """{"nodeId":4,"text":"dshm-tap","left":100,"top":200,"right":300,"bottom":260}""", elapsed),
            "screen.capture" => Ok(id, $$"""{"path":"{{guestRoot}}/out/shot/s-{{elapsed}}.png","width":1080,"height":2400,"bytes":{{1000 + elapsed}}}""", elapsed),
            "contact.read" => Err(id, "E_GATE_SYSTEM_MISSING"),
            "ui.tap" => Ok(id, $$"""{"x":{{args.GetProperty("x").GetInt32()}},"y":{{args.GetProperty("y").GetInt32()}}}""", elapsed),
            _ => Ok(id, $$"""{"capability":"{{cap}}","waitedMs":{{elapsed}}}""", elapsed),
        };

    private async Task<ShadowReport> RunAsync(Func<string, JsonElement, string, string> legacy, Func<string, JsonElement, string, string> cs)
    {
        MakeMailbox(Legacy);
        MakeMailbox(Cs);
        using var stop = new CancellationTokenSource();
        var servers = Task.WhenAll(ServeAsync(Legacy, legacy, stop.Token), ServeAsync(Cs, cs, stop.Token));
        try
        {
            return await RelayHostShadowCheck.RunAsync(Legacy, Cs, TimeProvider.System, TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(5),
                CancellationToken.None);
        }
        finally
        {
            await stop.CancelAsync();
            await servers;
        }
    }

    [Fact]
    public async Task Agrees_when_only_volatile_fields_and_guest_path_differ()
    {
        var report = await RunAsync(Relay("/opt/pilot", 3), Relay("/opt/pilot-cs", 9));

        Assert.Equal(ShadowVerdict.Agree, report.Verdict);
        Assert.Equal(RelayHostShadowCheck.Slice, report.Slice);
        Assert.Contains($"{RelayHostShadowCheck.Calls.Count}/{RelayHostShadowCheck.Calls.Count} equal", report.Detail, StringComparison.Ordinal);
        // ui.tap took its point from the ui.node box: the centre (200, 230) went to both sides.
        Assert.Contains("ui.tap: equal", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_a_difference_with_an_excerpt()
    {
        var report = await RunAsync(Relay("/opt/pilot", 3), Relay("/opt/pilot-cs", 3, cap => cap == "ui.click" ? """{"clicked":false}""" : null));

        Assert.Equal(ShadowVerdict.Diverge, report.Verdict);
        Assert.Contains("ui.click", report.Detail, StringComparison.Ordinal);
        Assert.Contains("\"clicked\":false", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Private_differences_show_only_hashes()
    {
        var report = await RunAsync(Relay("/opt/pilot", 3, cap => cap == "cal.read" ? """{"events":[{"title":"secret dentist"}]}""" : null),
            Relay("/opt/pilot-cs", 3, cap => cap == "cal.read" ? """{"events":[]}""" : null));

        Assert.Equal(ShadowVerdict.Diverge, report.Verdict);
        Assert.Contains("cal.read", report.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("dentist", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Waits_out_awaiting_consent_by_resending()
    {
        var asked = 0;
        var legacy = Relay("/opt/pilot", 3);
        Func<string, JsonElement, string, string> cs = (cap, args, id) =>
            cap == "app.launch" && Interlocked.Increment(ref asked) == 1 ? Err(id, "E_AWAITING_CONSENT") : Relay("/opt/pilot-cs", 3)(cap, args, id);

        var report = await RunAsync(legacy, cs);

        Assert.True(report.Verdict == ShadowVerdict.Agree, report.Detail);
        Assert.Equal(2, asked);
    }

    [Fact]
    public async Task Each_side_resets_the_target_right_before_a_state_dependent_call()
    {
        var legacySeen = new List<string>();
        var csSeen = new List<string>();
        Func<string, JsonElement, string, string> Recording(List<string> seen, string root) => (cap, args, id) =>
        {
            lock (seen)
            {
                seen.Add(cap + " " + args.GetRawText());
            }
            return Relay(root, 3)(cap, args, id);
        };

        var report = await RunAsync(Recording(legacySeen, "/opt/pilot"), Recording(csSeen, "/opt/pilot-cs"));

        Assert.True(report.Verdict == ShadowVerdict.Agree, report.Detail);
        foreach (var seen in new[] { legacySeen, csSeen })
        {
            foreach (var fresh in RelayHostShadowCheck.Calls.Where(c => c.Fresh))
            {
                var at = seen.FindIndex(s => s.StartsWith(fresh.Capability + " ", StringComparison.Ordinal));
                Assert.True(at > 0, fresh.Capability);
                Assert.Equal("ui.click " + RelayHostShadowCheck.ResetArgs, seen[at - 1]);
            }
        }
    }

    [Fact]
    public async Task Inconclusive_without_the_csharp_mailbox()
    {
        MakeMailbox(Legacy);

        var report = await RelayHostShadowCheck.RunAsync(Legacy, Cs, TimeProvider.System, TimeSpan.FromSeconds(5), null, CancellationToken.None);

        Assert.Equal(ShadowVerdict.Inconclusive, report.Verdict);
        Assert.Contains("C# Relay mailbox", report.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"v":1,"id":"a","ok":true,"data":{"x":1,"waitedMs":5},"elapsedMs":3}""", """{"v":1,"id":"b","ok":true,"data":{"x":1,"waitedMs":9},"elapsedMs":7}""", true)]
    [InlineData("""{"ok":true,"data":{"path":"/opt/pilot/out/a.png"}}""", """{"ok":true,"data":{"path":"/opt/pilot-cs/out/a.png"}}""", true)]
    [InlineData("""{"ok":true,"data":{"x":1}}""", """{"ok":true,"data":{"x":2}}""", false)]
    public void Normalize_exact(string a, string b, bool same) =>
        Assert.Equal(same, RelayHostShadowCheck.Normalize(a, RelayHostShadowCheck.Compare.Exact) == RelayHostShadowCheck.Normalize(b, RelayHostShadowCheck.Compare.Exact));

    [Fact]
    public void Read_back_rounds_are_timing_but_the_read_back_value_is_compared()
    {
        static string N(string s) => RelayHostShadowCheck.Normalize(s, RelayHostShadowCheck.Compare.Exact);
        Assert.Equal(N("""{"ok":true,"data":{"readBack":50.0,"readBackRounds":2}}"""), N("""{"ok":true,"data":{"readBack":50.0}}"""));
        Assert.NotEqual(N("""{"ok":true,"data":{"readBack":50.0}}"""), N("""{"ok":true,"data":{"readBack":49.0}}"""));
    }

    [Fact]
    public void Diff_excerpt_starts_near_the_first_difference()
    {
        var a = new string('x', 500) + "\"readBack\":50.0}";
        var b = new string('x', 500) + "\"readBack\":49.0}";

        var excerpt = RelayHostShadowCheck.DiffExcerpt(a, b);

        Assert.Contains("first difference at 511", excerpt, StringComparison.Ordinal);
        Assert.Contains("\"readBack\":50.0", excerpt, StringComparison.Ordinal);
        Assert.Contains("\"readBack\":49.0", excerpt, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_shape_keeps_only_outcome_and_keys()
    {
        Assert.Equal("ok:bytes,path", RelayHostShadowCheck.Normalize("""{"ok":true,"data":{"path":"p","bytes":3}}""", RelayHostShadowCheck.Compare.Shape));
        Assert.Equal("err:E_X", RelayHostShadowCheck.Normalize("""{"ok":false,"error":{"code":"E_X"}}""", RelayHostShadowCheck.Compare.Shape));
    }

    [Fact]
    public void Tap_point_is_the_centre_of_the_first_box()
    {
        Assert.Equal((200, 230), RelayHostShadowCheck.TapPoint("""{"ok":true,"data":{"node":{"left":100,"top":200,"right":300,"bottom":260}}}"""));
        Assert.Null(RelayHostShadowCheck.TapPoint("""{"ok":false,"error":{"code":"E"}}"""));
    }

    [Fact]
    public void Calls_act_only_on_the_test_target()
    {
        foreach (var call in RelayHostShadowCheck.Calls.Where(c => c.Args.Contains("\"package\"", StringComparison.Ordinal)))
        {
            Assert.Contains(RelayHostShadowCheck.TargetPackage, call.Args, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(RelayHostShadowCheck.Calls, c => c.Capability is "sys.shell" or "app.install" or "appops.set" or "contact.write" or "cal.write");
    }
}
