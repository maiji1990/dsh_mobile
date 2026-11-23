using Android.Content.Res;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using DshMobile.Agent.Presentation;
using DshMobile.App.Ui;
using Google.Android.Material.Button;

namespace DshMobile.App.Credentials;

/// <summary>凭据状态列表适配器（只渲染配置态与脱敏掩码，绝不持有或展示明文密钥，N-05-01）。</summary>
internal sealed class CredentialsAdapter(
    Action<CredentialEntryView> onSetClicked,
    Action<CredentialEntryView> onClearClicked) : RecyclerView.Adapter
{
    private IReadOnlyList<CredentialEntryView> _items = [];

    public void Submit(IReadOnlyList<CredentialEntryView> items)
    {
        _items = items;
        NotifyDataSetChanged();
    }

    public override int ItemCount => _items.Count;

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var view = LayoutInflater.From(parent.Context)!.Inflate(Resource.Layout.item_credential_entry, parent, false)!;
        return new Holder(view, onSetClicked, onClearClicked);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        ((Holder)holder).Bind(_items[position]);
    }

    private sealed class Holder : RecyclerView.ViewHolder
    {
        private readonly TextView _name;
        private readonly TextView _status;
        private readonly TextView _ref;
        private readonly TextView _masked;
        private readonly MaterialButton _clearBtn;
        private readonly MaterialButton _setBtn;
        private CredentialEntryView? _bound;

        public Holder(
            View itemView,
            Action<CredentialEntryView> onSetClicked,
            Action<CredentialEntryView> onClearClicked) : base(itemView)
        {
            _name = itemView.FindViewById<TextView>(Resource.Id.cred_item_name)!;
            _status = itemView.FindViewById<TextView>(Resource.Id.cred_item_status)!;
            _ref = itemView.FindViewById<TextView>(Resource.Id.cred_item_ref)!;
            _masked = itemView.FindViewById<TextView>(Resource.Id.cred_item_masked)!;
            _clearBtn = itemView.FindViewById<MaterialButton>(Resource.Id.cred_item_clear)!;
            _setBtn = itemView.FindViewById<MaterialButton>(Resource.Id.cred_item_set)!;

            _setBtn.Click += (_, _) =>
            {
                if (_bound is not null)
                {
                    onSetClicked(_bound);
                }
            };
            _clearBtn.Click += (_, _) =>
            {
                if (_bound is not null)
                {
                    onClearClicked(_bound);
                }
            };
        }

        public void Bind(CredentialEntryView entry)
        {
            _bound = entry;
            var ctx = ItemView.Context!;
            _name.Text = entry.DisplayName;
            _ref.Text = string.IsNullOrWhiteSpace(entry.Source)
                ? entry.Ref
                : $"{entry.Ref} · {Rows.Fmt(ctx, Resource.String.credentials_source_fmt, entry.Source)}";

            _status.SetText(entry.Configured
                ? Resource.String.credentials_configured
                : Resource.String.credentials_not_configured);
            _status.SetTextColor(ColorStateList.ValueOf(
                entry.Configured
                    ? new Android.Graphics.Color(ctx.GetColor(Resource.Color.dshm_running))
                    : ResolveAttrColor(ctx, Android.Resource.Attribute.TextColorSecondary)));

            if (entry.Configured && !string.IsNullOrWhiteSpace(entry.Masked))
            {
                _masked.Visibility = ViewStates.Visible;
                _masked.Text = entry.Masked;
            }
            else
            {
                _masked.Visibility = ViewStates.Gone;
            }

            _setBtn.Enabled = entry.Writable;
            _clearBtn.Visibility = entry.Configured && entry.Writable ? ViewStates.Visible : ViewStates.Gone;
        }

        private static Android.Graphics.Color ResolveAttrColor(Android.Content.Context ctx, int attr)
        {
            using var tv = new Android.Util.TypedValue();
            ctx.Theme?.ResolveAttribute(attr, tv, true);
            return tv.ResourceId != 0
                ? new Android.Graphics.Color(ctx.GetColor(tv.ResourceId))
                : new Android.Graphics.Color(tv.Data);
        }
    }
}
