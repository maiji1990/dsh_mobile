using System.Text.Json;
using DshMobile.Core.Bridge;
using DshMobile.Runtime.Legacy;

namespace DshMobile.Runtime.Tests;

public class LegacyBridgeRuntimeManagerTests
{
    /// <summary>Answers every request from a method -> response JSON table, asynchronously.</summary>
    private sealed class ScriptedTransport(Func<string, string> reply) : IBridgeTransport
    {
        private IBridgeSink? _sink;
        public readonly List<string> Methods = [];

        public void Attach(IBridgeSink sink) => _sink = sink;

        public void Send(string requestJson)
        {
            var req = JsonSerializer.Deserialize(requestJson, BridgeJsonContext.Default.BridgeRequest)!;
            lock (Methods)
            {
                Methods.Add(req.Method);
            }
            var body = reply(req.Method);
            ThreadPool.QueueUserWorkItem(_ => _sink!.OnInbound($$$"""{"protocol":1,"requestId":"{{{req.RequestId}}}",{{{body}}}}"""));
        }
    }

    private static string Status(string sandbox, string dsh, bool installed = true, string version = "\"0.2.1-rc.5\"") =>
        $$$""" "ok":true,"code":"OK","message":null,"payload":{"sandboxState":"{{{sandbox}}}","dshState":"{{{dsh}}}","dshVersion":{{{version}}},"dshUnresponsive":false,"runtimeInstalled":{{{(installed ? "true" : "false")}}},"nodeLayerInstalled":true,"hasLaunchToken":false,"linkShim":true,"endpoint":"http://127.0.0.1:3080","freeBytes":1}""";

    [Theory]
    [InlineData("RUNNING", "READY", true, RuntimeState.Running, HarnessState.Ready)]
    [InlineData("STOPPED", "STOPPED", true, RuntimeState.Ready, HarnessState.Stopped)]
    [InlineData("UNINITIALIZED", "UNINITIALIZED", false, RuntimeState.NotInstalled, HarnessState.Stopped)]
    [InlineData("ERROR", "STOPPED", true, RuntimeState.Failed, HarnessState.Stopped)]
    [InlineData("STARTING", "STOPPED", true, RuntimeState.Starting, HarnessState.Stopped)]
    [InlineData("RUNNING", "ERROR", true, RuntimeState.Running, HarnessState.Failed)]
    public async Task Status_MapsLegacyEnums(string sandbox, string dsh, bool installed, RuntimeState state, HarnessState harness)
    {
        using var client = new BridgeClient(new ScriptedTransport(_ => Status(sandbox, dsh, installed)));
        var s = await new LegacyBridgeRuntimeManager(client).GetStateAsync();
        Assert.Equal(state, s.State);
        Assert.Equal(harness, s.Harness);
    }

    [Fact]
    public async Task UnknownLegacyState_IsFailureNeverSuccess()
    {
        using var client = new BridgeClient(new ScriptedTransport(_ => Status("HIBERNATING", "READY")));
        var r = await new LegacyBridgeRuntimeManager(client).StartSandboxAsync(CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(BridgeErrorCodes.Internal, r.Code);
    }

    [Fact]
    public async Task UpstreamFailure_KeepsUpstreamCode()
    {
        using var client = new BridgeClient(new ScriptedTransport(_ =>
            """ "ok":false,"code":"RUNTIME_ERROR","message":"BUNDLE_SHA256_MISMATCH: Layer base SHA-256 verification failed","payload":{"upstreamCode":"BUNDLE_SHA256_MISMATCH","recoverable":false}"""));
        var r = await new LegacyBridgeRuntimeManager(client).InstallAsync(new RuntimePackage("/x.zip"), null, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal("RUNTIME_ERROR", r.Code);
        Assert.Equal("BUNDLE_SHA256_MISMATCH", r.UpstreamCode);
    }

    [Fact]
    public async Task NonWebProfile_IsNotImplementedWithoutCallingDshStart()
    {
        var transport = new ScriptedTransport(_ => Status("RUNNING", "STOPPED"));
        using var client = new BridgeClient(transport);
        var r = await new LegacyBridgeRuntimeManager(client).StartHarnessAsync(new DshProfile("acp"), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(RuntimeErrorCodes.NotImplemented, r.Code);
        Assert.DoesNotContain(BridgeMethods.DshStart, transport.Methods);
    }

    [Fact]
    public async Task Methods_UseTheLegacyBridgeNames()
    {
        var transport = new ScriptedTransport(_ => Status("RUNNING", "READY"));
        using var client = new BridgeClient(transport);
        var m = new LegacyBridgeRuntimeManager(client);
        await m.StartSandboxAsync(CancellationToken.None);
        await m.StartHarnessAsync(DshProfile.Web, CancellationToken.None);
        await m.StopHarnessAsync(CancellationToken.None);
        await m.StopSandboxAsync(CancellationToken.None);
        Assert.Equal([BridgeMethods.SandboxStart, BridgeMethods.DshStart, BridgeMethods.DshStop, BridgeMethods.SandboxStop], transport.Methods);
    }
}
