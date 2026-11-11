using System.Text.Json;
using Android.Views;
using DshMobile.App.Bridge;
using DshMobile.Core.Bridge;
using DshMobile.Runtime.Guest;

namespace DshMobile.App.Plugins;

/// <summary>
/// Cordis plugin manager (TASK-009) for the `web` profile: lists, adds and removes plugins through DSH's own
/// `dsh plugin` (pnpm). Installing a plugin runs third-party code inside the sandbox, so every add/remove is
/// confirmed by the user; results show the real exit status and output.
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

    private async Task RefreshAsync()
    {
        SetBusy(true, GetString(Resource.String.plugins_loading));
        var r = await RunAsync(BridgeMethods.PluginList, new PluginRequest(Profile), ListDeadline);
        _plugins = r is { Ok: true } ? GuestPlugins.ParseList(r.Output).ToList() : [];
        if (r is not null)
        {
            // PR-05 shadow (read-only): the same list through the C# PRoot launcher, compared in the background.
            var runtime = DshMobileApplication.Current.Bridge.Runtime;
            _ = Task.Run(() => runtime.ShadowPluginListAsync(Profile, r.Ok, r.Output, CancellationToken.None));
        }
        _list.Adapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleListItem1,
            _plugins.Count == 0 ? [GetString(Resource.String.plugins_none)] : _plugins.Select(p => $"{p.Name}  {p.Version}").ToList());
        _list.Enabled = _plugins.Count > 0;
        SetBusy(false, r is null ? null : Describe("list", r));
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
            .SetMessage(Resource.String.plugins_add_confirm_body)!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(Resource.String.plugins_add, async (_, _) =>
            {
                SetBusy(true, Fmt(Resource.String.plugins_installing, spec));
                var r = await RunAsync(BridgeMethods.PluginAdd, new PluginRequest(Profile, Spec: spec), ChangeDeadline);
                SetBusy(false, r is null ? null : Describe("add " + spec, r));
                if (r is { Ok: true })
                {
                    _spec.Text = "";
                    await RefreshAsync();
                }
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
                var r = await RunAsync(BridgeMethods.PluginRemove, new PluginRequest(Profile, Name: name), ChangeDeadline);
                SetBusy(false, r is null ? null : Describe("remove " + name, r));
                await RefreshAsync();
            })!
            .Show();

    private async Task<PluginCommandResult?> RunAsync(string method, PluginRequest request, TimeSpan deadline)
    {
        var payload = BridgeCodec.ToPayload(request, BridgeJsonContext.Default.PluginRequest);
        var resp = await _bridge.Client.ExecuteAsync(method, payload, deadline);
        if (!resp.Ok)
        {
            _status.Text = $"{method} FAILED {resp.Code}: {resp.Message}";
            _bridge.Note(_status.Text);
            return null;
        }
        var result = BridgeCodec.ReadPayload(resp, BridgeJsonContext.Default.PluginCommandResult);
        _bridge.Note($"{method} exit={result?.ExitCode}");
        return result;
    }

    private string Describe(string what, PluginCommandResult r)
    {
        var tail = string.Join('\n', r.Output.Split('\n').TakeLast(25));
        // Only add/remove change what DSH loads; a list is read-only.
        var hint = what == "list" ? "" : GetString(Resource.String.plugins_restart_hint) + "\n";
        return r.Ok
            ? $"{what}: OK\n{hint}\n{tail}"
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
