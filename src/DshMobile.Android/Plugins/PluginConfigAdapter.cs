using System.Globalization;
using System.Text.Json;
using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using DshMobile.Agent.Domain;
using DshMobile.App.Ui;
using Google.Android.Material.Button;
using Google.Android.Material.MaterialSwitch;

namespace DshMobile.App.Plugins;

/// <summary>
/// 官方 <c>settings/*</c> 实时配置表单多 ViewType 适配器（ADR-0034）：
/// 严格采用 6 套独立 XML 模板渲染布尔、文本、数值、单选、密钥与 JSON 降级字段，
/// 零命令式控件构造；密钥字段绝不回显明文。
/// </summary>
internal sealed class PluginConfigAdapter(
    Action<PluginConfigFieldView, bool> onBooleanToggled,
    Action<PluginConfigFieldView> onEditFieldRequested,
    Action<PluginConfigFieldView> onSelectOptionRequested,
    Action<PluginConfigFieldView> onResetFieldRequested) : RecyclerView.Adapter
{
    private const int ViewTypeBoolean = 0;
    private const int ViewTypeText = 1;
    private const int ViewTypeNumber = 2;
    private const int ViewTypeSingleSelect = 3;
    private const int ViewTypeSecret = 4;
    private const int ViewTypeJsonFallback = 5;

    private IReadOnlyList<PluginConfigFieldView> _fields = [];
    private bool _settingsWritable;
    private bool _isBusy;

    public void Submit(IReadOnlyList<PluginConfigFieldView> fields, bool settingsWritable, bool isBusy)
    {
        _fields = fields;
        _settingsWritable = settingsWritable;
        _isBusy = isBusy;
        NotifyDataSetChanged();
    }

    public override int ItemCount => _fields.Count;

    public override int GetItemViewType(int position) => _fields[position].Kind switch
    {
        PluginConfigFieldKind.Boolean => ViewTypeBoolean,
        PluginConfigFieldKind.Text => ViewTypeText,
        PluginConfigFieldKind.Number => ViewTypeNumber,
        PluginConfigFieldKind.SingleSelect => ViewTypeSingleSelect,
        PluginConfigFieldKind.Secret => ViewTypeSecret,
        _ => ViewTypeJsonFallback,
    };

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var inflater = LayoutInflater.From(parent.Context)!;
        return viewType switch
        {
            ViewTypeBoolean => new BooleanFieldHolder(
                inflater.Inflate(Resource.Layout.item_plugin_config_bool, parent, false)!,
                onBooleanToggled,
                onResetFieldRequested),
            ViewTypeNumber => new NumberFieldHolder(
                inflater.Inflate(Resource.Layout.item_plugin_config_number, parent, false)!,
                onEditFieldRequested,
                onResetFieldRequested),
            ViewTypeSingleSelect => new SingleSelectFieldHolder(
                inflater.Inflate(Resource.Layout.item_plugin_config_select, parent, false)!,
                onSelectOptionRequested,
                onResetFieldRequested),
            ViewTypeSecret => new SecretFieldHolder(
                inflater.Inflate(Resource.Layout.item_plugin_config_secret, parent, false)!,
                onEditFieldRequested,
                onResetFieldRequested),
            ViewTypeJsonFallback => new JsonFieldHolder(
                inflater.Inflate(Resource.Layout.item_plugin_config_json, parent, false)!,
                onEditFieldRequested,
                onResetFieldRequested),
            _ => new TextFieldHolder(
                inflater.Inflate(Resource.Layout.item_plugin_config_text, parent, false)!,
                onEditFieldRequested,
                onResetFieldRequested),
        };
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        ((ConfigFieldHolderBase)holder).Bind(_fields[position], _settingsWritable, _isBusy);
    }

    /// <summary>尝试将 JSON 字符串字面量反引号为原始展示文本。</summary>
    internal static string UnquoteJsonDisplay(string? rawJson)
    {
        if (string.IsNullOrEmpty(rawJson))
        {
            return string.Empty;
        }
        if (rawJson.Length >= 2 && rawJson[0] == '"' && rawJson[^1] == '"')
        {
            try
            {
                using var doc = JsonDocument.Parse(rawJson);
                if (doc.RootElement.ValueKind == JsonValueKind.String)
                {
                    return doc.RootElement.GetString() ?? string.Empty;
                }
            }
            catch (JsonException)
            {
            }
        }
        return rawJson;
    }

    private abstract class ConfigFieldHolderBase(View itemView) : RecyclerView.ViewHolder(itemView)
    {
        private readonly TextView _labelView = itemView.FindViewById<TextView>(Resource.Id.plugin_cfg_label)!;
        private readonly TextView _keyView = itemView.FindViewById<TextView>(Resource.Id.plugin_cfg_key)!;
        private readonly TextView _descView = itemView.FindViewById<TextView>(Resource.Id.plugin_cfg_desc)!;
        private readonly TextView? _overrideBadge = itemView.FindViewById<TextView>(Resource.Id.plugin_cfg_override_badge);
        private readonly TextView? _readOnlyBadge = itemView.FindViewById<TextView>(Resource.Id.plugin_cfg_readonly_badge);

        protected PluginConfigFieldView? BoundField { get; private set; }
        protected bool CanEdit { get; private set; }

        public void Bind(PluginConfigFieldView field, bool settingsWritable, bool isBusy)
        {
            BoundField = field;
            var editable = settingsWritable && !field.ReadOnly;
            CanEdit = editable && !isBusy;

            _labelView.Text = field.Label;
            _keyView.Text = field.KeyDisplay;
            if (!string.IsNullOrWhiteSpace(field.Description))
            {
                _descView.Visibility = ViewStates.Visible;
                _descView.Text = field.Description;
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
                _readOnlyBadge.Visibility = !editable ? ViewStates.Visible : ViewStates.Gone;
            }

            BindSpecific(field, editable, isBusy);
        }

        protected abstract void BindSpecific(PluginConfigFieldView field, bool editable, bool isBusy);
    }

    private sealed class BooleanFieldHolder : ConfigFieldHolderBase
    {
        private readonly MaterialSwitch _switchView;
        private readonly View _actionsRow;
        private readonly MaterialButton _resetButton;

        public BooleanFieldHolder(
            View itemView,
            Action<PluginConfigFieldView, bool> onBooleanToggled,
            Action<PluginConfigFieldView> onResetFieldRequested) : base(itemView)
        {
            _switchView = itemView.FindViewById<MaterialSwitch>(Resource.Id.plugin_cfg_switch)!;
            _actionsRow = itemView.FindViewById(Resource.Id.plugin_cfg_actions_row)!;
            _resetButton = itemView.FindViewById<MaterialButton>(Resource.Id.plugin_cfg_reset_btn)!;

            _switchView.Click += (_, _) =>
            {
                if (BoundField is not { } field || !CanEdit)
                {
                    if (BoundField is not null)
                    {
                        _switchView.Checked = ParseBooleanValue(BoundField);
                    }
                    return;
                }
                onBooleanToggled(field, _switchView.Checked);
            };

            _resetButton.Click += (_, _) =>
            {
                if (BoundField is { } field && CanEdit && field.HasUserOverride)
                {
                    onResetFieldRequested(field);
                }
            };
        }

        protected override void BindSpecific(PluginConfigFieldView field, bool editable, bool isBusy)
        {
            _switchView.Checked = ParseBooleanValue(field);
            _switchView.Enabled = editable && !isBusy;
            _switchView.ContentDescription = field.Label;

            var showReset = editable && field.HasUserOverride;
            _actionsRow.Visibility = showReset ? ViewStates.Visible : ViewStates.Gone;
            _resetButton.Enabled = showReset && !isBusy;
        }

        private static bool ParseBooleanValue(PluginConfigFieldView field)
        {
            var raw = field.CurrentValueJson ?? field.BaseValueJson;
            return string.Equals(raw?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class TextFieldHolder : ConfigFieldHolderBase
    {
        private readonly TextView _valueView;
        private readonly MaterialButton _editButton;
        private readonly MaterialButton _resetButton;

        public TextFieldHolder(
            View itemView,
            Action<PluginConfigFieldView> onEditFieldRequested,
            Action<PluginConfigFieldView> onResetFieldRequested) : base(itemView)
        {
            _valueView = itemView.FindViewById<TextView>(Resource.Id.plugin_cfg_value)!;
            _editButton = itemView.FindViewById<MaterialButton>(Resource.Id.plugin_cfg_edit_btn)!;
            _resetButton = itemView.FindViewById<MaterialButton>(Resource.Id.plugin_cfg_reset_btn)!;

            _editButton.Click += (_, _) =>
            {
                if (BoundField is { } field && CanEdit)
                {
                    onEditFieldRequested(field);
                }
            };

            _resetButton.Click += (_, _) =>
            {
                if (BoundField is { } field && CanEdit && field.HasUserOverride)
                {
                    onResetFieldRequested(field);
                }
            };
        }

        protected override void BindSpecific(PluginConfigFieldView field, bool editable, bool isBusy)
        {
            var context = ItemView.Context!;
            var display = UnquoteJsonDisplay(field.CurrentValueJson ?? field.BaseValueJson);
            _valueView.Text = string.IsNullOrEmpty(display)
                ? context.GetString(Resource.String.plugin_detail_config_empty_value)
                : display;

            _editButton.Visibility = editable ? ViewStates.Visible : ViewStates.Gone;
            _editButton.Enabled = editable && !isBusy;

            var showReset = editable && field.HasUserOverride;
            _resetButton.Visibility = showReset ? ViewStates.Visible : ViewStates.Gone;
            _resetButton.Enabled = showReset && !isBusy;
        }
    }

    private sealed class NumberFieldHolder : ConfigFieldHolderBase
    {
        private readonly TextView _rangeView;
        private readonly TextView _valueView;
        private readonly MaterialButton _editButton;
        private readonly MaterialButton _resetButton;

        public NumberFieldHolder(
            View itemView,
            Action<PluginConfigFieldView> onEditFieldRequested,
            Action<PluginConfigFieldView> onResetFieldRequested) : base(itemView)
        {
            _rangeView = itemView.FindViewById<TextView>(Resource.Id.plugin_cfg_range)!;
            _valueView = itemView.FindViewById<TextView>(Resource.Id.plugin_cfg_value)!;
            _editButton = itemView.FindViewById<MaterialButton>(Resource.Id.plugin_cfg_edit_btn)!;
            _resetButton = itemView.FindViewById<MaterialButton>(Resource.Id.plugin_cfg_reset_btn)!;

            _editButton.Click += (_, _) =>
            {
                if (BoundField is { } field && CanEdit)
                {
                    onEditFieldRequested(field);
                }
            };

            _resetButton.Click += (_, _) =>
            {
                if (BoundField is { } field && CanEdit && field.HasUserOverride)
                {
                    onResetFieldRequested(field);
                }
            };
        }

        protected override void BindSpecific(PluginConfigFieldView field, bool editable, bool isBusy)
        {
            var context = ItemView.Context!;
            var rangeText = FormatNumberRange(context, field);
            if (!string.IsNullOrEmpty(rangeText))
            {
                _rangeView.Visibility = ViewStates.Visible;
                _rangeView.Text = rangeText;
            }
            else
            {
                _rangeView.Visibility = ViewStates.Gone;
            }

            var rawValue = (field.CurrentValueJson ?? field.BaseValueJson)?.Trim();
            _valueView.Text = string.IsNullOrEmpty(rawValue)
                ? context.GetString(Resource.String.plugin_detail_config_empty_value)
                : rawValue;

            _editButton.Visibility = editable ? ViewStates.Visible : ViewStates.Gone;
            _editButton.Enabled = editable && !isBusy;

            var showReset = editable && field.HasUserOverride;
            _resetButton.Visibility = showReset ? ViewStates.Visible : ViewStates.Gone;
            _resetButton.Enabled = showReset && !isBusy;
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
                : Rows.Fmt(context, Resource.String.plugin_detail_config_number_range_fmt, string.Join(" · ", parts));
        }
    }

    private sealed class SingleSelectFieldHolder : ConfigFieldHolderBase
    {
        private readonly TextView _valueView;
        private readonly MaterialButton _selectButton;
        private readonly MaterialButton _resetButton;

        public SingleSelectFieldHolder(
            View itemView,
            Action<PluginConfigFieldView> onSelectOptionRequested,
            Action<PluginConfigFieldView> onResetFieldRequested) : base(itemView)
        {
            _valueView = itemView.FindViewById<TextView>(Resource.Id.plugin_cfg_value)!;
            _selectButton = itemView.FindViewById<MaterialButton>(Resource.Id.plugin_cfg_select_btn)!;
            _resetButton = itemView.FindViewById<MaterialButton>(Resource.Id.plugin_cfg_reset_btn)!;

            _selectButton.Click += (_, _) =>
            {
                if (BoundField is { } field && CanEdit && field.Options.Count > 0)
                {
                    onSelectOptionRequested(field);
                }
            };

            _resetButton.Click += (_, _) =>
            {
                if (BoundField is { } field && CanEdit && field.HasUserOverride)
                {
                    onResetFieldRequested(field);
                }
            };
        }

        protected override void BindSpecific(PluginConfigFieldView field, bool editable, bool isBusy)
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
                : (!string.IsNullOrEmpty(effectiveJson)
                    ? UnquoteJsonDisplay(effectiveJson)
                    : context.GetString(Resource.String.plugin_detail_config_empty_value));

            _selectButton.Visibility = editable && field.Options.Count > 0 ? ViewStates.Visible : ViewStates.Gone;
            _selectButton.Enabled = editable && field.Options.Count > 0 && !isBusy;

            var showReset = editable && field.HasUserOverride;
            _resetButton.Visibility = showReset ? ViewStates.Visible : ViewStates.Gone;
            _resetButton.Enabled = showReset && !isBusy;
        }
    }

    private sealed class SecretFieldHolder : ConfigFieldHolderBase
    {
        private readonly TextView _statusBadge;
        private readonly MaterialButton _setButton;
        private readonly MaterialButton _clearButton;

        public SecretFieldHolder(
            View itemView,
            Action<PluginConfigFieldView> onEditFieldRequested,
            Action<PluginConfigFieldView> onResetFieldRequested) : base(itemView)
        {
            _statusBadge = itemView.FindViewById<TextView>(Resource.Id.plugin_cfg_secret_status)!;
            _setButton = itemView.FindViewById<MaterialButton>(Resource.Id.plugin_cfg_secret_set_btn)!;
            _clearButton = itemView.FindViewById<MaterialButton>(Resource.Id.plugin_cfg_secret_clear_btn)!;

            _setButton.Click += (_, _) =>
            {
                if (BoundField is { } field && CanEdit)
                {
                    onEditFieldRequested(field);
                }
            };

            _clearButton.Click += (_, _) =>
            {
                if (BoundField is { } field && CanEdit && (field.IsSecretSet || field.HasUserOverride))
                {
                    onResetFieldRequested(field);
                }
            };
        }

        protected override void BindSpecific(PluginConfigFieldView field, bool editable, bool isBusy)
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
            _setButton.Enabled = editable && !isBusy;

            var showClear = editable && (field.IsSecretSet || field.HasUserOverride);
            _clearButton.Visibility = showClear ? ViewStates.Visible : ViewStates.Gone;
            _clearButton.Enabled = showClear && !isBusy;
        }
    }

    private sealed class JsonFieldHolder : ConfigFieldHolderBase
    {
        private readonly TextView _valueView;
        private readonly MaterialButton _editButton;
        private readonly MaterialButton _resetButton;

        public JsonFieldHolder(
            View itemView,
            Action<PluginConfigFieldView> onEditFieldRequested,
            Action<PluginConfigFieldView> onResetFieldRequested) : base(itemView)
        {
            _valueView = itemView.FindViewById<TextView>(Resource.Id.plugin_cfg_value)!;
            _editButton = itemView.FindViewById<MaterialButton>(Resource.Id.plugin_cfg_edit_btn)!;
            _resetButton = itemView.FindViewById<MaterialButton>(Resource.Id.plugin_cfg_reset_btn)!;

            _editButton.Click += (_, _) =>
            {
                if (BoundField is { } field && CanEdit)
                {
                    onEditFieldRequested(field);
                }
            };

            _resetButton.Click += (_, _) =>
            {
                if (BoundField is { } field && CanEdit && field.HasUserOverride)
                {
                    onResetFieldRequested(field);
                }
            };
        }

        protected override void BindSpecific(PluginConfigFieldView field, bool editable, bool isBusy)
        {
            var context = ItemView.Context!;
            var rawJson = field.CurrentValueJson ?? field.BaseValueJson;
            _valueView.Text = string.IsNullOrWhiteSpace(rawJson)
                ? context.GetString(Resource.String.plugin_detail_config_empty_value)
                : rawJson;

            _editButton.Visibility = editable ? ViewStates.Visible : ViewStates.Gone;
            _editButton.Enabled = editable && !isBusy;

            var showReset = editable && field.HasUserOverride;
            _resetButton.Visibility = showReset ? ViewStates.Visible : ViewStates.Gone;
            _resetButton.Enabled = showReset && !isBusy;
        }
    }
}
