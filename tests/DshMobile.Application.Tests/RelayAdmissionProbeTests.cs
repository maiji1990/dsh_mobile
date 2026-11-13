using System.Text.Json;
using DshMobile.Application.Shadow;
using DshMobile.Runtime.Automation.Relay;

namespace DshMobile.Application.Tests;

/// <summary>PR-07 segment 3: the on-device admission probe, against a fake Relay that answers in device text.</summary>
public sealed class RelayAdmissionProbeTests : IDisposable
{
    private const string Self = "org.example.dshmobile";
    private readonly string _pilot = Path.Combine(Path.GetTempPath(), "dshm-admprobe-" + Guid.NewGuid().ToString("N"), "pilot");

    private string Run => Path.Combine(_pilot, "run");

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_pilot)!;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private void MakeMailbox()
    {
        foreach (var d in new[] { "inbox", "outbox", "control-inbox", "control-outbox" })
        {
            Directory.CreateDirectory(Path.Combine(Run, d));
        }
    }

    /// <summary>Claims v1 requests and answers with <paramref name="answer"/>(id, request) (null = no answer).</summary>
    private async Task FakeRelay(Func<string, RelayRequest, string?> answer, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var f in Directory.GetFiles(Path.Combine(Run, "inbox"), "*.json"))
            {
                var name = Path.GetFileName(f);
                if (name.StartsWith('.'))
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
                var request = Assert.IsType<RelayDecodeResult.Valid>(RelayEnvelope.DecodeRequest(text, name)).Request;
                if (answer(request.Id, request) is { } reply)
                {
                    var target = Path.Combine(Run, "outbox", request.Id + ".json");
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

    private static string CSharpAnswer(RelayRequest request, long elapsedMs) =>
        RelayEncoding.Encode(new RelayAdmission(UiToolsShadowCheck.AllowedWire, Self, new RelayRateLimiter(() => 0, RelayRateLimiter.EntryLimit))
            .Check(request, elapsedMs)!);

    private async Task<ShadowReport> ProbeWith(Func<string, RelayRequest, string?>? answer, TimeSpan timeout, IEnumerable<string>? registered = null)
    {
        using var cts = new CancellationTokenSource();
        var relay = answer is null ? Task.CompletedTask : Task.Run(() => FakeRelay(answer, cts.Token));
        try
        {
            return await RelayAdmissionProbe.RunAsync(_pilot, Self, registered ?? UiToolsShadowCheck.AllowedWire, TimeProvider.System, timeout, CancellationToken.None);
        }
        finally
        {
            await cts.CancelAsync();
            await relay;
        }
    }

    private void AssertNothingLeft()
    {
        foreach (var d in new[] { "inbox", "outbox" })
        {
            Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(Run, d)));
        }
    }

    [Fact]
    public void EveryTemplate_IsAValidEnvelope_RejectedByAdmission_WithAShortTtl()
    {
        var admission = new RelayAdmission(UiToolsShadowCheck.AllowedWire, Self, new RelayRateLimiter(() => 0, RelayRateLimiter.EntryLimit));
        Assert.Equal(12, RelayAdmissionProbe.Templates.Count);
        foreach (var (id, capability, args) in RelayAdmissionProbe.Templates)
        {
            var text = RelayAdmissionProbe.RequestText(id, capability, args.Replace("{self}", Self, StringComparison.Ordinal));
            var request = Assert.IsType<RelayDecodeResult.Valid>(RelayEnvelope.DecodeRequest(text, id + ".json")).Request;
            Assert.Equal(RelayAdmissionProbe.ProbeTtlMs, request.TimeoutMs);
            Assert.NotNull(admission.Check(request));
        }
    }

    [Fact]
    public async Task RelayAnsweringLikeCSharp_Agrees_DespiteDifferentTimings()
    {
        MakeMailbox();
        var report = await ProbeWith((_, r) => CSharpAnswer(r, 23), TimeSpan.FromSeconds(20));
        Assert.Equal(ShadowVerdict.Agree, report.Verdict);
        Assert.Contains("12/12 answers byte-identical", report.Detail, StringComparison.Ordinal);
        Assert.Contains("E_GATE_HOST_DENIED ×2", report.Detail, StringComparison.Ordinal);
        AssertNothingLeft();
    }

    [Fact]
    public async Task OneByteOff_Diverges()
    {
        MakeMailbox();
        // Maven-style output (no "\/" escaping) is exactly the kind of difference this must catch.
        var report = await ProbeWith((id, r) => id == "pa-selarr" ? CSharpAnswer(r, 1).Replace("\\/", "/", StringComparison.Ordinal) : CSharpAnswer(r, 1),
            TimeSpan.FromSeconds(20));
        Assert.Equal(ShadowVerdict.Diverge, report.Verdict);
        Assert.Contains("pa-selarr.json", report.Detail, StringComparison.Ordinal);
        Assert.Equal("E_TRANSPORT_MALFORMED", report.LegacyCode);
    }

    [Fact]
    public async Task EntryFloodAnswers_AreInconclusive_NotADifference()
    {
        MakeMailbox();
        var flood = (string id, RelayRequest r) => id == "pa-unknown"
            ? RelayEncoding.Encode(RelayAdmission.Rejected(id, RelayError.RateLimited, "E_RATE_LIMITED/entry_flood", r.Capability, 0))
            : CSharpAnswer(r, 0);
        var report = await ProbeWith(flood, TimeSpan.FromSeconds(20));
        Assert.Equal(ShadowVerdict.Inconclusive, report.Verdict);
        Assert.Contains("entry flood limit hit for pa-unknown.json", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IfCSharpWouldAdmitAProbe_NothingIsWritten()
    {
        MakeMailbox();
        // A template C# would admit could reach the gate: refuse the whole run, even when the others are fine.
        var templates = RelayAdmissionProbe.Templates.Append(("pa-ok", "ui.tap", """{"x":1,"y":2}""")).ToList();
        var report = await RelayAdmissionProbe.RunAsync(_pilot, Self, UiToolsShadowCheck.AllowedWire, TimeProvider.System,
            TimeSpan.FromSeconds(1), CancellationToken.None, templates);
        Assert.Equal(ShadowVerdict.Inconclusive, report.Verdict);
        Assert.Contains("C# admits probe pa-ok", report.Detail, StringComparison.Ordinal);
        AssertNothingLeft();
    }

    [Fact]
    public async Task NoAnswer_IsInconclusive_AndTakesTheRequestsBack()
    {
        MakeMailbox();
        var report = await ProbeWith(null, TimeSpan.FromMilliseconds(600));
        Assert.Equal(ShadowVerdict.Inconclusive, report.Verdict);
        Assert.Contains("no answer within", report.Detail, StringComparison.Ordinal);
        AssertNothingLeft();
    }

    [Fact]
    public void ProbeReasons_AreTheVectorOnes()
    {
        // The libcore-only expectations the device run confirms are also locked in the shared vector file.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root!.FullName, "global.json")))
        {
            root = root.Parent;
        }
        var v = JsonElement.Parse(File.ReadAllText(Path.Combine(root.FullName, "tests", "TestVectors", "relay-admission.json")));
        var all = v.GetProperty("args").EnumerateArray().Concat(v.GetProperty("csharpOnly").GetProperty("args").EnumerateArray()).ToList();
        foreach (var (_, capability, args) in RelayAdmissionProbe.Templates.Where(t => !t.Args.Contains("{self}", StringComparison.Ordinal)
                     && UiToolsShadowCheck.AllowedWire.Contains(t.Capability)))
        {
            var match = all.FirstOrDefault(c => c.GetProperty("capability").GetString() == capability
                && JsonElement.DeepEquals(JsonElement.Parse(c.GetProperty("args").GetString()!), JsonElement.Parse(args)));
            if (match.ValueKind == JsonValueKind.Object)
            {
                Assert.Equal(match.GetProperty("expect").GetString(), CapabilityArgsSpec.Validate(capability, JsonElement.Parse(args)));
            }
        }
    }
}
