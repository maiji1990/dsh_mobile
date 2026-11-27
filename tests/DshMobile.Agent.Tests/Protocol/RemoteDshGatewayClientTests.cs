using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Protocol;
using DshMobile.Agent.Tests.Transport;
using DshMobile.Agent.Transport;

namespace DshMobile.Agent.Tests.Protocol;

public sealed class RemoteDshGatewayClientTests
{
    private const string DummyToken = "gateway-token-123";

    private static string FixturePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }
        return Path.Combine(dir!.FullName, "tests", "DshMobile.Agent.Tests", "Fixtures", "route-b-golden-v0.2.2-rc.5.json");
    }

    private static string ReadyFrame(string streamId, string clientId = "c-gw")
    {
        var payload = new RemoteEventsReadyPayload("ready", clientId, new RemoteHostInfo("/home/sanitized"));
        var valueEl = JsonSerializer.SerializeToElement(payload, AgentProtocolJsonContext.Default.RemoteEventsReadyPayload);
        return RemoteWireCodec.EncodeMuxItem(streamId, valueEl);
    }

    private static string ParseRpcId(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("rpcId").GetString()!;
    }

    private static string WrapServerResponse(string rpcId, JsonElement resultObj) =>
        "{\"type\":\"server-response\",\"rpcId\":\"" + rpcId + "\",\"result\":" + resultObj.GetRawText() + "}";

    private static string WrapOk(string rpcId, string valueJson) =>
        "{\"type\":\"server-response\",\"rpcId\":\"" + rpcId + "\",\"result\":{\"ok\":true,\"value\":" + valueJson + "}}";

    private static string WrapErr(string rpcId, string code, string message) =>
        "{\"type\":\"server-response\",\"rpcId\":\"" + rpcId + "\",\"result\":{\"ok\":false,\"error\":{\"code\":\"" + code + "\",\"message\":\"" + message + "\"}}}";

    [Fact]
    public void GoldenFixture_DecodesAllRealWireShapesAndDegradesUnknownGracefully()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FixturePath()));
        var root = doc.RootElement;

        var ready = RemoteWireCodec.TryDecodeEventsReady(root.GetProperty("eventsReady").GetProperty("value"));
        Assert.Equal("ready", ready!.Type);

        var sessionCreate = root.GetProperty("sessionCreate").GetProperty("result").GetProperty("value")
            .Deserialize(AgentProtocolJsonContext.Default.WireSessionCreateValue);
        Assert.Equal("session-00000000-0000-4000-8000-000000000010", sessionCreate!.SessionId);

        var list = root.GetProperty("sessionList").GetProperty("result").GetProperty("value")
            .Deserialize(AgentProtocolJsonContext.Default.WireSessionListValue);
        Assert.Single(list!.Items);

        var follow = RemoteProtocolCodec.DecodeFollowFrame(root.GetProperty("followSnapshot").GetProperty("value"));
        var snap = Assert.IsType<SessionFollowSnapshotUpdate>(follow);
        Assert.Equal(2, snap.Events.Count);
        Assert.Equal("turn/end", snap.Events[0].Type);
        Assert.True(snap.Events[0].IsKnownType);
        Assert.Equal("future/telemetry-marker", snap.Events[1].Type);
        Assert.False(snap.Events[1].IsKnownType);

        var turnEnd = RemoteProtocolCodec.ParseTurnEndReason(snap.Events[0].Data);
        Assert.Equal(TurnEndReasonKind.Error, turnEnd.Kind);
        Assert.True(turnEnd.IsFailure);
        Assert.Equal("PROVIDER_UNAVAILABLE", turnEnd.ErrorCode);

        var forwarded = RemoteWireCodec.TryDecodeForwardedEvent(root.GetProperty("forwardedUnknown"));
        var decodedUnknown = RemoteProtocolCodec.DecodeForwardedEvent(forwarded!);
        Assert.IsType<UnknownGatewayEvent>(decodedUnknown);

        var plugins = root.GetProperty("pluginInventory").GetProperty("result").GetProperty("value")
            .Deserialize(AgentProtocolJsonContext.Default.WirePluginInventorySnapshot);
        Assert.Equal(2, plugins!.Entries.Count);
        Assert.Null(RemoteProtocolCodec.ResolveLocalizedText(plugins.Entries[0].Meta?.Description));
        Assert.Equal("示例插件", RemoteProtocolCodec.ResolveLocalizedText(plugins.Entries[1].Meta?.Description));

        var variants = root.GetProperty("turnEndVariants").EnumerateArray().ToList();
        Assert.Equal(TurnEndReasonKind.Completed, RemoteProtocolCodec.ParseTurnEndReason(variants[0]).Kind);
        Assert.False(RemoteProtocolCodec.ParseTurnEndReason(variants[0]).IsFailure);
        Assert.Equal(TurnEndReasonKind.Aborted, RemoteProtocolCodec.ParseTurnEndReason(variants[1]).Kind);
        Assert.True(RemoteProtocolCodec.ParseTurnEndReason(variants[1]).IsFailure);
        Assert.Equal(TurnEndReasonKind.Error, RemoteProtocolCodec.ParseTurnEndReason(variants[2]).Kind);
        Assert.Equal(TurnEndReasonKind.MaxTokens, RemoteProtocolCodec.ParseTurnEndReason(variants[3]).Kind);
        Assert.Equal(TurnEndReasonKind.Interrupted, RemoteProtocolCodec.ParseTurnEndReason(variants[4]).Kind);
        Assert.Equal(TurnEndReasonKind.Unknown, RemoteProtocolCodec.ParseTurnEndReason(variants[5]).Kind);

        var archivedRes = root.GetProperty("workspaceArchiveSession").GetProperty("result").GetProperty("value")
            .Deserialize(AgentProtocolJsonContext.Default.WireWorkspaceArchivedValue);
        Assert.Equal(["session-00000000-0000-4000-8000-000000000010"], archivedRes!.ArchivedSessionIds);

        var unarchivedRes = root.GetProperty("workspaceUnarchiveSession").GetProperty("result").GetProperty("value")
            .Deserialize(AgentProtocolJsonContext.Default.WireWorkspaceArchivedValue);
        Assert.Empty(unarchivedRes!.ArchivedSessionIds);

        var wsBaseline = Assert.IsType<WorkspaceBaselineFrame>(
            RemoteProtocolCodec.DecodeWorkspaceFollowFrame(root.GetProperty("workspaceFollowBaseline").GetProperty("value")));
        Assert.Single(wsBaseline.ArchivedSessionIds);
        Assert.Empty(wsBaseline.PinnedSessionIds);

        var wsArchived = Assert.IsType<WorkspaceArchivedChangedFrame>(
            RemoteProtocolCodec.DecodeWorkspaceFollowFrame(root.GetProperty("workspaceFollowArchived").GetProperty("value")));
        Assert.Equal(2, wsArchived.ArchivedSessionIds.Count);

        var wsReplace = Assert.IsType<WorkspaceReplacedFrame>(
            RemoteProtocolCodec.DecodeWorkspaceFollowFrame(root.GetProperty("workspaceFollowReplace").GetProperty("value")));
        Assert.Empty(wsReplace.ArchivedSessionIds);
        Assert.Single(wsReplace.PinnedSessionIds);

        var wsUnknown = Assert.IsType<UnknownWorkspaceFollowFrame>(
            RemoteProtocolCodec.DecodeWorkspaceFollowFrame(root.GetProperty("workspaceFollowUnknown").GetProperty("value")));
        Assert.Equal("future-frame-v2", wsUnknown.RawType);
    }

    [Fact]
    public async Task GatewayClient_ProbesCapabilities_SendsStrictSchemas_AndHandlesWaitQuirkAndWaterfallReplies()
    {
        using var fixtureDoc = JsonDocument.Parse(File.ReadAllText(FixturePath()));
        var fx = fixtureDoc.RootElement;

        await using var server = new FakeRemoteServer();
        string? capturedPromptJson = null;
        string? capturedCreateJson = null;
        string? capturedCmdJson = null;
        string? capturedEventResultJson = null;
        string? capturedSettingsJson = null;

        server.OnHttpRequest = req =>
        {
            if (req.Path == "/connect")
            {
                return Task.FromResult(new FakeRemoteServer.HttpResponseData(
                    302, "Found", [("Location", "/"), ("Set-Cookie", $"dsh-auth-{server.Port}=ck-gw; HttpOnly")], []));
            }

            var rpcId = ParseRpcId(req.Body);
            if (req.Path == "/api/session/list")
            {
                return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(200,
                    WrapServerResponse(rpcId, fx.GetProperty("sessionList").GetProperty("result"))));
            }
            if (req.Path == "/api/session/modelCatalog")
            {
                return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(200,
                    WrapOk(rpcId, """{"default":{"provider":"deepseek-official","model":"deepseek-flash"},"routableProviders":["deepseek-official"],"groups":[],"failures":[]}""")));
            }
            if (req.Path == "/api/credentials/describe")
            {
                return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(200,
                    WrapServerResponse(rpcId, fx.GetProperty("credentialsDescribe").GetProperty("result"))));
            }
            if (req.Path == "/api/settings/describe")
            {
                capturedSettingsJson = Encoding.UTF8.GetString(req.Body);
                return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(200,
                    WrapOk(rpcId, """{"writable":true,"namespaces":[{"ns":"models","value":{"providers":{"custom":{"apiKeyEnv":"CUSTOM_ENV_KEY"}}}}]}""")));
            }
            if (req.Path == "/api/pluginInventory/list")
            {
                // Simulate missing plugin inventory capability dynamically (`N-02-02`)
                return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(200,
                    WrapErr(rpcId, "unsupported", "disabled by patch")));
            }
            if (req.Path == "/api/session/create")
            {
                capturedCreateJson = Encoding.UTF8.GetString(req.Body);
                return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(200,
                    WrapServerResponse(rpcId, fx.GetProperty("sessionCreate").GetProperty("result"))));
            }
            if (req.Path == "/api/session/prompt")
            {
                capturedPromptJson = Encoding.UTF8.GetString(req.Body);
                return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(200,
                    WrapOk(rpcId, """{"accepted":true}""")));
            }
            if (req.Path == "/api/commands/list")
            {
                return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(200,
                    WrapServerResponse(rpcId, fx.GetProperty("commandsList").GetProperty("result"))));
            }
            if (req.Path == "/api/commands/execute")
            {
                capturedCmdJson = Encoding.UTF8.GetString(req.Body);
                return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(200,
                    WrapServerResponse(rpcId, fx.GetProperty("commandExecute").GetProperty("result"))));
            }
            if (req.Path == "/api/session/wait")
            {
                // Simulate empty `{}` quirk (`N-02-03`)
                return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(200,
                    WrapOk(rpcId, "{}")));
            }
            if (req.Path == "/api/$events/result")
            {
                capturedEventResultJson = Encoding.UTF8.GetString(req.Body);
                return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(200,
                    "{\"type\":\"server-response\",\"rpcId\":\"" + rpcId + "\",\"result\":{\"ok\":true}}"));
            }

            return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(404, "{}"));
        };

        server.OnWebSocketConnected = async (_, ws, ct) =>
        {
            var openEv = await FakeRemoteServer.ReceiveTextAsync(ws, ct);
            using var evDoc = JsonDocument.Parse(openEv!);
            var evSid = evDoc.RootElement.GetProperty("streamId").GetString()!;
            await FakeRemoteServer.SendTextAsync(ws, ReadyFrame(evSid, "c-gw"), ct);

            // Emit a forwarded approval request
            await FakeRemoteServer.SendTextAsync(ws,
                "{\"type\":\"item\",\"streamId\":\"" + evSid + "\",\"value\":{\"type\":\"waterfall\",\"event\":\"approval/request\",\"eventId\":\"ap-1\",\"agentId\":\"s-1\",\"args\":{\"toolName\":\"bash\",\"callId\":\"call-9\",\"reason\":\"run tests\"}}}",
                ct);

            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var raw = await FakeRemoteServer.ReceiveTextAsync(ws, ct);
                if (raw is null)
                {
                    break;
                }
                using var frame = JsonDocument.Parse(raw);
                if (frame.RootElement.GetProperty("type").GetString() == "open" &&
                    frame.RootElement.GetProperty("endpoint").GetString() == "session/follow")
                {
                    var flSid = frame.RootElement.GetProperty("streamId").GetString()!;
                    var snapVal = fx.GetProperty("followSnapshot").GetProperty("value").GetRawText();
                    await FakeRemoteServer.SendTextAsync(ws,
                        $$$"""{"type":"item","streamId":"{{{flSid}}}","value":{{{snapVal}}}}""", ct);
                }
            }
        };

        await using var conn = new DshRemoteConnection(new RemoteConnectionOptions(server.BaseUri), () => DummyToken);
        using var gw = new RemoteDshGatewayClient(conn, timeZoneProvider: () => "Asia/Shanghai");

        var approvalTcs = new TaskCompletionSource<ApprovalRequestView>(TaskCreationOptions.RunContinuationsAsynchronously);
        gw.ForwardedEvent += evt =>
        {
            if (evt is ApprovalGatewayEvent ap)
            {
                approvalTcs.TrySetResult(ap.Request);
            }
        };

        // 1. Dynamic capability probe
        var capsOutcome = await gw.ProbeCapabilitiesAsync();
        Assert.True(capsOutcome.IsOk);
        Assert.True(capsOutcome.Value!.Prompt);
        Assert.True(capsOutcome.Value.ListSessions);
        Assert.True(capsOutcome.Value.SwitchModelInSession);
        Assert.True(capsOutcome.Value.ManageCredentials);
        Assert.False(capsOutcome.Value.PluginInventory); // probed dynamically!

        // 2. Strict session create/resume schema (only non-null sessionId, zero unknown fields)
        var resumed = await gw.CreateOrResumeSessionAsync(new CreateOrResumeSessionRequest(SessionId: "s-existing"));
        Assert.True(resumed.IsOk);
        using var createDoc = JsonDocument.Parse(capturedCreateJson!);
        var createReqProps = createDoc.RootElement.GetProperty("payload").GetProperty("args").GetProperty("request")
            .EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["sessionId"], createReqProps);

        // 3. Prompt with text + image + auto IANA timeZone injection
        var pngB64 = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47]);
        var sendOk = await gw.SendPromptAsync(new PromptSendRequest(
            "req-1",
            "s-1",
            "Inspect screenshot",
            Images: [new PromptAttachmentImage("image/png", pngB64, "screen.png")]));
        Assert.True(sendOk.IsOk);
        using var promptDoc = JsonDocument.Parse(capturedPromptJson!);
        var promptReq = promptDoc.RootElement.GetProperty("payload").GetProperty("args").GetProperty("request");
        Assert.Equal("Asia/Shanghai", promptReq.GetProperty("clientTimeZone").GetString());
        Assert.Equal(2, promptReq.GetProperty("content").GetArrayLength());
        Assert.Equal("image/png", promptReq.GetProperty("content")[1].GetProperty("mediaType").GetString());

        // Oversized/bad MIME image fails locally before touching HTTP
        var badMime = await gw.SendPromptAsync(new PromptSendRequest(
            "req-2",
            "s-1",
            "Bad",
            Images: [new PromptAttachmentImage("image/bmp", pngB64)]));
        Assert.False(badMime.IsOk);
        Assert.Equal(DshFailureCode.InvalidState, badMime.Error!.Code);

        // 4. Native slash commands (`/compact`, `/plan`, `/permission`, `/goal`)
        var cmds = await gw.ListCommandsAsync("s-1");
        Assert.True(cmds.IsOk);
        Assert.Contains(cmds.Value!, c => c.Name == "compact");
        var exec = await gw.ExecuteCommandAsync("s-1", "/compact");
        Assert.True(exec.IsOk);
        Assert.True(exec.Value!.Succeeded);
        Assert.Contains("\"line\":\"/compact\"", capturedCmdJson!, StringComparison.Ordinal);

        // 5. `session/wait` returning empty `{}` maps cleanly to AmbiguousEmptyResponse
        var waitRes = await gw.WaitForSessionAsync("s-1");
        Assert.True(waitRes.IsOk);
        Assert.Equal(SessionWaitOutcomeKind.AmbiguousEmptyResponse, waitRes.Value!.Kind);

        // 6. Forwarded approval waterfall & fail-closed reply
        var apReq = await approvalTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("bash", apReq.ToolName);
        Assert.Equal("call-9", apReq.CallId);
        var replyOk = await gw.ReplyApprovalAsync(conn.CurrentGeneration!.ClientId, apReq.EventId, ApprovalDecision.AllowedOnce);
        Assert.True(replyOk.IsOk);
        Assert.Contains("\"allowed-once\"", capturedEventResultJson!, StringComparison.Ordinal);

        // 7. Session follow delivers snapshot + unknown event without throwing
        var followTcs = new TaskCompletionSource<SessionFollowSnapshotUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var followOutcome = await gw.FollowSessionAsync("s-1", u =>
        {
            if (u is SessionFollowSnapshotUpdate snapUpdate)
            {
                followTcs.TrySetResult(snapUpdate);
            }
            return ValueTask.CompletedTask;
        });
        Assert.True(followOutcome.IsOk);
        var followedSnap = await followTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, followedSnap.Events.Count);
        await followOutcome.Value!.DisposeAsync();

        // 8. `settings/describe` sends empty `{}` args and extracts `DEEPSEEK_API_KEY` + custom `apiKeyEnv` (F15)
        var discoveredRefs = await gw.DiscoverCredentialRefsAsync();
        Assert.True(discoveredRefs.IsOk);
        Assert.Contains("DEEPSEEK_API_KEY", discoveredRefs.Value!);
        Assert.Contains("CUSTOM_ENV_KEY", discoveredRefs.Value!);
        using var settingsDoc = JsonDocument.Parse(capturedSettingsJson!);
        Assert.Empty(settingsDoc.RootElement.GetProperty("payload").GetProperty("args").EnumerateObject());
    }

    [Fact]
    public async Task GatewayClient_RejectsSchemaMismatchCleanlyWithProtocolMismatch()
    {
        await using var server = new FakeRemoteServer();
        server.OnHttpRequest = req =>
        {
            if (req.Path == "/connect")
            {
                return Task.FromResult(new FakeRemoteServer.HttpResponseData(
                    302, "Found", [("Location", "/"), ("Set-Cookie", $"dsh-auth-{server.Port}=ck-mismatch; HttpOnly")], []));
            }
            var rpcId = ParseRpcId(req.Body);
            if (req.Path == "/api/pluginInventory/list")
            {
                // Intentionally incompatible entries type (number instead of array)
                return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(200,
                    WrapOk(rpcId, """{"managementAvailable":false,"entries":42}""")));
            }
            return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(404, "{}"));
        };
        server.OnWebSocketConnected = async (_, ws, ct) =>
        {
            var openEv = await FakeRemoteServer.ReceiveTextAsync(ws, ct);
            using var evDoc = JsonDocument.Parse(openEv!);
            await FakeRemoteServer.SendTextAsync(ws, ReadyFrame(evDoc.RootElement.GetProperty("streamId").GetString()!), ct);
            await FakeRemoteServer.ReceiveTextAsync(ws, ct);
        };

        await using var conn = new DshRemoteConnection(new RemoteConnectionOptions(server.BaseUri), () => DummyToken);
        using var gw = new RemoteDshGatewayClient(conn);
        var plugins = await gw.GetPluginInventoryAsync();

        Assert.False(plugins.IsOk);
        Assert.Equal(DshFailureCode.ProtocolMismatch, plugins.Error!.Code);
    }

    [Fact]
    public async Task GatewayClient_WorkspaceArchive_StrictSchema_FollowStream_AndCapabilityProbe()
    {
        await using var server = new FakeRemoteServer();
        var capturedArchiveBodies = new List<string>();
        string? capturedUnarchiveBody = null;
        var rejectActiveSession = true;

        server.OnHttpRequest = req =>
        {
            if (req.Path == "/connect")
            {
                return Task.FromResult(new FakeRemoteServer.HttpResponseData(
                    302, "Found", [("Location", "/"), ("Set-Cookie", $"dsh-auth-{server.Port}=ck-ws; HttpOnly")], []));
            }

            var rpcId = ParseRpcId(req.Body);
            return req.Path switch
            {
                "/api/session/list" => Task.FromResult(FakeRemoteServer.HttpResponseData.Json(200,
                    WrapOk(rpcId, """{"items":[]}"""))),
                "/api/session/modelCatalog" => Task.FromResult(FakeRemoteServer.HttpResponseData.Json(200,
                    WrapOk(rpcId, """{"default":{"provider":"deepseek-official","model":"deepseek-chat"},"routableProviders":[],"groups":[],"failures":[]}"""))),
                "/api/credentials/describe" => Task.FromResult(FakeRemoteServer.HttpResponseData.Json(200,
                    WrapOk(rpcId, "{}"))),
                "/api/pluginInventory/list" => Task.FromResult(FakeRemoteServer.HttpResponseData.Json(200,
                    WrapOk(rpcId, """{"managementAvailable":false,"entries":[]}"""))),
                "/api/workspace/archiveSession" => HandleArchive(rpcId, req.Body),
                "/api/workspace/unarchiveSession" => HandleUnarchive(rpcId, req.Body),
                _ => Task.FromResult(FakeRemoteServer.HttpResponseData.Json(404, "{}")),
            };
        };

        Task<FakeRemoteServer.HttpResponseData> HandleArchive(string rpcId, byte[] body)
        {
            var rawJson = Encoding.UTF8.GetString(body);
            capturedArchiveBodies.Add(rawJson);
            using var doc = JsonDocument.Parse(rawJson);
            var reqEl = doc.RootElement.GetProperty("payload").GetProperty("args").GetProperty("request");
            var stopActivity = reqEl.TryGetProperty("stopActivity", out var stopEl) && stopEl.GetBoolean();
            if (rejectActiveSession && !stopActivity)
            {
                return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(200,
                    WrapErr(rpcId, "workspace/session-active", "session is active")));
            }
            return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(200,
                WrapOk(rpcId, """{"archivedSessionIds":["s-arch-1"]}""")));
        }

        Task<FakeRemoteServer.HttpResponseData> HandleUnarchive(string rpcId, byte[] body)
        {
            capturedUnarchiveBody = Encoding.UTF8.GetString(body);
            return Task.FromResult(FakeRemoteServer.HttpResponseData.Json(200,
                WrapOk(rpcId, """{"archivedSessionIds":[]}""")));
        }

        var followOpenCount = 0;
        server.OnWebSocketConnected = async (_, ws, ct) =>
        {
            var openEv = await FakeRemoteServer.ReceiveTextAsync(ws, ct);
            using var evDoc = JsonDocument.Parse(openEv!);
            var evSid = evDoc.RootElement.GetProperty("streamId").GetString()!;
            await FakeRemoteServer.SendTextAsync(ws, ReadyFrame(evSid, "c-ws"), ct);

            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var raw = await FakeRemoteServer.ReceiveTextAsync(ws, ct);
                if (raw is null)
                {
                    break;
                }
                using var frame = JsonDocument.Parse(raw);
                if (frame.RootElement.GetProperty("type").GetString() == "open" &&
                    frame.RootElement.GetProperty("endpoint").GetString() == "workspace/follow")
                {
                    var wsStreamId = frame.RootElement.GetProperty("streamId").GetString()!;
                    var currentOpen = Interlocked.Increment(ref followOpenCount);
                    await FakeRemoteServer.SendTextAsync(ws,
                        $$$$"""{"type":"item","streamId":"{{{{wsStreamId}}}}","value":{"type":"baseline","value":{"archivedSessionIds":["s-existing-arch"],"pinnedSessionIds":[]}}}""", ct);

                    if (currentOpen >= 3)
                    {
                        // 实时订阅流依次推送增量归档、整包替换和未知未来帧
                        await FakeRemoteServer.SendTextAsync(ws,
                            $$$$"""{"type":"item","streamId":"{{{{wsStreamId}}}}","value":{"type":"archived","archivedSessionIds":["s-existing-arch","s-arch-2"]}}""", ct);
                        await FakeRemoteServer.SendTextAsync(ws,
                            $$$$"""{"type":"item","streamId":"{{{{wsStreamId}}}}","value":{"type":"replace","value":{"archivedSessionIds":["s-arch-3"],"pinnedSessionIds":[]}}}""", ct);
                        await FakeRemoteServer.SendTextAsync(ws,
                            $$$$"""{"type":"item","streamId":"{{{{wsStreamId}}}}","value":{"type":"future-delta"}}""", ct);
                    }
                }
            }
        };

        await using var conn = new DshRemoteConnection(new RemoteConnectionOptions(server.BaseUri), () => DummyToken);
        using var gw = new RemoteDshGatewayClient(conn);

        // 1. ProbeCapabilitiesAsync 探测 workspace/follow 成功 → ArchiveSessions = true
        var caps = await gw.ProbeCapabilitiesAsync();
        Assert.True(caps.IsOk);
        Assert.True(caps.Value!.ArchiveSessions);

        // 2. GetArchivedSessionIdsAsync 读取首帧基线并释放流
        var baseline = await gw.GetArchivedSessionIdsAsync();
        Assert.True(baseline.IsOk);
        Assert.Equal(["s-existing-arch"], baseline.Value!);

        // 3. ArchiveSessionAsync(stopActivity: false) 严格不传 stopActivity 字段，且映射 workspace/session-active → SessionActive
        var activeErr = await gw.ArchiveSessionAsync("s-arch-1", stopActivity: false);
        Assert.False(activeErr.IsOk);
        Assert.Equal(DshFailureCode.SessionActive, activeErr.Error!.Code);
        using (var firstDoc = JsonDocument.Parse(capturedArchiveBodies[0]))
        {
            var props = firstDoc.RootElement.GetProperty("payload").GetProperty("args").GetProperty("request")
                .EnumerateObject().Select(p => p.Name).ToArray();
            Assert.Equal(["sessionId"], props);
        }

        // 4. ArchiveSessionAsync(stopActivity: true) 传 stopActivity=true 并返回最新归档 IDs
        var archivedOk = await gw.ArchiveSessionAsync("s-arch-1", stopActivity: true);
        Assert.True(archivedOk.IsOk);
        Assert.Equal(["s-arch-1"], archivedOk.Value!);
        using (var secondDoc = JsonDocument.Parse(capturedArchiveBodies[1]))
        {
            var reqEl = secondDoc.RootElement.GetProperty("payload").GetProperty("args").GetProperty("request");
            Assert.True(reqEl.GetProperty("stopActivity").GetBoolean());
        }

        // 5. UnarchiveSessionAsync 严格仅发 sessionId 字段
        var unarchivedOk = await gw.UnarchiveSessionAsync("s-arch-1");
        Assert.True(unarchivedOk.IsOk);
        Assert.Empty(unarchivedOk.Value!);
        using (var unarchDoc = JsonDocument.Parse(capturedUnarchiveBody!))
        {
            var props = unarchDoc.RootElement.GetProperty("payload").GetProperty("args").GetProperty("request")
                .EnumerateObject().Select(p => p.Name).ToArray();
            Assert.Equal(["sessionId"], props);
        }

        // 6. FollowWorkspaceAsync 实时接收 baseline / archived / replace / unknown 帧
        var received = new ConcurrentQueue<WorkspaceFollowFrame>();
        var allFourTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var followOutcome = await gw.FollowWorkspaceAsync(f =>
        {
            received.Enqueue(f);
            if (received.Count >= 4)
            {
                allFourTcs.TrySetResult();
            }
            return ValueTask.CompletedTask;
        });
        Assert.True(followOutcome.IsOk);
        await allFourTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await followOutcome.Value!.DisposeAsync();

        var frames = received.ToArray();
        Assert.IsType<WorkspaceBaselineFrame>(frames[0]);
        Assert.IsType<WorkspaceArchivedChangedFrame>(frames[1]);
        Assert.IsType<WorkspaceReplacedFrame>(frames[2]);
        var unk = Assert.IsType<UnknownWorkspaceFollowFrame>(frames[3]);
        Assert.Equal("future-delta", unk.RawType);
    }

    [Fact]
    public async Task GatewayClient_ProbeCapabilities_SetsArchiveFalseWhenWorkspaceFollowUnsupported()
    {
        await using var server = new FakeRemoteServer();
        server.OnHttpRequest = req =>
        {
            if (req.Path == "/connect")
            {
                return Task.FromResult(new FakeRemoteServer.HttpResponseData(
                    302, "Found", [("Location", "/"), ("Set-Cookie", $"dsh-auth-{server.Port}=ck-noarch; HttpOnly")], []));
            }
            var rpcId = ParseRpcId(req.Body);
            return req.Path switch
            {
                "/api/session/list" => Task.FromResult(FakeRemoteServer.HttpResponseData.Json(200, WrapOk(rpcId, """{"items":[]}"""))),
                "/api/session/modelCatalog" => Task.FromResult(FakeRemoteServer.HttpResponseData.Json(200, WrapErr(rpcId, "unsupported", "no"))),
                "/api/credentials/describe" => Task.FromResult(FakeRemoteServer.HttpResponseData.Json(200, WrapOk(rpcId, "{}"))),
                "/api/pluginInventory/list" => Task.FromResult(FakeRemoteServer.HttpResponseData.Json(200, WrapErr(rpcId, "unsupported", "no"))),
                _ => Task.FromResult(FakeRemoteServer.HttpResponseData.Json(404, "{}")),
            };
        };

        server.OnWebSocketConnected = async (_, ws, ct) =>
        {
            var openEv = await FakeRemoteServer.ReceiveTextAsync(ws, ct);
            using var evDoc = JsonDocument.Parse(openEv!);
            await FakeRemoteServer.SendTextAsync(ws, ReadyFrame(evDoc.RootElement.GetProperty("streamId").GetString()!, "c-noarch"), ct);

            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var raw = await FakeRemoteServer.ReceiveTextAsync(ws, ct);
                if (raw is null)
                {
                    break;
                }
                using var frame = JsonDocument.Parse(raw);
                if (frame.RootElement.GetProperty("type").GetString() == "open" &&
                    frame.RootElement.GetProperty("endpoint").GetString() == "workspace/follow")
                {
                    var sid = frame.RootElement.GetProperty("streamId").GetString()!;
                    await FakeRemoteServer.SendTextAsync(ws,
                        $$$$"""{"type":"error","streamId":"{{{{sid}}}}","error":{"code":"unsupported","message":"workspace/follow disabled"}}""", ct);
                }
            }
        };

        await using var conn = new DshRemoteConnection(new RemoteConnectionOptions(server.BaseUri), () => DummyToken);
        using var gw = new RemoteDshGatewayClient(conn);

        var caps = await gw.ProbeCapabilitiesAsync();
        Assert.True(caps.IsOk);
        Assert.True(caps.Value!.ListSessions);
        Assert.False(caps.Value.ArchiveSessions);
    }
}
