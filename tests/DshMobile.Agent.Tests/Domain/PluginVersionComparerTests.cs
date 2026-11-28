using DshMobile.Agent.Domain;

namespace DshMobile.Agent.Tests.Domain;

/// <summary>SemVer 2.0 版本比较的黄金用例：更新判定、预发布排序与非法输入安全降级。</summary>
public sealed class PluginVersionComparerTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.4")]
    [InlineData("V1.2.3", "1.3.0")]
    [InlineData("1.9.0", "1.10.0")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("0.2.1", "0.2.2-rc.5")]
    [InlineData("1.0.0-alpha", "1.0.0-beta")]
    public void IsUpdateAvailable_ReturnsTrue_WhenLatestStrictlyHigher(string installedVersion, string latestVersion)
    {
        Assert.True(PluginVersionComparer.IsUpdateAvailable(installedVersion, latestVersion));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.4", "1.2.3")]
    [InlineData("1.10.0", "1.9.0")]
    [InlineData("1.0.0", "1.0.0-rc.1")]
    [InlineData("0.2.2-rc.5", "0.2.1")]
    [InlineData("1.0.0+build.1", "1.0.0")]
    [InlineData("1.0.0", "1.0.0+build.2")]
    public void IsUpdateAvailable_ReturnsFalse_WhenEqualOrLower(string installedVersion, string latestVersion)
    {
        Assert.False(PluginVersionComparer.IsUpdateAvailable(installedVersion, latestVersion));
    }

    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
    [InlineData("1.0.0-beta", "1.0.0-beta.2")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    public void PrereleaseIdentifiers_FollowSemVerOrdering(string lowerVersion, string higherVersion)
    {
        Assert.True(PluginVersionComparer.IsUpdateAvailable(lowerVersion, higherVersion));
        Assert.False(PluginVersionComparer.IsUpdateAvailable(higherVersion, lowerVersion));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("latest")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("^1.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3-α")]
    public void InvalidVersions_AreNotComparable_AndNeverReportUpdate(string? version)
    {
        Assert.False(PluginVersionComparer.IsValidVersion(version));
        Assert.False(PluginVersionComparer.IsUpdateAvailable(version, "9.9.9"));
        Assert.False(PluginVersionComparer.IsUpdateAvailable("1.0.0", version));
        Assert.False(PluginVersionComparer.TryCompare(version, "1.0.0", out var comparison));
        Assert.Equal(0, comparison);
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("v1.2.3")]
    [InlineData("V0.0.1")]
    [InlineData("1.2.3-rc.1")]
    [InlineData("1.2.3-rc.1+build.5")]
    public void IsValidVersion_AcceptsStrictSemVerWithOptionalPrefix(string version)
    {
        Assert.True(PluginVersionComparer.IsValidVersion(version));
    }
}
