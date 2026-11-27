using Android.Views;
using AndroidX.RecyclerView.Widget;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Presentation;
using DshMobile.App.Ui;
using Google.Android.Material.Button;
using Google.Android.Material.MaterialSwitch;

namespace DshMobile.App.Plugins;

/// <summary>
/// 原生插件目录列表适配器（PR-07 / N-07-02 / ADR-0033）：
/// 仅渲染 L0 状态标签、精简短模块主标题 + 中文功能副描述 + 去重包名，无动态配置表单与内嵌网页视图。
/// </summary>
internal sealed class PluginsAdapter(
    Action<PluginEntryView, bool> onToggleRequested,
    Action<PluginEntryView> onUninstallRequested) : RecyclerView.Adapter
{
    private IReadOnlyList<PluginEntryView> _items = [];
    private bool _isBusy;

    public void Submit(IReadOnlyList<PluginEntryView> items, bool isBusy)
    {
        _items = items;
        _isBusy = isBusy;
        NotifyDataSetChanged();
    }

    public override int ItemCount => _items.Count;

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var view = LayoutInflater.From(parent.Context)!.Inflate(Resource.Layout.item_plugin_entry, parent, false)!;
        return new Holder(view, onToggleRequested, onUninstallRequested);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        ((Holder)holder).Bind(_items[position], _isBusy);
    }

    private sealed class Holder : RecyclerView.ViewHolder
    {
        private readonly TextView _name;
        private readonly TextView _desc;
        private readonly TextView _status;
        private readonly TextView _version;
        private readonly TextView _protectedBadge;
        private readonly TextView _module;
        private readonly TextView _webUiNotice;
        private readonly TextView _diagnostic;
        private readonly View _actionsRow;
        private readonly MaterialButton _uninstallBtn;
        private readonly MaterialSwitch _toggleSwitch;
        private PluginEntryView? _bound;
        private bool _isBusy;

        public Holder(
            View itemView,
            Action<PluginEntryView, bool> onToggleRequested,
            Action<PluginEntryView> onUninstallRequested) : base(itemView)
        {
            _name = itemView.FindViewById<TextView>(Resource.Id.plugin_item_name)!;
            _desc = itemView.FindViewById<TextView>(Resource.Id.plugin_item_desc)!;
            _status = itemView.FindViewById<TextView>(Resource.Id.plugin_item_status)!;
            _version = itemView.FindViewById<TextView>(Resource.Id.plugin_item_version)!;
            _protectedBadge = itemView.FindViewById<TextView>(Resource.Id.plugin_item_protected)!;
            _module = itemView.FindViewById<TextView>(Resource.Id.plugin_item_module)!;
            _webUiNotice = itemView.FindViewById<TextView>(Resource.Id.plugin_item_webui_notice)!;
            _diagnostic = itemView.FindViewById<TextView>(Resource.Id.plugin_item_diagnostic)!;
            _actionsRow = itemView.FindViewById(Resource.Id.plugin_item_actions)!;
            _uninstallBtn = itemView.FindViewById<MaterialButton>(Resource.Id.plugin_item_uninstall)!;
            _toggleSwitch = itemView.FindViewById<MaterialSwitch>(Resource.Id.plugin_item_switch)!;

            _toggleSwitch.Click += (_, _) =>
            {
                if (_bound is not { } entry || _isBusy || !entry.CanToggle)
                {
                    if (_bound is not null)
                    {
                        _toggleSwitch.Checked = _bound.Enabled;
                    }
                    return;
                }
                var desiredEnabled = _toggleSwitch.Checked;
                // 先恢复既有开关外观，仅在用户确认弹窗后再提交变更。
                _toggleSwitch.Checked = entry.Enabled;
                onToggleRequested(entry, desiredEnabled);
            };

            _uninstallBtn.Click += (_, _) =>
            {
                if (_bound is { } entry && !_isBusy && entry.CanUninstall)
                {
                    onUninstallRequested(entry);
                }
            };
        }

        public void Bind(PluginEntryView entry, bool isBusy)
        {
            _bound = entry;
            _isBusy = isBusy;
            var ctx = ItemView.Context!;
            var display = PluginDisplayFormatter.Format(entry);

            _name.Text = display.ShortTitle;
            if (!string.IsNullOrWhiteSpace(display.Description))
            {
                _desc.Visibility = ViewStates.Visible;
                _desc.Text = display.Description;
            }
            else
            {
                _desc.Visibility = ViewStates.Gone;
            }
            _module.Text = display.Meta;

            if (!string.IsNullOrWhiteSpace(entry.Version))
            {
                _version.Visibility = ViewStates.Visible;
                _version.Text = $"v{entry.Version}";
            }
            else
            {
                _version.Visibility = ViewStates.Gone;
            }

            _protectedBadge.Visibility = entry.IsProtected ? ViewStates.Visible : ViewStates.Gone;
            BindStatus(ctx, entry);

            _toggleSwitch.Checked = entry.Enabled;
            _toggleSwitch.Enabled = entry.CanToggle && !isBusy;
            _toggleSwitch.Visibility = entry.CanToggle ? ViewStates.Visible : ViewStates.Gone;
            _toggleSwitch.ContentDescription = display.ShortTitle;

            _webUiNotice.Visibility = entry.HasCustomWebUi ? ViewStates.Visible : ViewStates.Gone;

            if (!string.IsNullOrWhiteSpace(entry.DiagnosticSummary))
            {
                _diagnostic.Visibility = ViewStates.Visible;
                _diagnostic.Text = entry.DiagnosticSummary;
            }
            else
            {
                _diagnostic.Visibility = ViewStates.Gone;
            }

            _actionsRow.Visibility = entry.CanUninstall ? ViewStates.Visible : ViewStates.Gone;
            _uninstallBtn.Enabled = entry.CanUninstall && !isBusy;
        }

        private void BindStatus(Android.Content.Context ctx, PluginEntryView entry)
        {
            var (textRes, tone) = entry.FiberPhase switch
            {
                "failed" => (Resource.String.plugins_status_failed, Rows.BadgeTone.Error),
                "pending-restart" => (Resource.String.plugins_status_pending_restart, Rows.BadgeTone.Primary),
                "loading" or "pending" when entry.Enabled => (Resource.String.plugins_status_loading, Rows.BadgeTone.Primary),
                "active" when entry.Enabled => (Resource.String.plugins_status_active, Rows.BadgeTone.Success),
                _ when !entry.Enabled => (Resource.String.plugins_status_disabled, Rows.BadgeTone.Neutral),
                _ => (Resource.String.plugins_status_inactive, Rows.BadgeTone.Neutral),
            };
            Rows.SetBadge(_status, ctx.GetString(textRes), tone);
        }
    }
}
