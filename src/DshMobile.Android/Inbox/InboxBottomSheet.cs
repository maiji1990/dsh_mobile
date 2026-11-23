using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using DshMobile.Agent.Engines;
using DshMobile.Agent.Presentation;
using DshMobile.App.Chat;
using DshMobile.App.Ui;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Button;

namespace DshMobile.App.Inbox;

/// <summary>
/// 跨会话待办收件箱底部弹窗（N-06-03）：
/// 全量使用 XML 布局（<c>sheet_inbox.xml</c> / <c>item_inbox_request.xml</c>）与 <see cref="DiffUtil"/>，
/// 点击任意待办条目直接跳转 <see cref="ChatActivity"/> 打开对应会话并定位交互卡片。
/// </summary>
public sealed class InboxBottomSheet : BottomSheetDialogFragment
{
    public const string SheetTag = "dsh.inbox.bottom_sheet";

    private InboxViewModel _vm = null!;
    private TextView _subtitle = null!;
    private RecyclerView _list = null!;
    private View _empty = null!;
    private InboxAdapter _adapter = null!;
    private int _renderPosted;

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        var view = inflater.Inflate(Resource.Layout.sheet_inbox, container, false)!;
        _vm = DshMobileApplication.Current.Bridge.Agent.Inbox;
        _subtitle = view.FindViewById<TextView>(Resource.Id.inbox_sheet_subtitle)!;
        _list = view.FindViewById<RecyclerView>(Resource.Id.inbox_list)!;
        _list.SetLayoutManager(new LinearLayoutManager(RequireContext()));
        _adapter = new InboxAdapter(OpenTargetChat);
        _list.SetAdapter(_adapter);

        _empty = view.FindViewById(Resource.Id.inbox_empty)!;
        _empty.FindViewById<ImageView>(Resource.Id.empty_icon)!.SetImageResource(Resource.Drawable.ic_check_circle);
        _empty.FindViewById<TextView>(Resource.Id.empty_text)!.SetText(Resource.String.inbox_empty);

        return view;
    }

    public override void OnStart()
    {
        base.OnStart();
        _vm.StateChanged += OnStateChanged;
        Render(_vm.State);
    }

    public override void OnStop()
    {
        _vm.StateChanged -= OnStateChanged;
        base.OnStop();
    }

    private void OnStateChanged(InboxUiState _)
    {
        if (Interlocked.Exchange(ref _renderPosted, 1) == 0)
        {
            Activity?.RunOnUiThread(() =>
            {
                Volatile.Write(ref _renderPosted, 0);
                if (View is not null && IsAdded)
                {
                    Render(_vm.State);
                }
            });
        }
    }

    private void Render(InboxUiState state)
    {
        _subtitle.Text = Rows.Fmt(RequireContext(), Resource.String.inbox_sheet_subtitle_fmt, state.TotalCount);
        _adapter.Submit(state.Items);
        _empty.Visibility = state.Items.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
        _list.Visibility = state.Items.Count == 0 ? ViewStates.Gone : ViewStates.Visible;
    }

    private void OpenTargetChat(InboxItemView item)
    {
        var ctx = RequireContext();
        DismissAllowingStateLoss();
        var intent = new Intent(ctx, typeof(ChatActivity))
            .PutExtra(ChatActivity.ExtraSessionId, item.SessionId)
            .PutExtra(ChatActivity.ExtraFocusEventId, item.EventId);
        StartActivity(intent);
    }

    private sealed class InboxAdapter(Action<InboxItemView> onOpen) : RecyclerView.Adapter
    {
        private List<InboxItemView> _items = [];

        public override int ItemCount => _items.Count;

        public void Submit(IReadOnlyList<InboxItemView> next)
        {
            var old = _items;
            var nextList = next.ToList();
            var diff = DiffUtil.CalculateDiff(new InboxDiffCallback(old, nextList));
            _items = nextList;
            diff.DispatchUpdatesTo(this);
        }

        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
        {
            var view = LayoutInflater.From(parent.Context)!.Inflate(Resource.Layout.item_inbox_request, parent, false)!;
            return new ItemViewHolder(view, onOpen);
        }

        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position) =>
            ((ItemViewHolder)holder).Bind(_items[position]);

        private sealed class ItemViewHolder : RecyclerView.ViewHolder
        {
            private readonly TextView _kind;
            private readonly TextView _session;
            private readonly TextView _state;
            private readonly TextView _headline;
            private readonly TextView _summary;
            private readonly MaterialButton _openBtn;
            private InboxItemView? _bound;

            public ItemViewHolder(View itemView, Action<InboxItemView> onOpen) : base(itemView)
            {
                _kind = itemView.FindViewById<TextView>(Resource.Id.inbox_item_kind)!;
                _session = itemView.FindViewById<TextView>(Resource.Id.inbox_item_session)!;
                _state = itemView.FindViewById<TextView>(Resource.Id.inbox_item_state)!;
                _headline = itemView.FindViewById<TextView>(Resource.Id.inbox_item_headline)!;
                _summary = itemView.FindViewById<TextView>(Resource.Id.inbox_item_summary)!;
                _openBtn = itemView.FindViewById<MaterialButton>(Resource.Id.inbox_item_open_btn)!;

                itemView.Click += (_, _) =>
                {
                    if (_bound is { } item)
                    {
                        onOpen(item);
                    }
                };
                _openBtn.Click += (_, _) =>
                {
                    if (_bound is { } item)
                    {
                        onOpen(item);
                    }
                };
            }

            public void Bind(InboxItemView item)
            {
                _bound = item;
                var ctx = ItemView.Context!;
                _kind.SetText(item.Kind == PendingRequestKind.Approval ? Resource.String.inbox_kind_approval : Resource.String.inbox_kind_question);
                _session.Text = item.SessionTitle;
                _state.SetText(item.State switch
                {
                    PendingRequestState.Held => Resource.String.inbox_state_held,
                    PendingRequestState.Sending => Resource.String.inbox_state_sending,
                    PendingRequestState.TransportLost => Resource.String.inbox_state_lost,
                    _ => Resource.String.inbox_state_waiting,
                });
                _headline.Text = item.Headline;
                _summary.Text = item.SanitizedSummary;
            }
        }

        private sealed class InboxDiffCallback(
            IReadOnlyList<InboxItemView> oldItems,
            IReadOnlyList<InboxItemView> newItems) : DiffUtil.Callback
        {
            public override int OldListSize => oldItems.Count;
            public override int NewListSize => newItems.Count;

            public override bool AreItemsTheSame(int oldItemPosition, int newItemPosition) =>
                string.Equals(oldItems[oldItemPosition].EventId, newItems[newItemPosition].EventId, StringComparison.Ordinal);

            public override bool AreContentsTheSame(int oldItemPosition, int newItemPosition) =>
                oldItems[oldItemPosition] == newItems[newItemPosition];
        }
    }
}
