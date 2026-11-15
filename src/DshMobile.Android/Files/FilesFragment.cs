using System.Text;
using Android.Content;
using Android.Views;
using DshMobile.App.Ui;
using DshMobile.Core.Files;
using Google.Android.Material.AppBar;
using Google.Android.Material.Dialog;
using Fragment = AndroidX.Fragment.App.Fragment;
using Log = Android.Util.Log;

namespace DshMobile.App.Files;

/// <summary>
/// Files tab (TASK-007, ADR-0021): browses the DSH workspace (guest /root/projects = host files/user-data), previews
/// text, imports from and exports to the system document picker. All path rules live in <see cref="WorkspaceFileSystem"/>
/// (no symlink traversal, .dsh protected, no overwrite). Back goes up a folder before leaving the tab.
/// </summary>
public sealed class FilesFragment : Fragment, ITabRoot
{
    private const int RequestImport = 71;
    private const int RequestExport = 72;
    private const int MenuImport = 1;
    private const int MenuRefresh = 2;
    private const long PreviewLimit = WorkspaceFileSystem.DefaultPreviewLimit;
    private const string GuestRoot = "/root/projects";

    private WorkspaceFileSystem? _fs;
    private string _dir = "";
    private string? _pendingExport;
    private MaterialToolbar _toolbar = null!;
    private TextView _status = null!;
    private ListView _list = null!;
    private View _empty = null!;
    private IReadOnlyList<WorkspaceEntry> _entries = [];

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        var view = inflater.Inflate(Resource.Layout.fragment_files, container, false)!;
        _dir = savedInstanceState?.GetString("dir") ?? _dir;
        _toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.toolbar)!;
        _toolbar.SetTitle(Resource.String.files_title);
        _toolbar.Menu!.Add(0, MenuImport, 0, GetString(Resource.String.files_import))!.SetIcon(Resource.Drawable.ic_upload)!.SetShowAsAction(ShowAsAction.Always);
        _toolbar.Menu.Add(0, MenuRefresh, 1, GetString(Resource.String.files_refresh))!.SetIcon(Resource.Drawable.ic_refresh)!.SetShowAsAction(ShowAsAction.Always);
        _toolbar.MenuItemClick += (_, e) =>
        {
            if (e.Item?.ItemId == MenuImport)
            {
                StartImport();
            }
            else if (e.Item?.ItemId == MenuRefresh)
            {
                ShowStatus(null);
                Refresh();
            }
        };
        _toolbar.NavigationClick += (_, _) => GoUp();
        _status = view.FindViewById<TextView>(Resource.Id.files_status)!;
        _list = view.FindViewById<ListView>(Resource.Id.files_list)!;
        _list.ItemClick += (_, e) => OnEntryClicked(_entries[e.Position]);
        _empty = view.FindViewById(Resource.Id.files_empty)!;
        _empty.FindViewById<ImageView>(Resource.Id.empty_icon)!.SetImageResource(Resource.Drawable.ic_folder);

        var workspace = Path.Combine(RequireContext().FilesDir!.AbsolutePath, "user-data");
        if (Directory.Exists(workspace))
        {
            _fs = new WorkspaceFileSystem(workspace);
        }
        Refresh();
        return view;
    }

    public override void OnSaveInstanceState(Bundle outState)
    {
        base.OnSaveInstanceState(outState);
        outState.PutString("dir", _dir);
    }

    public override void OnHiddenChanged(bool hidden)
    {
        base.OnHiddenChanged(hidden);
        if (!hidden)
        {
            Refresh(); // DSH or the terminal may have changed the workspace meanwhile
        }
    }

    public bool OnBack()
    {
        if (_dir.Length == 0)
        {
            return false;
        }
        GoUp();
        return true;
    }

    public void OnReselected()
    {
        _dir = "";
        Refresh();
    }

    private void Refresh()
    {
        if (_fs is null)
        {
            // The workspace appears with the first runtime import.
            var workspace = Path.Combine(RequireContext().FilesDir!.AbsolutePath, "user-data");
            if (!Directory.Exists(workspace))
            {
                ShowList([], GetString(Resource.String.files_no_workspace));
                return;
            }
            _fs = new WorkspaceFileSystem(workspace);
        }
        try
        {
            ShowList(_fs.List(_dir), GetString(Resource.String.files_empty));
        }
        catch (WorkspaceException e)
        {
            ShowError(e);
        }
    }

    private void ShowList(IReadOnlyList<WorkspaceEntry> entries, string emptyText)
    {
        _entries = entries;
        _toolbar.Subtitle = GuestRoot + (_dir.Length == 0 ? "" : "/" + _dir);
        if (_dir.Length == 0)
        {
            _toolbar.NavigationIcon = null;
        }
        else
        {
            _toolbar.SetNavigationIcon(Resource.Drawable.ic_arrow_back);
            _toolbar.SetNavigationContentDescription(Resource.String.files_up);
        }
        _list.Adapter = new EntryAdapter(RequireContext(), entries);
        _empty.FindViewById<TextView>(Resource.Id.empty_text)!.Text = emptyText;
        _empty.Visibility = entries.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
    }

    private void GoUp()
    {
        if (_dir.Length == 0)
        {
            return;
        }
        var i = _dir.LastIndexOf('/');
        _dir = i < 0 ? "" : _dir[..i];
        ShowStatus(null);
        Refresh();
    }

    private void OnEntryClicked(WorkspaceEntry e)
    {
        switch (e.Kind)
        {
            case WorkspaceEntryKind.Directory:
                _dir = e.RelativePath;
                ShowStatus(null);
                Refresh();
                break;
            case WorkspaceEntryKind.Symlink:
                ShowStatus(GetString(Resource.String.files_symlink_blocked) + " → " + (e.LinkTarget ?? "?"));
                break;
            case WorkspaceEntryKind.File when e.Protected:
                ShowStatus(GetString(Resource.String.files_protected));
                break;
            case WorkspaceEntryKind.File:
                new MaterialAlertDialogBuilder(RequireContext())
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
            var ctx = RequireContext();
            var text = bytes.Contains((byte)0) ? GetString(Resource.String.files_binary) : Encoding.UTF8.GetString(bytes);
            var view = (TextView)LayoutInflater.From(ctx)!.Inflate(Resource.Layout.view_mono_text, null, false)!;
            view.Text = text;
            var scroll = new ScrollView(ctx);
            var pad = Rows.Dp(ctx, 24);
            scroll.SetPadding(pad, Rows.Dp(ctx, 8), pad, 0);
            scroll.AddView(view);
            new MaterialAlertDialogBuilder(ctx).SetTitle(e.Name)!.SetView(scroll)!.SetPositiveButton(Android.Resource.String.Ok, (_, _) => { })!.Show();
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
            ShowStatus(GetString(Resource.String.files_no_workspace));
            return;
        }
        StartActivityForResult(DocumentPicker.OpenIntent(null, multiple: true), RequestImport);
    }

    public override async void OnActivityResult(int requestCode, int resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (resultCode != (int)Android.App.Result.Ok || _fs is null)
        {
            return;
        }
        var resolver = RequireContext().ContentResolver!;
        try
        {
            if (requestCode == RequestImport)
            {
                var done = new List<string>();
                foreach (var uri in DocumentPicker.ResultUris(data))
                {
                    var name = DocumentPicker.DisplayName(resolver, uri);
                    await using var input = resolver.OpenInputStream(uri) ?? throw new IOException("cannot open " + name);
                    done.Add(await _fs.ImportAsync(_dir, name, input));
                }
                ShowStatus($"{GetString(Resource.String.files_imported)} {done.Count}: {string.Join(", ", done)}");
                Refresh();
            }
            else if (requestCode == RequestExport && _pendingExport is { } rel && data?.Data is { } target)
            {
                _pendingExport = null;
                await using var input = _fs.OpenRead(rel);
                await using var output = resolver.OpenOutputStream(target, "wt") ?? throw new IOException("cannot open export target");
                await input.CopyToAsync(output);
                ShowStatus($"{GetString(Resource.String.files_exported)} {rel}");
            }
        }
        catch (WorkspaceException e)
        {
            ShowError(e);
        }
        catch (Exception e) when (e is IOException or Java.Lang.Exception)
        {
            Log.Warn("DshMobile", $"files: {e.GetType().Name}: {e.Message}");
            ShowStatus($"{e.GetType().Name}: {e.Message}");
        }
    }

    private void ShowStatus(string? text)
    {
        _status.Text = text;
        _status.Visibility = string.IsNullOrEmpty(text) ? ViewStates.Gone : ViewStates.Visible;
    }

    private void ShowError(WorkspaceException e)
    {
        ShowStatus($"{e.Code}: {e.Message}");
        Log.Info("DshMobile", $"files {e.Code}: {e.Message}");
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / 1024.0 / 1024.0:F1} MB",
    };

    /// <summary>One <c>item_row</c> per entry: kind icon, name, size / link target, a lock on protected entries.</summary>
    private sealed class EntryAdapter(Context context, IReadOnlyList<WorkspaceEntry> entries) : BaseAdapter
    {
        public override int Count => entries.Count;

        public override Java.Lang.Object? GetItem(int position) => null;

        public override long GetItemId(int position) => position;

        public override View GetView(int position, View? convertView, ViewGroup? parent)
        {
            var view = convertView ?? LayoutInflater.From(context)!.Inflate(Resource.Layout.item_row, parent, false)!;
            var row = new Rows.Row(view);
            var e = entries[position];
            row.Title.Text = e.Name;
            switch (e.Kind)
            {
                case WorkspaceEntryKind.Directory:
                    row.Icon.SetImageResource(Resource.Drawable.ic_folder);
                    row.SetSubtitle(null);
                    break;
                case WorkspaceEntryKind.Symlink:
                    row.Icon.SetImageResource(Resource.Drawable.ic_link);
                    row.SetSubtitle("→ " + (e.LinkTarget ?? "?"));
                    break;
                default:
                    row.Icon.SetImageResource(Resource.Drawable.ic_file);
                    row.SetSubtitle(FormatSize(e.Size));
                    break;
            }
            if (e.Protected)
            {
                var lockIcon = new ImageView(context);
                lockIcon.SetImageResource(Resource.Drawable.ic_lock);
                lockIcon.ContentDescription = context.GetString(Resource.String.files_protected_short);
                row.SetTrailing(lockIcon);
            }
            else
            {
                row.SetTrailing(null);
            }
            return view;
        }
    }
}
