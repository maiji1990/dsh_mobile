using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using DshMobile.Agent.Engines;
using DshMobile.Agent.Presentation;
using DshMobile.App.Ui;
using Google.Android.Material.Button;

namespace DshMobile.App.Chats;

/// <summary>
/// UI-S4 无框高密度会话列表适配器：
/// 按「待你处理 → 进行中 → 今天/昨天/更早」多 ViewType 分区构建，
/// 会话行仅保留标题、相对时间与单行元信息，长按整行或左滑呼出操作抽屉，彻底移除行尾固定 <c>...</c> 占位。
/// </summary>
internal sealed class ChatsAdapter : RecyclerView.Adapter
{
    private const int MaxInlinePending = 2;
    private const int ViewTypeSectionHeader = 0;
    private const int ViewTypePendingHeader = 1;
    private const int ViewTypePending = 2;
    private const int ViewTypeSession = 3;

    private readonly Context _context;
    private readonly Action<SessionListItemView> _onOpen;
    private readonly Action<SessionListItemView> _onMore;
    private readonly Action<InboxItemView> _onApprovePending;
    private readonly Action<InboxItemView> _onRejectPending;
    private readonly Action<InboxItemView> _onOpenPending;
    private readonly Action _onViewAllPending;

    private List<Row> _rows = [];

    public ChatsAdapter(
        Context context,
        Action<SessionListItemView> onOpen,
        Action<SessionListItemView> onMore,
        Action<InboxItemView> onApprovePending,
        Action<InboxItemView> onRejectPending,
        Action<InboxItemView> onOpenPending,
        Action onViewAllPending)
    {
        _context = context;
        _onOpen = onOpen;
        _onMore = onMore;
        _onApprovePending = onApprovePending;
        _onRejectPending = onRejectPending;
        _onOpenPending = onOpenPending;
        _onViewAllPending = onViewAllPending;
    }

    public void Submit(
        IReadOnlyList<SessionListItemView> items,
        IReadOnlyList<InboxItemView> pendingItems,
        string? activeSessionId,
        bool isCachedOffline = false)
    {
        _rows = BuildRows(items, pendingItems, activeSessionId, isCachedOffline);
        NotifyDataSetChanged();
    }

    /// <summary>左滑与长按操作只对真实会话行生效，分区标题与内联审批卡不参与滑动。</summary>
    public SessionListItemView? GetSessionAt(int position) =>
        position >= 0 && position < _rows.Count && _rows[position] is SessionRow row
            ? row.Item
            : null;

    public override int ItemCount => _rows.Count;

    public override int GetItemViewType(int position) => _rows[position] switch
    {
        SectionRow => ViewTypeSectionHeader,
        PendingHeaderRow => ViewTypePendingHeader,
        PendingRow => ViewTypePending,
        _ => ViewTypeSession,
    };

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var inflater = LayoutInflater.From(parent.Context)!;
        return viewType switch
        {
            ViewTypeSectionHeader => new SectionHolder(
                (TextView)inflater.Inflate(Resource.Layout.item_session_section_header, parent, false)!),
            ViewTypePendingHeader => new PendingHeaderHolder(
                inflater.Inflate(Resource.Layout.item_session_pending_header, parent, false)!,
                _onViewAllPending),
            ViewTypePending => new PendingHolder(
                inflater.Inflate(Resource.Layout.item_session_pending, parent, false)!,
                _onApprovePending,
                _onRejectPending,
                _onOpenPending),
            _ => new SessionHolder(
                inflater.Inflate(Resource.Layout.item_session_row, parent, false)!,
                _onOpen,
                _onMore),
        };
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        switch (_rows[position])
        {
            case SectionRow section:
                ((SectionHolder)holder).Bind(section.Title);
                break;
            case PendingRow pending:
                ((PendingHolder)holder).Bind(pending.Item);
                break;
            case SessionRow session:
                ((SessionHolder)holder).Bind(session.Item, session.IsActive);
                break;
        }
    }

    private List<Row> BuildRows(
        IReadOnlyList<SessionListItemView> items,
        IReadOnlyList<InboxItemView> pendingItems,
        string? activeSessionId,
        bool isCachedOffline)
    {
        var rows = new List<Row>();

        // 离线只读缓存不展示实时待办与时间分组，避免把缓存快照伪装成在线状态。
        if (isCachedOffline)
        {
            foreach (var item in items)
            {
                rows.Add(new SessionRow(item, IsActive(item, activeSessionId)));
            }
            return rows;
        }

        if (pendingItems.Count > 0)
        {
            rows.Add(new PendingHeaderRow());
            foreach (var pending in pendingItems.Take(MaxInlinePending))
            {
                rows.Add(new PendingRow(pending));
            }
        }

        var runningIds = new HashSet<string>(StringComparer.Ordinal);
        var running = new List<SessionListItemView>();
        foreach (var item in items)
        {
            if (item.Running || IsActive(item, activeSessionId))
            {
                running.Add(item);
                runningIds.Add(item.SessionId);
            }
        }

        if (running.Count > 0)
        {
            rows.Add(new SectionRow(_context.GetString(Resource.String.chats_section_running) ?? string.Empty));
            foreach (var item in running)
            {
                rows.Add(new SessionRow(item, IsActive(item, activeSessionId)));
            }
        }

        var today = new List<SessionListItemView>();
        var yesterday = new List<SessionListItemView>();
        var earlier = new List<SessionListItemView>();
        var reference = DateTimeOffset.Now;
        foreach (var item in items)
        {
            if (runningIds.Contains(item.SessionId))
            {
                continue;
            }

            switch (ClassifyTimeBucket(item.UpdatedAtUnixMs, reference))
            {
                case 0:
                    today.Add(item);
                    break;
                case 1:
                    yesterday.Add(item);
                    break;
                default:
                    earlier.Add(item);
                    break;
            }
        }

        AppendTimeSection(rows, Resource.String.chats_section_today, today, activeSessionId);
        AppendTimeSection(rows, Resource.String.chats_section_yesterday, yesterday, activeSessionId);
        AppendTimeSection(rows, Resource.String.chats_section_earlier, earlier, activeSessionId);
        return rows;
    }

    private void AppendTimeSection(
        List<Row> rows,
        int titleResource,
        List<SessionListItemView> sectionItems,
        string? activeSessionId)
    {
        if (sectionItems.Count == 0)
        {
            return;
        }

        rows.Add(new SectionRow(_context.GetString(titleResource) ?? string.Empty));
        foreach (var item in sectionItems)
        {
            rows.Add(new SessionRow(item, IsActive(item, activeSessionId)));
        }
    }

    private static bool IsActive(SessionListItemView item, string? activeSessionId) =>
        !string.IsNullOrEmpty(activeSessionId) &&
        string.Equals(item.SessionId, activeSessionId, StringComparison.Ordinal);

    /// <summary>0 = 今天（含未来时间戳），1 = 昨天，2 = 更早或无时间戳。</summary>
    private static int ClassifyTimeBucket(long updatedAtUnixMs, DateTimeOffset reference)
    {
        if (updatedAtUnixMs <= 0)
        {
            return 2;
        }

        var updatedDate = DateTimeOffset.FromUnixTimeMilliseconds(updatedAtUnixMs).ToLocalTime().Date;
        var today = reference.ToLocalTime().Date;
        if (updatedDate >= today)
        {
            return 0;
        }
        return updatedDate == today.AddDays(-1) ? 1 : 2;
    }

    private abstract record Row;

    private sealed record SectionRow(string Title) : Row;

    private sealed record PendingHeaderRow : Row;

    private sealed record PendingRow(InboxItemView Item) : Row;

    private sealed record SessionRow(SessionListItemView Item, bool IsActive) : Row;

    private sealed class SectionHolder(TextView title) : RecyclerView.ViewHolder(title)
    {
        public void Bind(string text) => title.Text = text;
    }

    private sealed class PendingHeaderHolder : RecyclerView.ViewHolder
    {
        private readonly TextView _viewAll;

        public PendingHeaderHolder(View itemView, Action onViewAll) : base(itemView)
        {
            _viewAll = itemView.FindViewById<TextView>(Resource.Id.session_pending_header_view_all)!;
            _viewAll.Click += (_, _) => onViewAll();
        }
    }

    private sealed class PendingHolder : RecyclerView.ViewHolder
    {
        private readonly TextView _kind;
        private readonly TextView _session;
        private readonly TextView _state;
        private readonly TextView _headline;
        private readonly TextView _summary;
        private readonly MaterialButton _approveButton;
        private readonly MaterialButton _rejectButton;
        private readonly MaterialButton _openButton;
        private readonly Action<InboxItemView> _onApprove;
        private readonly Action<InboxItemView> _onReject;
        private readonly Action<InboxItemView> _onOpen;
        private InboxItemView? _bound;

        public PendingHolder(
            View itemView,
            Action<InboxItemView> onApprove,
            Action<InboxItemView> onReject,
            Action<InboxItemView> onOpen) : base(itemView)
        {
            _onApprove = onApprove;
            _onReject = onReject;
            _onOpen = onOpen;
            _kind = itemView.FindViewById<TextView>(Resource.Id.session_pending_kind)!;
            _session = itemView.FindViewById<TextView>(Resource.Id.session_pending_session)!;
            _state = itemView.FindViewById<TextView>(Resource.Id.session_pending_state)!;
            _headline = itemView.FindViewById<TextView>(Resource.Id.session_pending_headline)!;
            _summary = itemView.FindViewById<TextView>(Resource.Id.session_pending_summary)!;
            _approveButton = itemView.FindViewById<MaterialButton>(Resource.Id.session_pending_approve)!;
            _rejectButton = itemView.FindViewById<MaterialButton>(Resource.Id.session_pending_reject)!;
            _openButton = itemView.FindViewById<MaterialButton>(Resource.Id.session_pending_open)!;

            itemView.Click += (_, _) =>
            {
                if (_bound is { } item)
                {
                    _onOpen(item);
                }
            };
            _approveButton.Click += (_, _) =>
            {
                if (_bound is { } item)
                {
                    _onApprove(item);
                }
            };
            _rejectButton.Click += (_, _) =>
            {
                if (_bound is { } item)
                {
                    _onReject(item);
                }
            };
            _openButton.Click += (_, _) =>
            {
                if (_bound is { } item)
                {
                    _onOpen(item);
                }
            };
        }

        public void Bind(InboxItemView item)
        {
            _bound = item;
            var context = ItemView.Context!;
            _kind.SetText(item.Kind == PendingRequestKind.Approval
                ? Resource.String.inbox_kind_approval
                : Resource.String.inbox_kind_question);
            _session.Text = item.SessionTitle;
            _state.SetText(item.State switch
            {
                PendingRequestState.Held => Resource.String.inbox_state_held,
                PendingRequestState.Sending => Resource.String.inbox_state_sending,
                PendingRequestState.TransportLost => Resource.String.inbox_state_lost,
                _ => Resource.String.inbox_state_waiting,
            });
            _headline.Text = item.Headline;
            _summary.Text = string.IsNullOrWhiteSpace(item.SanitizedSummary)
                ? context.GetString(Resource.String.chats_inline_no_args) ?? string.Empty
                : item.SanitizedSummary;

            var isActionableApproval = item.Kind == PendingRequestKind.Approval && item.IsActionable;
            _approveButton.Visibility = isActionableApproval ? ViewStates.Visible : ViewStates.Gone;
            _rejectButton.Visibility = isActionableApproval ? ViewStates.Visible : ViewStates.Gone;
            _openButton.Visibility = isActionableApproval ? ViewStates.Gone : ViewStates.Visible;
            if (!isActionableApproval)
            {
                _openButton.SetText(item.Kind == PendingRequestKind.Approval
                    ? Resource.String.chats_inline_confirm
                    : Resource.String.chats_inline_answer);
            }
        }
    }

    private sealed class SessionHolder : RecyclerView.ViewHolder
    {
        private readonly TextView _title;
        private readonly TextView _time;
        private readonly TextView _meta;
        private readonly View _runningDot;
        private readonly Action<SessionListItemView> _onOpen;
        private readonly Action<SessionListItemView> _onMore;
        private SessionListItemView? _bound;

        public SessionHolder(
            View itemView,
            Action<SessionListItemView> onOpen,
            Action<SessionListItemView> onMore) : base(itemView)
        {
            _onOpen = onOpen;
            _onMore = onMore;
            _title = itemView.FindViewById<TextView>(Resource.Id.session_item_title)!;
            _time = itemView.FindViewById<TextView>(Resource.Id.session_item_time)!;
            _meta = itemView.FindViewById<TextView>(Resource.Id.session_item_meta)!;
            _runningDot = itemView.FindViewById(Resource.Id.session_item_badge_running)!;

            itemView.Click += (_, _) =>
            {
                if (_bound is { } item)
                {
                    _onOpen(item);
                }
            };
            itemView.LongClick += (_, e) =>
            {
                if (_bound is { } item)
                {
                    _onMore(item);
                    e.Handled = true;
                }
            };
        }

        public void Bind(SessionListItemView item, bool isActive)
        {
            _bound = item;
            var context = ItemView.Context!;
            _title.Text = ChatDisplayFormatter.FormatShortSessionTitle(
                item.SessionId,
                item.DisplayTitle,
                context.GetString(Resource.String.chats_untitled_prefix) ?? string.Empty);
            _title.SetTextColor(new Android.Graphics.Color(Rows.Color(
                _title,
                isActive ? Resource.Attribute.colorPrimary : Resource.Attribute.colorOnSurface)));

            _runningDot.Visibility = item.Running ? ViewStates.Visible : ViewStates.Gone;
            _time.Text = ChatDisplayFormatter.FormatRelativeSessionTime(item.UpdatedAtUnixMs, DateTimeOffset.Now);

            _meta.Text = BuildMetaLine(context, item);
        }

        private static string BuildMetaLine(Context context, SessionListItemView item)
        {
            var segments = new List<string>(5);
            var workspace = ChatDisplayFormatter.ExtractLastPathSegment(item.Cwd);
            if (!string.IsNullOrWhiteSpace(workspace))
            {
                segments.Add(workspace);
            }
            if (!string.IsNullOrWhiteSpace(item.AgentPreset))
            {
                segments.Add(item.AgentPreset!.Trim());
            }
            if (!string.IsNullOrWhiteSpace(item.Origin))
            {
                segments.Add(item.Origin!.Trim());
            }
            if (item.PendingRequestCount > 0)
            {
                segments.Add(Rows.Fmt(context, Resource.String.chats_badge_pending, item.PendingRequestCount));
            }
            else if (item.Running)
            {
                segments.Add(context.GetString(Resource.String.chats_badge_running) ?? string.Empty);
            }
            if (!string.IsNullOrWhiteSpace(item.ParentSessionId))
            {
                segments.Add(context.GetString(Resource.String.chats_badge_subagent) ?? string.Empty);
            }

            return segments.Count > 0 ? string.Join(" · ", segments) : item.SessionId;
        }
    }
}
