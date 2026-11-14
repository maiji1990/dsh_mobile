using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DshMobile.Runtime.Automation.Relay;
using DshMobile.Runtime.Automation.Relay.Exec;
using DshMobile.Runtime.Automation.Relay.Exec.Direct;
using DshMobile.Runtime.Automation.Relay.Exec.Shizuku;
using DshMobile.Runtime.Automation.Relay.Logging;
using DshMobile.Runtime.Automation.Relay.Storage;
using Microsoft.Extensions.Time.Testing;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// PR-07: the Shizuku backend (upstream exec/shizuku). ShellVerbs, the remote budget and LastFrameCache carry the
/// upstream JVM tests (ShellVerbsTest, RemoteBudgetTest, LastFrameCacheTest) case for case; the backend flows run over
/// a fake shell service with the expectations read from ShizukuBackend.kt. The binder and the trusted display itself
/// need the device (the combined device test).
/// </summary>
public sealed class RelayShizukuTests : IDisposable
{
    private const string Self = "org.example.dshmobile";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dshm-shizuku-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new();
    private readonly RelayPaths _paths;
    private readonly StorageReaper _reaper;
    private readonly RunLog _runLog;
    private readonly Platform _platform = new();
    private readonly Shell _shell = new();
    private readonly ShizukuBackend _backend;

    public RelayShizukuTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new RelayPaths(_root, RelayPathPolicy.DshMobile);
        _runLog = new RunLog(_paths.LogsDir);
        _reaper = new StorageReaper(_paths, new QuotaLedger(_paths.QuotaFile), _runLog, () => 0, () => 0, _ => long.MaxValue);
        _backend = new ShizukuBackend(_platform, _runLog, _reaper, _time, (ms, _) =>
        {
            _time.Advance(TimeSpan.FromMilliseconds(ms));
            return Task.CompletedTask;
        });
    }

    public void Dispose()
    {
        _backend.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private Task<BackendResult> Exec(string capability, string args = "{}", long budgetMs = 15_000, SurfaceKind surface = SurfaceKind.Foreground) =>
        _backend.ExecuteAsync(new BackendCall("r1", capability, J(args), surface, _paths, budgetMs), CancellationToken.None);

    private void Bind() => _backend.OnServiceConnected(_shell);

    private static BackendResult.Ok AssertOk(BackendResult result) => Assert.IsType<BackendResult.Ok>(result);

    private static BackendResult.Failed AssertFailed(BackendResult result, RelayError error)
    {
        var failed = Assert.IsType<BackendResult.Failed>(result);
        Assert.Equal(error, failed.Error);
        return failed;
    }

    // ── ShellVerbsTest ──

    private static ShellVerbs.Plan Plan(string verb, params string[] args) =>
        ShellVerbs.Build(J(JsonSerializer.Serialize(new { verb, args })));

    private static string Command(string verb, params string[] args) => Assert.IsType<ShellVerbs.Plan.Run>(Plan(verb, args)).Command;

    private static string BadReason(ShellVerbs.Plan plan) => Assert.IsType<ShellVerbs.Plan.Bad>(plan).Reason;

    [Fact]
    public void ShellVerbs_ReadOnlyVerbsBuildQuotedCommands()
    {
        Assert.Equal("dumpsys 'battery'", Command("dumpsys", "battery"));
        Assert.Equal("pm 'list' 'packages'", Command("pm", "list", "packages"));
        Assert.Equal("settings 'get' 'system' 'volume_music'", Command("settings", "get", "system", "volume_music"));
        Assert.Equal("getprop", Command("getprop"));
        Assert.Equal("appops 'get' 'com.example.app'", Command("appops", "get", "com.example.app"));
        Assert.Equal("wm 'size'", Command("wm", "size"));
    }

    [Fact]
    public void ShellVerbs_ScreencapIsReadOnlyButTakesNoArgs()
    {
        Assert.Equal("screencap", Command("screencap"));
        Assert.Contains("option flags", BadReason(Plan("screencap", "-d", "0")), StringComparison.Ordinal);
        Assert.Contains("screencap takes no args", BadReason(Plan("screencap", "png")), StringComparison.Ordinal);
    }

    [Fact]
    public void ShellVerbs_RawCommandStringsAndMetacharactersNeverBecomeACommand()
    {
        Assert.IsType<ShellVerbs.Plan.Bad>(Plan("dumpsys; rm", "-rf", "/"));
        Assert.IsType<ShellVerbs.Plan.Bad>(Plan("dumpsys", "battery|wc"));
        Assert.IsType<ShellVerbs.Plan.Bad>(Plan("dumpsys", "battery$(id)"));
        Assert.IsType<ShellVerbs.Plan.Bad>(Plan("settings", "get", "system", "-x; reboot"));
    }

    [Fact]
    public void ShellVerbs_DeleteClassVerbsAreRefusedAndNotConfigurable()
    {
        foreach (var verb in new[] { "rm", "rmdir", "dd", "su", "sh", "pm_clear", "settings_delete" })
        {
            Assert.Contains("refused by policy", BadReason(ShellVerbs.Build(J($"{{\"verb\":\"{verb}\"}}"))), StringComparison.Ordinal);
        }
        Assert.DoesNotContain(ShellVerbs.ReadOnlyNames, ShellVerbs.Forbidden.Contains);
    }

    [Fact]
    public void ShellVerbs_SystemChangingVerbsAskForTheirOwnSwitch()
    {
        foreach (var verb in new[] { "input", "svc", "media" })
        {
            var needs = Assert.IsType<ShellVerbs.Plan.NeedsOptIn>(Plan(verb, "1"));
            Assert.Equal(verb, needs.Verb);
        }
    }

    [Fact]
    public void ShellVerbs_UnknownVerbListsTheReadOnlySet()
    {
        var reason = BadReason(Plan("nope"));
        Assert.StartsWith("unknown verb: nope", reason, StringComparison.Ordinal);
        Assert.Contains("dumpsys", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellVerbs_PerVerbArgumentShapesAreEnforced()
    {
        Assert.Contains("service name", BadReason(Plan("dumpsys")), StringComparison.Ordinal);
        Assert.Contains("option flags", BadReason(Plan("dumpsys", "batterystats", "--reset")), StringComparison.Ordinal);
        Assert.Contains("exactly one", BadReason(Plan("dumpsys", "deviceidle", "force-idle")), StringComparison.Ordinal);
        Assert.Contains("query", BadReason(Plan("wm", "size", "reset")), StringComparison.Ordinal);
        Assert.Contains("list", BadReason(Plan("pm", "install", "/data/local/tmp/x.apk")), StringComparison.Ordinal);
        Assert.Contains("get", BadReason(Plan("settings", "put", "system", "x")), StringComparison.Ordinal);
        Assert.Contains("get", BadReason(Plan("appops", "set", "com.example.app")), StringComparison.Ordinal);
        Assert.Contains("at most", BadReason(Plan("wm", "size", "1080x1920", "extra", "a", "b", "c", "d")), StringComparison.Ordinal);
    }

    [Fact]
    public void ShellVerbs_OptedInVerbsCheckTheirShapeAndAJsonNullArgReadsNull()
    {
        var open = ShellVerbs.Build(J("{\"verb\":\"input\",\"args\":[\"tap\",\"1\",\"2\"]}"), _ => true);
        Assert.Equal("input 'tap' '1' '2'", Assert.IsType<ShellVerbs.Plan.Run>(open).Command);
        Assert.Contains("two coordinates", BadReason(ShellVerbs.Build(J("{\"verb\":\"input\",\"args\":[\"tap\",\"1\"]}"), _ => true)),
            StringComparison.Ordinal);
        // JSONArray.optString(i, "") on a JSON null is "null" (String.valueOf(JSONObject.NULL)).
        Assert.Equal("dumpsys 'null'", Assert.IsType<ShellVerbs.Plan.Run>(ShellVerbs.Build(J("{\"verb\":\"dumpsys\",\"args\":[null]}"))).Command);
    }

    // ── RemoteBudgetTest ──

    [Fact]
    public void Budget_AGenerousBudgetKeepsTheDefaultCap()
    {
        Assert.Equal(15_000L, ShizukuBackend.RemoteWaitMs(15_000L, 60_000L, ShizukuBackend.RemoteMarginMs));
        Assert.Equal(120_000L, ShizukuBackend.RemoteWaitMs(120_000L, 120_000L + ShizukuBackend.RemoteMarginMs, ShizukuBackend.RemoteMarginMs));
    }

    [Fact]
    public void Budget_ABudgetBelowTheDefaultClampsTheWait()
    {
        Assert.Equal(9_500L, ShizukuBackend.RemoteWaitMs(15_000L, 10_000L, ShizukuBackend.RemoteMarginMs));
        Assert.Equal(ShizukuBackend.MinRemoteRoundtripMs,
            ShizukuBackend.RemoteWaitMs(15_000L, ShizukuBackend.MinRemoteRoundtripMs + ShizukuBackend.RemoteMarginMs, ShizukuBackend.RemoteMarginMs));
    }

    [Fact]
    public void Budget_ABudgetThatCannotCoverOneRoundTripRefusesToIssue()
    {
        var floor = ShizukuBackend.MinRemoteRoundtripMs + ShizukuBackend.RemoteMarginMs;
        Assert.Null(ShizukuBackend.RemoteWaitMs(15_000L, floor - 1L, ShizukuBackend.RemoteMarginMs));
        Assert.Null(ShizukuBackend.RemoteWaitMs(15_000L, 0L, ShizukuBackend.RemoteMarginMs));
        Assert.Null(ShizukuBackend.RemoteWaitMs(15_000L, -1L, ShizukuBackend.RemoteMarginMs));
    }

    [Fact]
    public void Budget_TheIssueDecisionAgreesWithTheAllocation()
    {
        Assert.True(ShizukuBackend.CanIssueRemote(15_000L, 10_000L, ShizukuBackend.RemoteMarginMs));
        Assert.False(ShizukuBackend.CanIssueRemote(15_000L, ShizukuBackend.RemoteMarginMs, ShizukuBackend.RemoteMarginMs));
    }

    [Fact]
    public void Budget_TheReserveCoversTheReplySlack()
    {
        Assert.True(ShizukuBackend.RemoteMarginMs > ShizukuBackend.RemoteSlackMs);
        Assert.True(ShizukuBackend.RemoteMarginArtifactMs > ShizukuBackend.RemoteSlackMs);
        Assert.True(ShizukuBackend.RemoteMarginArtifactMs >= ShizukuBackend.RemoteMarginMs);
        Assert.True(ShizukuBackend.MinRemoteRoundtripMs > 0L);
    }

    [Fact]
    public void Budget_TheGiveUpReasonSaysTheOutcomeIsUnknown()
    {
        var submitted = ShizukuBackend.RemoteGiveUpReason("install", 1_000L, 120L);
        Assert.Contains("outcome is unknown", submitted, StringComparison.Ordinal);
        Assert.Contains("do not treat this as not executed", submitted, StringComparison.Ordinal);
        Assert.Contains("submitted 120ms before the local budget expired", submitted, StringComparison.Ordinal);
        var queued = ShizukuBackend.RemoteGiveUpReason("command", 800L, null);
        Assert.Contains("outcome is unknown", queued, StringComparison.Ordinal);
        Assert.Contains("do not treat this as not executed", queued, StringComparison.Ordinal);
        Assert.Contains("still queued", queued, StringComparison.Ordinal);
    }

    // ── LastFrameCacheTest ──

    private string FramePath() => Path.Combine(_root, "frames-" + Guid.NewGuid().ToString("N"), "last-frame.png");

    [Fact]
    public void FrameCache_ARememberedFrameIsHandedBackWithItsAge()
    {
        var now = 1_000L;
        var cache = new LastFrameCache(FramePath(), () => now);
        Assert.True(cache.Remember(7, [1, 2, 3]));
        now += 4_500L;
        var hit = cache.Peek(7);
        Assert.Equal([1, 2, 3], hit!.Value.Bytes);
        Assert.Equal(4_500L, hit.Value.AgeMs);
    }

    [Fact]
    public void FrameCache_NothingForADisplayThatNeverProducedAFrame() => Assert.Null(new LastFrameCache(FramePath(), () => 0L).Peek(3));

    [Fact]
    public void FrameCache_AnotherDisplayIdGetsNothing()
    {
        var cache = new LastFrameCache(FramePath(), () => 0L);
        cache.Remember(7, [9]);
        Assert.Null(cache.Peek(8));
    }

    [Fact]
    public void FrameCache_AnOversizedFrameIsDroppedWithWhateverWasCached()
    {
        var file = FramePath();
        var cache = new LastFrameCache(file, () => 0L, maxBytes: 4L);
        cache.Remember(7, [1, 1, 1]);
        Assert.False(cache.Remember(7, new byte[5]));
        Assert.Null(cache.Peek(7));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void FrameCache_AClockThatMovedBackwardsServesNothing()
    {
        var now = 10_000L;
        var cache = new LastFrameCache(FramePath(), () => now);
        cache.Remember(7, [1]);
        now = 5_000L;
        Assert.Null(cache.Peek(7));
    }

    [Fact]
    public void FrameCache_ClearRemovesTheCachedBytes()
    {
        var file = FramePath();
        var cache = new LastFrameCache(file, () => 0L);
        cache.Remember(7, [1, 2]);
        cache.Clear();
        Assert.Null(cache.Peek(7));
        Assert.False(File.Exists(file));
    }

    // ── FrameEncoder.convert ──

    [Fact]
    public void Nv12_Bt601LimitedRangeWithEncoderPadding()
    {
        // 2x2 source: white, black / red, blue; encoder stride 4 and slice height 3 (padding must stay untouched).
        int[] argb = [unchecked((int)0xFFFFFFFF), unchecked((int)0xFF000000), unchecked((int)0xFFFF0000), unchecked((int)0xFF0000FF)];
        var plane = new byte[Nv12.FrameBytes(4, 3)];
        Nv12.Convert(argb, 2, 2, 2, 2, 4, 3, plane);
        Assert.Equal(18, plane.Length);
        Assert.Equal(new byte[] { 235, 16, 0, 0, 82, 41, 0, 0 }, plane[..8]);
        // One chroma sample from the top-left (white): U = V = 128.
        Assert.Equal(new byte[] { 128, 128 }, plane[12..14]);
        var red = new byte[Nv12.FrameBytes(2, 2)];
        Nv12.Convert([unchecked((int)0xFFFF0000)], 1, 1, 2, 2, 2, 2, red);
        Assert.Equal(new byte[] { 82, 82, 82, 82, 90, 240 }, red);
    }

    [Fact]
    public void Nv12_DownscalesByNearestNeighbour()
    {
        var argb = new int[4 * 2];
        argb[2] = unchecked((int)0xFFFFFFFF);
        var plane = new byte[Nv12.FrameBytes(2, 1)];
        Nv12.Convert(argb, 4, 2, 2, 1, 2, 1, plane);
        Assert.Equal((byte)16, plane[0]);
        Assert.Equal((byte)235, plane[1]);
    }

    // ── pure helpers ──

    [Fact]
    public void TopPackages_FirstTopActivityPerDisplaySection()
    {
        const string dump = "  Display #0 (activities from top to bottom):\n" +
            "    topResumedActivity=ActivityRecord{1a2b u0 com.android.launcher3/.Launcher t12}\n" +
            "    topResumedActivity=ActivityRecord{3c4d u0 com.other/.X t13}\r\n" +
            "  Display #7 (activities from top to bottom):\n" +
            "    topResumedActivity=ActivityRecord{5e6f u0 com.example.app/.Main t20}\n";
        var tops = ShizukuBackend.ParseTopPackages(dump);
        Assert.Equal(2, tops.Count);
        Assert.Equal("com.android.launcher3", tops[0]);
        Assert.Equal("com.example.app", tops[7]);
    }

    [Theory]
    [InlineData(127, "", "E_CAPABILITY_UNAVAILABLE_ON_DEVICE")]
    [InlineData(1, "/system/bin/sh: media: inaccessible or not found", "E_CAPABILITY_UNAVAILABLE_ON_DEVICE")]
    [InlineData(255, "Security exception: Permission Denial: not allowed", "E_GATE_SYSTEM_MISSING")]
    [InlineData(1, "java.lang.SecurityException: x", "E_GATE_SYSTEM_MISSING")]
    [InlineData(2, "Bad argument", "E_INTERNAL")]
    public void CommandFailure_ThreeClasses(int exit, string stderr, string wire)
    {
        var (error, reason) = ShizukuBackend.CommandFailure(exit, stderr);
        Assert.Equal(wire, error.Code);
        Assert.StartsWith($"exit={exit} {stderr}", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("enabled_accessibility_services", true)]
    [InlineData("Default_Input_Method", true)]
    [InlineData("always_on_vpn_app", true)]
    [InlineData("install_non_market_apps", true)]
    [InlineData("wifi_on", true)]
    [InlineData("screen_brightness", false)]
    [InlineData("location_mode", false)]
    public void PrivilegeHandoffKeys_AreBlockedByFragmentOrName(string key, bool blocked) =>
        Assert.Equal(blocked, ShizukuBackend.IsPrivilegeHandoffKey(key));

    // ── backend flows ──

    [Fact]
    public void Supports_ShellAlwaysVirtualOnlyTrustedDisplayOnlyWhenAlive()
    {
        Assert.True(_backend.Supports("sys.shell", SurfaceKind.Foreground));
        Assert.True(_backend.Supports("app.install", SurfaceKind.BackendTrusted));
        Assert.False(_backend.Supports("surface.virtual", SurfaceKind.Foreground));
        Assert.True(_backend.Supports("surface.virtual", SurfaceKind.BackendTrusted));
        Assert.False(_backend.Supports("ui.tap", SurfaceKind.BackendTrusted));
        Assert.False(_backend.Supports("ui.snapshot", SurfaceKind.BackendTrusted));
    }

    [Fact]
    public async Task ArgsAreCheckedBeforeTheBinder()
    {
        var unknown = AssertFailed(await Exec("app.stop", "{\"package\":\"a.b\",\"x\":1}"), RelayError.TransportMalformed);
        Assert.StartsWith("unknown arg: x", unknown.Reason, StringComparison.Ordinal);
        Assert.Equal("missing arg: value", AssertFailed(await Exec("sys.settings.write", "{\"key\":\"k\"}"), RelayError.TransportMalformed).Reason);
        Assert.Contains("display", AssertFailed(await Exec("ui.tap", "{\"x\":1,\"y\":1,\"display\":2}"), RelayError.TransportMalformed).Reason,
            StringComparison.Ordinal);
        Assert.Empty(_shell.Commands);
        AssertFailed(await Exec("app.stop", "{\"package\":\"a.b\"}"), RelayError.BackendShizukuDead);
        Assert.False(_backend.Available());
    }

    [Fact]
    public async Task SettingsWrite_ReadsBackAndNamesTheLayerThatShadowsIt()
    {
        Bind();
        _shell.Reply = c => c switch
        {
            "settings get secure 'screen_brightness'" => new ShellOutcome(0, "80\n", ""),
            _ when c.StartsWith("echo ", StringComparison.Ordinal) => new ShellOutcome(0, "system=120 global=null\n", ""),
            _ => new ShellOutcome(0, "", ""),
        };
        var ok = AssertOk(await Exec("sys.settings.write", "{\"key\":\"screen_brightness\",\"value\":\"80\"}"));
        Assert.Equal(
            [
                "settings put secure 'screen_brightness' '80'",
                "settings get secure 'screen_brightness'",
                "echo system=$(settings get system 'screen_brightness') global=$(settings get global 'screen_brightness')",
            ],
            _shell.Commands);
        Assert.Equal(
            "{\"namespace\":\"secure\",\"key\":\"screen_brightness\",\"requested\":\"80\",\"readBack\":\"80\",\"verified\":true," +
            "\"shadowedBy\":\"system\",\"note\":\"this key also has a value in 'system' while this call wrote 'secure': the effective " +
            "setting probably did not change - pass \\\"namespace\\\" for the layer that key actually lives in\"}",
            ok.Data.Relaxed());
    }

    [Fact]
    public async Task SettingsWrite_RefusesHandoffKeysAndUnknownLayers()
    {
        Bind();
        Assert.Equal("key blocked: enabled_accessibility_services",
            AssertFailed(await Exec("sys.settings.write", "{\"key\":\"enabled_accessibility_services\",\"value\":\"x\"}"), RelayError.GateHostDenied).Reason);
        Assert.Equal("namespace must be one of secure|system|global, got 'vendor'",
            AssertFailed(await Exec("sys.settings.write", "{\"key\":\"k\",\"value\":\"1\",\"namespace\":\"vendor\"}"), RelayError.TransportMalformed).Reason);
        Assert.Equal("key", AssertFailed(await Exec("sys.settings.write", "{\"key\":\"1abc\",\"value\":\"1\"}"), RelayError.TransportMalformed).Reason);
        Assert.Empty(_shell.Commands);
    }

    [Fact]
    public async Task AppOps_NormalizesTheOpAndReportsTheEffectiveMode()
    {
        Bind();
        _shell.Reply = c => c.StartsWith("appops get", StringComparison.Ordinal)
            ? new ShellOutcome(0, "Uid mode: CAMERA: foreground\nCAMERA: allow\n", "")
            : new ShellOutcome(0, "", "");
        var ok = AssertOk(await Exec("appops.set", "{\"package\":\"com.example.app\",\"op\":\"CAMERA\",\"mode\":\"DEFAULT\"}"));
        Assert.Equal(["appops set com.example.app android:camera default", "appops get com.example.app android:camera"], _shell.Commands);
        Assert.Equal(
            "{\"package\":\"com.example.app\",\"op\":\"android:camera\",\"effective\":\"allow\",\"requested\":\"default\"," +
            "\"readBack\":\"Uid mode: CAMERA: foreground\\nCAMERA: allow\"}",
            ok.Data.Relaxed());
        Assert.StartsWith("op not allowed: READ_SMS, accepted: android:system_alert_window, android:camera",
            AssertFailed(await Exec("appops.set", "{\"package\":\"a.b\",\"op\":\"READ_SMS\",\"mode\":\"allow\"}"), RelayError.TransportMalformed).Reason,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForceStop_NeverTheHostAndCommandFailuresAreClassified()
    {
        Bind();
        Assert.Equal("refusing to stop the host app", AssertFailed(await Exec("app.stop", $"{{\"package\":\"{Self}\"}}"), RelayError.GateHostDenied).Reason);
        _shell.Reply = _ => new ShellOutcome(0, " done \n", "");
        Assert.Equal("{\"output\":\"done\",\"exit\":0}", AssertOk(await Exec("app.stop", "{\"package\":\"com.example.app\"}")).Data.Relaxed());
        Assert.Equal("am force-stop com.example.app", _shell.Commands[^1]);
        _shell.Reply = _ => new ShellOutcome(255, "", " Permission Denial \n");
        Assert.Equal("exit=255 Permission Denial", AssertFailed(await Exec("app.stop", "{\"package\":\"com.example.app\"}"), RelayError.GateSystemMissing).Reason);
        _shell.Reply = _ => null;
        Assert.Equal("binder unavailable", AssertFailed(await Exec("app.stop", "{\"package\":\"com.example.app\"}"), RelayError.BackendShizukuDead).Reason);
    }

    [Fact]
    public async Task SysShell_GatedVerbsNeedTheirSwitchAndRunsCarryTheCommand()
    {
        Bind();
        Assert.Equal(
            "verb 'input' changes the system and is not turned on: open it in the dangerous-capabilities page " +
            "(shape: input tap|swipe|text|keyevent <args>)",
            AssertFailed(await Exec("sys.shell", "{\"verb\":\"input\",\"args\":[\"tap\",\"1\",\"2\"]}"), RelayError.GateHostDenied).Reason);
        _backend.ShellOptInOpen = v => v == "input";
        _shell.Reply = _ => new ShellOutcome(0, "", "");
        var ok = AssertOk(await Exec("sys.shell", "{\"verb\":\"input\",\"args\":[\"tap\",\"1\",\"2\"]}"));
        Assert.Equal("{\"output\":\"\",\"exit\":0,\"verb\":\"input\",\"command\":\"input 'tap' '1' '2'\"}",
            ok.Data.Relaxed());
    }

    [Fact]
    public async Task SysShell_ScreencapBecomesAnArtifact()
    {
        Bind();
        _shell.Screenshot = () => new ScreenshotReply(null, "screencap exit=1");
        Assert.Equal("no frame from screencap: screencap exit=1", AssertFailed(await Exec("sys.shell", "{\"verb\":\"screencap\"}"), RelayError.BackendUnavailable).Reason);
        _shell.Screenshot = () => new ScreenshotReply([0x89, 0x50, 0x4E, 0x47], "");
        var ok = AssertOk(await Exec("sys.shell", "{\"verb\":\"screencap\"}"));
        Assert.Equal("{\"bytes\":4,\"source\":\"screencap\"}", ok.Data.Relaxed());
        Assert.Single(ok.Artifacts);
        Assert.Equal(4, ok.ArtifactBytes);
    }

    [Fact]
    public async Task ABudgetThatCannotCoverARoundTripSendsNothing()
    {
        Bind();
        var failed = AssertFailed(await Exec("app.stop", "{\"package\":\"com.example.app\"}", budgetMs: 700), RelayError.TransportTimeout);
        Assert.Equal(
            "remaining call budget (700ms) minus the 500ms close-out reserve cannot cover one remote round trip " +
            "(needs at least 300ms): nothing was sent",
            failed.Reason);
        Assert.Empty(_shell.Commands);
    }

    [Fact]
    public async Task AStuckTransactionIsGivenUpLocallyAndSaidToBeUnknown()
    {
        Bind();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _shell.Reply = _ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return new ShellOutcome(0, "", "");
        };
        var pending = Exec("app.stop", "{\"package\":\"com.example.app\"}", budgetMs: 2_000);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        _time.Advance(TimeSpan.FromMilliseconds(1_700));
        var failed = AssertFailed(await pending, RelayError.TransportTimeout);
        release.Set();
        Assert.Equal(ShizukuBackend.RemoteGiveUpReason("command", 1_700, 1_700), failed.Reason);
        var events = _runLog.Snapshot().Select(e => e.Event.Name).ToList();
        Assert.Contains("REMOTE_SUBMITTED", events);
        Assert.Contains("REMOTE_RESULT_DROPPED", events);
        Assert.Contains("REMOTE_GIVE_UP", events);
    }

    [Fact]
    public async Task Display_CreateQueryTapAndDeniedCreates()
    {
        Bind();
        _shell.Create = (w, h, d) => new DisplayReply(-1, "SecurityException: no");
        Assert.Equal("SecurityException: no", AssertFailed(await Exec("surface.virtual"), RelayError.SurfaceTrustedDenied).Reason);
        _shell.Create = (w, h, d) => new DisplayReply(-2, "cannot verify existing display 3");
        AssertFailed(await Exec("surface.virtual"), RelayError.BackendUnavailable);
        Assert.Equal(-1, _backend.DisplayId);

        _shell.Create = (w, h, d) =>
        {
            _shell.Created = (w, h, d);
            _platform.Displays.Add(5);
            return new DisplayReply(5, "");
        };
        _shell.Query = () => new DisplayReply(5, "id=5 720x1280");
        var created = AssertOk(await Exec("surface.virtual", "{\"width\":720,\"height\":1280}"));
        Assert.Equal((720, 1280, 420), _shell.Created);
        Assert.Equal("{\"state\":\"create\",\"display\":5,\"displayId\":5,\"width\":720,\"height\":1280}", created.Data.Relaxed());
        Assert.Equal("kept", AssertOk(await Exec("surface.virtual")).Data["state"]!.GetValue<string>());
        Assert.True(_backend.Supports("ui.tap", SurfaceKind.BackendTrusted));

        Assert.Equal("target (720,5) is outside the trusted display 720x1280",
            AssertFailed(await Exec("ui.tap", "{\"x\":720,\"y\":5}"), RelayError.TransportMalformed).Reason);
        Assert.Equal("x is out of range (got 4294967400)", AssertFailed(await Exec("ui.tap", "{\"x\":4294967400,\"y\":5}"), RelayError.TransportMalformed).Reason);
        Assert.Equal("y must be a number, got \"1\" (string)", AssertFailed(await Exec("ui.tap", "{\"x\":1,\"y\":\"1\"}"), RelayError.TransportMalformed).Reason);
        _shell.Reply = _ => new ShellOutcome(0, "", "");
        Assert.Equal("{\"output\":\"\",\"exit\":0,\"display\":5}", AssertOk(await Exec("ui.tap", "{\"x\":10.9,\"y\":20}")).Data.Relaxed());
        Assert.Equal("input -d 5 tap 10 20", _shell.Commands[^1]);
        AssertOk(await Exec("ui.swipe", "{\"fromX\":1,\"fromY\":2,\"toX\":3,\"toY\":4,\"durationMs\":9000}"));
        Assert.Equal("input -d 5 swipe 1 2 3 4 3000", _shell.Commands[^1]);
        AssertOk(await Exec("ui.text", "{\"text\":\"it's\"}"));
        Assert.Equal("input -d 5 text 'it'\\''s'", _shell.Commands[^1]);
        AssertOk(await Exec("ui.key", "{\"key\":\"recents\"}"));
        Assert.Equal("input -d 5 keyevent 187", _shell.Commands[^1]);
        Assert.Equal("unknown key: menu, accepted: [back, home, recents, enter]",
            AssertFailed(await Exec("ui.key", "{\"key\":\"menu\"}"), RelayError.TransportMalformed).Reason);

        // The display went away on the service side: a failed input answers "no display", not a missing grant.
        _platform.Displays.Clear();
        _shell.Query = () => new DisplayReply(-1, "");
        _shell.Reply = _ => new ShellOutcome(1, "", "Invalid display");
        Assert.Equal("no trusted display: call surface.virtual with {\"action\":\"create\"} first",
            AssertFailed(await Exec("ui.key", "{\"key\":\"back\"}"), RelayError.BackendUnavailable).Reason);
        Assert.Equal(-1, _backend.DisplayId);
    }

    [Fact]
    public async Task Display_CaptureServesTheLastFrameAsStale()
    {
        await CreateDisplayAsync();
        _shell.Capture = (_, q, w) => new DisplayReply(0, "", [1, 2, 3]);
        var fresh = AssertOk(await Exec("screen.capture", surface: SurfaceKind.BackendTrusted));
        Assert.Equal("{\"display\":5,\"bytes\":3,\"stale\":false}", fresh.Data.Relaxed());
        Assert.Equal((0, 0), _shell.LastCaptureQuality);

        _time.Advance(TimeSpan.FromMilliseconds(4_000));
        _shell.Capture = (_, _, _) => new DisplayReply(-1, "no frame before deadline");
        var stale = AssertOk(await Exec("screen.capture"));
        Assert.Equal("{\"display\":5,\"bytes\":3,\"stale\":true,\"frameAgeMs\":4000}", stale.Data.Relaxed());

        // The same bytes again keep the original time: the display redrew the same picture.
        _shell.Capture = (_, _, _) => new DisplayReply(0, "", [1, 2, 3]);
        Assert.Equal(4_000L, AssertOk(await Exec("screen.capture")).Data["frameAgeMs"]!.GetValue<long>());

        // Release clears the cache: a new display must not get the old one's pixels.
        _shell.Release = () => new DisplayReply(1, "");
        Assert.Equal("{\"released\":true}", AssertOk(await Exec("surface.virtual", "{\"action\":\"release\"}")).Data.Relaxed());
        Assert.Equal("no trusted display: call surface.virtual with {\"action\":\"create\"} first",
            AssertFailed(await Exec("screen.capture"), RelayError.BackendUnavailable).Reason);
        await CreateDisplayAsync();
        _shell.Capture = (_, _, _) => new DisplayReply(-1, "no frame before deadline");
        Assert.Equal("no frame from the trusted display: retry, or re-create it with surface.virtual",
            AssertFailed(await Exec("screen.capture"), RelayError.BackendUnavailable).Reason);
    }

    [Fact]
    public async Task Display_LaunchVerifiesTheLanding()
    {
        await CreateDisplayAsync();
        _platform.Components["com.example.app"] = "com.example.app/.Main";
        var landed = false;
        _shell.Reply = c =>
        {
            if (c.StartsWith("am start", StringComparison.Ordinal))
            {
                landed = true;
                return new ShellOutcome(0, "Starting: Intent", "");
            }
            return new ShellOutcome(0,
                "Display #0 (x\ntopResumedActivity=ActivityRecord{1 u0 com.android.launcher3/.L t1}\n" +
                (landed ? "Display #5 (x\ntopResumedActivity=ActivityRecord{2 u0 com.example.app/.Main t2}\n" : ""), "");
        };
        var ok = AssertOk(await Exec("app.launch", "{\"package\":\"com.example.app\"}"));
        Assert.Equal(ShizukuBackend.TopPackageCommand, _shell.Commands[0]);
        Assert.Equal("am start --display 5 -n com.example.app/.Main", _shell.Commands[1]);
        Assert.Equal(
            "{\"output\":\"Starting: Intent\",\"exit\":0,\"package\":\"com.example.app\",\"display\":5,\"verified\":true," +
            "\"landedOn\":5,\"topOnTarget\":\"com.example.app\"}",
            ok.Data.Relaxed());
        Assert.Equal("not launchable here: com.nope", AssertFailed(await Exec("app.launch", "{\"package\":\"com.nope\"}"), RelayError.TransportMalformed).Reason);
    }

    [Fact]
    public async Task Display_LaunchThatGrabsTheUserScreenIsNotLanded()
    {
        await CreateDisplayAsync();
        _platform.Components["com.example.app"] = "com.example.app/.Main";
        var launched = false;
        _shell.Reply = c =>
        {
            if (c.StartsWith("am start", StringComparison.Ordinal))
            {
                launched = true;
                return new ShellOutcome(0, "", "");
            }
            return new ShellOutcome(0,
                "Display #0 (x\ntopResumedActivity=ActivityRecord{1 u0 " + (launched ? "com.example.app" : "com.android.launcher3") + "/.L t1}\n" +
                "Display #5 (x\ntopResumedActivity=ActivityRecord{2 u0 com.other/.O t2}\n", "");
        };
        var failed = AssertFailed(await Exec("app.launch", "{\"package\":\"com.example.app\"}"), RelayError.LaunchNotLanded);
        Assert.StartsWith("accepted but not realised on display 5 (top is com.other) - it came up on display 0, the user's own screen",
            failed.Reason, StringComparison.Ordinal);
        Assert.Contains("am start --display 5 -n com.example.app/.Main -f 0x10000000", _shell.Commands);
    }

    [Fact]
    public async Task Install_VettedUploadToShellDescriptorNeverTheHost()
    {
        Bind();
        Assert.Equal("no such upload: x.apk", AssertFailed(await Exec("app.install", "{\"upload\":\"x.apk\"}"), RelayError.TransportMalformed).Reason);
        var upload = new Upload(Path.Combine(_root, "in.apk"));
        _platform.Uploads["in.apk"] = upload;
        _platform.ArchivePackage = Self;
        Assert.Equal("refusing to replace the host app", AssertFailed(await Exec("app.install", "{\"upload\":\"in.apk\"}"), RelayError.GateHostDenied).Reason);
        Assert.True(upload.Disposed);

        _platform.ArchivePackage = "com.example.app";
        _shell.Install = (_, staged, timeout) => new ShellOutcome(1, "", "Failure [INSTALL_FAILED_VERSION_DOWNGRADE]");
        Assert.Equal("exit=1 Failure [INSTALL_FAILED_VERSION_DOWNGRADE]",
            AssertFailed(await Exec("app.install", "{\"upload\":\"in.apk\"}"), RelayError.GateSystemMissing).Reason);
        Assert.False(upload.SourceDeleted);

        _shell.Install = (_, staged, timeout) =>
        {
            _shell.Staged = (staged, timeout);
            return new ShellOutcome(0, "Success\n", "");
        };
        var ok = AssertOk(await Exec("app.install", "{\"upload\":\"in.apk\"}"));
        Assert.Equal("{\"package\":\"com.example.app\",\"bytes\":1234,\"output\":\"Success\"}", ok.Data.Relaxed());
        Assert.Matches("^install-r1-[0-9a-f]+\\.apk$", _shell.Staged.Name);
        Assert.Equal(15_000 - ShizukuBackend.RemoteMarginMs, _shell.Staged.TimeoutMs);
        Assert.True(upload.SourceDeleted);
        Assert.True(upload.DescriptorsClosed > 0 && upload.DescriptorsClosed == upload.DescriptorsOpened);
    }

    [Fact]
    public async Task Record_EncodesPulledFramesAndReportsTheActualRate()
    {
        await CreateDisplayAsync();
        _shell.Capture = (_, q, w) => new DisplayReply(0, "", [7]);
        var ok = AssertOk(await Exec("screen.record", "{\"seconds\":1}"));
        Assert.Equal((60, 720), _shell.LastCaptureQuality);
        var encoder = _platform.Encoder!;
        Assert.Equal((320, 640), (encoder.Width, encoder.Height));
        Assert.Equal(8, ok.Data["frames"]!.GetValue<int>());
        Assert.Equal(8.0, ok.Data["actualFps"]!.GetValue<double>());
        Assert.Equal(1_000L, ok.Data["recordedMs"]!.GetValue<long>());
        Assert.True(encoder.Finished);
        Assert.Single(ok.Artifacts);
        Assert.Equal(0L, encoder.Pts[0]);
        Assert.Equal(125_000L, encoder.Pts[1]);
    }

    [Fact]
    public void ADroppedServiceRebindsOnceWithinTheCooldown()
    {
        _time.Advance(TimeSpan.FromSeconds(60));
        _backend.Connect();
        Assert.Equal(1, _platform.Binds);
        Bind();
        _backend.OnServiceDisconnected();
        Assert.Equal((1, 2), (_platform.Unbinds, _platform.Binds));
        _backend.OnBindingDied();
        Assert.Equal((1, 2), (_platform.Unbinds, _platform.Binds));
        _time.Advance(TimeSpan.FromSeconds(6));
        _backend.Reconnect();
        Assert.Equal((2, 3), (_platform.Unbinds, _platform.Binds));
    }

    private async Task CreateDisplayAsync()
    {
        Bind();
        _shell.Create = (w, h, d) =>
        {
            _platform.Displays.Add(5);
            return new DisplayReply(5, "");
        };
        _shell.Query = () => _platform.Displays.Contains(5) ? new DisplayReply(5, "id=5 640x1280") : new DisplayReply(-1, "");
        AssertOk(await Exec("surface.virtual"));
    }

    private sealed class Shell : IShellService
    {
        public List<string> Commands { get; } = [];

        public Func<string, ShellOutcome?> Reply { get; set; } = _ => new ShellOutcome(0, "", "");

        public Func<IDisposable, string, long, ShellOutcome?> Install { get; set; } = (_, _, _) => new ShellOutcome(0, "", "");

        public Func<int, int, int, DisplayReply> Create { get; set; } = (_, _, _) => new DisplayReply(-1, "no");

        public Func<DisplayReply> Release { get; set; } = () => new DisplayReply(0, "no display");

        public Func<DisplayReply> Query { get; set; } = () => new DisplayReply(-1, "");

        public Func<long, int, int, DisplayReply> Capture { get; set; } = (_, _, _) => new DisplayReply(-1, "no display");

        public Func<ScreenshotReply> Screenshot { get; set; } = () => new ScreenshotReply(null, "");

        public (int, int, int) Created { get; set; }

        public (string Name, long TimeoutMs) Staged { get; set; }

        public (int Quality, int MaxWidth) LastCaptureQuality { get; private set; }

        public ShellOutcome? Run(string command, long timeoutMs)
        {
            lock (Commands)
            {
                Commands.Add(command);
            }
            return Reply(command);
        }

        ShellOutcome? IShellService.Install(IDisposable descriptor, string stagedName, long timeoutMs) => Install(descriptor, stagedName, timeoutMs);

        public DisplayReply CreateDisplay(int width, int height, int dpi) => Create(width, height, dpi);

        public DisplayReply ReleaseDisplay() => Release();

        public DisplayReply QueryDisplay() => Query();

        public DisplayReply CaptureDisplay(long waitMs, int jpegQuality, int maxWidth)
        {
            LastCaptureQuality = (jpegQuality, maxWidth);
            return Capture(waitMs, jpegQuality, maxWidth);
        }

        ScreenshotReply IShellService.Screenshot() => Screenshot();
    }

    private sealed class Upload(string path) : IInstallUpload
    {
        public bool Disposed { get; private set; }

        public bool SourceDeleted { get; private set; }

        public int DescriptorsOpened { get; private set; }

        public int DescriptorsClosed { get; private set; }

        public string FilePath => path;

        public long Size => 1234;

        public int ReadAt(long position, Span<byte> buffer) => 0;

        public Stream OpenRead() => new MemoryStream();

        public void DeleteSource() => SourceDeleted = true;

        public IDisposable? OpenDescriptor()
        {
            DescriptorsOpened++;
            return new Closer(() => DescriptorsClosed++);
        }

        public void Dispose() => Disposed = true;

        private sealed class Closer(Action onClose) : IDisposable
        {
            public void Dispose() => onClose();
        }
    }

    private sealed class Encoder(string path, int width, int height) : IVideoEncoder
    {
        public int Width => width;

        public int Height => height;

        public int Stride => width;

        public int SliceHeight => height;

        public List<long> Pts { get; } = [];

        public bool Finished { get; private set; }

        public bool Push(byte[] nv12, int length, long ptsUs)
        {
            Assert.Equal(Nv12.FrameBytes(width, height), length);
            Pts.Add(ptsUs);
            return true;
        }

        public long Finish()
        {
            Finished = true;
            File.WriteAllBytes(path, new byte[Pts.Count * 10]);
            return Pts.Count * 10;
        }
    }

    private sealed class Platform : IShizukuPlatform
    {
        public HashSet<int> Displays { get; } = [];

        public Dictionary<string, string> Components { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, IInstallUpload> Uploads { get; } = new(StringComparer.Ordinal);

        public string? ArchivePackage { get; set; }

        public int Binds { get; private set; }

        public int Unbinds { get; private set; }

        public Encoder? Encoder { get; private set; }

        public string AppPackage => Self;

        public bool ServerAvailable => true;

        public bool BindUserService()
        {
            Binds++;
            return true;
        }

        public void UnbindUserService() => Unbinds++;

        public bool DisplayExists(int displayId) => Displays.Contains(displayId);

        public string? LaunchComponent(string packageName) => Components.GetValueOrDefault(packageName);

        public IInstallUpload? OpenInstallUpload(string name, long maxBytes) => Uploads.GetValueOrDefault(name);

        public string? ArchivePackageName(string apkPath) => ArchivePackage;

        public DecodedFrame? DecodeFrame(byte[] encoded) => new(300, 641, new int[300 * 641]);

        public IVideoEncoder? StartEncoder(string outputPath, int width, int height, int frameRate, int bitsPerSecond) =>
            Encoder = new Encoder(outputPath, width, height);
    }
}

internal static class RelaxedJson
{
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Relaxed(this JsonObject node) => node.ToJsonString(Options);
}
