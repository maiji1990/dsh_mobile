using Android.Views;
using Android.Views.InputMethods;
using DshMobile.App.Bridge;
using DshMobile.App.Ui;
using DshMobile.Runtime.Guest;
using DshMobile.Runtime.Plugins;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.ProgressIndicator;
using Google.Android.Material.TextField;

namespace DshMobile.App.Plugins;

/// <summary>
/// Cordis plugin manager (TASK-009, ADR-0021) for the `web` profile: lists, adds and removes plugins through DSH's own
/// `dsh plugin` (pnpm) through the C# PRoot launcher. Installing a plugin runs third-party code inside the sandbox, so
/// every add/remove is confirmed by the user; an install that added a package DSH cannot load is rolled back
/// (MIGRATION row 29). Results show the real exit status and output.
/// </summary>
[Activity(Label = "@string/plugins_title", Exported = false, WindowSoftInputMode = SoftInput.AdjustResize)]
public sealed class PluginsActivity : DshActivity
{
    private const string Profile = "web";
    private static readonly TimeSpan ListDeadline = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ChangeDeadline = TimeSpan.FromMinutes(15);

    private BridgeHost _bridge = null!;
    private LinearLayout _list = null!;
    private TextInputEditText _spec = null!;
    private TextView _output = null!;
    private MaterialButton _add = null!;
    private LinearProgressIndicator _progress = null!;
    private List<(string Name, string Version)> _plugins = [];
    private bool _busy;
    private bool _listed = true;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetScreen(Resource.Layout.activity_plugins, Resource.String.plugins_title);
        _bridge = DshMobileApplication.Current.Bridge;
        _list = FindViewById<LinearLayout>(Resource.Id.plugins_list)!;
        _spec = FindViewById<TextInputEditText>(Resource.Id.plugins_spec)!;
        _output = FindViewById<TextView>(Resource.Id.plugins_output)!;
        _add = FindViewById<MaterialButton>(Resource.Id.plugins_add)!;
        _progress = FindViewById<LinearProgressIndicator>(Resource.Id.plugins_progress)!;
        FindViewById<TextView>(Resource.Id.plugins_profile)!.Text = Rows.Fmt(this, Resource.String.plugins_profile, Profile);
        _add.Click += (_, _) => ConfirmAdd();
        _spec.EditorAction += (_, e) =>
        {
            e.Handled = e.ActionId == ImeAction.Done;
            if (e.Handled)
            {
                ConfirmAdd();
            }
        };
        _ = RefreshAsync();
    }

    /// <summary>Re-reads the list; <paramref name="keepStatus"/> keeps the result of the change that was just made on screen.</summary>
    private async Task RefreshAsync(bool keepStatus = false)
    {
        SetBusy(true, keepStatus ? null : GetString(Resource.String.plugins_loading));
        var r = await ListAsync();
        _plugins = r.Ok ? GuestPlugins.ParseList(r.Output).ToList() : [];
        _listed = r.Ok;
        ShowList();
        SetBusy(false, keepStatus ? null : Describe("list", r));
    }

    private void ShowList()
    {
        _list.RemoveAllViews();
        if (_plugins.Count == 0)
        {
            Rows.Add(_list, Resource.Drawable.ic_extension, GetString(_listed ? Resource.String.plugins_none : Resource.String.plugins_list_failed));
            return;
        }
        foreach (var (name, version) in _plugins)
        {
            var row = Rows.Add(_list, Resource.Drawable.ic_extension, name, version);
            var remove = new MaterialButton(this, null, Resource.Attribute.materialIconButtonStyle);
            remove.SetIconResource(Resource.Drawable.ic_delete);
            remove.ContentDescription = Rows.Fmt(this, Resource.String.plugins_remove_confirm, name);
            remove.Click += (_, _) => ConfirmRemove(name);
            remove.Enabled = !_busy;
            row.SetTrailing(remove);
        }
    }

    private void ConfirmAdd()
    {
        var spec = _spec.Text?.Trim() ?? "";
        if (spec.Length == 0 || _busy)
        {
            return;
        }
        new MaterialAlertDialogBuilder(this)
            .SetIcon(Resource.Drawable.ic_extension)!
            .SetTitle(Rows.Fmt(this, Resource.String.plugins_add_confirm_title, spec))!
            .SetMessage(GetString(Resource.String.plugins_add_confirm_body) + "\n\n" + AddNote)!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(Resource.String.plugins_add, async (_, _) =>
            {
                SetBusy(true, Rows.Fmt(this, Resource.String.plugins_installing, spec));
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
        new MaterialAlertDialogBuilder(this)
            .SetTitle(Rows.Fmt(this, Resource.String.plugins_remove_confirm, name))!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(Resource.String.plugins_remove, async (_, _) =>
            {
                SetBusy(true, Rows.Fmt(this, Resource.String.plugins_removing, name));
                SetBusy(false, Describe("remove " + name, await ChangeAsync("remove", name)));
                await RefreshAsync(keepStatus: true);
            })!
            .Show();

    private const string AddNote = "安装后核对新增的包是否为可加载的 DSH 插件，不是则自动卸载（回滚）。";

    /// <summary><c>dsh plugin list --json --depth 0</c> through the C# PRoot launcher.</summary>
    private async Task<GuestPluginResult> ListAsync()
    {
        var runtime = _bridge.Runtime;
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
        var runtime = _bridge.Runtime;
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
        _busy = busy;
        _add.Enabled = !busy;
        _progress.Visibility = busy ? ViewStates.Visible : ViewStates.Gone;
        ShowList();
        if (status is not null)
        {
            _output.Text = status.TrimEnd();
        }
    }
}
