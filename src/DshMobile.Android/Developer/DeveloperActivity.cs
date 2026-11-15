using Android.Content;
using Android.Views;
using DshMobile.App.Bridge;
using DshMobile.App.Ui;
using Google.Android.Material.Card;
using Google.Android.Material.Dialog;
using Google.Android.Material.ProgressIndicator;

namespace DshMobile.App.Developer;

/// <summary>
/// Developer options (ADR-0021): the acceptance tools that used to sit on the home screen — device / runtime facts,
/// diagnostics, the DSH profile probe (MIGRATION row 24), the PTY self-test (PR-06) — and the operation log. Hidden until
/// enabled from About; they read and run only what the old buttons did.
/// </summary>
[Activity(Label = "@string/developer_title", Exported = false)]
public sealed class DeveloperActivity : DshActivity
{
    private const int MenuCopy = 1;
    private const int MenuClear = 2;

    private BridgeHost _bridge = null!;
    private LinearProgressIndicator _progress = null!;
    private TextView _log = null!;
    private readonly List<View> _tools = [];
    private int _renderPosted;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetScreen(Resource.Layout.fragment_list, Resource.String.developer_title);
        _bridge = DshMobileApplication.Current.Bridge;
        _progress = FindViewById<LinearProgressIndicator>(Resource.Id.list_progress)!;
        var content = FindViewById<LinearLayout>(Resource.Id.list_content)!;

        Toolbar.Menu!.Add(0, MenuCopy, 0, GetString(Resource.String.developer_log_copy))!.SetIcon(Resource.Drawable.ic_copy)!.SetShowAsAction(ShowAsAction.Always);
        Toolbar.Menu.Add(0, MenuClear, 1, GetString(Resource.String.developer_log_clear))!.SetIcon(Resource.Drawable.ic_delete)!.SetShowAsAction(ShowAsAction.Always);
        Toolbar.MenuItemClick += (_, e) =>
        {
            if (e.Item?.ItemId == MenuCopy)
            {
                var cm = (ClipboardManager)GetSystemService(ClipboardService)!;
                cm.PrimaryClip = ClipData.NewPlainText("dshmobile log", _bridge.LogText);
                Toast.MakeText(this, Resource.String.developer_log_copied, ToastLength.Short)!.Show();
            }
            else if (e.Item?.ItemId == MenuClear)
            {
                _bridge.ClearLog();
            }
        };

        Rows.Header(content, GetString(Resource.String.developer_section_tools));
        Tool(content, Resource.Drawable.ic_info, Resource.String.dev_info, Resource.String.dev_info_sub, () => _ = _bridge.RunInfoAsync());
        Tool(content, Resource.Drawable.ic_build, Resource.String.dev_diag, Resource.String.dev_diag_sub, () => _ = _bridge.RunDiagnosticsAsync());
        Tool(content, Resource.Drawable.ic_code, Resource.String.dev_probe, Resource.String.dev_probe_sub, () => _ = _bridge.RunProbesAsync());
        Tool(content, Resource.Drawable.ic_terminal, Resource.String.dev_pty, Resource.String.dev_pty_sub, ConfirmPtyProbe);

        Rows.Header(content, GetString(Resource.String.developer_section_log));
        var card = new MaterialCardView(this, null, Resource.Attribute.materialCardViewFilledStyle);
        var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        var m = Rows.Dp(this, 16);
        lp.SetMargins(m, 0, m, 0);
        _log = (TextView)LayoutInflater.Inflate(Resource.Layout.view_mono_text, card, false)!;
        _log.SetPadding(m, m, m, m);
        card.AddView(_log);
        content.AddView(card, lp);

        Rows.Header(content, GetString(Resource.String.more_section_developer));
        Rows.Add(content, Resource.Drawable.ic_close, GetString(Resource.String.developer_disable), null, () =>
        {
            UiPrefs.SetDeveloperEnabled(this, false);
            Finish();
        });
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
            t.Enabled = !busy;
            t.Alpha = busy ? 0.38f : 1f;
        }
        var log = _bridge.LogText;
        _log.Text = log.Length == 0 ? GetString(Resource.String.developer_log_empty) : log;
    }

    private void Tool(LinearLayout content, int icon, int title, int subtitle, Action run)
    {
        var row = Rows.Add(content, icon, GetString(title), GetString(subtitle), () =>
        {
            if (!_bridge.IsBusy)
            {
                run();
            }
        });
        _tools.Add(row.View);
    }

    /// <summary>PR-06 acceptance: the C#-owned PTY self-test (guest login shell), after a confirmation.</summary>
    private void ConfirmPtyProbe() =>
        new MaterialAlertDialogBuilder(this)
            .SetTitle(Resource.String.dev_pty_confirm_title)!
            .SetMessage(Resource.String.dev_pty_confirm)!
            .SetPositiveButton(Resource.String.dev_pty_start, (_, _) => _ = _bridge.RunPtyProbeAsync())!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .Show();
}
