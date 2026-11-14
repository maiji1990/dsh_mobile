// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/exec/a11y/NodeActions.kt (consentRefusal, rootForWrite, fingerprint, act,
//     dispatch, click, longClick, select, dismiss, scroll, settleUntilMoved, valueReached, setValue, setProgress,
//     readBackUntilSettled, readBackRange, imeAction, isEditableField; the scroll / read-back constants)
// Registered in docs/UPSTREAMS.md. The node-level write path; the host-window rule is applied before it by the backend
// (A11yBackend), and rootForWrite re-applies it on every scroll round. Locked by tests/TestVectors/relay-a11y-write.json.
//
// Security: every dispatch first re-checks the resolved node against the system consent surfaces (the gate only saw
// the arguments, and a selector without "package" says nothing about whose button it is).

using System.Text.Json;
using System.Text.Json.Nodes;
using DshMobile.Runtime.Automation.Relay.Exec;

namespace DshMobile.Runtime.Automation.Relay.A11y;

/// <summary>What a node write needs besides the tree: the call budget, who "self" is, the consent check, the clock.</summary>
/// <param name="ConsentTarget">Upstream <c>ConsentSurfaces.looksLikeConsentControl</c> on the resolved node.</param>
/// <param name="MonotonicMs">Monotonic clock in milliseconds (never wall time).</param>
/// <param name="Sdk">Build.VERSION.SDK_INT (ui.imeAction needs 30).</param>
public sealed record A11yWriteEnv(
    long BudgetMs,
    string SelfPackage,
    Func<IA11yNode, bool> ConsentTarget,
    Func<long> MonotonicMs,
    Func<long, CancellationToken, Task> Delay,
    int Sdk)
{
    public static A11yWriteEnv Live(long budgetMs, string selfPackage, int sdk, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        var origin = time.GetTimestamp();
        return new A11yWriteEnv(budgetMs, selfPackage,
            node => Interlock.ConsentSurfaces.LooksLikeConsentControl(node.PackageName, node.ViewId),
            () => (long)time.GetElapsedTime(origin).TotalMilliseconds,
            (ms, ct) => Task.Delay(TimeSpan.FromMilliseconds(ms), time, ct), sdk);
    }
}

public sealed partial class NodeReader
{
    public const string KeyDirection = "direction";
    public const string KeyTimes = "times";
    public const string KeyUntil = "until";
    public const string KeyPercent = "percent";
    public const string KeyValue = "value";
    public const int MaxScrollTimes = 20;
    public const long ReadbackPollMs = 150;
    public const int MaxReadbackRounds = 4;
    public const long ReadbackBudgetMs = 600;
    public const long ScrollSettlePollMs = 80;
    public const int MaxScrollSettleRounds = 8;
    public const long ScrollStepSettleMs = 120;
    public const long ScrollBudgetMs = 8_000;
    public const int MaxUntilSteps = 40;

    private const string DispatchNote =
        "dispatched means the node accepted the action, not that the UI changed; re-read with ui.node or ui.waitFor to confirm the effect";

    public Task<BackendResult> ClickAsync(JsonElement args, A11yWriteEnv env) => Task.FromResult(Dispatch(args, A11yConstants.ActionClick, null, env));

    public BackendResult LongClick(JsonElement args, A11yWriteEnv env) => Dispatch(args, A11yConstants.ActionLongClick, null, env);

    public BackendResult Select(JsonElement args, A11yWriteEnv env) => Dispatch(args, A11yConstants.ActionSelect, null, env);

    public BackendResult Dismiss(JsonElement args, A11yWriteEnv env) => Dispatch(args, A11yConstants.ActionDismiss, null, env);

    private BackendResult.Failed? ConsentRefusal(Target.One target, A11yWriteEnv env) =>
        !env.ConsentTarget(target.Node)
            ? null
            : new BackendResult.Failed(RelayError.NodeSystemConsentTarget,
                "the resolved target is a system consent control: the user has to answer that dialog. " +
                "Refused at execution time - naming the target by nodeId, by view id, or by leaving " +
                "the package out of the selector does not bypass this check.");

    /// <summary>The tree a write re-reads: never the host's own window (the agent must not press its own approval).</summary>
    private IA11yNode? RootForWrite(A11yWriteEnv env) => Root() is { } r && r.PackageName != env.SelfPackage ? r : null;

    private static string Fingerprint(IA11yNode node)
    {
        var b = node.BoundsInScreen;
        return $"{node.ClassName ?? ""}|{node.ViewId ?? ""}|Rect({b.Left}, {b.Top} - {b.Right}, {b.Bottom})";
    }

    private BackendResult Act(Target.One target, int action, A11yActionArgs? extras, A11yWriteEnv env, bool allowUnadvertised = false)
    {
        var name = ActionName(action);
        if (ConsentRefusal(target, env) is { } refused)
        {
            return refused;
        }
        if (!allowUnadvertised && !target.Node.ActionIds.Contains(action))
        {
            return new BackendResult.Failed(RelayError.NodeNotActionable, $"node does not support {name}");
        }
        bool applied;
        try
        {
            applied = target.Node.PerformAction(action, extras);
        }
#pragma warning disable CA1031 // upstream runCatching: a platform exception is a refusal
        catch (Exception)
#pragma warning restore CA1031
        {
            applied = false;
        }
        if (!applied)
        {
            return new BackendResult.Failed(RelayError.NodeActionRejected, $"{name} was refused by the app");
        }
        // The node read back after the action: the one resolved before it is a stale copy of its fields.
        var fresh = FindByIdPreorder(Root(), target.NodeId);
        return new BackendResult.Ok(new JsonObject
        {
            ["dispatched"] = true,
            ["effectVerified"] = false,
            ["nodeId"] = target.NodeId,
            ["node"] = Describe(fresh ?? target.Node),
            ["note"] = DispatchNote,
        });
    }

    private BackendResult Dispatch(JsonElement args, int action, A11yActionArgs? extras, A11yWriteEnv env) => Resolve(args) switch
    {
        Target.Bad bad => new BackendResult.Failed(bad.Error, bad.Reason),
        Target.Many many => Ambiguous(many.Matches),
        Target.One one => Act(one, action, extras, env),
        _ => throw new InvalidOperationException(),
    };

    /// <summary>
    /// Upstream <c>scroll</c>: containers and sliders. Ranged nodes wait for each step to land before the next and report
    /// <c>advanced</c> (observed) apart from <c>dispatched</c> (accepted); the node is re-resolved by id and identity
    /// every round.
    /// </summary>
    public async Task<BackendResult> ScrollAsync(JsonElement args, A11yWriteEnv env, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(env);
        var direction = OrgJson.OptString(args, KeyDirection).ToLowerInvariant();
        if (direction is not ("forward" or "backward"))
        {
            return Malformed($"{KeyDirection} must be forward or backward");
        }
        var action = direction == "forward" ? A11yConstants.ActionScrollForward : A11yConstants.ActionScrollBackward;
        var timesGiven = args.TryGetProperty(KeyTimes, out var rawTimes);
        int times;
        if (timesGiven)
        {
            if (A11yRules.NumberArg(args, KeyTimes) is not { } t)
            {
                return Malformed(ArgErrors.NotNumber(KeyTimes, rawTimes));
            }
            times = OrgJson.JavaDoubleToInt(t);
        }
        else
        {
            times = 1;
        }
        if (times < 1 || times > MaxScrollTimes)
        {
            return Malformed($"{KeyTimes} must be within 1..{MaxScrollTimes}");
        }
        float? until = null;
        if (args.TryGetProperty(KeyUntil, out var rawUntil))
        {
            if (A11yRules.NumberArg(args, KeyUntil) is not { } u)
            {
                return Malformed(ArgErrors.NotNumber(KeyUntil, rawUntil));
            }
            until = (float)u;
        }
        var stepLimit = until is null || timesGiven ? times : MaxUntilSteps;
        Target.One picked;
        switch (Resolve(args))
        {
            case Target.Bad bad:
                return new BackendResult.Failed(bad.Error, bad.Reason);
            case Target.Many many:
                return Ambiguous(many.Matches);
            case Target.One one:
                picked = one;
                break;
            default:
                throw new InvalidOperationException();
        }
        if (ConsentRefusal(picked, env) is { } refused)
        {
            return refused;
        }
        var startValue = picked.Node.Range?.Current;
        if (until is { } goal)
        {
            if (picked.Node.Range is not { } range)
            {
                return new BackendResult.Failed(RelayError.NodeNotActionable, $"{KeyUntil} needs a node that reports a range; read ui.node {{range}}");
            }
            if (goal < range.Min || goal > range.Max)
            {
                return Malformed($"{KeyUntil}={OrgJson.JavaFloat(goal)} is outside this node's range " +
                    $"[{OrgJson.JavaFloat(range.Min)}..{OrgJson.JavaFloat(range.Max)}] (read by ui.node)");
            }
        }
        // actionList, not isScrollable: sliders take scroll actions while reporting isScrollable false.
        if (!picked.Node.ActionIds.Contains(action))
        {
            return new BackendResult.Failed(RelayError.NodeNotActionable, "node does not accept scroll; read ui.node {range} to see what it is");
        }
        var dispatched = 0;
        var advanced = 0;
        var measured = false;
        var reached = false;
        var landed = startValue;
        string? stoppedReason = null;
        var wanted = Fingerprint(picked.Node);
        var stepDeadline = env.MonotonicMs() + Math.Min(ScrollBudgetMs, Math.Max(env.BudgetMs, 0));
        // Already on the target: dispatch nothing (one more step on a wrapping picker would leave it).
        if (until is { } g0 && startValue is { } s0 && ValueReached(s0, g0))
        {
            reached = true;
            landed = startValue;
        }
        for (var round = 0; round < (reached ? 0 : stepLimit); round++)
        {
            var fresh = FindByIdPreorder(RootForWrite(env), picked.NodeId);
            if (fresh is null)
            {
                stoppedReason = dispatched == 0 ? "node is gone from the tree" : $"node is gone after {advanced} observed step(s) of {dispatched} accepted";
                break;
            }
            if (Fingerprint(fresh) != wanted)
            {
                stoppedReason = $"the node at this position changed after {advanced} observed step(s) of {dispatched} accepted";
                break;
            }
            if (!fresh.ActionIds.Contains(action))
            {
                stoppedReason = $"node stopped accepting scroll after {advanced} observed step(s) of {dispatched} accepted";
                break;
            }
            var before = fresh.Range?.Current;
            bool accepted;
            try
            {
                accepted = fresh.PerformAction(action, null);
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                accepted = false;
            }
            if (!accepted)
            {
                stoppedReason = $"app stopped accepting scroll after {advanced} observed step(s) of {dispatched} accepted";
                break;
            }
            dispatched++;
            if (before is not { } b)
            {
                await env.Delay(ScrollStepSettleMs, ct).ConfigureAwait(false);
                continue;
            }
            measured = true;
            var movedTo = await SettleUntilMovedAsync(picked.NodeId, wanted, b, stepDeadline, env, ct).ConfigureAwait(false);
            if (movedTo is not { } moved)
            {
                stoppedReason = env.MonotonicMs() >= stepDeadline
                    ? $"call budget exhausted after {advanced} step(s)"
                    : $"value stopped changing after {advanced} step(s); likely at either end";
                break;
            }
            advanced++;
            landed = moved;
            if (until is { } g && ValueReached(moved, g))
            {
                reached = true;
                break;
            }
        }
        if (dispatched == 0 && !reached)
        {
            var gone = stoppedReason?.StartsWith("node is gone", StringComparison.Ordinal) == true ||
                FindByIdPreorder(Root(), picked.NodeId) is null;
            return new BackendResult.Failed(gone ? RelayError.NodeNotFound : RelayError.NodeNotActionable,
                stoppedReason ?? "node would not scroll from here");
        }
        var data = new JsonObject
        {
            ["dispatched"] = dispatched,
            ["requested"] = times,
            ["stepLimit"] = stepLimit,
            ["direction"] = direction,
        };
        if (measured)
        {
            data["advanced"] = advanced;
        }
        if (until is { } target)
        {
            data["reached"] = reached;
            data["target"] = JavaFloat(target);
        }
        if (landed is { } l)
        {
            data["value"] = JavaFloat(l);
        }
        if (stoppedReason is not null)
        {
            data["stoppedEarly"] = true;
            data["stoppedReason"] = stoppedReason;
        }
        data["movementVerified"] = measured && advanced == dispatched;
        data["note"] = measured
            ? "advanced counts observed value changes, one settle per step; read ui.node {range} for the value it landed on"
            : "this node reports no range, so movement cannot be measured here; re-read with ui.snapshot or ui.waitFor to confirm the list moved";
        return new BackendResult.Ok(data);
    }

    private async Task<float?> SettleUntilMovedAsync(int nodeId, string identity, float before, long deadline, A11yWriteEnv env, CancellationToken ct)
    {
        var rounds = 0;
        while (rounds < MaxScrollSettleRounds)
        {
            await env.Delay(ScrollSettlePollMs, ct).ConfigureAwait(false);
            rounds++;
            if (ReadBackRange(nodeId, identity) is not { } current)
            {
                return null;
            }
#pragma warning disable S1244 // upstream compares the reported floats exactly
            if (current != before)
#pragma warning restore S1244
            {
                return current;
            }
            if (env.MonotonicMs() >= deadline)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>Half a step of tolerance: controls round to their own step, 23.0 and 23 are the same notch.</summary>
    private static bool ValueReached(float current, float target) => Math.Abs(current - target) < 0.5f;

    /// <summary>Upstream <c>setValue</c>: write text directly (no focus needed), then read the node back by id.</summary>
    public BackendResult SetValue(JsonElement args, A11yWriteEnv env)
    {
        ArgumentNullException.ThrowIfNull(env);
        var text = OrgJson.OptString(args, A11yRules.KeyText);
        if (IsBlank(text))
        {
            return Malformed($"missing arg: {A11yRules.KeyText}");
        }
        Target.One picked;
        switch (Resolve(args))
        {
            case Target.Bad bad:
                return new BackendResult.Failed(bad.Error, bad.Reason);
            case Target.Many many:
                return Ambiguous(many.Matches);
            case Target.One one:
                picked = one;
                break;
            default:
                throw new InvalidOperationException();
        }
        var result = Act(picked, A11yConstants.ActionSetText, new A11yActionArgs(SetText: text), env);
        if (result is BackendResult.Ok ok)
        {
            // Read back by id, not by the selector (which usually names the old text); no hard "matches" verdict.
            ok.Data["requested"] = text;
            if (FindByIdPreorder(Root(), picked.NodeId) is { } fresh)
            {
                ok.Data["readBack"] = fresh.Text ?? "";
            }
            else
            {
                ok.Data["readBackUnavailable"] = "node is no longer in the tree";
            }
        }
        return result;
    }

    /// <summary>Upstream <c>setProgress</c>: <c>value</c> wins over <c>percent</c>; the value is read back until it settles.</summary>
    public async Task<BackendResult> SetProgressAsync(JsonElement args, A11yWriteEnv env, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(env);
        var hasPercent = args.TryGetProperty(KeyPercent, out var rawPercent);
        var hasValue = args.TryGetProperty(KeyValue, out var rawValue);
        if (!hasPercent && !hasValue)
        {
            return Malformed($"give {KeyPercent} or {KeyValue}");
        }
        Target.One picked;
        switch (Resolve(args))
        {
            case Target.Bad bad:
                return new BackendResult.Failed(bad.Error, bad.Reason);
            case Target.Many many:
                return Ambiguous(many.Matches);
            case Target.One one:
                picked = one;
                break;
            default:
                throw new InvalidOperationException();
        }
        if (picked.Node.Range is not { } range)
        {
            return new BackendResult.Failed(RelayError.NodeNotActionable, "node reports no range info: not a slider or progress control");
        }
        float value;
        if (hasValue)
        {
            if (A11yRules.NumberArg(args, KeyValue) is not { } v)
            {
                return Malformed(ArgErrors.NotNumber(KeyValue, rawValue));
            }
            value = (float)v;
        }
        else
        {
            if (A11yRules.NumberArg(args, KeyPercent) is not { } percent)
            {
                return Malformed(ArgErrors.NotNumber(KeyPercent, rawPercent));
            }
            if (percent < 0.0 || percent > 100.0)
            {
                return Malformed($"{KeyPercent} must be within 0..100, got {OrgJson.JavaDouble(percent)}");
            }
            // Kotlin: Float + (Float - Float) * Double, i.e. the span in float, the rest in double.
            value = (float)(range.Min + (double)(range.Max - range.Min) * (percent / 100.0));
        }
        if (value < range.Min || value > range.Max)
        {
            return Malformed($"{KeyValue}={OrgJson.JavaFloat(value)} is outside this node's range " +
                $"[{OrgJson.JavaFloat(range.Min)}..{OrgJson.JavaFloat(range.Max)}] (read by ui.node)");
        }
        var identity = Fingerprint(picked.Node);
        var result = Act(picked, A11yConstants.ActionSetProgress, new A11yActionArgs(ProgressValue: value), env);
        if (result is BackendResult.Ok ok)
        {
            ok.Data["requestedValue"] = JavaFloat(value);
            ok.Data["range"] = RangeData(range);
            var (readBack, rounds) = await ReadBackUntilSettledAsync(picked.NodeId, value, identity, env, ct).ConfigureAwait(false);
            if (readBack is not { } rb)
            {
                ok.Data["readBackUnavailable"] = "node is gone from the tree, took another node's place, or reports no range";
            }
            else
            {
                ok.Data["readBack"] = JavaFloat(rb);
                if (rounds > 1)
                {
                    ok.Data["readBackRounds"] = rounds;
                }
            }
        }
        return result;
    }

    private async Task<(float? Value, int Rounds)> ReadBackUntilSettledAsync(int nodeId, float wanted, string identity, A11yWriteEnv env,
        CancellationToken ct)
    {
        var rounds = 0;
        float? last = null;
        var deadline = env.MonotonicMs() + Math.Min(ReadbackBudgetMs, Math.Max(env.BudgetMs, 0));
        while (rounds < MaxReadbackRounds)
        {
            last = ReadBackRange(nodeId, identity);
            if (last is null)
            {
                return (null, rounds + 1);
            }
            rounds++;
#pragma warning disable S1244
            if (last == wanted || env.MonotonicMs() >= deadline)
#pragma warning restore S1244
            {
                break;
            }
            await env.Delay(ReadbackPollMs, ct).ConfigureAwait(false);
        }
        return (last, rounds);
    }

    /// <summary>The node at this id now, if it is still the same node (identity) and reports a range.</summary>
    private float? ReadBackRange(int nodeId, string identity) =>
        FindByIdPreorder(Root(), nodeId) is { } node && Fingerprint(node) == identity ? node.Range?.Current : null;

    /// <summary>Upstream <c>imeAction</c>: the node reports IME_ENTER, or it is an input field (then unadvertised is allowed).</summary>
    public BackendResult ImeAction(JsonElement args, A11yWriteEnv env)
    {
        ArgumentNullException.ThrowIfNull(env);
        if (env.Sdk < 30)
        {
            return new BackendResult.Failed(RelayError.CapabilityUnavailableOnDevice, "ui.imeAction needs Android 11");
        }
        Target.One picked;
        switch (Resolve(args))
        {
            case Target.Bad bad:
                return new BackendResult.Failed(bad.Error, bad.Reason);
            case Target.Many many:
                return Ambiguous(many.Matches);
            case Target.One one:
                picked = one;
                break;
            default:
                throw new InvalidOperationException();
        }
        var selfReports = picked.Node.ActionIds.Contains(A11yConstants.ActionImeEnter);
        if (!selfReports && !IsEditableField(picked.Node))
        {
            return new BackendResult.Failed(RelayError.NodeNoImeAction, "node is not an editable field, so it has no IME action to trigger");
        }
        return Act(picked, A11yConstants.ActionImeEnter, null, env, allowUnadvertised: !selfReports);
    }

    private static bool IsEditableField(IA11yNode node)
    {
        var name = node.ClassName ?? "";
        return name.EndsWith("EditText", StringComparison.Ordinal) || name.EndsWith("AutoCompleteTextView", StringComparison.Ordinal) ||
            name == "android.widget.EditText" || name.Contains("TextInputEditText", StringComparison.Ordinal);
    }

    /// <summary>Kotlin <c>isBlank</c>: empty or only whitespace / space separators.</summary>
    internal static bool IsBlank(string s) => s.All(c => char.IsWhiteSpace(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.SpaceSeparator);
}
