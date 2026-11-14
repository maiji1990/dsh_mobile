// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/surface/BackendDispatcher.kt
// Registered in docs/UPSTREAMS.md. Picks a backend in the registry's preference order; the surface was decided
// before (SurfacePolicy) and backends never change it.

using System.Globalization;
using DshMobile.Runtime.Automation.Relay.Logging;

namespace DshMobile.Runtime.Automation.Relay.Exec;

public sealed class BackendDispatcher
{
    public const string BackendNone = "none";
    public const string ResultOk = "ok";
    public const string ResultFailed = "failed";
    public const string ResultUnavailable = "unavailable";
    public const string ResultNotImplemented = "not_implemented";

    private static readonly SurfaceKind[] AllSurfaces = [SurfaceKind.Foreground, SurfaceKind.BackendTrusted, SurfaceKind.ObserveOnly];

    private readonly IReadOnlyDictionary<RelayBackendId, IRelayBackend> _backends;
    private readonly RunLog? _runLog;
    private readonly Func<long> _now;

    public BackendDispatcher(IReadOnlyDictionary<RelayBackendId, IRelayBackend> backends, RunLog? runLog = null, Func<long>? now = null)
    {
        _backends = backends ?? throw new ArgumentNullException(nameof(backends));
        _runLog = runLog;
        _now = now ?? RelayClock.MonotonicNow;
    }

    /// <summary>The result together with the surface it finally ran on (for the response and the audit record).</summary>
    public sealed record Dispatched(BackendResult Result, SurfaceDecision Decision);

    public IRelayBackend? Pick(string wire, SurfaceKind surface)
    {
        foreach (var id in RelaySurfaces.BackendsOf(wire))
        {
            if (_backends.TryGetValue(id, out var b) && b.Available() && b.Supports(wire, surface))
            {
                return b;
            }
        }
        return null;
    }

    /// <summary>Same rule as <see cref="Pick"/>, but the backend whose turn it is first heals its own connection (calls only).</summary>
    private (RelayBackendId Id, IRelayBackend Backend)? PickAndHeal(string wire, SurfaceKind surface)
    {
        foreach (var id in RelaySurfaces.BackendsOf(wire))
        {
            if (!_backends.TryGetValue(id, out var backend))
            {
                continue;
            }
            if (!backend.Available())
            {
                backend.Reconnect();
            }
            if (backend.Available() && backend.Supports(wire, surface))
            {
                return (id, backend);
            }
        }
        return null;
    }

    /// <summary>Whether a backend implements this capability on this surface: supports() only, never available().</summary>
    public bool CanServe(string wire, SurfaceKind surface) =>
        RelaySurfaces.BackendsOf(wire).Any(id => _backends.TryGetValue(id, out var b) && b.Supports(wire, surface));

    /// <summary>Implemented on any of its allowed surfaces (mode-independent capabilities: the foreground).</summary>
    public bool IsImplemented(string wire)
    {
        var surfaces = RelaySurfaces.SurfacesOf(wire);
        return (surfaces.Count == 0 ? [SurfaceKind.Foreground] : surfaces).Any(s => CanServe(wire, s));
    }

    public async Task<Dispatched> DispatchAsync(string wire, SurfaceDecision decision, BackendCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(call);
        var startedAt = _now();
        if (PickAndHeal(wire, decision.Surface) is not { } picked)
        {
            var allowed = RelaySurfaces.SurfacesOf(wire);
            var implementedSomewhere = CanServe(wire, decision.Surface) || allowed.Any(s => CanServe(wire, s));
            BackendResult refused;
            if (implementedSomewhere)
            {
                var servedOn = AllSurfaces.Where(s => CanServe(wire, s)).Select(s => s.Wire())
                    .Concat(allowed.Select(s => s.Wire()))
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal);
                refused = new BackendResult.Failed(
                    RelayError.BackendUnavailable,
                    $"no backend reachable for the {decision.Surface.Wire()} surface; " +
                    $"a backend is written for: [{string.Join(", ", servedOn)}] — read backend.shizuku and " +
                    "the accessibility state in the capability list");
            }
            else
            {
                refused = new BackendResult.Failed(RelayError.CapabilityNotImplemented, wire);
            }
            LogCall(wire, BackendNone, implementedSomewhere ? ResultUnavailable : ResultNotImplemented, startedAt);
            return new Dispatched(refused, decision);
        }
        var name = WireOf(picked.Id);
        BackendResult outcome;
        try
        {
            outcome = await picked.Backend.ExecuteAsync(call with { Surface = decision.Surface }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The dispatcher is the only place that knows where the call should have gone: log, then rethrow.
            LogCall(wire, name, ResultFailed, startedAt);
            throw;
        }
        LogCall(wire, name, outcome is BackendResult.Ok ? ResultOk : ResultFailed, startedAt);
        return new Dispatched(outcome, decision);
    }

    public static string WireOf(RelayBackendId id) => id switch
    {
        RelayBackendId.Direct => "direct",
        RelayBackendId.A11y => "a11y",
        RelayBackendId.Shizuku => "shizuku",
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    private void LogCall(string cap, string backend, string result, long startedAt) =>
        _runLog?.Info(LogSubsystem.Backend, LogEvent.BackendCall, ("cap", cap), ("backend", backend), ("result", result),
            ("ms", Math.Max(0L, _now() - startedAt).ToString(CultureInfo.InvariantCulture)));
}
