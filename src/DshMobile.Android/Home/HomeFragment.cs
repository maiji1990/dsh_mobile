using Android.Content;
using Android.Content.Res;
using Android.Views;
using DshMobile.App.Automation;
using DshMobile.App.Bridge;
using DshMobile.App.Plugins;
using DshMobile.App.Remote;
using DshMobile.App.RuntimeUi;
using DshMobile.App.Ui;
using DshMobile.Application.Ui;
using DshMobile.Runtime.Automation.Relay;
using Google.Android.Material.Button;
using Google.Android.Material.Card;
using Google.Android.Material.ProgressIndicator;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace DshMobile.App.Home;

/// <summary>
/// Home tab (ADR-0021): one status card that says where DSH is and offers the one next step (import → start → open),
/// a notice for an operation that failed without the journal showing it, and shortcuts to the feature screens. Words,
/// colours and buttons come from <see cref="HomeStatus"/>; the runtime journal stays the source of truth.
/// </summary>
public sealed class HomeFragment : Fragment, ITabRoot
{
    private BridgeHost _bridge = null!;
    private MaterialCardView _card = null!;
    private ImageView _icon = null!;
    private TextView _label = null!, _title = null!, _detail = null!, _failureTitle = null!, _failureDetail = null!;
    private LinearProgressIndicator _progress = null!;
    private MaterialButton _primary = null!, _secondary = null!;
    private View _failure = null!;
    private TextView? _automationSubtitle;
    private HomeStatus? _shown;
    private int _renderPosted;

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        var view = inflater.Inflate(Resource.Layout.fragment_home, container, false)!;
        _bridge = DshMobileApplication.Current.Bridge;
        _card = view.FindViewById<MaterialCardView>(Resource.Id.status_card)!;
        _icon = view.FindViewById<ImageView>(Resource.Id.status_icon)!;
        _label = view.FindViewById<TextView>(Resource.Id.status_label)!;
        _title = view.FindViewById<TextView>(Resource.Id.status_title)!;
        _detail = view.FindViewById<TextView>(Resource.Id.status_detail)!;
        _progress = view.FindViewById<LinearProgressIndicator>(Resource.Id.status_progress)!;
        _primary = view.FindViewById<MaterialButton>(Resource.Id.status_primary)!;
        _secondary = view.FindViewById<MaterialButton>(Resource.Id.status_secondary)!;
        _failure = view.FindViewById(Resource.Id.failure_card)!;
        _failureTitle = view.FindViewById<TextView>(Resource.Id.failure_title)!;
        _failureDetail = view.FindViewById<TextView>(Resource.Id.failure_detail)!;
        _primary.Click += (_, _) => Act(_shown?.Primary ?? HomeAction.None);
        _secondary.Click += (_, _) => Act(_shown?.Secondary ?? HomeAction.None);

        var grid = view.FindViewById<GridLayout>(Resource.Id.shortcuts)!;
        _automationSubtitle = Shortcut(grid, Resource.Drawable.ic_phone, Resource.String.more_automation, Resource.String.home_shortcut_automation_sub, typeof(AutomationActivity));
        Shortcut(grid, Resource.Drawable.ic_cloud, Resource.String.more_remote, Resource.String.home_shortcut_remote_sub, typeof(RemoteActivity));
        Shortcut(grid, Resource.Drawable.ic_extension, Resource.String.more_plugins, Resource.String.home_shortcut_plugins_sub, typeof(PluginsActivity));
        Shortcut(grid, Resource.Drawable.ic_storage, Resource.String.more_runtime, Resource.String.home_shortcut_runtime_sub, typeof(RuntimeActivity));
        return view;
    }

    public override void OnStart()
    {
        base.OnStart();
        _bridge.Changed += OnBridgeChanged;
        Render();
    }

    public override void OnStop()
    {
        _bridge.Changed -= OnBridgeChanged;
        base.OnStop();
    }

    public override void OnHiddenChanged(bool hidden)
    {
        base.OnHiddenChanged(hidden);
        if (!hidden)
        {
            Render();
        }
    }

    public void OnReselected() => View?.FindViewById<AndroidX.Core.Widget.NestedScrollView>(Resource.Id.home_scroll)?.SmoothScrollTo(0, 0);

    private void OnBridgeChanged(object? sender, EventArgs e)
    {
        // Coalesce bursts (log lines from many threads) into one UI-thread render.
        if (Interlocked.Exchange(ref _renderPosted, 1) == 0)
        {
            Activity?.RunOnUiThread(() =>
            {
                Volatile.Write(ref _renderPosted, 0);
                if (View is not null)
                {
                    Render();
                }
            });
        }
    }

    private void Render()
    {
        var ctx = RequireContext();
        var s = _bridge.Status;
        _shown = s;
        var version = s.DshVersion;

        int label, title;
        string detail;
        var (container, onContainer, icon, iconTint) = (Resource.Attribute.colorSurfaceContainerHigh, Resource.Attribute.colorOnSurface, Resource.Drawable.ic_stop, Resource.Attribute.colorOutline);
        var running = false;
        switch (s.Phase)
        {
            case HomePhase.NotInstalled:
                (label, title, icon) = (Resource.String.home_label_not_installed, Resource.String.home_title_not_installed, Resource.Drawable.ic_download);
                detail = GetString(Resource.String.home_detail_not_installed);
                break;
            case HomePhase.Stopped:
                (label, title) = (Resource.String.home_label_stopped, Resource.String.home_title_stopped);
                detail = version is null ? GetString(Resource.String.home_detail_stopped) : Rows.Fmt(ctx, Resource.String.home_detail_stopped_version, version);
                break;
            case HomePhase.Starting:
                (label, title, icon, iconTint) = (Resource.String.home_label_starting, Resource.String.home_title_starting, Resource.Drawable.ic_play, Resource.Attribute.colorPrimary);
                detail = GetString(Resource.String.home_detail_starting);
                break;
            case HomePhase.WaitingForWebUi:
                (label, title, icon, iconTint) = (Resource.String.home_label_starting, Resource.String.home_title_waiting, Resource.Drawable.ic_play, Resource.Attribute.colorPrimary);
                detail = GetString(Resource.String.home_detail_waiting);
                break;
            case HomePhase.Ready:
                (label, title, icon) = (Resource.String.home_label_ready, Resource.String.home_title_ready, Resource.Drawable.ic_check_circle);
                (container, onContainer, running) = (Resource.Attribute.colorPrimaryContainer, Resource.Attribute.colorOnPrimaryContainer, true);
                detail = Rows.Fmt(ctx, Resource.String.home_detail_ready, version ?? "?");
                break;
            case HomePhase.Stopping:
                (label, title) = (Resource.String.home_label_stopping, Resource.String.home_title_stopping);
                detail = GetString(Resource.String.home_detail_stopping);
                break;
            case HomePhase.Busy:
                (label, title, icon, iconTint) = (Resource.String.home_label_busy, 0, Resource.Drawable.ic_refresh, Resource.Attribute.colorPrimary);
                detail = BusyDetail(ctx);
                break;
            default:
                (label, title, icon, iconTint) = (Resource.String.home_label_failed, Resource.String.home_title_failed, Resource.Drawable.ic_error, Resource.Attribute.colorError);
                detail = OperationNames.Error(s.ErrorCode ?? "?", s.ErrorMessage);
                break;
        }

        _card.SetCardBackgroundColor(Rows.Color(_card, container));
        var text = Rows.Color(_card, onContainer);
        foreach (var t in new[] { _label, _title, _detail })
        {
            t.SetTextColor(new Android.Graphics.Color(text));
        }
        _icon.SetImageResource(icon);
        _icon.ImageTintList = ColorStateList.ValueOf(new Android.Graphics.Color(running
            ? ctx.GetColor(Resource.Color.dshm_running)
            : Rows.Color(_card, iconTint)));
        if (s.Phase == HomePhase.Failed)
        {
            // A neutral card with the error colour on the icon and label only: the retry button keeps its normal colour.
            _label.SetTextColor(new Android.Graphics.Color(Rows.Color(_card, Resource.Attribute.colorError)));
        }
        _label.SetText(label);
        _title.Text = title == 0 ? Rows.Fmt(ctx, Resource.String.home_title_busy, OperationNames.Label(ctx, s.Operation ?? "")) : GetString(title);
        _detail.Text = detail;

        var busy = s.Phase is HomePhase.Starting or HomePhase.WaitingForWebUi or HomePhase.Stopping or HomePhase.Busy;
        _progress.Visibility = busy ? ViewStates.Visible : ViewStates.Gone;
        if (busy && _bridge.Progress is { Total: > 0 } p)
        {
            _progress.SetProgressCompat((int)Math.Clamp(p.Done * 100 / p.Total, 0, 100), true);
            _progress.Indeterminate = false;
        }
        else if (busy && !_progress.Indeterminate)
        {
            _progress.Visibility = ViewStates.Invisible; // switching back to indeterminate needs the view hidden
            _progress.Indeterminate = true;
            _progress.Visibility = ViewStates.Visible;
        }

        Button(_primary, s.Primary);
        Button(_secondary, s.Secondary);

        if (s.FailedOperation is { } f)
        {
            _failure.Visibility = ViewStates.Visible;
            _failureTitle.Text = Rows.Fmt(ctx, Resource.String.home_failed_operation, OperationNames.Label(ctx, f.Operation));
            _failureDetail.Text = OperationNames.Error(f.Code, f.Message);
        }
        else
        {
            _failure.Visibility = ViewStates.Gone;
        }

        if (_automationSubtitle is not null)
        {
            _automationSubtitle.SetText(DshMobile.App.Automation.Relay.CsRelay.Phase == RelayRuntimePhase.Stopped
                ? Resource.String.home_shortcut_automation_sub
                : Resource.String.home_shortcut_automation_on);
        }
    }

    private string BusyDetail(Context ctx)
    {
        if (_bridge.Progress is { } p)
        {
            return p.Total > 0
                ? Rows.Fmt(ctx, Resource.String.home_detail_progress, p.Step, (int)Math.Clamp(p.Done * 100 / p.Total, 0, 100))
                : p.Step;
        }
        return GetString(Resource.String.home_detail_busy);
    }

    private void Button(MaterialButton b, HomeAction action)
    {
        if (action == HomeAction.None)
        {
            b.Visibility = ViewStates.Gone;
            return;
        }
        b.Visibility = ViewStates.Visible;
        var (text, icon) = action switch
        {
            HomeAction.ImportRuntime => (Resource.String.home_action_import, Resource.Drawable.ic_upload),
            HomeAction.ImportDsh => (Resource.String.home_action_import_dsh, Resource.Drawable.ic_download),
            HomeAction.Start => (_shown?.Phase == HomePhase.Failed ? Resource.String.home_action_retry : Resource.String.home_action_start, Resource.Drawable.ic_play),
            HomeAction.OpenDsh => (Resource.String.home_action_open, Resource.Drawable.ic_open_in_new),
            _ => (Resource.String.home_action_stop, Resource.Drawable.ic_stop),
        };
        b.SetText(text);
        b.SetIconResource(icon);
    }

    private void Act(HomeAction action)
    {
        switch (action)
        {
            case HomeAction.ImportRuntime:
                StartActivityForResult(RuntimeImportFlow.PickIntent(), RuntimeImportFlow.RequestCode);
                break;
            case HomeAction.ImportDsh:
                StartActivityForResult(RuntimeImportFlow.PickDshIntent(), RuntimeImportFlow.DshRequestCode);
                break;
            case HomeAction.Start:
                _ = _bridge.RunStartAsync();
                break;
            case HomeAction.OpenDsh:
                StartActivity(new Intent(RequireContext(), typeof(DshWebActivity)));
                break;
            case HomeAction.Stop:
                _ = _bridge.RunStopAsync();
                break;
        }
    }

    public override void OnActivityResult(int requestCode, int resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (resultCode != (int)Android.App.Result.Ok || data?.Data is not { } uri)
        {
            return;
        }
        if (requestCode == RuntimeImportFlow.RequestCode)
        {
            _ = RuntimeImportFlow.ImportAsync(uri);
        }
        else if (requestCode == RuntimeImportFlow.DshRequestCode)
        {
            _ = RuntimeImportFlow.ImportDshAsync(uri);
        }
    }

    private TextView Shortcut(GridLayout grid, int icon, int title, int subtitle, Type activity)
    {
        var card = LayoutInflater.From(grid.Context)!.Inflate(Resource.Layout.item_shortcut, grid, false)!;
        card.FindViewById<ImageView>(Resource.Id.shortcut_icon)!.SetImageResource(icon);
        card.FindViewById<TextView>(Resource.Id.shortcut_title)!.SetText(title);
        var sub = card.FindViewById<TextView>(Resource.Id.shortcut_subtitle)!;
        sub.SetText(subtitle);
        card.Click += (_, _) => StartActivity(new Intent(RequireContext(), activity));
        var lp = new GridLayout.LayoutParams(GridLayout.InvokeSpec(GridLayout.Undefined), GridLayout.InvokeSpec(GridLayout.Undefined, 1f))
        {
            Width = 0,
        };
        var m = Rows.Dp(grid.Context!, 4);
        lp.SetMargins(m, m, m, m);
        grid.AddView(card, lp);
        return sub;
    }
}
