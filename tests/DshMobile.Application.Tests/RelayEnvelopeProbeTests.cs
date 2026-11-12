using System.Text.Json;
using System.Text.Json.Nodes;
using DshMobile.Application.Shadow;

namespace DshMobile.Application.Tests;

/// <summary>
/// PR-07 segment 2: the on-device Relay envelope probe. A fake Relay answers from the shared vectors
/// (tests/TestVectors/relay-envelope.json, the same answers the upstream codec gives in LegacyRelayEnvelopeParityTest).
/// </summary>
public sealed class RelayEnvelopeProbeTests : IDisposable
{
    private readonly string _pilot = Path.Combine(Path.GetTempPath(), "dshm-relayprobe-" + Guid.NewGuid().ToString("N"), "pilot");

    private string Run => Path.Combine(_pilot, "run");

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_pilot)!;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir.FullName;
    }

    private static readonly JsonElement Vectors =
        JsonElement.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "tests", "TestVectors", "relay-envelope.json")));

    private static List<(bool Control, JsonElement Case)> ProbeVectors() =>
    [
        .. Vectors.GetProperty("request").EnumerateArray().Where(IsProbe).Select(c => (false, c)),
        .. Vectors.GetProperty("control").EnumerateArray().Where(IsProbe).Select(c => (true, c)),
    ];

    private static bool IsProbe(JsonElement c) => c.TryGetProperty("probe", out var p) && p.GetBoolean();

    private static string Text(JsonElement c)
    {
        var text = c.GetProperty("text").GetString()!;
        return c.TryGetProperty("padTo", out var n) ? text.PadRight(n.GetInt32()) : text;
    }

    [Fact]
    public void Cases_AreExactlyTheVectorProbeCases()
    {
        var vectors = ProbeVectors();
        Assert.Equal(vectors.Count, RelayEnvelopeProbe.Cases.Count);
        foreach (var (control, c) in vectors)
        {
            var file = c.GetProperty("file").GetString()!;
            var probe = Assert.Single(RelayEnvelopeProbe.Cases, p => p.File == file);
            Assert.Equal(control, probe.Control);
            Assert.Equal(Text(c), probe.Text);
        }
    }

    private void MakeMailbox()
    {
        foreach (var d in new[] { "inbox", "outbox", "control-inbox", "control-outbox", "processing" })
        {
            Directory.CreateDirectory(Path.Combine(Run, d));
        }
    }

    /// <summary>A stand-in for Relay: claims request files and answers the way MailboxServer / ControlChannelServer do.</summary>
    private async Task FakeRelay(Func<string, string, string> code, CancellationToken ct)
    {
        var expect = ProbeVectors().ToDictionary(v => v.Case.GetProperty("file").GetString()!, v => v.Case.GetProperty("expect").GetProperty("rejected"));
        while (!ct.IsCancellationRequested)
        {
            foreach (var (inbox, outbox, control) in new[] { ("inbox", "outbox", false), ("control-inbox", "control-outbox", true) })
            {
                foreach (var f in Directory.GetFiles(Path.Combine(Run, inbox), "*.json"))
                {
                    var name = Path.GetFileName(f);
                    if (name.StartsWith('.'))
                    {
                        continue;
                    }
                    File.Delete(f);
                    var r = expect[name];
                    var id = r.GetProperty("id").GetString()!;
                    var answer = control
                        ? new JsonObject { ["v"] = 2, ["id"] = id, ["op"] = "unknown", ["ok"] = false, ["cause"] = code(name, $"{r.GetProperty("code").GetString()}/{r.GetProperty("cause").GetString()}") }
                        : new JsonObject { ["v"] = 1, ["id"] = id, ["ok"] = false, ["elapsedMs"] = 1, ["error"] = new JsonObject { ["code"] = code(name, r.GetProperty("code").GetString()!), ["retryable"] = false, ["exitCode"] = 1 } };
                    var target = Path.Combine(Run, outbox, id + ".json");
                    File.WriteAllText(target + ".tmp", answer.ToJsonString());
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

    private async Task<ShadowReport> ProbeWith(Func<string, string, string>? code, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource();
        var relay = code is null ? Task.CompletedTask : Task.Run(() => FakeRelay(code, cts.Token));
        try
        {
            return await RelayEnvelopeProbe.RunAsync(_pilot, TimeProvider.System, timeout, CancellationToken.None);
        }
        finally
        {
            await cts.CancelAsync();
            await relay;
        }
    }

    private void AssertNothingLeft()
    {
        foreach (var d in new[] { "inbox", "outbox", "control-inbox", "control-outbox" })
        {
            Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(Run, d)));
        }
    }

    [Fact]
    public async Task RelayAnsweringLikeTheVectors_Agrees_AndCleansUp()
    {
        MakeMailbox();
        var report = await ProbeWith((_, c) => c, TimeSpan.FromSeconds(20));
        Assert.Equal(ShadowVerdict.Agree, report.Verdict);
        Assert.Equal(RelayEnvelopeProbe.Slice, report.Slice);
        Assert.True(report.LegacyOk);
        Assert.Contains("16/16", report.Detail, StringComparison.Ordinal);
        AssertNothingLeft();
    }

    [Fact]
    public async Task OneDifferentAnswer_Diverges()
    {
        MakeMailbox();
        var report = await ProbeWith((file, c) => file == "pv2-op.json" ? "E_TRANSPORT_MALFORMED/opcode" : c, TimeSpan.FromSeconds(20));
        Assert.Equal(ShadowVerdict.Diverge, report.Verdict);
        Assert.Equal("E_TRANSPORT_MALFORMED/opcode", report.LegacyCode);
        Assert.Contains("pv2-op.json", report.Detail, StringComparison.Ordinal);
        AssertNothingLeft();
    }

    [Fact]
    public async Task DifferentV1Code_Diverges()
    {
        MakeMailbox();
        var report = await ProbeWith((file, c) => file == "pv1-cap.json" ? "E_TRANSPORT_MALFORMED" : c, TimeSpan.FromSeconds(20));
        Assert.Equal(ShadowVerdict.Diverge, report.Verdict);
        Assert.Contains("pv1-cap.json: legacy E_TRANSPORT_MALFORMED, C# E_NOT_IMPLEMENTED", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoRelayServing_IsInconclusive_AndTakesTheRequestsBack()
    {
        MakeMailbox();
        // A stale answer from an earlier run (wrong on purpose) must be dropped, not counted.
        File.WriteAllText(Path.Combine(Run, "control-outbox", "pv2-op.json"), """{"v":2,"id":"pv2-op","op":"unknown","ok":false,"cause":"stale"}""");
        var report = await ProbeWith(null, TimeSpan.FromMilliseconds(600));
        Assert.Equal(ShadowVerdict.Inconclusive, report.Verdict);
        Assert.Contains("0/16 answered", report.Detail, StringComparison.Ordinal);
        AssertNothingLeft();
    }

    [Fact]
    public async Task NoMailbox_IsInconclusive_AndWritesNothing()
    {
        var report = await ProbeWith(null, TimeSpan.FromSeconds(1));
        Assert.Equal(ShadowVerdict.Inconclusive, report.Verdict);
        Assert.Contains("start the relay first", report.Detail, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_pilot));
    }

    [Fact]
    public void EveryCase_IsRejectedByTheCSharpDecoder()
    {
        foreach (var c in RelayEnvelopeProbe.Cases)
        {
            object result = c.Control
                ? DshMobile.Runtime.Automation.Relay.RelayEnvelope.DecodeControl(c.Text, c.File)
                : DshMobile.Runtime.Automation.Relay.RelayEnvelope.DecodeRequest(c.Text, c.File);
            Assert.True(result is DshMobile.Runtime.Automation.Relay.RelayDecodeResult.Rejected
                or DshMobile.Runtime.Automation.Relay.ControlDecodeResult.Rejected, c.File);
        }
    }
}
