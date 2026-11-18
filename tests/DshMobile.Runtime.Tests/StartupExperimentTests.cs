using System.Text.Json;
using DshMobile.Core.Diagnostics;
using DshMobile.Runtime;
using DshMobile.Runtime.Diagnostics;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// The E1-E4 diagnostic catalogue and isolation rules: fixed variants only, isolated directories never point at user data,
/// and the one-line record can only carry the documented fields (docs/STARTUP_EXPERIMENTS.md).
/// </summary>
public sealed class StartupExperimentTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dshm-exp-").FullName;

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
    public void Variants_AreAFixedUniqueCatalogue_WithOnlyKnownProgramsAndFlags()
    {
        var ids = StartupExperiments.All.Select(v => v.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(StartupExperiments.All, v => Assert.Equal(v.Experiment, v.Id[..2]));
        Assert.All(StartupExperiments.All, v => Assert.Contains(v.Program, new[] { StartupExperiments.True, StartupExperiments.Node }));
        Assert.All(StartupExperiments.All, v => Assert.True(v.TimeoutSeconds is >= 30 and <= 180));
        Assert.All(StartupExperiments.All.Where(v => v.Endpoint == ExperimentEndpoint.Web), v => Assert.Equal(ExperimentRecord.KindWeb, v.Kind));
        Assert.Equal(2, StartupExperiments.For("E1").Count(v => v.Marker == StartupExperiments.Marker));
    }

    [Fact]
    public void UnknownVariant_IsNotInTheCatalogue() => Assert.False(StartupExperiments.TryGet("E1-9", out _));

    [Fact]
    public void IsolatedLayout_KeepsEverythingUnderItsOwnRoot_AndNeverTouchesUserData()
    {
        var production = Layout();
        var sentinel = Path.Combine(production.UserDataDir, "workspace.txt");
        Directory.CreateDirectory(production.UserDataDir);
        File.WriteAllText(sentinel, "user data");

        var root = Path.Combine(_root, "isolated");
        using var isolated = IsolatedGuestLayout.Create(production, root);

        Assert.StartsWith(root, isolated.WorkspaceDir, StringComparison.Ordinal);
        Assert.StartsWith(root, isolated.CacheDir, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(isolated.WorkspaceDir, ".dsh", "dshmobile", "bin", "dsh")));
        Assert.True(Directory.Exists(isolated.CacheDir));
        Assert.Equal("user data", File.ReadAllText(sentinel));
    }

    [Fact]
    public void CopyRegisteredOverlays_CopiesOnlyExistingFiles_AndReturnsGuestPaths()
    {
        var production = Layout();
        var relative = ".dsh/dshmobile/plugin-market/market-and-safe-mode-overlay.yml";
        var source = Path.Combine(production.UserDataDir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "- name: demo\n");

        using var isolated = IsolatedGuestLayout.Create(production, Path.Combine(_root, "iso2"));
        var overlays = isolated.CopyRegisteredOverlays(production);

        Assert.Equal(["/root/projects/" + relative], overlays);
        Assert.True(File.Exists(Path.Combine(isolated.WorkspaceDir, relative.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public void Record_WritesOnlyTheFixedFields_AndRedactsFreeText()
    {
        var record = new ExperimentRecord
        {
            Experiment = "E3",
            Variant = "E3-0",
            Kind = ExperimentRecord.KindWeb,
            RunId = "abc123def456",
            Iteration = 3,
            DurationMs = 16167.1234,
            SpawnMs = 54.2,
            HttpReadyMs = 16167.1,
            HttpStatus = 401,
            Result = ExperimentRecord.Ok,
            StartedAt = new DateTimeOffset(2026, 11, 18, 2, 3, 4, TimeSpan.Zero),
            Note = "/root/projects secret prompt",
        };

        var line = record.ToJsonLine();
        var json = JsonElement.Parse(line);
        Assert.Equal(1, json.GetProperty("schema").GetInt32());
        Assert.Equal("E3-0", json.GetProperty("variant").GetString());
        Assert.Equal(16167.123, json.GetProperty("durationMs").GetDouble(), 3);
        Assert.Equal(401, json.GetProperty("httpStatus").GetInt32());
        Assert.Equal(StartupTrace.Redacted, json.GetProperty("note").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("cacheFileCount").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("guestFirstMarkerMs").ValueKind);
        Assert.DoesNotContain("secret", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runner_RefusesWhileProductionProcessesRun_AndEmitsNothing()
    {
        var layout = Layout();
        var lines = new List<ExperimentRecord>();
        var runner = new StartupExperimentRunner(layout, _ => { }, () => true, _ => false);

        var result = await runner.RunAsync(StartupExperiments.SuiteE1E2, 1, lines.Add, CancellationToken.None);

        Assert.False(result.Ran);
        Assert.Equal(StartupExperimentRunner.DshRunning, result.Code);
        Assert.Empty(lines);
    }

    [Fact]
    public async Task Runner_RefusesWhileThePortIsBusy_AndEmitsNothing()
    {
        var layout = Layout();
        var lines = new List<ExperimentRecord>();
        var runner = new StartupExperimentRunner(layout, _ => { }, () => false, _ => true);

        var result = await runner.RunAsync(StartupExperiments.SuiteE3, 1, lines.Add, CancellationToken.None);

        Assert.False(result.Ran);
        Assert.Equal(StartupExperimentRunner.PortBusy, result.Code);
        Assert.Empty(lines);
    }

    [Fact]
    public async Task Runner_RejectsUnknownSuiteAndBadReps()
    {
        var layout = Layout();
        var runner = new StartupExperimentRunner(layout, _ => { }, () => false, _ => false);
        var unknown = await runner.RunAsync("E9", 1, _ => { }, CancellationToken.None);
        var badReps = await runner.RunAsync(StartupExperiments.SuiteE1E2, 0, _ => { }, CancellationToken.None);
        Assert.Equal(StartupExperimentRunner.UnknownSuite, unknown.Code);
        Assert.Equal("EXPERIMENT_BAD_REPS", badReps.Code);
    }
}
