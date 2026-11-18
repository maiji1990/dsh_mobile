using DshMobile.Core.Diagnostics;

namespace DshMobile.Runtime.Diagnostics;

/// <summary>Where a diagnostic measurement ends. Timings with different endpoints are never added together.</summary>
public enum ExperimentEndpoint
{
    /// <summary>Host call until the guest process exits.</summary>
    Exit,

    /// <summary>Host call until the first HTTP status from the diagnostic Web profile.</summary>
    Web,
}

/// <summary>
/// One fixed experiment variant. Everything is a code constant - the runner never accepts an argv, a command or a path
/// from the caller (docs/STARTUP_EXPERIMENTS.md).
/// </summary>
public sealed record StartupExperimentVariant(
    string Id,
    string Experiment,
    ExperimentEndpoint Endpoint,
    string Program,
    IReadOnlyList<string> Arguments,
    bool IsolatedWorkspace = false,
    string CacheMode = "unknown",
    bool IncludeRegisteredOverlays = false,
    int TimeoutSeconds = 60,
    string? Marker = null)
{
    public string Kind => Endpoint == ExperimentEndpoint.Exit ? ExperimentRecord.KindExit : ExperimentRecord.KindWeb;
}

/// <summary>
/// The E1-E4 catalogue from the root-cause plan (2026-11-18). Ids are stable: the JSONL report and the docs key on them.
/// </summary>
public static class StartupExperiments
{
    public const string SuiteE1E2 = "E1E2";
    public const string SuiteE2 = "E2";
    public const string SuiteE2Config = "E2-3";
    public const string SuiteE3 = "E3";
    public const string SuiteE4 = "E4";

    public const string Node = "/usr/local/bin/node";
    public const string True = "/bin/true";
    public const string Marker = "DSHM_NODE_READY";

    /// <summary>The fixed marker line of E1-2 / E1-3: a plain stdout line, no path, no environment, no token.</summary>
    public const string MarkerScript = "process.stdout.write(\"DSHM_NODE_READY\\n\")";

    private static readonly string[] DshEntry = ["--expose-internals", RuntimeConstants.GuestDshBin];

    public static IReadOnlyList<StartupExperimentVariant> All { get; } =
    [
        new("E1-0", "E1", ExperimentEndpoint.Exit, True, [], TimeoutSeconds: 30),
        new("E1-1", "E1", ExperimentEndpoint.Exit, Node, ["--version"], TimeoutSeconds: 30),
        new("E1-2", "E1", ExperimentEndpoint.Exit, Node, ["-e", MarkerScript], TimeoutSeconds: 30, Marker: Marker),
        new("E1-3", "E1", ExperimentEndpoint.Exit, Node,
            ["--import", RuntimeConstants.LinkShimGuestPath, "-e", MarkerScript], TimeoutSeconds: 30, Marker: Marker),
        new("E2-0", "E2", ExperimentEndpoint.Exit, Node, ["-e", "0"], TimeoutSeconds: 30),
        new("E2-1", "E2", ExperimentEndpoint.Exit, Node, [.. DshEntry, "--version"], TimeoutSeconds: 120),
        new("E2-2", "E2", ExperimentEndpoint.Exit, Node, [.. DshEntry, "--help"], TimeoutSeconds: 120),
        new("E2-3", "E2", ExperimentEndpoint.Exit, Node, [.. DshEntry, "--profile", RuntimeConstants.DshWebProfile, "--dump-config"], TimeoutSeconds: 180),
        new("E3-P", "E3", ExperimentEndpoint.Web, Node, [], TimeoutSeconds: 120),
        new("E3-0", "E3", ExperimentEndpoint.Web, Node, [], IsolatedWorkspace: true, TimeoutSeconds: 120),
        new("E3-1", "E3", ExperimentEndpoint.Web, Node, [], IsolatedWorkspace: true, IncludeRegisteredOverlays: true, TimeoutSeconds: 120),
        new("E4-A", "E4", ExperimentEndpoint.Web, Node, [], IsolatedWorkspace: true, CacheMode: "off", TimeoutSeconds: 120),
        new("E4-B", "E4", ExperimentEndpoint.Web, Node, [], IsolatedWorkspace: true, CacheMode: "cold", TimeoutSeconds: 120),
        new("E4-C", "E4", ExperimentEndpoint.Web, Node, [], IsolatedWorkspace: true, CacheMode: "warm", TimeoutSeconds: 120),
    ];

    public static bool TryGet(string id, out StartupExperimentVariant variant)
    {
        foreach (var v in All)
        {
            if (string.Equals(v.Id, id, StringComparison.Ordinal))
            {
                variant = v;
                return true;
            }
        }
        variant = null!;
        return false;
    }

    public static IReadOnlyList<StartupExperimentVariant> For(string experiment) =>
        All.Where(v => string.Equals(v.Experiment, experiment, StringComparison.Ordinal)).ToList();
}
