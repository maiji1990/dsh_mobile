using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using DshMobile.Agent.Presentation;
using DshMobile.App.Ui;
using Google.Android.Material.Button;

namespace DshMobile.App.Chats;

/// <summary>
/// 高密度紧凑会话列表适配器（UI Slice 2）：
/// 默认展示标题、时间/工作区摘要、状态胶囊与右上角 <c>⋮</c>（长按同样触发底部抽屉），
/// 仅在运行中/待审批时在右下角显露高频停止胶囊与状态角标，支持离线只读缓存模式。
/// </summary>
internal sealed class ChatsAdapter(
    Action<SessionListItemView> onOpen,
    Action<SessionListItemView> onMore,
    Action<SessionListItemView> onCancel) : RecyclerView.Adapter
{
    private IReadOnlyList<SessionListItemView> _items = [];
    private string? _activeSessionId;
    private bool _canCancel;
    private bool _isCachedOffline;

    public void Submit(
        IReadOnlyList<SessionListItemView> items,
        string? activeSessionId,
        bool canCancel,
        bool isCachedOffline = false)
    {
        _items = items;
        _activeSessionId = activeSessionId;
        _canCancel = canCancel;
        _isCachedOffline = isCachedOffline;
        NotifyDataSetChanged();
    }

    public override int ItemCount => _items.Count;

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var view = LayoutInflater.From(parent.Context)!.Inflate(Resource.Layout.item_session_row, parent, false)!;
        return new Holder(view, onOpen, onMore, onCancel);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        var item = _items[position];
        var isActive = string.Equals(item.SessionId, _activeSessionId, StringComparison.Ordinal);
        ((Holder)holder).Bind(item, isActive, _canCancel, _isCachedOffline);
    }

    private sealed class Holder : RecyclerView.ViewHolder
    {
        private readonly TextView _title;
        private readonly View _badgeActive;
        private readonly View _badgeRunning;
        private readonly TextView _badgePending;
        private readonly TextView _meta;
        private readonly MaterialButton _moreBtn;
        private readonly MaterialButton _cancelBtn;
        private SessionListItemView? _bound;

        public Holder(
            View itemView,
            Action<SessionListItemView> onOpen,
            Action<SessionListItemView> onMore,
            Action<SessionListItemView> onCancel) : base(itemView)
        {
            _title = itemView.FindViewById<TextView>(Resource.Id.session_item_title)!;
            _badgeActive = itemView.FindViewById(Resource.Id.session_item_badge_active)!;
            _badgeRunning = itemView.FindViewById(Resource.Id.session_item_badge_running)!;
            _badgePending = itemView.FindViewById<TextView>(Resource.Id.session_item_badge_pending)!;
            _meta = itemView.FindViewById<TextView>(Resource.Id.session_item_meta)!;
            _moreBtn = itemView.FindViewById<MaterialButton>(Resource.Id.session_item_more)!;
            _cancelBtn = itemView.FindViewById<MaterialButton>(Resource.Id.session_item_cancel)!;

            itemView.Click += (_, _) => { if (_bound is not null) onOpen(_bound); };
            itemView.LongClick += (_, e) =>
            {
                if (_bound is not null)
                {
                    onMore(_bound);
                    e.Handled = true;
                }
            };
            _moreBtn.Click += (_, _) => { if (_bound is not null) onMore(_bound); };
            _cancelBtn.Click += (_, _) => { if (_bound is not null) onCancel(_bound); };
        }

        public void Bind(SessionListItemView item, bool isActive, bool canCancel, bool isCachedOffline)
        {
            _bound = item;
            var ctx = ItemView.Context!;
            _title.Text = ChatDisplayFormatter.FormatShortSessionTitle(
                item.SessionId,
                item.DisplayTitle,
                ctx.GetString(Resource.String.chats_untitled_prefix));
            _badgeActive.Visibility = !isCachedOffline && isActive ? ViewStates.Visible : ViewStates.Gone;
            _badgeRunning.Visibility = !isCachedOffline && item.Running ? ViewStates.Visible : ViewStates.Gone;
            if (!isCachedOffline && item.PendingRequestCount > 0)
            {
                _badgePending.Visibility = ViewStates.Visible;
                _badgePending.Text = Rows.Fmt(ctx, Resource.String.chats_badge_pending, item.PendingRequestCount);
            }
            else
            {
                _badgePending.Visibility = ViewStates.Gone;
            }

            var updated = item.UpdatedAtUnixMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(item.UpdatedAtUnixMs).ToLocalTime().ToString("MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture)
                : "-";
            if (isCachedOffline)
            {
                _meta.Text = Rows.Fmt(ctx, Resource.String.chats_cached_meta_fmt, updated);
            }
            else
            {
                var subTag = !string.IsNullOrWhiteSpace(item.ParentSessionId) ? $"{ctx.GetString(Resource.String.chats_badge_subagent)} · " : string.Empty;
                var path = string.IsNullOrWhiteSpace(item.Cwd) ? item.SessionId : item.Cwd;
                _meta.Text = $"{subTag}{updated} · {path}";
            }

            _cancelBtn.Visibility = !isCachedOffline && item.Running && canCancel ? ViewStates.Visible : ViewStates.Gone;
            _cancelBtn.Enabled = canCancel;
        }
    }
}
