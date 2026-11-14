// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/exec/a11y/A11yBackend.kt (available, supports, trustedDisplayReachable,
//     execute, argsError, nodeActions, targetOwner, treeFor, TreeOutcome, trustedTreeMissing, click, swipe,
//     dispatchStroke, setText, firstEditable, globalAction, capture, estimatePngBytes; the companion constants)
//   src/main/java/interlock/relay/core/exec/a11y/DisplayTree.kt (TreeLook)
// Registered in docs/UPSTREAMS.md. Everything Android-specific (the service instance, gestures, screenshots, the
// cross-display window query) is behind IA11yPlatform, implemented in DshMobile.Android.
//
// Security: the host-window rule runs before any writing capability (the agent must never drive the host's own
// screens, i.e. press its own approval); ui.text re-checks the active root's package itself.

using System.Text.Json;
using System.Text.Json.Nodes;
using DshMobile.Runtime.Automation.Relay.Exec;

namespace DshMobile.Runtime.Automation.Relay.A11y;

/// <summary>Upstream <c>TreeLook</c>: the three outcomes of asking for one display's tree.</summary>
public abstract record TreeLook
{
    public sealed record Has(A11yTree Tree) : TreeLook;

    /// <summary>The query worked and that display holds no window with a tree right now.</summary>
    public sealed record NoWindowOnDisplay : TreeLook;

    /// <summary>The query itself failed; <paramref name="Cause"/> says how (never "the platform gives no tree").</summary>
    public sealed record QueryFailed(string Cause) : TreeLook;
}

/// <summary>A screenshot of the default display, or the mapped reason it was refused.</summary>
public abstract record A11yShot
{
    /// <summary><paramref name="WritePng"/> writes the PNG and returns its size: -1 when the bitmap cannot be converted, 0 when writing failed.</summary>
    public sealed record Taken(int Width, int Height, Func<string, long> WritePng, Action Release) : A11yShot;

    public sealed record Refused(RelayError Error, string Detail) : A11yShot;
}

/// <summary>The accessibility service as the backend uses it (implemented over the running service on Android).</summary>
public interface IA11yPlatform
{
    /// <summary><c>RelayAccessibilityService.current() != null</c>.</summary>
    bool Connected { get; }

    string SelfPackage { get; }

    int Sdk { get; }

    /// <summary>The default display's real pixel frame (maximum window metrics, not the app window).</summary>
    Bounds DisplayBounds();

    int DensityDpi { get; }

    IA11yNode? RootInActiveWindow();

    /// <summary>The package of every window root the service sees (the host-window rule).</summary>
    IReadOnlyList<string?> WindowRootPackages();

    /// <summary>Upstream <c>DisplayTree.ofDisplay</c> for a trusted display.</summary>
    TreeLook TreeOfDisplay(int displayId);

    /// <summary>One stroke on the default display; null when the system completed it, else "cancelled" / "rejected".</summary>
    Task<string?> DispatchStrokeAsync(float fromX, float fromY, float toX, float toY, long durationMs, CancellationToken ct);

    bool PerformGlobalAction(int action);

    Task<A11yShot> TakeScreenshotAsync(CancellationToken ct);
}

/// <summary>Upstream <c>A11yBackend</c>.</summary>
public sealed class A11yBackend(IA11yPlatform platform, Storage.StorageReaper reaper, TimeProvider time, Func<int>? trustedDisplayId = null)
    : IRelayBackend
{
    public const string MissingNoWindow = "no-window-on-display";
    public const long TapDurationMs = 60;
    public const long SwipeDurationMs = 260;
    public const long MinGestureMs = 50;
    public const long MaxGestureMs = 3_000;
    public const long BytesPerPixel = 4;
    public const long MaxEstimateBytes = 32L * 1024 * 1024;

    private readonly IA11yPlatform _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    private readonly Storage.StorageReaper _reaper = reaper ?? throw new ArgumentNullException(nameof(reaper));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly Func<int> _trustedDisplayId = trustedDisplayId ?? (() => -1);
    private readonly TrustedNodeTree _trustedNodeTree = new();
    private int _captureBusy;

    public bool Available() => _platform.Connected;

    public bool Supports(string capability, SurfaceKind surface)
    {
        if (A11yRules.TreeCapabilities.Contains(capability))
        {
            return surface == SurfaceKind.Foreground || (surface == SurfaceKind.BackendTrusted && TrustedDisplayReachable());
        }
        if (A11yRules.CoordinateCapabilities.Contains(capability))
        {
            return surface == SurfaceKind.Foreground;
        }
        return capability == "screen.capture" && surface == SurfaceKind.Foreground && _platform.Sdk >= 30;
    }

    /// <summary>Cross-display windows need API 33 (getWindowsOnAllDisplays); below it the display exists but no tree.</summary>
    private bool TrustedDisplayReachable() => _trustedDisplayId() >= 0 && _platform.Sdk >= 33;

    public string TrustedNodeTreeState(int displayId) => _trustedNodeTree.State(displayId);

    public async Task<BackendResult> ExecuteAsync(BackendCall backendCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backendCall);
        var call = backendCall;
        if (A11yRules.ArgsError(call.Capability, call.Args) is { } bad)
        {
            return Malformed(bad);
        }
        if (!_platform.Connected)
        {
            return new BackendResult.Failed(RelayError.BackendUnavailable, "service not connected");
        }
        if (HostWindowRule.Suspends(call.Capability, call.Surface, _platform.WindowRootPackages(), _platform.SelfPackage))
        {
            return HostWindowRule.SuspendedByHost();
        }
        if (A11yRules.TreeCapabilities.Contains(call.Capability))
        {
            var outcome = TreeFor(call);
            if (outcome.Tree is not { } tree)
            {
                return TrustedTreeMissing(outcome);
            }
            var reader = new NodeReader(tree);
            var env = A11yWriteEnv.Live(call.BudgetMs, _platform.SelfPackage, _platform.Sdk, _time);
            return call.Capability switch
            {
                "ui.snapshot" => A11ySnapshot.Snapshot(tree),
                "ui.click" => await reader.ClickAsync(call.Args, env).ConfigureAwait(false),
                "ui.longClick" => reader.LongClick(call.Args, env),
                "ui.select" => reader.Select(call.Args, env),
                "ui.dismiss" => reader.Dismiss(call.Args, env),
                "ui.scroll" => await reader.ScrollAsync(call.Args, env, cancellationToken).ConfigureAwait(false),
                "ui.setValue" => reader.SetValue(call.Args, env),
                "ui.node" => reader.ReadNode(call.Args),
                "ui.waitFor" => await reader.WaitForAsync(call.Args, call.BudgetMs, _time, cancellationToken).ConfigureAwait(false),
                "ui.setProgress" => await reader.SetProgressAsync(call.Args, env, cancellationToken).ConfigureAwait(false),
                _ => reader.ImeAction(call.Args, env),
            };
        }
        return call.Capability switch
        {
            "ui.tap" => await ClickAsync(call.Args, cancellationToken).ConfigureAwait(false),
            "ui.swipe" => await SwipeAsync(call.Args, cancellationToken).ConfigureAwait(false),
            "ui.text" => SetText(OrgJson.OptString(call.Args, A11yRules.KeyText)),
            "ui.key" => GlobalAction(call.Args),
            "screen.capture" => _platform.Sdk >= 30
                ? await CaptureAsync(call, cancellationToken).ConfigureAwait(false)
                : new BackendResult.Failed(RelayError.CapabilityUnavailableOnDevice, call.Capability),
            _ => new BackendResult.Failed(RelayError.CapabilityNotImplemented, call.Capability),
        };
    }

    /// <summary>Read-only owner lookup for the gate's consent check, on both displays; null when unknown (not "allowed").</summary>
    public TargetOwner? TargetOwnerOf(int nodeId)
    {
        if (!_platform.Connected)
        {
            return null;
        }
        var trees = new List<A11yTree> { HostTree() };
        if (TrustedDisplayReachable() && _platform.TreeOfDisplay(_trustedDisplayId()) is TreeLook.Has has)
        {
            trees.Add(has.Tree);
        }
        foreach (var tree in trees)
        {
            if (new NodeReader(tree).OwnerOf(nodeId) is { } owner)
            {
                return owner;
            }
        }
        return null;
    }

    private sealed record TreeOutcome(A11yTree? Tree, string? Missing = null, int DisplayId = -1);

    private TreeOutcome TreeFor(BackendCall call)
    {
        if (call.Surface != SurfaceKind.BackendTrusted)
        {
            return new TreeOutcome(HostTree());
        }
        var displayId = _trustedDisplayId();
        switch (_platform.TreeOfDisplay(displayId))
        {
            case TreeLook.Has has:
                if (displayId >= 0)
                {
                    _trustedNodeTree.Observe(displayId, true);
                }
                return new TreeOutcome(has.Tree, DisplayId: displayId);
            case TreeLook.NoWindowOnDisplay:
                if (displayId >= 0)
                {
                    _trustedNodeTree.Observe(displayId, false);
                }
                return new TreeOutcome(null, MissingNoWindow, displayId);
            case TreeLook.QueryFailed failed:
                return new TreeOutcome(null, failed.Cause, displayId);
            default:
                throw new InvalidOperationException();
        }
    }

    private static BackendResult.Failed TrustedTreeMissing(TreeOutcome outcome)
    {
        var displayId = outcome.DisplayId;
        var reason = displayId < 0
            ? "no trusted display to read: create one with surface.virtual and launch an app on it, or run on the foreground surface"
            : outcome.Missing == MissingNoWindow
                ? $"the accessibility service was given no window for trusted display {displayId} (the id we recorded the last " +
                  "time it answered): node-level calls cannot read that screen right now - use ui.tap / ui.swipe for " +
                  "coordinates or screen.capture / screen.record for pixels"
                : $"the cross-display window query for trusted display {displayId} did not answer ({outcome.Missing}): that " +
                  "is not the same as the display having no windows - retry, or run on the foreground surface";
        return new BackendResult.Failed(RelayError.BackendUnavailable, reason);
    }

    private A11yTree HostTree() => new(A11yConstants.DefaultDisplay, _platform.DisplayBounds(), _platform.DensityDpi,
        A11yTree.RetryOnce(_platform.RootInActiveWindow));

    private async Task<BackendResult> ClickAsync(JsonElement args, CancellationToken ct)
    {
        if (A11yRules.BadArgShape(args, A11yRules.PointKeys, A11yRules.PointKeys) is { } shape)
        {
            return Malformed(shape);
        }
        var screen = _platform.DisplayBounds();
        if (A11yRules.BadCoordinate(args, [(A11yRules.KeyX, screen.Width), (A11yRules.KeyY, screen.Height)]) is { } coord)
        {
            return Malformed(coord);
        }
        var x = (float)A11yRules.NumberArg(args, A11yRules.KeyX)!.Value;
        var y = (float)A11yRules.NumberArg(args, A11yRules.KeyY)!.Value;
        return await DispatchStrokeAsync(x, y, x, y, TapDurationMs, ct).ConfigureAwait(false);
    }

    private async Task<BackendResult> SwipeAsync(JsonElement args, CancellationToken ct)
    {
        if (A11yRules.BadArgShape(args, A11yRules.StrokeKeys, A11yRules.SwipeKeys) is { } shape)
        {
            return Malformed(shape);
        }
        var screen = _platform.DisplayBounds();
        if (A11yRules.BadCoordinate(args,
            [
                (A11yRules.KeyFromX, screen.Width), (A11yRules.KeyToX, screen.Width),
                (A11yRules.KeyFromY, screen.Height), (A11yRules.KeyToY, screen.Height),
            ]) is { } coord)
        {
            return Malformed(coord);
        }
        long durationMs;
        if (args.TryGetProperty(A11yRules.KeyDurationMs, out var rawDuration))
        {
            if (A11yRules.NumberArg(args, A11yRules.KeyDurationMs) is not { } d)
            {
                return Malformed(ArgErrors.NotNumber(A11yRules.KeyDurationMs, rawDuration));
            }
            durationMs = Math.Clamp(OrgJson.JavaDoubleToLong(d), MinGestureMs, MaxGestureMs);
        }
        else
        {
            durationMs = SwipeDurationMs;
        }
        float N(string key) => (float)A11yRules.NumberArg(args, key)!.Value;
        return await DispatchStrokeAsync(N(A11yRules.KeyFromX), N(A11yRules.KeyFromY), N(A11yRules.KeyToX), N(A11yRules.KeyToY),
            durationMs, ct).ConfigureAwait(false);
    }

    /// <summary>Only the system's completion counts: cancelled or refused at dispatch is a failure, never a success.</summary>
    private async Task<BackendResult> DispatchStrokeAsync(float fromX, float fromY, float toX, float toY, long requestedMs, CancellationToken ct)
    {
        var startedAt = _time.GetTimestamp();
        var failure = await _platform.DispatchStrokeAsync(fromX, fromY, toX, toY, requestedMs, ct).ConfigureAwait(false);
        var observedMs = (long)_time.GetElapsedTime(startedAt).TotalMilliseconds;
        return failure is not null
            ? new BackendResult.Failed(RelayError.BackendUnavailable, $"gesture {failure}")
            : new BackendResult.Ok(new JsonObject { ["durationMs"] = observedMs });
    }

    private BackendResult SetText(string text)
    {
        if (_platform.RootInActiveWindow() is not { } root)
        {
            return NodeReader.NoActiveWindow(A11yConstants.DefaultDisplay);
        }
        if (root.PackageName == _platform.SelfPackage)
        {
            return HostWindowRule.SuspendedByHost();
        }
        if ((root.FindInputFocus() ?? FirstEditable(root)) is not { } target)
        {
            return new BackendResult.Failed(RelayError.NoEditableTarget, "no editable target");
        }
        bool applied;
        try
        {
            applied = target.PerformAction(A11yConstants.ActionSetText, new A11yActionArgs(SetText: text));
        }
#pragma warning disable CA1031 // a platform exception is a refusal
        catch (Exception)
#pragma warning restore CA1031
        {
            applied = false;
        }
        return applied
            ? new BackendResult.Ok(new JsonObject { ["length"] = text.Length })
            : new BackendResult.Failed(RelayError.BackendUnavailable, "target rejected text");
    }

    /// <summary>The first editable node within the snapshot caps: only the fallback when nothing holds input focus.</summary>
    private static IA11yNode? FirstEditable(IA11yNode root)
    {
        var visited = 0;
        IA11yNode? found = null;
        bool Descend(IA11yNode node, int depth)
        {
            if (depth > A11ySnapshot.MaxDepth || visited >= A11ySnapshot.MaxNodes)
            {
                return false;
            }
            visited++;
            if (node.IsEditable)
            {
                found = node;
                return true;
            }
            for (var i = 0; i < node.ChildCount; i++)
            {
                if (node.GetChild(i) is { } child && Descend(child, depth + 1))
                {
                    return true;
                }
            }
            return false;
        }
        Descend(root, 0);
        return found;
    }

    private BackendResult GlobalAction(JsonElement args)
    {
        var requested = OrgJson.OptString(args, A11yRules.KeyKey).ToLowerInvariant();
        if (!A11yRules.GlobalActions.TryGetValue(requested, out var action))
        {
            return Malformed($"unknown value for {A11yRules.KeyKey}: {requested}, accepted: [{string.Join(", ", A11yRules.GlobalActions.Keys)}]");
        }
        return _platform.PerformGlobalAction(action)
            ? new BackendResult.Ok(new JsonObject { [A11yRules.KeyKey] = requested })
            : new BackendResult.Failed(RelayError.BackendUnavailable, $"action unavailable: {requested}");
    }

    private async Task<BackendResult> CaptureAsync(BackendCall call, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _captureBusy, 1, 0) != 0)
        {
            return new BackendResult.Failed(RelayError.RateLimited, "capture already in flight");
        }
        try
        {
            var shot = await _platform.TakeScreenshotAsync(ct).ConfigureAwait(false);
            if (shot is A11yShot.Refused refused)
            {
                return new BackendResult.Failed(refused.Error, refused.Detail);
            }
            var taken = (A11yShot.Taken)shot;
            try
            {
                var estimate = Math.Min((long)taken.Width * taken.Height * BytesPerPixel, MaxEstimateBytes);
                if (_reaper.NewArtifactFile(call.RequestId, "png", estimate) is not { } staged)
                {
                    return new BackendResult.Failed(RelayError.StorageFull, "quota exhausted");
                }
                var bytes = taken.WritePng(staged);
                if (bytes < 0)
                {
                    _reaper.Discard(staged);
                    return new BackendResult.Failed(RelayError.SurfaceSecureWindow, "convert rejected");
                }
                if (bytes == 0)
                {
                    _reaper.Discard(staged);
                    return new BackendResult.Failed(RelayError.StorageFull, "artifact write failed");
                }
                if (_reaper.PublishArtifact(staged, call.RequestId) is not { } artifact)
                {
                    return new BackendResult.Failed(RelayError.StorageFull, "artifact publish failed");
                }
                if (call.Paths.ToGuestPath(artifact) is not { } guestPath)
                {
                    _reaper.Discard(artifact);
                    return new BackendResult.Failed(RelayError.InternalError, "artifact path unmappable");
                }
                return new BackendResult.Ok(new JsonObject { ["bytes"] = bytes }, [guestPath], bytes);
            }
            finally
            {
                taken.Release();
            }
        }
        finally
        {
            Volatile.Write(ref _captureBusy, 0);
        }
    }

    private static BackendResult.Failed Malformed(string reason) => new(RelayError.TransportMalformed, reason);
}
