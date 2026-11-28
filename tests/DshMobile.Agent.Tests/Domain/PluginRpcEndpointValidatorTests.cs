using DshMobile.Agent.Domain;

namespace DshMobile.Agent.Tests.Domain;

/// <summary>插件 SDUI 扩展 RPC 端点白名单校验：合法端点放行、核心命名空间与畸形端点一律拒绝。</summary>
public sealed class PluginRpcEndpointValidatorTests
{
    [Theory]
    [InlineData("demo/echo")]
    [InlineData("acme.corp/tools.run")]
    [InlineData("my-plugin/v1")]
    [InlineData("deepseek_ai/settingsPreview")]
    [InlineData("123/456")]
    [InlineData("a/b")]
    public void IsAllowedExtensionEndpoint_AcceptsTwoSegmentExtensionEndpoints(string endpoint)
    {
        Assert.True(PluginRpcEndpointValidator.IsAllowedExtensionEndpoint(endpoint));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("demo")]
    [InlineData("demo/echo/extra")]
    [InlineData("/echo")]
    [InlineData("demo/")]
    [InlineData("demo//echo")]
    [InlineData("$session/list")]
    [InlineData("demo/$echo")]
    [InlineData("demo/../echo")]
    [InlineData("..")]
    [InlineData("demo/echo\n")]
    [InlineData("demo/ec ho")]
    [InlineData("demo/ec\tho")]
    [InlineData("demo/écho")]
    [InlineData("demo/-echo")]
    [InlineData("demo/.echo")]
    [InlineData("-demo/echo")]
    public void IsAllowedExtensionEndpoint_RejectsMalformedEndpoints(string? endpoint)
    {
        Assert.False(PluginRpcEndpointValidator.IsAllowedExtensionEndpoint(endpoint));
    }

    [Theory]
    [InlineData("session/list")]
    [InlineData("SESSION/list")]
    [InlineData("commands/execute")]
    [InlineData("credentials/set")]
    [InlineData("settings/describe")]
    [InlineData("pluginInventory/list")]
    [InlineData("pluginManager/listBundles")]
    [InlineData("pluginmanager/listBundles")]
    [InlineData("workspace/archiveSession")]
    [InlineData("fs/read")]
    [InlineData("system/info")]
    [InlineData("relay/read")]
    [InlineData("ui/tap")]
    [InlineData("remote/connect")]
    [InlineData("connection/status")]
    [InlineData("webserver/start")]
    [InlineData("typert/describe")]
    public void IsAllowedExtensionEndpoint_BlocksReservedCoreNamespaces(string endpoint)
    {
        Assert.False(PluginRpcEndpointValidator.IsAllowedExtensionEndpoint(endpoint));
    }

    [Fact]
    public void IsAllowedExtensionEndpoint_EnforcesLengthBoundaries()
    {
        // 恰好 128 字符（64 + '/' + 63）仍合法。
        var longestAllowed = new string('a', PluginRpcEndpointValidator.MaxSegmentLength) + "/" +
                             new string('b', PluginRpcEndpointValidator.MaxSegmentLength - 1);
        Assert.Equal(PluginRpcEndpointValidator.MaxEndpointLength, longestAllowed.Length);
        Assert.True(PluginRpcEndpointValidator.IsAllowedExtensionEndpoint(longestAllowed));

        // 129 字符（64 + '/' + 64）超总长上限。
        var overTotalLength = new string('a', 64) + "/" + new string('b', 64);
        Assert.Equal(PluginRpcEndpointValidator.MaxEndpointLength + 1, overTotalLength.Length);
        Assert.False(PluginRpcEndpointValidator.IsAllowedExtensionEndpoint(overTotalLength));

        // 单段 65 字符超段长上限。
        var overSegmentLength = "demo/" + new string('c', PluginRpcEndpointValidator.MaxSegmentLength + 1);
        Assert.False(PluginRpcEndpointValidator.IsAllowedExtensionEndpoint(overSegmentLength));
    }
}
