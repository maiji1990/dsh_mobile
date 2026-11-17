using System.Text;
using Android.Content;
using Android.Views;
using AndroidX.RecyclerView.Widget;
using DshMobile.App.Ui;
using DshMobile.Core.Files;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.Snackbar;
using Fragment = AndroidX.Fragment.App.Fragment;
using Log = Android.Util.Log;

namespace DshMobile.App.Files;

/// <summary>
/// Files tab (TASK-007, ADR-0021; vNext PR-09 workbench). Browses the DSH workspace (guest /root/projects = host
/// files/user-data) through <see cref="WorkspaceProvider"/>: every path rule and every refusal is the provider's (no
/// symlink traversal, <c>.dsh</c> untouchable, no overwrite, conflict-checked saves); this screen only offers the
/// actions <see cref="IFileProvider.CapabilitiesOf"/> allows. RecyclerView + DiffUtil, path bar, filter, sort, recursive
/// search, long-press multi-select with copy / cut / paste / delete (to a trash, undoable) / zip, extraction, a text
/// editor that never silently overwrites, SAF import / export. Back leaves search, then selection, then goes up.
/// </summary>
public sealed class FilesFragment : Fragment, ITabRoot
{
    private const int RequestImport = 71;
    private const int RequestExport = 72;
    private const string GuestRoot = "/root/projects";
    private const int DiffLimit = 5000; // above this a full rebind is cheaper than a diff
    private static readonly TimeSpan TrashAge = TimeSpan.FromDays(7);

    private const int MenuImport = 1;
    private const int MenuRefresh = 2;
    private const int MenuSearch = 3;
    private const int MenuSort = 4;
    private const int MenuNewFolder = 5;
    private const int MenuNewFile = 6;
    private const int MenuCopy = 10;
    private const int MenuCut = 11;
    private const int MenuDelete = 12;
    private const int MenuZip = 13;
    private const int MenuSelectAll = 14;
    private const int MenuClearSelection = 15;

    private WorkspaceProvider? _provider;
    private string _dir = "";
    private string? _searchQuery;
    private SearchResult? _searchResult;
    private string? _pendingExport;
    private FileSort _sort = FileSort.Name;
    private bool _descending;
    private string _filter = "";
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private List<string> _clip = [];
    private bool _clipCut;
    private bool _busy;
    private int _loadGeneration;

    private MaterialToolbar _toolbar = null!;
    private LinearLayout _crumbs = null!;
    private HorizontalScrollView _crumbsScroll = null!;
    private EditText _filterText = null!;
    private TextView _status = null!;
    private RecyclerView _list = null!;
    private View _empty = null!;
    private View _pasteBar = null!;
    private TextView _pasteText = null!;
    private EntryAdapter _adapter = null!;
    private IReadOnlyList<WorkspaceEntry> _entries = [];

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        var view = inflater.Inflate(Resource.Layout.fragment_files, container, false)!;
        _dir = savedInstanceState?.GetString("dir") ?? _dir;
        _toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.toolbar)!;
        _toolbar.SetTitle(Resource.String.files_title);
        _toolbar.MenuItemClick += (_, e) => OnMenu(e.Item?.ItemId ?? 0);
        _toolbar.NavigationClick += (_, _) => OnBack();
        _crumbs = view.FindViewById<LinearLayout>(Resource.Id.files_crumbs)!;
        _crumbsScroll = view.FindViewById<HorizontalScrollView>(Resource.Id.files_crumbs_scroll)!;
        _filterText = view.FindViewById<EditText>(Resource.Id.files_filter)!;
        _filterText.TextChanged += (_, _) =>
        {
            _filter = _filterText.Text ?? "";
            Show();
        };
        _status = view.FindViewById<TextView>(Resource.Id.files_status)!;
        _list = view.FindViewById<RecyclerView>(Resource.Id.files_list)!;
        _list.SetLayoutManager(new LinearLayoutManager(RequireContext()));
        _list.SetItemAnimator(null); // no flicker on 10k-row directories
        _adapter = new EntryAdapter(this);
        _list.SetAdapter(_adapter);
        _empty = view.FindViewById(Resource.Id.files_empty)!;
        _empty.FindViewById<ImageView>(Resource.Id.empty_icon)!.SetImageResource(Resource.Drawable.ic_folder);
        _pasteBar = view.FindViewById(Resource.Id.files_paste_bar)!;
        _pasteText = view.FindViewById<TextView>(Resource.Id.files_paste_text)!;
        view.FindViewById<MaterialButton>(Resource.Id.files_paste)!.Click += (_, _) => _ = PasteAsync();
        view.FindViewById<MaterialButton>(Resource.Id.files_paste_clear)!.Click += (_, _) =>
        {
            _clip = [];
            UpdateChrome();
        };
        EnsureProvider();
        _ = LoadAsync();
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
        if (!hidden && _searchQuery is null)
        {
            _ = LoadAsync(); // DSH may have changed the workspace meanwhile
        }
    }

    public bool OnBack()
    {
        if (_searchQuery is not null)
        {
            _searchQuery = null;
            _searchResult = null;
            _ = LoadAsync();
            return true;
        }
        if (_selected.Count > 0)
        {
            _selected.Clear();
            Show();
            return true;
        }
        if (_dir.Length == 0)
        {
            return false;
        }
        var i = _dir.LastIndexOf('/');
        Navigate(i < 0 ? "" : _dir[..i]);
        return true;
    }

    public void OnReselected() => Navigate("");

    private bool EnsureProvider()
    {
        if (_provider is not null)
        {
            return true;
        }
        var files = RequireContext().FilesDir!.AbsolutePath;
        var workspace = Path.Combine(files, "user-data");
        if (!Directory.Exists(workspace))
        {
            return false; // appears with the first runtime import
        }
        // The trash lives next to the workspace (same filesystem: deleting is a rename), outside what DSH can see.
        var provider = new WorkspaceProvider(new WorkspaceFileSystem(workspace), Path.Combine(files, "dshmobile-files-trash"), GuestRoot);
        _provider = provider;
        _ = Task.Run(() =>
        {
            try
            {
                provider.PurgeTrash(TrashAge);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn("DshMobile", "files: trash purge failed: " + e.Message);
            }
        });
        return true;
    }

    private void Navigate(string dir)
    {
        _dir = dir;
        _searchQuery = null;
        _searchResult = null;
        _selected.Clear();
        _filter = "";
        _filterText.Text = "";
        ShowStatus(null);
        _ = LoadAsync();
    }

    // ── loading and showing ───────────────────────────────────────────────

    private async Task LoadAsync()
    {
        if (!EnsureProvider())
        {
            _entries = [];
            Show(GetString(Resource.String.files_no_workspace));
            return;
        }
        var gen = ++_loadGeneration;
        try
        {
            var entries = await _provider!.ListAsync(_dir, CancellationToken.None);
            if (gen == _loadGeneration && IsAdded)
            {
                _entries = entries;
                _selected.IntersectWith(entries.Select(e => e.RelativePath));
                Show();
            }
        }
        catch (WorkspaceException e)
        {
            if (gen == _loadGeneration && IsAdded)
            {
                _entries = [];
                Show();
                ShowError(e);
            }
        }
    }

    private void Show(string? emptyText = null)
    {
        if (!IsAdded)
        {
            return;
        }
        var source = _searchResult?.Matches ?? _entries;
        var shown = FileListing.Filter(FileListing.Sort(source, _sort, _descending), _filter);
        _adapter.Update(shown, _selected, inSearch: _searchQuery is not null);
        _empty.FindViewById<TextView>(Resource.Id.empty_text)!.Text = emptyText ?? GetString(Resource.String.files_empty);
        _empty.Visibility = shown.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
        UpdateChrome();
    }

    private void UpdateChrome()
    {
        var ctx = RequireContext();
        _toolbar.Subtitle = _selected.Count > 0
            ? Rows.Fmt(ctx, Resource.String.files_selected, _selected.Count)
            : _searchQuery is { } q ? Rows.Fmt(ctx, Resource.String.files_search_title, q) : null;
        if (_dir.Length == 0 && _searchQuery is null && _selected.Count == 0)
        {
            _toolbar.NavigationIcon = null;
        }
        else
        {
            _toolbar.SetNavigationIcon(_selected.Count > 0 ? Resource.Drawable.ic_close : Resource.Drawable.ic_arrow_back);
            _toolbar.SetNavigationContentDescription(Resource.String.files_up);
        }
        BuildMenu();
        BuildCrumbs(ctx);
        _pasteBar.Visibility = _clip.Count > 0 && _searchQuery is null ? ViewStates.Visible : ViewStates.Gone;
        if (_clip.Count > 0)
        {
            _pasteText.Text = Rows.Fmt(ctx, _clipCut ? Resource.String.files_clip_cut : Resource.String.files_clip_copy, _clip.Count);
        }
    }

    private void BuildMenu()
    {
        var menu = _toolbar.Menu!;
        menu.Clear();
        void Item(int id, int title, int? icon = null)
        {
            var item = menu.Add(0, id, id, GetString(title))!;
            if (icon is { } i)
            {
                item.SetIcon(i);
                item.SetShowAsAction(ShowAsAction.IfRoom);
            }
            else
            {
                item.SetShowAsAction(ShowAsAction.Never);
            }
            item.SetEnabled(!_busy);
        }
        if (_selected.Count > 0)
        {
            Item(MenuCopy, Resource.String.files_copy, Resource.Drawable.ic_copy);
            Item(MenuDelete, Resource.String.files_delete, Resource.Drawable.ic_delete);
            Item(MenuCut, Resource.String.files_cut);
            Item(MenuZip, Resource.String.files_compress);
            Item(MenuSelectAll, Resource.String.files_select_all);
            Item(MenuClearSelection, Resource.String.files_clear_selection);
            return;
        }
        Item(MenuImport, Resource.String.files_import, Resource.Drawable.ic_upload);
        Item(MenuRefresh, Resource.String.files_refresh, Resource.Drawable.ic_refresh);
        Item(MenuSearch, Resource.String.files_search);
        Item(MenuSort, Resource.String.files_sort);
        Item(MenuNewFolder, Resource.String.files_new_folder);
        Item(MenuNewFile, Resource.String.files_new_file);
    }

    private void BuildCrumbs(Context ctx)
    {
        _crumbs.RemoveAllViews();
        var crumbs = FileListing.Breadcrumbs(GuestRoot, _dir);
        for (var i = 0; i < crumbs.Count; i++)
        {
            var crumb = crumbs[i];
            if (i > 0)
            {
                var sep = new ImageView(ctx);
                sep.SetImageResource(Resource.Drawable.ic_chevron_right);
                _crumbs.AddView(sep);
            }
            var b = new MaterialButton(ctx, null, Resource.Attribute.borderlessButtonStyle) { Text = crumb.Label };
            b.SetAllCaps(false);
            b.SetMinWidth(0);
            b.SetMinimumWidth(0);
            var last = i == crumbs.Count - 1 && _searchQuery is null;
            b.Enabled = !last;
            b.Click += (_, _) => Navigate(crumb.Path);
            _crumbs.AddView(b);
        }
        _crumbsScroll.Post(() => _crumbsScroll.FullScroll(FocusSearchDirection.Right));
    }

    // ── menu ──────────────────────────────────────────────────────────────

    private void OnMenu(int id)
    {
        switch (id)
        {
            case MenuImport:
                StartImport();
                break;
            case MenuRefresh:
                ShowStatus(null);
                _ = LoadAsync();
                break;
            case MenuSearch:
                AskName(Resource.String.files_search, Resource.String.files_search_hint, "", q => _ = SearchAsync(q));
                break;
            case MenuSort:
                ChooseSort();
                break;
            case MenuNewFolder:
                AskName(Resource.String.files_new_folder, Resource.String.files_name_hint, "", n => Run(() => _provider!.CreateDirectory(_dir, n)));
                break;
            case MenuNewFile:
                AskName(Resource.String.files_new_file, Resource.String.files_name_hint, "", n => Run(() => _provider!.CreateFile(_dir, n)));
                break;
            case MenuCopy:
            case MenuCut:
                _clip = [.. _selected];
                _clipCut = id == MenuCut;
                _selected.Clear();
                Show();
                break;
            case MenuDelete:
                ConfirmDelete([.. _selected]);
                break;
            case MenuZip:
                _ = ZipAsync([.. _selected]);
                break;
            case MenuSelectAll:
                foreach (var e in _adapter.Items)
                {
                    if (_provider!.CapabilitiesOf(e).HasFlag(FileCapabilities.Delete))
                    {
                        _selected.Add(e.RelativePath);
                    }
                }
                Show();
                break;
            case MenuClearSelection:
                _selected.Clear();
                Show();
                break;
        }
    }

    private void ChooseSort()
    {
        var keys = new[] { FileSort.Name, FileSort.Size, FileSort.Modified, FileSort.Type };
        var labels = new[] { Resource.String.files_sort_name, Resource.String.files_sort_size, Resource.String.files_sort_modified, Resource.String.files_sort_type }
            .Select((r, i) => GetString(r) + (keys[i] == _sort ? (_descending ? " ↓" : " ↑") : "")).ToArray();
        new MaterialAlertDialogBuilder(RequireContext())
            .SetTitle(GetString(Resource.String.files_sort) + " · " + GetString(Resource.String.files_sort_hint))!
            .SetSingleChoiceItems(labels, Array.IndexOf(keys, _sort), (d, e) =>
            {
                var key = keys[e.Which];
                _descending = key == _sort && !_descending;
                _sort = key;
                (d as IDialogInterface)?.Dismiss();
                Show();
            })!
            .Show(); // no SetMessage: an AlertDialog with a message hides its choice list
    }

    // ── entries ───────────────────────────────────────────────────────────

    private void OnClick(WorkspaceEntry e)
    {
        if (_selected.Count > 0)
        {
            Toggle(e);
            return;
        }
        if (e.Kind == WorkspaceEntryKind.Directory) // .dsh too: it is listed (read-only), never read
        {
            Navigate(e.RelativePath);
            return;
        }
        ShowActions(e);
    }

    private void OnLongClick(WorkspaceEntry e) => Toggle(e);

    private void Toggle(WorkspaceEntry e)
    {
        if (!_selected.Remove(e.RelativePath))
        {
            if (!_provider!.CapabilitiesOf(e).HasFlag(FileCapabilities.Delete))
            {
                ShowStatus(GetString(Resource.String.files_protected));
                return;
            }
            _selected.Add(e.RelativePath);
        }
        Show();
    }

    /// <summary>Exactly the actions this entry's capabilities allow.</summary>
    private void ShowActions(WorkspaceEntry e)
    {
        var provider = _provider!;
        var caps = provider.CapabilitiesOf(e);
        var actions = new List<(string Label, Action Run)>();
        if (e.Kind == WorkspaceEntryKind.Symlink)
        {
            ShowStatus(GetString(Resource.String.files_symlink_blocked) + " → " + (e.LinkTarget ?? "?"));
        }
        if (caps.HasFlag(FileCapabilities.Read))
        {
            actions.Add((GetString(caps.HasFlag(FileCapabilities.Write) ? Resource.String.files_edit : Resource.String.files_open_text), () => _ = OpenTextAsync(e)));
            actions.Add((GetString(Resource.String.files_export), () =>
            {
                _pendingExport = e.RelativePath;
                StartActivityForResult(DocumentPicker.CreateIntent(e.Name), RequestExport);
            }));
        }
        if (caps.HasFlag(FileCapabilities.Archive) && e.Kind == WorkspaceEntryKind.File && WorkspaceProvider.FormatOf(e.Name) is not null)
        {
            actions.Add((GetString(Resource.String.files_extract), () => _ = ExtractAsync(e)));
        }
        if (caps.HasFlag(FileCapabilities.Rename))
        {
            actions.Add((GetString(Resource.String.files_rename), () =>
                AskName(Resource.String.files_rename, Resource.String.files_name_hint, e.Name, n => Run(() => provider.Rename(e.RelativePath, n)))));
        }
        if (caps.HasFlag(FileCapabilities.Copy))
        {
            actions.Add((GetString(Resource.String.files_copy), () => SetClip([e.RelativePath], cut: false)));
        }
        if (caps.HasFlag(FileCapabilities.Move))
        {
            actions.Add((GetString(Resource.String.files_cut), () => SetClip([e.RelativePath], cut: true)));
        }
        if (caps.HasFlag(FileCapabilities.Archive) && e.Kind is WorkspaceEntryKind.File or WorkspaceEntryKind.Directory)
        {
            actions.Add((GetString(Resource.String.files_compress), () => _ = ZipAsync([e.RelativePath])));
        }
        if (caps.HasFlag(FileCapabilities.Delete))
        {
            actions.Add((GetString(Resource.String.files_delete), () => ConfirmDelete([e.RelativePath])));
        }
        if (actions.Count == 0)
        {
            ShowStatus(GetString(Resource.String.files_protected));
            return;
        }
        new MaterialAlertDialogBuilder(RequireContext())
            .SetTitle(e.Name)!
            .SetItems(actions.Select(a => a.Label).ToArray(), (_, a) => actions[a.Which].Run())!
            .Show();
    }

    private void SetClip(List<string> paths, bool cut)
    {
        _clip = paths;
        _clipCut = cut;
        UpdateChrome();
    }

    // ── operations ────────────────────────────────────────────────────────

    /// <summary>A quick synchronous provider call: result in the status line, then reload.</summary>
    private void Run(Func<string> op)
    {
        try
        {
            var done = op();
            ShowStatus(Rows.Fmt(RequireContext(), Resource.String.files_done, GuestRoot + "/" + done));
        }
        catch (WorkspaceException e)
        {
            ShowError(e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowStatus($"{WorkspaceErrors.IoError}: {e.Message}");
        }
        _ = LoadAsync();
    }

    /// <summary>A long provider call off the UI thread, with the menu disabled meanwhile.</summary>
    private async Task Busy(Func<Task<string?>> op)
    {
        if (_busy)
        {
            return;
        }
        _busy = true;
        ShowStatus(GetString(Resource.String.files_busy));
        BuildMenu();
        try
        {
            ShowStatus(await op());
        }
        catch (WorkspaceException e)
        {
            ShowError(e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowStatus($"{WorkspaceErrors.IoError}: {e.Message}");
        }
        finally
        {
            _busy = false;
        }
        if (IsAdded)
        {
            await LoadAsync();
        }
    }

    private string Describe(TransferResult r)
    {
        var ctx = RequireContext();
        var text = r.Failed.Count == 0
            ? Rows.Fmt(ctx, Resource.String.files_done, string.Join(", ", r.Created))
            : Rows.Fmt(ctx, Resource.String.files_partial, r.Created.Count, r.Failed.Count, string.Join("; ", r.Failed.Select(f => $"{f.Path} {f.Code}")));
        return r.SkippedLinks > 0 ? text + Rows.Fmt(ctx, Resource.String.files_skipped_links, r.SkippedLinks) : text;
    }

    private Task PasteAsync()
    {
        var paths = _clip;
        var cut = _clipCut;
        return Busy(async () =>
        {
            var r = cut
                ? await _provider!.MoveAsync(paths, _dir, CancellationToken.None)
                : await _provider!.CopyAsync(paths, _dir, CancellationToken.None);
            if (cut)
            {
                _clip = [];
            }
            return Describe(r);
        });
    }

    private void ConfirmDelete(List<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }
        var trash = _provider!.Capabilities.HasFlag(FileCapabilities.Trash);
        new MaterialAlertDialogBuilder(RequireContext())
            .SetIcon(Resource.Drawable.ic_delete)!
            .SetTitle(Rows.Fmt(RequireContext(), Resource.String.files_delete_title, paths.Count))!
            .SetMessage(trash ? Resource.String.files_delete_trash : Resource.String.files_delete_permanent)!
            .SetPositiveButton(Resource.String.files_delete, (_, _) => _ = DeleteAsync(paths))!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .Show();
    }

    private async Task DeleteAsync(List<string> paths)
    {
        DeleteReceipt? receipt = null;
        await Busy(async () =>
        {
            var got = await _provider!.DeleteAsync(paths, CancellationToken.None);
            receipt = got;
            _selected.Clear();
            _clip.RemoveAll(c => got.Deleted.Contains(c));
            return got.Failed.Count == 0 ? null
                : Rows.Fmt(RequireContext(), Resource.String.files_partial, got.Deleted.Count, got.Failed.Count, string.Join("; ", got.Failed.Select(f => $"{f.Path} {f.Code}")));
        });
        if (receipt is { Deleted.Count: > 0 } r && IsAdded && View is { } root)
        {
            var bar = Snackbar.Make(root, Rows.Fmt(RequireContext(), Resource.String.files_deleted, r.Deleted.Count), Snackbar.LengthLong);
            if (r.Restorable)
            {
                bar.SetAction(GetString(Resource.String.files_undo), view => _ = Busy(async () =>
                {
                    var restored = await _provider!.RestoreAsync(r, CancellationToken.None);
                    return Rows.Fmt(RequireContext(), Resource.String.files_restored, string.Join(", ", restored));
                }));
            }
            bar.Show();
        }
    }

    private Task ExtractAsync(WorkspaceEntry e) => Busy(async () =>
    {
        var r = await _provider!.ExtractAsync(e.RelativePath, _dir, null, CancellationToken.None);
        var ctx = RequireContext();
        var text = Rows.Fmt(ctx, Resource.String.files_extracted, GuestRoot + "/" + r.Directory, r.Files);
        return r.Skipped.Count == 0 ? text : text + Rows.Fmt(ctx, Resource.String.files_extract_skipped, r.Skipped.Count, string.Join(", ", r.Skipped.Take(5)));
    });

    private Task ZipAsync(List<string> paths)
    {
        if (paths.Count == 0)
        {
            return Task.CompletedTask;
        }
        var name = paths.Count == 1 ? paths[0][(paths[0].LastIndexOf('/') + 1)..] : "archive";
        return Busy(async () =>
        {
            var r = await _provider!.CreateZipAsync(paths, _dir, name, CancellationToken.None);
            _selected.Clear();
            var text = Rows.Fmt(RequireContext(), Resource.String.files_zipped, GuestRoot + "/" + r.Path, r.Files);
            return r.SkippedLinks > 0 ? text + Rows.Fmt(RequireContext(), Resource.String.files_skipped_links, r.SkippedLinks) : text;
        });
    }

    private async Task SearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || _provider is null)
        {
            return;
        }
        var fs = _provider.FileSystem;
        var start = _dir;
        await Busy(async () =>
        {
            var r = await Task.Run(() => FileListing.Search(fs, start, query));
            _searchQuery = query.Trim();
            _searchResult = r;
            _selected.Clear();
            return Rows.Fmt(RequireContext(), r.Truncated ? Resource.String.files_search_truncated : Resource.String.files_search_result, r.Matches.Count, r.Visited);
        });
        Show();
    }

    // ── text editor ───────────────────────────────────────────────────────

    private async Task OpenTextAsync(WorkspaceEntry e, string? keepEdits = null)
    {
        TextDocument? doc;
        try
        {
            var (bytes, version) = await Task.Run(() =>
            {
                var v = _provider!.Version(e.RelativePath);
                using var s = _provider.FileSystem.OpenRead(e.RelativePath, TextDocument.EditLimit);
                var b = new byte[s.Length];
                s.ReadExactly(b);
                return (b, v);
            });
            doc = TextDocument.Decode(bytes, version);
        }
        catch (WorkspaceException ex)
        {
            ShowError(ex);
            return;
        }
        catch (IOException ex)
        {
            ShowStatus($"{WorkspaceErrors.IoError}: {ex.Message}");
            return;
        }
        if (!IsAdded)
        {
            return;
        }
        if (doc is null)
        {
            ShowStatus(GetString(Resource.String.files_binary));
            return;
        }
        var writable = !doc.ReadOnly && _provider!.CapabilitiesOf(e).HasFlag(FileCapabilities.Write);
        ShowEditor(e, doc, keepEdits ?? doc.Text, writable);
    }

    private void ShowEditor(WorkspaceEntry e, TextDocument doc, string text, bool writable)
    {
        var ctx = RequireContext();
        var editor = new EditText(ctx)
        {
            Text = text,
            Typeface = Android.Graphics.Typeface.Monospace,
            Gravity = GravityFlags.Top | GravityFlags.Start,
            InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextFlagMultiLine | Android.Text.InputTypes.TextFlagNoSuggestions,
        };
        editor.SetHorizontallyScrolling(true);
        editor.SetTextSize(Android.Util.ComplexUnitType.Sp, 13);
        if (!writable)
        {
            editor.KeyListener = null; // selectable, not editable
            editor.SetTextIsSelectable(true);
        }
        var pad = Rows.Dp(ctx, 16);
        var frame = new FrameLayout(ctx);
        frame.SetPadding(pad, Rows.Dp(ctx, 8), pad, 0);
        frame.AddView(editor, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Rows.Dp(ctx, 420)));
        var kind = TextDocument.KindOf(e.Name);
        var title = e.Name + (doc.ReadOnly ? " · " + Rows.Fmt(ctx, Resource.String.files_readonly, doc.ReadOnlyReason ?? "") : "");
        var builder = new MaterialAlertDialogBuilder(ctx).SetTitle(title)!.SetView(frame)!;
        if (writable)
        {
            builder.SetPositiveButton(Resource.String.files_save, (_, _) =>
            {
                var edited = editor.Text ?? "";
                if (TextDocument.Validate(kind, edited) is { } warning)
                {
                    new MaterialAlertDialogBuilder(ctx)
                        .SetTitle(Resource.String.files_validate_title)!
                        .SetMessage(warning)!
                        .SetPositiveButton(Resource.String.files_save_anyway, (_, _) => _ = SaveAsync(e, doc, edited, doc.Version))!
                        .SetNegativeButton(Resource.String.files_conflict_keep, (_, _) => ShowEditor(e, doc, edited, writable))!
                        .Show();
                    return;
                }
                _ = SaveAsync(e, doc, edited, doc.Version);
            });
            builder.SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { });
        }
        else
        {
            builder.SetPositiveButton(Android.Resource.String.Ok, (_, _) => { });
        }
        builder.Show();
    }

    /// <summary>Saves against the version the editor read; a changed file is a choice for the user, never an overwrite.</summary>
    private async Task SaveAsync(WorkspaceEntry e, TextDocument doc, string edited, FileVersion expected)
    {
        try
        {
            var bytes = doc.Encode(edited);
            await _provider!.WriteAsync(e.RelativePath, new MemoryStream(bytes), expected, CancellationToken.None);
            ShowStatus(Rows.Fmt(RequireContext(), Resource.String.files_saved, GuestRoot + "/" + e.RelativePath));
            _ = LoadAsync();
        }
        catch (WorkspaceException ex) when (ex.Code == WorkspaceErrors.Conflict && IsAdded)
        {
            var ctx = RequireContext();
            new MaterialAlertDialogBuilder(ctx)
                .SetIcon(Resource.Drawable.ic_error)!
                .SetTitle(Resource.String.files_conflict_title)!
                .SetMessage(Rows.Fmt(ctx, Resource.String.files_conflict_message, e.Name))!
                .SetPositiveButton(Resource.String.files_conflict_keep, (_, _) => ShowEditor(e, doc, edited, writable: true))!
                .SetNeutralButton(Resource.String.files_conflict_reload, (_, _) => _ = OpenTextAsync(e))!
                .SetNegativeButton(Resource.String.files_conflict_overwrite, (_, _) => _ = OverwriteAsync(e, doc, edited))!
                .Show();
        }
        catch (WorkspaceException ex)
        {
            ShowError(ex);
        }
        catch (IOException ex)
        {
            ShowStatus($"{WorkspaceErrors.IoError}: {ex.Message}");
        }
    }

    /// <summary>The user chose to overwrite the other change: save against what is there now (still atomic).</summary>
    private async Task OverwriteAsync(WorkspaceEntry e, TextDocument doc, string edited)
    {
        FileVersion? current = null;
        try
        {
            current = await _provider!.VersionAsync(e.RelativePath, CancellationToken.None);
        }
        catch (WorkspaceException ex) when (ex.Code == WorkspaceErrors.NotFound)
        {
            // deleted meanwhile: write it as a new file
        }
        try
        {
            var bytes = doc.Encode(edited);
            await _provider!.WriteAsync(e.RelativePath, new MemoryStream(bytes), current, CancellationToken.None);
            ShowStatus(Rows.Fmt(RequireContext(), Resource.String.files_saved, GuestRoot + "/" + e.RelativePath));
            _ = LoadAsync();
        }
        catch (WorkspaceException ex)
        {
            ShowError(ex); // changed yet again: the user sees it and decides again
        }
    }

    // ── dialogs, import / export ──────────────────────────────────────────

    private void AskName(int title, int hint, string initial, Action<string> onOk)
    {
        var ctx = RequireContext();
        var input = new EditText(ctx) { Text = initial, Hint = GetString(hint), InputType = Android.Text.InputTypes.ClassText };
        input.SetSingleLine(true);
        var frame = new FrameLayout(ctx);
        var pad = Rows.Dp(ctx, 20);
        frame.SetPadding(pad, Rows.Dp(ctx, 8), pad, 0);
        frame.AddView(input);
        new MaterialAlertDialogBuilder(ctx)
            .SetTitle(title)!
            .SetView(frame)!
            .SetPositiveButton(Android.Resource.String.Ok, (_, _) =>
            {
                var text = input.Text ?? "";
                if (text.Length > 0)
                {
                    onOk(text);
                }
            })!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .Show();
    }

    private void StartImport()
    {
        if (!EnsureProvider())
        {
            ShowStatus(GetString(Resource.String.files_no_workspace));
            return;
        }
        StartActivityForResult(DocumentPicker.OpenIntent(null, multiple: true), RequestImport);
    }

    public override async void OnActivityResult(int requestCode, int resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (resultCode != (int)Android.App.Result.Ok || !EnsureProvider())
        {
            return;
        }
        var fs = _provider!.FileSystem;
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
                    done.Add(await fs.ImportAsync(_dir, name, input));
                }
                ShowStatus($"{GetString(Resource.String.files_imported)} {done.Count}: {string.Join(", ", done)}");
                await LoadAsync();
            }
            else if (requestCode == RequestExport && _pendingExport is { } rel && data?.Data is { } target)
            {
                _pendingExport = null;
                await using var input = fs.OpenRead(rel);
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
        if (!IsAdded)
        {
            return;
        }
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
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F1} GB",
    };

    // ── list ──────────────────────────────────────────────────────────────

    private sealed class RowHolder(View view) : RecyclerView.ViewHolder(view)
    {
        public Rows.Row Row { get; } = new(view);

        public WorkspaceEntry? Entry { get; set; }
    }

    /// <summary>One <c>item_row</c> per entry; DiffUtil keeps updates cheap and the scroll position stable.</summary>
    private sealed class EntryAdapter(FilesFragment owner) : RecyclerView.Adapter
    {
        private List<(WorkspaceEntry Entry, bool Selected)> _rows = [];
        private bool _inSearch;

        public IEnumerable<WorkspaceEntry> Items => _rows.Select(r => r.Entry);

        public override int ItemCount => _rows.Count;

        public void Update(IReadOnlyList<WorkspaceEntry> entries, HashSet<string> selected, bool inSearch)
        {
            var next = entries.Select(e => (e, selected.Contains(e.RelativePath))).ToList();
            var old = _rows;
            var searchChanged = inSearch != _inSearch;
            _rows = next;
            _inSearch = inSearch;
            if (searchChanged || old.Count > DiffLimit || next.Count > DiffLimit)
            {
                NotifyDataSetChanged();
                return;
            }
            DiffUtil.CalculateDiff(new Diff(old, next), false).DispatchUpdatesTo(this);
        }

        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
        {
            var view = LayoutInflater.From(parent.Context)!.Inflate(Resource.Layout.item_row, parent, false)!;
            var holder = new RowHolder(view);
            view.Click += (_, _) =>
            {
                if (holder.Entry is { } e)
                {
                    owner.OnClick(e);
                }
            };
            view.LongClick += (_, a) =>
            {
                a.Handled = holder.Entry is not null;
                if (holder.Entry is { } e)
                {
                    owner.OnLongClick(e);
                }
            };
            return holder;
        }

        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
        {
            var h = (RowHolder)holder;
            var (e, selected) = _rows[position];
            h.Entry = e;
            var row = h.Row;
            var context = row.View.Context!;
            row.Title.Text = e.Name;
            var where = _inSearch ? WhereOf(e.RelativePath) : null;
            switch (e.Kind)
            {
                case WorkspaceEntryKind.Directory:
                    row.Icon.SetImageResource(Resource.Drawable.ic_folder);
                    row.SetSubtitle(where);
                    break;
                case WorkspaceEntryKind.Symlink:
                    row.Icon.SetImageResource(Resource.Drawable.ic_link);
                    row.SetSubtitle("→ " + (e.LinkTarget ?? "?") + (where is null ? "" : " · " + where));
                    break;
                default:
                    row.Icon.SetImageResource(WorkspaceProvider.FormatOf(e.Name) is not null ? Resource.Drawable.ic_storage
                        : TextDocument.KindOf(e.Name) == TextKind.Plain ? Resource.Drawable.ic_file : Resource.Drawable.ic_code);
                    row.SetSubtitle(FormatSize(e.Size) + " · " + e.Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)
                        + (where is null ? "" : " · " + where));
                    break;
            }
            if (selected || e.Protected)
            {
                var icon = new ImageView(context);
                icon.SetImageResource(selected ? Resource.Drawable.ic_check_circle : Resource.Drawable.ic_lock);
                icon.ContentDescription = context.GetString(selected ? Resource.String.files_clear_selection : Resource.String.files_protected_short);
                row.SetTrailing(icon);
            }
            else
            {
                row.SetTrailing(null);
            }
            row.View.SetBackgroundColor(selected
                ? new Android.Graphics.Color(Rows.Color(row.View, Resource.Attribute.colorSecondaryContainer))
                : Android.Graphics.Color.Transparent);
        }

        private static string WhereOf(string path)
        {
            var i = path.LastIndexOf('/');
            return GuestRoot + (i < 0 ? "" : "/" + path[..i]);
        }

        private sealed class Diff(List<(WorkspaceEntry Entry, bool Selected)> old, List<(WorkspaceEntry Entry, bool Selected)> next) : DiffUtil.Callback
        {
            public override int OldListSize => old.Count;

            public override int NewListSize => next.Count;

            public override bool AreItemsTheSame(int oldItemPosition, int newItemPosition) =>
                old[oldItemPosition].Entry.RelativePath == next[newItemPosition].Entry.RelativePath;

            public override bool AreContentsTheSame(int oldItemPosition, int newItemPosition) =>
                old[oldItemPosition] == next[newItemPosition];
        }
    }
}
