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
        var automation = FindViewById<Button>(Resource.Id.btn_automation)!;
        _actions = [info, kotlin, stress, prepare, import, start, stop, diag, probe];

        // Fire-and-forget is intentional: BridgeHost owns the task and reports through its log,
        // so an Activity recreated mid-run (rotation) still sees the result.
        info.Click += (_, _) => _ = _bridge.RunInfoAsync();
        kotlin.Click += (_, _) => _ = _bridge.RunKotlinCompareAsync();
        stress.Click += (_, _) => _ = _bridge.RunStressAsync(1000);
        clear.Click += (_, _) => _bridge.ClearLog();
        // Default: the legacy runtime.prepare installs the bundled DSH layer, C# shadows it (MIGRATION row 17).
        prepare.Click += (_, _) => _ = _bridge.RunPrepareAsync();
        // Developer opt-in: the C# RuntimePreparer decides for this one call (no DSHBox code).
        prepare.LongClick += (_, e) =>
        {
            e.Handled = true;
            new AlertDialog.Builder(this)
                .SetTitle("准备实现")!
                .SetMessage("用 C# RuntimePreparer 做这一次准备（安装 APK 内置的 DSH 层，按版本仲裁，实验）？默认仍由旧实现准备并做影子比对。运行中只允许“保留现有版本”，需要替换 DSH 层时会拒绝。")!
                .SetPositiveButton("C# 准备", (_, _) => _ = _bridge.RunPrepareAsync(DshMobile.Application.SliceMode.CSharp))!
                .SetNegativeButton("取消", (_, _) => { })!
                .Show();
        };
        // Runtime bundle comes from the system picker (no storage permission, no adb needed).
        import.Click += (_, _) => PickRuntimeBundle(csharpImporter: false);
        // Developer opt-in for device acceptance of the C# LayerInstaller (ADR-0007): one import only, explicit choice.
        import.LongClick += (_, e) =>
        {
            e.Handled = true;
            new AlertDialog.Builder(this)
                .SetTitle("导入实现")!
                .SetMessage("用 C# LayerInstaller 做这一次导入（实验，用于真机验收）？默认导入仍使用旧实现并做影子比对。")!
                .SetPositiveButton("C# 导入器", (_, _) => PickRuntimeBundle(csharpImporter: true))!
                .SetNegativeButton("取消", (_, _) => { })!
                .Show();
        };
        // ADR-0015: the C# DshHost starts and owns sandbox + DSH by default (no DSHBox code on that path).
        start.Click += (_, _) => _ = _bridge.RunStartAsync();
        // Explicit fallback: the legacy DSHBox sandbox-manager (browser automation still follows it).
        start.LongClick += (_, e) =>
        {
            e.Handled = true;
            new AlertDialog.Builder(this)
                .SetTitle("启动实现")!
                .SetMessage("默认由 C# 宿主启动沙箱和 DSH。改用旧实现（DSHBox sandbox-manager）做这一次启动？浏览器自动化（browser_*）只在旧实现启动时可用；也用于问题对照。")!
                .SetPositiveButton("旧实现（回退）", (_, _) => _ = _bridge.RunStartAsync(DshMobile.Application.SliceMode.Legacy))!
                .SetNegativeButton("取消", (_, _) => { })!
                .Show();
        };
        stop.Click += (_, _) => _ = _bridge.RunStopAsync();
        diag.Click += (_, _) => _ = _bridge.RunDiagnosticsAsync();
        // Developer opt-in (ADR-0007 PR-06): self-test of the C#-owned PTY (guest login shell, no legacy terminal code).
        diag.LongClick += (_, e) =>
        {
            e.Handled = true;
            new AlertDialog.Builder(this)
                .SetTitle("终端 PTY 自检")!
                .SetMessage("用 C# + libdshmobile_native 打开一个 guest 登录 shell，自动执行 tty / stty / 调整窗口大小 / Ctrl-C / exit 并检查无残留进程（实验，用于真机验收）。终端界面仍使用旧实现。")!
                .SetPositiveButton("开始自检", (_, _) => _ = _bridge.RunCSharpPtyProbeAsync())!
                .SetNegativeButton("取消", (_, _) => { })!
                .Show();
        };
        probe.Click += (_, _) => _ = _bridge.RunProbesAsync();
        openDsh.Click += (_, _) => StartActivity(new Android.Content.Intent(this, typeof(DshWebActivity)));
        files.Click += (_, _) => StartActivity(new Android.Content.Intent(this, typeof(FilesActivity)));
        terminal.Click += (_, _) => StartActivity(new Android.Content.Intent(this, typeof(DshMobile.App.Terminal.TerminalActivity)));
        // Developer opt-in (ADR-0007 PR-06): C#-owned PTY sessions + xterm.js screen; the legacy TerminalView stays the default.
        terminal.LongClick += (_, e) =>
        {
            e.Handled = true;
            new AlertDialog.Builder(this)
                .SetTitle("终端实现")!
                .SetMessage("打开 C# 终端（实验，用于真机验收）？会话由 C# 经 libdshmobile_native 持有、界面为本地 xterm.js；与旧终端的会话互相独立。默认终端仍使用旧实现。")!
                .SetPositiveButton("C# 终端", (_, _) => StartActivity(new Android.Content.Intent(this, typeof(DshMobile.App.Terminal.CSharpTerminalActivity))))!
                .SetNegativeButton("取消", (_, _) => { })!
                .Show();
        };
        plugins.Click += (_, _) => StartActivity(new Android.Content.Intent(this, typeof(DshMobile.App.Plugins.PluginsActivity)));
        automation.Click += (_, _) => StartActivity(new Android.Content.Intent(this, typeof(DshMobile.App.Automation.AutomationActivity)));
        FindViewById<Button>(Resource.Id.btn_browser)!.Click +=
            (_, _) => StartActivity(new Android.Content.Intent(this, typeof(DshMobile.App.Browser.BrowserToolsActivity)));
        FindViewById<Button>(Resource.Id.btn_remote)!.Click +=
            (_, _) => StartActivity(new Android.Content.Intent(this, typeof(DshMobile.App.Remote.RemoteActivity)));

        // The foreground-service notification is only visible with this permission (Android 13+).
        // The service itself runs either way, so a denial is not an error.
        if (OperatingSystem.IsAndroidVersionAtLeast(33)
            && CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Android.Content.PM.Permission.Granted)
        {
            RequestPermissions([Android.Manifest.Permission.PostNotifications], 1);
        }
    }

    private const int RequestRuntimeBundle = 61;

    /// <summary>Importer chosen for the pending picker result (reset after each use).</summary>
    private bool _csharpImporter;

    private void PickRuntimeBundle(bool csharpImporter)
    {
        _csharpImporter = csharpImporter;
        StartActivityForResult(DocumentPicker.OpenIntent(["application/zip", ".zip"], multiple: false), RequestRuntimeBundle);
    }

    protected override async void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        var csharpImporter = _csharpImporter;
        _csharpImporter = false;
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
            await _bridge.RunImportAsync(dest, deleteAfter: true, csharpImporter);
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
