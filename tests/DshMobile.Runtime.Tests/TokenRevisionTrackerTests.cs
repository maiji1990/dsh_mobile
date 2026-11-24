using System.Text.Json;
using DshMobile.Runtime.Hosting;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// launch token 单调代次追踪器回归测试（PR-09 将 <see cref="DshTokenRevisionTracker"/> 迁入 Runtime 后保留向量验证）。
/// </summary>
public sealed class TokenRevisionTrackerTests
{
    [Fact]
    public void TokenTracker_MatchesSharedSequences()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "tests", "TestVectors", "webview-policy.json")));
        foreach (var seq in doc.RootElement.GetProperty("tokenTracker").EnumerateArray())
        {
            var tracker = new DshTokenRevisionTracker();
            var stepIndex = 0;
            foreach (var step in seq.GetProperty("steps").EnumerateArray())
            {
                var label = $"{seq.GetProperty("name").GetString()} #{stepIndex++}";
                if (step.TryGetProperty("clear", out var clear) && clear.GetBoolean())
                {
                    tracker.ClearForRelease();
                }
                else
                {
                    var value = step.GetProperty("update").ValueKind == JsonValueKind.Null ? null : step.GetProperty("update").GetString();
                    Assert.True(step.GetProperty("changed").GetBoolean() == tracker.Update(value), label);
                }
                Assert.True(step.GetProperty("hasToken").GetBoolean() == (tracker.Token is not null), label);
                long? expected = step.GetProperty("revision").ValueKind == JsonValueKind.Null ? null : step.GetProperty("revision").GetInt64();
                Assert.True(expected == tracker.Revision, $"{label}: revision {tracker.Revision}");
                Assert.True(tracker.Token is null || !tracker.ToString().Contains(tracker.Token, StringComparison.Ordinal), label);
            }
        }
    }
}
