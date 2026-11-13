// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/exec/a11y/NodeActions.kt (waitFor, Outcome, evaluate; WAIT_POLL_MS,
//     DEFAULT_WAIT_MS, MAX_WAIT_MS)
// Registered in docs/UPSTREAMS.md. ui.waitFor is a read (not under the host-window rule); it shares target resolution
// with ui.node (A11yRead.cs). Locked by tests/TestVectors/relay-a11y-waitfor.json and the on-device shadow
// automation.relay-a11y-waitfor.

using System.Text.Json;
using System.Text.Json.Nodes;
using DshMobile.Runtime.Automation.Relay.Exec;

namespace DshMobile.Runtime.Automation.Relay.A11y;

public sealed partial class NodeReader
{
    public const long WaitPollMs = 150;
    public const long DefaultWaitMs = 3_000;
    public const long MaxWaitMs = 15_000;

    private abstract record Outcome
    {
        public sealed record Satisfied(JsonObject Node) : Outcome;

        /// <summary>The call must not keep waiting: this is its answer.</summary>
        public sealed record Rejected(RelayError Error, string Reason) : Outcome;

        public sealed record Unsatisfied(string Reason) : Outcome;
    }

    /// <summary>Upstream <c>waitFor</c> on the system monotonic clock.</summary>
    public Task<BackendResult> WaitForAsync(JsonElement args, long budgetMs, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(time);
        var origin = time.GetTimestamp();
        return WaitForAsync(args, budgetMs, () => (long)time.GetElapsedTime(origin).TotalMilliseconds,
            (ms, token) => Task.Delay(TimeSpan.FromMilliseconds(ms), time, token), ct);
    }

    /// <summary>
    /// Upstream <c>waitFor</c>: poll every <see cref="WaitPollMs"/> until the condition holds, the spec is rejected, or
    /// the timeout — capped to the channel budget the call has left — runs out. <paramref name="monotonicMs"/> is a
    /// monotonic clock in milliseconds (never wall time).
    /// </summary>
    public async Task<BackendResult> WaitForAsync(JsonElement args, long budgetMs, Func<long> monotonicMs,
        Func<long, CancellationToken, Task> delay, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(monotonicMs);
        ArgumentNullException.ThrowIfNull(delay);
        long requested;
        if (args.TryGetProperty(A11yRules.KeyTimeoutMs, out var rawTimeout))
        {
            if (A11yRules.NumberArg(args, A11yRules.KeyTimeoutMs) is not { } d)
            {
                return Malformed(ArgErrors.NotNumber(A11yRules.KeyTimeoutMs, rawTimeout));
            }
            requested = OrgJson.JavaDoubleToLong(d);
        }
        else
        {
            requested = DefaultWaitMs;
        }
        if (requested < 0 || requested > MaxWaitMs)
        {
            return Malformed($"{A11yRules.KeyTimeoutMs} must be within 0..{MaxWaitMs}");
        }
        var timeoutMs = Math.Min(requested, Math.Max(budgetMs, 0));
        var startedAt = monotonicMs();
        var deadline = startedAt + timeoutMs;
        var polls = 0;
        var lastReason = "not evaluated";
        while (true)
        {
            polls++;
            switch (Evaluate(args))
            {
                case Outcome.Satisfied s:
                    return new BackendResult.Ok(new JsonObject
                    {
                        ["satisfied"] = true,
                        ["polls"] = polls,
                        ["waitedMs"] = monotonicMs() - startedAt,
                        ["requestedMs"] = requested,
                        ["cappedToBudget"] = timeoutMs < requested,
                        ["node"] = s.Node,
                    });
                case Outcome.Rejected r:
                    return new BackendResult.Failed(r.Error, r.Reason);
                case Outcome.Unsatisfied u:
                    lastReason = u.Reason;
                    break;
            }
            if (monotonicMs() >= deadline)
            {
                // A failure carries no data, so the reason says how much time the call was really given.
                var capped = timeoutMs < requested ? " (capped to the remaining channel budget)" : "";
                return new BackendResult.Failed(RelayError.WaitTimeout,
                    $"condition still false after {timeoutMs}ms of the requested {requested}ms{capped} and {polls} polls: {lastReason}");
            }
            await delay(WaitPollMs, ct).ConfigureAwait(false);
        }
    }

    private Outcome Evaluate(JsonElement args)
    {
        var absent = Last(args, A11yRules.KeyAbsent) is { ValueKind: JsonValueKind.True };
        var target = Resolve(args);
        // "Not in the tree" only counts as gone when the tree was read and the target was looked up by its own
        // features: an unreadable tree or a vanished anchor must never satisfy an absence assertion.
        var absenceJudged = target is Target.Bad { Error: var e } && e == RelayError.NodeNotFound &&
            !args.TryGetProperty(A11yRules.KeyRelative, out _) && Root() is not null;
        if (target is Target.Bad && absent && absenceJudged)
        {
            return new Outcome.Satisfied(new JsonObject { ["gone"] = true });
        }
        if (target is Target.Bad notFound && absent && notFound.Error == RelayError.NodeNotFound)
        {
            return new Outcome.Rejected(notFound.Error, $"{notFound.Reason} (absence cannot be judged from here)");
        }
        IA11yNode node;
        switch (target)
        {
            case Target.Bad bad:
                return bad.Error == RelayError.NodeNotFound
                    ? new Outcome.Unsatisfied(bad.Reason)
                    : new Outcome.Rejected(bad.Error, bad.Reason);
            case Target.Many:
                return new Outcome.Rejected(RelayError.NodeAmbiguous, "selector is ambiguous");
            case Target.One one:
                node = one.Node;
                break;
            default:
                throw new InvalidOperationException();
        }
        if (absent)
        {
            return node.IsVisibleToUser ? new Outcome.Unsatisfied("node still visible") : new Outcome.Satisfied(Describe(node));
        }
        if (Last(args, A11yRules.KeyChecked) is { } want)
        {
            // Kotlin `node.isChecked != want`: only a JSON boolean can ever equal it; anything else waits forever.
            var equal = want.ValueKind is JsonValueKind.True or JsonValueKind.False && (want.ValueKind == JsonValueKind.True) == node.IsChecked;
            if (!equal)
            {
                return new Outcome.Unsatisfied($"checked is {(node.IsChecked ? "true" : "false")}, wanted {OrgJson.JavaString(want)}");
            }
        }
        if (args.TryGetProperty(A11yRules.KeyText, out _))
        {
            var wantText = OrgJson.OptString(args, A11yRules.KeyText);
            var actual = node.Text;
            if (actual != wantText)
            {
                // Only lengths: this is asked every 150 ms, and echoing the text would stream a field being typed into.
                return new Outcome.Unsatisfied($"text differs (length {actual?.Length ?? 0}, wanted {wantText.Length})");
            }
        }
        return new Outcome.Satisfied(Describe(node));
    }

    private static BackendResult.Failed Malformed(string reason) => new(RelayError.TransportMalformed, reason);
}
