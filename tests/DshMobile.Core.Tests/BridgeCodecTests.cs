using System.Text.Json;
using DshMobile.Core.Bridge;

namespace DshMobile.Core.Tests;

public class BridgeCodecTests
{
    [Fact]
    public void EncodeRequest_UsesProtocolV1FieldNames()
    {
        var payload = BridgeCodec.ToPayload(new VersionsCompareRequest("1.0.0", "1.0.0-rc.1"), BridgeJsonContext.Default.VersionsCompareRequest);
        var json = BridgeCodec.EncodeRequest(new BridgeRequest(1, "id-1", BridgeMethods.DshboxVersionsCompare, 1500, payload));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("protocol").GetInt32());
        Assert.Equal("id-1", root.GetProperty("requestId").GetString());
        Assert.Equal("dshbox.versions.compare", root.GetProperty("method").GetString());
        Assert.Equal(1500, root.GetProperty("deadlineMs").GetInt32());
        Assert.Equal("1.0.0-rc.1", root.GetProperty("payload").GetProperty("b").GetString());
        Assert.Equal(5, root.EnumerateObject().Count());
    }

    [Fact]
    public void Decode_SuccessResponse()
    {
        const string json = """{"protocol":1,"requestId":"r","ok":true,"code":"OK","message":null,"payload":{"result":1}}""";
        Assert.True(BridgeCodec.TryDecodeInbound(json, out var resp, out var evt));
        Assert.Null(evt);
        Assert.NotNull(resp);
        Assert.True(resp.Ok);
        Assert.Equal(1, BridgeCodec.ReadPayload(resp, BridgeJsonContext.Default.VersionsCompareResult)!.Result);
    }

    [Fact]
    public void Decode_RelayStatusWithUiTools()
    {
        // Shape produced by RuntimeController.relayStatus() (TASK-010).
        const string json = """
            {"protocol":1,"requestId":"r","ok":true,"code":"OK","message":null,"payload":{
              "phase":"STARTING","channelRunning":false,"degradedReason":null,"accessibilityEnabled":true,
              "allowed":["ui.node","ui.snapshot"],"guestCli":"/opt/pilot/bin/relay",
              "uiTools":{"registered":true,"loadedByDsh":null,"dshRestartNeeded":false,"conflict":false,"serverName":"ui"}}}
            """;
        Assert.True(BridgeCodec.TryDecodeInbound(json, out var resp, out _));
        var s = BridgeCodec.ReadPayload(resp!, BridgeJsonContext.Default.RelayStatus)!;
        Assert.Equal(["ui.node", "ui.snapshot"], s.Allowed);
        Assert.NotNull(s.UiTools);
        Assert.True(s.UiTools.Registered);
        Assert.Null(s.UiTools.LoadedByDsh);
        Assert.False(s.UiTools.DshRestartNeeded);
        Assert.Equal("ui", s.UiTools.ServerName);
    }

    [Fact]
    public void Decode_BrowserStatus()
    {
        // Shape produced by RuntimeController.browserStatus() (TASK-011).
        const string json = """
            {"protocol":1,"requestId":"r","ok":true,"code":"OK","message":null,"payload":{
              "brokerAvailable":true,"brokerRunning":true,"cliReady":true,"channelPublished":true,
              "adapterInstalled":false,"lastError":null,"guestCli":"/opt/browser/bin/browser",
              "sandboxState":"RUNNING","toolsLoadedByDsh":false,"dshRestartNeeded":false}}
            """;
        Assert.True(BridgeCodec.TryDecodeInbound(json, out var resp, out _));
        var s = BridgeCodec.ReadPayload(resp!, BridgeJsonContext.Default.BrowserStatusInfo)!;
        Assert.True(s.BrokerRunning);
        Assert.False(s.AdapterInstalled);
        Assert.Equal(false, s.ToolsLoadedByDsh);
        Assert.Equal("/opt/browser/bin/browser", s.GuestCli);
    }

    [Fact]
    public void Decode_RemoteStatus_HostKeyUnknown_CarriesFingerprintButNoToken()
    {
        // Shape produced by RemoteHosts.connect() on an unconfirmed host key (TASK-012).
        const string json = """
            {"protocol":1,"requestId":"r","ok":true,"code":"OK","message":null,"payload":{
              "state":"DISCONNECTED","code":"HOST_KEY_UNKNOWN","message":"confirm the host key fingerprint before connecting",
              "hostId":"h1","label":"pc","probe":null,"localPort":null,"dshPort":3091,"hasLaunchToken":false,
              "fingerprint":"SHA256:/O0DopfcupCOPu5e6/7zGNQITxFKE51poNy7jxKvT5Y","keyType":"ecdsa-sha2-nistp256"}}
            """;
        Assert.True(BridgeCodec.TryDecodeInbound(json, out var resp, out _));
        var s = BridgeCodec.ReadPayload(resp!, BridgeJsonContext.Default.RemoteStatusInfo)!;
        Assert.Equal(RemoteCodes.HostKeyUnknown, s.Code);
        Assert.Equal("SHA256:/O0DopfcupCOPu5e6/7zGNQITxFKE51poNy7jxKvT5Y", s.Fingerprint);
        Assert.False(s.HasLaunchToken);
        Assert.Null(s.Probe);
        Assert.DoesNotContain("token", json.Replace("hasLaunchToken", ""), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decode_RemoteStatus_Ready()
    {
        const string json = """
            {"protocol":1,"requestId":"r","ok":true,"code":"OK","message":null,"payload":{
              "state":"READY","code":"OK","message":null,"hostId":"h1","label":"pc",
              "probe":{"node":true,"nodeVersion":"v26.13.0","dsh":true,"dshVersion":"0.2.1-rc.5"},
              "localPort":40123,"dshPort":3091,"hasLaunchToken":true,"httpUnauthenticated":401}}
            """;
        Assert.True(BridgeCodec.TryDecodeInbound(json, out var resp, out _));
        var s = BridgeCodec.ReadPayload(resp!, BridgeJsonContext.Default.RemoteStatusInfo)!;
        Assert.Equal("READY", s.State);
        Assert.True(s.Probe!.Dsh);
        Assert.Equal(401, s.HttpUnauthenticated);
        Assert.Equal(40123, s.LocalPort);
    }

    [Fact]
    public void Decode_RelayStatusWithoutUiTools_IsNull()
    {
        const string json = """{"protocol":1,"requestId":"r","ok":true,"code":"OK","message":null,"payload":{"phase":"STOPPED","channelRunning":false,"degradedReason":null,"accessibilityEnabled":false,"allowed":[],"guestCli":"/opt/pilot/bin/relay"}}""";
        Assert.True(BridgeCodec.TryDecodeInbound(json, out var resp, out _));
        Assert.Null(BridgeCodec.ReadPayload(resp!, BridgeJsonContext.Default.RelayStatus)!.UiTools);
    }

    [Fact]
    public void Decode_ErrorResponseWithNullPayload()
    {
        const string json = """{"protocol":1,"requestId":null,"ok":false,"code":"BAD_REQUEST","message":"invalid JSON","payload":null}""";
        Assert.True(BridgeCodec.TryDecodeInbound(json, out var resp, out _));
        Assert.False(resp!.Ok);
        Assert.Null(resp.RequestId);
        Assert.Null(BridgeCodec.ReadPayload(resp, BridgeJsonContext.Default.BridgeInfo));
    }

    [Fact]
    public void Decode_Event()
    {
        const string json = """{"protocol":1,"type":"event","event":"bridge.lifecycle","payload":{"state":"INITIALIZED"}}""";
        Assert.True(BridgeCodec.TryDecodeInbound(json, out var resp, out var evt));
        Assert.Null(resp);
        Assert.Equal("bridge.lifecycle", evt!.Event);
        Assert.Equal("INITIALIZED", evt.Payload!.Value.GetProperty("state").GetString());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"protocol":2,"requestId":"r","ok":true,"code":"OK"}""")]
    [InlineData("""{"protocol":1.5,"requestId":"r","ok":true,"code":"OK"}""")]
    [InlineData("""{"protocol":1,"requestId":"r","code":"OK"}""")]
    [InlineData("""{"protocol":1,"requestId":"r","ok":"yes","code":"OK"}""")]
    // ok/code mismatch is a contract violation, never trusted as success.
    [InlineData("""{"protocol":1,"requestId":"r","ok":true,"code":"INTERNAL"}""")]
    [InlineData("""{"protocol":1,"requestId":"r","ok":false,"code":"OK"}""")]
    [InlineData("""{"protocol":1,"type":"event","event":""}""")]
    public void Decode_RejectsMalformed(string json)
    {
        Assert.False(BridgeCodec.TryDecodeInbound(json, out _, out _));
    }

    [Fact]
    public void Decode_BridgeInfoPayload()
    {
        const string json = """
            {"protocol":1,"requestId":"r","ok":true,"code":"OK","message":null,
             "payload":{"bridgeVersion":"0.1.0","protocol":1,"upstream":{"dshbox":"v1.5.1-0-g7c9d33c"},
                        "kotlinVersion":"2.4.20","sdkInt":34,"abis":["arm64-v8a"],"deviceModel":"Pixel"}}
            """;
        Assert.True(BridgeCodec.TryDecodeInbound(json, out var resp, out _));
        var info = BridgeCodec.ReadPayload(resp!, BridgeJsonContext.Default.BridgeInfo)!;
        Assert.Equal("v1.5.1-0-g7c9d33c", info.Upstream["dshbox"]);
        Assert.Equal(["arm64-v8a"], info.Abis);
    }
}
