using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using DshMobile.Agent.Domain;
using DshMobile.App.Ui;

namespace DshMobile.App.Chat;

/// <summary>
/// UI Slice 2：斜杠命令浮层适配器（全 XML <c>item_chat_slash_row.xml</c>），
/// 展示 <c>/command</c>、说明摘要与动作状态徽章（立即执行 / 需补参数），支持输入前缀过滤。
/// </summary>
internal sealed class SlashCommandAdapter(Action<SlashCommandDescriptorView> onSelect) : RecyclerView.Adapter
{
    private IReadOnlyList<SlashCommandDescriptorView> _items = [];

    public void Submit(IReadOnlyList<SlashCommandDescriptorView> commands, string? filterQuery)
    {
        var q = filterQuery?.Trim().TrimStart('/') ?? string.Empty;
        _items = q.Length == 0
            ? commands
            : commands
                .Where(c =>
                    c.Name.TrimStart('/').Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(c.Description) && c.Description.Contains(q, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        NotifyDataSetChanged();
    }

    public override int ItemCount => _items.Count;

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var view = LayoutInflater.From(parent.Context)!.Inflate(Resource.Layout.item_chat_slash_row, parent, false)!;
        return new Holder(view, onSelect);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position) =>
        ((Holder)holder).Bind(_items[position]);

    private sealed class Holder : RecyclerView.ViewHolder
    {
        private readonly TextView _name;
        private readonly TextView _desc;
        private readonly TextView _badge;
        private SlashCommandDescriptorView? _bound;

        public Holder(View itemView, Action<SlashCommandDescriptorView> onSelect) : base(itemView)
        {
            _name = itemView.FindViewById<TextView>(Resource.Id.chat_slash_name)!;
            _desc = itemView.FindViewById<TextView>(Resource.Id.chat_slash_desc)!;
            _badge = itemView.FindViewById<TextView>(Resource.Id.chat_slash_badge)!;
            itemView.Click += (_, _) =>
            {
                if (_bound is not null)
                {
                    onSelect(_bound);
                }
            };
        }

        public void Bind(SlashCommandDescriptorView cmd)
        {
            _bound = cmd;
            var ctx = ItemView.Context!;
            var trimmed = cmd.Name.Trim();
            _name.Text = trimmed.StartsWith('/') ? trimmed : "/" + trimmed;

            if (string.IsNullOrWhiteSpace(cmd.Description))
            {
                _desc.Visibility = ViewStates.Gone;
            }
            else
            {
                _desc.Visibility = ViewStates.Visible;
                _desc.Text = SlashCommandLocalization.Describe(cmd.Name, cmd.Description);
            }

            var needsArgs = !string.IsNullOrWhiteSpace(cmd.InputHint);
            _badge.SetText(needsArgs ? Resource.String.chat_slash_action_args : Resource.String.chat_slash_action_run);
            _badge.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(
                new Android.Graphics.Color(Rows.Color(_badge, needsArgs ? Resource.Attribute.colorSurfaceContainerHighest : Resource.Attribute.colorPrimaryContainer)));
            _badge.SetTextColor(
                new Android.Graphics.Color(Rows.Color(_badge, needsArgs ? Resource.Attribute.colorOnSurfaceVariant : Resource.Attribute.colorOnPrimaryContainer)));
        }
    }
}
