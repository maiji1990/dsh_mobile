using Android.Content;
using Android.Text;
using Android.Text.Method;
using Android.Views;
using Android.Widget;
using AndroidX.Activity;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;
using DshMobile.App.Ui;
using DshMobile.Core.Files;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.ProgressIndicator;
using Google.Android.Material.Snackbar;

namespace DshMobile.App.Files;

/// <summary>
/// UI Slice 3：全屏工作区文本阅读与编辑页（全 XML <c>activity_file_editor.xml</c>），
/// 取代此前 420dp 弹窗编辑器，严格保留 <see cref="TextDocument"/> 编码/行尾保真、
/// 只读保护、语法提示校验及 <see cref="WorkspaceErrors.Conflict"/> 三方冲突选择逻辑。
/// </summary>
[Activity(
    Label = "@string/files_editor_title",
    Exported = false,
    WindowSoftInputMode = SoftInput.AdjustResize)]
public sealed class WorkspaceEditorActivity : AppCompatActivity
{
    public const string ExtraRelativePath = "editor.extra.relative_path";
    public const string ExtraStartEditing = "editor.extra.start_editing";
    public const string ExtraSavedPath = "editor.extra.saved_path";

    private const string GuestRoot = "/root/projects";

    private WorkspaceProvider? _provider;
    private WorkspaceEntry? _entry;
    private TextDocument? _doc;
    private string _relativePath = "";
    private string _loadedText = "";
    private bool _writable;
    private bool _isEditing;
    private bool _dirty;
    private bool _savedOnce;
    private bool _suppressDirtyEvents;

    private MaterialToolbar _toolbar = null!;
    private IMenuItem? _toggleItem;
    private IMenuItem? _saveItem;
    private LinearProgressIndicator _progress = null!;
    private TextView _notice = null!;
    private EditText _input = null!;
    private TextView _meta = null!;
    private MaterialButton _modePill = null!;
    private IKeyListener? _editableKeyListener;

    public static Intent CreateIntent(Context context, string relativePath, bool startEditing) =>
        new Intent(context, typeof(WorkspaceEditorActivity))
            .PutExtra(ExtraRelativePath, relativePath)
            .PutExtra(ExtraStartEditing, startEditing);

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Edges.Enable(Window!);
        SetContentView(Resource.Layout.activity_file_editor);

        var root = FindViewById(Resource.Id.screen_root)!;
        ViewCompat.SetOnApplyWindowInsetsListener(root, new Edges.Listener((v, insets) =>
        {
            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout())!;
            var ime = insets.GetInsets(WindowInsetsCompat.Type.Ime())!;
            v.SetPadding(bars.Left, bars.Top, bars.Right, Math.Max(bars.Bottom, ime.Bottom));
            return insets;
        }));

        _relativePath = Intent?.GetStringExtra(ExtraRelativePath) ?? "";
        _isEditing = Intent?.GetBooleanExtra(ExtraStartEditing, false) ?? false;

        _toolbar = FindViewById<MaterialToolbar>(Resource.Id.editor_toolbar)!;
        _toggleItem = _toolbar.Menu?.FindItem(Resource.Id.editor_menu_toggle_mode);
        _saveItem = _toolbar.Menu?.FindItem(Resource.Id.editor_menu_save);
        _toggleItem?.SetVisible(false);
        _saveItem?.SetVisible(false);
        _toolbar.NavigationClick += (_, _) => HandleCloseRequested();
        _toolbar.MenuItemClick += (_, e) => OnMenuClicked(e.Item?.ItemId ?? 0);

        _progress = FindViewById<LinearProgressIndicator>(Resource.Id.editor_progress)!;
        _notice = FindViewById<TextView>(Resource.Id.editor_notice)!;
        _input = FindViewById<EditText>(Resource.Id.editor_input)!;
        _editableKeyListener = _input.KeyListener;
        _input.TextChanged += (_, _) =>
        {
            if (_suppressDirtyEvents)
            {
                return;
            }
            _dirty = !string.Equals(_input.Text ?? "", _loadedText, StringComparison.Ordinal);
            UpdateMetaAndMenu();
        };

        _meta = FindViewById<TextView>(Resource.Id.editor_meta)!;
        _modePill = FindViewById<MaterialButton>(Resource.Id.editor_mode_pill)!;
        _modePill.Click += (_, _) => ToggleEditingMode();

        OnBackPressedDispatcher.AddCallback(this, new CloseBackCallback(this));

        _toolbar.Title = string.IsNullOrWhiteSpace(_relativePath)
            ? GetString(Resource.String.files_editor_title)
            : Path.GetFileName(_relativePath);
        _toolbar.Subtitle = string.IsNullOrWhiteSpace(_relativePath) ? GuestRoot : $"{GuestRoot}/{_relativePath}";

        Edges.ClampMaxWidth(FindViewById(Resource.Id.editor_scroll));

        _ = LoadFileAsync();
    }

    private bool EnsureProvider()
    {
        if (_provider is not null)
        {
            return true;
        }
        var files = FilesDir!.AbsolutePath;
        var workspace = Path.Combine(files, "user-data");
        if (!Directory.Exists(workspace))
        {
            return false;
        }
        _provider = new WorkspaceProvider(
            new WorkspaceFileSystem(workspace),
            Path.Combine(files, "dshmobile-files-trash"),
            GuestRoot);
        return true;
    }

    private async Task LoadFileAsync(bool preserveEdits = false, string? keepText = null)
    {
        if (string.IsNullOrWhiteSpace(_relativePath) || !EnsureProvider())
        {
            ShowErrorBanner(GetString(Resource.String.files_no_workspace));
            return;
        }

        _progress.Visibility = ViewStates.Visible;
        try
        {
            var provider = _provider!;
            var (entry, doc) = await Task.Run(async () =>
            {
                var st = await provider.StatAsync(_relativePath, CancellationToken.None).ConfigureAwait(false);
                var ver = provider.Version(_relativePath);
                using var s = provider.FileSystem.OpenRead(_relativePath, TextDocument.EditLimit);
                var bytes = new byte[s.Length];
                s.ReadExactly(bytes);
                return (st, TextDocument.Decode(bytes, ver));
            });

            if (doc is null)
            {
                _progress.Visibility = ViewStates.Gone;
                ShowErrorBanner(GetString(Resource.String.files_binary));
                _input.KeyListener = null;
                _input.SetTextIsSelectable(true);
                _writable = false;
                UpdateMetaAndMenu();
                return;
            }

            _entry = entry;
            _doc = doc;
            _writable = !doc.ReadOnly && provider.CapabilitiesOf(entry).HasFlag(FileCapabilities.Write);
            if (!_writable)
            {
                _isEditing = false;
            }
            _loadedText = doc.Text;
            var display = preserveEdits && keepText is not null ? keepText : doc.Text;

            _suppressDirtyEvents = true;
            _input.Text = display;
            _suppressDirtyEvents = false;
            _dirty = preserveEdits && !string.Equals(display, _loadedText, StringComparison.Ordinal);

            if (doc.ReadOnly)
            {
                _notice.Visibility = ViewStates.Visible;
                _notice.Text = Rows.Fmt(this, Resource.String.files_readonly, doc.ReadOnlyReason ?? "");
            }
            else
            {
                _notice.Visibility = ViewStates.Gone;
            }

            ApplyEditability();
            UpdateMetaAndMenu();
        }
        catch (WorkspaceException ex)
        {
            ShowErrorBanner($"{ex.Code}: {ex.Message}");
        }
        catch (IOException ex)
        {
            ShowErrorBanner($"{WorkspaceErrors.IoError}: {ex.Message}");
        }
        finally
        {
            _progress.Visibility = ViewStates.Gone;
        }
    }

    private void ApplyEditability()
    {
        if (_writable && _isEditing)
        {
            _input.KeyListener = _editableKeyListener;
            _input.SetTextIsSelectable(true);
        }
        else
        {
            _input.KeyListener = null;
            _input.SetTextIsSelectable(true);
        }
    }

    private void ToggleEditingMode()
    {
        if (!_writable)
        {
            return;
        }
        _isEditing = !_isEditing;
        ApplyEditability();
        UpdateMetaAndMenu();
        if (_isEditing)
        {
            _input.RequestFocus();
        }
    }

    private void UpdateMetaAndMenu()
    {
        var text = _input.Text ?? "";
        var lineCount = text.Length == 0 ? 1 : text.Count(c => c == '\n') + 1;
        var encoding = _doc?.Encoding.ToString() ?? "UTF-8";
        _meta.Text = Rows.Fmt(this, Resource.String.files_editor_meta_fmt, encoding, lineCount, text.Length);

        _modePill.Visibility = _writable ? ViewStates.Visible : ViewStates.Gone;
        _modePill.SetText(_isEditing ? Resource.String.files_editor_state_editing : Resource.String.files_editor_mode_read);

        _toggleItem?.SetVisible(_writable);
        _toggleItem?.SetTitle(_isEditing ? Resource.String.files_editor_exit_edit : Resource.String.files_editor_mode_edit);
        _saveItem?.SetVisible(_writable && (_isEditing || _dirty));
        _saveItem?.SetEnabled(_dirty || _isEditing);
    }

    private void OnMenuClicked(int itemId)
    {
        if (itemId == Resource.Id.editor_menu_toggle_mode)
        {
            ToggleEditingMode();
        }
        else if (itemId == Resource.Id.editor_menu_save)
        {
            TrySaveWithValidation();
        }
    }

    private void TrySaveWithValidation()
    {
        if (!_writable || _entry is null || _doc is null)
        {
            return;
        }
        var edited = _input.Text ?? "";
        var kind = TextDocument.KindOf(_entry.Name);
        if (TextDocument.Validate(kind, edited) is { } warning)
        {
            new MaterialAlertDialogBuilder(this)
                .SetTitle(Resource.String.files_validate_title)!
                .SetMessage(warning)!
                .SetPositiveButton(Resource.String.files_save_anyway, (_, _) => _ = SaveAsync(_entry, _doc, edited, _doc.Version))!
                .SetNegativeButton(Resource.String.files_conflict_keep, (_, _) => { })!
                .Show();
            return;
        }
        _ = SaveAsync(_entry, _doc, edited, _doc.Version);
    }

    private async Task SaveAsync(WorkspaceEntry entry, TextDocument doc, string edited, FileVersion expected)
    {
        _progress.Visibility = ViewStates.Visible;
        try
        {
            var bytes = doc.Encode(edited);
            await _provider!.WriteAsync(entry.RelativePath, new MemoryStream(bytes), expected, CancellationToken.None);
            _savedOnce = true;
            _dirty = false;
            SetResult(global::Android.App.Result.Ok, new Intent().PutExtra(ExtraSavedPath, entry.RelativePath));
            var freshVersion = await _provider.VersionAsync(entry.RelativePath, CancellationToken.None);
            _doc = TextDocument.Decode(bytes, freshVersion) ?? doc;
            _loadedText = _doc.Text;
            UpdateMetaAndMenu();
            Snackbar.Make(FindViewById(Resource.Id.screen_root)!, Rows.Fmt(this, Resource.String.files_saved, $"{GuestRoot}/{entry.RelativePath}"), Snackbar.LengthShort).Show();
        }
        catch (WorkspaceException ex) when (ex.Code == WorkspaceErrors.Conflict)
        {
            new MaterialAlertDialogBuilder(this)
                .SetIcon(Resource.Drawable.ic_error)!
                .SetTitle(Resource.String.files_conflict_title)!
                .SetMessage(Rows.Fmt(this, Resource.String.files_conflict_message, entry.Name))!
                .SetPositiveButton(Resource.String.files_conflict_keep, (_, _) => { })!
                .SetNeutralButton(Resource.String.files_conflict_reload, (_, _) => _ = LoadFileAsync())!
                .SetNegativeButton(Resource.String.files_conflict_overwrite, (_, _) => _ = OverwriteAsync(entry, doc, edited))!
                .Show();
        }
        catch (WorkspaceException ex)
        {
            ShowErrorBanner($"{ex.Code}: {ex.Message}");
        }
        catch (IOException ex)
        {
            ShowErrorBanner($"{WorkspaceErrors.IoError}: {ex.Message}");
        }
        finally
        {
            _progress.Visibility = ViewStates.Gone;
        }
    }

    private async Task OverwriteAsync(WorkspaceEntry entry, TextDocument doc, string edited)
    {
        FileVersion? current = null;
        try
        {
            current = await _provider!.VersionAsync(entry.RelativePath, CancellationToken.None);
        }
        catch (WorkspaceException ex) when (ex.Code == WorkspaceErrors.NotFound)
        {
        }
        try
        {
            var bytes = doc.Encode(edited);
            await _provider!.WriteAsync(entry.RelativePath, new MemoryStream(bytes), current, CancellationToken.None);
            _savedOnce = true;
            _dirty = false;
            SetResult(global::Android.App.Result.Ok, new Intent().PutExtra(ExtraSavedPath, entry.RelativePath));
            var freshVersion = await _provider.VersionAsync(entry.RelativePath, CancellationToken.None);
            _doc = TextDocument.Decode(bytes, freshVersion) ?? doc;
            _loadedText = _doc.Text;
            UpdateMetaAndMenu();
            Snackbar.Make(FindViewById(Resource.Id.screen_root)!, Rows.Fmt(this, Resource.String.files_saved, $"{GuestRoot}/{entry.RelativePath}"), Snackbar.LengthShort).Show();
        }
        catch (WorkspaceException ex)
        {
            ShowErrorBanner($"{ex.Code}: {ex.Message}");
        }
    }

    private void ShowErrorBanner(string msg)
    {
        _notice.Visibility = ViewStates.Visible;
        _notice.Text = msg;
    }

    private void HandleCloseRequested()
    {
        if (!_dirty)
        {
            if (_savedOnce)
            {
                SetResult(global::Android.App.Result.Ok, new Intent().PutExtra(ExtraSavedPath, _relativePath));
            }
            Finish();
            return;
        }
        new MaterialAlertDialogBuilder(this)
            .SetTitle(Resource.String.files_editor_discard_title)!
            .SetMessage(Resource.String.files_editor_discard_message)!
            .SetPositiveButton(Resource.String.files_editor_discard_confirm, (_, _) => Finish())!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .Show();
    }

    private sealed class CloseBackCallback(WorkspaceEditorActivity owner) : OnBackPressedCallback(true)
    {
        public override void HandleOnBackPressed() => owner.HandleCloseRequested();
    }
}
