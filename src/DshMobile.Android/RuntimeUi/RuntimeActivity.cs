using Android.Content;
using Android.Content.Res;
using Android.Views;
using DshMobile.App.Bridge;
using DshMobile.App.Ui;
using DshMobile.Application.Ui;
using DshMobile.Runtime.Hosting;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.MaterialSwitch;
using Google.Android.Material.ProgressIndicator;

namespace DshMobile.App.RuntimeUi;

/// <summary>
/// 运行环境页（ADR-0021 / UI Slice 4）：
/// 全面迁入 <c>activity_runtime.xml</c> 4 张结构化功能卡，局部增量更新不再销毁重建整页；
/// 保留 DSH 运行前导入防呆引导、Ed25519/SHA-256 在线更新事务安装/取消与系统治理指标。
/// </summary>
[Activity(Label = "@string/more_runtime", Exported = false)]
public sealed class RuntimeActivity : DshActivity
{
    private BridgeHost _bridge = null!;
    private LinearProgressIndicator _progress = null!;
    private TextView _phaseBadge = null!;
    private TextView _dshVersion = null!;
    private TextView _phaseDetail = null!;
    private MaterialButton _startStopBtn = null!;
    private View _tileLayers = null!;
    private View _tileShim = null!;
    private View _tileFree = null!;
    private View _tileLastOp = null!;
    private TextView _opDetail = null!;

    private View _rowImportBundle = null!;
    private View _rowImportDsh = null!;
    private View _rowRollback = null!;

    private View _rowChannel = null!;
    private MaterialSwitch _autoCheckSwitch = null!;
    private LinearLayout _offersContainer = null!;
    private View _installBox = null!;
    private TextView _installSummary = null!;
    private MaterialButton _installBtn = null!;
    private View _cancelRow = null!;
    private MaterialButton _cancelBtn = null!;
    private TextView _updateStatusText = null!;
    private MaterialButton _copyUpdateBtn = null!;
    private MaterialButton _checkUpdateBtn = null!;

    private TextView _phantomRatio = null!;
    private LinearProgressIndicator _phantomBar = null!;
    private TextView _phantomDesc = null!;
    private MaterialButton _phantomDevBtn = null!;
    private TextView _heatValue = null!;
    private TextView _leasesValue = null!;
    private TextView _exitValue = null!;

    private RuntimeFacts? _facts;
    private DshMobile.Runtime.Supervision.PhantomProcessReport? _phantomBudget;
    private int _renderPosted;
    private bool _wasBusy;
    private bool _suppressAutoCheckEvent;
    private string? _lastRenderedOffersSignature;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetScreen(Resource.Layout.activity_runtime, Resource.String.more_runtime);
        _bridge = DshMobileApplication.Current.Bridge;

        _progress = FindViewById<LinearProgressIndicator>(Resource.Id.runtime_progress)!;
        _phaseBadge = FindViewById<TextView>(Resource.Id.runtime_phase_badge)!;
        _dshVersion = FindViewById<TextView>(Resource.Id.runtime_dsh_version)!;
        _phaseDetail = FindViewById<TextView>(Resource.Id.runtime_phase_detail)!;
        _startStopBtn = FindViewById<MaterialButton>(Resource.Id.runtime_start_stop_btn)!;
        _startStopBtn.Click += (_, _) =>
        {
            if (_bridge.IsBusy)
            {
                return;
            }
            var s = _bridge.Status;
            if (s.Primary == HomeAction.Start)
            {
                _ = _bridge.RunStartAsync();
            }
            else if (s.Secondary == HomeAction.Stop)
            {
                _ = _bridge.RunStopAsync();
            }
        };

        _tileLayers = FindViewById(Resource.Id.runtime_tile_layers)!;
        _tileShim = FindViewById(Resource.Id.runtime_tile_shim)!;
        _tileFree = FindViewById(Resource.Id.runtime_tile_free)!;
        _tileLastOp = FindViewById(Resource.Id.runtime_tile_last_op)!;
        _opDetail = FindViewById<TextView>(Resource.Id.runtime_op_detail)!;

        _rowImportBundle = FindViewById(Resource.Id.runtime_row_import_bundle)!;
        Rows.BindNavRow(_rowImportBundle, Resource.Drawable.ic_folder, GetString(Resource.String.runtime_import),
            GetString(Resource.String.runtime_import_sub),
            () => RunImportWithStopPrompt(RuntimeImportFlow.PickIntent(), RuntimeImportFlow.RequestCode));

        _rowImportDsh = FindViewById(Resource.Id.runtime_row_import_dsh)!;
        Rows.BindNavRow(_rowImportDsh, Resource.Drawable.ic_folder, GetString(Resource.String.runtime_import_dsh),
            GetString(Resource.String.runtime_import_dsh_sub),
            () => RunImportWithStopPrompt(RuntimeImportFlow.PickDshIntent(), RuntimeImportFlow.DshRequestCode));

        _rowRollback = FindViewById(Resource.Id.runtime_row_rollback)!;
        Rows.BindNavRow(_rowRollback, Resource.Drawable.ic_history, GetString(Resource.String.runtime_rollback),
            GetString(Resource.String.runtime_rollback_sub), ConfirmRollback);

        _rowChannel = FindViewById(Resource.Id.runtime_row_channel)!;
        Rows.BindNavRow(_rowChannel, Resource.Drawable.ic_cloud, GetString(Resource.String.update_channel),
            null, ChooseChannel);

        var autoCheckRow = FindViewById(Resource.Id.runtime_row_auto_check)!;
        _autoCheckSwitch = Rows.BindSwitchRow(autoCheckRow, Resource.Drawable.ic_refresh, GetString(Resource.String.update_auto_check),
            null, UiPrefs.AutoCheckUpdatesEnabled(this), enabled =>
            {
                if (_suppressAutoCheckEvent)
                {
                    return;
                }
                UiPrefs.SetAutoCheckUpdatesEnabled(this, enabled);
                Render();
            });

        _offersContainer = FindViewById<LinearLayout>(Resource.Id.runtime_update_offers_container)!;
        _installBox = FindViewById(Resource.Id.runtime_update_install_box)!;
        _installSummary = FindViewById<TextView>(Resource.Id.runtime_update_install_summary)!;
        _installBtn = FindViewById<MaterialButton>(Resource.Id.runtime_update_install_btn)!;
        _cancelRow = FindViewById(Resource.Id.runtime_update_cancel_row)!;
        _cancelBtn = FindViewById<MaterialButton>(Resource.Id.runtime_update_cancel_btn)!;
        _cancelBtn.Click += (_, _) => _bridge.CancelUpdate();
        _updateStatusText = FindViewById<TextView>(Resource.Id.runtime_update_status_text)!;

        _copyUpdateBtn = FindViewById<MaterialButton>(Resource.Id.runtime_update_copy_btn)!;
        _copyUpdateBtn.Click += (_, _) =>
        {
            var cm = (ClipboardManager)GetSystemService(ClipboardService)!;
            cm.PrimaryClip = ClipData.NewPlainText("dshmobile update", _bridge.UpdateDiagnostics());
            Toast.MakeText(this, Resource.String.developer_log_copied, ToastLength.Short)!.Show();
        };
        _checkUpdateBtn = FindViewById<MaterialButton>(Resource.Id.runtime_update_check_btn)!;
        _checkUpdateBtn.Click += (_, _) =>
        {
            if (!_bridge.IsBusy)
            {
                _ = _bridge.RunUpdateCheckAsync(UiPrefs.UpdateChannel(this));
            }
        };

        _phantomRatio = FindViewById<TextView>(Resource.Id.runtime_gov_phantom_ratio)!;
        _phantomBar = FindViewById<LinearProgressIndicator>(Resource.Id.runtime_gov_phantom_bar)!;
        _phantomDesc = FindViewById<TextView>(Resource.Id.runtime_gov_phantom_desc)!;
        _phantomDevBtn = FindViewById<MaterialButton>(Resource.Id.runtime_gov_dev_btn)!;
        _phantomDevBtn.Click += (_, _) => Governance.SystemGovernanceMonitor.OpenDeveloperOptionsForPhantomLimit(this);
        _heatValue = FindViewById<TextView>(Resource.Id.runtime_gov_heat_value)!;
        _leasesValue = FindViewById<TextView>(Resource.Id.runtime_gov_leases_value)!;
        _exitValue = FindViewById<TextView>(Resource.Id.runtime_gov_exit_value)!;
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
        _phantomBudget = await Task.Run(Governance.SystemGovernanceMonitor.ObservePhantomProcessBudget);
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

        var (phaseLabel, tone) = PhaseBadge(status);
        Rows.SetBadge(_phaseBadge, phaseLabel, tone);

        var version = _facts?.DshVersion ?? status.DshVersion;
        _dshVersion.Text = string.IsNullOrWhiteSpace(version) ? GetString(Resource.String.runtime_none) : $"DSH {version}";
        if (status.Phase == HomePhase.Failed && status.ErrorCode is not null)
        {
            _phaseDetail.Visibility = ViewStates.Visible;
            _phaseDetail.Text = OperationNames.Error(status.ErrorCode, status.ErrorMessage);
        }
        else
        {
            _phaseDetail.Visibility = ViewStates.Gone;
        }

        if (status.Primary == HomeAction.Start)
        {
            _startStopBtn.Visibility = ViewStates.Visible;
            _startStopBtn.SetText(Resource.String.home_action_stop);
            _startStopBtn.SetText(Resource.String.op_start);
            _startStopBtn.Enabled = !busy;
        }
        else if (status.Secondary == HomeAction.Stop)
        {
            _startStopBtn.Visibility = ViewStates.Visible;
            _startStopBtn.SetText(Resource.String.op_stop);
            _startStopBtn.Enabled = !busy;
        }
        else
        {
            _startStopBtn.Visibility = ViewStates.Gone;
        }

        var f = _facts;
        Rows.BindMetricTile(_tileLayers, Resource.Drawable.ic_storage, GetString(Resource.String.runtime_tile_layers_label),
            f is null ? "…" : f.RuntimeInstalled ? GetString(Resource.String.runtime_installed) : GetString(Resource.String.runtime_none));
        Rows.BindMetricTile(_tileShim, Resource.Drawable.ic_link, GetString(Resource.String.runtime_tile_shim_label),
            f is null ? "…" : GetString(f.LinkShim ? Resource.String.runtime_ok : Resource.String.runtime_missing));
        Rows.BindMetricTile(_tileFree, Resource.Drawable.ic_folder, GetString(Resource.String.runtime_tile_free_label),
            f is null ? "…" : f.FreeBytes < 0 ? "?" : Files.FilesFragment.FormatSize(f.FreeBytes));

        var lastOutcome = _bridge.LastOutcome is { } last
            && last.Operation is HomeStatus.ImportOperation or HomeStatus.ImportDshOperation or "runtime.rollback" or HomeStatus.UpdateCheckOperation or HomeStatus.UpdateInstallOperation
            ? last
            : null;
        Rows.BindMetricTile(_tileLastOp,
            lastOutcome is null ? Resource.Drawable.ic_info : lastOutcome.Ok ? Resource.Drawable.ic_check_circle : Resource.Drawable.ic_error,
            GetString(Resource.String.runtime_tile_last_op_label),
            lastOutcome is null
                ? GetString(Resource.String.runtime_tile_last_none)
                : OperationNames.Label(this, lastOutcome.Operation));

        if (busy && _bridge.CurrentOperation is { } op)
        {
            var progress = _bridge.Progress is { } p
                ? (p.Total > 0 ? Rows.Fmt(this, Resource.String.home_detail_progress, p.Step, (int)Math.Clamp(p.Done * 100 / p.Total, 0, 100)) : p.Step)
                : "";
            _opDetail.Visibility = ViewStates.Visible;
            _opDetail.Text = Rows.Fmt(this, Resource.String.home_title_busy, OperationNames.Label(this, op))
                + (string.IsNullOrEmpty(progress) ? "" : " · " + progress);
        }
        else if (lastOutcome is not null && (!lastOutcome.Ok || !string.IsNullOrWhiteSpace(lastOutcome.Message)))
        {
            _opDetail.Visibility = ViewStates.Visible;
            _opDetail.Text = lastOutcome.Ok
                ? Rows.Fmt(this, Resource.String.runtime_last_ok, OperationNames.Label(this, lastOutcome.Operation)) + " · " + lastOutcome.Message
                : Rows.Fmt(this, Resource.String.runtime_last_failed, OperationNames.Label(this, lastOutcome.Operation)) + " · " + OperationNames.Error(lastOutcome.Code, lastOutcome.Message);
        }
        else
        {
            _opDetail.Visibility = ViewStates.Gone;
        }

        Rows.SetRowEnabled(_rowImportBundle, !busy);
        Rows.SetRowEnabled(_rowImportDsh, !busy);
        Rows.SetRowEnabled(_rowRollback, !busy);

        RenderUpdates(busy);
        RenderGovernance();
    }

    private void RunImportWithStopPrompt(Intent pickerIntent, int requestCode)
    {
        if (_bridge.IsBusy)
        {
            return;
        }
        var phase = _bridge.Status.Phase;
        if (phase is HomePhase.Ready or HomePhase.Starting or HomePhase.WaitingForDsh)
        {
            new MaterialAlertDialogBuilder(this)
                .SetTitle(Resource.String.runtime_stop_before_import_title)!
                .SetMessage(Resource.String.runtime_stop_before_import_msg)!
                .SetPositiveButton(Resource.String.runtime_stop_before_import_action, (_, _) => _ = StopAndPickAsync(pickerIntent, requestCode))!
                .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
                .Show();
            return;
        }
#pragma warning disable CS0618
        StartActivityForResult(pickerIntent, requestCode);
#pragma warning restore CS0618
    }

    private async Task StopAndPickAsync(Intent pickerIntent, int requestCode)
    {
        await _bridge.RunStopAsync();
        if (!IsDestroyed && _bridge.Status.Phase is HomePhase.Stopped or HomePhase.NotInstalled)
        {
#pragma warning disable CS0618
            StartActivityForResult(pickerIntent, requestCode);
#pragma warning restore CS0618
        }
    }

    private void RenderUpdates(bool busy)
    {
        var channel = UiPrefs.UpdateChannel(this);
        Rows.SetBadge(
            _rowChannel.FindViewById<TextView>(Resource.Id.row_trailing_badge)!,
            GetString(channel == "preview" ? Resource.String.runtime_channel_preview_short : Resource.String.runtime_channel_stable_short),
            channel == "preview" ? Rows.BadgeTone.Warning : Rows.BadgeTone.Primary);
        Rows.SetRowSubtitle(_rowChannel, GetString(channel == "preview" ? Resource.String.update_channel_preview : Resource.String.update_channel_stable));
        Rows.SetRowEnabled(_rowChannel, !busy);

        var autoCheck = UiPrefs.AutoCheckUpdatesEnabled(this);
        _suppressAutoCheckEvent = true;
        _autoCheckSwitch.Checked = autoCheck;
        _autoCheckSwitch.Enabled = !busy;
        _suppressAutoCheckEvent = false;
        Rows.SetRowSubtitle(FindViewById(Resource.Id.runtime_row_auto_check)!,
            GetString(autoCheck ? Resource.String.update_auto_check_on : Resource.String.update_auto_check_off));

        _checkUpdateBtn.Enabled = !busy;
        _copyUpdateBtn.Enabled = true;

        var downloading = busy
            && _bridge.CurrentOperation == HomeStatus.UpdateInstallOperation
            && _bridge.Progress is { Step: DshMobile.Runtime.Updates.UpdatePhases.Downloading };
        _cancelRow.Visibility = downloading ? ViewStates.Visible : ViewStates.Gone;

        var sig = _bridge.LastCheck is { Ok: true } check && _bridge.LastCheckChannel == channel
            ? $"ok:{channel}:{string.Join(';', check.Choices.Select(c => $"{c.Kind}={c.Installed}:{string.Join(',', c.Offers.Take(3).Select(o => o.Artifact.Version + o.BlockedBy))}"))}:{_facts?.DshVersion}"
            : _bridge.LastCheck is { Ok: false } fail && _bridge.LastCheckChannel == channel
                ? $"err:{channel}:{fail.Code}:{fail.Message}"
                : "none";

        if (!string.Equals(sig, _lastRenderedOffersSignature, StringComparison.Ordinal))
        {
            _lastRenderedOffersSignature = sig;
            _offersContainer.RemoveAllViews();
            if (_bridge.LastCheck is { Ok: true } okCheck && _bridge.LastCheckChannel == channel)
            {
                _offersContainer.Visibility = ViewStates.Visible;
                var plan = DshMobile.Runtime.Updates.UpdatePlanner.Plan(okCheck.Choices);
                foreach (var c in okCheck.Choices)
                {
                    var offered = c.Offers.Count == 0
                        ? GetString(Resource.String.update_none_offered)
                        : string.Join(", ", c.Offers.Take(3).Select(o => o.Artifact.Version + (o.BlockedBy is null ? "" : " ✕")));
                    var detail = Rows.Fmt(this, Resource.String.update_row_detail, c.Installed ?? GetString(Resource.String.runtime_none), offered);
                    var note = plan.Notes.FirstOrDefault(n => n.StartsWith(c.Kind + " ", StringComparison.Ordinal));
                    var item = LayoutInflater.Inflate(Resource.Layout.item_runtime_update_offer, _offersContainer, false)!;
                    item.FindViewById<ImageView>(Resource.Id.offer_icon)!.SetImageResource(KindIcon(c.Kind));
                    item.FindViewById<TextView>(Resource.Id.offer_title)!.Text = KindTitle(c.Kind);
                    item.FindViewById<TextView>(Resource.Id.offer_detail)!.Text = note is null ? detail : detail + "\n" + note;
                    _offersContainer.AddView(item);
                }

                if (plan.HasWork)
                {
                    _installBox.Visibility = ViewStates.Visible;
                    var titleId = plan.Runtime is not null && plan.Dsh is not null
                        ? Resource.String.update_install_combined
                        : plan.Runtime is not null
                            ? Resource.String.update_install_runtime
                            : Resource.String.update_install_dsh;
                    _installBtn.SetText(titleId);
                    _installSummary.Text = Rows.Fmt(this, Resource.String.update_install_plan_sub, UpdateUi.DigestHint(plan), UpdateUi.TotalMiB(plan));
                    _installBtn.SetOnClickListener(new ActionClickListener(() =>
                    {
                        if (!_bridge.IsBusy)
                        {
                            UpdateUi.ShowConfirmInstallDialog(this, _bridge, plan);
                        }
                    }));
                }
                else
                {
                    _installBox.Visibility = ViewStates.Gone;
                }
            }
            else if (_bridge.LastCheck is { Ok: false } errCheck && _bridge.LastCheckChannel == channel)
            {
                _offersContainer.Visibility = ViewStates.Visible;
                _installBox.Visibility = ViewStates.Gone;
                var item = LayoutInflater.Inflate(Resource.Layout.item_runtime_update_offer, _offersContainer, false)!;
                item.FindViewById<ImageView>(Resource.Id.offer_icon)!.SetImageResource(Resource.Drawable.ic_error);
                item.FindViewById<TextView>(Resource.Id.offer_title)!.SetText(Resource.String.update_check_failed);
                item.FindViewById<TextView>(Resource.Id.offer_detail)!.Text = OperationNames.Error(errCheck.Code, errCheck.Message);
                _offersContainer.AddView(item);
            }
            else
            {
                _offersContainer.Visibility = ViewStates.Gone;
                _installBox.Visibility = ViewStates.Gone;
            }
        }
        _installBtn.Enabled = !busy;

        if (_bridge.UpdateInstaller.LastState is { } lastUpdate)
        {
            _updateStatusText.Visibility = ViewStates.Visible;
            var detail = lastUpdate.Code is null ? lastUpdate.Message : OperationNames.Error(lastUpdate.Code, lastUpdate.Message);
            _updateStatusText.Text = Rows.Fmt(this, Resource.String.update_last, lastUpdate.Kind, lastUpdate.Version, lastUpdate.Phase)
                + (string.IsNullOrWhiteSpace(detail) ? "" : "\n" + detail);
        }
        else
        {
            _updateStatusText.Visibility = ViewStates.Gone;
        }
    }

    private void RenderGovernance()
    {
        if (_phantomBudget is { Unsupported: false } budget)
        {
            _phantomRatio.Text = Rows.Fmt(this, Resource.String.runtime_phantom_ratio_fmt, budget.ChildProcessCount, budget.Limit);
            _phantomBar.Visibility = ViewStates.Visible;
            _phantomBar.Max = Math.Max(1, budget.Limit);
            _phantomBar.Progress = Math.Clamp(budget.ChildProcessCount, 0, Math.Max(1, budget.Limit));
            var indicatorColor = Rows.Color(_phantomBar, budget.NearLimit ? Android.Resource.Attribute.ColorError : Resource.Attribute.colorPrimary);
            _phantomBar.SetIndicatorColor(indicatorColor);
            _phantomDesc.Text = budget.NearLimit
                ? Rows.Fmt(this, Resource.String.governance_phantom_warn_fmt, budget.ChildProcessCount, budget.Limit)
                : Rows.Fmt(this, Resource.String.governance_phantom_fmt, budget.ChildProcessCount, budget.Limit);
            _phantomDesc.SetTextColor(new Android.Graphics.Color(Rows.Color(_phantomDesc, budget.NearLimit ? Android.Resource.Attribute.ColorError : Resource.Attribute.colorOnSurfaceVariant)));
        }
        else
        {
            _phantomRatio.Text = "--";
            _phantomBar.Visibility = ViewStates.Gone;
            _phantomDesc.SetText(Resource.String.governance_phantom_unsupported);
            _phantomDesc.SetTextColor(new Android.Graphics.Color(Rows.Color(_phantomDesc, Resource.Attribute.colorOnSurfaceVariant)));
        }

        _phantomDevBtn.Visibility = OperatingSystem.IsAndroidVersionAtLeast(34) ? ViewStates.Visible : ViewStates.Gone;

        var heat = _bridge.Governance.CurrentHeatGovernance();
        var tierLabel = GetString(heat.Tier switch
        {
            DshMobile.Application.Runtime.HeatTier.Moderate => Resource.String.governance_heat_moderate,
            DshMobile.Application.Runtime.HeatTier.Severe => Resource.String.governance_heat_severe,
            DshMobile.Application.Runtime.HeatTier.Critical => Resource.String.governance_heat_critical,
            _ => Resource.String.governance_heat_nominal,
        });
        var warmupLabel = GetString(heat.AllowBackgroundWarmup ? Resource.String.governance_heat_allow : Resource.String.governance_heat_suppress);
        _heatValue.Text = Rows.Fmt(this, Resource.String.governance_heat_fmt, tierLabel, warmupLabel, heat.MaxBackgroundConcurrency);

        var leases = _bridge.Governance.Leases.Snapshot();
        _leasesValue.Text = leases.Count == 0
            ? GetString(Resource.String.governance_leases_idle)
            : Rows.Fmt(this, Resource.String.governance_leases_active_fmt, leases.Count,
                string.Join(", ", leases.Select(l => l.Kind.ToString()).Distinct()));

        var lastExit = _bridge.Governance.LastExitAssessment;
        var isMemLimited = lastExit.Kind == DshMobile.Application.Runtime.HistoricalExitKind.MemoryLimited;
        _exitValue.SetText(isMemLimited ? Resource.String.governance_exit_memory_limited : Resource.String.governance_exit_none);
        _exitValue.SetTextColor(new Android.Graphics.Color(Rows.Color(_exitValue,
            isMemLimited ? Android.Resource.Attribute.ColorError : Resource.Attribute.colorOnSurface)));
    }

    private (string Label, Rows.BadgeTone Tone) PhaseBadge(HomeStatus s) => s.Phase switch
    {
        HomePhase.NotInstalled => (GetString(Resource.String.home_label_not_installed), Rows.BadgeTone.Neutral),
        HomePhase.Stopped => (GetString(Resource.String.home_label_stopped), Rows.BadgeTone.Neutral),
        HomePhase.Starting or HomePhase.WaitingForDsh => (GetString(Resource.String.home_label_starting), Rows.BadgeTone.Warning),
        HomePhase.Ready => (GetString(Resource.String.home_label_ready), Rows.BadgeTone.Success),
        HomePhase.Stopping => (GetString(Resource.String.home_label_stopping), Rows.BadgeTone.Warning),
        HomePhase.Busy => (GetString(Resource.String.home_label_busy), Rows.BadgeTone.Warning),
        _ => (GetString(Resource.String.home_label_failed), Rows.BadgeTone.Error),
    };

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
                _lastRenderedOffersSignature = null;
                Render();
            })!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .Show();
    }

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

    private sealed class ActionClickListener(Action action) : Java.Lang.Object, View.IOnClickListener
    {
        public void OnClick(View? v) => action();
    }
}
