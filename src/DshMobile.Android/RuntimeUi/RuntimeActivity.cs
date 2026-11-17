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
/// maintenance actions — import a runtime bundle, import the DSH layer (ADR-0027: it is no longer bundled with the APK),
/// roll back to the previous runtime body (row 18, confirmed first). Every result is the coordinator's own code.
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
        else if (_bridge.LastOutcome is { } last && last.Operation is HomeStatus.ImportOperation or HomeStatus.ImportDshOperation or "runtime.rollback" or "update.check" or "update.install")
        {
            var row = Rows.Add(_content, last.Ok ? Resource.Drawable.ic_check_circle : Resource.Drawable.ic_error,
                Rows.Fmt(this, last.Ok ? Resource.String.runtime_last_ok : Resource.String.runtime_last_failed, OperationNames.Label(this, last.Operation)),
                last.Ok ? (last.Operation == HomeStatus.ImportDshOperation ? last.Message : null) : OperationNames.Error(last.Code, last.Message));
            row.Subtitle.SetTextIsSelectable(true);
        }

        Rows.Header(_content, GetString(Resource.String.runtime_section_actions));
        Action(Resource.Drawable.ic_upload, Resource.String.runtime_import, Resource.String.runtime_import_sub, busy,
            () => StartActivityForResult(RuntimeImportFlow.PickIntent(), RuntimeImportFlow.RequestCode));
        Action(Resource.Drawable.ic_download, Resource.String.runtime_import_dsh, Resource.String.runtime_import_dsh_sub, busy,
            () => StartActivityForResult(RuntimeImportFlow.PickDshIntent(), RuntimeImportFlow.DshRequestCode));
        Action(Resource.Drawable.ic_history, Resource.String.runtime_rollback, Resource.String.runtime_rollback_sub, busy, ConfirmRollback);
        Rows.Add(_content, 0, "", GetString(Resource.String.runtime_bundle_note)).Title.Visibility = ViewStates.Gone;

        RenderUpdates(busy);
    }

    /// <summary>
    /// vNext PR-06: online updates. Channel, check, the four rows (Runtime / Node / DSH / App: installed, offered, why not),
    /// download + install of the DSH layer after a confirmation, cancel while
    /// downloading, copyable diagnostics. Every outcome shows its own code; nothing installs in the background.
    /// </summary>
    private void RenderUpdates(bool busy)
    {
        Rows.Header(_content, GetString(Resource.String.update_section));
        var channel = UiPrefs.UpdateChannel(this);
        Action(Resource.Drawable.ic_cloud, Resource.String.update_channel, channel == "preview" ? Resource.String.update_channel_preview : Resource.String.update_channel_stable,
            busy, ChooseChannel);
        Action(Resource.Drawable.ic_refresh, Resource.String.update_check, Resource.String.update_check_sub, busy, () => _ = _bridge.RunUpdateCheckAsync(channel));

        if (busy && _bridge.CurrentOperation == "update.install" && _bridge.Progress is { Step: DshMobile.Runtime.Updates.UpdatePhases.Downloading })
        {
            Rows.Add(_content, Resource.Drawable.ic_close, GetString(Resource.String.update_cancel), GetString(Resource.String.update_cancel_sub), _bridge.CancelUpdate);
        }

        if (_bridge.LastCheck is { Ok: true } check && _bridge.LastCheckChannel == channel)
        {
            var plan = DshMobile.Runtime.Updates.UpdatePlanner.Plan(check.Choices);
            foreach (var c in check.Choices)
            {
                var offered = c.Offers.Count == 0 ? GetString(Resource.String.update_none_offered)
                    : string.Join(", ", c.Offers.Take(3).Select(o => o.Artifact.Version + (o.BlockedBy is null ? "" : " ✕")));
                var detail = Rows.Fmt(this, Resource.String.update_row_detail, c.Installed ?? GetString(Resource.String.runtime_none), offered);
                var note = plan.Notes.FirstOrDefault(n => n.StartsWith(c.Kind + " ", StringComparison.Ordinal));
                var row = Rows.Add(_content, KindIcon(c.Kind), KindTitle(c.Kind), note is null ? detail : detail + "\n" + note);
                row.Subtitle.SetTextIsSelectable(true);
            }
            if (plan.Dsh is { } target)
            {
                Action(Resource.Drawable.ic_download, Resource.String.update_install_dsh, 0, busy, () => ConfirmDshUpdate(target))
                    .SetSubtitle(Rows.Fmt(this, Resource.String.update_install_dsh_sub, target.Version, Math.Max(1, target.Size / (1024 * 1024)), target.Sha256[..12]));
            }
        }
        else if (_bridge.LastCheck is { Ok: false } failed && _bridge.LastCheckChannel == channel)
        {
            Rows.Add(_content, Resource.Drawable.ic_error, GetString(Resource.String.update_check_failed), OperationNames.Error(failed.Code, failed.Message))
                .Subtitle.SetTextIsSelectable(true);
        }

        if (_bridge.UpdateInstaller.LastState is { } last)
        {
            var ok = last.Phase == DshMobile.Runtime.Updates.UpdatePhases.Committed;
            Rows.Add(_content, ok ? Resource.Drawable.ic_check_circle : Resource.Drawable.ic_history,
                Rows.Fmt(this, Resource.String.update_last, last.Kind, last.Version, last.Phase),
                last.Code is null ? last.Message : OperationNames.Error(last.Code, last.Message)).Subtitle.SetTextIsSelectable(true);
        }
        Rows.Add(_content, Resource.Drawable.ic_copy, GetString(Resource.String.update_copy), null, () =>
        {
            var cm = (ClipboardManager)GetSystemService(ClipboardService)!;
            cm.PrimaryClip = ClipData.NewPlainText("dshmobile update", _bridge.UpdateDiagnostics());
            Toast.MakeText(this, Resource.String.developer_log_copied, ToastLength.Short)!.Show();
        });
    }

    private static int KindIcon(string kind) => kind switch
    {
        "runtime" => Resource.Drawable.ic_storage,
        "node" => Resource.Drawable.ic_code,
        "dsh" => Resource.Drawable.ic_terminal,
        _ => Resource.Drawable.ic_phone,
    };

    private string KindTitle(string kind) => GetString(kind switch
    {
        "runtime" => Resource.String.update_kind_runtime,
        "node" => Resource.String.update_kind_node,
        "dsh" => Resource.String.update_kind_dsh,
        _ => Resource.String.update_kind_app,
    });

    private void ChooseChannel()
    {
        string[] values = ["stable", "preview"];
        string[] labels = [GetString(Resource.String.update_channel_stable), GetString(Resource.String.update_channel_preview)];
        new MaterialAlertDialogBuilder(this)
            .SetTitle(Resource.String.update_channel)!
            .SetSingleChoiceItems(labels, Array.IndexOf(values, UiPrefs.UpdateChannel(this)), (d, e) =>
            {
                UiPrefs.SetUpdateChannel(this, values[e.Which]);
                (d as IDialogInterface)?.Dismiss();
                Render();
            })!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .Show();
    }

    /// <summary>Asks for confirmation before installation stops a running DSH.</summary>
    private void ConfirmDshUpdate(DshMobile.Runtime.Updates.ReleaseArtifact target)
    {
        var running = _bridge.Runtime.HostOwnsProcesses;
        new MaterialAlertDialogBuilder(this)
            .SetIcon(Resource.Drawable.ic_download)!
            .SetTitle(Rows.Fmt(this, Resource.String.update_confirm_title, target.Version))!
            .SetMessage(GetString(running ? Resource.String.update_confirm_running : Resource.String.update_confirm_stopped))!
            .SetPositiveButton(Resource.String.update_confirm_action, (_, _) => _ = _bridge.RunDshUpdateAsync(stopConfirmed: true))!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .Show();
    }

    private Rows.Row Action(int icon, int title, int subtitle, bool busy, Action onClick)
    {
        var row = Rows.Add(_content, icon, GetString(title), subtitle == 0 ? null : GetString(subtitle), () =>
        {
            if (!_bridge.IsBusy)
            {
                onClick();
            }
        });
        row.View.Enabled = !busy;
        row.View.Alpha = busy ? 0.38f : 1f;
        return row;
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
        if (resultCode != Result.Ok || data?.Data is not { } uri)
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
}
