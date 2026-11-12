// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/runtime/RelayCoordinator.kt (handle: the steps before the surface policy and
//     the gate — capability index, selfTargetArg / childPath, isHostBringToFront, entry flood limit, args pre-check;
//     rejected())
//   src/main/java/interlock/relay/core/interlock/InterlockGate.kt  (RateLimiter, WINDOW_MS, EXECUTION_LIMITS, ENTRY_LIMITS)
// Registered in docs/UPSTREAMS.md. Locked by tests/TestVectors/relay-admission.json.

using System.Text.Json;

namespace DshMobile.Runtime.Automation.Relay;

/// <summary>Sliding one-minute window per capability (upstream <c>RateLimiter</c>).</summary>
public sealed class RelayRateLimiter
{
    public const long WindowMs = 60_000;

    private readonly Func<long> _now;
    private readonly Func<string, int> _limitOf;
    private readonly Dictionary<string, Queue<long>> _events = new(StringComparer.Ordinal);

    public RelayRateLimiter(Func<long> nowMs, Func<string, int> limitOf)
    {
        _now = nowMs;
        _limitOf = limitOf;
    }

    /// <summary>Per-minute execution quota by category: observe 30, control 60, everything else 20.</summary>
    public static int ExecutionLimit(string wire) => RelaySemantics.CategoryOf(wire) switch
    {
        CapabilityCategory.Observe => 30,
        CapabilityCategory.Control => 60,
        _ => 20,
    };

    /// <summary>Entry flood limit: twice the execution quota, applied before any argument is looked at.</summary>
    public static int EntryLimit(string wire) => 2 * ExecutionLimit(wire);

    public bool Accepted(string wire)
    {
        var windowStart = _now() - WindowMs;
        lock (_events)
        {
            if (!_events.TryGetValue(wire, out var queue))
            {
                _events[wire] = queue = new Queue<long>();
            }
            while (queue.Count > 0 && queue.Peek() < windowStart)
            {
                queue.Dequeue();
            }
            if (queue.Count >= _limitOf(wire))
            {
                return false;
            }
            queue.Enqueue(_now());
            return true;
        }
    }
}

/// <summary>
/// The steps Relay takes on a well-formed request before the surface policy and the approval gate, in upstream order:
/// the capability must be registered (DSH Mobile registers only its allowlist); no argument may name this app itself
/// (except the one "bring the host to front" shape); the entry flood limit; the common argument pre-check. A request
/// rejected here never reaches an approval prompt or a backend.
/// </summary>
public sealed class RelayAdmission
{
    private readonly HashSet<string> _registered;
    private readonly string _selfPackage;
    private readonly RelayRateLimiter _entryRate;

    public RelayAdmission(IEnumerable<string> registeredCapabilities, string selfPackage, RelayRateLimiter entryRate)
    {
        _registered = new HashSet<string>(registeredCapabilities, StringComparer.Ordinal);
        _selfPackage = selfPackage;
        _entryRate = entryRate;
    }

    /// <summary>The rejection response, or null when the request goes on to the surface policy and the gate.</summary>
    public RelayResponse? Check(RelayRequest request, long elapsedMs = 0)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!RelayProtocol.IsCapability(request.Capability) || !_registered.Contains(request.Capability))
        {
            return Rejected(request.Id, RelayError.CapabilityNotImplemented, request.Capability, null, elapsedMs);
        }
        var wire = request.Capability;
        var selfArg = SelfTargetArg(request.Args, _selfPackage);
        if (selfArg is not null && !IsHostBringToFront(request, _selfPackage))
        {
            return Rejected(request.Id, RelayError.GateHostDenied,
                $"arg \"{selfArg}\" names this app itself, and the assistant cannot read or act on the " +
                "host app: drop that argument or name another app", wire, elapsedMs);
        }
        if (!_entryRate.Accepted(wire))
        {
            return Rejected(request.Id, RelayError.RateLimited, RelayError.RateLimited.Code + "/entry_flood", wire, elapsedMs);
        }
        if (CapabilityArgsSpec.Validate(wire, request.Args) is { } reason)
        {
            return Rejected(request.Id, RelayError.TransportMalformed, reason, wire, elapsedMs);
        }
        return null;
    }

    /// <summary>Upstream <c>rejected()</c>: no surface yet, retry policy only when the capability is known.</summary>
    public static RelayResponse Rejected(string id, RelayError error, string detail, string? capability, long elapsedMs) =>
        new(id, false, null, [], null, null, detail, error, elapsedMs,
            RetryPolicy: capability is null ? null : RelaySemantics.RetryPolicyOf(capability),
            EffectState: RelaySemantics.EffectStateFor(false, error),
            ConsentKind: RelaySemantics.ConsentKindFor(error));

    /// <summary>
    /// Upstream <c>selfTargetArg</c>: the key path of the first string value equal to this app's package, scanning the
    /// whole argument tree (not key names: a new key that can carry a package must not slip past).
    /// </summary>
    public static string? SelfTargetArg(JsonElement value, string selfPackage, string path = "")
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString() == selfPackage ? path : null;
            case JsonValueKind.Object:
                foreach (var p in value.EnumerateObject())
                {
                    if (SelfTargetArg(p.Value, selfPackage, ChildPath(path, p.Name)) is { } hit)
                    {
                        return hit;
                    }
                }
                return null;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (SelfTargetArg(item, selfPackage, $"{path}[{i}]") is { } hit)
                    {
                        return hit;
                    }
                    i++;
                }
                return null;
            default:
                return null;
        }
    }

    private static string ChildPath(string parent, string key)
    {
        var name = key.Length == 0 ? "<no-name>" : key;
        return parent.Length == 0 ? name : parent + "." + name;
    }

    /// <summary>Upstream <c>isHostBringToFront</c>: exactly <c>app.launch {"package": self}</c>.</summary>
    public static bool IsHostBringToFront(RelayRequest request, string selfPackage) =>
        request.Capability == "app.launch"
        && request.Args.ValueKind == JsonValueKind.Object
        && request.Args.EnumerateObject().Count() == 1
        && OrgJson.OptString(request.Args, "package") == selfPackage;
}
