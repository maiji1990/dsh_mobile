using System.Text.Json;
using DshMobile.Runtime.Automation.Relay;
using DshMobile.Runtime.Automation.Relay.A11y;
using DshMobile.Runtime.Automation.Relay.Exec;
using DshMobile.Runtime.Automation.Relay.Logging;
using DshMobile.Runtime.Automation.Relay.Storage;
using Microsoft.Extensions.Time.Testing;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// The accessibility backend's own routing (upstream A11yBackend.execute / supports / click / swipe / setText /
/// globalAction / capture), over a fake platform. Expectations are read from upstream A11yBackend.kt; the node-level
/// writes behind it are locked by relay-a11y-write.json.
/// </summary>
public sealed class RelayA11yBackendTests : IDisposable
{
    private const string Self = "org.example.dshmobile";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dshm-a11y-backend-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new();
    private readonly Platform _platform;
    private readonly RelayPaths _paths;
    private readonly A11yBackend _backend;

    public RelayA11yBackendTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new RelayPaths(_root, RelayPathPolicy.DshMobile);
        var reaper = new StorageReaper(_paths, new QuotaLedger(_paths.QuotaFile), new RunLog(_paths.LogsDir), () => 0, () => 0, _ => long.MaxValue);
        _platform = new Platform(_time);
        _backend = new A11yBackend(_platform, reaper, _time);
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

    private sealed class Platform(FakeTimeProvider time) : IA11yPlatform
    {
        public bool Connected { get; set; } = true;

        public string SelfPackage => Self;

        public int Sdk { get; set; } = 37;

        public int DensityDpi => 480;

        public Bounds DisplayBounds() => new(0, 0, 1080, 2400);

        public IA11yNode? Root { get; set; }

        public IA11yNode? RootInActiveWindow() => Root;

        public List<string?> Windows { get; } = ["com.example"];

        public IReadOnlyList<string?> WindowRootPackages() => Windows;

        public TreeLook TreeOfDisplay(int displayId) => new TreeLook.NoWindowOnDisplay();

        public List<(float, float, float, float, long)> Strokes { get; } = [];

        public string? StrokeFailure { get; set; }

        public Task<string?> DispatchStrokeAsync(float fromX, float fromY, float toX, float toY, long durationMs, CancellationToken ct)
        {
            Strokes.Add((fromX, fromY, toX, toY, durationMs));
            time.Advance(TimeSpan.FromMilliseconds(StrokeFailure is null ? durationMs : 5));
            return Task.FromResult(StrokeFailure);
        }

        public List<int> GlobalActions { get; } = [];

        public bool GlobalActionWorks { get; set; } = true;

        public bool PerformGlobalAction(int action)
        {
            GlobalActions.Add(action);
            return GlobalActionWorks;
        }

        public Func<A11yShot> Shot { get; set; } = () => new A11yShot.Refused(RelayError.GateSystemMissing, "unset");

        public int Released { get; set; }

        public Task<A11yShot> TakeScreenshotAsync(CancellationToken ct) => Task.FromResult(Shot());
    }

    private sealed class Field(string cls, string? pkg, bool editable, IReadOnlyList<Field>? kids = null, bool accepts = true) : IA11yNode
    {
        private readonly IReadOnlyList<Field> _kids = kids ?? [];

        public string? Text { get; private set; }

        public string? ContentDescription => null;

        public string? ViewId => null;

        public string? ClassName => cls;

        public string? PackageName => pkg;

        public int SiblingIndex => 0;

        public IReadOnlyList<INodeView> Children => _kids;

        public Bounds BoundsInScreen => new(0, 0, 100, 100);

        public int ChildCount => _kids.Count;

        public IA11yNode? GetChild(int index) => _kids[index];

        public bool IsScrollable => false;

        public bool IsClickable => false;

        public bool IsEditable => editable;

        public bool IsChecked => false;

        public bool IsCheckable => false;

        public bool IsEnabled => true;

        public bool IsSelected => false;

        public bool IsVisibleToUser => true;

        public bool IsFocused => false;

        public IReadOnlyList<int> ActionIds => [];

        public A11yRange? Range => null;

        public Field? Focus { get; set; }

        public IA11yNode? FindInputFocus() => Focus;

        public bool PerformAction(int action, A11yActionArgs? args)
        {
            if (!accepts || action != A11yConstants.ActionSetText)
            {
                return false;
            }
            Text = args?.SetText;
            return true;
        }
    }

    private Task<BackendResult> Run(string capability, string args, SurfaceKind surface = SurfaceKind.Foreground) =>
        _backend.ExecuteAsync(new BackendCall("r1", capability, JsonDocument.Parse(args).RootElement, surface, _paths, 15_000), CancellationToken.None);

    private static void AssertFailed(BackendResult r, string code, string? reason = null)
    {
        var f = Assert.IsType<BackendResult.Failed>(r);
        Assert.Equal(code, f.Error.Code);
        if (reason is not null)
        {
            Assert.Equal(reason, f.Reason);
        }
    }

    private static string Data(BackendResult r) => RelayEncoding.EncodeData(Assert.IsType<BackendResult.Ok>(r).Data);

    [Fact]
    public async Task ArgsAreCheckedBeforeTheServiceIsAsked()
    {
        _platform.Connected = false;
        AssertFailed(await Run("ui.tap", """{"x":1,"y":2,"z":3}"""), "E_TRANSPORT_MALFORMED");
        AssertFailed(await Run("ui.tap", """{"x":1,"y":2}"""), "E_BACKEND_UNAVAILABLE", "service not connected");
        Assert.Empty(_platform.Strokes);
    }

    [Fact]
    public async Task HostWindowOnScreen_SuspendsWritesButNotReads()
    {
        _platform.Windows.Add(Self);
        _platform.Root = new Field("android.widget.FrameLayout", "com.example", false);
        AssertFailed(await Run("ui.tap", """{"x":1,"y":2}"""), "E_TASK_SUSPENDED_BY_HOST");
        AssertFailed(await Run("ui.click", """{"nodeId":0}"""), "E_TASK_SUSPENDED_BY_HOST");
        Assert.IsType<BackendResult.Ok>(await Run("ui.snapshot", "{}"));
        Assert.Empty(_platform.Strokes);
    }

    [Fact]
    public async Task Tap_DispatchesOneShortStroke_AndReportsTheObservedDuration()
    {
        Assert.Equal("""{"durationMs":60}""", Data(await Run("ui.tap", """{"x":100.5,"y":200}""")));
        Assert.Equal((100.5f, 200f, 100.5f, 200f, 60L), Assert.Single(_platform.Strokes));
        AssertFailed(await Run("ui.tap", """{"x":1080,"y":2}"""), "E_TRANSPORT_MALFORMED", "x out of screen [0,1080): 1080.0");
        AssertFailed(await Run("ui.tap", """{"x":-1,"y":2}"""), "E_TRANSPORT_MALFORMED", "x out of screen [0,1080): -1.0");
        AssertFailed(await Run("ui.tap", """{"x":"5","y":2}"""), "E_TRANSPORT_MALFORMED", "x must be a number, got \"5\" (string)");
        _platform.StrokeFailure = "cancelled";
        AssertFailed(await Run("ui.tap", """{"x":1,"y":2}"""), "E_BACKEND_UNAVAILABLE", "gesture cancelled");
    }

    [Fact]
    public async Task Swipe_ClampsTheDuration_AndDefaultsIt()
    {
        Assert.Equal("""{"durationMs":260}""", Data(await Run("ui.swipe", """{"fromX":1,"fromY":2,"toX":3,"toY":4}""")));
        Assert.Equal("""{"durationMs":50}""", Data(await Run("ui.swipe", """{"fromX":1,"fromY":2,"toX":3,"toY":4,"durationMs":10}""")));
        Assert.Equal("""{"durationMs":3000}""", Data(await Run("ui.swipe", """{"fromX":1,"fromY":2,"toX":3,"toY":4,"durationMs":1e9}""")));
        Assert.Equal((1f, 2f, 3f, 4f, 260L), _platform.Strokes[0]);
        AssertFailed(await Run("ui.swipe", """{"fromX":1,"fromY":2,"toX":3,"toY":4,"durationMs":"fast"}"""), "E_TRANSPORT_MALFORMED",
            "durationMs must be a number, got \"fast\" (string)");
        AssertFailed(await Run("ui.swipe", """{"fromX":1,"fromY":2,"toX":3,"toY":2400}"""), "E_TRANSPORT_MALFORMED", "toY out of screen [0,2400): 2400.0");
        _platform.StrokeFailure = "rejected";
        AssertFailed(await Run("ui.swipe", """{"fromX":1,"fromY":2,"toX":3,"toY":4}"""), "E_BACKEND_UNAVAILABLE", "gesture rejected");
    }

    [Fact]
    public async Task Text_GoesToTheFocusedField_ElseTheFirstEditable()
    {
        AssertFailed(await Run("ui.text", """{"text":"hi"}"""), "E_BACKEND_UNAVAILABLE");
        var second = new Field("android.widget.EditText", "com.example", true);
        var first = new Field("android.widget.EditText", "com.example", true);
        var root = new Field("android.widget.FrameLayout", "com.example", false, [new Field("android.widget.TextView", "com.example", false), first, second]);
        _platform.Root = root;
        Assert.Equal("""{"length":5}""", Data(await Run("ui.text", """{"text":"héllo"}""")));
        Assert.Equal("héllo", first.Text);
        root.Focus = second;
        Assert.Equal("""{"length":2}""", Data(await Run("ui.text", """{"text":"ok"}""")));
        Assert.Equal("ok", second.Text);
        _platform.Root = new Field("android.widget.FrameLayout", "com.example", false, [new Field("android.widget.TextView", "com.example", false)]);
        AssertFailed(await Run("ui.text", """{"text":"x"}"""), "E_NO_EDITABLE_TARGET", "no editable target");
        _platform.Root = new Field("android.widget.FrameLayout", "com.example", false, [new Field("android.widget.EditText", "com.example", true, accepts: false)]);
        AssertFailed(await Run("ui.text", """{"text":"x"}"""), "E_BACKEND_UNAVAILABLE", "target rejected text");
        // The active root is the host's own (it can be the only window while the host-window list says otherwise).
        _platform.Root = new Field("android.widget.FrameLayout", Self, false, [new Field("android.widget.EditText", Self, true)]);
        AssertFailed(await Run("ui.text", """{"text":"x"}"""), "E_TASK_SUSPENDED_BY_HOST");
    }

    [Fact]
    public async Task Key_MapsTheThreeGlobalActions()
    {
        Assert.Equal("""{"key":"back"}""", Data(await Run("ui.key", """{"key":"BACK"}""")));
        Assert.Equal("""{"key":"recents"}""", Data(await Run("ui.key", """{"key":"recents"}""")));
        Assert.Equal([1, 3], _platform.GlobalActions);
        AssertFailed(await Run("ui.key", """{"key":"volume"}"""), "E_TRANSPORT_MALFORMED", "unknown value for key: volume, accepted: [back, home, recents]");
        _platform.GlobalActionWorks = false;
        AssertFailed(await Run("ui.key", """{"key":"home"}"""), "E_BACKEND_UNAVAILABLE", "action unavailable: home");
    }

    [Fact]
    public async Task Capture_WritesOneArtifact_AndMapsEveryRefusal()
    {
        var released = 0;
        _platform.Shot = () => new A11yShot.Taken(10, 20, path =>
        {
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            return 4;
        }, () => released++);
        var ok = Assert.IsType<BackendResult.Ok>(await Run("screen.capture", "{}"));
        Assert.Equal("""{"bytes":4}""", RelayEncoding.EncodeData(ok.Data));
        Assert.Equal(4, ok.ArtifactBytes);
        var guest = Assert.Single(ok.Artifacts);
        Assert.EndsWith(".png", guest, StringComparison.Ordinal);
        Assert.Equal(1, released);

        _platform.Shot = () => new A11yShot.Taken(10, 20, _ => -1, () => released++);
        AssertFailed(await Run("screen.capture", "{}"), "E_SURFACE_SECURE_WINDOW", "convert rejected");
        _platform.Shot = () => new A11yShot.Taken(10, 20, _ => 0, () => released++);
        AssertFailed(await Run("screen.capture", "{}"), "E_STORAGE_FULL", "artifact write failed");
        Assert.Equal(3, released);
        _platform.Shot = () => new A11yShot.Refused(RelayError.RateLimited, "interval");
        AssertFailed(await Run("screen.capture", "{}"), "E_RATE_LIMITED", "interval");
        _platform.Sdk = 29;
        AssertFailed(await Run("screen.capture", "{}"), "E_CAPABILITY_UNAVAILABLE_ON_DEVICE", "screen.capture");
        Assert.False(_backend.Supports("screen.capture", SurfaceKind.Foreground));
    }

    [Fact]
    public async Task TrustedDisplay_WithoutOne_SaysWhatToDoInstead()
    {
        var r = await Run("ui.snapshot", "{}", SurfaceKind.BackendTrusted);
        AssertFailed(r, "E_BACKEND_UNAVAILABLE",
            "no trusted display to read: create one with surface.virtual and launch an app on it, or run on the foreground surface");
        Assert.False(_backend.Supports("ui.snapshot", SurfaceKind.BackendTrusted));
        Assert.True(_backend.Supports("ui.snapshot", SurfaceKind.Foreground));
        Assert.False(_backend.Supports("ui.tap", SurfaceKind.BackendTrusted));
        AssertFailed(await Run("ui.pinch", "{}"), "E_NOT_IMPLEMENTED");
    }
}
