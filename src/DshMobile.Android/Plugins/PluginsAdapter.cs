using Android.Views;
using AndroidX.RecyclerView.Widget;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Presentation;
using DshMobile.App.Ui;
using Google.Android.Material.Button;
using Google.Android.Material.MaterialSwitch;

namespace DshMobile.App.Plugins;

/// <summary>
/// 原生插件目录列表适配器（PR-07 / N-07-02 / ADR-0034）：
/// 渲染运行状态标签、核心保护与可配置胶囊、精简裁剪/非 Bundle/页面扩展提示条，支持点击卡片直达详情页。
/// </summary>
internal sealed class PluginsAdapter(
    Action<PluginEntryView> onOpenDetail,
    Action<PluginEntryView, bool> onToggleRequested,
    Action<PluginEntryView> onUninstallRequested) : RecyclerView.Adapter
{
    private IReadOnlyList<PluginEntryView> _items = [];
    private bool _isBusy;
    private bool _isFullWebUiCompatActive;

    public void Submit(IReadOnlyList<PluginEntryView> items, bool isBusy, bool isFullWebUiCompatActive = false)
    {
        _items = items;
        _isBusy = isBusy;
        _isFullWebUiCompatActive = isFullWebUiCompatActive;
        NotifyDataSetChanged();
    }

    public override int ItemCount => _items.Count;

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var view = LayoutInflater.From(parent.Context)!.Inflate(Resource.Layout.item_plugin_entry, parent, false)!;
        return new Holder(view, onOpenDetail, onToggleRequested, onUninstallRequested);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        ((Holder)holder).Bind(_items[position], _isBusy, _isFullWebUiCompatActive);
    }

    private sealed class Holder : RecyclerView.ViewHolder
    {
        private readonly TextView _name;
        private readonly TextView _desc;
        private readonly TextView _status;
        private readonly TextView _version;
        private readonly TextView _protectedBadge;
        private readonly TextView _configurableBadge;
        private readonly TextView _module;
        private readonly TextView _trimmedNotice;
        private readonly TextView _nonBundleNotice;
        private readonly TextView _webUiNotice;
        private readonly TextView _diagnostic;
        private readonly View _actionsRow;
        private readonly MaterialButton _uninstallBtn;
        private readonly MaterialSwitch _toggleSwitch;
        private PluginEntryView? _bound;
        private bool _isBusy;

        public Holder(
            View itemView,
            Action<PluginEntryView> onOpenDetail,
            Action<PluginEntryView, bool> onToggleRequested,
            Action<PluginEntryView> onUninstallRequested) : base(itemView)
        {
            _name = itemView.FindViewById<TextView>(Resource.Id.plugin_item_name)!;
            _desc = itemView.FindViewById<TextView>(Resource.Id.plugin_item_desc)!;
            _status = itemView.FindViewById<TextView>(Resource.Id.plugin_item_status)!;
            _version = itemView.FindViewById<TextView>(Resource.Id.plugin_item_version)!;
            _protectedBadge = itemView.FindViewById<TextView>(Resource.Id.plugin_item_protected)!;
            _configurableBadge = itemView.FindViewById<TextView>(Resource.Id.plugin_item_configurable)!;
            _module = itemView.FindViewById<TextView>(Resource.Id.plugin_item_module)!;
            _trimmedNotice = itemView.FindViewById<TextView>(Resource.Id.plugin_item_trimmed_notice)!;
            _nonBundleNotice = itemView.FindViewById<TextView>(Resource.Id.plugin_item_non_bundle_notice)!;
            _webUiNotice = itemView.FindViewById<TextView>(Resource.Id.plugin_item_webui_notice)!;
            _diagnostic = itemView.FindViewById<TextView>(Resource.Id.plugin_item_diagnostic)!;
            _actionsRow = itemView.FindViewById(Resource.Id.plugin_item_actions)!;
            _uninstallBtn = itemView.FindViewById<MaterialButton>(Resource.Id.plugin_item_uninstall)!;
            _toggleSwitch = itemView.FindViewById<MaterialSwitch>(Resource.Id.plugin_item_switch)!;

            itemView.Click += (_, _) =>
            {
                if (_bound is { } entry)
                {
                    onOpenDetail(entry);
                }
            };

            _toggleSwitch.Click += (_, _) =>
            {
                if (_bound is not { } entry || _isBusy || !entry.CanToggle || entry.IsTrimmedByNativeMode)
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

        public void Bind(PluginEntryView entry, bool isBusy, bool isFullWebUiCompatActive)
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
            _configurableBadge.Visibility = entry.HasConfigurableSettings ? ViewStates.Visible : ViewStates.Gone;
            BindStatus(ctx, entry);

            var showSwitch = !entry.IsProtected && (entry.CanToggle || entry.IsTrimmedByNativeMode);
            _toggleSwitch.Checked = entry.Enabled;
            _toggleSwitch.Enabled = entry.CanToggle && !entry.IsTrimmedByNativeMode && !isBusy;
            _toggleSwitch.Visibility = showSwitch ? ViewStates.Visible : ViewStates.Gone;
            _toggleSwitch.ContentDescription = display.ShortTitle;

            _trimmedNotice.Visibility = entry.IsTrimmedByNativeMode ? ViewStates.Visible : ViewStates.Gone;
            _nonBundleNotice.Visibility = entry.IsInstalledInProfile && !entry.HasBundleManifest
                ? ViewStates.Visible
                : ViewStates.Gone;
            _webUiNotice.Visibility = entry.HasCustomWebUi ? ViewStates.Visible : ViewStates.Gone;
            if (entry.HasCustomWebUi)
            {
                _webUiNotice.SetText(isFullWebUiCompatActive
                    ? Resource.String.plugins_custom_webui_compat_active_hint
                    : Resource.String.plugins_custom_webui_compat_hint);
            }

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
            var isNonBundle = string.Equals(entry.FiberPhase, "not-bundle", StringComparison.Ordinal) ||
                              (entry.IsInstalledInProfile && !entry.HasBundleManifest);
            var (textRes, tone) = entry.FiberPhase switch
            {
                "failed" => (Resource.String.plugins_status_failed, Rows.BadgeTone.Error),
                _ when isNonBundle => (Resource.String.plugins_status_not_bundle, Rows.BadgeTone.Warning),
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
