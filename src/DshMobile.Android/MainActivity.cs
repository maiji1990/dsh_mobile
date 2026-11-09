using Android.Views;
using DshMobile.App.Bridge;

namespace DshMobile.App;

[Activity(Label = "@string/app_name", MainLauncher = true, Exported = true)]
public class MainActivity : Activity
{
    private BridgeHost _bridge = null!;
    private TextView _status = null!;
    private TextView _log = null!;
    private ScrollView _logScroll = null!;
    private Button[] _actions = [];
    private int _refreshPosted;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_main);

        _bridge = DshMobileApplication.Current.Bridge;
        _status = FindViewById<TextView>(Resource.Id.status)!;
        _log = FindViewById<TextView>(Resource.Id.log)!;
        _logScroll = FindViewById<ScrollView>(Resource.Id.log_scroll)!;

        var info = FindViewById<Button>(Resource.Id.btn_info)!;
        var kotlin = FindViewById<Button>(Resource.Id.btn_kotlin)!;
        var stress = FindViewById<Button>(Resource.Id.btn_stress)!;
        var clear = FindViewById<Button>(Resource.Id.btn_clear)!;
        _actions = [info, kotlin, stress];

        // Fire-and-forget is intentional: BridgeHost owns the task and reports through its log,
        // so an Activity recreated mid-run (rotation) still sees the result.
        info.Click += (_, _) => _ = _bridge.RunInfoAsync();
        kotlin.Click += (_, _) => _ = _bridge.RunKotlinCompareAsync();
        stress.Click += (_, _) => _ = _bridge.RunStressAsync(1000);
        clear.Click += (_, _) => _bridge.ClearLog();
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
        // Coalesce bursts (the stress test logs from many threads) into one UI-thread render.
        if (Interlocked.Exchange(ref _refreshPosted, 1) == 0)
        {
            RunOnUiThread(() =>
            {
                Volatile.Write(ref _refreshPosted, 0);
                Render();
            });
        }
    }

    private void Render()
    {
        var busy = _bridge.IsBusy;
        _status.SetText(busy ? Resource.String.status_running : Resource.String.status_idle);
        foreach (var b in _actions)
        {
            b.Enabled = !busy;
        }
        _log.Text = _bridge.LogText;
        _logScroll.Post(() => _logScroll.FullScroll(FocusSearchDirection.Down));
    }
}
