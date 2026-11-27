using Android.Views;
using AndroidX.RecyclerView.Widget;
using DshMobile.Agent.Presentation;
using DshMobile.Agent.Protocol;
using DshMobile.App.Bridge;
using DshMobile.App.Ui;
using DshMobile.Runtime.Hosting;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.ProgressIndicator;
using Google.Android.Material.Snackbar;
using Google.Android.Material.TextField;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace DshMobile.App.Credentials;

/// <summary>
/// 原生 Provider 与 API Key 凭据配置 Fragment：
/// 全 XML 视图渲染，通过 <see cref="CredentialsViewModel"/> 写入 DSH，绝不回显或持久化明文密钥；
/// 刷新与新增自定义引用均固定在顶栏，无悬浮按钮遮挡列表底部。
/// </summary>
public sealed class CredentialsFragment : Fragment
{
    private BridgeHost _bridge = null!;
    private CredentialsViewModel _vm = null!;
    private CredentialsAdapter _adapter = null!;
    private MaterialToolbar _toolbar = null!;
    private IMenuItem? _addMenuItem;
    private LinearProgressIndicator _progress = null!;
    private View _hostBanner = null!;
    private MaterialButton _startDshBtn = null!;
    private int _renderPosted;
    private string? _lastToastStatusKey;

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        var view = inflater.Inflate(Resource.Layout.fragment_credentials, container, false)!;
        _bridge = DshMobileApplication.Current.Bridge;
        _vm = _bridge.Agent.Credentials;

        _toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.credentials_toolbar)!;
        _addMenuItem = _toolbar.Menu?.FindItem(Resource.Id.credentials_menu_add);
        _addMenuItem?.SetVisible(false);
        _toolbar.NavigationClick += (_, _) => Activity?.Finish();
        _toolbar.MenuItemClick += (_, e) =>
        {
            var id = e.Item?.ItemId ?? 0;
            if (id == Resource.Id.credentials_menu_add)
            {
                ShowCredentialInputDialog(fixedEntry: null);
            }
            else if (id == Resource.Id.credentials_menu_refresh)
            {
                _ = RefreshWhenReadyAsync();
            }
        };

        _progress = view.FindViewById<LinearProgressIndicator>(Resource.Id.credentials_progress)!;
        _hostBanner = view.FindViewById(Resource.Id.credentials_host_banner)!;
        _startDshBtn = view.FindViewById<MaterialButton>(Resource.Id.credentials_start_dsh_btn)!;

        _startDshBtn.Click += (_, _) => _ = _bridge.RunStartAsync();

        var list = view.FindViewById<RecyclerView>(Resource.Id.credentials_list)!;
        list.SetLayoutManager(new LinearLayoutManager(RequireContext()));
        _adapter = new CredentialsAdapter(
            entry => ShowCredentialInputDialog(entry),
            entry => ConfirmClearCredential(entry));
        list.SetAdapter(_adapter);

        Edges.ClampMaxWidth(list);
        Edges.ClampMaxWidthByMargin(view.FindViewById(Resource.Id.credentials_intro_card));

        return view;
    }

    public override void OnStart()
    {
        base.OnStart();
        _vm.StateChanged += OnStateChanged;
        _bridge.Changed += OnBridgeChanged;
        Render(_vm.State);
        _ = RefreshWhenReadyAsync();
    }

    public override void OnStop()
    {
        _vm.StateChanged -= OnStateChanged;
        _bridge.Changed -= OnBridgeChanged;
        base.OnStop();
    }

    private void OnStateChanged(CredentialsUiState _) => ScheduleRender();

    private void OnBridgeChanged(object? sender, EventArgs e)
    {
        if (_bridge.Runtime.Host?.State == DshHostState.Ready &&
            _vm.State.Entries.Count == 0 &&
            !_vm.State.IsLoading)
        {
            _ = RefreshWhenReadyAsync();
        }
        ScheduleRender();
    }

    private void ScheduleRender()
    {
        if (Interlocked.Exchange(ref _renderPosted, 1) == 0)
        {
            Activity?.RunOnUiThread(() =>
            {
                Volatile.Write(ref _renderPosted, 0);
                if (View is not null)
                {
                    Render(_vm.State);
                }
            });
        }
    }

    private void Render(CredentialsUiState state)
    {
        var hostReady = _bridge.Runtime.Host?.State == DshHostState.Ready;
        _progress.Visibility = state.IsLoading || state.IsSaving ? ViewStates.Visible : ViewStates.Gone;
        _hostBanner.Visibility = hostReady ? ViewStates.Gone : ViewStates.Visible;
        _startDshBtn.Enabled = !_bridge.IsBusy;
        _toolbar.Menu?.FindItem(Resource.Id.credentials_menu_refresh)?.SetEnabled(hostReady && !state.IsLoading && !state.IsSaving);
        _addMenuItem?.SetVisible(hostReady);
        _addMenuItem?.SetEnabled(hostReady && !state.IsSaving);
        _adapter.Submit(state.Entries);

        if (View is { } root)
        {
            if (!string.IsNullOrWhiteSpace(state.FailureCode))
            {
                Snackbar.Make(root, Rows.Fmt(RequireContext(), Resource.String.chat_error_banner, state.FailureCode), Snackbar.LengthShort).Show();
            }
            else if (state.StatusKey is not null && !string.Equals(state.StatusKey, _lastToastStatusKey, StringComparison.Ordinal))
            {
                _lastToastStatusKey = state.StatusKey;
                var msgRes = state.StatusKey == "credentials.status.cleared"
                    ? Resource.String.credentials_status_cleared
                    : Resource.String.credentials_status_saved;
                Snackbar.Make(root, msgRes, Snackbar.LengthShort).Show();
                _ = _bridge.Agent.Chat.RefreshCredentialReadinessAsync();
            }
            else if (state.StatusKey is null)
            {
                _lastToastStatusKey = null;
            }
        }
    }

    private async Task RefreshWhenReadyAsync()
    {
        if (_bridge.Runtime.Host?.State != DshHostState.Ready)
        {
            return;
        }
        try
        {
            await _vm.RefreshAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ShowCredentialInputDialog(CredentialEntryView? fixedEntry)
    {
        var ctx = RequireContext();
        var content = LayoutInflater.From(ctx)!.Inflate(Resource.Layout.dialog_credential_input, null)!;
        var refLayout = content.FindViewById<TextInputLayout>(Resource.Id.cred_dialog_ref_layout)!;
        var refInput = content.FindViewById<TextInputEditText>(Resource.Id.cred_dialog_ref_input)!;
        var secretInput = content.FindViewById<TextInputEditText>(Resource.Id.cred_dialog_secret_input)!;

        if (fixedEntry is not null)
        {
            refInput.Text = fixedEntry.Ref;
            refInput.Enabled = false;
            refLayout.Enabled = false;
        }

        var title = fixedEntry?.DisplayName ?? GetString(Resource.String.credentials_add_custom);
        new MaterialAlertDialogBuilder(ctx)
            .SetTitle(title)!
            .SetView(content)!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) =>
            {
                secretInput.Text = string.Empty;
            })!
            .SetPositiveButton(Resource.String.credentials_dialog_save, (_, _) =>
            {
                var targetRef = fixedEntry?.Ref ?? refInput.Text?.Trim() ?? string.Empty;
                var inputSecret = secretInput.Text ?? string.Empty;
                secretInput.Text = string.Empty;
                if (!RemoteDshGatewayClient.IsValidCredentialRef(targetRef))
                {
                    if (View is { } root)
                    {
                        Snackbar.Make(root, Resource.String.credentials_invalid_ref, Snackbar.LengthShort).Show();
                    }
                    return;
                }
                if (!string.IsNullOrWhiteSpace(inputSecret))
                {
                    _ = _vm.SaveCredentialAsync(targetRef, inputSecret);
                }
            })!
            .Show();
    }

    private void ConfirmClearCredential(CredentialEntryView entry)
    {
        var ctx = RequireContext();
        new MaterialAlertDialogBuilder(ctx)
            .SetTitle(Resource.String.credentials_dialog_clear_title)!
            .SetMessage(Resource.String.credentials_dialog_clear_msg)!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(Resource.String.credentials_action_clear, (_, _) =>
            {
                _ = _vm.ClearCredentialAsync(entry.Ref);
            })!
            .Show();
    }
}
