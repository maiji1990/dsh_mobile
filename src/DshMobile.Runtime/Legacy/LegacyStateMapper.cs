namespace DshMobile.Runtime.Legacy;

/// <summary>
/// Maps DSHBox sandbox-manager enum names (SandboxState / DshState, v1.5.1) to the C# lifecycle. A contract test scans
/// the upstream SandboxState.kt so a new upstream value cannot slip through unmapped.
/// </summary>
public static class LegacyStateMapper
{
    public static IReadOnlyList<string> SandboxStates { get; } = ["UNINITIALIZED", "INITIALIZING", "STARTING", "RUNNING", "ERROR", "RECOVERING", "STOPPED"];

    public static IReadOnlyList<string> DshStates { get; } = ["UNINITIALIZED", "STARTING", "RUNNING", "READY", "ERROR", "STOPPED"];

    /// <summary>Null for a value this build does not know (callers treat that as a hard failure, never as success).</summary>
    public static RuntimeState? MapSandbox(string sandboxState, bool runtimeInstalled) => sandboxState switch
    {
        "RUNNING" => RuntimeState.Running,
        "STARTING" or "INITIALIZING" => RuntimeState.Starting,
        "RECOVERING" => RuntimeState.Recovering,
        "ERROR" => RuntimeState.Failed,
        "UNINITIALIZED" or "STOPPED" => runtimeInstalled ? RuntimeState.Ready : RuntimeState.NotInstalled,
        _ => null,
    };

    public static HarnessState? MapDsh(string dshState) => dshState switch
    {
        "UNINITIALIZED" or "STOPPED" => HarnessState.Stopped,
        "STARTING" => HarnessState.Starting,
        "RUNNING" => HarnessState.Running,
        "READY" => HarnessState.Ready,
        "ERROR" => HarnessState.Failed,
        _ => null,
    };
}
