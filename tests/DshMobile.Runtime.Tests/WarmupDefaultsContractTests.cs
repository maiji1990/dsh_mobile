using System.Xml.Linq;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// 5A：后台预热默认开启的源码契约。Android 偏好与资源无法在纯托管测试里实例化，这里直接钉住源码，
/// 防止默认值被改回 false 或文案仍写着“默认关闭”。
/// </summary>
public sealed class WarmupDefaultsContractTests
{
    private static string AndroidDir => Path.Combine(Repo.Root, "src", "DshMobile.Android");

    private static string UiPrefs => File.ReadAllText(Path.Combine(AndroidDir, "Ui", "UiPrefs.cs"));

    private static string MainActivity => File.ReadAllText(Path.Combine(AndroidDir, "MainActivity.cs"));

    private static string StringResource(string name)
    {
        var doc = XDocument.Load(Path.Combine(AndroidDir, "Resources", "values", "strings.xml"));
        return doc.Descendants("string")
            .Single(e => (string?)e.Attribute("name") == name)
            .Value;
    }

    [Fact]
    public void EagerWarmup_DefaultsToEnabled()
    {
        Assert.Contains("GetBoolean(EagerWarmupKey, true)", UiPrefs, StringComparison.Ordinal);
        Assert.DoesNotContain("GetBoolean(EagerWarmupKey, false)", UiPrefs, StringComparison.Ordinal);
    }

    [Fact]
    public void WarmupSettingText_StatesSilentWarmupAndProtections()
    {
        // 副标题已精简为 ≤2 行：保留“静默预热 / 低电量 / 温控 / 自动跳过”等关键语义。
        foreach (var key in new[] { "more_warmup_sub", "more_warmup_short_sub" })
        {
            var text = StringResource(key);
            Assert.Contains("静默预热", text, StringComparison.Ordinal);
            Assert.Contains("低电量", text, StringComparison.Ordinal);
            Assert.Contains("温控", text, StringComparison.Ordinal);
            Assert.Contains("自动跳过", text, StringComparison.Ordinal);
            Assert.DoesNotContain("默认关闭", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MainActivity_PassesTheRealPreference_NotAHardCodedTrue()
    {
        Assert.Contains("TryWarmupAsync(enabled, lowBattery, powerSave)", MainActivity, StringComparison.Ordinal);
        Assert.Contains("var enabled = UiPrefs.EagerWarmupEnabled(this);", MainActivity, StringComparison.Ordinal);
    }

    [Fact]
    public void SilentWarmup_DoesNotRunTheLanPermissionGate()
    {
        var start = MainActivity.IndexOf("private async Task TryEagerWarmupAsync", StringComparison.Ordinal);
        var end = MainActivity.IndexOf("public override void OnConfigurationChanged", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);

        var method = MainActivity[start..end];
        Assert.DoesNotContain("LocalNetworkAccess", method, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestPermissions", method, StringComparison.Ordinal);
    }
}
