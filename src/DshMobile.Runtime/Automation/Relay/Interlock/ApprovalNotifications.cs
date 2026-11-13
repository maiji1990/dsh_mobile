// SPI shape ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/spi/RelaySurfaces.kt (RelayApprovalNotificationSurface)
//   src/main/java/interlock/relay/core/runtime/RelayContainer.kt (rerouteApprovalPresentation / revokeApprovalPresenters)
// Content rules mirror this repository's native/native-bridge RelayApproval.kt (TASK-010, ADR-0004) so the C# surface
// posts the same card. Registered in docs/UPSTREAMS.md.
//
// Security: the notification only ever offers the three answers; the receiver accepts nothing else and always names
// the request id it answers (a stale card cannot answer a newer question). Notifications blocked → CanShow false →
// the queue fails the call closed (UNABLE_TO_SHOW); nothing here approves on its own.

namespace DshMobile.Runtime.Automation.Relay.Interlock;

/// <summary>Upstream <c>RelayApprovalNotificationSurface</c>.</summary>
public interface IApprovalNotificationSurface
{
    /// <summary>Whether the system lets this app post on the approval channel.</summary>
    bool CanShow();

    /// <summary>Post (or replace) the card for this pending request; <paramref name="waitingCount"/> questions queue behind it.</summary>
    void Show(InterlockQueue.Request request, int waitingCount);

    /// <summary>Remove the card whichever question it belongs to.</summary>
    void Cancel();

    /// <summary>Remove only the card of <paramref name="requestId"/>.</summary>
    void CancelFor(int requestId);

    /// <summary>Last presentation state; at best "posted, visibility unverified".</summary>
    string LastPostState();
}

/// <summary>Honest delivery states (RelayApproval.kt <c>state</c>).</summary>
public static class ApprovalPostStates
{
    public const string None = "NONE";
    public const string NoManager = "NO_MANAGER";
    public const string Blocked = "BLOCKED";
    public const string PostedUnverified = "POSTED_UNVERIFIED";
    public const string PostFailed = "POST_FAILED";
    public const string Cancelled = "CANCELLED";
}

/// <summary>What the approval notification says and which answers its buttons carry.</summary>
public static class ApprovalNotificationContent
{
    public const string Channel = "dshmobile_relay_approval";
    public const string Tag = "relay-approval";
    public const string ExtraRequestId = "requestId";
    public const string ExtraChoice = "choice";
    public const string ExtraToken = "token";
    public const string ExtraTitle = "title";
    public const string ExtraText = "text";
    public const string ExtraReceiver = "receiver";

    /// <summary>The in-app page an "allow" button opens; the final allow is pressed there (see <see cref="ConfirmTarget"/>).</summary>
    public const string ConfirmActivity = "dshmobile.app.RelayApprovalConfirmActivity";

    /// <summary>The two receivers an answer may go to, by the confirmation page's whitelisted <see cref="ExtraReceiver"/> value.</summary>
    public const string ReceiverLegacy = "legacy";
    public const string ReceiverCSharp = "csharp";

    /// <summary>The three buttons, in display order.</summary>
    public static readonly IReadOnlyList<InterlockChoice> Actions = [InterlockChoice.AllowOnce, InterlockChoice.AllowSession, InterlockChoice.Deny];

    public static string Text(string capability, int waitingCount) =>
        "DSH 请求执行 " + capability + (waitingCount > 0 ? "（另有 " + waitingCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 个请求排队）" : "");

    /// <summary>
    /// The expanded card and confirmation-page text: the headline plus what is being approved (upstream's display-
    /// truncated target and parameters), so e.g. a sys.shell card shows its command and not only its capability.
    /// </summary>
    public static string Detail(string capability, int waitingCount, string? targetLabel, string? paramDetail)
    {
        var lines = new List<string> { Text(capability, waitingCount) };
        if (!string.IsNullOrWhiteSpace(targetLabel))
        {
            lines.Add("目标：" + targetLabel);
        }
        if (!string.IsNullOrWhiteSpace(paramDetail))
        {
            lines.Add("参数：" + paramDetail);
        }
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Where the confirmation page delivers an answer: (receiver class, action) for a whitelisted receiver name, else null.
    /// The name comes from an intent extra, so anything outside the two known receivers is refused.
    /// </summary>
    public static (string ClassName, string Action)? ConfirmTarget(string? receiver) => receiver switch
    {
        ReceiverLegacy => ("dshmobile.nativebridge.RelayApprovalReceiver", "dshmobile.nativebridge.RELAY_APPROVAL"),
        ReceiverCSharp => ("dshmobile.app.RelayApprovalReceiver", "dshmobile.app.RELAY_APPROVAL"),
        _ => null,
    };

    /// <summary>
    /// Whether a button needs the in-app confirmation page: every answer that approves. A deny cannot help a caller, so it
    /// stays a direct broadcast.
    /// </summary>
    public static bool NeedsConfirmation(InterlockChoice choice) => choice is InterlockChoice.AllowOnce or InterlockChoice.AllowSession;

    /// <summary>A distinct PendingIntent request code per (id, choice) so the three buttons do not collapse into one.</summary>
    public static int RequestCode(int requestId, InterlockChoice choice) => unchecked((requestId * 8) + (int)choice);

    /// <summary>The receiver's filter: a non-negative id and exactly one of the three answers, else nothing.</summary>
    public static InterlockChoice? ParseReceived(int requestId, string? choiceWire)
    {
        if (requestId < 0 || choiceWire is null)
        {
            return null;
        }
        foreach (var c in Actions)
        {
            if (c.Wire() == choiceWire)
            {
                return c;
            }
        }
        return null;
    }

    /// <summary>
    /// Upstream <c>rerouteApprovalPresentation</c>'s surface step: NOTIFICATION posts (or updates) the one card, every
    /// other route removes it — the question is answerable in exactly one place.
    /// </summary>
    public static void ApplyRouting(InterlockQueue.Presentation routing, IApprovalNotificationSurface? surface)
    {
        ArgumentNullException.ThrowIfNull(routing);
        if (surface is null)
        {
            return;
        }
        if (routing.Via == InterlockChannel.Notification && routing.Request is { } request)
        {
            surface.Show(request, routing.WaitingCount);
        }
        else
        {
            surface.Cancel();
        }
    }
}

/// <summary>
/// One-time answer tokens for posted approval cards. Guest processes run under this app's UID and can reach
/// <c>/system/bin/am</c>, so a non-exported receiver alone does not keep a forged "allow" out: the receiver accepts an
/// answer only with the 128-bit token that was put into the card's own PendingIntents (which other processes cannot
/// read), and each token answers once.
/// </summary>
public sealed class ApprovalTokens
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _tokens = new();

    /// <summary>The card's token, issued once per request id (a re-post of the same request reuses it).</summary>
    public string Issue(int requestId) =>
        _tokens.GetOrAdd(requestId, _ => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)));

    /// <summary>True exactly once for the right token; a wrong token leaves the right one in place.</summary>
    public bool Consume(int requestId, string? token)
    {
        if (token is null || !_tokens.TryGetValue(requestId, out var expected))
        {
            return false;
        }
        var match = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(expected), System.Text.Encoding.ASCII.GetBytes(token));
        return match && _tokens.TryRemove(new KeyValuePair<int, string>(requestId, expected));
    }

    public void Revoke(int requestId) => _tokens.TryRemove(requestId, out _);

    public void Clear() => _tokens.Clear();
}
