using System.Text.RegularExpressions;
using DshMobile.Runtime.Hosting;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// 生产宿主装配门禁（任务 2）：<c>BridgeHost</c> 必须为 App 内的 DSH 宿主显式启用
/// <c>TrimmedWeb: true</c>，且 <see cref="DshHostOptions"/> 的默认值保持 <c>false</c>（测试与远程路径不受影响）。
/// 同时钉住 <c>DshHost</c> 的两条 spawn 路径都走统一的 argv 构建入口，避免“只裁剪首启、
/// 健康循环原地重启后浏览器前端插件又被加载回来”的回归。
/// </summary>
public sealed class ProductionTrimmedWebGateTests
{
    private static readonly string BridgeHostFile =
        Repo.PathOf("src", "DshMobile.Android", "Bridge", "BridgeHost.cs");

    private static readonly string DshHostFile =
        Repo.PathOf("src", "DshMobile.Runtime", "Hosting", "DshHost.cs");

    [Fact]
    public void BridgeHost_EnablesTrimmedWeb_ForTheProductionHost()
    {
        var source = File.ReadAllText(BridgeHostFile);
        var start = source.IndexOf("new DshMobile.Runtime.Hosting.DshHostOptions(", StringComparison.Ordinal);
        Assert.True(start >= 0, "BridgeHost 必须构造 DshHostOptions");
        var end = source.IndexOf(')', start);
        Assert.True(end > start, "BridgeHost 的 DshHostOptions 构造必须有闭合括号");

        var call = source[start..end];
        Assert.Contains("TrimmedWeb: true", call, StringComparison.Ordinal);
    }

    [Fact]
    public void DshHostOptions_DefaultRemainsUntrimmed_ForTestsAndRemotePaths()
    {
        Assert.False(new DshHostOptions().TrimmedWeb);
    }

    [Fact]
    public void DshHost_AllSpawnPathsGoThroughTheSharedArgvBuilder()
    {
        var source = File.ReadAllText(DshHostFile);

        var sharedCalls = Regex.Count(source, Regex.Escape("BuildDshArgv(_builder, _options"));
        Assert.True(sharedCalls == 2, $"首启与自动重启都应走统一入口，实际 {sharedCalls} 处");
        // 只允许统一入口内部出现一次 DshCommand 调用；两条 spawn 路径不得再各自直接拼 argv。
        var directCalls = Regex.Count(source, Regex.Escape("DshCommand(inPlaceRestart:"));
        Assert.True(directCalls == 1, $"DshCommand 只应在统一入口出现一次，实际 {directCalls} 次");
    }
}
