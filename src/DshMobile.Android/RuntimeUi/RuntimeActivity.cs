using Android.Content;
using Android.Views;
using DshMobile.App.Bridge;
using DshMobile.App.Ui;
using DshMobile.Application.Ui;
using DshMobile.Runtime.Hosting;
using Google.Android.Material.Dialog;
using Google.Android.Material.ProgressIndicator;

namespace DshMobile.App.RuntimeUi;

/// <summary>
/// Runtime screen (ADR-0021): what is installed (from <see cref="RuntimeStatusReader"/>, read from disk) and the three
/// maintenance actions — import a runtime bundle, re-install the bundled DSH layer (MIGRATION row 17), roll back to the
/// previous runtime body (row 18, confirmed first). Every result is the coordinator's own code.
/// </summary>
[Activity(Label = "@string/more_runtime", Exported = false)]
public sealed class RuntimeActivity : DshActivity
{
    private BridgeHost _bridge = null!;
    private LinearLayout _content = null!;
    private LinearProgressIndicator _progress = null!;
    private RuntimeFacts? _facts;
    private int _renderPosted;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetScreen(Resource.Layout.fragment_list, Resource.String.more_runtime);
        _bridge = DshMobileApplication.Current.Bridge;
        _content = FindViewById<LinearLayout>(Resource.Id.list_content)!;
        _progress = FindViewById<LinearProgressIndicator>(Resource.Id.list_progress)!;
    }

    protected override void OnStart()
    {
        base.OnStart();
        _bridge.Changed += OnBridgeChanged;
        _ = ReadFactsAsync();
        Render();
    }

    protected override void OnStop()
    {
        _bridge.Changed -= OnBridgeChanged;
        base.OnStop();
    }

    private bool _wasBusy;

    private void OnBridgeChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _renderPosted, 1) == 0)
        {
            RunOnUiThread(() =>
            {
                Volatile.Write(ref _renderPosted, 0);
                if (IsDestroyed)
                {
                    return;
                }
                // An operation just ended: what is on disk may have changed.
                if (_wasBusy && !_bridge.IsBusy)
                {
                    _ = ReadFactsAsync();
                }
                Render();
            });
        }
    }

    private async Task ReadFactsAsync()
    {
        _facts = await _bridge.Runtime.ReadFactsAsync(CancellationToken.None);
        if (!IsDestroyed)
        {
            Render();
        }
    }

    private void Render()
    {
        var busy = _bridge.IsBusy;
        _wasBusy = busy;
        var status = _bridge.Status;
        _progress.Visibility = busy ? ViewStates.Visible : ViewStates.Gone;
        _content.RemoveAllViews();

        Rows.Header(_content, GetString(Resource.String.runtime_section_status));
        Rows.Add(_content, Resource.Drawable.ic_info, GetString(Resource.String.runtime_state), PhaseText(status));
        var f = _facts;
        Rows.Add(_content, Resource.Drawable.ic_terminal, GetString(Resource.String.runtime_dsh), f?.DshVersion ?? status.DshVersion ?? GetString(Resource.String.runtime_none));
        Rows.Add(_content, Resource.Drawable.ic_storage, GetString(Resource.String.runtime_layers), f is null ? "…"
            : f.RuntimeInstalled ? GetString(Resource.String.runtime_installed) : Rows.Fmt(this, Resource.String.runtime_not_installed, f.InstallReason));
        Rows.Add(_content, Resource.Drawable.ic_link, GetString(Resource.String.runtime_shim), f is null ? "…" : GetString(f.LinkShim ? Resource.String.runtime_ok : Resource.String.runtime_missing));
        Rows.Add(_content, Resource.Drawable.ic_folder, GetString(Resource.String.runtime_free), f is null ? "…"
            : f.FreeBytes < 0 ? "?" : Files.FilesFragment.FormatSize(f.FreeBytes));

        if (busy && _bridge.CurrentOperation is { } op)
        {
            var progress = _bridge.Progress is { } p ? (p.Total > 0 ? Rows.Fmt(this, Resource.String.home_detail_progress, p.Step, (int)Math.Clamp(p.Done * 100 / p.Total, 0, 100)) : p.Step) : null;
            Rows.Add(_content, Resource.Drawable.ic_refresh, Rows.Fmt(this, Resource.String.home_title_busy, OperationNames.Label(this, op)), progress);
        }
        else if (_bridge.LastOutcome is { } last && last.Operation is HomeStatus.ImportOperation or "runtime.prepare" or "runtime.rollback")
        {
            var row = Rows.Add(_content, last.Ok ? Resource.Drawable.ic_check_circle : Resource.Drawable.ic_error,
                Rows.Fmt(this, last.Ok ? Resource.String.runtime_last_ok : Resource.String.runtime_last_failed, OperationNames.Label(this, last.Operation)),
                last.Ok ? OkDetail(last) : OperationNames.Error(last.Code, last.Message));
            row.Subtitle.SetTextIsSelectable(true);
        }

        Rows.Header(_content, GetString(Resource.String.runtime_section_actions));
        Action(Resource.Drawable.ic_upload, Resource.String.runtime_import, Resource.String.runtime_import_sub, busy,
            () => StartActivityForResult(RuntimeImportFlow.PickIntent(), RuntimeImportFlow.RequestCode));
        Action(Resource.Drawable.ic_build, Resource.String.runtime_repair, Resource.String.runtime_repair_sub, busy,
            () => _ = _bridge.RunPrepareAsync());
        Action(Resource.Drawable.ic_history, Resource.String.runtime_rollback, Resource.String.runtime_rollback_sub, busy, ConfirmRollback);
        Rows.Add(_content, 0, "", GetString(Resource.String.runtime_bundle_note)).Title.Visibility = ViewStates.Gone;
    }

    /// <summary>What a successful "install bundled DSH" actually did (it may have kept the installed layer).</summary>
    private string? OkDetail(OperationOutcome last)
    {
        if (last.Operation != "runtime.prepare")
        {
            return null;
        }
        var o = PrepareOutcome.Parse(last.Message);
        return o.Kind switch
        {
            PrepareKind.Installed => Rows.Fmt(this, Resource.String.runtime_prepare_installed, o.Version ?? "?"),
            PrepareKind.Kept => Rows.Fmt(this, Resource.String.runtime_prepare_kept, o.Version ?? "?"),
            _ => last.Message,
        };
    }

    private void Action(int icon, int title, int subtitle, bool busy, Action onClick)
    {
        var row = Rows.Add(_content, icon, GetString(title), GetString(subtitle), () =>
        {
            if (!_bridge.IsBusy)
            {
                onClick();
            }
        });
        row.View.Enabled = !busy;
        row.View.Alpha = busy ? 0.38f : 1f;
    }

    private string PhaseText(HomeStatus s) => GetString(s.Phase switch
    {
        HomePhase.NotInstalled => Resource.String.home_label_not_installed,
        HomePhase.Stopped => Resource.String.home_label_stopped,
        HomePhase.Starting or HomePhase.WaitingForWebUi => Resource.String.home_label_starting,
        HomePhase.Ready => Resource.String.home_label_ready,
        HomePhase.Stopping => Resource.String.home_label_stopping,
        HomePhase.Busy => Resource.String.home_label_busy,
        _ => Resource.String.home_label_failed,
    }) + (s.Phase == HomePhase.Failed ? " · " + OperationNames.Error(s.ErrorCode, s.ErrorMessage) : "");

    /// <summary>MIGRATION row 18: put the previous runtime body back (C#), after an explicit confirmation.</summary>
    private void ConfirmRollback() =>
        new MaterialAlertDialogBuilder(this)
            .SetIcon(Resource.Drawable.ic_history)!
            .SetTitle(Resource.String.runtime_rollback_confirm_title)!
            .SetMessage(Resource.String.runtime_rollback_confirm)!
            .SetPositiveButton(Resource.String.runtime_rollback_action, (_, _) => _ = _bridge.RunRollbackAsync())!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .Show();

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == RuntimeImportFlow.RequestCode && resultCode == Result.Ok && data?.Data is { } uri)
        {
            _ = RuntimeImportFlow.ImportAsync(this, uri);
        }
    }
}
