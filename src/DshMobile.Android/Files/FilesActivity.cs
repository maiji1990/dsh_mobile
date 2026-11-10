using System.Text;
using Android.Content;
using Android.Views;
using DshMobile.Core.Files;
using Log = Android.Util.Log;

namespace DshMobile.App.Files;

/// <summary>
/// Files screen (TASK-007): browses the DSH workspace (guest /root/projects = host files/user-data),
/// previews text, imports from and exports to the system document picker. All path rules live in
/// <see cref="WorkspaceFileSystem"/> (no symlink traversal, .dsh protected, no overwrite).
/// </summary>
[Activity(Label = "@string/files_title", Exported = false)]
public sealed class FilesActivity : Activity
{
    private const int RequestImport = 71;
    private const int RequestExport = 72;
    private const long PreviewLimit = WorkspaceFileSystem.DefaultPreviewLimit;

    private WorkspaceFileSystem? _fs;
    private string _dir = "";
    private string? _pendingExport;
    private TextView _path = null!;
    private TextView _status = null!;
    private ListView _list = null!;
    private IReadOnlyList<WorkspaceEntry> _entries = [];

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _dir = savedInstanceState?.GetString("dir") ?? "";

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetFitsSystemWindows(true);
        root.SetPadding(24, 16, 24, 16);
        _path = new TextView(this) { TextSize = 16 };
        _status = new TextView(this) { AccessibilityLiveRegion = AccessibilityLiveRegion.Polite };
        var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        bar.AddView(MakeButton(Resource.String.files_up, GoUp), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        bar.AddView(MakeButton(Resource.String.files_import, StartImport), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        bar.AddView(MakeButton(Resource.String.files_refresh, Refresh), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        _list = new ListView(this);
        _list.ItemClick += (_, e) => OnEntryClicked(_entries[e.Position]);
        root.AddView(_path);
        root.AddView(bar);
        root.AddView(_status);
        root.AddView(_list, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
        SetContentView(root);

        var workspace = System.IO.Path.Combine(FilesDir!.AbsolutePath, "user-data");
        if (Directory.Exists(workspace))
        {
            _fs = new WorkspaceFileSystem(workspace);
            Refresh();
        }
        else
        {
            _status.SetText(Resource.String.files_no_workspace);
        }
    }

    protected override void OnSaveInstanceState(Bundle outState)
    {
        base.OnSaveInstanceState(outState);
        outState.PutString("dir", _dir);
    }

    private Button MakeButton(int text, Action onClick)
    {
        var b = new Button(this);
        b.SetText(text);
        b.SetMinHeight(48 * (int)(Resources!.DisplayMetrics!.Density));
        b.Click += (_, _) => onClick();
        return b;
    }

    private void Refresh()
    {
        if (_fs is null)
        {
            return;
        }
        try
        {
            _entries = _fs.List(_dir);
            _path.Text = "/root/projects/" + _dir;
            _list.Adapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleListItem1, _entries.Select(Describe).ToList());
        }
        catch (WorkspaceException e)
        {
            ShowError(e);
        }
    }

    private static string Describe(WorkspaceEntry e) => e.Kind switch
    {
        WorkspaceEntryKind.Directory => $"📁 {e.Name}{(e.Protected ? "  🔒" : "")}",
        WorkspaceEntryKind.Symlink => $"↪ {e.Name} → {e.LinkTarget}",
        WorkspaceEntryKind.File => $"📄 {e.Name}  {FormatSize(e.Size)}{(e.Protected ? "  🔒" : "")}",
        _ => $"• {e.Name}",
    };

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / 1024.0 / 1024.0:F1} MB",
    };

    private void GoUp()
    {
        if (_dir.Length == 0)
        {
            return;
        }
        var i = _dir.LastIndexOf('/');
        _dir = i < 0 ? "" : _dir[..i];
        Refresh();
    }

    private void OnEntryClicked(WorkspaceEntry e)
    {
        switch (e.Kind)
        {
            case WorkspaceEntryKind.Directory:
                _dir = e.RelativePath;
                Refresh();
                break;
            case WorkspaceEntryKind.Symlink:
                _status.Text = GetString(Resource.String.files_symlink_blocked) + " → " + (e.LinkTarget ?? "?");
                break;
            case WorkspaceEntryKind.File when e.Protected:
                _status.SetText(Resource.String.files_protected);
                break;
            case WorkspaceEntryKind.File:
                new AlertDialog.Builder(this)
                    .SetTitle(e.Name)!
                    .SetItems([GetString(Resource.String.files_preview), GetString(Resource.String.files_export)], (_, a) =>
                    {
                        if (a.Which == 0)
                        {
                            Preview(e);
                        }
                        else
                        {
                            _pendingExport = e.RelativePath;
                            StartActivityForResult(DocumentPicker.CreateIntent(e.Name), RequestExport);
                        }
                    })!
                    .Show();
                break;
        }
    }

    private void Preview(WorkspaceEntry e)
    {
        try
        {
            using var s = _fs!.OpenRead(e.RelativePath, PreviewLimit);
            var bytes = new byte[s.Length];
            s.ReadExactly(bytes);
            var text = bytes.Contains((byte)0) ? GetString(Resource.String.files_binary) : Encoding.UTF8.GetString(bytes);
            var view = new TextView(this) { Text = text, TextSize = 12 };
            view.SetTextIsSelectable(true);
            view.SetPadding(32, 16, 32, 16);
            var scroll = new ScrollView(this);
            scroll.AddView(view);
            new AlertDialog.Builder(this).SetTitle(e.Name)!.SetView(scroll)!.SetPositiveButton(Android.Resource.String.Ok, (_, _) => { })!.Show();
        }
        catch (WorkspaceException ex)
        {
            ShowError(ex);
        }
    }

    private void StartImport()
    {
        if (_fs is null)
        {
            return;
        }
        StartActivityForResult(DocumentPicker.OpenIntent(null, multiple: true), RequestImport);
    }

    protected override async void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (resultCode != Result.Ok || _fs is null)
        {
            return;
        }
        try
        {
            if (requestCode == RequestImport)
            {
                var done = new List<string>();
                foreach (var uri in DocumentPicker.ResultUris(data))
                {
                    var name = DocumentPicker.DisplayName(ContentResolver!, uri);
                    await using var input = ContentResolver!.OpenInputStream(uri) ?? throw new IOException("cannot open " + name);
                    done.Add(await _fs.ImportAsync(_dir, name, input));
                }
                _status.Text = $"{GetString(Resource.String.files_imported)} {done.Count}: {string.Join(", ", done)}";
                Refresh();
            }
            else if (requestCode == RequestExport && _pendingExport is { } rel && data?.Data is { } target)
            {
                _pendingExport = null;
                await using var input = _fs.OpenRead(rel);
                await using var output = ContentResolver!.OpenOutputStream(target, "wt") ?? throw new IOException("cannot open export target");
                await input.CopyToAsync(output);
                _status.Text = $"{GetString(Resource.String.files_exported)} {rel}";
            }
        }
        catch (WorkspaceException e)
        {
            ShowError(e);
        }
        catch (Exception e) when (e is IOException or Java.Lang.Exception)
        {
            Log.Warn("DshMobile", $"files: {e.GetType().Name}: {e.Message}");
            _status.Text = $"{e.GetType().Name}: {e.Message}";
        }
    }

    private void ShowError(WorkspaceException e)
    {
        _status.Text = $"{e.Code}: {e.Message}";
        Log.Info("DshMobile", $"files {e.Code}: {e.Message}");
    }
}
