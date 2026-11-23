using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using DshMobile.Agent.Presentation;
using DshMobile.App.Ui;
using Google.Android.Material.Button;

namespace DshMobile.App.Chats;

/// <summary>会话列表适配器（支持标题/工作区摘要、运行中与待审批角标、打开/重命名/Fork/取消操作，N-05-03）。</summary>
internal sealed class ChatsAdapter(
    Action<SessionListItemView> onOpen,
    Action<SessionListItemView> onRename,
    Action<SessionListItemView> onFork,
    Action<SessionListItemView> onCancel) : RecyclerView.Adapter
{
    private IReadOnlyList<SessionListItemView> _items = [];
    private string? _activeSessionId;
    private bool _canCancel;

    public void Submit(IReadOnlyList<SessionListItemView> items, string? activeSessionId, bool canCancel)
    {
        _items = items;
        _activeSessionId = activeSessionId;
        _canCancel = canCancel;
        NotifyDataSetChanged();
    }

    public override int ItemCount => _items.Count;

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var view = LayoutInflater.From(parent.Context)!.Inflate(Resource.Layout.item_session_row, parent, false)!;
        return new Holder(view, onOpen, onRename, onFork, onCancel);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        var item = _items[position];
        var isActive = string.Equals(item.SessionId, _activeSessionId, StringComparison.Ordinal);
        ((Holder)holder).Bind(item, isActive, _canCancel);
    }

    private sealed class Holder : RecyclerView.ViewHolder
    {
        private readonly View _body;
        private readonly TextView _title;
        private readonly View _badgeActive;
        private readonly View _badgeRunning;
        private readonly TextView _badgePending;
        private readonly TextView _meta;
        private readonly MaterialButton _cancelBtn;
        private readonly MaterialButton _forkBtn;
        private readonly MaterialButton _renameBtn;
        private readonly MaterialButton _openBtn;
        private SessionListItemView? _bound;

        public Holder(
            View itemView,
            Action<SessionListItemView> onOpen,
            Action<SessionListItemView> onRename,
            Action<SessionListItemView> onFork,
            Action<SessionListItemView> onCancel) : base(itemView)
        {
            _body = itemView.FindViewById(Resource.Id.session_item_body)!;
            _title = itemView.FindViewById<TextView>(Resource.Id.session_item_title)!;
            _badgeActive = itemView.FindViewById(Resource.Id.session_item_badge_active)!;
            _badgeRunning = itemView.FindViewById(Resource.Id.session_item_badge_running)!;
            _badgePending = itemView.FindViewById<TextView>(Resource.Id.session_item_badge_pending)!;
            _meta = itemView.FindViewById<TextView>(Resource.Id.session_item_meta)!;
            _cancelBtn = itemView.FindViewById<MaterialButton>(Resource.Id.session_item_cancel)!;
            _forkBtn = itemView.FindViewById<MaterialButton>(Resource.Id.session_item_fork)!;
            _renameBtn = itemView.FindViewById<MaterialButton>(Resource.Id.session_item_rename)!;
            _openBtn = itemView.FindViewById<MaterialButton>(Resource.Id.session_item_open)!;

            // F18：整行卡片、内容主体与标题均可直接点击打开会话。
            itemView.Click += (_, _) => { if (_bound is not null) onOpen(_bound); };
            _body.Click += (_, _) => { if (_bound is not null) onOpen(_bound); };
            _title.Click += (_, _) => { if (_bound is not null) onOpen(_bound); };
            _openBtn.Click += (_, _) => { if (_bound is not null) onOpen(_bound); };
            _renameBtn.Click += (_, _) => { if (_bound is not null) onRename(_bound); };
            _forkBtn.Click += (_, _) => { if (_bound is not null) onFork(_bound); };
            _cancelBtn.Click += (_, _) => { if (_bound is not null) onCancel(_bound); };
        }

        public void Bind(SessionListItemView item, bool isActive, bool canCancel)
        {
            _bound = item;
            var ctx = ItemView.Context!;
            _title.Text = item.DisplayTitle;
            _badgeActive.Visibility = isActive ? ViewStates.Visible : ViewStates.Gone;
            _badgeRunning.Visibility = item.Running ? ViewStates.Visible : ViewStates.Gone;
            if (item.PendingRequestCount > 0)
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
            var subTag = !string.IsNullOrWhiteSpace(item.ParentSessionId) ? $"{ctx.GetString(Resource.String.chats_badge_subagent)} · " : string.Empty;
            var path = string.IsNullOrWhiteSpace(item.Cwd) ? item.SessionId : item.Cwd;
            _meta.Text = $"{subTag}{updated} · {path}";

            _cancelBtn.Visibility = item.Running ? ViewStates.Visible : ViewStates.Gone;
            _cancelBtn.Enabled = canCancel;
        }
    }
}
