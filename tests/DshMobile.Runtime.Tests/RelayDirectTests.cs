using System.Text.Json;
using DshMobile.Runtime.Automation.Relay;
using DshMobile.Runtime.Automation.Relay.Exec;
using DshMobile.Runtime.Automation.Relay.Exec.Direct;
using DshMobile.Runtime.Automation.Relay.Logging;
using DshMobile.Runtime.Automation.Relay.Storage;
using Microsoft.Extensions.Time.Testing;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// PR-07: the Direct backend (upstream exec/direct). IntentTemplates and appMatches carry the upstream JVM tests
/// (IntentTemplatesTest, AppKeywordFilterTest, ScreenProjectionIdleExpiryTest) case for case; the backend flows run over
/// a fake platform with the expectations read from DirectBackend.kt / AudioCapture.kt / ScreenProjection.kt.
/// </summary>
public sealed class RelayDirectTests : IDisposable
{
    private const string Self = "org.example.dshmobile";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dshm-direct-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new();
    private readonly RelayPaths _paths;
    private readonly StorageReaper _reaper;
    private readonly RunLog _runLog;
    private readonly Platform _platform = new();
    private readonly NotificationWindow _notes;
    private readonly Recorder _recorder = new();
    private readonly Projection _projectionPlatform = new();
    private readonly ScreenProjection _projection;
    private bool _clipboardUsable = true;
    private bool _hostInFront;
    private long _wall = 1_700_000_000_000;
    private readonly List<long> _delays = [];

    public RelayDirectTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new RelayPaths(_root, RelayPathPolicy.DshMobile);
        _runLog = new RunLog(_paths.LogsDir);
        _reaper = new StorageReaper(_paths, new QuotaLedger(_paths.QuotaFile), _runLog, () => 0, () => 0, _ => long.MaxValue);
        _notes = new NotificationWindow(() => _wall);
        _projection = new ScreenProjection(_projectionPlatform, _reaper, _runLog, _time);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private DirectBackend Backend() => new(_platform, _notes, new AudioCapture(_reaper, _recorder, Delay), _projection,
        () => _clipboardUsable, () => _hostInFront, () => _wall, Delay, "DshMobile");

    private Task Delay(long ms, CancellationToken ct)
    {
        _delays.Add(ms);
        _platform.Tick();
        return Task.CompletedTask;
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    private Task<BackendResult> Run(string capability, string args, SurfaceKind surface = SurfaceKind.Foreground) =>
        Backend().ExecuteAsync(new BackendCall("r1", capability, J(args), surface, _paths, 15_000), CancellationToken.None);

    private static string Data(BackendResult r) => RelayEncoding.EncodeData(Assert.IsType<BackendResult.Ok>(r).Data);

    private static BackendResult.Failed Failed(BackendResult r, string code, string? reason = null)
    {
        var f = Assert.IsType<BackendResult.Failed>(r);
        Assert.Equal(code, f.Error.Code);
        if (reason is not null)
        {
            Assert.Equal(reason, f.Reason);
        }
        return f;
    }

    private sealed class Platform : IDirectPlatform
    {
        public string AppPackage => Self;

        public List<(string, string)> Apps { get; } = [("com.android.deskclock", "时钟"), ("com.android.settings", "设置")];

        public IReadOnlyList<(string Package, string Label)> LaunchableApps() => Apps;

        public LaunchStart Launch { get; set; } = LaunchStart.Started;

        public List<string> Launched { get; } = [];

        public LaunchStart StartLaunch(string packageName, out string? thrown)
        {
            Launched.Add(packageName);
            thrown = Launch == LaunchStart.Threw ? "SecurityException" : null;
            return Launch;
        }

        /// <summary>Foreground per sample: the queue drains one entry per read; the last one repeats.</summary>
        public Queue<string?> Fronts { get; } = new();

        private string? _last;

        public void Tick()
        {
        }

        public int ForegroundReads { get; private set; }

        public string? ForegroundPackage()
        {
            ForegroundReads++;
            if (Fronts.Count > 0)
            {
                _last = Fronts.Dequeue();
            }
            return _last;
        }

        public bool AccessibilityConnected { get; set; } = true;

        public bool Overlay { get; set; }

        public bool OverlayGranted() => Overlay;

        public string? Resolver { get; set; } = "com.android.deskclock";

        public string? StartFailure { get; set; }

        public List<IntentTemplates.Params> Started { get; } = [];

        public string? ResolveTemplate(IntentTemplates.Params template, out string action)
        {
            action = template.Template == IntentTemplates.AlarmSet ? "android.intent.action.SET_ALARM" : "android.intent.action.VIEW";
            return Resolver;
        }

        public string? StartTemplate(IntentTemplates.Params template)
        {
            Started.Add(template);
            return StartFailure;
        }

        public IEnumerable<(string? Name, string? Number)> QueryPhones() => Enumerable.Range(0, 500).Select(i => ((string?)$"n{i}", i == 1 ? null : (string?)$"{i}"));

        public int InsertContact(string name, string? number) => name == "boom" ? throw new InvalidOperationException() : number is null ? 2 : 3;

        public IEnumerable<(long CalendarId, string? Title, long StartMs, long EndMs)> QueryEvents() => [(1, "a", 10, 20)];

        public long? CalendarId { get; set; } = 7;

        public long? FirstCalendarId() => CalendarId;

        public List<(long, string, long, long)> Events { get; } = [];

        public long InsertEvent(long calendarId, string title, long startMs, long endMs)
        {
            Events.Add((calendarId, title, startMs, endMs));
            return 42;
        }

        public (int EnabledProviders, IReadOnlyList<LocationFix> Fixes)? Location { get; set; }

        public (int EnabledProviders, IReadOnlyList<LocationFix> Fixes)? LastKnownLocations() => Location;

        public IEnumerable<(string Uri, string? Name)> QueryMedia(string kind) => [($"content://media/{kind}/1", "a.png")];

        public IVettedUpload? Upload { get; set; }

        public IVettedUpload? OpenVettedUpload(string name, long maxBytes) => Upload;

        public long? Volume { get; set; }

        public long? TargetVolumeAvailableBytes() => Volume;

        public string? MimeFromExtension(string extension) => extension == "png" ? "image/png" : extension == "txt" ? "text/plain" : null;

        public List<string> Inserted { get; } = [];

        public string? InsertPendingMedia(string kind, string displayName, string mime, string relativePath)
        {
            Inserted.Add($"{kind}|{displayName}|{mime}|{relativePath}");
            return "content://media/external/images/media/9";
        }

        public bool WriteOk { get; set; } = true;

        public bool WriteMedia(string uri, Stream source, long bytes) => WriteOk;

        public int ReleasePendingMedia(string uri) => 1;

        public List<string> Deleted { get; } = [];

        public void DeleteMedia(string uri) => Deleted.Add(uri);

        public bool NotificationServiceAvailable => true;

        public List<(string, string, int)> Posted { get; } = [];

        public void PostNotification(string title, string text, int id) => Posted.Add((title, text, id));

        public ClipRead Clip { get; set; } = new ClipRead.NoClip();

        public ClipRead ReadClipboard() => Clip;

        public List<string> Clips { get; } = [];

        public void WriteClipboard(string text) => Clips.Add(text);
    }

    private sealed class Upload(byte[] bytes) : IVettedUpload
    {
        public long Size => bytes.Length;

        public bool SourceDeleted { get; private set; }

        public int ReadAt(long position, Span<byte> buffer)
        {
            if (position >= bytes.Length)
            {
                return 0;
            }
            var n = Math.Min(buffer.Length, bytes.Length - (int)position);
            bytes.AsSpan((int)position, n).CopyTo(buffer);
            return n;
        }

        public Stream OpenRead() => new MemoryStream(bytes);

        public void DeleteSource() => SourceDeleted = true;

        public void Dispose()
        {
        }
    }

    private sealed class Recorder : IAudioRecorder
    {
        public string? Failure { get; set; }

        public bool StopOk { get; set; } = true;

        public int Released { get; private set; }

        public IAudioRecording? Start(string outputPath, int seconds, out string? failure)
        {
            failure = Failure;
            if (Failure is not null)
            {
                return null;
            }
            return new Recording(this, outputPath);
        }

        private sealed class Recording(Recorder owner, string path) : IAudioRecording
        {
            public bool Stop()
            {
                if (owner.StopOk)
                {
                    File.WriteAllBytes(path, [1, 2, 3]);
                }
                return owner.StopOk;
            }

            public void Release() => owner.Released++;
        }
    }

    private sealed class Token : IProjectionToken
    {
        public int Stops { get; private set; }

        public void Stop() => Stops++;
    }

    private sealed class Projection : IProjectionPlatform
    {
        public bool HasIntent { get; set; } = true;

        public bool Host { get; set; } = true;

        public int HostStarts { get; private set; }

        public object? CreateConsentIntent() => HasIntent ? "intent" : null;

        public bool StartConsentHost()
        {
            HostStarts++;
            return Host;
        }

        public bool ServiceStarts { get; set; } = true;

        public Action? OnReady { get; private set; }

        public bool StartProjectionService(Action onReady)
        {
            OnReady = onReady;
            return ServiceStarts;
        }

        public int ServiceStops { get; private set; }

        public void StopProjectionService() => ServiceStops++;

        public Token? Minted { get; private set; }

        public IProjectionToken? Mint(int resultCode, object data, Action onStop, out string? failure)
        {
            failure = null;
            Minted = new Token();
            return Minted;
        }

        public int MetricsWidth => 1080;

        public int MetricsHeight => 2400;

        public (int W, int H)? Grabbed { get; private set; }

        public Task<FrameGrab> GrabFrameAsync(IProjectionToken token, int width, int height, CancellationToken ct)
        {
            Grabbed = (width, height);
            return Task.FromResult<FrameGrab>(new FrameGrab.Frame(width, height, path =>
            {
                File.WriteAllBytes(path, [0x89, 0x50]);
                return true;
            }, () => { }));
        }

        public IScreenRecording? StartRecording(IProjectionToken token, string path, int width, int height, int frameRate, int bitsPerSecond,
            out string? failure)
        {
            failure = "recorder";
            return null;
        }
    }

    // --- upstream IntentTemplatesTest -----------------------------------------------------------------------------

    private static IntentTemplates.Params OkT(string json) => Assert.IsType<IntentTemplates.Outcome.Ok>(IntentTemplates.Validate(J(json))).Params;

    private static string BadT(string json) => Assert.IsType<IntentTemplates.Outcome.Bad>(IntentTemplates.Validate(J(json))).Reason;

    [Fact]
    public void IntentTemplates_MatchTheUpstreamTable()
    {
        var alarm = OkT("""{"template":"alarm.set","hour":23,"minute":40}""");
        Assert.Equal((IntentTemplates.AlarmSet, 23, 40), (alarm.Template, alarm.Hour, alarm.Minute));
        Assert.Contains("0..23", BadT("""{"template":"alarm.set","hour":25,"minute":0}"""), StringComparison.Ordinal);
        Assert.Contains("0..59", BadT("""{"template":"alarm.set","hour":7,"minute":60}"""), StringComparison.Ordinal);
        Assert.Equal("hour must be within 0..23, got -1", BadT("""{"template":"alarm.set","hour":-1,"minute":0}"""));
        Assert.Equal("missing arg: hour", BadT("""{"template":"alarm.set","minute":5}"""));
        Assert.Equal("missing arg: minute", BadT("""{"template":"alarm.set","hour":5}"""));
        Assert.Equal(90, OkT("""{"template":"timer.set","length":90}""").LengthSeconds);
        Assert.Equal("length must be within 1..86400 seconds, got 0", BadT("""{"template":"timer.set","length":0}"""));
        Assert.Contains("seconds", BadT("""{"template":"timer.set","length":90000}"""), StringComparison.Ordinal);
        Assert.Equal("missing arg: length", BadT("""{"template":"timer.set"}"""));
        Assert.Equal("unknown template: alarm.delete, accepted: [alarm.set, timer.set, alarm.show, timer.show, settings.open, app.info, dial, web.open]",
            BadT("""{"template":"alarm.delete","hour":1,"minute":1}"""));
        Assert.Equal("missing arg: template", BadT("""{"hour":7}"""));
        Assert.Equal("message must be at most 100 characters, got 101",
            BadT($$"""{"template":"alarm.set","hour":7,"minute":0,"message":"{{new string('a', 101)}}"}"""));
        Assert.Equal("起床", OkT("""{"template":"alarm.set","hour":7,"minute":0,"message":" 起床 "}""").Message);
        Assert.Equal(IntentTemplates.AlarmShow, OkT("""{"template":"alarm.show"}""").Template);
        Assert.Equal(IntentTemplates.TimerShow, OkT("""{"template":"timer.show"}""").Template);
        Assert.Equal("wifi", OkT("""{"template":"settings.open","page":"wifi"}""").Page);
        Assert.Equal("nfc", OkT("""{"template":"settings.open","page":"NFC"}""").Page);
        Assert.Equal("missing arg: page, accepted: [wifi, bluetooth, display, location, sound, apn, developer, nfc]", BadT("""{"template":"settings.open"}"""));
        Assert.Equal("page 'airplane' is not one of [wifi, bluetooth, display, location, sound, apn, developer, nfc]",
            BadT("""{"template":"settings.open","page":"airplane"}"""));
        Assert.Equal("com.x.y", OkT("""{"template":"app.info","package":"com.x.y"}""").PackageName);
        Assert.Equal("missing arg: package", BadT("""{"template":"app.info"}"""));
        Assert.Equal("package is not a package name: has spaces", BadT("""{"template":"app.info","package":"has spaces"}"""));
        Assert.NotEmpty(BadT("""{"template":"app.info","package":"com.x/y"}"""));
        Assert.Equal("+86 138-0000", OkT("""{"template":"dial","number":"+86 138-0000"}""").Number);
        Assert.NotEmpty(BadT("""{"template":"dial","number":"138;rm -rf"}"""));
        Assert.NotEmpty(BadT("""{"template":"dial","number":"http://x"}"""));
        Assert.NotEmpty(BadT($$"""{"template":"dial","number":"{{new string('1', 33)}}"}"""));
        Assert.Equal("missing arg: number", BadT("""{"template":"dial"}"""));
        Assert.Equal("https://example.com/a?b=1", OkT("""{"template":"web.open","url":"https://example.com/a?b=1"}""").Url);
        foreach (var bad in new[] { "file:///sdcard/x", "content://mms", "intent:#Intent", "javascript:alert(1)" })
        {
            Assert.NotEmpty(BadT($$"""{"template":"web.open","url":"{{bad}}"}"""));
        }
        Assert.Equal("url must start with http:// or https://, got scheme 'intent:#intent'", BadT("""{"template":"web.open","url":"intent:#Intent"}"""));
        Assert.Equal("url must not contain whitespace", BadT("""{"template":"web.open","url":"https://a b"}"""));
        Assert.Equal("url must be at most 512 characters, got 520", BadT($$"""{"template":"web.open","url":"https://{{new string('a', 512)}}"}"""));
        var banned = new[] { "delete", "remove", "dismiss", "snooze", "clear", "uninstall", "stop" };
        Assert.DoesNotContain(IntentTemplates.Names, n => banned.Any(b => n.Contains(b, StringComparison.Ordinal)));
    }

    [Fact]
    public void AppMatches_IsTheUpstreamFilter()
    {
        Assert.True(DirectBackend.AppMatches("", "com.foo", "Foo"));
        Assert.True(DirectBackend.AppMatches("   ", "com.foo", "Foo"));
        Assert.True(DirectBackend.AppMatches("deskclock", "com.android.deskclock", "时钟"));
        Assert.True(DirectBackend.AppMatches("DESK", "com.android.deskclock", "时钟"));
        Assert.True(DirectBackend.AppMatches("时钟", "com.android.deskclock", "闹钟时钟"));
        Assert.False(DirectBackend.AppMatches("clock", "com.android.settings", "设置"));
    }

    [Fact]
    public void IdleTimeLeft_IsWhatRemainsOfTheWindow()
    {
        Assert.Equal(60_000, ScreenProjection.IdleTimeLeftMs(1_000, 1_000, 60_000));
        Assert.Equal(0, ScreenProjection.IdleTimeLeftMs(1_000, 61_000, 60_000));
        Assert.Equal(-5_000, ScreenProjection.IdleTimeLeftMs(1_000, 66_000, 60_000));
    }

    [Theory]
    [InlineData("89504e470d0a1a0a0000000d", "image", true)]
    [InlineData("ffd8ffe000104a464946", "image", true)]
    // Upstream reads the RIFF form at bytes 12..15 (substring(24, 32)), not 8..11 where WEBP/WAVE/AVI really are: a
    // real WEBP header does not match on the RIFF rule. Kept for parity with the legacy decider (docs/UPSTREAMS.md).
    [InlineData("524946462400000057454250565038204c", "image", false)]
    [InlineData("52494646aaaaaaaa0000000057454250", "image", true)]
    [InlineData("52494646aaaaaaaa57454250", "image", false)]
    [InlineData("0000001866747970686569630000", "image", true)]
    [InlineData("000000186674797069736f6d0000", "image", false)]
    [InlineData("000000186674797069736f6d0000", "video", true)]
    [InlineData("000000186674797069736f6d0000", "audio", true)]
    [InlineData("49443304", "audio", true)]
    [InlineData("fff14c80", "audio", true)]
    [InlineData("fff1", "audio", false)]
    [InlineData("1a45dfa3", "video", true)]
    [InlineData("48656c6c6f20776f726c64", "image", false)]
    [InlineData("ffd8", "image", false)]
    public void HeaderMatchesKind_ByMagicNumber(string hex, string kind, bool expected) =>
        Assert.Equal(expected, DirectBackend.HeaderMatchesKind(Convert.FromHexString(hex), kind));

    // --- backend flows --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Supports_NeverTheTrustedDisplay_AndClipboardOnlyInFront()
    {
        var b = Backend();
        Assert.False(b.Supports("pkg.query", SurfaceKind.BackendTrusted));
        Assert.True(b.Supports("pkg.query", SurfaceKind.ObserveOnly));
        Assert.True(b.Supports("clip.read", SurfaceKind.Foreground));
        Assert.False(b.Supports("clip.read", SurfaceKind.ObserveOnly));
        Assert.True(b.Supports("screen.observe", SurfaceKind.ObserveOnly));
        Assert.False(b.Supports("screen.observe", SurfaceKind.Foreground));
        Assert.False(b.Supports("ui.tap", SurfaceKind.Foreground));
        Failed(await Run("contact.write", """{"name":" "}"""), "E_TRANSPORT_MALFORMED", "missing arg: name");
        Failed(await Run("pkg.query", """{"q":"x"}"""), "E_TRANSPORT_MALFORMED");
        Failed(await Run("app.stop", "{}"), "E_NOT_IMPLEMENTED", "app.stop");
    }

    [Fact]
    public async Task PkgQuery_SaysTotalWhenFiltered()
    {
        Assert.Equal("""{"count":2,"apps":[{"package":"com.android.deskclock","name":"时钟"},{"package":"com.android.settings","name":"设置"}]}""",
            Data(await Run("pkg.query", "{}")));
        Assert.Equal("""{"count":1,"apps":[{"package":"com.android.deskclock","name":"时钟"}],"keyword":"clock","total":2}""",
            Data(await Run("pkg.query", """{"keyword":" clock "}""")));
    }

    [Fact]
    public async Task SysIntent_RefusesABackgroundStart_InsteadOfAFalseSuccess()
    {
        _platform.Fronts.Enqueue("com.android.launcher");
        var f = Failed(await Run("sys.intent", """{"template":"alarm.set","hour":7,"minute":5}"""), "E_GATE_NO_FOREGROUND");
        Assert.StartsWith("would be blocked as a background activity start (foreground is com.android.launcher)", f.Reason, StringComparison.Ordinal);
        Assert.Empty(_platform.Started);
        _platform.Overlay = true;
        Assert.Equal("""{"template":"alarm.set","resolvedPackage":"com.android.deskclock","skipUi":true,"hour":7,"minute":5,"note":"the clock app took the request without a screen (SKIP_UI); it does not report back, so this says the hand-off happened, not that a specific alarm now exists"}""",
            Data(await Run("sys.intent", """{"template":"alarm.set","hour":7,"minute":5}""")));
        Assert.Equal("""{"template":"dial","resolvedPackage":"com.android.deskclock","numberLength":5,"note":"the dialer opened with this number; nothing was called - ACTION_DIAL never places the call"}""",
            Data(await Run("sys.intent", """{"template":"dial","number":"10086"}""")));
        _platform.StartFailure = "SecurityException:not allowed";
        Failed(await Run("sys.intent", """{"template":"alarm.show"}"""), "E_BACKEND_UNAVAILABLE",
            "startActivity refused for com.android.deskclock: SecurityException:not allowed");
        _platform.Resolver = null;
        Failed(await Run("sys.intent", """{"template":"alarm.set","hour":7,"minute":5}"""), "E_BACKEND_UNAVAILABLE",
            "no app on this device handles android.intent.action.SET_ALARM: a template that is in the table can still have no receiver here, which is a property of this device, not of the argument");
        Failed(await Run("sys.intent", """{"template":"alarm.set","hour":24,"minute":5}"""), "E_TRANSPORT_MALFORMED", "hour must be within 0..23, got 24");
    }

    [Fact]
    public async Task Launch_IsVerifiedByWhoHoldsTheFront()
    {
        _platform.Fronts.Enqueue(Self);
        _platform.Fronts.Enqueue("com.android.deskclock");
        Assert.Equal("""{"package":"com.android.deskclock","verified":true}""", Data(await Run("app.launch", """{"package":"com.android.deskclock"}""")));
        Assert.Equal([200L], _delays);

        _delays.Clear();
        _platform.Fronts.Enqueue("com.other");
        var f = Failed(await Run("app.launch", """{"package":"com.android.deskclock"}"""), "E_LAUNCH_NOT_LANDED");
        Assert.Contains("foreground is com.other", f.Reason, StringComparison.Ordinal);
        Assert.Equal(9, _delays.Count);

        _delays.Clear();
        _platform.Fronts.Enqueue(null);
        var cold = Assert.IsType<BackendResult.Ok>(await Run("app.launch", """{"package":"com.android.deskclock"}"""));
        Assert.Equal("""{"package":"com.android.deskclock","verified":false}""", RelayEncoding.EncodeData(cold.Data));
        Assert.Equal("no foreign window seen yet: still launching or blocked by this app", cold.Reason);
        Assert.Equal(2, _delays.Count);

        _platform.Fronts.Enqueue("com.vivo.doubleinstance");
        var clone = Assert.IsType<BackendResult.Ok>(await Run("app.launch", """{"package":"com.android.deskclock"}"""));
        Assert.Equal("""{"package":"com.android.deskclock","observed":"com.vivo.doubleinstance","verified":false}""", RelayEncoding.EncodeData(clone.Data));
        Assert.StartsWith("foreground is clone container com.vivo.doubleinstance", clone.Reason, StringComparison.Ordinal);

        _platform.Launch = LaunchStart.NotLaunchable;
        Failed(await Run("app.launch", """{"package":"时钟"}"""), "E_TRANSPORT_MALFORMED",
            "not launchable: 时钟 (expected a package name from pkg.query, not a display name)");
        _platform.Launch = LaunchStart.Threw;
        Failed(await Run("app.launch", """{"package":"com.x"}"""), "E_INTERNAL", "startActivity threw SecurityException");
    }

    [Fact]
    public async Task Launch_OfTheHostWhileInFront_StartsNothing()
    {
        _hostInFront = true;
        _platform.Fronts.Enqueue(Self);
        var ok = Assert.IsType<BackendResult.Ok>(await Run("app.launch", $$"""{"package":"{{Self}}"}"""));
        Assert.Equal($$"""{"package":"{{Self}}","verified":true,"alreadyInFront":true,"observed":"{{Self}}"}""", RelayEncoding.EncodeData(ok.Data));
        Assert.Equal("this app was already in the foreground; nothing was started", ok.Reason);
        Assert.Empty(_platform.Launched);
    }

    [Fact]
    public async Task DataReads_AreClampedAndNullColumnsLeftOut()
    {
        var contacts = Data(await Run("contact.read", """{"limit":2}"""));
        Assert.Equal("""{"count":2,"contacts":[{"name":"n0","number":"0"},{"name":"n1"}]}""", contacts);
        Assert.StartsWith("""{"count":200,""", Data(await Run("contact.read", """{"limit":2147483647}""")), StringComparison.Ordinal);
        Assert.StartsWith("""{"count":1,""", Data(await Run("contact.read", """{"limit":-4}""")), StringComparison.Ordinal);
        Assert.Equal("""{"operations":2}""", Data(await Run("contact.write", """{"name":"Ann","number":" "}""")));
        Failed(await Run("contact.write", """{"name":"boom"}"""), "E_INTERNAL", "contact write rejected: InvalidOperationException");
        Assert.Equal("""{"count":1,"events":[{"calendarId":1,"title":"a","startMs":10,"endMs":20}]}""", Data(await Run("cal.read", "{}")));
        Assert.Equal("""{"eventId":42}""", Data(await Run("cal.write", """{"title":"t","startMs":1000}""")));
        Assert.Equal((7L, "t", 1000L, 3_601_000L), _platform.Events[0]);
        _platform.CalendarId = null;
        Failed(await Run("cal.write", """{"title":"t"}"""), "E_GATE_SYSTEM_MISSING", "no calendar account");
        Failed(await Run("media.read", """{"kind":"audio"}"""), "E_TRANSPORT_MALFORMED", "kind must be image/video");
        Assert.Equal("""{"count":1,"items":[{"uri":"content:\/\/media\/image\/1","name":"a.png"}]}""", Data(await Run("media.read", "{}")));
    }

    [Fact]
    public async Task Location_TakesTheNewestFix()
    {
        Failed(await Run("loc.read", "{}"), "E_CAPABILITY_UNAVAILABLE_ON_DEVICE", "no location service");
        _platform.Location = (0, []);
        Failed(await Run("loc.read", "{}"), "E_BACKEND_UNAVAILABLE", "no location fix yet (enabled providers: 0)");
        _platform.Location = (2, [new LocationFix(1.5, 2, 12.5f, _wall - 9000, "network"), new LocationFix(30.25, 120.5, 4f, _wall - 1000, "gps")]);
        Assert.Equal("""{"latitude":30.25,"longitude":120.5,"accuracyMeters":4,"fixAgeMs":1000,"provider":"gps"}""", Data(await Run("loc.read", "{}")));
    }

    [Fact]
    public async Task MediaWrite_ChecksTheContent_AndNeverLeavesAHalfRow()
    {
        Failed(await Run("media.write", """{"file":"x.png"}"""), "E_TRANSPORT_MALFORMED", "no such upload: x.png");
        var text = new Upload("hello world, not a picture"u8.ToArray());
        _platform.Upload = text;
        Failed(await Run("media.write", """{"file":"r.txt"}"""), "E_TRANSPORT_MALFORMED", "content does not match kind=image");
        Assert.True(text.SourceDeleted);
        _platform.Upload = new Upload(Convert.FromHexString("89504e470d0a1a0a0000000d49484452"));
        Assert.Equal("""{"uri":"content:\/\/media\/external\/images\/media\/9","bytes":16}""", Data(await Run("media.write", """{"file":"a.png"}""")));
        Assert.Equal("image|a.png|image/png|Pictures/DshMobile/", _platform.Inserted[^1]);
        _platform.Upload = new Upload(Convert.FromHexString("89504e470d0a1a0a0000000d49484452"));
        _platform.WriteOk = false;
        Failed(await Run("media.write", """{"file":"a.png"}"""), "E_INTERNAL", "media publish failed");
        Assert.Single(_platform.Deleted);
        _platform.Upload = new Upload(Convert.FromHexString("89504e47"));
        _platform.Volume = 1024;
        Failed(await Run("media.write", """{"file":"a.png"}"""), "E_STORAGE_FULL", "target volume has no room");
        Failed(await Run("media.write", """{"file":"a.png","kind":"doc"}"""), "E_TRANSPORT_MALFORMED", "kind must be image/video/audio");
    }

    [Fact]
    public async Task Clipboard_TellsEmptyFromMasked()
    {
        Assert.Equal("""{"text":"","length":0}""", Data(await Run("clip.read", "{}")));
        _platform.Clip = new ClipRead.Clip(null);
        Failed(await Run("clip.read", "{}"), "E_CLIPBOARD_NO_FOCUS", "clip content masked without focus");
        _platform.Clip = new ClipRead.Clip("hi");
        Assert.Equal("""{"text":"hi","length":2}""", Data(await Run("clip.read", "{}")));
        Assert.Equal("""{"written":3}""", Data(await Run("clip.write", """{"text":"abc"}""")));
        _clipboardUsable = false;
        Failed(await Run("clip.write", """{"text":"abc"}"""), "E_CLIPBOARD_NO_FOCUS", "clipboard needs app focus");
        Assert.Equal(["abc"], _platform.Clips);
    }

    [Fact]
    public async Task Notifications_AreAWindowNotAnArchive()
    {
        Failed(await Run("notify.read", "{}"), "E_BACKEND_UNAVAILABLE", "notification listener not bound");
        var listener = new object();
        _notes.Attach(listener, [new NotificationEntry("a/1/null", "a", "old", "", _wall - NotificationWindow.WindowMs - 1, false)]);
        _notes.Record(new NotificationEntry("b/1/null", "b", "Hi", "10%", _wall - 10, false));
        _notes.Record(new NotificationEntry("b/1/null", "b", "Hi", "90%", _wall - 5, false));
        _notes.Record(new NotificationEntry("c/1/null", "c", "Music", "", _wall, true));
        _notes.Record(new NotificationEntry("d/1/null", "d", "", "", _wall, false));
        Assert.Equal("""{"count":1,"notifications":[{"package":"b","title":"Hi","text":"90%","postedAtMs":1699999999995}]}""",
            Data(await Run("notify.read", "{}")));
        Assert.Equal(2, _notes.Snapshot(10, null, includeOngoing: true).Count);
        Assert.StartsWith("""{"count":1,""", Data(await Run("notify.read", """{"package":"c","includeOngoing":"TRUE"}""")), StringComparison.Ordinal);
        _notes.Retire("b/1/null");
        Assert.Empty(_notes.Snapshot(10, null, false));
        _notes.Detach(listener);
        Assert.False(_notes.IsConnected);
        Assert.Equal("""{"posted":true}""", Data(await Run("notify.post", """{"title":"t","id":"5"}""")));
        Assert.Equal(("t", "", 5), _platform.Posted[0]);
    }

    [Fact]
    public async Task Audio_OneAtATime_AndTheGrantIsNamed()
    {
        var ok = Assert.IsType<BackendResult.Ok>(await Run("audio.capture", """{"seconds":99}"""));
        Assert.Equal("""{"seconds":30,"bytes":3}""", RelayEncoding.EncodeData(ok.Data));
        Assert.Equal([30_000L], _delays);
        Assert.Single(ok.Artifacts);
        _recorder.Failure = "security";
        Failed(await Run("audio.capture", "{}"), "E_GATE_SYSTEM_MISSING", "RECORD_AUDIO not granted: recorder start refused");
        _recorder.Failure = "IllegalStateException";
        Failed(await Run("audio.capture", "{}"), "E_BACKEND_UNAVAILABLE", "recorder rejected: IllegalStateException");
        _recorder.Failure = null;
        _recorder.StopOk = false;
        Failed(await Run("audio.capture", "{}"), "E_BACKEND_UNAVAILABLE", "capture stopped early");
        Assert.Equal(2, _recorder.Released);
    }

    [Fact]
    public async Task Projection_WithoutASurface_AnswersNoForeground()
    {
        _projectionPlatform.Host = false;
        var f = Failed(await Run("screen.capture", "{}"), "E_GATE_NO_FOREGROUND", "no foreground surface can present the system capture dialog");
        Assert.Equal(RelayResponse.ConsentKindAndroidProjection, f.ConsentKind);
        Assert.Null(_projection.ConsentRequest);
    }

    [Fact]
    public async Task Projection_GrantedOnce_CapturesOneFrame_ThenEndsTheSession()
    {
        var pending = Run("screen.capture", "{}");
        Assert.Equal("intent", _projection.ConsentRequest);
        _projection.OnConsentResult(ScreenProjection.ResultOk, "token-data");
        _projectionPlatform.OnReady!();
        var ok = Assert.IsType<BackendResult.Ok>(await pending);
        Assert.Equal("""{"width":540,"height":1200}""", RelayEncoding.EncodeData(ok.Data));
        Assert.Equal(1, _projectionPlatform.Minted!.Stops);
        Assert.Null(_projection.ConsentRequest);

        // The next capture needs the dialog again; a decline is a consent answer, not a backend fault.
        var second = Run("screen.capture", "{}");
        _projection.OnConsentResult(0, null);
        var f = Failed(await second, "E_SURFACE_SYSTEM_CONSENT_REQUIRED", "user declined the system capture dialog");
        Assert.Equal(RelayResponse.ConsentKindAndroidProjection, f.ConsentKind);
    }

    [Fact]
    public async Task Projection_StillWaiting_AfterTheGrace_SaysAwaitingConsent()
    {
        var pending = Run("screen.capture", "{}");
        _time.Advance(TimeSpan.FromMilliseconds(ScreenProjection.ConsentGraceMs));
        Failed(await pending, "E_AWAITING_CONSENT", "system capture dialog is waiting for the user");
        Assert.Equal("intent", _projection.ConsentRequest);

        // The mint must happen within 5 s of the grant, or the session is refused.
        var retry = Run("screen.capture", "{}");
        _projection.OnConsentResult(ScreenProjection.ResultOk, "token-data");
        _time.Advance(TimeSpan.FromMilliseconds(ScreenProjection.MintTimeoutMs));
        Failed(await retry, "E_BACKEND_UNAVAILABLE", "system refused the capture token for this session");
        Assert.Equal(1, _projectionPlatform.HostStarts);
    }
}
