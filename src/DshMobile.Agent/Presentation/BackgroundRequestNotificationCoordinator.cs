using System.Security.Cryptography;
using System.Text;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;

namespace DshMobile.Agent.Presentation;

/// <summary>后台审批/问答系统通知的脱敏展示载荷（N-06-04），绝不含敏感参数全文与密钥。</summary>
public sealed record AgentRequestNotificationPayload(
    string EventId,
    int NotificationId,
    string SessionId,
    string SessionTitle,
    PendingRequestKind Kind,
    string SanitizedToolOrTopic,
    string SanitizedSummary,
    string OneTimeNonce,
    bool IsUpdate);

/// <summary>Android 侧 DSH Agent 请求通知通道抽象（与 Relay <c>ui_*</c> 通道严格物理隔离）。</summary>
public interface IBackgroundRequestNotificationSurface
{
    bool CanShow();
    void ShowOrUpdate(AgentRequestNotificationPayload payload);
    void CancelForEvent(string eventId, int notificationId);
    void CancelAll();
}

/// <summary>
/// 纯托管后台审批/问答通知协调器（N-06-04）：
/// 仅在 App 处于后台且请求处于 <see cref="PendingRequestState.Waiting"/> 时派发高优先级通知，
/// 为每张审批卡签发一次性 128-bit 加密随机数；通知快捷操作统一经 <see cref="RequestsEngine.DecideApprovalImmediateAsync"/>
/// 原子单写者认领，确保与页内卡片并发点击至多生效一次，拒绝后零工具执行。
/// </summary>
public sealed class BackgroundRequestNotificationCoordinator : IAsyncDisposable
{
    private const int MaxToolChars = 48;
    private const int MaxSummaryChars = 140;

    private readonly RequestsEngine _requests;
    private readonly IBackgroundRequestNotificationSurface _surface;
    private readonly Func<string, string?>? _sessionTitleResolver;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PostedEntry> _posted = new(StringComparer.Ordinal);
    private bool _inBackground;
    private int _disposed;

    public BackgroundRequestNotificationCoordinator(
        RequestsEngine requests,
        IBackgroundRequestNotificationSurface surface,
        Func<string, string?>? sessionTitleResolver = null)
    {
        _requests = requests ?? throw new ArgumentNullException(nameof(requests));
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _sessionTitleResolver = sessionTitleResolver;
        _requests.RequestsChanged += OnRequestsChanged;
    }

    public bool IsAppInBackground
    {
        get
        {
            lock (_gate)
            {
                return _inBackground;
            }
        }
    }

    /// <summary>切换 App 前后台状态；切回前台时清理未决通知并吊销卡片随机数，切入后台时按当前等待队列补发通知。</summary>
    public void SetAppInBackground(bool inBackground)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        bool changed;
        lock (_gate)
        {
            changed = _inBackground != inBackground;
            _inBackground = inBackground;
        }

        if (!changed)
        {
            return;
        }

        if (!inBackground)
        {
            lock (_gate)
            {
                _posted.Clear();
            }
            _surface.CancelAll();
            return;
        }

        ReconcileSnapshot(_requests.Snapshot());
    }

    /// <summary>
    /// 校验通知快捷操作携带的一次性随机数并交由 <see cref="RequestsEngine"/> 原子认领；
    /// 仅接受 <see cref="ApprovalDecision.AllowedOnce"/> 与 <see cref="ApprovalDecision.Rejected"/>。
    /// </summary>
    public bool TryClaimNotificationAction(
        string? eventId,
        ApprovalDecision decision,
        string? nonce,
        out Task<Outcome<Unit>> dispatchTask)
    {
        dispatchTask = Task.FromResult(Outcome<Unit>.Fail(DshFailureCode.StaleRequest, "agent_notify.invalid_claim"));
        if (Volatile.Read(ref _disposed) != 0 ||
            string.IsNullOrWhiteSpace(eventId) ||
            string.IsNullOrWhiteSpace(nonce) ||
            decision is not (ApprovalDecision.AllowedOnce or ApprovalDecision.Rejected))
        {
            return false;
        }

        int? cancelNotifId = null;
        lock (_gate)
        {
            if (!_posted.TryGetValue(eventId, out var entry) || entry.Kind != PendingRequestKind.Approval)
            {
                return false;
            }
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(entry.OneTimeNonce),
                    Encoding.ASCII.GetBytes(nonce)))
            {
                return false;
            }
            // 消费即吊销，防止同一 PendingIntent 重放
            _posted.Remove(eventId);
            cancelNotifId = entry.NotificationId;
        }

        if (cancelNotifId is { } nid)
        {
            _surface.CancelForEvent(eventId, nid);
        }

        dispatchTask = _requests.DecideApprovalImmediateAsync(eventId, decision);
        return true;
    }

    /// <summary>构建确定性且不与其它渠道混淆的通知 ID。</summary>
    public static int ComputeNotificationId(string eventId)
    {
        unchecked
        {
            uint hash = 2166136261U;
            foreach (var ch in eventId)
            {
                hash ^= ch;
                hash *= 16777619U;
            }
            // 偏移到独立高位区间，避免与 Relay 通知自增 ID 冲突
            return 0x4000_0000 | (int)(hash & 0x3FFF_FFFF);
        }
    }

    /// <summary>对工具名与参数摘要执行全量凭据脱敏、单行归一化与长度截断，绝不泄漏敏感参数原文。</summary>
    public static string SanitizeForNotification(string? raw, int maxChars = MaxSummaryChars)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }
        var flattened = raw.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return SecretRedactor.RedactAndTruncate(flattened, maxChars);
    }

    private void OnRequestsChanged(IReadOnlyList<PendingInteractionCard> cards)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        ReconcileSnapshot(cards);
    }

    private void ReconcileSnapshot(IReadOnlyList<PendingInteractionCard> cards)
    {
        var waitingCards = new Dictionary<string, PendingInteractionCard>(StringComparer.Ordinal);
        foreach (var card in cards)
        {
            if (card.State == PendingRequestState.Waiting)
            {
                waitingCards[card.EventId] = card;
            }
        }

        List<(string EventId, int NotificationId)> toCancel = [];
        List<AgentRequestNotificationPayload> toShow = [];

        lock (_gate)
        {
            // 先清理已结算、已取消、正在发送或进入 Held/TransportLost 的条目
            foreach (var existingId in _posted.Keys.ToArray())
            {
                if (!waitingCards.ContainsKey(existingId))
                {
                    toCancel.Add((existingId, _posted[existingId].NotificationId));
                    _posted.Remove(existingId);
                }
            }

            if (_inBackground && _surface.CanShow())
            {
                foreach (var card in waitingCards.Values)
                {
                    var payload = BuildPayload(card);
                    if (_posted.TryGetValue(card.EventId, out var prev))
                    {
                        if (string.Equals(prev.ContentSignature, payload.ContentSignature, StringComparison.Ordinal))
                        {
                            continue;
                        }
                        // 例如晚到 tool/call 回填了参数摘要：沿用同一随机数与通知 ID 做静默刷新
                        _posted[card.EventId] = prev with { ContentSignature = payload.ContentSignature };
                        toShow.Add(payload.Payload with
                        {
                            NotificationId = prev.NotificationId,
                            OneTimeNonce = prev.OneTimeNonce,
                            IsUpdate = true,
                        });
                    }
                    else
                    {
                        var notifId = ComputeNotificationId(card.EventId);
                        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
                        _posted[card.EventId] = new PostedEntry(notifId, card.Kind, nonce, payload.ContentSignature);
                        toShow.Add(payload.Payload with
                        {
                            NotificationId = notifId,
                            OneTimeNonce = nonce,
                            IsUpdate = false,
                        });
                    }
                }
            }
        }

        foreach (var (eventId, nid) in toCancel)
        {
            _surface.CancelForEvent(eventId, nid);
        }
        foreach (var item in toShow)
        {
            _surface.ShowOrUpdate(item);
        }
    }

    private (AgentRequestNotificationPayload Payload, string ContentSignature) BuildPayload(PendingInteractionCard card)
    {
        var sessionId = card.Scope.SessionId;
        var resolvedTitle = _sessionTitleResolver?.Invoke(sessionId);
        var sessionTitle = SanitizeForNotification(
            !string.IsNullOrWhiteSpace(resolvedTitle) ? resolvedTitle : sessionId,
            40);

        string toolOrTopic;
        string summary;
        if (card.Kind == PendingRequestKind.Approval)
        {
            var rawTool = string.IsNullOrWhiteSpace(card.ToolName) ? "unknown" : card.ToolName;
            toolOrTopic = SanitizeForNotification(rawTool, MaxToolChars);
            summary = card.ArgumentsReady && !string.IsNullOrWhiteSpace(card.SanitizedArgumentsSummary)
                ? SanitizeForNotification(card.SanitizedArgumentsSummary, MaxSummaryChars)
                : "参数尚未加载，请打开会话确认后再授权";
        }
        else
        {
            var first = card.Questions is { Count: > 0 } ? card.Questions[0] : null;
            toolOrTopic = SanitizeForNotification(
                !string.IsNullOrWhiteSpace(first?.Header) ? first!.Header : "Agent 提问",
                MaxToolChars);
            summary = SanitizeForNotification(
                first?.Question ?? "Agent 需要您的回答，点击打开会话",
                MaxSummaryChars);
        }

        var signature = $"{card.Kind}|{sessionTitle}|{toolOrTopic}|{summary}";
        var payload = new AgentRequestNotificationPayload(
            card.EventId,
            0,
            sessionId,
            sessionTitle,
            card.Kind,
            toolOrTopic,
            summary,
            string.Empty,
            IsUpdate: false);
        return (payload, signature);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }
        _requests.RequestsChanged -= OnRequestsChanged;
        lock (_gate)
        {
            _posted.Clear();
        }
        _surface.CancelAll();
        return ValueTask.CompletedTask;
    }

    private sealed record PostedEntry(
        int NotificationId,
        PendingRequestKind Kind,
        string OneTimeNonce,
        string ContentSignature);
}
