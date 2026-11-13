// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/interlock/InterlockPrompt.kt
// Registered in docs/UPSTREAMS.md. Locked by tests/TestVectors/relay-control.json.

namespace DshMobile.Runtime.Automation.Relay.Interlock;

/// <summary>Upstream <c>PromptCodes</c>: the single source of ask failure codes and their exit codes.</summary>
public static class PromptCodes
{
    public const string Timeout = "E_ASK_TIMEOUT";
    public const string Busy = "E_ASK_BUSY";
    public const string NoSurface = "E_ASK_NO_SURFACE";
    public const string Internal = "E_INTERNAL";

    public const string NoSurfaceBlind = NoSurface + "/background mode hides floating cards";
    public const string NoSurfaceNotGranted = NoSurface + "/overlay permission is not granted";
    public const string NoSurfaceHidden = NoSurface + "/hidden-by-platform";
    public const string BusyAnotherQuestion = Busy + "/another question is already waiting on screen";
    public const string BusyApprovalCard = Busy + "/an approval card is on screen";
    public const string BusyChannelStopped = Busy + "/the channel stopped while the card was up";
    public const string InternalNoChannel = Internal + "/this host has no ask channel wired";
    public const string InternalDuplicate = Internal + "/an ask with this id is already in flight";
    public const string TimeoutNoAnswer = Timeout + "/no answer within the window";
    public const string TimeoutCovered = Timeout + "/the card lost window focus while waiting (likely hidden)";
    public const string TimeoutExpired = Timeout + "/the request sat in the inbox past its own window";

    /// <summary>Prefix → exit code; an unknown prefix is a host-internal fault.</summary>
    public static int ExitCodeFor(string cause) => CodeOf(cause) switch
    {
        Timeout => RelayExitCode.Timeout,
        NoSurface => RelayExitCode.NeedsUserAction,
        Busy => RelayExitCode.RetryLater,
        _ => RelayExitCode.Internal,
    };

    /// <summary>The prefix code of a cause (<c>substringBefore('/')</c>); unknown prefixes collapse to <see cref="Internal"/>.</summary>
    public static string CodeOf(string cause)
    {
        ArgumentNullException.ThrowIfNull(cause);
        var slash = cause.IndexOf('/', StringComparison.Ordinal);
        var head = slash < 0 ? cause : cause[..slash];
        return head is Timeout or Busy or NoSurface or Internal ? head : Internal;
    }

    public static bool Retryable(string cause) => CodeOf(cause) != Internal;
}

/// <summary>Upstream <c>PromptOutcome</c>: the five ends of an ask.</summary>
public abstract record PromptOutcome
{
    public sealed record Chosen(int Index, string Label) : PromptOutcome;

    public sealed record RejectedAll : PromptOutcome;

    public sealed record Reasked : PromptOutcome;

    public sealed record TimedOut(bool Covered) : PromptOutcome;

    public sealed record Unavailable(string Cause) : PromptOutcome;
}

/// <summary>Upstream <c>InterlockPrompt</c>: put a question in front of the user and wait; never touches execution, never answers for the user.</summary>
public interface IInterlockPrompt
{
    Task<PromptOutcome> AskAsync(string question, IReadOnlyList<string> options, long timeoutMs, CancellationToken cancellationToken);
}

/// <summary>Upstream <c>RelayAskSurface</c>: the prompt plus the two management actions the core needs.</summary>
public interface IRelayAskSurface : IInterlockPrompt
{
    /// <summary>Upper bound for getting the card on screen (shared with the guest CLI's wait margin).</summary>
    public const long OnScreenBudgetMs = 4000;

    bool IsPresented();

    /// <summary>Remove the card and settle the wait now. Idempotent, any thread.</summary>
    void Withdraw(string cause = PromptCodes.BusyChannelStopped);

    /// <summary>The default surface: never asks, answers "no ask channel wired".</summary>
    public static IRelayAskSurface Unavailable { get; } = new NoSurfaceImpl();

    private sealed class NoSurfaceImpl : IRelayAskSurface
    {
        public bool IsPresented() => false;

        public void Withdraw(string cause = PromptCodes.BusyChannelStopped)
        {
        }

        public Task<PromptOutcome> AskAsync(string question, IReadOnlyList<string> options, long timeoutMs, CancellationToken cancellationToken) =>
            Task.FromResult<PromptOutcome>(new PromptOutcome.Unavailable(PromptCodes.InternalNoChannel));
    }
}
