using DshMobile.Runtime.Migration;

namespace DshMobile.Runtime.Tests;

public sealed class WebProfileInventoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dshm-webinv-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void InspectReadOnly_DetectsSessionsAndCredentialsExistence_WithoutModifyingFiles()
    {
        var dshDir = Path.Combine(_root, ".dsh");
        var sessionsDir = Path.Combine(dshDir, "sessions");
        var projectSessionsDir = Path.Combine(dshDir, "projects", "demo-proj", "sessions");
        Directory.CreateDirectory(Path.Combine(sessionsDir, "sess-known-1"));
        Directory.CreateDirectory(projectSessionsDir);

        var credFile = Path.Combine(dshDir, ".credentials.yaml");
        const string sentinelSecret = "deepseek_api_key: sk-never-read-or-modify";
        File.WriteAllText(credFile, sentinelSecret);

        var legacyFile = Path.Combine(projectSessionsDir, "sess-legacy-9.jsonl");
        File.WriteAllText(legacyFile, "{}");

        var report = WebProfileInventory.InspectReadOnly(
            _root,
            new HashSet<string>(["sess-known-1"], StringComparer.Ordinal));

        Assert.True(report.HasUserDshDirectory);
        Assert.True(report.HasCredentialsFile);
        Assert.Equal(2, report.DiscoveredSessions.Count);
        Assert.Equal(1, report.RecoverableCount);
        Assert.Equal(1, report.UnrecognizedOrLegacyCount);
        Assert.Equal(sentinelSecret, File.ReadAllText(credFile));
        Assert.True(File.Exists(legacyFile));
    }

    [Fact]
    public void InspectReadOnly_WhenMissingUserDshDir_ReturnsEmptyReportCleanly()
    {
        Directory.CreateDirectory(_root);
        var report = WebProfileInventory.InspectReadOnly(_root);

        Assert.False(report.HasUserDshDirectory);
        Assert.False(report.HasCredentialsFile);
        Assert.Empty(report.DiscoveredSessions);
    }
}
