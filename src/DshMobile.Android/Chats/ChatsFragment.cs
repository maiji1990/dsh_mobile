using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Presentation;
using DshMobile.App.Bridge;
using DshMobile.App.Chat;
using DshMobile.App.Ui;
using DshMobile.Runtime.Hosting;
using DshMobile.Runtime.Migration;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.FloatingActionButton;
using Google.Android.Material.ProgressIndicator;
using Google.Android.Material.Snackbar;
using Google.Android.Material.TextField;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace DshMobile.App.Chats;

/// <summary>
/// PR-05 原生会话列表页签（N-05-03 + N-05-05）：
/// 承载新建、搜索、重命名、Fork、状态/待审批角标、按能力停止，以及旧 Web Profile 只读盘点提示。
/// </summary>
public sealed class ChatsFragment : Fragment, ITabRoot
{
    private BridgeHost _bridge = null!;
    private SessionsViewModel _vm = null!;
    private ChatsAdapter _adapter = null!;
    private LinearProgressIndicator _progress = null!;
    private View _hostBanner = null!;
    private MaterialButton _hostAction = null!;
    private View _webInvCard = null!;
    private TextView _webInvSummary = null!;
    private RecyclerView _list = null!;
    private View _empty = null!;
    private FloatingActionButton _newFab = null!;
    private int _renderPosted;
    private WebProfileInventoryReport? _lastWebInventory;
    private int _inventoryChecked;

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        var view = inflater.Inflate(Resource.Layout.fragment_chats, container, false)!;
        _bridge = DshMobileApplication.Current.Bridge;
        _vm = _bridge.Agent.Sessions;

        var toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.chats_toolbar)!;
        toolbar.InflateMenu(Resource.Menu.chat_toolbar);
        toolbar.Menu!.FindItem(Resource.Id.chat_menu_switch_model)?.SetVisible(false);
        toolbar.Menu.FindItem(Resource.Id.chat_menu_cancel)?.SetVisible(false);
        toolbar.Menu.FindItem(Resource.Id.chat_menu_new_session)?.SetVisible(false);
        toolbar.MenuItemClick += (_, e) =>
        {
            var id = e.Item?.ItemId ?? 0;
            if (id == Resource.Id.chat_menu_refresh)
            {
                _ = RefreshIfReadyAsync();
            }
            else if (id == Resource.Id.chat_menu_credentials)
            {
                StartActivity(new Intent(RequireContext(), typeof(Credentials.CredentialsActivity)));
            }
        };

        _progress = view.FindViewById<LinearProgressIndicator>(Resource.Id.chats_progress)!;
        _hostBanner = view.FindViewById(Resource.Id.chats_host_banner)!;
        _hostAction = view.FindViewById<MaterialButton>(Resource.Id.chats_host_action)!;
        _hostAction.Click += (_, _) => _ = _bridge.RunStartAsync();

        _webInvCard = view.FindViewById(Resource.Id.chats_web_inventory_card)!;
        _webInvSummary = view.FindViewById<TextView>(Resource.Id.chats_web_inventory_summary)!;
        view.FindViewById<MaterialButton>(Resource.Id.chats_web_inventory_dismiss)!.Click += (_, _) =>
        {
            UiPrefs.SetWebInventoryDismissed(RequireContext(), true);
            _webInvCard.Visibility = ViewStates.Gone;
        };
        view.FindViewById<MaterialButton>(Resource.Id.chats_web_inventory_details)!.Click += (_, _) => ShowWebInventoryDetails();

        var search = view.FindViewById<TextInputEditText>(Resource.Id.chats_search_input)!;
        search.TextChanged += (_, e) => _vm.SetSearchQuery(e.Text?.ToString());

        _list = view.FindViewById<RecyclerView>(Resource.Id.chats_list)!;
        _list.SetLayoutManager(new LinearLayoutManager(RequireContext()));
        _adapter = new ChatsAdapter(
            item => OpenChatSession(item.SessionId),
            ShowRenameDialog,
            item => _ = ForkAndOpenAsync(item.SessionId),
            item => _ = CancelSessionAsync(item.SessionId));
        _list.SetAdapter(_adapter);

        _empty = view.FindViewById(Resource.Id.chats_empty)!;
        _empty.FindViewById<ImageView>(Resource.Id.empty_icon)!.SetImageResource(Resource.Drawable.ic_chat);
        _empty.FindViewById<TextView>(Resource.Id.empty_text)!.SetText(Resource.String.chats_empty);

        _newFab = view.FindViewById<FloatingActionButton>(Resource.Id.chats_new_fab)!;
        _newFab.Click += (_, _) => _ = CreateAndOpenSessionAsync();

        return view;
    }

    public override void OnStart()
    {
        base.OnStart();
        _vm.StateChanged += OnStateChanged;
        _bridge.Changed += OnBridgeChanged;
        Render(_vm.State);
        _ = RefreshIfReadyAsync();
    }

    public override void OnStop()
    {
        _vm.StateChanged -= OnStateChanged;
        _bridge.Changed -= OnBridgeChanged;
        base.OnStop();
    }

    public override void OnHiddenChanged(bool hidden)
    {
        base.OnHiddenChanged(hidden);
        if (!hidden)
        {
            Render(_vm.State);
            _ = RefreshIfReadyAsync();
        }
    }

    public void OnReselected()
    {
        _list.SmoothScrollToPosition(0);
        _ = RefreshIfReadyAsync();
    }

    private void OnStateChanged(SessionsUiState _) => ScheduleRender();

    private void OnBridgeChanged(object? sender, EventArgs e)
    {
        if (_bridge.Runtime.Host?.State == DshHostState.Ready && !_vm.State.IsConnected && !_vm.State.IsLoading)
        {
            _ = RefreshIfReadyAsync();
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

    private void Render(SessionsUiState state)
    {
        var hostReady = _bridge.Runtime.Host?.State == DshHostState.Ready;
        _progress.Visibility = state.IsLoading ? ViewStates.Visible : ViewStates.Gone;
        _hostBanner.Visibility = hostReady ? ViewStates.Gone : ViewStates.Visible;
        _hostAction.Enabled = !_bridge.IsBusy;
        _newFab.Enabled = hostReady;

        _adapter.Submit(state.Sessions, state.ActiveSessionId, state.CanCancelSession);
        _empty.Visibility = state.Sessions.Count == 0 ? ViewStates.Visible : ViewStates.Gone;

        RenderWebInventoryCard();
    }

    private void RenderWebInventoryCard()
    {
        var report = Volatile.Read(ref _lastWebInventory);
        if (report is null ||
            !report.HasUserDshDirectory ||
            (report.DiscoveredSessions.Count == 0 && !report.HasCredentialsFile) ||
            UiPrefs.WebInventoryDismissed(RequireContext()))
        {
            _webInvCard.Visibility = ViewStates.Gone;
            return;
        }
        _webInvCard.Visibility = ViewStates.Visible;
        var resId = report.HasCredentialsFile ? Resource.String.web_inv_summary_creds : Resource.String.web_inv_summary;
        _webInvSummary.Text = Rows.Fmt(RequireContext(), resId, report.RecoverableCount, report.UnrecognizedOrLegacyCount);
    }

    private async Task RefreshIfReadyAsync()
    {
        if (_bridge.Runtime.Host?.State == DshHostState.Ready)
        {
            try
            {
                await _vm.RefreshAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        if (Interlocked.Exchange(ref _inventoryChecked, 1) == 0 || _bridge.Runtime.Host?.State == DshHostState.Ready)
        {
            var report = await Task.Run(() => _bridge.Agent.InspectWebProfileReadOnly()).ConfigureAwait(false);
            Volatile.Write(ref _lastWebInventory, report);
            ScheduleRender();
        }
    }

    private void OpenChatSession(string sessionId)
    {
        var intent = new Intent(RequireContext(), typeof(ChatActivity));
        intent.PutExtra(ChatActivity.ExtraSessionId, sessionId);
        StartActivity(intent);
    }

    private async Task CreateAndOpenSessionAsync()
    {
        var created = await _vm.CreateSessionAsync().ConfigureAwait(false);
        Activity?.RunOnUiThread(() =>
        {
            if (View is null)
            {
                return;
            }
            if (created.IsOk)
            {
                OpenChatSession(created.Value!);
            }
            else if (created.Error is { } err)
            {
                Snackbar.Make(View, Rows.Fmt(RequireContext(), Resource.String.chat_error_banner, err.DiagnosticKey ?? err.Code.ToString()), Snackbar.LengthShort).Show();
            }
        });
    }

    private void ShowRenameDialog(SessionListItemView item)
    {
        var ctx = RequireContext();
        var content = LayoutInflater.From(ctx)!.Inflate(Resource.Layout.dialog_session_rename, null)!;
        var input = content.FindViewById<TextInputEditText>(Resource.Id.session_rename_input)!;
        input.Text = item.DisplayTitle;
        input.SetSelection(input.Text?.Length ?? 0);

        new MaterialAlertDialogBuilder(ctx)
            .SetTitle(Resource.String.chats_rename_title)!
            .SetView(content)!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(Resource.String.chats_rename_save, (_, _) =>
            {
                var nextTitle = input.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(nextTitle))
                {
                    _ = RenameSessionAndSyncAsync(item.SessionId, nextTitle);
                }
            })!
            .Show();
    }

    private async Task RenameSessionAndSyncAsync(string sessionId, string title)
    {
        var res = await _vm.RenameSessionAsync(sessionId, title).ConfigureAwait(false);
        if (res.IsOk && _bridge.Agent.Chat.State.ActiveSession.SessionId == sessionId)
        {
            await _bridge.Agent.Chat.RefreshHistoryAsync().ConfigureAwait(false);
        }
    }

    private async Task ForkAndOpenAsync(string sessionId)
    {
        var forked = await _vm.ForkSessionAsync(sessionId).ConfigureAwait(false);
        Activity?.RunOnUiThread(() =>
        {
            if (View is not null && forked.IsOk)
            {
                OpenChatSession(forked.Value!);
            }
        });
    }

    private async Task CancelSessionAsync(string sessionId)
    {
        var res = await _vm.CancelSessionAsync(sessionId).ConfigureAwait(false);
        Activity?.RunOnUiThread(() =>
        {
            if (View is { } v && !res.IsOk)
            {
                var msgRes = res.Error?.Code == DshFailureCode.Unsupported
                    ? Resource.String.chats_cancel_unsupported
                    : Resource.String.chat_error_banner;
                var msg = msgRes == Resource.String.chat_error_banner
                    ? Rows.Fmt(RequireContext(), msgRes, res.Error?.Code.ToString() ?? "ERROR")
                    : GetString(msgRes);
                Snackbar.Make(v, msg, Snackbar.LengthShort).Show();
            }
        });
    }

    private void ShowWebInventoryDetails()
    {
        var report = Volatile.Read(ref _lastWebInventory);
        if (report is null)
        {
            return;
        }
        var lines = new List<string> { GetString(Resource.String.web_inv_dialog_intro), string.Empty };
        foreach (var entry in report.DiscoveredSessions.Take(15))
        {
            var tag = entry.IsKnownToRemote ? "可直开" : "需迁移/只读保留";
            lines.Add($"• {entry.SessionId} [{tag}]");
        }
        if (report.DiscoveredSessions.Count > 15)
        {
            lines.Add($"… 其余 {report.DiscoveredSessions.Count - 15} 项");
        }

        new MaterialAlertDialogBuilder(RequireContext())
            .SetTitle(Resource.String.web_inv_dialog_title)!
            .SetMessage(string.Join('\n', lines))!
            .SetPositiveButton(Android.Resource.String.Ok, (_, _) => { })!
            .Show();
    }
}
