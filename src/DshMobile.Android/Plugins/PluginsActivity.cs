using System.Text.Json;
using Android.Views;
using DshMobile.App.Bridge;
using DshMobile.Core.Bridge;
using DshMobile.Runtime.Guest;
using DshMobile.Runtime.Plugins;

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
        // MIGRATION row 29: the C# PRoot launcher + ported install guard (verify, roll back) by default; long-press is
        // the explicit legacy fallback (plugin.add through the bridge) for this one change.
        _add.Click += (_, _) => ConfirmAdd(csharp: CsChanges);
        _add.LongClick += (_, e) =>
        {
            e.Handled = true;
            ConfirmAdd(csharp: !CsChanges);
        };
        row.AddView(_spec, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        row.AddView(_add);
        root.AddView(row);

        _status = new TextView(this) { AccessibilityLiveRegion = AccessibilityLiveRegion.Polite };
        _status.SetTextIsSelectable(true);
        var statusScroll = new ScrollView(this);
        statusScroll.AddView(_status);
        root.AddView(statusScroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        _list = new ListView(this);
        _list.ItemClick += (_, e) => ConfirmRemove(_plugins[e.Position].Name, csharp: CsChanges);
        _list.ItemLongClick += (_, e) =>
        {
            e.Handled = true;
            ConfirmRemove(_plugins[e.Position].Name, csharp: !CsChanges);
        };
        root.AddView(_list, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 2f));
        SetContentView(root);

        _ = RefreshAsync();
    }

    /// <summary>Add / remove default (MIGRATION row 29): C# unless the slice is set back to legacy.</summary>
    private static bool CsChanges => DshMobileApplication.Current.Bridge.Runtime.Options.PluginChange == DshMobile.Application.SliceMode.CSharp;

    /// <summary>Re-reads the list; <paramref name="keepStatus"/> keeps the result of the change that was just made on screen.</summary>
    private async Task RefreshAsync(bool keepStatus = false)
    {
        SetBusy(true, keepStatus ? null : GetString(Resource.String.plugins_loading));
        var runtime = DshMobileApplication.Current.Bridge.Runtime;
        var csharpList = runtime.Options.PluginList == DshMobile.Application.SliceMode.CSharp;
        // ADR-0016: the list runs through the C# PRoot launcher by default; otherwise legacy plugin.list + C# shadow.
        var r = csharpList ? await ListWithCSharpAsync() : await RunAsync(BridgeMethods.PluginList, new PluginRequest(Profile), ListDeadline);
        _plugins = r is { Ok: true } ? GuestPlugins.ParseList(r.Output).ToList() : [];
        if (r is not null && !csharpList)
        {
            // PR-05 shadow (read-only): the same list through the C# PRoot launcher, compared in the background.
            _ = Task.Run(() => runtime.ShadowPluginListAsync(Profile, r.Ok, r.Output, CancellationToken.None));
        }
        _list.Adapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleListItem1,
            _plugins.Count == 0 ? [GetString(r is { Ok: true } ? Resource.String.plugins_none : Resource.String.plugins_list_failed)] : _plugins.Select(p => $"{p.Name}  {p.Version}").ToList());
        _list.Enabled = _plugins.Count > 0;
        SetBusy(false, r is null || keepStatus ? null : Describe("list", r));
    }

    private void ConfirmAdd(bool csharp)
    {
        var spec = _spec.Text?.Trim() ?? "";
        if (spec.Length == 0)
        {
            return;
        }
        new AlertDialog.Builder(this)
            .SetTitle(Fmt(Resource.String.plugins_add_confirm_title, spec) + Tag(csharp))!
            .SetMessage(GetString(Resource.String.plugins_add_confirm_body) + "\n\n" + (csharp ? CSharpAddNote : LegacyNote))!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(Resource.String.plugins_add, async (_, _) =>
            {
                SetBusy(true, Fmt(Resource.String.plugins_installing, spec));
                bool ok;
                if (csharp)
                {
                    var r = await ChangeWithCSharpAsync("add", spec);
                    ok = r.Ok;
                    SetBusy(false, Describe("add " + spec + Tag(true), r));
                }
                else
                {
                    var r = await RunAsync(BridgeMethods.PluginAdd, new PluginRequest(Profile, Spec: spec), ChangeDeadline);
                    ok = r is { Ok: true };
                    SetBusy(false, r is null ? null : Describe("add " + spec + Tag(false), r));
                }
                if (ok)
                {
                    _spec.Text = "";
                }
                await RefreshAsync(keepStatus: true);
            })!
            .Show();
    }

    private void ConfirmRemove(string name, bool csharp) =>
        new AlertDialog.Builder(this)
            .SetTitle(Fmt(Resource.String.plugins_remove_confirm, name) + Tag(csharp))!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(Resource.String.plugins_remove, async (_, _) =>
            {
                SetBusy(true, Fmt(Resource.String.plugins_removing, name));
                if (csharp)
                {
                    SetBusy(false, Describe("remove " + name + Tag(true), await ChangeWithCSharpAsync("remove", name)));
                }
                else
                {
                    var r = await RunAsync(BridgeMethods.PluginRemove, new PluginRequest(Profile, Name: name), ChangeDeadline);
                    SetBusy(false, r is null ? null : Describe("remove " + name + Tag(false), r));
                }
                await RefreshAsync(keepStatus: true);
            })!
            .Show();

    /// <summary>Marks the path that is not the default, so a fallback result is never mistaken for the default one.</summary>
    private static string Tag(bool csharp) => csharp == CsChanges ? "" : csharp ? "（C#）" : "（旧实现）";

    private const string CSharpAddNote = "安装后核对新增的包是否为可加载的 DSH 插件，不是则自动卸载（回滚）。长按「安装」= 旧实现（回退）。";
    private const string LegacyNote = "旧实现（回退）：只看 dsh plugin 的退出码，不核对新增的包。";

    /// <summary>C# <c>dsh plugin list</c>, in the legacy result shape so the page renders it the same way.</summary>
    private async Task<PluginCommandResult> ListWithCSharpAsync()
    {
        var runtime = DshMobileApplication.Current.Bridge.Runtime;
        try
        {
            using var cts = new CancellationTokenSource(ListDeadline);
            var r = await Task.Run(() => runtime.ListPluginsAsync(Profile, cts.Token));
            _bridge.Note($"plugin list (C#) exit={r.ExitCode}");
            return new PluginCommandResult(r.Ok, r.ExitCode, r.Output, r.Error);
        }
        catch (OperationCanceledException)
        {
            return new PluginCommandResult(false, null, "", $"TIMEOUT: no result within {ListDeadline.TotalMinutes:F0} min");
        }
    }

    private async Task<PluginChangeResult> ChangeWithCSharpAsync(string verb, string operand)
    {
        var runtime = DshMobileApplication.Current.Bridge.Runtime;
        try
        {
            using var cts = new CancellationTokenSource(ChangeDeadline);
            return await Task.Run(() => runtime.ChangePluginWithCSharpAsync(verb, Profile, operand, cts.Token));
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
