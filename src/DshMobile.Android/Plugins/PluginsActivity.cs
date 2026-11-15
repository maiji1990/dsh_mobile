using Android.Views;
using DshMobile.App.Bridge;
using DshMobile.Runtime.Guest;
using DshMobile.Runtime.Plugins;

namespace DshMobile.App.Plugins;

/// <summary>
/// Cordis plugin manager (TASK-009) for the `web` profile: lists, adds and removes plugins through DSH's own
/// `dsh plugin` (pnpm) through the C# PRoot launcher. Installing a plugin runs third-party code inside the sandbox, so
/// every add/remove is confirmed by the user; an install that added a package DSH cannot load is rolled back
/// (MIGRATION row 29). Results show the real exit status and output.
/// </summary>
[Activity(Label = "@string/plugins_title", Exported = false)]
public sealed class PluginsActivity : Activity
{
    private const string Profile = "web";
    private static readonly TimeSpan ListDeadline = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ChangeDeadline = TimeSpan.FromMinutes(15);

    private BridgeHost _bridge = null!;
    private ListView _list = null!;
    private EditText _spec = null!;
    private TextView _status = null!;
    private Button _add = null!;
    private List<(string Name, string Version)> _plugins = [];

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _bridge = DshMobileApplication.Current.Bridge;

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetFitsSystemWindows(true);
        root.SetPadding(24, 16, 24, 16);
        root.AddView(new TextView(this) { Text = Fmt(Resource.String.plugins_profile, Profile) });

        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        _spec = new EditText(this) { Hint = GetString(Resource.String.plugins_spec_hint) };
        _spec.SetSingleLine(true);
        _add = new Button(this) { Text = GetString(Resource.String.plugins_add) };
        _add.Click += (_, _) => ConfirmAdd();
        row.AddView(_spec, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        row.AddView(_add);
        root.AddView(row);

        _status = new TextView(this) { AccessibilityLiveRegion = AccessibilityLiveRegion.Polite };
        _status.SetTextIsSelectable(true);
        var statusScroll = new ScrollView(this);
        statusScroll.AddView(_status);
        root.AddView(statusScroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        _list = new ListView(this);
        _list.ItemClick += (_, e) => ConfirmRemove(_plugins[e.Position].Name);
        root.AddView(_list, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 2f));
        SetContentView(root);

        _ = RefreshAsync();
    }

    /// <summary>Re-reads the list; <paramref name="keepStatus"/> keeps the result of the change that was just made on screen.</summary>
    private async Task RefreshAsync(bool keepStatus = false)
    {
        SetBusy(true, keepStatus ? null : GetString(Resource.String.plugins_loading));
        var r = await ListAsync();
        _plugins = r.Ok ? GuestPlugins.ParseList(r.Output).ToList() : [];
        _list.Adapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleListItem1,
            _plugins.Count == 0 ? [GetString(r.Ok ? Resource.String.plugins_none : Resource.String.plugins_list_failed)] : _plugins.Select(p => $"{p.Name}  {p.Version}").ToList());
        _list.Enabled = _plugins.Count > 0;
        SetBusy(false, keepStatus ? null : Describe("list", r));
    }

    private void ConfirmAdd()
    {
        var spec = _spec.Text?.Trim() ?? "";
        if (spec.Length == 0)
        {
            return;
        }
        new AlertDialog.Builder(this)
            .SetTitle(Fmt(Resource.String.plugins_add_confirm_title, spec))!
            .SetMessage(GetString(Resource.String.plugins_add_confirm_body) + "\n\n" + AddNote)!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(Resource.String.plugins_add, async (_, _) =>
            {
                SetBusy(true, Fmt(Resource.String.plugins_installing, spec));
                var r = await ChangeAsync("add", spec);
                SetBusy(false, Describe("add " + spec, r));
                if (r.Ok)
                {
                    _spec.Text = "";
                }
                await RefreshAsync(keepStatus: true);
            })!
            .Show();
    }

    private void ConfirmRemove(string name) =>
        new AlertDialog.Builder(this)
            .SetTitle(Fmt(Resource.String.plugins_remove_confirm, name))!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(Resource.String.plugins_remove, async (_, _) =>
            {
                SetBusy(true, Fmt(Resource.String.plugins_removing, name));
                SetBusy(false, Describe("remove " + name, await ChangeAsync("remove", name)));
                await RefreshAsync(keepStatus: true);
            })!
            .Show();

    private const string AddNote = "安装后核对新增的包是否为可加载的 DSH 插件，不是则自动卸载（回滚）。";

    /// <summary><c>dsh plugin list --json --depth 0</c> through the C# PRoot launcher.</summary>
    private async Task<GuestPluginResult> ListAsync()
    {
        var runtime = DshMobileApplication.Current.Bridge.Runtime;
        try
        {
            using var cts = new CancellationTokenSource(ListDeadline);
            var r = await Task.Run(() => runtime.ListPluginsAsync(Profile, cts.Token));
            _bridge.Note($"plugin list exit={r.ExitCode}");
            return r;
        }
        catch (OperationCanceledException)
        {
            return new GuestPluginResult(false, null, "", $"TIMEOUT: no result within {ListDeadline.TotalMinutes:F0} min");
        }
    }

    private async Task<PluginChangeResult> ChangeAsync(string verb, string operand)
    {
        var runtime = DshMobileApplication.Current.Bridge.Runtime;
        try
        {
            using var cts = new CancellationTokenSource(ChangeDeadline);
            return await Task.Run(() => runtime.ChangePluginAsync(verb, Profile, operand, cts.Token));
        }
        catch (OperationCanceledException)
        {
            return new PluginChangeResult(false, "TIMEOUT", $"no result within {ChangeDeadline.TotalMinutes:F0} min", []);
        }
    }

    private string Describe(string what, PluginChangeResult r)
    {
        var tail = string.Join('\n', r.Lines.TakeLast(25));
        return r.Ok
            ? $"{what}: OK\n{GetString(Resource.String.plugins_restart_hint)}\n\n{tail}"
            : $"{what}: FAILED {r.Code}{(r.RolledBack ? " (rolled back)" : "")}\n{r.Message}\n\n{tail}";
    }

    private static string Describe(string what, GuestPluginResult r)
    {
        var tail = string.Join('\n', r.Output.Split('\n').TakeLast(25));
        return r.Ok
            ? $"{what}: OK\n\n{tail}"
            : $"{what}: FAILED exit={r.ExitCode?.ToString() ?? "?"} {r.Error}\n\n{tail}";
    }

    private void SetBusy(bool busy, string? status)
    {
        _add.Enabled = !busy;
        _list.Enabled = !busy && _plugins.Count > 0;
        if (status is not null)
        {
            _status.Text = status;
        }
    }

    /// <summary>Android %1$s formatting with a non-null Java string argument.</summary>
    private string Fmt(int resId, string arg) => GetString(resId, new Java.Lang.String(arg)) ?? arg;
}
