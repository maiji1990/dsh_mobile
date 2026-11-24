using System.Net.WebSockets;
using System.Text.Json;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Protocol;
using DshMobile.Agent.Transport;

namespace DshMobile.Agent.Tests.Transport;

/// <summary>PR-08 Agent 契约测试：动态 SSH 隧道回环地址校验、断线切换重连，以及 exit 137 / MemoryLimiter 领域错码映射。</summary>
public sealed class RemoteGatewayAdapterTests
{
    private static void ConfigureFakeRemote(FakeRemoteServer server, string cookieValue, string clientId, string hostHome)
    {
        server.OnHttpRequest = req =>
        {
            if (req.Path == "/connect")
            {
                return Task.FromResult(new FakeRemoteServer.HttpResponseData(
                    302,
                    "Found",
                    [
                        ("Location", "/"),
                        ("Set-Cookie", $"dsh-auth-{server.Port}={cookieValue}; Path=/; HttpOnly; SameSite=Strict"),
                    ],
                    []));
            }
            return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(404, "{}"));
        };
        server.OnWebSocketConnected = async (_, ws, ct) =>
        {
            var openRaw = await FakeRemoteServer.ReceiveTextAsync(ws, ct).ConfigureAwait(false);
            if (openRaw is null)
            {
                return;
            }
            using var doc = JsonDocument.Parse(openRaw);
            var streamId = doc.RootElement.GetProperty("streamId").GetString()!;
            var ready = new RemoteEventsReadyPayload("ready", clientId, new RemoteHostInfo(hostHome));
            var readyEl = JsonSerializer.SerializeToElement(ready, AgentProtocolJsonContext.Default.RemoteEventsReadyPayload);
            await FakeRemoteServer.SendTextAsync(ws, RemoteWireCodec.EncodeMuxItem(streamId, readyEl), ct).ConfigureAwait(false);
            await FakeRemoteServer.ReceiveTextAsync(ws, ct).ConfigureAwait(false);
        };
    }

    [Fact]
    public void DshFailureMapper_MapsPhantomProcessLimit_AndMemoryLimited()
    {
        var exit137 = DshFailureMapper.FromProcessExitCode(137);
        Assert.Equal(DshFailureCode.PhantomProcessLimit, exit137.Code);
        Assert.False(exit137.Retryable);

        var phantom = DshFailureMapper.FromRuntimeErrorCode("PHANTOM_PROCESS_LIMIT");
        Assert.Equal(DshFailureCode.PhantomProcessLimit, phantom.Code);

        var memory = DshFailureMapper.FromRuntimeErrorCode("MEMORY_LIMITED");
        Assert.Equal(DshFailureCode.MemoryLimited, memory.Code);
    }

    [Fact]
    public void ValidateLoopbackBaseUri_RejectsNonLoopbackAndQueryOrCredentials()
    {
        var valid = RemoteConnectionOptions.ValidateLoopbackBaseUri(new Uri("http://127.0.0.1:43210/ignored"));
        Assert.Equal("http://127.0.0.1:43210/", valid.AbsoluteUri);

        Assert.Throws<ArgumentException>(() => RemoteConnectionOptions.ValidateLoopbackBaseUri(new Uri("http://192.168.1.20:3080/")));
        Assert.Throws<ArgumentException>(() => RemoteConnectionOptions.ValidateLoopbackBaseUri(new Uri("http://user:pass@127.0.0.1:3080/")));
        Assert.Throws<ArgumentException>(() => RemoteConnectionOptions.ValidateLoopbackBaseUri(new Uri("http://127.0.0.1:3080/?x=1")));
    }

    [Fact]
    public async Task DshRemoteConnection_FollowsDynamicSshTunnelPort_OnReconnect()
    {
        await using var firstServer = new FakeRemoteServer();
        await using var secondServer = new FakeRemoteServer();

        ConfigureFakeRemote(firstServer, "sess-1", "c-remote-1", "/srv/remote-1");
        ConfigureFakeRemote(secondServer, "sess-2", "c-remote-2", "/srv/remote-2");

        var activeUri = firstServer.BaseUri;
        var activeToken = "ssh-token-1";

        var options = new RemoteConnectionOptions(new Uri("http://127.0.0.1:3080/"))
        {
            BackoffBase = TimeSpan.FromMilliseconds(20),
            BackoffMax = TimeSpan.FromMilliseconds(50),
        };
        await using var connection = new DshRemoteConnection(
            options,
            () => activeToken,
            baseUriProvider: () => activeUri);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var gen1 = await connection.ConnectAsync(cts.Token);
        Assert.Equal("c-remote-1", gen1.ClientId);
        Assert.Equal("/srv/remote-1", gen1.HostHome);

        // 模拟 SSH 断线后重新建立新端口转发与新启动令牌，并校验重连切到第二个隧道端点。
        var secondReady = new TaskCompletionSource<ConnectionGeneration>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.GenerationChanged += g =>
        {
            if (g is { ClientId: "c-remote-2" })
            {
                secondReady.TrySetResult(g);
            }
        };

        activeUri = secondServer.BaseUri;
        activeToken = "ssh-token-2";
        connection.RequestReconnect();

        var gen2 = await secondReady.Task.WaitAsync(cts.Token);
        Assert.Equal("c-remote-2", gen2.ClientId);
        Assert.Equal("/srv/remote-2", gen2.HostHome);
    }
}
