#if DSH_DIAGNOSTICS
using Android.Views;
using DshMobile.App.Bridge;
using DshMobile.App.Ui;
using DshMobile.Core.Diagnostics;
using DshMobile.Runtime.Diagnostics;
using Google.Android.Material.ProgressIndicator;

namespace DshMobile.App.Developer;

/// <summary>
/// 诊断构建专用启动实验页（<c>-p:DshDiagnostics=true</c> 编译启用，普通 Release 构建不包含）。
/// UI Slice 4 全 XML 化：绑定 <c>activity_startup_experiment.xml</c>。
/// </summary>
[Activity(Label = "@string/dev_experiments_title", Exported = false)]
public sealed class StartupExperimentActivity : DshActivity
{
    private const int Reps = 10;

    private BridgeHost _bridge = null!;
    private LinearProgressIndicator _progress = null!;
    private TextView _status = null!;
    private readonly List<View> _rows = [];
    private View? _cancelRow;
    private CancellationTokenSource? _cts;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetScreen(Resource.Layout.activity_startup_experiment, Resource.String.dev_experiments_title);
        _bridge = DshMobileApplication.Current.Bridge;
        _progress = FindViewById<LinearProgressIndicator>(Resource.Id.experiments_progress)!;
        _status = FindViewById<TextView>(Resource.Id.exp_status_text)!;

        BindSuite(Resource.Id.exp_row_e1e2, Resource.String.dev_experiments_e1e2, Resource.String.dev_experiments_e1e2_sub, [StartupExperiments.SuiteE1E2]);
        BindSuite(Resource.Id.exp_row_e2, Resource.String.dev_experiments_e2, Resource.String.dev_experiments_e2_sub, [StartupExperiments.SuiteE2]);
        BindSuite(Resource.Id.exp_row_e23, Resource.String.dev_experiments_e23, Resource.String.dev_experiments_e23_sub, [StartupExperiments.SuiteE2Config]);
        BindSuite(Resource.Id.exp_row_e3, Resource.String.dev_experiments_e3, Resource.String.dev_experiments_e3_sub, [StartupExperiments.SuiteE3]);
        BindSuite(Resource.Id.exp_row_e4, Resource.String.dev_experiments_e4, Resource.String.dev_experiments_e4_sub, [StartupExperiments.SuiteE4]);

        var webRow = FindViewById(Resource.Id.exp_row_e5)!;
        Rows.BindActionRow(webRow, Resource.Drawable.ic_code, GetString(Resource.String.dev_experiments_e5),
            GetString(Resource.String.dev_experiments_e5_sub), RunWebProfile);
        _rows.Add(webRow);

        var cacheRow = FindViewById(Resource.Id.exp_row_e5_cache)!;
        Rows.BindActionRow(cacheRow, Resource.Drawable.ic_code, GetString(Resource.String.dev_experiments_e5_cache),
            GetString(Resource.String.dev_experiments_e5_cache_sub), RunCompileCacheInventory);
        _rows.Add(cacheRow);

        BindSuite(Resource.Id.exp_row_e6, Resource.String.dev_experiments_e6, Resource.String.dev_experiments_e6_sub, [StartupExperiments.SuiteE6]);
        BindSuite(Resource.Id.exp_row_e7, Resource.String.dev_experiments_e7, Resource.String.dev_experiments_e7_sub, [StartupExperiments.SuiteE7]);
        BindSuite(Resource.Id.exp_row_all, Resource.String.dev_experiments_all, Resource.String.dev_experiments_all_sub,
            [StartupExperiments.SuiteE1E2, StartupExperiments.SuiteE3, StartupExperiments.SuiteE4]);

        _cancelRow = FindViewById(Resource.Id.exp_row_cancel)!;
        Rows.BindActionRow(_cancelRow, Resource.Drawable.ic_close, GetString(Resource.String.dev_experiments_cancel),
            GetString(Resource.String.dev_experiments_cancel_sub), () => _cts?.Cancel());
        _rows.Add(_cancelRow);
    }

    protected override void OnDestroy()
    {
        _cts?.Cancel();
        base.OnDestroy();
    }

    private void BindSuite(int rowId, int title, int subtitle, string[] suites)
    {
        var row = FindViewById(rowId)!;
        Rows.BindActionRow(row, Resource.Drawable.ic_code, GetString(title), GetString(subtitle), () => Run(suites, Reps));
        _rows.Add(row);
    }

    /// <summary>
    /// 能力 A：诊断构建内只读盘点生产 node-compile-cache，只把计数、版本/架构前缀和固定错误码写入 App 私有 JSONL 与
    /// DshMobile 日志；不读取任何用户文件、不输出路径、不要求 root。
    /// </summary>
    private void RunCompileCacheInventory()
    {
        if (_cts is not null)
        {
            return;
        }
        var experiments = GetExternalFilesDir("startup-experiments")?.AbsolutePath ?? FilesDir!.AbsolutePath;
        Directory.CreateDirectory(experiments);
        var path = Path.Combine(experiments, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-E5-CACHE.jsonl");
        try
        {
            var record = WebProfileRunner.InspectProductionCompileCache(_bridge.Layout);
            File.WriteAllText(path, record.ToJsonLine() + Environment.NewLine);
            _bridge.Note("E5 cache inventory: " + record.Summary);
            _status.Text = "E5 cache inventory: " + record.Summary + "\njsonl: " + path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _status.Text = "E5 cache inventory failed: " + e.GetType().Name;
        }
    }

    /// <summary>P0: alternating E5-OFF / E5-PROFILE Web runs with the in-process V8 CPU profile preload.</summary>
    private void RunWebProfile()
    {
        if (_cts is not null)
        {
            return;
        }
        byte[]? preload;
        try
        {
            using var asset = typeof(StartupExperimentActivity).Assembly
                .GetManifestResourceStream("DshMobile.Diagnostics.web-profile-preload.mjs");
            using var buffer = new MemoryStream();
            asset?.CopyTo(buffer);
            preload = asset is null ? null : buffer.ToArray();
        }
        catch (IOException)
        {
            preload = null;
        }
        if (preload is null || preload.Length == 0)
        {
            _status.Text = "diagnostic preload asset missing (build with -p:DshDiagnostics=true)";
            return;
        }
        var experiments = GetExternalFilesDir("startup-experiments")?.AbsolutePath ?? FilesDir!.AbsolutePath;
        var profilesRoot = GetExternalFilesDir("startup-profiles")?.AbsolutePath ?? FilesDir!.AbsolutePath;
        var profileDir = Path.Combine(profilesRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(experiments);
        Directory.CreateDirectory(profileDir);
        var path = Path.Combine(experiments, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-E5.jsonl");
        var cachePath = Path.Combine(experiments, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-E5-CACHE.jsonl");
        var writer = new StreamWriter(path, append: false) { AutoFlush = true };
        var cacheWriter = new StreamWriter(cachePath, append: false) { AutoFlush = true };
        var gate = new Lock();
        var runner = new WebProfileRunner(_bridge.Layout, _bridge.Note, () => _bridge.Runtime.HostOwnsProcesses);
        var cts = new CancellationTokenSource();
        _cts = cts;
        foreach (var row in _rows)
        {
            row.Enabled = row == _cancelRow;
        }
        _progress.Visibility = ViewStates.Visible;
        _status.Text = GetString(Resource.String.dev_experiments_running) + "\n" + path;

        _ = Task.Run(async () =>
        {
            var cacheRuns = 0;
            var message = "E5: not run";
            try
            {
                // 3 轮独立冷启动：每轮前清空 node-compile-cache，最后一轮 flush 后供 probe 与 10 对 warm 复用。
                var result = await runner.RunAsync(Reps, preload, profileDir, record =>
                {
                    lock (gate)
                    {
                        writer.WriteLine(record.ToJsonLine());
                    }
                }, cts.Token, record =>
                {
                    lock (gate)
                    {
                        cacheWriter.WriteLine(record.ToJsonLine());
                        cacheRuns++;
                    }
                }, coldRounds: 3).ConfigureAwait(false);
                message = $"E5: {(result.Ran ? "ran" : result.Code)} warm-records={result.Runs} cache-records={cacheRuns} not-ok={result.Failures}";
            }
            catch (OperationCanceledException)
            {
                message = "E5: cancelled";
            }
            catch (Exception e)
            {
                message = "E5: error " + e.GetType().Name;
            }
            finally
            {
                lock (gate)
                {
                    writer.Dispose();
                    cacheWriter.Dispose();
                }
                cts.Dispose();
                _cts = null;
                var snapshot = $"{message}\nprofiles: {profileDir}\njsonl: {path}\ncache: {cachePath}";
                RunOnUiThread(() =>
                {
                    _progress.Visibility = ViewStates.Gone;
                    _status.Text = snapshot;
                    foreach (var row in _rows)
                    {
                        row.Enabled = true;
                    }
                });
            }
        });
    }

    private void Run(string[] suites, int reps)
    {
        if (_cts is not null)
        {
            return;
        }
        var dir = GetExternalFilesDir("startup-experiments")?.AbsolutePath ?? FilesDir!.AbsolutePath;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{string.Join('-', suites)}.jsonl");
        var writer = new StreamWriter(path, append: false) { AutoFlush = true };
        var gate = new Lock();
        var runner = new StartupExperimentRunner(_bridge.Layout, _bridge.Note, () => _bridge.Runtime.HostOwnsProcesses);
        var cts = new CancellationTokenSource();
        _cts = cts;
        foreach (var row in _rows)
        {
            row.Enabled = row == _cancelRow;
        }
        _progress.Visibility = ViewStates.Visible;
        _status.Text = GetString(Resource.String.dev_experiments_running) + "\n" + path;

        _ = Task.Run(async () =>
        {
            var lines = new List<string>();
            var runs = 0;
            var failures = 0;
            try
            {
                foreach (var suite in suites)
                {
                    var result = await runner.RunAsync(suite, reps, record =>
                    {
                        lock (gate)
                        {
                            writer.WriteLine(record.ToJsonLine());
                            runs++;
                            if (!string.Equals(record.Result, ExperimentRecord.Ok, StringComparison.Ordinal))
                            {
                                failures++;
                            }
                        }
                    }, cts.Token).ConfigureAwait(false);
                    lines.Add($"{suite}: {(result.Ran ? "ran" : result.Code)} runs={result.Runs} not-ok={result.Failures}");
                    var snapshot = string.Join("\n", lines);
                    RunOnUiThread(() => _status.Text = snapshot + "\n" + path);
                }
                lines.Add($"done runs={runs} not-ok={failures}");
            }
            catch (OperationCanceledException)
            {
                lines.Add("cancelled");
            }
            catch (Exception e)
            {
                lines.Add("error " + e.GetType().Name);
            }
            finally
            {
                lock (gate)
                {
                    writer.Dispose();
                }
                cts.Dispose();
                _cts = null;
                var snapshot = string.Join("\n", lines) + "\n" + path;
                RunOnUiThread(() =>
                {
                    _progress.Visibility = ViewStates.Gone;
                    _status.Text = snapshot;
                    foreach (var row in _rows)
                    {
                        row.Enabled = true;
                    }
                });
            }
        });
    }
}
#endif
