using DshMobile.Runtime.Supervision;

namespace DshMobile.Application.Shadow;

/// <summary>
/// PR-03 read-only supervisor shadow: what the C# side observes on its own (<c>/proc</c> trees found by the legacy
/// markers, its own HTTP probe) versus what the legacy sandbox-manager reports. Nothing is started or killed.
/// </summary>
public static class SupervisorObserveShadowCheck
{
    public const string Slice = "runtime.supervisor-observe";

    public static ShadowReport Compare(string legacySandboxState, string legacyDshState, bool legacyHealthOk,
        RuntimeProcessSnapshot? processes, DshHealthSnapshot health, long csharpMs, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(health);
        if (processes is null)
        {
            return new ShadowReport(Slice, at, ShadowVerdict.Inconclusive, legacyHealthOk, null, health.WebUiReady, null,
                "/proc not readable on this host", csharpMs);
        }
        var sandboxLegacy = legacySandboxState == "RUNNING";
        var sandboxCs = processes.SandboxRootPid is not null;
        var dshLegacy = legacyDshState is "RUNNING" or "READY" or "STARTING";
        var dshCs = processes.DshRootPid is not null;
        var checks = new (string Name, bool Legacy, bool CSharp, string Extra)[]
        {
            ("sandbox", sandboxLegacy, sandboxCs, sandboxCs ? $"pid {processes.SandboxRootPid}, {processes.SandboxTreeSize} procs" : ""),
            ("dsh", dshLegacy, dshCs, dshCs ? $"pid {processes.DshRootPid}, {processes.DshTreeSize} procs" : ""),
            ("webUi", legacyHealthOk, health.WebUiReady, health.HttpStatus is { } s ? $"HTTP {s}" : health.Error ?? ""),
        };
        var diverged = checks.Any(c => c.Legacy != c.CSharp);
        var detail = string.Join("; ", checks.Select(c =>
            $"{c.Name}: legacy={(c.Legacy ? "up" : "down")} csharp={(c.CSharp ? "up" : "down")}{(c.Extra.Length > 0 ? " (" + c.Extra + ")" : "")}"));
        return new ShadowReport(Slice, at, diverged ? ShadowVerdict.Diverge : ShadowVerdict.Agree,
            legacyHealthOk, $"{legacySandboxState}/{legacyDshState}", health.WebUiReady, null, detail, csharpMs);
    }
}
