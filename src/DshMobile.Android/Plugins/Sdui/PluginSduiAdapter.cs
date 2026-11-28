using System.Globalization;
using System.Text.Json;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Sdui;
using DshMobile.App.Plugins;
using DshMobile.App.Ui;
using Google.Android.Material.Button;
using Google.Android.Material.Card;
using Google.Android.Material.Dialog;
using Google.Android.Material.MaterialSwitch;
using Google.Android.Material.ProgressIndicator;

namespace DshMobile.App.Plugins.Sdui;

/// <summary>
/// 插件 SDUI 宿主的回调契约。适配器只负责把强类型渲染项绑定到既有 XML 控件，
/// 所有业务动作、配置写入与外部链接处理都交回宿主；回调均在主线程触发。
/// </summary>
internal interface IPluginSduiAdapterCallbacks
{
    /// <summary>执行按钮或列表行动作；<paramref name="item"/> 为列表行上下文，非行内动作为 <see langword="null"/>。</summary>
    void OnAction(string actionId, JsonElement? item);

    /// <summary>分段开关被点击：提交 UI 状态键、选中值与可选变化动作。</summary>
    void OnSegmentedChanged(string stateKey, string value, string? onChangeActionId);

    /// <summary>外部链接按钮被点击。</summary>
    void OnExternalLink(string url);

    /// <summary>官方配置桥布尔字段被切换。</summary>
    void OnConfigBoolean(string settingsNamespace, PluginConfigFieldView field, bool value);

    /// <summary>官方配置桥文本/数值/密钥/JSON 字段请求编辑。</summary>
    void OnConfigEdit(string settingsNamespace, PluginConfigFieldView field);

    /// <summary>官方配置桥单选字段请求选择候选项。</summary>
    void OnConfigSelect(string settingsNamespace, PluginConfigFieldView field);

    /// <summary>官方配置桥字段请求恢复默认值。</summary>
    void OnConfigReset(string settingsNamespace, PluginConfigFieldView field);
}

/// <summary>
/// 插件声明式 SDUI 多 ViewType 适配器（受控 L2）：
/// 所有控件均来自预先定义的 XML 布局，代码中零命令式控件构造；
/// 渲染项只做只读投影，动作执行与配置写入通过 <see cref="IPluginSduiAdapterCallbacks"/> 交回宿主。
/// </summary>
internal sealed class PluginSduiAdapter : RecyclerView.Adapter
{
    private const int ViewTypeStatusBanner = 0;
    private const int ViewTypeMetricGrid = 1;
    private const int ViewTypeQuotaBar = 2;
    private const int ViewTypeKeyValueList = 3;
    private const int ViewTypeSegmentedSwitch = 4;
    private const int ViewTypeListHeader = 5;
    private const int ViewTypeListEmpty = 6;
    private const int ViewTypeListRow = 7;
    private const int ViewTypeActionBar = 8;
    private const int ViewTypeExternalLink = 9;
    private const int ViewTypeSettingsHeader = 10;
    private const int ViewTypeConfigBoolean = 11;
    private const int ViewTypeConfigNumber = 12;
    private const int ViewTypeConfigText = 13;
    private const int ViewTypeConfigSelect = 14;
    private const int ViewTypeConfigSecret = 15;
    private const int ViewTypeConfigJson = 16;
    private const int ViewTypeSourceError = 17;
    private const int ViewTypeActionResult = 18;

    private readonly IPluginSduiAdapterCallbacks _callbacks;
    private IReadOnlyList<SduiRenderItem> _items = [];

    /// <summary>创建适配器；<paramref name="callbacks"/> 负责所有业务回调。</summary>
    public PluginSduiAdapter(IPluginSduiAdapterCallbacks callbacks)
    {
        _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
    }

    /// <summary>用新的渲染项快照替换列表内容；空集合表示当前无内容。</summary>
    public void Submit(IReadOnlyList<SduiRenderItem> items)
    {
        _items = items ?? [];
        NotifyDataSetChanged();
    }

    /// <inheritdoc />
    public override int ItemCount => _items.Count;

    /// <inheritdoc />
    public override int GetItemViewType(int position) => _items[position] switch
    {
        SduiStatusBannerRenderItem => ViewTypeStatusBanner,
        SduiMetricGridRenderItem => ViewTypeMetricGrid,
        SduiQuotaBarRenderItem => ViewTypeQuotaBar,
        SduiKeyValueListRenderItem => ViewTypeKeyValueList,
        SduiSegmentedSwitchRenderItem => ViewTypeSegmentedSwitch,
        SduiListHeaderRenderItem => ViewTypeListHeader,
        SduiListEmptyRenderItem => ViewTypeListEmpty,
        SduiListRowRenderItem => ViewTypeListRow,
        SduiActionBarRenderItem => ViewTypeActionBar,
        SduiExternalLinkRenderItem => ViewTypeExternalLink,
        SduiSettingsBridgeHeaderRenderItem => ViewTypeSettingsHeader,
        SduiSettingsBridgeFieldRenderItem fieldItem => fieldItem.Field.Kind switch
        {
            PluginConfigFieldKind.Boolean => ViewTypeConfigBoolean,
            PluginConfigFieldKind.Number => ViewTypeConfigNumber,
            PluginConfigFieldKind.SingleSelect => ViewTypeConfigSelect,
            PluginConfigFieldKind.Secret => ViewTypeConfigSecret,
            PluginConfigFieldKind.JsonFallback => ViewTypeConfigJson,
            _ => ViewTypeConfigText,
        },
        SduiActionResultRenderItem => ViewTypeActionResult,
        SduiSourceErrorRenderItem => ViewTypeSourceError,
        _ => ViewTypeSourceError,
    };

    /// <inheritdoc />
    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var itemView = Inflate(parent, viewType switch
        {
            ViewTypeStatusBanner => Resource.Layout.item_sdui_status_banner,
            ViewTypeMetricGrid => Resource.Layout.item_sdui_metric_grid,
            ViewTypeQuotaBar => Resource.Layout.item_sdui_quota_bar,
            ViewTypeKeyValueList => Resource.Layout.item_sdui_key_value_list,
            ViewTypeSegmentedSwitch => Resource.Layout.item_sdui_segmented_switch,
            ViewTypeListHeader => Resource.Layout.item_sdui_list_header,
            ViewTypeListEmpty => Resource.Layout.item_sdui_list_empty,
            ViewTypeListRow => Resource.Layout.item_sdui_list_row,
            ViewTypeActionBar => Resource.Layout.item_sdui_action_bar,
            ViewTypeExternalLink => Resource.Layout.item_sdui_external_link,
            ViewTypeSettingsHeader => Resource.Layout.item_sdui_settings_bridge_header,
            ViewTypeConfigBoolean => Resource.Layout.item_plugin_config_bool,
            ViewTypeConfigNumber => Resource.Layout.item_plugin_config_number,
            ViewTypeConfigSelect => Resource.Layout.item_plugin_config_select,
            ViewTypeConfigSecret => Resource.Layout.item_plugin_config_secret,
            ViewTypeConfigJson => Resource.Layout.item_plugin_config_json,
            ViewTypeConfigText => Resource.Layout.item_plugin_config_text,
            ViewTypeActionResult => Resource.Layout.item_sdui_action_result,
            _ => Resource.Layout.item_sdui_source_error,
        });

        return viewType switch
        {
            ViewTypeStatusBanner => new StatusBannerHolder(itemView),
            ViewTypeMetricGrid => new MetricGridHolder(itemView),
            ViewTypeQuotaBar => new QuotaBarHolder(itemView),
            ViewTypeKeyValueList => new KeyValueListHolder(itemView),
            ViewTypeSegmentedSwitch => new SegmentedSwitchHolder(itemView, _callbacks),
            ViewTypeListHeader => new ListHeaderHolder(itemView),
            ViewTypeListEmpty => new ListEmptyHolder(itemView),
            ViewTypeListRow => new ListRowHolder(itemView, _callbacks),
            ViewTypeActionBar => new ActionBarHolder(itemView, _callbacks),
            ViewTypeExternalLink => new ExternalLinkHolder(itemView, _callbacks),
            ViewTypeSettingsHeader => new SettingsHeaderHolder(itemView),
            ViewTypeConfigBoolean => new BooleanFieldHolder(itemView, _callbacks),
            ViewTypeConfigNumber => new NumberFieldHolder(itemView, _callbacks),
            ViewTypeConfigSelect => new SingleSelectFieldHolder(itemView, _callbacks),
            ViewTypeConfigSecret => new SecretFieldHolder(itemView, _callbacks),
            ViewTypeConfigJson => new JsonFieldHolder(itemView, _callbacks),
            ViewTypeConfigText => new TextFieldHolder(itemView, _callbacks),
            ViewTypeActionResult => new ActionResultHolder(itemView),
            _ => new SourceErrorHolder(itemView),
        };
    }

    /// <inheritdoc />
    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        switch (_items[position])
        {
            case SduiStatusBannerRenderItem item when holder is StatusBannerHolder banner:
                banner.Bind(item);
                break;

            case SduiMetricGridRenderItem item when holder is MetricGridHolder grid:
                grid.Bind(item);
                break;

            case SduiQuotaBarRenderItem item when holder is QuotaBarHolder quota:
                quota.Bind(item);
                break;

            case SduiKeyValueListRenderItem item when holder is KeyValueListHolder keyValue:
                keyValue.Bind(item);
                break;

            case SduiSegmentedSwitchRenderItem item when holder is SegmentedSwitchHolder segmented:
                segmented.Bind(item);
                break;

            case SduiListHeaderRenderItem item when holder is ListHeaderHolder header:
                header.Bind(item);
                break;

            case SduiListEmptyRenderItem item when holder is ListEmptyHolder empty:
                empty.Bind(item);
                break;

            case SduiListRowRenderItem item when holder is ListRowHolder row:
                row.Bind(item);
                break;

            case SduiActionBarRenderItem item when holder is ActionBarHolder actionBar:
                actionBar.Bind(item);
                break;

            case SduiExternalLinkRenderItem item when holder is ExternalLinkHolder externalLink:
                externalLink.Bind(item);
                break;

            case SduiSettingsBridgeHeaderRenderItem item when holder is SettingsHeaderHolder settingsHeader:
                settingsHeader.Bind(item);
                break;

            case SduiSettingsBridgeFieldRenderItem item when holder is ConfigFieldHolderBase configField:
                configField.Bind(item.Namespace, item.Field);
                break;

            case SduiActionResultRenderItem item when holder is ActionResultHolder actionResult:
                actionResult.Bind(item);
                break;

            case SduiSourceErrorRenderItem item when holder is SourceErrorHolder sourceError:
                sourceError.Bind(item);
                break;
        }
    }

    private static View Inflate(ViewGroup parent, int layoutRes) =>
        LayoutInflater.From(parent.Context)!.Inflate(layoutRes, parent, false)!;

    /// <summary>把百分比限制为 0..100 的整数进度。</summary>
    private static int ClampPercent(double value) =>
        (int)Math.Clamp(Math.Round(value), 0d, 100d);

    /// <summary>按动作声明决定直接回调，还是先弹出二次确认对话框。</summary>
    private static void InvokeAction(
        Context context,
        IPluginSduiAdapterCallbacks callbacks,
        SduiButtonView button,
        JsonElement? item)
    {
        if (!button.Confirm)
        {
            callbacks.OnAction(button.ActionId, item);
            return;
        }

        var builder = new MaterialAlertDialogBuilder(context);
        var title = string.IsNullOrWhiteSpace(button.ConfirmTitle) ? button.Label : button.ConfirmTitle;
        if (!string.IsNullOrWhiteSpace(title))
        {
            builder.SetTitle(title);
        }
        if (!string.IsNullOrWhiteSpace(button.ConfirmMessage))
        {
            builder.SetMessage(button.ConfirmMessage);
        }

        var positiveText = string.IsNullOrWhiteSpace(button.Label)
            ? context.GetString(Android.Resource.String.Ok)
            : button.Label;
        builder.SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { });
        builder.SetPositiveButton(positiveText, (_, _) => callbacks.OnAction(button.ActionId, item));
        builder.Show();
    }

    /// <summary>把第 4 个及之后的动作放进兼容主题的溢出菜单。</summary>
    private static void ShowOverflowMenu(
        Context context,
        View anchor,
        IPluginSduiAdapterCallbacks callbacks,
        IReadOnlyList<SduiButtonView> actions,
        int startIndex,
        JsonElement? item)
    {
        var popup = new AndroidX.AppCompat.Widget.PopupMenu(context, anchor);
        var menu = popup.Menu;
        if (menu is null)
        {
            return;
        }

        for (var index = startIndex; index < actions.Count; index++)
        {
            var action = actions[index];
            var label = string.IsNullOrWhiteSpace(action.Label) ? action.ActionId : action.Label;
            menu.Add(0, index, index, label);
        }

        popup.MenuItemClick += (_, args) =>
        {
            var index = args.Item?.ItemId ?? -1;
            if (index >= startIndex && index < actions.Count)
            {
                InvokeAction(context, callbacks, actions[index], item);
            }
        };
        popup.Show();
    }

    private abstract class SduiHolderBase(View itemView) : RecyclerView.ViewHolder(itemView)
    {
    }

    /// <summary>状态横幅：按语义色调切换容器背景与文字对比色。</summary>
    private sealed class StatusBannerHolder(View itemView) : SduiHolderBase(itemView)
    {
        private readonly MaterialCardView _card = itemView.FindViewById<MaterialCardView>(Resource.Id.sdui_banner_card)!;
        private readonly TextView _title = itemView.FindViewById<TextView>(Resource.Id.sdui_banner_title)!;
        private readonly TextView _subtitle = itemView.FindViewById<TextView>(Resource.Id.sdui_banner_subtitle)!;

        public void Bind(SduiStatusBannerRenderItem item)
        {
            _title.Text = item.Title;
            _subtitle.Text = item.Subtitle;
            _subtitle.Visibility = string.IsNullOrWhiteSpace(item.Subtitle)
                ? ViewStates.Gone
                : ViewStates.Visible;

            var (backgroundAttr, titleAttr, subtitleAttr) = item.Tone.Trim().ToLowerInvariant() switch
            {
                "warn" => (
                    Resource.Attribute.colorTertiaryContainer,
                    Resource.Attribute.colorOnTertiaryContainer,
                    Resource.Attribute.colorOnTertiaryContainer),
                "success" => (
                    Resource.Attribute.colorSecondaryContainer,
                    Resource.Attribute.colorOnSecondaryContainer,
                    Resource.Attribute.colorOnSecondaryContainer),
                "danger" => (
                    Resource.Attribute.colorErrorContainer,
                    Resource.Attribute.colorOnErrorContainer,
                    Resource.Attribute.colorOnErrorContainer),
                _ => (
                    Resource.Attribute.colorSurfaceContainerHigh,
                    Resource.Attribute.colorOnSurface,
                    Resource.Attribute.colorOnSurfaceVariant),
            };

            _card.SetCardBackgroundColor(new Android.Graphics.Color(Rows.Color(_card, backgroundAttr)));
            _title.SetTextColor(new Android.Graphics.Color(Rows.Color(_title, titleAttr)));
            _subtitle.SetTextColor(new Android.Graphics.Color(Rows.Color(_subtitle, subtitleAttr)));
        }
    }

    /// <summary>指标网格：最多绑定 4 个静态槽位，多余槽位隐藏。</summary>
    private sealed class MetricGridHolder(View itemView) : SduiHolderBase(itemView)
    {
        private readonly LinearLayout[] _slots =
        [
            itemView.FindViewById<LinearLayout>(Resource.Id.sdui_metric_slot_0)!,
            itemView.FindViewById<LinearLayout>(Resource.Id.sdui_metric_slot_1)!,
            itemView.FindViewById<LinearLayout>(Resource.Id.sdui_metric_slot_2)!,
            itemView.FindViewById<LinearLayout>(Resource.Id.sdui_metric_slot_3)!,
        ];

        private readonly TextView[] _labels =
        [
            itemView.FindViewById<TextView>(Resource.Id.sdui_metric_label_0)!,
            itemView.FindViewById<TextView>(Resource.Id.sdui_metric_label_1)!,
            itemView.FindViewById<TextView>(Resource.Id.sdui_metric_label_2)!,
            itemView.FindViewById<TextView>(Resource.Id.sdui_metric_label_3)!,
        ];

        private readonly TextView[] _values =
        [
            itemView.FindViewById<TextView>(Resource.Id.sdui_metric_value_0)!,
            itemView.FindViewById<TextView>(Resource.Id.sdui_metric_value_1)!,
            itemView.FindViewById<TextView>(Resource.Id.sdui_metric_value_2)!,
            itemView.FindViewById<TextView>(Resource.Id.sdui_metric_value_3)!,
        ];

        public void Bind(SduiMetricGridRenderItem item)
        {
            for (var index = 0; index < _slots.Length; index++)
            {
                if (index >= item.Items.Count)
                {
                    _slots[index].Visibility = ViewStates.Gone;
                    continue;
                }

                var metric = item.Items[index];
                _slots[index].Visibility = ViewStates.Visible;
                _labels[index].Text = metric.Label;
                _values[index].Text = metric.ValueText;
                _values[index].Typeface = metric.Monospace ? Typeface.Monospace : Typeface.Default;
            }
        }
    }

    /// <summary>额度进度：百分比、状态与重置时间全部来自渲染项。</summary>
    private sealed class QuotaBarHolder(View itemView) : SduiHolderBase(itemView)
    {
        private readonly TextView _label = itemView.FindViewById<TextView>(Resource.Id.sdui_quota_label)!;
        private readonly TextView _percent = itemView.FindViewById<TextView>(Resource.Id.sdui_quota_percent)!;
        private readonly LinearProgressIndicator _progress =
            itemView.FindViewById<LinearProgressIndicator>(Resource.Id.sdui_quota_progress)!;
        private readonly TextView _status = itemView.FindViewById<TextView>(Resource.Id.sdui_quota_status)!;
        private readonly TextView _reset = itemView.FindViewById<TextView>(Resource.Id.sdui_quota_reset)!;

        public void Bind(SduiQuotaBarRenderItem item)
        {
            _label.Text = item.Label;
            _percent.Text = $"{item.Percent:0.#}%";
            _progress.Progress = (int)Math.Clamp(Math.Round(item.Percent), 0, 100);

            _status.Text = item.StatusText;
            _status.Visibility = string.IsNullOrWhiteSpace(item.StatusText)
                ? ViewStates.Gone
                : ViewStates.Visible;
            _reset.Text = item.ResetAtText;
            _reset.Visibility = string.IsNullOrWhiteSpace(item.ResetAtText)
                ? ViewStates.Gone
                : ViewStates.Visible;
        }
    }

    /// <summary>键值对列表：最多绑定 6 行，空行隐藏。</summary>
    private sealed class KeyValueListHolder(View itemView) : SduiHolderBase(itemView)
    {
        private readonly LinearLayout[] _rows =
        [
            itemView.FindViewById<LinearLayout>(Resource.Id.sdui_kv_row_0)!,
            itemView.FindViewById<LinearLayout>(Resource.Id.sdui_kv_row_1)!,
            itemView.FindViewById<LinearLayout>(Resource.Id.sdui_kv_row_2)!,
            itemView.FindViewById<LinearLayout>(Resource.Id.sdui_kv_row_3)!,
            itemView.FindViewById<LinearLayout>(Resource.Id.sdui_kv_row_4)!,
            itemView.FindViewById<LinearLayout>(Resource.Id.sdui_kv_row_5)!,
        ];

        private readonly TextView[] _labels =
        [
            itemView.FindViewById<TextView>(Resource.Id.sdui_kv_label_0)!,
            itemView.FindViewById<TextView>(Resource.Id.sdui_kv_label_1)!,
            itemView.FindViewById<TextView>(Resource.Id.sdui_kv_label_2)!,
            itemView.FindViewById<TextView>(Resource.Id.sdui_kv_label_3)!,
            itemView.FindViewById<TextView>(Resource.Id.sdui_kv_label_4)!,
            itemView.FindViewById<TextView>(Resource.Id.sdui_kv_label_5)!,
        ];

        private readonly TextView[] _values =
        [
            itemView.FindViewById<TextView>(Resource.Id.sdui_kv_value_0)!,
            itemView.FindViewById<TextView>(Resource.Id.sdui_kv_value_1)!,
            itemView.FindViewById<TextView>(Resource.Id.sdui_kv_value_2)!,
            itemView.FindViewById<TextView>(Resource.Id.sdui_kv_value_3)!,
            itemView.FindViewById<TextView>(Resource.Id.sdui_kv_value_4)!,
            itemView.FindViewById<TextView>(Resource.Id.sdui_kv_value_5)!,
        ];

        public void Bind(SduiKeyValueListRenderItem item)
        {
            for (var index = 0; index < _rows.Length; index++)
            {
                if (index >= item.Items.Count)
                {
                    _rows[index].Visibility = ViewStates.Gone;
                    continue;
                }

                var entry = item.Items[index];
                _rows[index].Visibility = ViewStates.Visible;
                _labels[index].Text = entry.Label;
                _values[index].Text = entry.ValueText;
            }
        }
    }

    /// <summary>
    /// 分段开关：最多绑定 5 个静态按钮。绑定阶段先静默设置选中项，
    /// 用户点击时才回调，避免 RecyclerView 复用触发 <c>ButtonChecked</c> 抖动。
    /// </summary>
    private sealed class SegmentedSwitchHolder : SduiHolderBase
    {
        private readonly TextView _label;
        private readonly MaterialButtonToggleGroup _group;
        private readonly MaterialButton[] _buttons;
        private readonly IPluginSduiAdapterCallbacks _callbacks;

        private SduiSegmentedSwitchRenderItem? _item;
        private bool _suppressSelectionCallback;

        public SegmentedSwitchHolder(View itemView, IPluginSduiAdapterCallbacks callbacks)
            : base(itemView)
        {
            _callbacks = callbacks;
            _label = itemView.FindViewById<TextView>(Resource.Id.sdui_seg_label)!;
            _group = itemView.FindViewById<MaterialButtonToggleGroup>(Resource.Id.sdui_seg_group)!;
            _buttons =
            [
                itemView.FindViewById<MaterialButton>(Resource.Id.sdui_seg_btn_0)!,
                itemView.FindViewById<MaterialButton>(Resource.Id.sdui_seg_btn_1)!,
                itemView.FindViewById<MaterialButton>(Resource.Id.sdui_seg_btn_2)!,
                itemView.FindViewById<MaterialButton>(Resource.Id.sdui_seg_btn_3)!,
                itemView.FindViewById<MaterialButton>(Resource.Id.sdui_seg_btn_4)!,
            ];

            for (var index = 0; index < _buttons.Length; index++)
            {
                var buttonIndex = index;
                _buttons[buttonIndex].Click += (_, _) => HandleClick(buttonIndex);
            }
        }

        public void Bind(SduiSegmentedSwitchRenderItem item)
        {
            _item = item;
            _label.Text = item.Label;
            _label.Visibility = string.IsNullOrWhiteSpace(item.Label)
                ? ViewStates.Gone
                : ViewStates.Visible;

            var selectedIndex = -1;
            for (var index = 0; index < _buttons.Length; index++)
            {
                var button = _buttons[index];
                var visible = index < item.Options.Count;
                button.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;
                if (!visible)
                {
                    continue;
                }

                var option = item.Options[index];
                button.Text = string.IsNullOrWhiteSpace(option.Label) ? option.Value : option.Label;
                if (selectedIndex < 0 &&
                    string.Equals(option.Value, item.SelectedValue, StringComparison.Ordinal))
                {
                    selectedIndex = index;
                }
            }

            if (item.Options.Count == 0)
            {
                return;
            }

            // 程序化 Check 只更新视觉选中态，不触发 Click；显式开关仅用于防御性屏蔽回调。
            _suppressSelectionCallback = true;
            try
            {
                _group.Check(_buttons[selectedIndex >= 0 ? selectedIndex : 0].Id);
            }
            finally
            {
                _suppressSelectionCallback = false;
            }
        }

        private void HandleClick(int index)
        {
            if (_suppressSelectionCallback || _item is not { } item)
            {
                return;
            }

            if (index < 0 || index >= item.Options.Count)
            {
                return;
            }

            _callbacks.OnSegmentedChanged(item.StateKey, item.Options[index].Value, item.OnChangeActionId);
        }
    }

    /// <summary>列表小节标题。</summary>
    private sealed class ListHeaderHolder(View itemView) : SduiHolderBase(itemView)
    {
        private readonly TextView _title = itemView.FindViewById<TextView>(Resource.Id.sdui_list_header_title)!;

        public void Bind(SduiListHeaderRenderItem item)
        {
            _title.Text = item.Title;
        }
    }

    /// <summary>列表空状态。</summary>
    private sealed class ListEmptyHolder(View itemView) : SduiHolderBase(itemView)
    {
        private readonly TextView _title = itemView.FindViewById<TextView>(Resource.Id.sdui_list_empty_title)!;
        private readonly TextView _text = itemView.FindViewById<TextView>(Resource.Id.sdui_list_empty_text)!;

        public void Bind(SduiListEmptyRenderItem item)
        {
            _title.Text = item.Title;
            _text.Text = item.EmptyText;
            _text.Visibility = string.IsNullOrWhiteSpace(item.EmptyText)
                ? ViewStates.Gone
                : ViewStates.Visible;
        }
    }

    /// <summary>
    /// 列表行：最多 3 个直显动作，其余进入溢出菜单；
    /// 每个动作都保留该行原始 <see cref="JsonElement"/> 作为动作上下文。
    /// </summary>
    private sealed class ListRowHolder : SduiHolderBase
    {
        private readonly TextView _title;
        private readonly TextView _subtitle;
        private readonly TextView _badge;
        private readonly LinearProgressIndicator _progress;
        private readonly MaterialButton[] _buttons;
        private readonly MaterialButton _more;
        private readonly IPluginSduiAdapterCallbacks _callbacks;

        private IReadOnlyList<SduiButtonView> _actions = [];
        private JsonElement? _itemContext;

        public ListRowHolder(View itemView, IPluginSduiAdapterCallbacks callbacks)
            : base(itemView)
        {
            _callbacks = callbacks;
            _title = itemView.FindViewById<TextView>(Resource.Id.sdui_row_title)!;
            _subtitle = itemView.FindViewById<TextView>(Resource.Id.sdui_row_subtitle)!;
            _badge = itemView.FindViewById<TextView>(Resource.Id.sdui_row_badge)!;
            _progress = itemView.FindViewById<LinearProgressIndicator>(Resource.Id.sdui_row_progress)!;
            _buttons =
            [
                itemView.FindViewById<MaterialButton>(Resource.Id.sdui_row_action_0)!,
                itemView.FindViewById<MaterialButton>(Resource.Id.sdui_row_action_1)!,
                itemView.FindViewById<MaterialButton>(Resource.Id.sdui_row_action_2)!,
            ];
            _more = itemView.FindViewById<MaterialButton>(Resource.Id.sdui_row_action_more)!;

            for (var index = 0; index < _buttons.Length; index++)
            {
                var buttonIndex = index;
                _buttons[buttonIndex].Click += (_, _) => InvokeDirect(buttonIndex);
            }
            _more.Click += (_, _) => ShowOverflow();
        }

        public void Bind(SduiListRowRenderItem item)
        {
            _title.Text = item.Title;
            _subtitle.Text = item.Subtitle;
            _subtitle.Visibility = string.IsNullOrWhiteSpace(item.Subtitle)
                ? ViewStates.Gone
                : ViewStates.Visible;
            Rows.SetBadge(_badge, item.Badge, Rows.BadgeTone.Neutral);

            if (item.ProgressPercent is { } percent)
            {
                _progress.Visibility = ViewStates.Visible;
                _progress.Progress = ClampPercent(percent);
            }
            else
            {
                _progress.Visibility = ViewStates.Gone;
            }

            _actions = item.Actions;
            _itemContext = item.Item;

            var directCount = Math.Min(item.Actions.Count, _buttons.Length);
            for (var index = 0; index < _buttons.Length; index++)
            {
                if (index < directCount)
                {
                    _buttons[index].Text = item.Actions[index].Label;
                    _buttons[index].Visibility = ViewStates.Visible;
                }
                else
                {
                    _buttons[index].Visibility = ViewStates.Gone;
                }
            }

            _more.Visibility = item.Actions.Count > _buttons.Length
                ? ViewStates.Visible
                : ViewStates.Gone;
        }

        private void InvokeDirect(int index)
        {
            if (index < 0 || index >= _actions.Count)
            {
                return;
            }

            InvokeAction(ItemView.Context!, _callbacks, _actions[index], _itemContext);
        }

        private void ShowOverflow()
        {
            if (_actions.Count <= _buttons.Length)
            {
                return;
            }

            ShowOverflowMenu(
                ItemView.Context!,
                _more,
                _callbacks,
                _actions,
                _buttons.Length,
                _itemContext);
        }
    }

    /// <summary>动作栏：最多 3 个直显按钮，其余进入溢出菜单；行内没有条目上下文。</summary>
    private sealed class ActionBarHolder : SduiHolderBase
    {
        private readonly MaterialButton[] _buttons;
        private readonly MaterialButton _more;
        private readonly IPluginSduiAdapterCallbacks _callbacks;

        private IReadOnlyList<SduiButtonView> _actions = [];

        public ActionBarHolder(View itemView, IPluginSduiAdapterCallbacks callbacks)
            : base(itemView)
        {
            _callbacks = callbacks;
            _buttons =
            [
                itemView.FindViewById<MaterialButton>(Resource.Id.sdui_action_btn_0)!,
                itemView.FindViewById<MaterialButton>(Resource.Id.sdui_action_btn_1)!,
                itemView.FindViewById<MaterialButton>(Resource.Id.sdui_action_btn_2)!,
            ];
            _more = itemView.FindViewById<MaterialButton>(Resource.Id.sdui_action_btn_more)!;

            for (var index = 0; index < _buttons.Length; index++)
            {
                var buttonIndex = index;
                _buttons[buttonIndex].Click += (_, _) => InvokeDirect(buttonIndex);
            }
            _more.Click += (_, _) => ShowOverflow();
        }

        public void Bind(SduiActionBarRenderItem item)
        {
            _actions = item.Actions;
            ItemView.Visibility = item.Actions.Count == 0 ? ViewStates.Gone : ViewStates.Visible;

            var directCount = Math.Min(item.Actions.Count, _buttons.Length);
            for (var index = 0; index < _buttons.Length; index++)
            {
                if (index < directCount)
                {
                    _buttons[index].Text = item.Actions[index].Label;
                    _buttons[index].Visibility = ViewStates.Visible;
                }
                else
                {
                    _buttons[index].Visibility = ViewStates.Gone;
                }
            }

            _more.Visibility = item.Actions.Count > _buttons.Length
                ? ViewStates.Visible
                : ViewStates.Gone;
        }

        private void InvokeDirect(int index)
        {
            if (index < 0 || index >= _actions.Count)
            {
                return;
            }

            InvokeAction(ItemView.Context!, _callbacks, _actions[index], null);
        }

        private void ShowOverflow()
        {
            if (_actions.Count <= _buttons.Length)
            {
                return;
            }

            ShowOverflowMenu(
                ItemView.Context!,
                _more,
                _callbacks,
                _actions,
                _buttons.Length,
                null);
        }
    }

    /// <summary>外部链接：只显示受控按钮，点击后把 URL 交回宿主做安全打开。</summary>
    private sealed class ExternalLinkHolder : SduiHolderBase
    {
        private readonly IPluginSduiAdapterCallbacks _callbacks;
        private readonly MaterialButton _button;
        private string _url = string.Empty;

        public ExternalLinkHolder(View itemView, IPluginSduiAdapterCallbacks callbacks)
            : base(itemView)
        {
            _callbacks = callbacks;
            _button = itemView.FindViewById<MaterialButton>(Resource.Id.sdui_external_link_btn)!;
            _button.Click += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(_url))
                {
                    _callbacks.OnExternalLink(_url);
                }
            };
        }

        public void Bind(SduiExternalLinkRenderItem item)
        {
            _url = item.Url;
            _button.Text = string.IsNullOrWhiteSpace(item.Label)
                ? ItemView.Context!.GetString(Resource.String.plugin_sdui_external_link_btn)
                : item.Label;
        }
    }

    /// <summary>官方配置桥标题：展示配置命名空间。</summary>
    private sealed class SettingsHeaderHolder(View itemView) : SduiHolderBase(itemView)
    {
        private readonly TextView _title =
            itemView.FindViewById<TextView>(Resource.Id.sdui_settings_header_title)!;
        private readonly TextView _namespace =
            itemView.FindViewById<TextView>(Resource.Id.sdui_settings_header_ns)!;

        public void Bind(SduiSettingsBridgeHeaderRenderItem item)
        {
            _title.Text = item.Title;
            _namespace.Text = item.Namespace;
        }
    }

    /// <summary>数据源失败诊断卡；单源失败只显示页内提示，不影响其余区块。</summary>
    private sealed class SourceErrorHolder(View itemView) : SduiHolderBase(itemView)
    {
        private readonly TextView _title =
            itemView.FindViewById<TextView>(Resource.Id.sdui_source_err_title)!;
        private readonly TextView _message =
            itemView.FindViewById<TextView>(Resource.Id.sdui_source_err_msg)!;

        public void Bind(SduiSourceErrorRenderItem item)
        {
            _title.Text = ItemView.Context!.GetString(Resource.String.plugin_sdui_source_err_title);
            _message.Text = item.Message;
        }
    }

    /// <summary>
    /// RPC 动作持久结果卡：成功用次要容器色、失败用错误容器色 + 错误色描边，
    /// 只重设既有静态控件的文字与卡片颜色，不构造任何新控件。
    /// </summary>
    private sealed class ActionResultHolder(View itemView) : SduiHolderBase(itemView)
    {
        private readonly MaterialCardView _card =
            itemView.FindViewById<MaterialCardView>(Resource.Id.sdui_action_result_card)!;
        private readonly TextView _title =
            itemView.FindViewById<TextView>(Resource.Id.sdui_action_result_title)!;
        private readonly TextView _status =
            itemView.FindViewById<TextView>(Resource.Id.sdui_action_result_status)!;
        private readonly TextView _elapsed =
            itemView.FindViewById<TextView>(Resource.Id.sdui_action_result_elapsed)!;
        private readonly TextView _meta =
            itemView.FindViewById<TextView>(Resource.Id.sdui_action_result_meta)!;
        private readonly TextView _summary =
            itemView.FindViewById<TextView>(Resource.Id.sdui_action_result_summary)!;

        public void Bind(SduiActionResultRenderItem item)
        {
            var context = ItemView.Context!;

            _title.Text = context.GetString(Resource.String.plugin_sdui_action_result_title);
            _status.Text = context.GetString(item.Success
                ? Resource.String.plugin_sdui_action_result_ok
                : Resource.String.plugin_sdui_action_result_fail);

            var elapsedText = item.ElapsedMs.ToString(CultureInfo.InvariantCulture);
            _elapsed.Text = string.Concat(
                context.GetString(Resource.String.plugin_sdui_action_result_elapsed),
                " ",
                elapsedText,
                " ms");

            var meta = string.Concat(
                context.GetString(Resource.String.plugin_sdui_action_result_endpoint),
                " ",
                item.Endpoint);
            if (!string.IsNullOrWhiteSpace(item.ErrorCode))
            {
                meta = string.Concat(
                    meta,
                    " · ",
                    context.GetString(Resource.String.plugin_sdui_action_result_error_code),
                    " ",
                    item.ErrorCode);
            }

            _meta.Text = meta;
            _summary.Text = item.SummaryText;
            _summary.Visibility = string.IsNullOrWhiteSpace(item.SummaryText)
                ? ViewStates.Gone
                : ViewStates.Visible;

            var (containerAttr, onContainerAttr, strokeAttr) = item.Success
                ? (
                    Resource.Attribute.colorSecondaryContainer,
                    Resource.Attribute.colorOnSecondaryContainer,
                    Resource.Attribute.colorSecondaryContainer)
                : (
                    Resource.Attribute.colorErrorContainer,
                    Resource.Attribute.colorOnErrorContainer,
                    Resource.Attribute.colorError);

            var container = new Android.Graphics.Color(Rows.Color(_card, containerAttr));
            var onContainer = new Android.Graphics.Color(Rows.Color(_card, onContainerAttr));

            _card.SetCardBackgroundColor(container);
            _card.StrokeColor = Rows.Color(_card, strokeAttr);
            _title.SetTextColor(onContainer);
            _status.SetTextColor(onContainer);
            _status.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(onContainer);
            _elapsed.SetTextColor(onContainer);
            _meta.SetTextColor(onContainer);
            _summary.SetTextColor(onContainer);
        }
    }

    /// <summary>6 套官方配置字段布局的公共部分：标题、键名、描述与只读/已修改徽标。</summary>
    private abstract class ConfigFieldHolderBase : SduiHolderBase
    {
        private readonly TextView _labelView;
        private readonly TextView _keyView;
        private readonly TextView _descView;
        private readonly TextView? _overrideBadge;
        private readonly TextView? _readOnlyBadge;

        protected ConfigFieldHolderBase(View itemView, IPluginSduiAdapterCallbacks callbacks)
            : base(itemView)
        {
            Callbacks = callbacks;
            _labelView = itemView.FindViewById<TextView>(Resource.Id.plugin_cfg_label)!;
            _keyView = itemView.FindViewById<TextView>(Resource.Id.plugin_cfg_key)!;
            _descView = itemView.FindViewById<TextView>(Resource.Id.plugin_cfg_desc)!;
            _overrideBadge = itemView.FindViewById<TextView>(Resource.Id.plugin_cfg_override_badge);
            _readOnlyBadge = itemView.FindViewById<TextView>(Resource.Id.plugin_cfg_readonly_badge);
        }

        protected IPluginSduiAdapterCallbacks Callbacks { get; }
        protected PluginConfigFieldView? BoundField { get; private set; }
        protected string SettingsNamespace { get; private set; } = string.Empty;
        protected bool CanEdit => BoundField is { ReadOnly: false };

        public void Bind(string settingsNamespace, PluginConfigFieldView field)
        {
            SettingsNamespace = settingsNamespace;
            BoundField = field;

            _labelView.Text = field.Label;
            _keyView.Text = field.KeyDisplay;
            if (!string.IsNullOrWhiteSpace(field.Description))
            {
                _descView.Text = field.Description;
                _descView.Visibility = ViewStates.Visible;
            }
            else
            {
                _descView.Visibility = ViewStates.Gone;
            }

            if (_overrideBadge is not null)
            {
                _overrideBadge.Visibility = field.HasUserOverride ? ViewStates.Visible : ViewStates.Gone;
            }
            if (_readOnlyBadge is not null)
            {
                _readOnlyBadge.Visibility = field.ReadOnly ? ViewStates.Visible : ViewStates.Gone;
            }

            BindSpecific(field, !field.ReadOnly);
        }

        protected abstract void BindSpecific(PluginConfigFieldView field, bool editable);
    }

    /// <summary>布尔字段：点击开关直接触发配置写入，绑定阶段不注册选中变化回调。</summary>
    private sealed class BooleanFieldHolder : ConfigFieldHolderBase
    {
        private readonly MaterialSwitch _switchView;
        private readonly View _actionsRow;
        private readonly MaterialButton _resetButton;

        public BooleanFieldHolder(View itemView, IPluginSduiAdapterCallbacks callbacks)
            : base(itemView, callbacks)
        {
            _switchView = itemView.FindViewById<MaterialSwitch>(Resource.Id.plugin_cfg_switch)!;
            _actionsRow = itemView.FindViewById(Resource.Id.plugin_cfg_actions_row)!;
            _resetButton = itemView.FindViewById<MaterialButton>(Resource.Id.plugin_cfg_reset_btn)!;

            _switchView.Click += (_, _) =>
            {
                if (BoundField is not { } field || !CanEdit)
                {
                    if (BoundField is { } bound)
                    {
                        _switchView.Checked = ParseBoolean(bound);
                    }
                    return;
                }

                Callbacks.OnConfigBoolean(SettingsNamespace, field, _switchView.Checked);
            };

            _resetButton.Click += (_, _) =>
            {
                if (BoundField is { } field && CanEdit && field.HasUserOverride)
                {
                    Callbacks.OnConfigReset(SettingsNamespace, field);
                }
            };
        }

        protected override void BindSpecific(PluginConfigFieldView field, bool editable)
        {
            _switchView.Checked = ParseBoolean(field);
            _switchView.Enabled = editable;
            _switchView.ContentDescription = field.Label;

            var showReset = editable && field.HasUserOverride;
            _actionsRow.Visibility = showReset ? ViewStates.Visible : ViewStates.Gone;
            _resetButton.Enabled = showReset;
        }

        private static bool ParseBoolean(PluginConfigFieldView field)
        {
            var raw = field.CurrentValueJson ?? field.BaseValueJson;
            return string.Equals(raw?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>文本/数值/JSON 字段共用的编辑与重置按钮接线。</summary>
    private abstract class EditableFieldHolderBase : ConfigFieldHolderBase
    {
        private readonly MaterialButton _editButton;
        private readonly MaterialButton _resetButton;

        protected EditableFieldHolderBase(View itemView, IPluginSduiAdapterCallbacks callbacks)
            : base(itemView, callbacks)
        {
            _editButton = itemView.FindViewById<MaterialButton>(Resource.Id.plugin_cfg_edit_btn)!;
            _resetButton = itemView.FindViewById<MaterialButton>(Resource.Id.plugin_cfg_reset_btn)!;

            _editButton.Click += (_, _) =>
            {
                if (BoundField is { } field && CanEdit)
                {
                    Callbacks.OnConfigEdit(SettingsNamespace, field);
                }
            };

            _resetButton.Click += (_, _) =>
            {
                if (BoundField is { } field && CanEdit && field.HasUserOverride)
                {
                    Callbacks.OnConfigReset(SettingsNamespace, field);
                }
            };
        }

        protected override void BindSpecific(PluginConfigFieldView field, bool editable)
        {
            _editButton.Visibility = editable ? ViewStates.Visible : ViewStates.Gone;
            _editButton.Enabled = editable;

            var showReset = editable && field.HasUserOverride;
            _resetButton.Visibility = showReset ? ViewStates.Visible : ViewStates.Gone;
            _resetButton.Enabled = showReset;

            BindValue(field);
        }

        protected abstract void BindValue(PluginConfigFieldView field);
    }

    /// <summary>普通文本字段。</summary>
    private sealed class TextFieldHolder(View itemView, IPluginSduiAdapterCallbacks callbacks)
        : EditableFieldHolderBase(itemView, callbacks)
    {
        private readonly TextView _valueView =
            itemView.FindViewById<TextView>(Resource.Id.plugin_cfg_value)!;

        protected override void BindValue(PluginConfigFieldView field)
        {
            var display = PluginConfigAdapter.UnquoteJsonDisplay(
                field.CurrentValueJson ?? field.BaseValueJson);
            _valueView.Text = string.IsNullOrEmpty(display)
                ? ItemView.Context!.GetString(Resource.String.plugin_detail_config_empty_value)
                : display;
        }
    }

    /// <summary>数值字段，额外展示可选的范围与步长提示。</summary>
    private sealed class NumberFieldHolder(View itemView, IPluginSduiAdapterCallbacks callbacks)
        : EditableFieldHolderBase(itemView, callbacks)
    {
        private readonly TextView _rangeView =
            itemView.FindViewById<TextView>(Resource.Id.plugin_cfg_range)!;
        private readonly TextView _valueView =
            itemView.FindViewById<TextView>(Resource.Id.plugin_cfg_value)!;

        protected override void BindValue(PluginConfigFieldView field)
        {
            var context = ItemView.Context!;
            var rangeText = FormatNumberRange(context, field);
            if (!string.IsNullOrEmpty(rangeText))
            {
                _rangeView.Text = rangeText;
                _rangeView.Visibility = ViewStates.Visible;
            }
            else
            {
                _rangeView.Visibility = ViewStates.Gone;
            }

            var rawValue = (field.CurrentValueJson ?? field.BaseValueJson)?.Trim();
            _valueView.Text = string.IsNullOrEmpty(rawValue)
                ? context.GetString(Resource.String.plugin_detail_config_empty_value)
                : rawValue;
        }

        private static string? FormatNumberRange(Context context, PluginConfigFieldView field)
        {
            var parts = new List<string>(3);
            if (field.Min.HasValue)
            {
                parts.Add($">= {field.Min.Value.ToString(CultureInfo.InvariantCulture)}");
            }
            if (field.Max.HasValue)
            {
                parts.Add($"<= {field.Max.Value.ToString(CultureInfo.InvariantCulture)}");
            }
            if (field.Step.HasValue)
            {
                parts.Add($"step {field.Step.Value.ToString(CultureInfo.InvariantCulture)}");
            }

            return parts.Count == 0
                ? null
                : Rows.Fmt(
                    context,
                    Resource.String.plugin_detail_config_number_range_fmt,
                    string.Join(" · ", parts));
        }
    }

    /// <summary>单选字段：展示当前候选项标签，点击按钮交给宿主弹出选择器。</summary>
    private sealed class SingleSelectFieldHolder : ConfigFieldHolderBase
    {
        private readonly TextView _valueView;
        private readonly MaterialButton _selectButton;
        private readonly MaterialButton _resetButton;

        public SingleSelectFieldHolder(View itemView, IPluginSduiAdapterCallbacks callbacks)
            : base(itemView, callbacks)
        {
            _valueView = itemView.FindViewById<TextView>(Resource.Id.plugin_cfg_value)!;
            _selectButton = itemView.FindViewById<MaterialButton>(Resource.Id.plugin_cfg_select_btn)!;
            _resetButton = itemView.FindViewById<MaterialButton>(Resource.Id.plugin_cfg_reset_btn)!;

            _selectButton.Click += (_, _) =>
            {
                if (BoundField is { } field && CanEdit && field.Options.Count > 0)
                {
                    Callbacks.OnConfigSelect(SettingsNamespace, field);
                }
            };

            _resetButton.Click += (_, _) =>
            {
                if (BoundField is { } field && CanEdit && field.HasUserOverride)
                {
                    Callbacks.OnConfigReset(SettingsNamespace, field);
                }
            };
        }

        protected override void BindSpecific(PluginConfigFieldView field, bool editable)
        {
            var context = ItemView.Context!;
            var effectiveJson = (field.CurrentValueJson ?? field.BaseValueJson)?.Trim();
            string? matchedLabel = null;
            if (!string.IsNullOrEmpty(effectiveJson))
            {
                foreach (var option in field.Options)
                {
                    if (string.Equals(option.RawValueJson.Trim(), effectiveJson, StringComparison.Ordinal))
                    {
                        matchedLabel = option.DisplayLabel;
                        break;
                    }
                }
            }

            _valueView.Text = !string.IsNullOrWhiteSpace(matchedLabel)
                ? matchedLabel
                : !string.IsNullOrEmpty(effectiveJson)
                    ? PluginConfigAdapter.UnquoteJsonDisplay(effectiveJson)
                    : context.GetString(Resource.String.plugin_detail_config_empty_value);

            _selectButton.Visibility =
                editable && field.Options.Count > 0 ? ViewStates.Visible : ViewStates.Gone;
            _selectButton.Enabled = editable && field.Options.Count > 0;

            var showReset = editable && field.HasUserOverride;
            _resetButton.Visibility = showReset ? ViewStates.Visible : ViewStates.Gone;
            _resetButton.Enabled = showReset;
        }
    }

    /// <summary>密钥字段：只显示是否已配置，绝不回显明文。</summary>
    private sealed class SecretFieldHolder : ConfigFieldHolderBase
    {
        private readonly TextView _statusBadge;
        private readonly MaterialButton _setButton;
        private readonly MaterialButton _clearButton;

        public SecretFieldHolder(View itemView, IPluginSduiAdapterCallbacks callbacks)
            : base(itemView, callbacks)
        {
            _statusBadge = itemView.FindViewById<TextView>(Resource.Id.plugin_cfg_secret_status)!;
            _setButton = itemView.FindViewById<MaterialButton>(Resource.Id.plugin_cfg_secret_set_btn)!;
            _clearButton = itemView.FindViewById<MaterialButton>(Resource.Id.plugin_cfg_secret_clear_btn)!;

            _setButton.Click += (_, _) =>
            {
                if (BoundField is { } field && CanEdit)
                {
                    Callbacks.OnConfigEdit(SettingsNamespace, field);
                }
            };

            _clearButton.Click += (_, _) =>
            {
                if (BoundField is { } field && CanEdit && (field.IsSecretSet || field.HasUserOverride))
                {
                    Callbacks.OnConfigReset(SettingsNamespace, field);
                }
            };
        }

        protected override void BindSpecific(PluginConfigFieldView field, bool editable)
        {
            var context = ItemView.Context!;
            if (field.IsSecretSet)
            {
                Rows.SetBadge(
                    _statusBadge,
                    context.GetString(Resource.String.plugin_detail_config_secret_set),
                    Rows.BadgeTone.Success);
            }
            else
            {
                Rows.SetBadge(
                    _statusBadge,
                    context.GetString(Resource.String.plugin_detail_config_secret_unset),
                    Rows.BadgeTone.Neutral);
            }

            _setButton.Visibility = editable ? ViewStates.Visible : ViewStates.Gone;
            _setButton.Enabled = editable;

            var showClear = editable && (field.IsSecretSet || field.HasUserOverride);
            _clearButton.Visibility = showClear ? ViewStates.Visible : ViewStates.Gone;
            _clearButton.Enabled = showClear;
        }
    }

    /// <summary>JSON 降级字段：原样展示序列化文本，编辑仍由宿主处理。</summary>
    private sealed class JsonFieldHolder(View itemView, IPluginSduiAdapterCallbacks callbacks)
        : EditableFieldHolderBase(itemView, callbacks)
    {
        private readonly TextView _valueView =
            itemView.FindViewById<TextView>(Resource.Id.plugin_cfg_value)!;

        protected override void BindValue(PluginConfigFieldView field)
        {
            var rawJson = field.CurrentValueJson ?? field.BaseValueJson;
            _valueView.Text = string.IsNullOrWhiteSpace(rawJson)
                ? ItemView.Context!.GetString(Resource.String.plugin_detail_config_empty_value)
                : rawJson;
        }
    }
}
