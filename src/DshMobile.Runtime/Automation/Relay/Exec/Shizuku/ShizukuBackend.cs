// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/exec/shizuku/ShizukuBackend.kt (connection + rebind, transactBoundedBlocking,
//     CallBudget, ARG_SPECS / argsError, sys.shell, screencap, sys.settings.write, appops.set, app.stop, app.install,
//     surface.virtual, the trusted-display launch / tap / swipe / text / key / capture / record paths, refreshDisplay,
//     remoteWaitMs / canIssueRemote / remoteGiveUpReason and the constants)
// Registered in docs/UPSTREAMS.md. Every command is built here per capability from validated arguments and per-token
// single quoting; the caller never supplies a raw command line. Binder calls go through IShellService on one
// dedicated thread (a stuck transaction holds that thread only; callers give up locally on their budget).

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DshMobile.Runtime.Automation.Relay.Logging;
using DshMobile.Runtime.Automation.Relay.Storage;

namespace DshMobile.Runtime.Automation.Relay.Exec.Shizuku;

public sealed partial class ShizukuBackend : IRelayBackend, IDisposable
{
    /// <summary>The cap field of lifecycle waits (release before unbind, the periodic reconcile).</summary>
    public const string CapLifecycle = "lifecycle";

    public const long MinRemoteRoundtripMs = 300L;
    public const long RemoteMarginMs = 500L;
    public const long RemoteMarginArtifactMs = 2_000L;
    public const long RemoteSlackMs = 200L;
    public const long DisplayTxnWaitMs = 3_000L;
    public const long ScreenshotWaitMs = 5_200L;
    public const int DefaultDisplay = 0;
    public const long SettleWaitMs = 200L;
    public const long LandingWaitMs = 1_200L;
    public const long RetryLandingWaitMs = 1_500L;
    public const long InstallTimeoutMs = 120_000L;
    public const long RebindCooldownMs = 5_000L;
    public const int DefaultWidth = 1080;
    public const int DefaultHeight = 1920;
    public const int DefaultDpi = 420;
    public const int DefaultSwipeMs = 300;
    public const int MaxSwipeMs = 3_000;
    public const int MaxTextChars = 500;
    public const long LaunchTimeoutMs = 15_000L;
    public const long CaptureWaitMs = 3_000L;
    public const long CaptureEstimateBytes = 12L * 1024 * 1024;
    public const string LastFrameFile = "last-frame.png";
    public const int DefaultRecordSeconds = 5;
    public const int MaxRecordSeconds = 30;
    public const int RecordFps = 8;
    public const int RecordBitsPerSecond = 4_000_000;
    public const int MinEncodePx = 320;
    public const int MaxFrameMisses = 3;
    public const int RecordJpegQuality = 60;
    public const int RecordMaxWidth = 720;
    public const long RecordFrameWaitMs = 250L;
    public const long MaxInstallBytes = 128L * 1024L * 1024L;
    public const long DefaultCommandTimeoutMs = 15_000L;
    private const int MaxValueChars = 4096;
    private const int MaxErrorChars = 200;
    private const int ExitCommandNotFound = 127;
    private const int Lossless = 0;
    private const int CodeUnverified = -2;
    private const string ArtifactSuffix = "png";
    private const string RecordSuffix = "mp4";
    private const string DefaultNamespace = "secure";

    public const string KeyUpload = "upload";
    public const string KeySetting = "key";
    public const string KeyValue = "value";
    public const string KeyNamespace = "namespace";
    public const string KeyPackage = "package";
    public const string KeyOp = "op";
    public const string KeyMode = "mode";
    public const string KeyAction = "action";
    public const string KeyWidth = "width";
    public const string KeyHeight = "height";
    public const string KeyDpi = "dpi";
    public const string KeyText = "text";
    public const string KeyKeyEvent = "key";
    public const string KeyX = "x";
    public const string KeyY = "y";
    public const string KeyFromX = "fromX";
    public const string KeyFromY = "fromY";
    public const string KeyToX = "toX";
    public const string KeyToY = "toY";
    public const string KeyDurationMs = "durationMs";
    public const string KeySeconds = "seconds";
    public const string ActionCreate = "create";
    public const string ActionRelease = "release";
    public const string ActionQuery = "query";

    /// <summary>One dumpsys for every display's section head and its top resumed activity.</summary>
    public const string TopPackageCommand = "dumpsys activity activities | grep -aE 'Display #[0-9]+ \\(|topResumedActivity='";

    private static readonly string[] SettingsNamespaceOrder = ["secure", "system", "global"];

    public static readonly IReadOnlyList<string> AllowedOps =
    [
        "android:system_alert_window", "android:camera", "android:record_audio", "android:location", "android:post_notification",
    ];

    public static readonly IReadOnlyList<string> AllowedModes = ["allow", "deny", "ignore", "default"];

    /// <summary>Key fragments that hand a system-level hook to another app: blocked as a whole class.</summary>
    public static readonly IReadOnlyList<string> PrivilegeKeyFragments =
    [
        "accessibility", "input_method", "ime_", "_ime", "listener", "device_admin", "companion", "autofill", "print_", "adb",
        "verify", "install_", "_install", "vpn", "mock_location",
    ];

    /// <summary>Keys without those fragments that still hand over control or feed a gate's own probe.</summary>
    public static readonly IReadOnlySet<string> BlockedSettingsKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "default_input_method", "enabled_input_methods", "show_ime_with_hard_keyboard", "enabled_accessibility_services",
        "accessibility_enabled", "accessibility_button_targets", "accessibility_shortcut_target_service",
        "enabled_notification_listeners", "enabled_print_services", "sms_selected_app_package_name", "role_sms", "role_dialer",
        "development_settings_enabled", "bluetooth_on", "wifi_on", "nfc_on",
    };

    private static readonly (string Name, int Code)[] KeyEvents = [("back", 4), ("home", 3), ("recents", 187), ("enter", 66)];

    public sealed record ArgSpec(IReadOnlyList<string> Allowed, IReadOnlyList<string> Required);

    private static ArgSpec Spec(string[]? allowed = null, string[]? required = null) => new(allowed ?? [], required ?? []);

    /// <summary>Upstream <c>ARG_SPECS</c>.</summary>
    public static readonly IReadOnlyDictionary<string, ArgSpec> ArgSpecs = new Dictionary<string, ArgSpec>(StringComparer.Ordinal)
    {
        ["sys.settings.write"] = Spec([KeySetting, KeyValue, KeyNamespace], [KeySetting, KeyValue]),
        ["sys.shell"] = Spec([ShellVerbs.KeyVerb, ShellVerbs.KeyArgs], [ShellVerbs.KeyVerb]),
        ["appops.set"] = Spec([KeyPackage, KeyOp, KeyMode], [KeyPackage, KeyOp, KeyMode]),
        ["app.stop"] = Spec([KeyPackage], [KeyPackage]),
        ["app.install"] = Spec([KeyUpload], [KeyUpload]),
        ["surface.virtual"] = Spec([KeyAction, KeyWidth, KeyHeight, KeyDpi]),
        ["app.launch"] = Spec([KeyPackage], [KeyPackage]),
        ["ui.text"] = Spec([KeyText], [KeyText]),
        ["ui.key"] = Spec([KeyKeyEvent], [KeyKeyEvent]),
        ["screen.capture"] = Spec(),
        ["screen.record"] = Spec([KeySeconds]),
        ["ui.tap"] = Spec([KeyX, KeyY]),
        ["ui.swipe"] = Spec([KeyFromX, KeyFromY, KeyToX, KeyToY, KeyDurationMs]),
    };

    private static readonly HashSet<string> ShellCapabilities = new(StringComparer.Ordinal)
    {
        "sys.settings.write", "appops.set", "app.stop", "sys.shell", "app.install",
    };

    private static readonly HashSet<string> DisplayCapabilities = new(StringComparer.Ordinal)
    {
        "app.launch", "ui.tap", "ui.swipe", "ui.text", "ui.key", "screen.capture", "screen.record",
    };

    private readonly IShizukuPlatform _platform;
    private readonly RunLog _runLog;
    private readonly StorageReaper _reaper;
    private readonly TimeProvider _time;
    private readonly Func<long, CancellationToken, Task> _delay;
    private readonly RemoteExecutor _remote = new("relay-shizuku-remote");
    private volatile IShellService? _binder;
    private volatile bool _deliberatelyUnbound = true;
    private long _lastRebindMs;
    private volatile int _displayId = -1;
    private (int Width, int Height) _displaySize;
    private volatile LastFrameCache? _frameCache;

    public ShizukuBackend(IShizukuPlatform platform, RunLog runLog, StorageReaper reaper, TimeProvider? time = null,
        Func<long, CancellationToken, Task>? delay = null)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _runLog = runLog ?? throw new ArgumentNullException(nameof(runLog));
        _reaper = reaper ?? throw new ArgumentNullException(nameof(reaper));
        _time = time ?? TimeProvider.System;
        _delay = delay ?? ((ms, ct) => Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, ms)), _time, ct));
    }

    /// <summary>The binding changed: the host re-renders capabilities.json (usable follows the binding).</summary>
    public event Action? BoundChanged;

    /// <summary>"Has the user turned on this system-changing verb", supplied by the composition root from preferences.</summary>
    public Func<string, bool> ShellOptInOpen { get; set; } = _ => false;

    public bool BoundSnapshot => _binder is not null;

    /// <summary>The trusted display's id on the service side, -1 when none (a cache: see <see cref="DisplayAlive"/>).</summary>
    public int DisplayId => _displayId;

    public (int Width, int Height) DisplaySize => _displaySize;

    private long Now() => _time.GetTimestamp() * 1000L / _time.TimestampFrequency;

    // ── connection ──

    public void OnServiceConnected(IShellService service)
    {
        _binder = service;
        _runLog.Info(LogSubsystem.Gate, LogEvent.ShizukuServiceConnected);
        BoundChanged?.Invoke();
    }

    public void OnServiceDisconnected()
    {
        _binder = null;
        _runLog.Warn(LogSubsystem.Gate, LogEvent.ShizukuServiceDead);
        BoundChanged?.Invoke();
        RebindIfDropped();
    }

    public void OnBindingDied()
    {
        _binder = null;
        _runLog.Warn(LogSubsystem.Gate, LogEvent.ShizukuServiceDead, ("where", "bindingDied"));
        BoundChanged?.Invoke();
        RebindIfDropped();
    }

    public void Connect()
    {
        _deliberatelyUnbound = false;
        if (_binder is null)
        {
            _platform.BindUserService();
        }
    }

    /// <summary>Releases the display before unbinding (it may host someone's app), bounded; then unbinds.</summary>
    public async Task DisconnectAsync()
    {
        _deliberatelyUnbound = true;
        if (DisplayAlive())
        {
            await TransactBoundedAsync(CapLifecycle, "display", DisplayTxnWaitMs, () => RawDisplay(s => s.ReleaseDisplay()), CancellationToken.None)
                .ConfigureAwait(false);
        }
        _displayId = -1;
        ClearFrameCache();
        _platform.UnbindUserService();
        _binder = null;
    }

    public bool Available() => _platform.ServerAvailable && _binder is not null;

    /// <summary>Self-heal from the dispatcher and the periodic probe only, never from status rendering.</summary>
    public void Reconnect()
    {
        if (_binder is null)
        {
            RebindIfDropped();
        }
    }

    /// <summary>Unbind then bind (Shizuku caches the connection per UserServiceArgs), with a cooldown.</summary>
    private void RebindIfDropped()
    {
        if (_deliberatelyUnbound || !_platform.ServerAvailable)
        {
            return;
        }
        var now = Now();
        if (now - Interlocked.Read(ref _lastRebindMs) < RebindCooldownMs)
        {
            return;
        }
        Interlocked.Exchange(ref _lastRebindMs, now);
        _displayId = -1;
        _runLog.Warn(LogSubsystem.Gate, LogEvent.ShizukuServiceDead, ("action", "rebind"));
        _platform.UnbindUserService();
        _platform.BindUserService();
    }

    /// <summary>Asks the display service, not the cache: the system can take the display away after it was created.</summary>
    public bool DisplayAlive()
    {
        var id = _displayId;
        return id >= 0 && _platform.DisplayExists(id);
    }

    public bool Supports(string capability, SurfaceKind surface)
    {
        if (ShellCapabilities.Contains(capability))
        {
            return true;
        }
        if (capability == "surface.virtual")
        {
            return surface == SurfaceKind.BackendTrusted;
        }
        return DisplayCapabilities.Contains(capability) && surface == SurfaceKind.BackendTrusted && DisplayAlive();
    }

    public async Task<BackendResult> ExecuteAsync(BackendCall backendCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backendCall);
        var call = backendCall;
        var ct = cancellationToken;
        if (ArgsError(call.Capability, call.Args) is { } bad)
        {
            return Malformed(bad);
        }
        var budget = new CallBudget(call.BudgetMs, call.Capability, Now);
        var args = call.Args;
        return call.Capability switch
        {
            "sys.settings.write" => await SecureSettingsAsync(args, budget, ct).ConfigureAwait(false),
            "sys.shell" => await ShellVerbCallAsync(call, budget, ct).ConfigureAwait(false),
            "appops.set" => await AppOpsAsync(args, budget, ct).ConfigureAwait(false),
            "app.stop" => await ForceStopAsync(args, budget, ct).ConfigureAwait(false),
            "app.install" => await InstallAppAsync(call, budget, ct).ConfigureAwait(false),
            "surface.virtual" => await DisplayActionAsync(args, budget, ct).ConfigureAwait(false),
            "app.launch" => await DisplayLaunchAsync(call, budget, ct).ConfigureAwait(false),
            "ui.tap" => await DisplayTapAsync(args, budget, ct).ConfigureAwait(false),
            "ui.swipe" => await DisplaySwipeAsync(args, budget, ct).ConfigureAwait(false),
            "ui.text" => await DisplayTextAsync(args, budget, ct).ConfigureAwait(false),
            "ui.key" => await DisplayKeyAsync(args, budget, ct).ConfigureAwait(false),
            "screen.capture" => await DisplayCaptureAsync(call, budget, ct).ConfigureAwait(false),
            "screen.record" => await RecordOnDisplayAsync(call, budget, ct).ConfigureAwait(false),
            _ => new BackendResult.Failed(RelayError.CapabilityNotImplemented, call.Capability),
        };
    }

    public void Dispose() => _remote.Dispose();

    // ── budget ──

    /// <summary>The remote budget ledger of one call: each remote send asks "how much is left right now".</summary>
    internal sealed class CallBudget(long totalMs, string cap, Func<long> now)
    {
        private readonly long _startedAt = now();

        public string Cap { get; } = cap;

        public long RemainingMs() => Math.Max(0L, totalMs - (now() - _startedAt));

        public long? RemoteWaitMs(long defaultMs, long marginMs) => ShizukuBackend.RemoteWaitMs(defaultMs, RemainingMs(), marginMs);
    }

    /// <summary>min(default, budget - margin); null when one minimal round trip does not fit (nothing is sent).</summary>
    public static long? RemoteWaitMs(long defaultMs, long budgetMs, long marginMs)
    {
        var available = budgetMs - marginMs;
        if (available < MinRemoteRoundtripMs)
        {
            return null;
        }
        return Math.Min(defaultMs, available);
    }

    public static bool CanIssueRemote(long defaultMs, long budgetMs, long marginMs) => RemoteWaitMs(defaultMs, budgetMs, marginMs) is not null;

    /// <summary>The standard answer after a local give-up: the transaction was not cancelled and may still complete.</summary>
    public static string RemoteGiveUpReason(string kind, long waitedMs, long? submittedBeforeExpiryMs)
    {
        var b = $"local wait for the {kind} transaction gave up after {waitedMs}ms; the transaction " +
            "was not cancelled and its outcome is unknown - do not treat this as not executed";
        return submittedBeforeExpiryMs is { } ms
            ? $"{b} (it was submitted {ms}ms before the local budget expired and may still complete)"
            : $"{b} (it was still queued locally when the wait expired and may still be submitted and run later)";
    }

    private static BackendResult.Failed RemoteBudgetExhausted(CallBudget budget, long marginMs) => new(
        RelayError.TransportTimeout,
        $"remaining call budget ({budget.RemainingMs()}ms) minus the {marginMs}ms close-out " +
        $"reserve cannot cover one remote round trip (needs at least {MinRemoteRoundtripMs}ms): nothing was sent");

    // ── bounded transactions ──

    private abstract record RemoteTxn<T>
    {
        public sealed record Done(T Value) : RemoteTxn<T>;

        public sealed record GaveUp(long WaitedMs, long? SubmittedBeforeExpiryMs) : RemoteTxn<T>;
    }

    /// <summary>
    /// Hands one blocking transaction to the remote thread and waits at most <paramref name="waitMs"/> locally.
    /// A give-up drops the local wait only; the remote half runs to its end.
    /// </summary>
    private async Task<RemoteTxn<T>> TransactBoundedAsync<T>(string cap, string kind, long waitMs, Func<T> task, CancellationToken ct)
    {
        long submittedAt = long.MinValue;
        var waitStartedAt = Now();
        _runLog.Info(LogSubsystem.Backend, LogEvent.RemoteSubmitted, ("cap", cap), ("waitMs", Text(waitMs)));
        var future = _remote.Submit(() =>
        {
            Interlocked.Exchange(ref submittedAt, Now());
            return task();
        });
        RemoteTxn<T> outcome;
        try
        {
            outcome = new RemoteTxn<T>.Done(await future.WaitAsync(TimeSpan.FromMilliseconds(waitMs), _time, ct).ConfigureAwait(false));
        }
        catch (TimeoutException)
        {
            outcome = new RemoteTxn<T>.GaveUp(waitMs, SubmittedBeforeExpiry(waitStartedAt, waitMs, Interlocked.Read(ref submittedAt)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = new RemoteTxn<T>.GaveUp(waitMs, SubmittedBeforeExpiry(waitStartedAt, waitMs, Interlocked.Read(ref submittedAt)));
        }
        if (outcome is RemoteTxn<T>.GaveUp)
        {
            _runLog.Warn(LogSubsystem.Backend, LogEvent.RemoteResultDropped, ("kind", kind), ("waitedMs", Text(waitMs)));
            var submitted = Interlocked.Read(ref submittedAt);
            var from = submitted == long.MinValue ? waitStartedAt : submitted;
            _runLog.Warn(LogSubsystem.Backend, LogEvent.RemoteGiveUp, ("cap", cap), ("ago", Text(Math.Max(0L, Now() - from))));
        }
        return outcome;
    }

    private static long? SubmittedBeforeExpiry(long waitStartedAt, long waitMs, long submittedAt) =>
        submittedAt == long.MinValue ? null : Math.Max(0L, waitStartedAt + waitMs - submittedAt);

    // ── args ──

    public static string? ArgsError(string capability, JsonElement args)
    {
        if (!ArgSpecs.TryGetValue(capability, out var spec))
        {
            return null;
        }
        foreach (var p in args.EnumerateObject())
        {
            if (!spec.Allowed.Contains(p.Name))
            {
                return ArgErrors.Unknown(p.Name, spec.Allowed);
            }
        }
        foreach (var key in spec.Required)
        {
            if (OrgJson.OptString(args, key).All(OrgJson.KotlinIsWhitespace))
            {
                return "missing arg: " + key;
            }
        }
        return null;
    }

    /// <summary>Integer coordinate keys: missing, not a number, or outside the int range (getInt would truncate to 32 bits).</summary>
    public static string? IntArgsError(JsonElement args, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!args.TryGetProperty(key, out var v))
            {
                return "missing arg: " + key;
            }
            if (v.ValueKind != JsonValueKind.Number)
            {
                return ArgErrors.NotNumber(key, v);
            }
            var number = v.GetDouble();
            if (number < int.MinValue || number > int.MaxValue)
            {
                return $"{key} is out of range (got {LibcoreJson.ValueOf(v)})";
            }
        }
        return null;
    }

    private static int GetInt(JsonElement args, string key) => OrgJson.JavaDoubleToInt(args.GetProperty(key).GetDouble());

    private static BackendResult.Failed Malformed(string reason) => new(RelayError.TransportMalformed, reason);

    // ── sys.shell ──

    private async Task<BackendResult> ShellVerbCallAsync(BackendCall call, CallBudget budget, CancellationToken ct)
    {
        switch (ShellVerbs.Build(call.Args, ShellOptInOpen))
        {
            case ShellVerbs.Plan.Bad bad:
                return Malformed(bad.Reason);
            case ShellVerbs.Plan.NeedsOptIn needs:
                return new BackendResult.Failed(
                    RelayError.GateHostDenied,
                    $"verb '{needs.Verb}' changes the system and is not turned on: open it in the " +
                    $"dangerous-capabilities page (shape: {needs.Shape})");
            case ShellVerbs.Plan.Run run when run.Verb == "screencap":
                return await ScreenshotArtifactAsync(call, budget, ct).ConfigureAwait(false);
            case ShellVerbs.Plan.Run run:
                var outcome = await RunAsync(run.Command, budget, ct).ConfigureAwait(false);
                if (outcome is BackendResult.Ok ok)
                {
                    ok.Data["verb"] = run.Verb;
                    ok.Data["command"] = run.Command;
                }
                return outcome;
            default:
                throw new InvalidOperationException("unknown plan");
        }
    }

    /// <summary>screencap's frame comes back through a descriptor and becomes an artifact; no service path is reported.</summary>
    private async Task<BackendResult> ScreenshotArtifactAsync(BackendCall call, CallBudget budget, CancellationToken ct)
    {
        if (_binder is not { } target)
        {
            return new BackendResult.Failed(RelayError.BackendShizukuDead);
        }
        if (budget.RemoteWaitMs(ScreenshotWaitMs, RemoteMarginArtifactMs) is not { } waitMs)
        {
            return RemoteBudgetExhausted(budget, RemoteMarginArtifactMs);
        }
        var txn = await TransactBoundedAsync(budget.Cap, "screencap", waitMs, () =>
        {
            try
            {
                return target.Screenshot();
            }
#pragma warning disable CA1031 // upstream catch (t: Throwable) { serviceNote = t.javaClass.simpleName }
            catch (Exception e)
#pragma warning restore CA1031
            {
                return new ScreenshotReply(null, e.GetType().Name);
            }
        }, ct).ConfigureAwait(false);
        if (txn is RemoteTxn<ScreenshotReply>.GaveUp gaveUp)
        {
            return new BackendResult.Failed(RelayError.TransportTimeout, RemoteGiveUpReason("screencap", gaveUp.WaitedMs, gaveUp.SubmittedBeforeExpiryMs));
        }
        var reply = ((RemoteTxn<ScreenshotReply>.Done)txn).Value;
        if (reply.Frame is not { Length: > 0 } bytes)
        {
            var note = reply.Note ?? "";
            return new BackendResult.Failed(
                RelayError.BackendUnavailable,
                "no frame from screencap" + (note.All(OrgJson.KotlinIsWhitespace) ? "" : ": " + note));
        }
        return StageAndPublish(call, bytes, new JsonObject { ["bytes"] = 0L, ["source"] = "screencap" });
    }

    // ── settings / appops / force-stop ──

    private async Task<BackendResult> SecureSettingsAsync(JsonElement args, CallBudget budget, CancellationToken ct)
    {
        var key = OrgJson.OptString(args, KeySetting);
        if (!SettingKeyPattern().IsMatch(key))
        {
            return Malformed(KeySetting);
        }
        if (IsPrivilegeHandoffKey(key))
        {
            return new BackendResult.Failed(RelayError.GateHostDenied, "key blocked: " + key);
        }
        var value = OrgJson.OptString(args, KeyValue);
        if (value.Length > MaxValueChars)
        {
            return Malformed("value too long");
        }
        var ns = OrgJson.OptString(args, KeyNamespace);
        if (ns.All(OrgJson.KotlinIsWhitespace))
        {
            ns = DefaultNamespace;
        }
        if (!SettingsNamespaceOrder.Contains(ns))
        {
            return Malformed($"{KeyNamespace} must be one of {string.Join("|", SettingsNamespaceOrder)}, got '{ns}'");
        }
        var write = await RunAsync($"settings put {ns} {ShellVerbs.ShellQuote(key)} {ShellVerbs.ShellQuote(value)}", budget, ct).ConfigureAwait(false);
        if (write is BackendResult.Failed)
        {
            return write;
        }
        var readBack = await AskAsync($"settings get {ns} {ShellVerbs.ShellQuote(key)}", budget, ct).ConfigureAwait(false);
        var shadowedBy = await ShadowedNamespaceAsync(key, ns, budget, ct).ConfigureAwait(false);
        var data = new JsonObject
        {
            ["namespace"] = ns,
            ["key"] = key,
            ["requested"] = value,
            ["readBack"] = readBack,
            ["verified"] = readBack == value,
        };
        if (shadowedBy is not null)
        {
            data["shadowedBy"] = shadowedBy;
            data["note"] = $"this key also has a value in '{shadowedBy}' while this call wrote " +
                $"'{ns}': the effective setting probably did not change - " +
                $"pass \"{KeyNamespace}\" for the layer that key actually lives in";
        }
        return new BackendResult.Ok(data);
    }

    /// <summary>Whether the key also has a value in a layer this write did not touch; both layers in one transaction.</summary>
    private async Task<string?> ShadowedNamespaceAsync(string key, string written, CallBudget budget, CancellationToken ct)
    {
        var quoted = ShellVerbs.ShellQuote(key);
        var others = SettingsNamespaceOrder.Where(n => n != written).ToList();
        var probe = string.Join(" ", others.Select(n => $"{n}=$(settings get {n} {quoted})"));
        if (await AskAsync("echo " + probe, budget, ct).ConfigureAwait(false) is not { } output)
        {
            return null;
        }
        foreach (var ns in others)
        {
            var marker = ns + "=";
            var at = output.IndexOf(marker, StringComparison.Ordinal);
            var after = at < 0 ? "" : output[(at + marker.Length)..];
            var space = after.IndexOf(' ', StringComparison.Ordinal);
            var found = space < 0 ? after : after[..space];
            if (found.Length > 0 && found != "null")
            {
                return ns;
            }
        }
        return null;
    }

    private async Task<BackendResult> AppOpsAsync(JsonElement args, CallBudget budget, CancellationToken ct)
    {
        var pkg = OrgJson.OptString(args, KeyPackage);
        var op = OrgJson.OptString(args, KeyOp);
        var mode = OrgJson.OptString(args, KeyMode);
        if (!PackagePattern().IsMatch(pkg))
        {
            return Malformed(KeyPackage);
        }
        var wanted = RemovePrefix(op, "android:").ToLowerInvariant();
        var opName = AllowedOps.FirstOrDefault(o => string.Equals(RemovePrefix(o, "android:"), wanted, StringComparison.OrdinalIgnoreCase));
        if (opName is null)
        {
            return Malformed($"op not allowed: {op}, accepted: {string.Join(", ", AllowedOps)}");
        }
        var normalized = mode.ToLowerInvariant();
        if (!AllowedModes.Contains(normalized))
        {
            return Malformed($"{KeyMode} must be one of {string.Join(", ", AllowedModes)}");
        }
        var set = await RunAsync($"appops set {pkg} {opName} {normalized}", budget, ct).ConfigureAwait(false);
        if (set is BackendResult.Failed)
        {
            return set;
        }
        var readBack = await AskAsync($"appops get {pkg} {opName}", budget, ct).ConfigureAwait(false);
        var matches = AppOpsMode().Matches(readBack ?? "");
        var data = new JsonObject { ["package"] = pkg, ["op"] = opName };
        if (matches.Count > 0)
        {
            data["effective"] = matches[^1].Groups[1].Value;
        }
        data["requested"] = normalized;
        data["readBack"] = readBack;
        return new BackendResult.Ok(data);
    }

    private async Task<BackendResult> ForceStopAsync(JsonElement args, CallBudget budget, CancellationToken ct)
    {
        var pkg = OrgJson.OptString(args, KeyPackage);
        if (!PackagePattern().IsMatch(pkg))
        {
            return Malformed(KeyPackage);
        }
        if (pkg == _platform.AppPackage)
        {
            return new BackendResult.Failed(RelayError.GateHostDenied, "refusing to stop the host app");
        }
        return await RunAsync("am force-stop " + pkg, budget, ct).ConfigureAwait(false);
    }

    // ── app.install ──

    /// <summary>
    /// Installs a vetted upload: the staged name is generated here, the shell side gets a descriptor of the vetted
    /// handle (never a path), the archive must parse, and the host itself is never replaced. Always ASK_ONLY upstream.
    /// </summary>
    private async Task<BackendResult> InstallAppAsync(BackendCall call, CallBudget budget, CancellationToken ct)
    {
        var name = OrgJson.OptString(call.Args, KeyUpload);
        if (_platform.OpenInstallUpload(name, MaxInstallBytes) is not { } upload)
        {
            return new BackendResult.Failed(RelayError.TransportMalformed, "no such upload: " + name);
        }
        using (upload)
        {
            string? pkg;
            try
            {
                pkg = _platform.ArchivePackageName(upload.FilePath);
            }
#pragma warning disable CA1031 // upstream runCatching { getPackageArchiveInfo }.getOrNull()
            catch (Exception)
#pragma warning restore CA1031
            {
                pkg = null;
            }
            if (pkg is null || pkg.All(OrgJson.KotlinIsWhitespace))
            {
                return new BackendResult.Failed(RelayError.TransportMalformed, "not an installable package");
            }
            if (pkg == _platform.AppPackage)
            {
                return new BackendResult.Failed(RelayError.GateHostDenied, "refusing to replace the host app");
            }
            if (_binder is not { } target)
            {
                return new BackendResult.Failed(RelayError.BackendShizukuDead);
            }
            if (budget.RemoteWaitMs(InstallTimeoutMs, RemoteMarginMs) is not { } waitMs)
            {
                return RemoteBudgetExhausted(budget, RemoteMarginMs);
            }
            IDisposable? descriptor;
            try
            {
                descriptor = upload.OpenDescriptor();
            }
#pragma warning disable CA1031 // upstream runCatching { upload.openDescriptor() }.getOrNull()
            catch (Exception)
#pragma warning restore CA1031
            {
                descriptor = null;
            }
            if (descriptor is null)
            {
                return new BackendResult.Failed(RelayError.BackendShizukuDead, "descriptor or binder unavailable");
            }
            var staged = StagedName(call.RequestId);
            var txn = await TransactBoundedAsync(budget.Cap, "install", waitMs + RemoteSlackMs, () =>
            {
                try
                {
                    return target.Install(descriptor, staged, waitMs);
                }
#pragma warning disable CA1031 // upstream catch (t: Throwable) { Outcome(-1, "", simpleName) }
                catch (Exception e)
#pragma warning restore CA1031
                {
                    return new ShellOutcome(-1, "", e.GetType().Name);
                }
                finally
                {
                    descriptor.Dispose();
                }
            }, ct).ConfigureAwait(false);
            if (txn is RemoteTxn<ShellOutcome?>.GaveUp gaveUp)
            {
                return new BackendResult.Failed(RelayError.TransportTimeout, RemoteGiveUpReason("install", gaveUp.WaitedMs, gaveUp.SubmittedBeforeExpiryMs));
            }
            if (((RemoteTxn<ShellOutcome?>.Done)txn).Value is not { } outcome)
            {
                return new BackendResult.Failed(RelayError.BackendShizukuDead, "descriptor or binder unavailable");
            }
            if (!outcome.Success)
            {
                var err = OrgJson.KotlinTrim(outcome.Stderr);
                return new BackendResult.Failed(
                    RelayError.GateSystemMissing,
                    "exit=" + outcome.ExitCode.ToString(CultureInfo.InvariantCulture) + " " + (err.Length <= MaxErrorChars ? err : err[..MaxErrorChars]));
            }
            upload.DeleteSource();
            return new BackendResult.Ok(new JsonObject
            {
                ["package"] = pkg,
                ["bytes"] = upload.Size,
                ["output"] = OrgJson.KotlinTrim(outcome.Stdout),
            });
        }
    }

    /// <summary>The staged name is built here (filtered request id + a timestamp): the caller cannot pick it.</summary>
    public string StagedName(string requestId)
    {
        var filtered = new string([.. requestId.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').Take(48)]);
        var b = filtered.All(OrgJson.KotlinIsWhitespace) ? "pkg" : filtered;
        return "install-" + b + "-" + _time.GetTimestamp().ToString("x", CultureInfo.InvariantCulture) + ".apk";
    }

    // ── commands ──

    /// <summary>Sends one command; local wait and the remote kill timeout are both clamped to the call budget.</summary>
    private async Task<BackendResult> RunAsync(string command, CallBudget budget, CancellationToken ct, long defaultTimeoutMs = DefaultCommandTimeoutMs)
    {
        if (_binder is not { } target)
        {
            return new BackendResult.Failed(RelayError.BackendShizukuDead);
        }
        if (budget.RemoteWaitMs(defaultTimeoutMs, RemoteMarginMs) is not { } waitMs)
        {
            return RemoteBudgetExhausted(budget, RemoteMarginMs);
        }
        var txn = await TransactBoundedAsync(budget.Cap, "command", waitMs + RemoteSlackMs, () =>
        {
            try
            {
                return target.Run(command, waitMs);
            }
#pragma warning disable CA1031 // upstream catch (t: Throwable) { Outcome(-1, "", simpleName) }
            catch (Exception e)
#pragma warning restore CA1031
            {
                return new ShellOutcome(-1, "", e.GetType().Name);
            }
        }, ct).ConfigureAwait(false);
        if (txn is RemoteTxn<ShellOutcome?>.GaveUp gaveUp)
        {
            return new BackendResult.Failed(RelayError.TransportTimeout, RemoteGiveUpReason("command", gaveUp.WaitedMs, gaveUp.SubmittedBeforeExpiryMs));
        }
        if (((RemoteTxn<ShellOutcome?>.Done)txn).Value is not { } outcome)
        {
            return new BackendResult.Failed(RelayError.BackendShizukuDead, "binder unavailable");
        }
        if (outcome.Success)
        {
            return new BackendResult.Ok(new JsonObject { ["output"] = OrgJson.KotlinTrim(outcome.Stdout), ["exit"] = outcome.ExitCode });
        }
        var (error, reason) = CommandFailure(outcome.ExitCode, OrgJson.KotlinTrim(outcome.Stderr));
        return new BackendResult.Failed(error, reason);
    }

    /// <summary>127 / "not found" = the device has no such command; denial words = a missing grant; else the command's own failure.</summary>
    public static (RelayError Error, string Reason) CommandFailure(int exitCode, string stderr)
    {
        var detail = $"exit={exitCode.ToString(CultureInfo.InvariantCulture)} {stderr}";
        if (exitCode == ExitCommandNotFound || stderr.Contains("inaccessible or not found", StringComparison.Ordinal))
        {
            return (RelayError.CapabilityUnavailableOnDevice, detail + ": this device has no such command, no switch or grant will add it");
        }
        if (stderr.Contains("Permission Denial", StringComparison.Ordinal) || stderr.Contains("SecurityException", StringComparison.Ordinal) ||
            stderr.Contains("not allowed", StringComparison.Ordinal))
        {
            return (RelayError.GateSystemMissing, detail);
        }
        return (RelayError.InternalError, detail);
    }

    /// <summary>A read-only command's stdout; empty, the literal "null" or any failure all read as null.</summary>
    private async Task<string?> AskAsync(string command, CallBudget budget, CancellationToken ct)
    {
        var output = await RunAsync(command, budget, ct).ConfigureAwait(false) is BackendResult.Ok ok
            ? OrgJson.KotlinTrim(ok.Data["output"]?.GetValue<string>() ?? "")
            : "";
        return output.Length == 0 || output == "null" ? null : output;
    }

    // ── trusted display ──

    private async Task<BackendResult> DisplayActionAsync(JsonElement args, CallBudget budget, CancellationToken ct)
    {
        var action = OrgJson.OptString(args, KeyAction);
        if (action.All(OrgJson.KotlinIsWhitespace))
        {
            action = ActionCreate;
        }
        if (action == ActionCreate)
        {
            if (DisplayAlive())
            {
                await RefreshDisplayAsync(budget, ct).ConfigureAwait(false);
            }
            if (DisplayAlive())
            {
                return new BackendResult.Ok(DisplayData("kept"));
            }
            if (budget.RemoteWaitMs(DisplayTxnWaitMs, RemoteMarginMs) is not { } waitMs)
            {
                return RemoteBudgetExhausted(budget, RemoteMarginMs);
            }
            var width = OrgJson.OptInt(args, KeyWidth, DefaultWidth);
            var height = OrgJson.OptInt(args, KeyHeight, DefaultHeight);
            var dpi = OrgJson.OptInt(args, KeyDpi, DefaultDpi);
            var txn = await TransactBoundedAsync(budget.Cap, "display", waitMs, () => RawDisplay(s => s.CreateDisplay(width, height, dpi)), ct)
                .ConfigureAwait(false);
            if (txn is RemoteTxn<DisplayReply>.GaveUp gaveUp)
            {
                return new BackendResult.Failed(RelayError.TransportTimeout,
                    RemoteGiveUpReason("display create", gaveUp.WaitedMs, gaveUp.SubmittedBeforeExpiryMs));
            }
            var created = ((RemoteTxn<DisplayReply>.Done)txn).Value;
            if (created.Code == CodeUnverified)
            {
                // The service did not say the display is gone, only that it could not check: keep the cached id.
                return new BackendResult.Failed(RelayError.BackendUnavailable, created.Reason);
            }
            if (created.Code < 0)
            {
                _displayId = -1;
                return new BackendResult.Failed(RelayError.SurfaceTrustedDenied, created.Reason);
            }
            _displayId = created.Code;
            // A new display may reuse an id with different content: the cache is keyed by id, so clear it here.
            _frameCache?.Clear();
            await RefreshDisplayAsync(budget, ct).ConfigureAwait(false);
            return new BackendResult.Ok(DisplayData(ActionCreate));
        }
        if (action == ActionRelease)
        {
            if (budget.RemoteWaitMs(DisplayTxnWaitMs, RemoteMarginMs) is not { } waitMs)
            {
                return RemoteBudgetExhausted(budget, RemoteMarginMs);
            }
            var txn = await TransactBoundedAsync(budget.Cap, "display", waitMs, () => RawDisplay(s => s.ReleaseDisplay()), ct).ConfigureAwait(false);
            _displayId = -1;
            _frameCache?.Clear();
            if (txn is RemoteTxn<DisplayReply>.GaveUp gaveUp)
            {
                return new BackendResult.Failed(RelayError.TransportTimeout,
                    RemoteGiveUpReason("display release", gaveUp.WaitedMs, gaveUp.SubmittedBeforeExpiryMs));
            }
            var released = ((RemoteTxn<DisplayReply>.Done)txn).Value;
            return released.Code < 0
                ? new BackendResult.Failed(RelayError.BackendUnavailable, released.Reason)
                : new BackendResult.Ok(new JsonObject { ["released"] = released.Code == 1 });
        }
        if (action != ActionQuery)
        {
            return Malformed($"unknown action: {action}, accepted: [{ActionCreate}, {ActionRelease}, {ActionQuery}]");
        }
        if (!await RefreshDisplayAsync(budget, ct).ConfigureAwait(false))
        {
            return RemoteBudgetExhausted(budget, RemoteMarginMs);
        }
        return new BackendResult.Ok(DisplayData(ActionQuery));
    }

    private async Task<BackendResult> DisplayLaunchAsync(BackendCall call, CallBudget budget, CancellationToken ct)
    {
        var packageName = OrgJson.OptString(call.Args, KeyPackage);
        if (_platform.LaunchComponent(packageName) is not { } component)
        {
            return Malformed("not launchable here: " + packageName);
        }
        if (RequireDisplay() is not { } display)
        {
            return NoDisplay();
        }
        var before = await TopPackagesAsync(budget, ct).ConfigureAwait(false);
        var launched = await RunAsync($"am start --display {display} -n {component}", budget, ct, LaunchTimeoutMs).ConfigureAwait(false);
        if (launched is not BackendResult.Ok ok)
        {
            return launched;
        }
        ok.Data["package"] = packageName;
        ok.Data["display"] = display;
        var deadline = Now() + Math.Clamp(call.BudgetMs, 0L, LandingWaitMs);
        var tops = await TopPackagesAsync(budget, ct).ConfigureAwait(false);
        while (!LandingSettled(tops, display, packageName, before) && Now() < deadline)
        {
            await _delay(SettleWaitMs, ct).ConfigureAwait(false);
            tops = await TopPackagesAsync(budget, ct).ConfigureAwait(false);
        }
        ReportLanding(ok.Data, tops, display, packageName, before);
        if (tops is null)
        {
            await RefreshDisplayAsync(budget, ct).ConfigureAwait(false);
            if (!DisplayAlive())
            {
                return NoDisplay();
            }
        }
        if (Top(tops, display) == packageName)
        {
            return ok;
        }
        // Not landed: correct once (same component with NEW_TASK), then fail plainly if it still does not land.
        await RunAsync($"am start --display {display} -n {component} -f 0x10000000", budget, ct, LaunchTimeoutMs).ConfigureAwait(false);
        var extraDeadline = Now() + Math.Min(RetryLandingWaitMs, Math.Max(1L, call.BudgetMs));
        var retried = await TopPackagesAsync(budget, ct).ConfigureAwait(false);
        while (Top(retried, display) != packageName && Now() < extraDeadline)
        {
            await _delay(SettleWaitMs, ct).ConfigureAwait(false);
            retried = await TopPackagesAsync(budget, ct).ConfigureAwait(false);
        }
        if (Top(retried, display) == packageName)
        {
            ok.Data["retried"] = true;
            ReportLanding(ok.Data, retried, display, packageName, before);
            return ok;
        }
        if (retried is not { } seen)
        {
            return new BackendResult.Failed(
                RelayError.BackendUnavailable,
                "cannot read any display's top window after the retry: the launch may still have " +
                "happened - re-check with pkg.query or ui.snapshot instead of re-launching blind");
        }
        if (Top(seen, display) is null)
        {
            await RefreshDisplayAsync(budget, ct).ConfigureAwait(false);
            if (!DisplayAlive())
            {
                return NoDisplay();
            }
            return new BackendResult.Failed(
                RelayError.BackendUnavailable,
                $"display {display} reported no top window yet: still starting, or observation " +
                "ran out of this call's budget - retry rather than treating it as a refusal");
        }
        var topNow = Top(seen, display);
        var userTop = Top(seen, DefaultDisplay);
        var grabbedUserScreen = userTop == packageName && Top(before, DefaultDisplay) != packageName;
        if (LaunchMediators.IsMediator(topNow) || LaunchMediators.IsMediator(userTop))
        {
            return new BackendResult.Failed(
                RelayError.LaunchNotLanded,
                $"accepted but a system chooser may hold the front (top on display {display} is {Str(topNow)}, " +
                $"the user's own screen shows {Str(userTop)}): the target is waiting for " +
                "someone to pick an icon. The box may or may not expose a node tree on this ROM, so " +
                "read ui.snapshot first and fall back to screen.capture: the two icons often carry " +
                "neither text nor contentDescription, and only pixels tell the original from the " +
                "clone. " +
                "ui.tap takes no display argument: which screen a call runs on is the user's " +
                "execution-mode choice, so bring the interface back to the main screen first " +
                "(switch the execution mode, or send surface.virtual {\"action\":\"release\"}), " +
                "then capture, answer the box, and send this same launch again. Re-creating the " +
                "virtual display dismisses nothing.");
        }
        return new BackendResult.Failed(
            RelayError.LaunchNotLanded,
            $"accepted but not realised on display {display} (top is {Str(topNow)})" +
            (grabbedUserScreen
                ? $" - it came up on display {DefaultDisplay}, the user's own screen: send " +
                  "surface.virtual {\"action\":\"release\"} and then surface.virtual {} to get a " +
                  "fresh display, or leave trusted-display mode and drive the app where it is"
                : " - send surface.virtual {\"action\":\"release\"} then surface.virtual {} to " +
                  "re-create it; retrying this same call will land the same way"));
    }

    private static string? Top(IReadOnlyDictionary<int, string>? tops, int display) =>
        tops is not null && tops.TryGetValue(display, out var p) ? p : null;

    /// <summary>Kotlin string templates print a null as "null".</summary>
    private static string Str(string? value) => value ?? "null";

    public static bool LandingSettled(IReadOnlyDictionary<int, string>? tops, int display, string packageName, IReadOnlyDictionary<int, string>? before)
    {
        if (tops is null)
        {
            return false;
        }
        var there = Top(tops, display);
        if (there is not null && there != packageName)
        {
            return true;
        }
        if (there == packageName)
        {
            return true;
        }
        return Top(tops, DefaultDisplay) != Top(before, DefaultDisplay);
    }

    /// <summary>Writes the landing observation into the response, each case in its own words.</summary>
    public static void ReportLanding(JsonObject data, IReadOnlyDictionary<int, string>? tops, int display, string packageName,
        IReadOnlyDictionary<int, string>? before)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (tops is null)
        {
            data["verified"] = false;
            data["landingCheck"] = "failed";
            return;
        }
        var topThere = Top(tops, display);
        var onTarget = topThere == packageName;
        data["verified"] = onTarget;
        data["landedOn"] = onTarget ? display : null;
        data["topOnTarget"] = topThere;
        if (onTarget)
        {
            return;
        }
        var moved = topThere is not null && Top(before, display) != topThere;
        data["targetDisplayMoved"] = moved;
        var onUserScreen = Top(tops, DefaultDisplay) == packageName;
        if (onUserScreen && Top(before, DefaultDisplay) != packageName)
        {
            data["grabbedUserScreen"] = true;
            data["note"] = "the app came up on the user's own display: it did not stay on the trusted display";
        }
        else if (onUserScreen)
        {
            data["note"] = "the user was already on this app before the launch, so this response does not say where it came up";
        }
        else if (moved)
        {
            data["note"] = "something came up on the target display but its top activity is not the requested package " +
                "(a router activity, an alias or a cross-package launch looks like this)";
        }
    }

    /// <summary>Each display's top resumed package from one dumpsys; null when the question itself failed.</summary>
    private async Task<IReadOnlyDictionary<int, string>?> TopPackagesAsync(CallBudget budget, CancellationToken ct) =>
        await AskAsync(TopPackageCommand, budget, ct).ConfigureAwait(false) is { } dump ? ParseTopPackages(dump) : null;

    public static Dictionary<int, string> ParseTopPackages(string dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        var result = new Dictionary<int, string>();
        var display = -1;
        foreach (var line in dump.Split('\n'))
        {
            var trimmed = OrgJson.KotlinTrim(line.TrimEnd('\r'));
            if (DisplaySection().Match(trimmed) is { Success: true } section)
            {
                display = int.Parse(section.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            if (display >= 0 && !result.ContainsKey(display) && TopActivity().Match(trimmed) is { Success: true } top)
            {
                result[display] = top.Groups[1].Value;
            }
        }
        return result;
    }

    private async Task<BackendResult> DisplayTapAsync(JsonElement args, CallBudget budget, CancellationToken ct)
    {
        if (IntArgsError(args, KeyX, KeyY) is { } bad)
        {
            return Malformed(bad);
        }
        if (RequireDisplay() is not { } display)
        {
            return NoDisplay();
        }
        var x = GetInt(args, KeyX);
        var y = GetInt(args, KeyY);
        if (OffDisplay(x, y) is { } off)
        {
            return Malformed(off);
        }
        return await RunOnDisplayAsync($"input -d {display} tap {x} {y}", display, budget, ct).ConfigureAwait(false);
    }

    private async Task<BackendResult> DisplaySwipeAsync(JsonElement args, CallBudget budget, CancellationToken ct)
    {
        if (IntArgsError(args, KeyFromX, KeyFromY, KeyToX, KeyToY) is { } bad)
        {
            return Malformed(bad);
        }
        if (RequireDisplay() is not { } display)
        {
            return NoDisplay();
        }
        var fromX = GetInt(args, KeyFromX);
        var fromY = GetInt(args, KeyFromY);
        var toX = GetInt(args, KeyToX);
        var toY = GetInt(args, KeyToY);
        if ((OffDisplay(fromX, fromY) ?? OffDisplay(toX, toY)) is { } off)
        {
            return Malformed(off);
        }
        var duration = Math.Clamp(OrgJson.OptInt(args, KeyDurationMs, DefaultSwipeMs), 50, MaxSwipeMs);
        return await RunOnDisplayAsync($"input -d {display} swipe {fromX} {fromY} {toX} {toY} {duration}", display, budget, ct)
            .ConfigureAwait(false);
    }

    private async Task<BackendResult> DisplayTextAsync(JsonElement args, CallBudget budget, CancellationToken ct)
    {
        if (RequireDisplay() is not { } display)
        {
            return NoDisplay();
        }
        var text = OrgJson.OptString(args, KeyText);
        if (text.Any(c => c is '\n' or '\r'))
        {
            return Malformed(KeyText + " must be single line");
        }
        if (text.Length > MaxTextChars)
        {
            return Malformed(KeyText + " too long");
        }
        return await RunOnDisplayAsync($"input -d {display} text {ShellVerbs.ShellQuote(text)}", display, budget, ct).ConfigureAwait(false);
    }

    private async Task<BackendResult> DisplayKeyAsync(JsonElement args, CallBudget budget, CancellationToken ct)
    {
        if (RequireDisplay() is not { } display)
        {
            return NoDisplay();
        }
        var name = OrgJson.OptString(args, KeyKeyEvent);
        var code = KeyEvents.Where(k => k.Name == name).Select(k => (int?)k.Code).FirstOrDefault();
        if (code is null)
        {
            return Malformed($"unknown key: {name}, accepted: [{string.Join(", ", KeyEvents.Select(k => k.Name))}]");
        }
        return await RunOnDisplayAsync($"input -d {display} keyevent {code}", display, budget, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The display's current frame as an artifact. A display that stopped redrawing gets the last frame again,
    /// marked <c>stale</c> with <c>frameAgeMs</c>; a display that never produced one answers unreachable.
    /// </summary>
    private async Task<BackendResult> DisplayCaptureAsync(BackendCall call, CallBudget budget, CancellationToken ct)
    {
        if (RequireDisplay() is not { } display)
        {
            return NoDisplay();
        }
        if (budget.RemoteWaitMs(CaptureWaitMs, RemoteMarginArtifactMs) is not { } waitMs)
        {
            return RemoteBudgetExhausted(budget, RemoteMarginArtifactMs);
        }
        var frames = FrameCacheFor(call);
        var txn = await CaptureBytesAsync(budget.Cap, Lossless, Lossless, waitMs, ct).ConfigureAwait(false);
        if (txn is RemoteTxn<byte[]?>.GaveUp gaveUp)
        {
            return new BackendResult.Failed(RelayError.TransportTimeout,
                RemoteGiveUpReason("display capture", gaveUp.WaitedMs, gaveUp.SubmittedBeforeExpiryMs));
        }
        var fresh = ((RemoteTxn<byte[]?>.Done)txn).Value;
        byte[] bytes;
        long ageMs;
        if (fresh is not null)
        {
            var cached = frames.Peek(display);
            var unchanged = cached is { } c && c.Bytes.AsSpan().SequenceEqual(fresh);
            bytes = fresh;
            ageMs = unchanged ? cached!.Value.AgeMs : 0L;
            if (!unchanged)
            {
                frames.Remember(display, fresh);
            }
        }
        else if (frames.Peek(display) is { } cached)
        {
            bytes = cached.Bytes;
            ageMs = cached.AgeMs;
        }
        else
        {
            return new BackendResult.Failed(RelayError.BackendUnavailable, "no frame from the trusted display: retry, or re-create it with surface.virtual");
        }
        var data = new JsonObject { ["display"] = display, ["bytes"] = 0L, ["stale"] = ageMs > 0L };
        if (ageMs > 0L)
        {
            data["frameAgeMs"] = ageMs;
        }
        return StageAndPublish(call, bytes, data);
    }

    /// <summary>Drops the cached frame (user pixels outside the quota): on create, release, disconnect and every channel start.</summary>
    public void ClearFrameCache()
    {
        _frameCache?.Clear();
        _frameCache = null;
    }

    private LastFrameCache FrameCacheFor(BackendCall call) =>
        _frameCache ??= new LastFrameCache(Path.Combine(call.Paths.PrivateDir, LastFrameFile), Now);

    private async Task<RemoteTxn<byte[]?>> CaptureBytesAsync(string cap, int jpegQuality, int maxWidth, long waitMs, CancellationToken ct)
    {
        var txn = await TransactBoundedAsync(cap, "display", waitMs + RemoteSlackMs, () => RawDisplay(s => s.CaptureDisplay(waitMs, jpegQuality, maxWidth)), ct)
            .ConfigureAwait(false);
        return txn switch
        {
            RemoteTxn<DisplayReply>.GaveUp g => new RemoteTxn<byte[]?>.GaveUp(g.WaitedMs, g.SubmittedBeforeExpiryMs),
            RemoteTxn<DisplayReply>.Done { Value: var reply } => new RemoteTxn<byte[]?>.Done(reply.Code == 0 && reply.Frame is { Length: > 0 } f ? f : null),
            _ => throw new InvalidOperationException(),
        };
    }

    /// <summary>
    /// Background recording: frames pulled one by one from the display and encoded here. The answer reports the
    /// actual frame count and time covered, not the target rate.
    /// </summary>
    private async Task<BackendResult> RecordOnDisplayAsync(BackendCall call, CallBudget budget, CancellationToken ct)
    {
        if (RequireDisplay() is not { } display)
        {
            return NoDisplay();
        }
        var seconds = Math.Clamp(OrgJson.OptInt(call.Args, KeySeconds, DefaultRecordSeconds), 1, MaxRecordSeconds);
        if (budget.RemoteWaitMs(seconds * 1000L, RemoteMarginMs) is not { } recordMs)
        {
            return RemoteBudgetExhausted(budget, RemoteMarginMs);
        }
        var firstTxn = await CaptureBytesAsync(budget.Cap, RecordJpegQuality, RecordMaxWidth, Math.Min(CaptureWaitMs, recordMs), ct).ConfigureAwait(false);
        if (firstTxn is RemoteTxn<byte[]?>.GaveUp gaveUp)
        {
            return new BackendResult.Failed(RelayError.TransportTimeout,
                RemoteGiveUpReason("display capture", gaveUp.WaitedMs, gaveUp.SubmittedBeforeExpiryMs));
        }
        if (((RemoteTxn<byte[]?>.Done)firstTxn).Value is not { } firstBytes || Decode(firstBytes) is not { } first)
        {
            return NoFrame(display);
        }
        var width = Math.Max(first.Width, MinEncodePx) & ~1;
        var height = Math.Max(first.Height, MinEncodePx) & ~1;
        if (_reaper.NewArtifactFile(call.RequestId, RecordSuffix, (long)seconds * RecordBitsPerSecond / 8L) is not { } staged)
        {
            return new BackendResult.Failed(RelayError.StorageFull, "quota exhausted");
        }
        if (_platform.StartEncoder(staged, width, height, RecordFps, RecordBitsPerSecond) is not { } encoder)
        {
            _reaper.Discard(staged);
            return new BackendResult.Failed(RelayError.BackendUnavailable, $"no NV12 h264 encoder for {width}x{height} on this device");
        }
        var plane = new byte[Nv12.FrameBytes(encoder.Stride, encoder.SliceHeight)];
        var startedAt = Now();
        var deadlineAt = startedAt + recordMs;
        var frames = 0;
        var misses = 0;
        long written;
        DecodedFrame? last = first;
        try
        {
            while (true)
            {
                var frameAt = Now();
                if (frameAt >= deadlineAt)
                {
                    break;
                }
                var frameWait = Math.Min(deadlineAt - frameAt, RecordFrameWaitMs);
                var txn = await CaptureBytesAsync(budget.Cap, RecordJpegQuality, RecordMaxWidth, frameWait, ct).ConfigureAwait(false);
                DecodedFrame? fresh = null;
                var grabbedAt = frameAt;
                if (txn is RemoteTxn<byte[]?>.Done { Value: { } encoded })
                {
                    var at = Now();
                    if (Decode(encoded) is { } decoded)
                    {
                        fresh = decoded;
                        grabbedAt = at;
                    }
                }
                var frame = fresh ?? last;
                if (frame is null)
                {
                    misses++;
                    if (misses >= MaxFrameMisses)
                    {
                        break;
                    }
                }
                else
                {
                    misses = 0;
                    if (frame.Width >= 1 && frame.Height >= 1)
                    {
                        Nv12.Convert(frame.Argb, frame.Width, frame.Height, width, height, encoder.Stride, encoder.SliceHeight, plane);
                        if (encoder.Push(plane, plane.Length, (grabbedAt - startedAt) * 1000))
                        {
                            frames++;
                        }
                    }
                    if (fresh is not null)
                    {
                        last = fresh;
                    }
                }
                var pace = frameAt + 1000L / RecordFps - Now();
                if (pace > 0)
                {
                    await _delay(pace, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // finish on every branch: a container without its moov box is a file with no readable duration.
            written = encoder.Finish();
        }
        if (frames == 0 || written <= 0L)
        {
            _reaper.Discard(staged);
            return NoFrame(display);
        }
        var elapsedMs = Math.Max(1L, Now() - startedAt);
        var data = new JsonObject
        {
            ["display"] = display,
            ["seconds"] = seconds,
            ["recordedMs"] = elapsedMs,
            ["frames"] = frames,
            ["actualFps"] = Math.Round(frames * 1000.0 / elapsedMs * 100, MidpointRounding.ToEven) / 100.0,
            ["width"] = width,
            ["height"] = height,
            ["bytes"] = written,
        };
        return Publish(staged, call, data);
    }

    private DecodedFrame? Decode(byte[] bytes)
    {
        try
        {
            return _platform.DecodeFrame(bytes);
        }
#pragma warning disable CA1031 // upstream runCatching { BitmapFactory.decodeByteArray }.getOrNull()
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private static BackendResult.Failed NoFrame(int display) => new(
        RelayError.BackendUnavailable,
        $"no frame from the trusted display (display={display}): re-create it with surface.virtual");

    private static BackendResult.Failed NoDisplay() => new(
        RelayError.BackendUnavailable,
        "no trusted display: call surface.virtual with {\"action\":\"create\"} first");

    private int? RequireDisplay() => _displayId is var id and >= 0 ? id : null;

    private JsonObject DisplayData(string state)
    {
        var size = _displaySize;
        var id = _displayId;
        return new JsonObject
        {
            ["state"] = state,
            ["display"] = id,
            ["displayId"] = id,
            ["width"] = size.Width,
            ["height"] = size.Height,
        };
    }

    /// <summary>Lifecycle reconcile (no call budget): fixed cap, the cache is untouched when the wait is given up.</summary>
    public Task<bool> RefreshDisplayAsync() => RefreshDisplayAsync(null, CancellationToken.None);

    /// <summary>
    /// Reconciles the cached display with the service. True when the service answered (or there is nothing to ask);
    /// false when the budget could not cover the question or the wait was given up (cache unchanged).
    /// </summary>
    internal async Task<bool> RefreshDisplayAsync(CallBudget? budget, CancellationToken ct)
    {
        if (_binder is null)
        {
            _displayId = -1;
            _displaySize = (0, 0);
            return true;
        }
        long waitMs;
        if (budget is null)
        {
            waitMs = DisplayTxnWaitMs;
        }
        else if (budget.RemoteWaitMs(DisplayTxnWaitMs, RemoteMarginMs) is { } w)
        {
            waitMs = w;
        }
        else
        {
            return false;
        }
        var txn = await TransactBoundedAsync(budget?.Cap ?? CapLifecycle, "display", waitMs, () => RawDisplay(s => s.QueryDisplay()), ct)
            .ConfigureAwait(false);
        if (txn is not RemoteTxn<DisplayReply>.Done { Value: var reply })
        {
            return false;
        }
        _displayId = reply.Code;
        if (reply.Code < 0)
        {
            _displaySize = (0, 0);
        }
        else if (DisplaySizePattern().Match(reply.Reason) is { Success: true } m)
        {
            _displaySize = (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        }
        return true;
    }

    /// <summary>The raw display transaction on the remote thread: reads the binder at run time, touches no cache.</summary>
    private DisplayReply RawDisplay(Func<IShellService, DisplayReply> transact)
    {
        if (_binder is not { } target)
        {
            return new DisplayReply(-1, "shizuku service binder unavailable");
        }
        try
        {
            return transact(target);
        }
#pragma warning disable CA1031 // upstream catch (t: Throwable) { DisplayReply(-1, simpleName) }
        catch (Exception e)
#pragma warning restore CA1031
        {
            return new DisplayReply(-1, e.GetType().Name);
        }
    }

    /// <summary>
    /// A command aimed at the display. A failure reconciles first: an idle-released display makes <c>input -d</c>
    /// exit non-zero, and the right next step is re-creating it, not a "go grant something".
    /// </summary>
    private async Task<BackendResult> RunOnDisplayAsync(string command, int display, CallBudget budget, CancellationToken ct)
    {
        var outcome = await RunAsync(command, budget, ct).ConfigureAwait(false);
        if (outcome is BackendResult.Ok ok)
        {
            ok.Data["display"] = display;
            return ok;
        }
        await RefreshDisplayAsync(budget, ct).ConfigureAwait(false);
        return DisplayAlive() ? outcome : NoDisplay();
    }

    /// <summary>Same bound as the accessibility path: <c>input</c> silently ignores off-screen points. Unknown size: no check.</summary>
    private string? OffDisplay(int x, int y)
    {
        var (width, height) = _displaySize;
        if (width <= 0 || height <= 0)
        {
            return null;
        }
        return x < 0 || x >= width || y < 0 || y >= height ? $"target ({x},{y}) is outside the trusted display {width}x{height}" : null;
    }

    public static bool IsPrivilegeHandoffKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var lower = key.ToLowerInvariant();
        return BlockedSettingsKeys.Contains(lower) || PrivilegeKeyFragments.Any(f => lower.Contains(f, StringComparison.Ordinal));
    }

    // ── artifacts ──

    /// <summary>Stages <paramref name="bytes"/> as a png artifact; <paramref name="data"/>'s "bytes" key (a placeholder) takes the file size.</summary>
    private BackendResult StageAndPublish(BackendCall call, byte[] bytes, JsonObject data)
    {
        if (_reaper.NewArtifactFile(call.RequestId, ArtifactSuffix, CaptureEstimateBytes) is not { } staged)
        {
            return new BackendResult.Failed(RelayError.StorageFull, "quota exhausted");
        }
        bool written;
        try
        {
            File.WriteAllBytes(staged, bytes);
            written = new FileInfo(staged).Length > 0L;
        }
#pragma warning disable CA1031 // upstream runCatching { staged.writeBytes(bytes) }.isSuccess
        catch (Exception)
#pragma warning restore CA1031
        {
            written = false;
        }
        if (!written)
        {
            _reaper.Discard(staged);
            return new BackendResult.Failed(RelayError.InternalError, "frame copy failed");
        }
        return Publish(staged, call, data);
    }

    private BackendResult Publish(string staged, BackendCall call, JsonObject data)
    {
        var bytes = new FileInfo(staged).Length;
        if (_reaper.PublishArtifact(staged, call.RequestId) is not { } artifact)
        {
            return new BackendResult.Failed(RelayError.StorageFull, "artifact publish failed");
        }
        if (call.Paths.ToGuestPath(artifact) is not { } guestPath)
        {
            _reaper.Discard(artifact);
            return new BackendResult.Failed(RelayError.InternalError, "artifact path unmappable");
        }
        data["bytes"] = bytes;
        return new BackendResult.Ok(data, [guestPath], bytes);
    }

    private static string RemovePrefix(string s, string prefix) => s.StartsWith(prefix, StringComparison.Ordinal) ? s[prefix.Length..] : s;

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.]{0,127}\\z")]
    private static partial Regex PackagePattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}\\z")]
    private static partial Regex SettingKeyPattern();

    [GeneratedRegex("[A-Za-z_]+:\\s*(allow|deny|ignore|default|foreground|background)\\b", RegexOptions.ECMAScript)]
    private static partial Regex AppOpsMode();

    [GeneratedRegex("^Display #(\\d+) \\(", RegexOptions.ECMAScript)]
    private static partial Regex DisplaySection();

    [GeneratedRegex("^topResumedActivity=ActivityRecord\\{\\S+ u\\d+ ([A-Za-z][A-Za-z0-9_.]*)/", RegexOptions.ECMAScript)]
    private static partial Regex TopActivity();

    [GeneratedRegex("(\\d+)x(\\d+)", RegexOptions.ECMAScript)]
    private static partial Regex DisplaySizePattern();

    /// <summary>The single remote-transaction thread (daemon): a stuck binder call holds this thread only.</summary>
    private sealed class RemoteExecutor : IDisposable
    {
        private readonly BlockingCollection<Action> _work = [];

        public RemoteExecutor(string name)
        {
            var thread = new Thread(() =>
            {
                foreach (var item in _work.GetConsumingEnumerable())
                {
                    item();
                }
            })
            {
                IsBackground = true,
                Name = name,
            };
            thread.Start();
        }

        public Task<T> Submit<T>(Func<T> work)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _work.Add(() =>
            {
                try
                {
                    tcs.SetResult(work());
                }
#pragma warning disable CA1031 // surfaced to the awaiting caller (upstream rethrows ExecutionException's cause)
                catch (Exception e)
#pragma warning restore CA1031
                {
                    tcs.SetException(e);
                }
            });
            return tcs.Task;
        }

        /// <summary>Stops taking work; the thread ends after what is queued (it is a background thread either way).</summary>
        public void Dispose() => _work.CompleteAdding();
    }
}
