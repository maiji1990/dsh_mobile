using Android.Content;
using Android.Views;
using DshMobile.App.Bridge;
using DshMobile.App.Ui;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.ProgressIndicator;

namespace DshMobile.App.Developer;

/// <summary>
/// 开发者选项页（ADR-0021 / UI Slice 4）：
/// 全 XML 化布局（<c>activity_developer.xml</c>），诊断工具分组、日志卡片头部就近集成复制/清空/全屏查看、
/// 底部固定危险色「关闭开发者选项」动作卡，不再使用代码拼装卡片或被超长日志挤出视野。
/// </summary>
[Activity(Label = "@string/developer_title", Exported = false)]
public sealed class DeveloperActivity : DshActivity
{
    private BridgeHost _bridge = null!;
    private LinearProgressIndicator _progress = null!;
    private TextView _log = null!;
    private readonly List<View> _tools = [];
    private int _renderPosted;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetScreen(Resource.Layout.activity_developer, Resource.String.developer_title);
        _bridge = DshMobileApplication.Current.Bridge;
        _progress = FindViewById<LinearProgressIndicator>(Resource.Id.developer_progress)!;
        _log = FindViewById<TextView>(Resource.Id.dev_log_text)!;

        BindTool(Resource.Id.dev_row_info, Resource.Drawable.ic_info, Resource.String.dev_info, Resource.String.dev_info_sub, () => _ = _bridge.RunInfoAsync());
        BindTool(Resource.Id.dev_row_diag, Resource.Drawable.ic_build, Resource.String.dev_diag, Resource.String.dev_diag_sub, () => _ = _bridge.RunDiagnosticsAsync());
        BindTool(Resource.Id.dev_row_probe, Resource.Drawable.ic_code, Resource.String.dev_probe, Resource.String.dev_probe_sub, () => _ = _bridge.RunProbesAsync());
        BindTool(Resource.Id.dev_row_pty, Resource.Drawable.ic_terminal, Resource.String.dev_pty, Resource.String.dev_pty_sub, ConfirmPtyProbe);
#if DSH_DIAGNOSTICS
        var expRow = FindViewById(Resource.Id.dev_row_experiments)!;
        FindViewById(Resource.Id.dev_experiments_divider)!.Visibility = ViewStates.Visible;
        expRow.Visibility = ViewStates.Visible;
        Rows.BindNavRow(expRow, Resource.Drawable.ic_code, GetString(Resource.String.dev_experiments),
            GetString(Resource.String.dev_experiments_sub), () =>
            {
                if (!_bridge.IsBusy)
                {
                    StartActivity(new Intent(this, typeof(StartupExperimentActivity)));
                }
            });
        _tools.Add(expRow);
#endif
        BindTool(Resource.Id.dev_row_update, Resource.Drawable.ic_info, Resource.String.dev_update, Resource.String.dev_update_sub, ChooseUpdateChannel);

        FindViewById<MaterialButton>(Resource.Id.dev_log_fullscreen_btn)!.Click += (_, _) =>
        {
            var text = _bridge.LogText;
            Rows.ShowScrollableTextDialog(this, Resource.String.developer_section_log,
                text.Length == 0 ? GetString(Resource.String.developer_log_empty) : text);
        };
        FindViewById<MaterialButton>(Resource.Id.dev_log_copy_btn)!.Click += (_, _) =>
        {
            var cm = (ClipboardManager)GetSystemService(ClipboardService)!;
            cm.PrimaryClip = ClipData.NewPlainText("dshmobile log", _bridge.LogText);
            Toast.MakeText(this, Resource.String.developer_log_copied, ToastLength.Short)!.Show();
        };
        FindViewById<MaterialButton>(Resource.Id.dev_log_clear_btn)!.Click += (_, _) => _bridge.ClearLog();

        Rows.BindActionRow(
            FindViewById(Resource.Id.dev_row_disable)!,
            Resource.Drawable.ic_close,
            GetString(Resource.String.developer_disable),
            GetString(Resource.String.developer_disable_sub),
            () =>
            {
                UiPrefs.SetDeveloperEnabled(this, false);
                Finish();
            },
            destructive: true);
    }

    protected override void OnStart()
    {
        base.OnStart();
        _bridge.Changed += OnBridgeChanged;
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
                if (!IsDestroyed)
                {
                    Render();
                }
            });
        }
    }

    private void Render()
    {
        var busy = _bridge.IsBusy;
        _progress.Visibility = busy ? ViewStates.Visible : ViewStates.Gone;
        foreach (var t in _tools)
        {
            Rows.SetRowEnabled(t, !busy);
        }
        var log = _bridge.LogText;
        _log.Text = log.Length == 0 ? GetString(Resource.String.developer_log_empty) : log;
    }

    private void BindTool(int rowId, int icon, int title, int subtitle, Action run)
    {
        var row = FindViewById(rowId)!;
        Rows.BindActionRow(row, icon, GetString(title), GetString(subtitle), () =>
        {
            if (!_bridge.IsBusy)
            {
                run();
            }
        });
        _tools.Add(row);
    }

    private void ChooseUpdateChannel()
    {
        var channels = DshMobile.Runtime.Updates.ReleaseEndpoints.Channels.ToArray();
        new MaterialAlertDialogBuilder(this)
            .SetTitle(Resource.String.dev_update_choose)!
            .SetItems(channels, (_, e) => _ = _bridge.RunUpdateCheckAsync(channels[e.Which]))!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .Show();
    }

    private void ConfirmPtyProbe() =>
        new MaterialAlertDialogBuilder(this)
            .SetTitle(Resource.String.dev_pty_confirm_title)!
            .SetMessage(Resource.String.dev_pty_confirm)!
            .SetPositiveButton(Resource.String.dev_pty_start, (_, _) => _ = _bridge.RunPtyProbeAsync())!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .Show();
}
