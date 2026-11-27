using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Presentation;
using DshMobile.App.Ui;
using Google.Android.Material.Button;

namespace DshMobile.App.Plugins;

/// <summary>
/// 远端插件发现列表适配器（ADR-0034 增强）：
/// 绑定 <c>item_plugin_catalog_entry.xml</c>，展示短标题、版本、官方/已安装状态胶囊、描述、
/// 完整包名与发布者/关键词元信息，支持点击整卡进入详情页并提供受控快捷安装按钮。
/// </summary>
internal sealed class PluginCatalogAdapter(
    Action<PluginCatalogEntryView> onOpenDetail,
    Action<PluginCatalogEntryView> onInstallRequested) : RecyclerView.Adapter
{
    private IReadOnlyList<PluginCatalogEntryView> _items = [];
    private bool _hostReady;
    private bool _isBusy;

    public void Submit(IReadOnlyList<PluginCatalogEntryView> items, bool hostReady, bool isBusy)
    {
        _items = items;
        _hostReady = hostReady;
        _isBusy = isBusy;
        NotifyDataSetChanged();
    }

    public override int ItemCount => _items.Count;

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var view = LayoutInflater.From(parent.Context)!.Inflate(Resource.Layout.item_plugin_catalog_entry, parent, false)!;
        return new CatalogEntryHolder(view, onOpenDetail, onInstallRequested);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        ((CatalogEntryHolder)holder).Bind(_items[position], _hostReady, _isBusy);
    }

    private sealed class CatalogEntryHolder : RecyclerView.ViewHolder
    {
        private readonly TextView _titleView;
        private readonly TextView _versionBadge;
        private readonly TextView _officialBadge;
        private readonly TextView _installedBadge;
        private readonly TextView _descriptionView;
        private readonly TextView _metaView;
        private readonly TextView _installHintView;
        private readonly View _spacerView;
        private readonly MaterialButton _installButton;

        private PluginCatalogEntryView? _boundEntry;
        private bool _canInvokeInstall;

        public CatalogEntryHolder(
            View itemView,
            Action<PluginCatalogEntryView> onOpenDetail,
            Action<PluginCatalogEntryView> onInstallRequested) : base(itemView)
        {
            _titleView = itemView.FindViewById<TextView>(Resource.Id.catalog_item_title)!;
            _versionBadge = itemView.FindViewById<TextView>(Resource.Id.catalog_item_version)!;
            _officialBadge = itemView.FindViewById<TextView>(Resource.Id.catalog_item_official)!;
            _installedBadge = itemView.FindViewById<TextView>(Resource.Id.catalog_item_installed_badge)!;
            _descriptionView = itemView.FindViewById<TextView>(Resource.Id.catalog_item_desc)!;
            _metaView = itemView.FindViewById<TextView>(Resource.Id.catalog_item_meta)!;
            _installHintView = itemView.FindViewById<TextView>(Resource.Id.catalog_item_install_hint)!;
            _spacerView = itemView.FindViewById(Resource.Id.catalog_item_spacer)!;
            _installButton = itemView.FindViewById<MaterialButton>(Resource.Id.catalog_item_install_btn)!;

            itemView.Click += (_, _) =>
            {
                if (_boundEntry is { } entry)
                {
                    onOpenDetail(entry);
                }
            };

            _installButton.Click += (_, _) =>
            {
                if (_boundEntry is { } entry && _canInvokeInstall)
                {
                    onInstallRequested(entry);
                }
            };
        }

        public void Bind(PluginCatalogEntryView entry, bool hostReady, bool isBusy)
        {
            _boundEntry = entry;
            var context = ItemView.Context!;
            var formatted = PluginDisplayFormatter.FormatCatalog(entry);

            _titleView.Text = formatted.ShortTitle;

            if (!string.IsNullOrWhiteSpace(entry.Version))
            {
                _versionBadge.Visibility = ViewStates.Visible;
                _versionBadge.Text = $"v{entry.Version.Trim()}";
            }
            else
            {
                _versionBadge.Visibility = ViewStates.Gone;
            }

            _officialBadge.Visibility = entry.IsOfficialScope ? ViewStates.Visible : ViewStates.Gone;
            _installedBadge.Visibility = entry.IsInstalled ? ViewStates.Visible : ViewStates.Gone;

            _descriptionView.Text = !string.IsNullOrWhiteSpace(formatted.Description)
                ? formatted.Description
                : context.GetString(Resource.String.plugins_catalog_no_desc);

            _metaView.Text = BuildMetadataSummary(context, entry);

            if (entry.IsInstalled)
            {
                _canInvokeInstall = false;
                _installButton.SetText(Resource.String.plugins_catalog_action_installed);
                _installButton.Enabled = false;
                if (!string.IsNullOrWhiteSpace(entry.InstalledVersion))
                {
                    SetInstallHint(Rows.Fmt(context, Resource.String.plugins_catalog_installed_version_fmt, entry.InstalledVersion.Trim()));
                }
                else
                {
                    SetInstallHint(null);
                }
                return;
            }

            if (!hostReady)
            {
                _canInvokeInstall = false;
                _installButton.SetText(Resource.String.plugins_catalog_action_install);
                _installButton.Enabled = false;
                SetInstallHint(context.GetString(Resource.String.plugins_catalog_action_need_host));
                return;
            }

            var hasValidSpec = PluginPackageValidator.IsValidInstallSpec(entry.RecommendedInstallSpec);
            _canInvokeInstall = hasValidSpec && !isBusy;
            _installButton.SetText(Resource.String.plugins_catalog_action_install);
            _installButton.Enabled = _canInvokeInstall;
            SetInstallHint(null);
        }

        private void SetInstallHint(string? hint)
        {
            if (string.IsNullOrWhiteSpace(hint))
            {
                _installHintView.Visibility = ViewStates.Gone;
                _installHintView.Text = string.Empty;
                _spacerView.Visibility = ViewStates.Visible;
            }
            else
            {
                _installHintView.Visibility = ViewStates.Visible;
                _installHintView.Text = hint;
                _spacerView.Visibility = ViewStates.Gone;
            }
        }

        private static string BuildMetadataSummary(Android.Content.Context context, PluginCatalogEntryView entry)
        {
            var segments = new List<string>(3) { entry.PackageName };
            if (!string.IsNullOrWhiteSpace(entry.Publisher))
            {
                segments.Add(Rows.Fmt(context, Resource.String.plugins_catalog_publisher_fmt, entry.Publisher.Trim()));
            }
            if (entry.Keywords is { Count: > 0 })
            {
                var topKeywords = entry.Keywords
                    .Select(keyword => keyword?.Trim())
                    .Where(keyword => !string.IsNullOrEmpty(keyword))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToArray();
                if (topKeywords.Length > 0)
                {
                    segments.Add(string.Join(", ", topKeywords));
                }
            }
            return string.Join(" · ", segments);
        }
    }
}
