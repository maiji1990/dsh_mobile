using Android.Content;
using Android.Text;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Sdui;
using DshMobile.App.Ui;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.MaterialSwitch;
using Google.Android.Material.TextField;

namespace DshMobile.App.Plugins.Sdui;

/// <summary>
/// 插件 SDUI 表单弹窗（<c>open_form</c> 动作）：全部控件来自 XML 布局并按字段类型静态复用，
/// 代码中不实例化任何视图。用户输入只保存在本对象的内存字典中，提交成功或弹窗关闭时立即清空；
/// 密钥字段永不预填、永不回显，任何路径都不记录用户输入或异常细节。
/// </summary>
internal sealed class PluginSduiFormDialog : IDisposable
{
    private const int ViewTypeText = 0;
    private const int ViewTypeSelect = 1;
    private const int ViewTypeBool = 2;

    private readonly Context _context;
    private readonly PendingSduiFormRequest _request;
    private readonly Func<IReadOnlyDictionary<string, string>, Task<Outcome<Unit>>> _onSubmitAsync;
    private readonly Action _onDismissed;
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly List<EditText> _trackedInputs = [];
    private readonly AndroidX.AppCompat.App.AlertDialog _dialog;
    private readonly TextView _errorView;

    private int _isSubmitting;
    private int _dismissed;
    private int _disposed;

    private PluginSduiFormDialog(
        Context context,
        PendingSduiFormRequest request,
        Func<IReadOnlyDictionary<string, string>, Task<Outcome<Unit>>> onSubmitAsync,
        Action onDismissed)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _onSubmitAsync = onSubmitAsync ?? throw new ArgumentNullException(nameof(onSubmitAsync));
        _onDismissed = onDismissed ?? throw new ArgumentNullException(nameof(onDismissed));

        EnsureDefaultValues();

        var content = LayoutInflater.From(context)!.Inflate(Resource.Layout.dialog_sdui_form, null)!;
        _errorView = content.FindViewById<TextView>(Resource.Id.sdui_form_error)!;

        var list = content.FindViewById<RecyclerView>(Resource.Id.sdui_form_list)!;
        list.SetLayoutManager(new LinearLayoutManager(context));
        list.SetAdapter(new FormFieldsAdapter(_request.Fields, _values, _trackedInputs));

        var builder = new MaterialAlertDialogBuilder(context);
        builder.SetTitle(string.IsNullOrWhiteSpace(_request.Title)
            ? context.GetString(Resource.String.plugin_sdui_form_title)
            : _request.Title);
        builder.SetView(content);
        builder.SetNegativeButton(Resource.String.plugin_sdui_form_cancel, (_, _) => { });
        builder.SetPositiveButton(Resource.String.plugin_sdui_form_submit, (_, _) => { });

        _dialog = builder.Create()!;
        _dialog.SetOnDismissListener(new DialogDismissListener(this));
    }

    /// <summary>
    /// 弹出表单。调用方持有返回句柄，在页面销毁时释放即可关闭弹窗；
    /// <paramref name="onDismissed"/> 只在弹窗真正关闭时回调一次。
    /// </summary>
    public static IDisposable Show(
        Context context,
        PendingSduiFormRequest request,
        Func<IReadOnlyDictionary<string, string>, Task<Outcome<Unit>>> onSubmitAsync,
        Action onDismissed)
    {
        var dialog = new PluginSduiFormDialog(context, request, onSubmitAsync, onDismissed);
        dialog.ShowCore();
        return dialog;
    }

    /// <summary>关闭弹窗并释放内存中的输入；重复调用无副作用。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_dialog.IsShowing)
        {
            _dialog.Dismiss();
        }

        ClearSensitiveValues();
        SecretInputProtection.ClearSecureWindowFlag(_dialog);
        _dialog.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ShowCore()
    {
        // 含密钥字段时先给窗口加安全标志，避免弹窗内容进入截图或最近任务预览。
        if (_request.Fields.Any(IsSecretField))
        {
            SecretInputProtection.ApplySecureWindowFlag(_dialog);
        }

        _dialog.Show();

        // 用自定义监听替换默认「点后即关」行为：校验失败或提交失败时必须保持弹窗打开。
        if (_dialog.GetButton((int)DialogButtonType.Positive) is { } submit)
        {
            submit.Click += (_, _) => _ = SubmitAsync();
        }
    }

    private async Task SubmitAsync()
    {
        if (Interlocked.CompareExchange(ref _isSubmitting, 1, 0) != 0)
        {
            return;
        }

        ShowError(null);

        if (!TryValidate(out var errorMessage))
        {
            ShowError(errorMessage);
            Interlocked.Exchange(ref _isSubmitting, 0);
            return;
        }

        var submit = _dialog.GetButton((int)DialogButtonType.Positive);
        var succeeded = false;
        var snapshot = new Dictionary<string, string>(_values, StringComparer.Ordinal);

        if (submit is not null)
        {
            submit.Enabled = false;
        }

        try
        {
            var outcome = await _onSubmitAsync(snapshot);
            if (Volatile.Read(ref _disposed) != 0 || !_dialog.IsShowing)
            {
                return;
            }

            if (outcome.IsOk)
            {
                succeeded = true;
                ClearSensitiveValues();
                _dialog.Dismiss();
                return;
            }

            ShowError(_context.GetString(Resource.String.plugin_sdui_action_failed));
        }
        catch (Exception)
        {
            // 异常文本可能夹带用户输入；这里只展示脱敏提示，绝不记录或回显。
            if (Volatile.Read(ref _disposed) == 0 && _dialog.IsShowing)
            {
                ShowError(_context.GetString(Resource.String.plugin_sdui_action_failed));
            }
        }
        finally
        {
            // 提交用的临时快照立即丢弃；主字典在成功关闭或弹窗销毁时统一清空。
            snapshot.Clear();
            if (succeeded)
            {
                ClearSensitiveValues();
            }

            Interlocked.Exchange(ref _isSubmitting, 0);
            if (submit is not null &&
                Volatile.Read(ref _disposed) == 0 &&
                _dialog.IsShowing)
            {
                submit.Enabled = true;
            }
        }
    }

    private bool TryValidate(out string? errorMessage)
    {
        foreach (var field in _request.Fields)
        {
            var value = _values.TryGetValue(field.Key, out var current) ? current : string.Empty;

            if (field.Required && string.IsNullOrWhiteSpace(value))
            {
                errorMessage = _context.GetString(Resource.String.plugin_sdui_form_required);
                return false;
            }

            if (field.MaxLength is { } maxLength && maxLength >= 0 && value.Length > maxLength)
            {
                errorMessage = _context.GetString(Resource.String.plugin_sdui_form_invalid);
                return false;
            }
        }

        errorMessage = null;
        return true;
    }

    private void ShowError(string? message)
    {
        _errorView.Text = message ?? string.Empty;
        _errorView.Visibility = string.IsNullOrEmpty(message) ? ViewStates.Gone : ViewStates.Visible;
    }

    private void HandleDismissed()
    {
        if (Interlocked.Exchange(ref _dismissed, 1) != 0)
        {
            return;
        }

        ClearSensitiveValues();
        SecretInputProtection.ClearSecureWindowFlag(_dialog);
        _onDismissed();
    }

    private void ClearSensitiveValues()
    {
        // 先清空输入框：TextChanged 会把空串写回字典，最后再清字典避免残留键。
        foreach (var edit in _trackedInputs)
        {
            SecretInputProtection.ClearEditText(edit);
        }

        _values.Clear();
    }

    private void EnsureDefaultValues()
    {
        foreach (var field in _request.Fields)
        {
            _values[field.Key] = NormalizeType(field) switch
            {
                "bool" => ParseBoolean(field.DefaultValue) ? "true" : "false",
                "select" => ResolveSelectValue(field),
                "secret_input" => string.Empty,
                _ => field.DefaultValue ?? string.Empty,
            };
        }
    }

    private static int GetViewType(PendingSduiFormFieldView field) => NormalizeType(field) switch
    {
        "select" => ViewTypeSelect,
        "bool" => ViewTypeBool,
        _ => ViewTypeText,
    };

    private static string NormalizeType(PendingSduiFormFieldView field)
    {
        var raw = field.Type?.Trim() ?? string.Empty;
        if (raw.Length == 0)
        {
            return field.IsSecret ? "secret_input" : "text";
        }

        return raw.ToLowerInvariant() switch
        {
            "select" => "select",
            "bool" or "boolean" or "switch" => "bool",
            "number" or "integer" or "numeric" => "number",
            "secret_input" or "password" or "secret" => "secret_input",
            _ => field.IsSecret ? "secret_input" : "text",
        };
    }

    private static bool IsSecretField(PendingSduiFormFieldView field) =>
        field.IsSecret || string.Equals(NormalizeType(field), "secret_input", StringComparison.Ordinal);

    private static bool ParseBoolean(string? raw) => bool.TryParse(raw, out var parsed) && parsed;

    private static string ResolveSelectValue(PendingSduiFormFieldView field)
    {
        if (string.IsNullOrEmpty(field.DefaultValue))
        {
            return string.Empty;
        }

        foreach (var option in field.Options)
        {
            if (string.Equals(option.Value, field.DefaultValue, StringComparison.Ordinal))
            {
                return option.Value;
            }
        }

        return string.Empty;
    }

    private static string ResolveSelectText(PendingSduiFormFieldView field, string value)
    {
        foreach (var option in field.Options)
        {
            if (string.Equals(option.Value, value, StringComparison.Ordinal))
            {
                return string.IsNullOrWhiteSpace(option.Label) ? option.Value : option.Label;
            }
        }

        return string.IsNullOrWhiteSpace(field.Placeholder) ? field.Label : field.Placeholder;
    }

    private sealed class DialogDismissListener : Java.Lang.Object, IDialogInterfaceOnDismissListener
    {
        private readonly PluginSduiFormDialog _owner;

        public DialogDismissListener(PluginSduiFormDialog owner) => _owner = owner;

        public void OnDismiss(IDialogInterface? dialog) => _owner.HandleDismissed();
    }

    /// <summary>按字段类型复用三种行视图；所有值变更直接写回弹窗持有的输入字典。</summary>
    private sealed class FormFieldsAdapter(
        IReadOnlyList<PendingSduiFormFieldView> fields,
        Dictionary<string, string> values,
        List<EditText> trackedInputs) : RecyclerView.Adapter
    {
        public override int ItemCount => fields.Count;

        public override int GetItemViewType(int position) => GetViewType(fields[position]);

        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
        {
            var inflater = LayoutInflater.From(parent.Context)!;
            return viewType switch
            {
                ViewTypeSelect => new SelectHolder(
                    inflater.Inflate(Resource.Layout.item_sdui_form_select, parent, false)!,
                    values),
                ViewTypeBool => new BoolHolder(
                    inflater.Inflate(Resource.Layout.item_sdui_form_bool, parent, false)!,
                    values),
                _ => new TextHolder(
                    inflater.Inflate(Resource.Layout.item_sdui_form_text, parent, false)!,
                    values,
                    trackedInputs),
            };
        }

        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
        {
            var field = fields[position];
            switch (holder)
            {
                case TextHolder text:
                    text.Bind(field);
                    break;
                case SelectHolder select:
                    select.Bind(field);
                    break;
                case BoolHolder boolean:
                    boolean.Bind(field);
                    break;
            }
        }
    }

    private sealed class TextHolder : RecyclerView.ViewHolder
    {
        private readonly TextInputLayout _layout;
        private readonly TextInputEditText _edit;
        private readonly Dictionary<string, string> _values;
        private PendingSduiFormFieldView? _field;

        public TextHolder(View itemView, Dictionary<string, string> values, List<EditText> trackedInputs) : base(itemView)
        {
            _layout = itemView.FindViewById<TextInputLayout>(Resource.Id.sdui_form_input_layout)!;
            _edit = itemView.FindViewById<TextInputEditText>(Resource.Id.sdui_form_input_edit)!;
            _values = values;
            trackedInputs.Add(_edit);
            _edit.TextChanged += (_, _) =>
            {
                if (_field is { } field)
                {
                    _values[field.Key] = _edit.Text ?? string.Empty;
                }
            };
        }

        public void Bind(PendingSduiFormFieldView field)
        {
            _field = field;
            _layout.Hint = field.Label;

            var type = NormalizeType(field);
            if (IsSecretField(field))
            {
                // 密钥只接受即时输入：切换密码键盘类型，挂载无障碍掩码代理，且不预填回显。
                _edit.InputType = InputTypes.ClassText | InputTypes.TextVariationPassword;
                SecretInputProtection.AttachSecretInput(_edit);
                _edit.Text = string.Empty;
            }
            else
            {
                // 复用行视图时必须解除密钥保护，恢复普通文本的自动填充与无障碍代理。
                SecretInputProtection.DetachSecretInput(_edit);

                if (string.Equals(type, "number", StringComparison.Ordinal))
                {
                    _edit.InputType = InputTypes.ClassNumber | InputTypes.NumberFlagDecimal | InputTypes.NumberFlagSigned;
                    _edit.Text = field.DefaultValue ?? string.Empty;
                }
                else
                {
                    _edit.InputType = InputTypes.ClassText;
                    _edit.Text = field.DefaultValue ?? string.Empty;
                }
            }

            _values[field.Key] = _edit.Text ?? string.Empty;
        }
    }

    private sealed class SelectHolder : RecyclerView.ViewHolder
    {
        private readonly TextView _label;
        private readonly MaterialButton _button;
        private readonly Dictionary<string, string> _values;
        private PendingSduiFormFieldView? _field;

        public SelectHolder(View itemView, Dictionary<string, string> values) : base(itemView)
        {
            _label = itemView.FindViewById<TextView>(Resource.Id.sdui_form_select_label)!;
            _button = itemView.FindViewById<MaterialButton>(Resource.Id.sdui_form_select_btn)!;
            _values = values;
            _button.Click += (_, _) => ShowOptions();
        }

        public void Bind(PendingSduiFormFieldView field)
        {
            _field = field;
            _label.Text = field.Label;
            var value = _values.TryGetValue(field.Key, out var current) ? current : string.Empty;
            _button.Text = ResolveSelectText(field, value);
        }

        private void ShowOptions()
        {
            if (_field is not { } field || field.Options.Count == 0)
            {
                return;
            }

            var labels = new string[field.Options.Count];
            var checkedIndex = -1;
            var currentValue = _values.TryGetValue(field.Key, out var current) ? current : string.Empty;

            for (var i = 0; i < field.Options.Count; i++)
            {
                var option = field.Options[i];
                labels[i] = string.IsNullOrWhiteSpace(option.Label) ? option.Value : option.Label;
                if (checkedIndex < 0 &&
                    string.Equals(option.Value, currentValue, StringComparison.Ordinal))
                {
                    checkedIndex = i;
                }
            }

            var context = ItemView.Context;
            if (context is null)
            {
                return;
            }

            var builder = new MaterialAlertDialogBuilder(context);
            builder.SetTitle(string.IsNullOrWhiteSpace(field.Label)
                ? context.GetString(Resource.String.plugin_sdui_form_title)
                : field.Label);
            builder.SetSingleChoiceItems(labels, checkedIndex, (sender, args) =>
            {
                var index = args.Which;
                if (index >= 0 && index < field.Options.Count)
                {
                    var option = field.Options[index];
                    _values[field.Key] = option.Value;
                    _button.Text = ResolveSelectText(field, option.Value);
                }

                (sender as IDialogInterface)?.Dismiss();
            });
            builder.SetNegativeButton(Resource.String.plugin_sdui_form_cancel, (_, _) => { });
            builder.Show();
        }
    }

    private sealed class BoolHolder : RecyclerView.ViewHolder
    {
        private readonly TextView _label;
        private readonly MaterialSwitch _toggle;
        private readonly Dictionary<string, string> _values;
        private PendingSduiFormFieldView? _field;
        private bool _binding;

        public BoolHolder(View itemView, Dictionary<string, string> values) : base(itemView)
        {
            _label = itemView.FindViewById<TextView>(Resource.Id.sdui_form_bool_label)!;
            _toggle = itemView.FindViewById<MaterialSwitch>(Resource.Id.sdui_form_bool_switch)!;
            _values = values;
            _toggle.CheckedChange += (_, args) =>
            {
                if (_binding || _field is not { } field)
                {
                    return;
                }

                _values[field.Key] = args.IsChecked ? "true" : "false";
            };
        }

        public void Bind(PendingSduiFormFieldView field)
        {
            _field = field;
            _label.Text = field.Label;

            var value = _values.TryGetValue(field.Key, out var current) ? current : string.Empty;
            _binding = true;
            _toggle.Checked = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            _binding = false;

            _values[field.Key] = _toggle.Checked ? "true" : "false";
        }
    }
}
