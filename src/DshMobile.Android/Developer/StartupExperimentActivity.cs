#if DSH_DIAGNOSTICS
using Android.Content;
using Android.Views;
using Android.Widget;
using DshMobile.App.Bridge;
using DshMobile.App.Ui;
using DshMobile.Core.Diagnostics;
using DshMobile.Runtime.Diagnostics;
using Google.Android.Material.ProgressIndicator;

namespace DshMobile.App.Developer;

/// <summary>
/// Diagnostic-build only (docs/STARTUP_EXPERIMENTS.md): runs the fixed E1-E4 suites from <see cref="StartupExperiments"/>.
/// The activity compiles only with <c>-p:DshDiagnostics=true</c>, so a normal Release build has no entry at all. Every run
/// refuses while DSH is running or port 3080 is busy, writes one JSON line per measurement to the app's external files
/// directory (<c>Android/data/&lt;pkg&gt;/files/startup-experiments</c>) and never touches user data for E3/E4.
/// </summary>
[Activity(Label = "@string/dev_experiments_title", Exported = false)]
public sealed class StartupExperimentActivity : DshActivity
{
    private const int Reps = 10;

    private BridgeHost _bridge = null!;
    private LinearProgressIndicator _progress = null!;
    private TextView _status = null!;
    private readonly List<Rows.Row> _rows = [];
    private CancellationTokenSource? _cts;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetScreen(Resource.Layout.fragment_list, Resource.String.dev_experiments_title);
        _bridge = DshMobileApplication.Current.Bridge;
        _progress = FindViewById<LinearProgressIndicator>(Resource.Id.list_progress)!;
        var content = FindViewById<LinearLayout>(Resource.Id.list_content)!;

        Rows.Header(content, GetString(Resource.String.dev_experiments_section));
        Row(content, Resource.String.dev_experiments_e1e2, Resource.String.dev_experiments_e1e2_sub, [StartupExperiments.SuiteE1E2]);
        Row(content, Resource.String.dev_experiments_e2, Resource.String.dev_experiments_e2_sub, [StartupExperiments.SuiteE2]);
        Row(content, Resource.String.dev_experiments_e23, Resource.String.dev_experiments_e23_sub, [StartupExperiments.SuiteE2Config]);
        Row(content, Resource.String.dev_experiments_e3, Resource.String.dev_experiments_e3_sub, [StartupExperiments.SuiteE3]);
        Row(content, Resource.String.dev_experiments_e4, Resource.String.dev_experiments_e4_sub, [StartupExperiments.SuiteE4]);
        Row(content, Resource.String.dev_experiments_all, Resource.String.dev_experiments_all_sub,
            [StartupExperiments.SuiteE1E2, StartupExperiments.SuiteE3, StartupExperiments.SuiteE4]);
        Row(content, Resource.String.dev_experiments_cancel, Resource.String.dev_experiments_cancel_sub, []);

        Rows.Header(content, GetString(Resource.String.dev_experiments_status));
        var text = (TextView)LayoutInflater.Inflate(Resource.Layout.view_mono_text, content, false)!;
        text.SetPadding(text.PaddingLeft, 16, text.PaddingRight, 16);
        _status = text;
        content.AddView(text, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
    }

    protected override void OnDestroy()
    {
        _cts?.Cancel();
        base.OnDestroy();
    }

    private void Row(LinearLayout content, int title, int subtitle, string[] suites)
    {
        var row = Rows.Add(content, Resource.Drawable.ic_code, GetString(title), GetString(subtitle), () =>
        {
            if (suites.Length == 0)
            {
                _cts?.Cancel();
                return;
            }
            Run(suites, Reps);
        });
        _rows.Add(row);
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
            row.View.Enabled = false;
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
                        row.View.Enabled = true;
                    }
                });
            }
        });
    }
}
#endif
