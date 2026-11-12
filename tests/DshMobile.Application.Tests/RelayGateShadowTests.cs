using System.Text.Json;
using DshMobile.Application.Shadow;
using DshMobile.Runtime.Automation.Relay;

namespace DshMobile.Application.Tests;

/// <summary>
/// PR-07 segment 5: the gate shadow. Passive half over capabilities.json texts shaped like Relay's
/// (RelayContainer.capabilitiesJson rows with systemPermission / assistantTier / ceiling / usable); live half against a
/// fake Relay mailbox, with the guards that keep it from ever writing a call the gate might let through.
/// </summary>
public sealed class RelayGateShadowTests : IDisposable
{
    private const string Self = "org.example.dshmobile";
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;
    private static readonly string[] Registered = ["ui.node", "ui.snapshot"];
    private readonly string _pilot = Path.Combine(Path.GetTempPath(), "dshm-gateprobe-" + Guid.NewGuid().ToString("N"), "pilot");

    private string Run => Path.Combine(_pilot, "run");

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_pilot)!;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static RelayDeviceFacts Device(bool a11yListed = false, bool listener = false) =>
        new(37, a11yListed, listener, new HashSet<string>(StringComparer.Ordinal), MicrophoneBlocked: false);

    private static Func<string, string?> Prefs(params (string Key, string Value)[] values) =>
        key => values.FirstOrDefault(v => v.Key == key).Value;

    private static string Row(string id, string system, string tier = "ASK", bool usable = false, string ceiling = "ANY") =>
        $$"""{"id":"{{id}}","category":"observe","systemPermission":"{{system}}","assistantTier":"{{tier}}","ceiling":"{{ceiling}}","usable":{{(usable ? "true" : "false")}},"implemented":true,"minSdk":29,"surfaces":["foreground","trusted-display"],"guide":"ACCESSIBILITY"}""";

    private static string Doc(string rows, bool running = false, bool bound = false, int displayId = -1) =>
        """{"protocol":1,"entry":"/opt/pilot/bin/relay","host":{"package":"org.example.dshmobile"},"backend":{"shizuku":{"running":"""
        + (running ? "true" : "false") + ""","authorized":false,"bound":""" + (bound ? "true" : "false")
        + ""","shellUid":-1,"version":-1,"trustedDisplay":{"alive":false,"displayId":""" + displayId + """}}},"capabilities":[""" + rows + "]}";

    /// <summary>The phone today: accessibility off, so Relay publishes MANUAL_ONLY for both ui.* rows.</summary>
    private static string Off(string tier = "ASK") => Doc(Row("ui.snapshot", "MANUAL_ONLY", tier) + "," + Row("ui.node", "MANUAL_ONLY", tier));

    private static ShadowReport Check(string? json, RelayDeviceFacts? device = null, Func<string, string?>? prefs = null) =>
        RelayGateShadowCheck.Compare(json, device ?? Device(), prefs ?? Prefs(), Registered, At);

    [Fact]
    public void PhoneToday_AccessibilityOff_Agrees()
    {
        var r = Check(Off());
        Assert.Equal(ShadowVerdict.Agree, r.Verdict);
        Assert.Equal(RelayGateShadowCheck.Slice, r.Slice);
        Assert.Contains("2/2 rows agree on system state, tier and ceiling (states MANUAL_ONLY ×2; tiers ASK ×2)", r.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("binding", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AccessibilityListed_IsGranted_AndUsableIsAllowed()
    {
        var r = Check(Doc(Row("ui.snapshot", "GRANTED", usable: true) + "," + Row("ui.node", "GRANTED", usable: true)), Device(a11yListed: true));
        Assert.Equal(ShadowVerdict.Agree, r.Verdict);
        // Listed but Relay says MANUAL_ONLY: no binding explains that.
        var wrong = Check(Off(), Device(a11yListed: true));
        Assert.Equal(ShadowVerdict.Diverge, wrong.Verdict);
        Assert.Contains("ui.snapshot systemPermission@foreground: legacy MANUAL_ONLY C# GRANTED", wrong.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundButUnlisted_IsAcceptedAsABinding_CSharpCannotSee()
    {
        // Some ROMs leave the Settings list empty while the service is bound: Relay says GRANTED, C# alone MANUAL_ONLY.
        var r = Check(Doc(Row("ui.snapshot", "GRANTED", usable: true) + "," + Row("ui.node", "GRANTED", usable: true)));
        Assert.Equal(ShadowVerdict.Agree, r.Verdict);
        Assert.Contains("2 only explained by a binding C# cannot see", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Tiers_ComeFromTheRelayPrefs_AndMustMatch()
    {
        var prefs = Prefs(("tier_ui_node", "DENIED"), ("tier_ui_snapshot", "ALWAYS"));
        var r = Check(Doc(Row("ui.snapshot", "MANUAL_ONLY", "ALWAYS") + "," + Row("ui.node", "MANUAL_ONLY", "DENIED")), prefs: prefs);
        Assert.Equal(ShadowVerdict.Agree, r.Verdict);
        Assert.Contains("tiers ALWAYS ×1, DENIED ×1", r.Detail, StringComparison.Ordinal);
        var stale = Check(Off(), prefs: prefs);
        Assert.Equal(ShadowVerdict.Diverge, stale.Verdict);
        Assert.Contains("ui.snapshot assistantTier: legacy ASK C# ALWAYS", stale.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void UsableWhereTheGateRefuses_OrAnotherCeiling_Diverges()
    {
        var r = Check(Doc(Row("ui.snapshot", "MANUAL_ONLY", usable: true) + "," + Row("ui.node", "MANUAL_ONLY", ceiling: "ASK_ONLY")));
        Assert.Equal(ShadowVerdict.Diverge, r.Verdict);
        Assert.Contains("ui.snapshot: legacy usable, C# gate refuses (MANUAL_ONLY, ASK)", r.Detail, StringComparison.Ordinal);
        Assert.Contains("ui.node ceiling: legacy ASK_ONLY C# ANY", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void NoFile_Unparsable_UnknownDisplay_MissingRows()
    {
        Assert.Equal(ShadowVerdict.Inconclusive, Check(null).Verdict);
        Assert.Equal(ShadowVerdict.Inconclusive, Check("{").Verdict);
        var display = Check(Doc(Row("ui.snapshot", "MANUAL_ONLY") + "," + Row("ui.node", "MANUAL_ONLY"), running: true, bound: false, displayId: 3));
        Assert.Equal(ShadowVerdict.Inconclusive, display.Verdict);
        Assert.Contains("trusted display state unknown", display.Detail, StringComparison.Ordinal);
        var missing = Check(Doc(Row("ui.snapshot", "MANUAL_ONLY")));
        Assert.Equal(ShadowVerdict.Diverge, missing.Verdict);
        Assert.Contains("missing [ui.node]", missing.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void UpperSnake_IsTheUpstreamEnumName()
    {
        Assert.Equal("SESSION_ONLY", RelayGateShadowCheck.Upper(TierCeiling.SessionOnly));
        Assert.Equal("UNAVAILABLE_ON_DEVICE", RelayGateShadowCheck.Upper(SystemGrantState.UnavailableOnDevice));
        Assert.Equal("ASK", RelayGateShadowCheck.Upper(AccessTier.Ask));
    }

    // ---- live probe ----

    private void MakeMailbox()
    {
        foreach (var d in new[] { "inbox", "outbox", "control-inbox", "control-outbox" })
        {
            Directory.CreateDirectory(Path.Combine(Run, d));
        }
    }

    private async Task FakeRelay(Func<RelayRequest, string?> answer, CancellationToken ct)
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
                var text = File.ReadAllText(f);
                File.Delete(f);
                var request = Assert.IsType<RelayDecodeResult.Valid>(RelayEnvelope.DecodeRequest(text, name)).Request;
                Assert.Equal(RelayGateShadowCheck.ProbeTtlMs, request.TimeoutMs);
                if (answer(request) is { } reply)
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

    /// <summary>What Relay sends for a system_missing refusal: the shared vector bytes (g1/g2) with this id and timing.</summary>
    private static string VectorAnswer(RelayRequest r, long elapsedMs)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root!.FullName, "global.json")))
        {
            root = root.Parent;
        }
        var v = JsonElement.Parse(File.ReadAllText(Path.Combine(root.FullName, "tests", "TestVectors", "relay-gate.json")));
        var json = v.GetProperty("refusals").EnumerateArray().First(c => c.GetProperty("capability").GetString() == r.Capability
            && c.GetProperty("label").GetString() == "system_missing").GetProperty("json").GetString()!;
        var id = JsonElement.Parse(json).GetProperty("id").GetString()!;
        return json.Replace($"\"id\":\"{id}\"", $"\"id\":\"{r.Id}\"", StringComparison.Ordinal)
            .Replace("\"elapsedMs\":7", $"\"elapsedMs\":{elapsedMs}", StringComparison.Ordinal);
    }

    private async Task<ShadowReport> Probe(Func<RelayRequest, string?>? answer, string? json, RelayDeviceFacts? device = null,
        Func<string, string?>? prefs = null, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource();
        var relay = answer is null ? Task.CompletedTask : Task.Run(() => FakeRelay(answer, cts.Token));
        try
        {
            return await RelayGateShadowCheck.RunProbeAsync(_pilot, json, device ?? Device(), prefs ?? Prefs(), Self,
                UiToolsShadowCheck.AllowedWire, TimeProvider.System, timeout ?? TimeSpan.FromSeconds(20), CancellationToken.None);
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
    public async Task RelayRefusingLikeTheVectors_Agrees()
    {
        MakeMailbox();
        var report = await Probe(r => VectorAnswer(r, 31), Off());
        Assert.Equal(ShadowVerdict.Agree, report.Verdict);
        Assert.Contains("2/2 answers byte-identical (E_GATE_SYSTEM_MISSING/system_missing", report.Detail, StringComparison.Ordinal);
        AssertNothingLeft();
    }

    [Fact]
    public async Task ADifferentRefusal_Diverges()
    {
        MakeMailbox();
        var report = await Probe(r => VectorAnswer(r, 3).Replace("system_missing", "tier_denied", StringComparison.Ordinal), Off());
        Assert.Equal(ShadowVerdict.Diverge, report.Verdict);
        Assert.Contains("pg-snapshot.json", report.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("listed", "the accessibility service is listed in Settings")]
    [InlineData("granted", "Relay publishes ui.snapshot as GRANTED, not MANUAL_ONLY")]
    [InlineData("always", "ui.snapshot is set to ALWAYS")]
    [InlineData("nofile", "no capabilities.json")]
    public async Task WhenTheGateMightNotRefuse_NothingIsWritten(string why, string expect)
    {
        MakeMailbox();
        var (json, device, prefs) = why switch
        {
            "listed" => (Off(), Device(a11yListed: true), Prefs()),
            "granted" => (Doc(Row("ui.snapshot", "GRANTED") + "," + Row("ui.node", "MANUAL_ONLY")), Device(), Prefs()),
            "always" => (Off("ALWAYS"), Device(), Prefs(("tier_ui_snapshot", "ALWAYS"))),
            _ => ((string?)null, Device(), Prefs()),
        };
        var report = await Probe(null, json, device, prefs, TimeSpan.FromMilliseconds(500));
        Assert.Equal(ShadowVerdict.Inconclusive, report.Verdict);
        Assert.Contains("not run: " + expect, report.Detail, StringComparison.Ordinal);
        AssertNothingLeft();
    }

    [Fact]
    public async Task NoAnswer_IsInconclusive_AndTakesTheRequestsBack()
    {
        MakeMailbox();
        var report = await Probe(null, Off(), timeout: TimeSpan.FromMilliseconds(600));
        Assert.Equal(ShadowVerdict.Inconclusive, report.Verdict);
        Assert.Contains("no answer within", report.Detail, StringComparison.Ordinal);
        AssertNothingLeft();
    }
}
