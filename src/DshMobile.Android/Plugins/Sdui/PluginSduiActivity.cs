using System.Text.Json;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;
using AndroidX.RecyclerView.Widget;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;
using DshMobile.Agent.Sdui;
using DshMobile.App.Bridge;
using DshMobile.App.Ui;
using Google.Android.Material.AppBar;
using Google.Android.Material.Chip;
using Google.Android.Material.Dialog;
using Google.Android.Material.ProgressIndicator;
using Google.Android.Material.Snackbar;
using Google.Android.Material.TextField;
using OperationCanceledException = System.OperationCanceledException;

namespace DshMobile.App.Plugins.Sdui;

/// <summary>
/// 实验性插件 SDUI 原生面板页。
/// 所有视图都来自既有 XML 布局，本类只负责绑定状态、转交动作与释放资源；
/// 表单明文和敏感字段不会写入状态、日志或生命周期字段。
/// </summary>
[Activity(
    Label = "@string/plugin_sdui_title",
    Exported = false,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden,
    WindowSoftInputMode = SoftInput.AdjustResize)]
internal sealed class PluginSduiActivity : AppCompatActivity, IPluginSduiAdapterCallbacks
{
    /// <summary>Intent 附加项：插件包名。</summary>
    public const string ExtraPackage = "extra_sdui_pkg";

    /// <summary>Intent 附加项：插件版本，可为空。</summary>
    public const string ExtraVersion = "extra_sdui_ver";

    private const int MaxTabChips = 8;

    /// <summary>从后台/息屏返回时刷新自动数据源的最小间隔，避免频繁切换造成重复请求。</summary>
    private const long ResumeRefreshCooldownMs = 3000;

    private readonly CancellationTokenSource _lifecycleCts = new();

    private BridgeHost _bridge = null!;
    private PluginSduiViewModel _viewModel = null!;
    private PluginSduiAdapter _adapter = null!;

    private View _root = null!;
    private MaterialToolbar _toolbar = null!;
    private LinearProgressIndicator _progress = null!;
    private View _tabScroll = null!;
    private ChipGroup _tabChips = null!;
    private Chip[] _tabChipViews = null!;
    private View _feedbackBanner = null!;
    private TextView _feedbackText = null!;
    private RecyclerView _recycler = null!;

    private string? _loadErrorText;
    private PendingSduiFormRequest? _shownFormRequest;
    private IDisposable? _activeFormDialog;
    private bool _isBindingTabs;
    private int _renderScheduled;
    private int _destroyed;
    private long _lastFeedbackSequence;
    private bool _hasLoadedDocument;
    private bool _hasCompletedInitialResume;
    private long _lastResumeRefreshElapsedMs;

    /// <summary>构造打开原生面板页的 Intent；版本为空时不携带版本参数。</summary>
    public static Intent CreateIntent(Context context, string packageName, string? version)
    {
        ArgumentNullException.ThrowIfNull(context);

        var intent = new Intent(context, typeof(PluginSduiActivity));
        intent.PutExtra(ExtraPackage, packageName);
        if (!string.IsNullOrWhiteSpace(version))
        {
            intent.PutExtra(ExtraVersion, version);
        }

        return intent;
    }

    /// <inheritdoc />
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var packageName = Intent?.GetStringExtra(ExtraPackage)?.Trim();
        if (!UiPrefs.ExperimentalPluginSduiEnabled(this) || string.IsNullOrWhiteSpace(packageName))
        {
            Finish();
            return;
        }

        var pluginVersion = Intent?.GetStringExtra(ExtraVersion)?.Trim();

        Edges.Enable(Window!);
        SetContentView(Resource.Layout.activity_plugin_sdui);
        BindViews();
        ConfigureToolbar();
        ConfigureRecycler();

        _bridge = DshMobileApplication.Current.Bridge;
        _viewModel = _bridge.Agent.CreatePluginSduiViewModel();
        _viewModel.StateChanged += OnStateChanged;

        _progress.Visibility = ViewStates.Visible;
        _ = Task.Run(() => LoadDocumentAsync(packageName, pluginVersion));
    }

    /// <inheritdoc />
    protected override void OnResume()
    {
        base.OnResume();

        if (_viewModel is null)
        {
            return;
        }

        // OnCreate 后紧跟的首轮 OnResume 只记账，避免与首次文档加载重复请求数据源。
        if (!_hasCompletedInitialResume)
        {
            _hasCompletedInitialResume = true;
            return;
        }

        // 仅在文档已就绪且当前没有加载/执行动作时，从息屏或后台返回才刷新自动数据源。
        if (!Volatile.Read(ref _hasLoadedDocument) ||
            _lifecycleCts.IsCancellationRequested ||
            _viewModel.State.IsLoading ||
            _viewModel.State.IsExecutingAction)
        {
            return;
        }

        var now = SystemClock.ElapsedRealtime();
        if (now - _lastResumeRefreshElapsedMs < ResumeRefreshCooldownMs)
        {
            return;
        }

        _lastResumeRefreshElapsedMs = now;
        _ = _viewModel.RefreshAutoLoadSourcesAsync(_lifecycleCts.Token);
    }

    /// <inheritdoc />
    protected override void OnDestroy()
    {
        if (Interlocked.Exchange(ref _destroyed, 1) == 0)
        {
            _lifecycleCts.Cancel();

            if (_viewModel is not null)
            {
                _viewModel.StateChanged -= OnStateChanged;
            }

            _shownFormRequest = null;
            _activeFormDialog?.Dispose();
            _activeFormDialog = null;

            if (_adapter is not null)
            {
                _recycler.SetAdapter(null);
            }

            if (_viewModel is not null)
            {
                _ = _viewModel.DisposeAsync().AsTask();
            }
        }

        base.OnDestroy();
    }

    /// <inheritdoc />
    public void OnAction(string actionId, JsonElement? item) =>
        _ = _viewModel.InvokeButtonActionAsync(actionId, item);

    /// <inheritdoc />
    public void OnSegmentedChanged(string stateKey, string value, string? onChangeActionId) =>
        _ = _viewModel.SelectSegmentedValueAsync(stateKey, value, onChangeActionId);

    /// <inheritdoc />
    public void OnExternalLink(string url) =>
        PluginBrowserDialogs.ConfirmAndOpenExternalHttpsUrl(this, _root, url);

    /// <inheritdoc />
    public void OnConfigBoolean(string settingsNamespace, PluginConfigFieldView field, bool value) =>
        _ = _viewModel.SaveSettingsBridgeFieldAsync(settingsNamespace, field, value ? "true" : "false");

    /// <inheritdoc />
    public void OnConfigEdit(string settingsNamespace, PluginConfigFieldView field)
    {
        var content = LayoutInflater.From(this)!.Inflate(Resource.Layout.dialog_text_input, null)!;
        var inputLayout = content.FindViewById<TextInputLayout>(Resource.Id.dialog_text_input_layout)!;
        var input = content.FindViewById<TextInputEditText>(Resource.Id.dialog_text_input)!;
        var isSecretField = field.Kind == PluginConfigFieldKind.Secret;
        inputLayout.ErrorEnabled = true;

        switch (field.Kind)
        {
            case PluginConfigFieldKind.Secret:
                input.InputType = InputTypes.ClassText | InputTypes.TextVariationPassword;
                input.SetSingleLine(true);
                inputLayout.Hint = GetString(Resource.String.plugin_detail_config_secret_input_hint);
                SecretInputProtection.AttachSecretInput(input);
                input.Text = string.Empty;
                break;

            case PluginConfigFieldKind.Number:
                input.InputType = InputTypes.ClassNumber | InputTypes.NumberFlagDecimal | InputTypes.NumberFlagSigned;
                input.SetSingleLine(true);
                inputLayout.Hint = field.KeyDisplay;
                input.Text = (field.CurrentValueJson ?? field.BaseValueJson)?.Trim() ?? string.Empty;
                break;

            case PluginConfigFieldKind.JsonFallback:
                input.InputType = InputTypes.ClassText | InputTypes.TextFlagMultiLine;
                input.SetSingleLine(false);
                input.SetMinLines(4);
                input.SetMaxLines(10);
                input.Typeface = Typeface.Monospace;
                inputLayout.Hint = field.KeyDisplay;
                input.Text = field.CurrentValueJson ?? field.BaseValueJson ?? "{}";
                break;

            default:
                input.InputType = InputTypes.ClassText;
                input.SetSingleLine(true);
                inputLayout.Hint = field.KeyDisplay;
                input.Text = PluginConfigAdapter.UnquoteJsonDisplay(
                    field.CurrentValueJson ?? field.BaseValueJson);
                break;
        }

        if (!string.IsNullOrEmpty(input.Text))
        {
            input.SetSelection(input.Text.Length);
        }

        var dialog = new MaterialAlertDialogBuilder(this)
            .SetTitle(field.Label)!
            .SetView(content)!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(Resource.String.plugin_detail_config_save, (_, _) => { })!
            .Create()!;

        if (isSecretField)
        {
            // 密钥弹窗在显示前加安全标志，并在关闭时统一清除输入与窗口标志。
            SecretInputProtection.ApplySecureWindowFlag(dialog);
            dialog.SetOnDismissListener(new DialogDismissListener(() =>
            {
                SecretInputProtection.ClearEditText(input);
                SecretInputProtection.ClearSecureWindowFlag(dialog);
            }));
        }

        dialog.Show();
        var saveButton = dialog.GetButton((int)DialogButtonType.Positive);
        if (saveButton is null)
        {
            return;
        }

        // 替换默认点击监听：校验失败时保留对话框并就地提示，不把无效值交给 ViewModel。
        saveButton.SetOnClickListener(new ButtonClickListener(view =>
        {
            var rawInput = input.Text ?? string.Empty;
            if (!PluginSettingsSchemaParser.TryEncodeFieldValue(field, rawInput, out _, out var errorKey))
            {
                inputLayout.Error = MapConfigError(errorKey);
                return;
            }

            inputLayout.Error = null;
            if (isSecretField)
            {
                SecretInputProtection.ClearEditText(input);
            }

            dialog.Dismiss();

            if (isSecretField)
            {
                SecretInputProtection.ClearSecureWindowFlag(dialog);
            }

            _ = _viewModel.SaveSettingsBridgeFieldAsync(settingsNamespace, field, rawInput);
        }));
    }

    /// <inheritdoc />
    public void OnConfigSelect(string settingsNamespace, PluginConfigFieldView field)
    {
        if (field.Options.Count == 0)
        {
            return;
        }

        var effectiveJson = (field.CurrentValueJson ?? field.BaseValueJson)?.Trim();
        var labels = new string[field.Options.Count];
        var checkedIndex = -1;

        for (var index = 0; index < field.Options.Count; index++)
        {
            var option = field.Options[index];
            labels[index] = string.IsNullOrWhiteSpace(option.Description)
                ? option.DisplayLabel
                : $"{option.DisplayLabel} ({option.Description})";

            if (checkedIndex < 0 &&
                !string.IsNullOrEmpty(effectiveJson) &&
                string.Equals(option.RawValueJson.Trim(), effectiveJson, StringComparison.Ordinal))
            {
                checkedIndex = index;
            }
        }

        new MaterialAlertDialogBuilder(this)
            .SetTitle(field.Label)!
            .SetSingleChoiceItems(labels, checkedIndex, (sender, args) =>
            {
                var selectedIndex = args.Which;
                (sender as IDialogInterface)?.Dismiss();
                if (selectedIndex >= 0 && selectedIndex < field.Options.Count)
                {
                    _ = _viewModel.SaveSettingsBridgeFieldAsync(
                        settingsNamespace,
                        field,
                        field.Options[selectedIndex].RawValueJson);
                }
            })!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .Show();
    }

    /// <inheritdoc />
    public void OnConfigReset(string settingsNamespace, PluginConfigFieldView field)
    {
        if (field.Kind != PluginConfigFieldKind.Secret)
        {
            _ = _viewModel.ResetSettingsBridgeFieldAsync(settingsNamespace, field);
            return;
        }

        new MaterialAlertDialogBuilder(this)
            .SetTitle(Resource.String.plugin_detail_config_secret_clear_title)!
            .SetMessage(Rows.Fmt(
                this,
                Resource.String.plugin_detail_config_secret_clear_msg,
                field.Label))!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(Resource.String.plugin_detail_config_secret_clear_confirm, (_, _) =>
            {
                _ = _viewModel.ResetSettingsBridgeFieldAsync(settingsNamespace, field);
            })!
            .Show();
    }

    private void BindViews()
    {
        _root = FindViewById<View>(Resource.Id.sdui_root)!;
        _toolbar = FindViewById<MaterialToolbar>(Resource.Id.sdui_toolbar)!;
        _progress = FindViewById<LinearProgressIndicator>(Resource.Id.sdui_progress)!;
        _tabScroll = FindViewById<View>(Resource.Id.sdui_tab_scroll)!;
        _tabChips = FindViewById<ChipGroup>(Resource.Id.sdui_tab_chips)!;
        _feedbackBanner = FindViewById<View>(Resource.Id.sdui_feedback_banner)!;
        _feedbackText = FindViewById<TextView>(Resource.Id.sdui_feedback_text)!;
        _recycler = FindViewById<RecyclerView>(Resource.Id.sdui_recycler)!;

        _tabChipViews =
        [
            FindViewById<Chip>(Resource.Id.sdui_tab_chip_0)!,
            FindViewById<Chip>(Resource.Id.sdui_tab_chip_1)!,
            FindViewById<Chip>(Resource.Id.sdui_tab_chip_2)!,
            FindViewById<Chip>(Resource.Id.sdui_tab_chip_3)!,
            FindViewById<Chip>(Resource.Id.sdui_tab_chip_4)!,
            FindViewById<Chip>(Resource.Id.sdui_tab_chip_5)!,
            FindViewById<Chip>(Resource.Id.sdui_tab_chip_6)!,
            FindViewById<Chip>(Resource.Id.sdui_tab_chip_7)!,
        ];

        for (var index = 0; index < _tabChipViews.Length; index++)
        {
            var chipIndex = index;
            _tabChipViews[index].Click += (_, _) => OnTabChipClicked(chipIndex);
        }

        // 统一处理状态栏、刘海与软键盘安全区；页面内容自行让位，避免被系统栏遮挡。
        Edges.Pad(_root);
        Edges.ClampMaxWidth(_recycler, _feedbackBanner);
    }

    private void ConfigureToolbar()
    {
        _toolbar.NavigationClick += (_, _) => Finish();
        _toolbar.MenuItemClick += (_, args) =>
        {
            if (args.Item?.ItemId == Resource.Id.sdui_action_refresh)
            {
                _ = _viewModel.RefreshAllAsync();
            }
        };
    }

    private void ConfigureRecycler()
    {
        _recycler.SetLayoutManager(new LinearLayoutManager(this));
        _adapter = new PluginSduiAdapter(this);
        _recycler.SetAdapter(_adapter);
    }

    private async Task LoadDocumentAsync(string packageName, string? pluginVersion)
    {
        var ct = _lifecycleCts.Token;
        try
        {
            var loadResult = await _bridge.Agent.PluginSduiStore
                .LoadAsync(packageName, pluginVersion, ct)
                .ConfigureAwait(false);

            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (!loadResult.Found || loadResult.Document is null)
            {
                RunOnUiThread(ShowLoadFailure);
                return;
            }

            _loadErrorText = null;
            await _viewModel.InitializeAsync(
                    loadResult.Document,
                    loadResult.Source ?? PluginSduiStore.CacheSource,
                    ct)
                .ConfigureAwait(false);
            Volatile.Write(ref _hasLoadedDocument, true);
        }
        catch (OperationCanceledException)
        {
            // 页面退出或生命周期结束时静默停止加载。
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!ct.IsCancellationRequested)
            {
                RunOnUiThread(ShowLoadFailure);
            }
        }
    }

    private void ShowLoadFailure()
    {
        if (Volatile.Read(ref _destroyed) != 0)
        {
            return;
        }

        _loadErrorText = GetString(Resource.String.plugin_sdui_source_err_msg);
        _toolbar.Subtitle = null;
        _progress.Visibility = ViewStates.Gone;
        _feedbackText.Text = _loadErrorText;
        _feedbackBanner.Visibility = ViewStates.Visible;
        _adapter.Submit([]);
        HideTabs();
        Snackbar.Make(_root, _loadErrorText, Snackbar.LengthLong).Show();
    }

    private void OnStateChanged(PluginSduiUiState _)
    {
        if (Volatile.Read(ref _destroyed) != 0)
        {
            return;
        }

        // 合并高频状态通知：同一次 UI 渲染只读取最新的不可变快照。
        if (Interlocked.Exchange(ref _renderScheduled, 1) != 0)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            Interlocked.Exchange(ref _renderScheduled, 0);
            if (Volatile.Read(ref _destroyed) != 0 || _viewModel is null)
            {
                return;
            }

            RenderState(_viewModel.State);
        });
    }

    private void RenderState(PluginSduiUiState state)
    {
        _progress.Visibility = state.IsLoading || state.IsExecutingAction
            ? ViewStates.Visible
            : ViewStates.Gone;

        _toolbar.Title = string.IsNullOrWhiteSpace(state.Title)
            ? GetString(Resource.String.plugin_sdui_title)
            : state.Title;
        _toolbar.Subtitle = ResolveSubtitle(state);

        RenderTabs(state);
        _adapter.Submit(state.RenderItems);
        RenderFeedback(state);
        RenderActionResultFeedback(state);
        SyncPendingForm(state);
        SyncPendingExternalUrl(state);
    }

    /// <summary>
    /// 仅在动作反馈序号自增时弹一次 Snackbar：成功短提示、失败长提示；
    /// 序号去重可避免配置变更或重复渲染导致同一结果反复弹出。
    /// </summary>
    private void RenderActionResultFeedback(PluginSduiUiState state)
    {
        if (state.ActionFeedbackSequence <= 0 ||
            state.ActionFeedbackSequence == _lastFeedbackSequence ||
            state.LastActionResult is not { } actionResult)
        {
            return;
        }

        _lastFeedbackSequence = state.ActionFeedbackSequence;
        Snackbar.Make(
                _root,
                actionResult.SummaryText,
                actionResult.Success ? Snackbar.LengthShort : Snackbar.LengthLong)
            .Show();
    }

    /// <summary>优先展示文档副标题，否则展示文档来源，便于区分缓存与包内置版本。</summary>
    private string? ResolveSubtitle(PluginSduiUiState state)
    {
        if (!string.IsNullOrWhiteSpace(state.Subtitle))
        {
            return state.Subtitle;
        }

        return state.DocumentSource switch
        {
            PluginSduiStore.CacheSource => GetString(Resource.String.plugin_sdui_source_cache),
            PluginSduiStore.ManifestSource => GetString(Resource.String.plugin_sdui_source_manifest),
            _ => null,
        };
    }

    private void RenderTabs(PluginSduiUiState state)
    {
        _isBindingTabs = true;
        try
        {
            var tabs = state.Tabs;
            var showTabs = tabs.Count > 1;
            _tabScroll.Visibility = showTabs ? ViewStates.Visible : ViewStates.Gone;
            _tabChips.Enabled = showTabs;

            var visibleCount = showTabs ? Math.Min(tabs.Count, MaxTabChips) : 0;
            for (var index = 0; index < _tabChipViews.Length; index++)
            {
                var chip = _tabChipViews[index];
                if (index >= visibleCount)
                {
                    chip.Checked = false;
                    chip.Visibility = ViewStates.Gone;
                    continue;
                }

                var tab = tabs[index];
                chip.Text = tab.Label;
                chip.Checked = string.Equals(tab.Id, state.ActiveTabId, StringComparison.Ordinal);
                chip.Visibility = ViewStates.Visible;
            }
        }
        finally
        {
            _isBindingTabs = false;
        }
    }

    private void HideTabs()
    {
        _isBindingTabs = true;
        try
        {
            _tabScroll.Visibility = ViewStates.Gone;
            _tabChips.Enabled = false;
            foreach (var chip in _tabChipViews)
            {
                chip.Checked = false;
                chip.Visibility = ViewStates.Gone;
            }
        }
        finally
        {
            _isBindingTabs = false;
        }
    }

    private void OnTabChipClicked(int index)
    {
        if (_isBindingTabs || _viewModel is null)
        {
            return;
        }

        var tabs = _viewModel.State.Tabs;
        if (index < 0 || index >= tabs.Count)
        {
            return;
        }

        _viewModel.SelectTab(tabs[index].Id);
    }

    private void RenderFeedback(PluginSduiUiState state)
    {
        string? message = null;
        if (!string.IsNullOrWhiteSpace(_loadErrorText))
        {
            message = _loadErrorText;
        }
        else if (!string.IsNullOrWhiteSpace(state.DiagnosticKey) ||
                 !string.IsNullOrWhiteSpace(state.FailureCode))
        {
            message = GetString(Resource.String.plugin_sdui_action_failed);
        }
        else if (!string.IsNullOrWhiteSpace(state.StatusMessage))
        {
            message = MapStatusMessage(state.StatusMessage);
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            _feedbackText.Text = string.Empty;
            _feedbackBanner.Visibility = ViewStates.Gone;
            return;
        }

        _feedbackText.Text = message;
        _feedbackBanner.Visibility = ViewStates.Visible;
    }

    private string MapStatusMessage(string statusMessage)
    {
        if (string.Equals(statusMessage, PluginSduiViewModel.SettingsSavedStatus, StringComparison.Ordinal))
        {
            return GetString(Resource.String.plugin_detail_config_saved_live);
        }

        return statusMessage.StartsWith("sdui.status.", StringComparison.Ordinal)
            ? GetString(Resource.String.plugin_sdui_action_done)
            : statusMessage;
    }

    private void SyncPendingForm(PluginSduiUiState state)
    {
        var pending = state.PendingFormRequest;
        if (pending is null || ReferenceEquals(pending, _shownFormRequest))
        {
            return;
        }

        if (_activeFormDialog is not null)
        {
            _shownFormRequest = null;
            var previousDialog = _activeFormDialog;
            _activeFormDialog = null;
            previousDialog.Dispose();
        }

        var request = pending;
        _shownFormRequest = request;
        _activeFormDialog = PluginSduiFormDialog.Show(
            this,
            request,
            values => _viewModel.SubmitFormAsync(request, values, _lifecycleCts.Token),
            () => HandleFormDismissed(request));
    }

    private void HandleFormDismissed(PendingSduiFormRequest request)
    {
        if (!ReferenceEquals(_shownFormRequest, request))
        {
            return;
        }

        _shownFormRequest = null;
        _activeFormDialog = null;
        _viewModel.DismissPendingForm();
    }

    private void SyncPendingExternalUrl(PluginSduiUiState state)
    {
        if (state.PendingExternalUrl is not { } pendingUrl)
        {
            return;
        }

        _viewModel.ClearPendingExternalUrl();
        PluginBrowserDialogs.ConfirmAndOpenExternalHttpsUrl(this, _root, pendingUrl.Url);
    }

    /// <summary>弹窗关闭时执行清理回调，确保密钥输入与窗口安全标志不会残留。</summary>
    private sealed class DialogDismissListener(Action onDismiss) : Java.Lang.Object, IDialogInterfaceOnDismissListener
    {
        public void OnDismiss(IDialogInterface? dialog) => onDismiss();
    }

    /// <summary>接管对话框正按钮点击，避免本地校验失败时弹窗被系统默认行为关闭。</summary>
    private sealed class ButtonClickListener(Action<View?> onClick) : Java.Lang.Object, View.IOnClickListener
    {
        public void OnClick(View? view) => onClick(view);
    }

    private string MapConfigError(string? errorKey) => errorKey switch
    {
        "settings.invalid_number" => GetString(Resource.String.plugin_detail_config_err_invalid_number),
        "settings.number_below_min" => GetString(Resource.String.plugin_detail_config_err_number_below_min),
        "settings.number_above_max" => GetString(Resource.String.plugin_detail_config_err_number_above_max),
        "settings.invalid_json" => GetString(Resource.String.plugin_detail_config_err_invalid_json),
        "settings.secret_empty" => GetString(Resource.String.plugin_detail_config_err_secret_empty),
        "settings.not_writable" or "settings.field_readonly"
            => GetString(Resource.String.plugin_detail_config_readonly_banner),
        _ => GetString(Resource.String.plugin_detail_config_err_invalid_value),
    };
}
