using DshMobile.Runtime;

namespace DshMobile.Application.Ui;

/// <summary>What the home screen's status card says (ADR-0021). Each value maps to one title / colour on Android.</summary>
public enum HomePhase
{
    /// <summary>No usable runtime layers: the only way forward is importing a runtime bundle.</summary>
    NotInstalled,
    /// <summary>Installed; sandbox and DSH not running.</summary>
    Stopped,
    Starting,
    /// <summary>Sandbox up, DSH process up, waiting for agent readiness.</summary>
    WaitingForDsh,
    /// <summary>DSH answered; native chat can be opened.</summary>
    Ready,
    Stopping,
    /// <summary>The runtime journal says the last operation failed (<see cref="HomeStatus.ErrorCode"/>).</summary>
    Failed,
    /// <summary>An operation other than start / stop runs (<see cref="HomeStatus.Operation"/>): import, rollback, …</summary>
    Busy,
}

/// <summary>A button on the status card. <see cref="None"/> hides it.</summary>
public enum HomeAction
{
    None,
    /// <summary>ADR-0029: 从 GitHub 签名通道在线下载并安装缺失的运行环境（Runtime / DSH）。</summary>
    OnlineInstall,
    ImportRuntime,
    /// <summary>The runtime body is installed but the DSH layer is not (ADR-0027: it is imported separately).</summary>
    ImportDsh,
    Start,
    OpenChat,
    Stop,
}

/// <summary>The end of one user-started operation, kept for the UI (never parsed back out of the log).</summary>
public sealed record OperationOutcome(string Operation, bool Ok, string Code, string? Message, DateTimeOffset At);

/// <summary>
/// Pure mapping from the runtime journal + the operation the app is running to the status card. The journal stays the
/// source of truth (plan section 5); this only chooses words and buttons, so a state the card cannot explain shows its
/// error code instead of looking fine (AGENTS.md rule 5).
/// </summary>
public sealed record HomeStatus(
    HomePhase Phase,
    HomeAction Primary,
    HomeAction Secondary,
    string? DshVersion,
    string? ErrorCode,
    string? ErrorMessage,
    string? Operation,
    OperationOutcome? FailedOperation)
{
    /// <summary>Operation names <c>BridgeHost</c> runs that the card names itself instead of as "busy".</summary>
    public const string StartOperation = "start";
    public const string StopOperation = "stop";
    public const string ImportOperation = "runtime.import";
    public const string ImportDshOperation = "runtime.import.dsh";
    public const string UpdateCheckOperation = "update.check";
    public const string UpdateInstallOperation = "update.install";

    /// <param name="snapshot">The runtime journal.</param>
    /// <param name="operation">The operation running now, or null.</param>
    /// <param name="last">The last finished operation, or null.</param>
    public static HomeStatus From(RuntimeSnapshot snapshot, string? operation, OperationOutcome? last)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // A failed operation that the journal does not already show as Failed (e.g. a refused start) is reported once,
        // under the card; while something runs it is stale.
        var failedOp = operation is null && last is { Ok: false } && snapshot.State != RuntimeState.Failed ? last : null;
        HomeStatus Make(HomePhase phase, HomeAction primary = HomeAction.None, HomeAction secondary = HomeAction.None) =>
            new(phase, primary, secondary, snapshot.DshVersion, snapshot.ErrorCode, snapshot.ErrorMessage, operation, failedOp);

        switch (operation)
        {
            case StartOperation:
                return Make(HomePhase.Starting);
            case StopOperation:
                return Make(HomePhase.Stopping);
            case not null:
                return Make(HomePhase.Busy);
        }

        return snapshot.State switch
        {
            // ADR-0029: 首次未安装时主按钮改为在线下载运行环境，次级按钮保留本地导入作为离线兜底。
            RuntimeState.NotInstalled => Make(HomePhase.NotInstalled, HomeAction.OnlineInstall, HomeAction.ImportRuntime),
            RuntimeState.Staging => Make(HomePhase.Busy),
            // ADR-0027 / ADR-0029: 主体已装但缺少 DSH 层时主按钮改为在线下载 DSH，次级按钮保留本地导入。
            RuntimeState.Ready => snapshot.DshVersion is null
                ? Make(HomePhase.Stopped, HomeAction.OnlineInstall, HomeAction.ImportDsh)
                : Make(HomePhase.Stopped, HomeAction.Start),
            RuntimeState.Starting or RuntimeState.Recovering => Make(HomePhase.Starting, HomeAction.None, HomeAction.Stop),
            RuntimeState.Running => snapshot.Harness switch
            {
                HarnessState.Ready => Make(HomePhase.Ready, HomeAction.OpenChat, HomeAction.Stop),
                HarnessState.Starting or HarnessState.Running => Make(HomePhase.WaitingForDsh, HomeAction.None, HomeAction.Stop),
                // Sandbox kept but DSH is gone: say it failed, offer the stop that cleans up and a fresh start.
                _ => Make(HomePhase.Failed, HomeAction.Start, HomeAction.Stop),
            },
            RuntimeState.Stopping => Make(HomePhase.Stopping),
            // An import that failed may have left no runtime (retry that import), otherwise retry the start.
            RuntimeState.Failed => Make(HomePhase.Failed, last?.Operation switch
            {
                ImportOperation => HomeAction.ImportRuntime,
                ImportDshOperation => HomeAction.ImportDsh,
                UpdateInstallOperation => HomeAction.OnlineInstall,
                _ => HomeAction.Start,
            }),
            _ => Make(HomePhase.Failed),
        };
    }
}
