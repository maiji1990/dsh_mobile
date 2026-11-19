using System.Text.Json;
using DshMobile.Core.Diagnostics;
using DshMobile.Runtime;
using DshMobile.Runtime.Diagnostics;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// The P0 Web-profile diagnostic surface: fixed variants only, the preload stays inside the isolated guest cache, the
/// handshake summary can only contribute numbers and allowlisted error codes, and nothing runs while production DSH runs
/// (docs/WEB_STARTUP_DIAGNOSIS.md).
/// </summary>
public sealed class WebProfileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dshm-webprof-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private RuntimeLayout Layout()
    {
        var files = Path.Combine(_root, "files");
        Directory.CreateDirectory(files);
        return new RuntimeLayout(files, Path.Combine(_root, "cache"), Path.Combine(_root, "libs"));
    }

    [Fact]
    public void Variants_AreFixedOffAndProfile_AndTheGuestPathIsInsideTheDiagnosticCache()
    {
        Assert.Equal(["E5-OFF", "E5-PROFILE"], WebProfileExperiments.All.Select(v => v.Id));
        Assert.Equal(["off", "profile"], WebProfileExperiments.All.Select(v => v.Mode));
        Assert.False(WebProfileExperiments.TryGet("E5-OTHER", out _));
        Assert.StartsWith(RuntimeConstants.GuestCacheDir + "/experiments/e5", WebProfileExperiments.GuestDir, StringComparison.Ordinal);
        Assert.Equal("web-profile-preload.mjs", WebProfileExperiments.PreloadName);
    }

    [Fact]
    public void StagePreload_WritesOnlyInsideTheIsolatedCache()
    {
        var production = Layout();
        using var isolated = IsolatedGuestLayout.Create(production, Path.Combine(_root, "iso"));
        var hostDir = WebProfileRunner.StagePreload(isolated, "console.log(1)\n"u8);

        Assert.StartsWith(isolated.CacheDir, hostDir, StringComparison.Ordinal);
        Assert.Equal("console.log(1)\n", File.ReadAllText(Path.Combine(hostDir, WebProfileExperiments.PreloadName)));
        Assert.Throws<ArgumentException>(() => WebProfileRunner.StagePreload(isolated, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Summary_ParsesOnlyNumbersAndAllowlistedCodes()
    {
        var summary = WebProfileSummary.TryParse(
            """{"schema":1,"mode":"profile","wallMs":16800.5,"nodeUserCpuMs":5800.2,"nodeSystemCpuMs":900.1,"eventLoopActiveMs":6800,"eventLoopIdleMs":9500,"profileSampleCount":1240,"stopLatencyMs":12,"error":null,"token":"secret","url":"/root/projects/x","functionName":"eval"}""");

        Assert.NotNull(summary);
        Assert.Equal(16800.5, summary!.WallMs);
        Assert.Equal(1240, summary.ProfileSampleCount);
        Assert.Null(summary.Error);
        Assert.DoesNotContain("secret", summary.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_RejectsUnknownErrorsAndMalformedPayloads()
    {
        Assert.Null(WebProfileSummary.TryParse("""{"schema":1,"error":"PROFILE_SOMETHING_ELSE","profileSampleCount":1}"""));
        Assert.Null(WebProfileSummary.TryParse("""{"schema":2,"wallMs":1}"""));
        Assert.Null(WebProfileSummary.TryParse("not json"));
        Assert.Null(WebProfileSummary.TryParse(""));

        var known = WebProfileSummary.TryParse("""{"schema":1,"error":"PROFILE_EMPTY","profileSampleCount":null}""");
        Assert.NotNull(known);
        Assert.Equal(WebProfileExperiments.ProfileEmpty, known!.Error);
        Assert.Null(known.ProfileSampleCount);
    }

    [Fact]
    public void Record_WritesOnlyTheFixedFields_AndRedactsUnexpectedText()
    {
        var record = new WebProfileRecord
        {
            Variant = WebProfileExperiments.Profile,
            RunId = "abc/def",
            Iteration = 2,
            HttpReadyMs = 16800.1234,
            ProfileCaptureMs = 420.5,
            ProfileSampleCount = 1240,
            NodeUserCpuMs = 5800.2,
            NodeSystemCpuMs = 900.1,
            EventLoopActiveMs = 6800,
            EventLoopIdleMs = 9500,
            StopLatencyMs = 12.0,
            Result = ExperimentRecord.Ok,
            ProfileStoredPrivately = true,
            StartedAt = new DateTimeOffset(2026, 11, 19, 2, 3, 4, TimeSpan.Zero),
        };

        var line = record.ToJsonLine();
        var json = JsonElement.Parse(line);
        Assert.Equal(1, json.GetProperty("schema").GetInt32());
        Assert.Equal("E5", json.GetProperty("experiment").GetString());
        Assert.Equal(16800.123, json.GetProperty("httpReadyMs").GetDouble(), 3);
        Assert.Equal(1240, json.GetProperty("profileSampleCount").GetInt32());
        Assert.True(json.GetProperty("profileStoredPrivately").GetBoolean());
        Assert.Equal(StartupTrace.Redacted, json.GetProperty("runId").GetString());
        Assert.DoesNotContain("def", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runner_RefusesWhileDshRunsOrThePortIsBusy_WithoutSpawning()
    {
        var layout = Layout();
        var records = new List<WebProfileRecord>();
        var running = new WebProfileRunner(layout, _ => { }, () => true, _ => false);
        var busyPort = new WebProfileRunner(layout, _ => { }, () => false, _ => true);

        var a = await running.RunAsync(1, "x"u8.ToArray(), Path.Combine(_root, "p1"), records.Add, CancellationToken.None);
        var b = await busyPort.RunAsync(1, "x"u8.ToArray(), Path.Combine(_root, "p2"), records.Add, CancellationToken.None);

        Assert.Equal(WebProfileRunner.DshRunning, a.Code);
        Assert.Equal(WebProfileRunner.PortBusy, b.Code);
        Assert.Empty(records);
    }

    [Fact]
    public async Task Runner_RejectsBadRepsAndEmptyPreload()
    {
        var layout = Layout();
        var runner = new WebProfileRunner(layout, _ => { }, () => false, _ => false);
        var bad = await runner.RunAsync(0, "x"u8.ToArray(), Path.Combine(_root, "p"), _ => { }, CancellationToken.None);
        var empty = await runner.RunAsync(1, [], Path.Combine(_root, "p"), _ => { }, CancellationToken.None);
        Assert.Equal(WebProfileRunner.BadReps, bad.Code);
        Assert.Equal(WebProfileRunner.NoPreload, empty.Code);
    }
}
