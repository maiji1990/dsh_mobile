using Android.Content;
using Android.Views;
using DshMobile.App.Files;
using DshMobile.App.Bridge;

namespace DshMobile.App;

[Activity(Label = "@string/app_name", MainLauncher = true, Exported = true)]
public class MainActivity : Activity
{
    private BridgeHost _bridge = null!;
    private TextView _status = null!;
    private TextView _runtimeState = null!;
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
        _runtimeState = FindViewById<TextView>(Resource.Id.runtime_state)!;
        _log = FindViewById<TextView>(Resource.Id.log)!;
        _logScroll = FindViewById<ScrollView>(Resource.Id.log_scroll)!;

        var info = FindViewById<Button>(Resource.Id.btn_info)!;
        var kotlin = FindViewById<Button>(Resource.Id.btn_kotlin)!;
        var stress = FindViewById<Button>(Resource.Id.btn_stress)!;
        var clear = FindViewById<Button>(Resource.Id.btn_clear)!;
        var prepare = FindViewById<Button>(Resource.Id.btn_prepare)!;
        var import = FindViewById<Button>(Resource.Id.btn_import)!;
        var start = FindViewById<Button>(Resource.Id.btn_start)!;
        var stop = FindViewById<Button>(Resource.Id.btn_stop)!;
        var diag = FindViewById<Button>(Resource.Id.btn_diag)!;
        var probe = FindViewById<Button>(Resource.Id.btn_probe)!;
        var openDsh = FindViewById<Button>(Resource.Id.btn_open_dsh)!;
        var files = FindViewById<Button>(Resource.Id.btn_files)!;
        var terminal = FindViewById<Button>(Resource.Id.btn_terminal)!;
        var plugins = FindViewById<Button>(Resource.Id.btn_plugins)!;
        _actions = [info, kotlin, stress, prepare, import, start, stop, diag, probe];

        // Fire-and-forget is intentional: BridgeHost owns the task and reports through its log,
        // so an Activity recreated mid-run (rotation) still sees the result.
        info.Click += (_, _) => _ = _bridge.RunInfoAsync();
        kotlin.Click += (_, _) => _ = _bridge.RunKotlinCompareAsync();
        stress.Click += (_, _) => _ = _bridge.RunStressAsync(1000);
        clear.Click += (_, _) => _bridge.ClearLog();
        prepare.Click += (_, _) => _ = _bridge.RunPrepareAsync();
        // Runtime bundle comes from the system picker (no storage permission, no adb needed).
        import.Click += (_, _) => StartActivityForResult(DocumentPicker.OpenIntent(["application/zip", ".zip"], multiple: false), RequestRuntimeBundle);
        start.Click += (_, _) => _ = _bridge.RunStartAsync();
        stop.Click += (_, _) => _ = _bridge.RunStopAsync();
        diag.Click += (_, _) => _ = _bridge.RunDiagnosticsAsync();
        probe.Click += (_, _) => _ = _bridge.RunProbesAsync();
        openDsh.Click += (_, _) => StartActivity(new Android.Content.Intent(this, typeof(DshWebActivity)));
        files.Click += (_, _) => StartActivity(new Android.Content.Intent(this, typeof(FilesActivity)));
        terminal.Click += (_, _) => StartActivity(new Android.Content.Intent(this, typeof(DshMobile.App.Terminal.TerminalActivity)));
        plugins.Click += (_, _) => StartActivity(new Android.Content.Intent(this, typeof(DshMobile.App.Plugins.PluginsActivity)));

        // The foreground-service notification is only visible with this permission (Android 13+).
        // The service itself runs either way, so a denial is not an error.
        if (OperatingSystem.IsAndroidVersionAtLeast(33)
            && CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Android.Content.PM.Permission.Granted)
        {
            RequestPermissions([Android.Manifest.Permission.PostNotifications], 1);
        }
    }

    private const int RequestRuntimeBundle = 61;

    protected override async void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != RequestRuntimeBundle || resultCode != Result.Ok || data?.Data is not { } uri)
        {
            return;
        }
        // Copy into app cache (an allowed import root), import, then delete the copy.
        var name = WorkspaceSafeName(DocumentPicker.DisplayName(ContentResolver!, uri));
        var dest = System.IO.Path.Combine(CacheDir!.AbsolutePath, "runtime-import", name);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
            var resolver = ContentResolver!;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _bridge.Note($"import: copying {name} from picker ...");
            await Task.Run(async () =>
            {
                await using var input = resolver.OpenInputStream(uri) ?? throw new IOException("cannot open picked file");
                await using var output = File.Create(dest);
                await input.CopyToAsync(output);
            });
            _bridge.Note($"import: copied {new FileInfo(dest).Length / 1024 / 1024} MiB in {sw.Elapsed.TotalSeconds:F1} s");
            await _bridge.RunImportAsync(dest, deleteAfter: true);
        }
        catch (Exception e) when (e is IOException or Java.Lang.Exception or UnauthorizedAccessException)
        {
            _bridge.Note($"import: copy failed {e.GetType().Name}: {e.Message}");
            File.Delete(dest);
        }
    }

    private static string WorkspaceSafeName(string name) =>
        DshMobile.Core.Files.WorkspaceFileSystem.SanitizeFileName(name);

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
        _runtimeState.Text = _bridge.RuntimeState;
        foreach (var b in _actions)
        {
            b.Enabled = !busy;
        }
        _log.Text = _bridge.LogText;
        _logScroll.Post(() => _logScroll.FullScroll(FocusSearchDirection.Down));
    }
}
