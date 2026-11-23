using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;

namespace DshMobile.Agent.Presentation;

/// <summary>跨会话待办收件箱单条展示模型（N-06-03）。</summary>
public sealed record InboxItemView(
    string EventId,
    string SessionId,
    string SessionTitle,
    PendingRequestKind Kind,
    PendingRequestState State,
    string Headline,
    string SanitizedSummary,
    bool ArgumentsReady,
    bool IsActionable);

/// <summary>跨会话待办收件箱不可变状态快照（N-06-03）。</summary>
public sealed record InboxUiState(
    int TotalCount,
    int ApprovalCount,
    int QuestionCount,
    IReadOnlyList<InboxItemView> Items);

/// <summary>
/// 纯托管跨会话待办收件箱 ViewModel（N-06-03）：
/// 聚合 <see cref="RequestsEngine"/> 的全局待审批与待问答卡片，结合会话标题解析器实时发布角标计数与跳转条目。
/// </summary>
public sealed class InboxViewModel : IAsyncDisposable
{
    private readonly RequestsEngine _requests;
    private readonly Func<string, string?>? _sessionTitleResolver;
    private readonly Lock _stateLock = new();
    private IReadOnlyList<PendingInteractionCard> _cards = [];
    private InboxUiState _state;
    private int _disposed;

    public InboxViewModel(
        RequestsEngine requests,
        Func<string, string?>? sessionTitleResolver = null)
    {
        _requests = requests ?? throw new ArgumentNullException(nameof(requests));
        _sessionTitleResolver = sessionTitleResolver;
        _cards = _requests.Snapshot();
        _state = BuildState(_cards, _sessionTitleResolver);
        _requests.RequestsChanged += OnRequestsChanged;
    }

    public InboxUiState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public event Action<InboxUiState>? StateChanged;

    /// <summary>当会话列表或会话标题更新时刷新收件箱条目中的会话标题。</summary>
    public void RefreshTitles()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        PublishFromCards();
    }

    /// <summary>经 <see cref="RequestsEngine"/> 原子单写者认领一次审批放行。</summary>
    public Task<Outcome<Unit>> ApproveOnceAsync(string eventId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _requests.DecideApprovalImmediateAsync(eventId, ApprovalDecision.AllowedOnce, ct);
    }

    /// <summary>经 <see cref="RequestsEngine"/> 原子单写者认领拒绝审批。</summary>
    public Task<Outcome<Unit>> RejectApprovalAsync(string eventId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _requests.DecideApprovalImmediateAsync(eventId, ApprovalDecision.Rejected, ct);
    }

    private void OnRequestsChanged(IReadOnlyList<PendingInteractionCard> cards)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        lock (_stateLock)
        {
            _cards = cards;
        }
        PublishFromCards();
    }

    private void PublishFromCards()
    {
        InboxUiState next;
        lock (_stateLock)
        {
            next = BuildState(_cards, _sessionTitleResolver);
            if (next == _state)
            {
                return;
            }
            _state = next;
        }
        StateChanged?.Invoke(next);
    }

    private static InboxUiState BuildState(
        IReadOnlyList<PendingInteractionCard> cards,
        Func<string, string?>? titleResolver)
    {
        if (cards.Count == 0)
        {
            return new InboxUiState(0, 0, 0, []);
        }

        var items = new List<InboxItemView>(cards.Count);
        var approvals = 0;
        var questions = 0;

        foreach (var card in cards)
        {
            if (card.State is PendingRequestState.Settled or PendingRequestState.Cancelled)
            {
                continue;
            }

            if (card.Kind == PendingRequestKind.Approval)
            {
                approvals++;
            }
            else
            {
                questions++;
            }

            var sessionId = card.Scope.SessionId;
            var resolvedTitle = titleResolver?.Invoke(sessionId);
            var sessionTitle = !string.IsNullOrWhiteSpace(resolvedTitle) ? resolvedTitle! : sessionId;

            string headline;
            string summary;
            if (card.Kind == PendingRequestKind.Approval)
            {
                var tool = string.IsNullOrWhiteSpace(card.ToolName) ? "unknown" : card.ToolName!;
                headline = SecretRedactor.RedactAndTruncate(tool, 48);
                summary = card.ArgumentsReady && !string.IsNullOrWhiteSpace(card.SanitizedArgumentsSummary)
                    ? SecretRedactor.RedactAndTruncate(card.SanitizedArgumentsSummary, 160)
                    : "参数尚未加载，请打开会话确认";
            }
            else
            {
                var firstQ = card.Questions is { Count: > 0 } ? card.Questions[0] : null;
                var rawTopic = !string.IsNullOrWhiteSpace(firstQ?.Header)
                    ? firstQ!.Header!
                    : firstQ?.Question ?? "等待回答问题";
                headline = SecretRedactor.RedactAndTruncate(rawTopic, 64);
                summary = SecretRedactor.RedactAndTruncate(firstQ?.Question ?? "请在会话中查看并提交回答", 160);
            }

            var actionable = card.State is PendingRequestState.Waiting or PendingRequestState.TransportLost;
            items.Add(new InboxItemView(
                card.EventId,
                sessionId,
                sessionTitle,
                card.Kind,
                card.State,
                headline,
                summary,
                card.ArgumentsReady,
                actionable));
        }

        return new InboxUiState(items.Count, approvals, questions, items);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }
        _requests.RequestsChanged -= OnRequestsChanged;
        return ValueTask.CompletedTask;
    }
}
