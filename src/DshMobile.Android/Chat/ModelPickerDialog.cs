using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.Fragment.App;
using AndroidX.RecyclerView.Widget;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Presentation;
using DshMobile.App.Ui;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Button;
using Google.Android.Material.Chip;
using Google.Android.Material.Snackbar;
using FragmentManager = AndroidX.Fragment.App.FragmentManager;

namespace DshMobile.App.Chat;

/// <summary>
/// UI-S4 模型与推理档位底部选择器（<see cref="BottomSheetDialogFragment"/>）：
/// 顶部一条统一的推理档位分段芯片，模型列表按 Provider 分组并以约 48dp 紧凑单行呈现，
/// 当前使用模型优先高亮；整个过程不重复为每个模型渲染档位按钮。
/// 配置变更后由 Fragment 重建，tag 去重避免重复弹窗。
/// </summary>
internal sealed class ModelPickerDialog : BottomSheetDialogFragment
{
    public const string SheetTag = "dsh.model_picker.bottom_sheet";

    private const int ViewTypeProviderHeader = 0;
    private const int ViewTypeModel = 1;

    private ChatViewModel _vm = null!;
    private RecyclerView _list = null!;
    private View _effortSection = null!;
    private ChipGroup _effortChips = null!;
    private TextView _empty = null!;
    private PickerAdapter _adapter = null!;
    private int _renderPosted;
    private bool _isApplying;

    /// <summary>以 tag 去重打开选择器；已存在实例时直接复用。</summary>
    public static void Show(FragmentManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (manager.FindFragmentByTag(SheetTag) is null)
        {
            new ModelPickerDialog().Show(manager, SheetTag);
        }
    }

    public override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetStyle(AndroidX.Fragment.App.DialogFragment.StyleNoTitle, Resource.Style.ThemeOverlay_DshMobile_BottomSheetDialog);
    }

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        var view = inflater.Inflate(Resource.Layout.sheet_model_picker, container, false)!;
        Edges.Pad(view, top: false, bottom: true);

        _vm = DshMobileApplication.Current.Bridge.Agent.Chat;
        _list = view.FindViewById<RecyclerView>(Resource.Id.model_picker_list)!;
        _effortSection = view.FindViewById(Resource.Id.model_picker_effort_section)!;
        _effortChips = view.FindViewById<ChipGroup>(Resource.Id.model_picker_effort_chips)!;
        _empty = view.FindViewById<TextView>(Resource.Id.model_picker_empty)!;

        _adapter = new PickerAdapter(spec => _ = ApplySelectionAsync(spec));
        _list.SetLayoutManager(new LinearLayoutManager(RequireContext()));
        _list.SetAdapter(_adapter);

        view.FindViewById<MaterialButton>(Resource.Id.model_picker_creds_btn)!.Click += (_, _) =>
        {
            DismissAllowingStateLoss();
            StartActivity(new Intent(RequireContext(), typeof(Credentials.CredentialsActivity)));
        };

        return view;
    }

    public override void OnStart()
    {
        base.OnStart();
        // 全展开并禁止半收起，让顶部档位条与长列表都可直接操作。
        if (Dialog is BottomSheetDialog sheet)
        {
            sheet.Behavior.SkipCollapsed = true;
            sheet.Behavior.State = BottomSheetBehavior.StateExpanded;
        }
        _vm.StateChanged += OnChatStateChanged;
        Render();
    }

    public override void OnStop()
    {
        _vm.StateChanged -= OnChatStateChanged;
        base.OnStop();
    }

    private void OnChatStateChanged(ChatUiState _) => ScheduleRender();

    private void ScheduleRender()
    {
        if (Interlocked.Exchange(ref _renderPosted, 1) == 0)
        {
            Activity?.RunOnUiThread(() =>
            {
                Volatile.Write(ref _renderPosted, 0);
                if (View is not null && IsAdded)
                {
                    Render();
                }
            });
        }
    }

    private void Render()
    {
        var state = _vm.State;
        IReadOnlyList<ModelProviderGroupView> groups = state.ModelGroups ?? [];
        var rows = BuildRows(groups, state.CurrentModel);
        _adapter.Submit(rows);
        _empty.Visibility = rows.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
        _list.Visibility = rows.Count == 0 ? ViewStates.Gone : ViewStates.Visible;
        RenderEffortBar(state, groups);
    }

    private List<PickerRow> BuildRows(
        IReadOnlyList<ModelProviderGroupView> groups,
        ModelSelectionSpec? current)
    {
        var rows = new List<PickerRow>();
        var currentGroup = current is null
            ? null
            : groups.FirstOrDefault(g => string.Equals(g.Id, current.Provider, StringComparison.Ordinal));
        var currentModel = currentGroup is null || current is null
            ? null
            : currentGroup.Models.FirstOrDefault(m => string.Equals(m.Id, current.Model, StringComparison.Ordinal));

        if (currentGroup is not null && currentModel is not null)
        {
            rows.Add(new ProviderHeaderRow(GetString(Resource.String.chat_model_current_section) ?? string.Empty));
            rows.Add(new ModelRow(currentGroup, currentModel, IsCurrent: true, current!.ReasoningEffort));
        }

        foreach (var group in groups)
        {
            var models = group.Models
                .Where(m => currentGroup is null
                    || currentModel is null
                    || !(string.Equals(group.Id, currentGroup.Id, StringComparison.Ordinal)
                        && string.Equals(m.Id, currentModel.Id, StringComparison.Ordinal)))
                .ToArray();
            if (models.Length == 0)
            {
                continue;
            }

            var title = string.IsNullOrWhiteSpace(group.Name) ? group.Id : group.Name;
            rows.Add(new ProviderHeaderRow(title));
            foreach (var model in models)
            {
                rows.Add(new ModelRow(group, model, IsCurrent: false, model.DefaultReasoningEffort));
            }
        }

        return rows;
    }

    private void RenderEffortBar(ChatUiState state, IReadOnlyList<ModelProviderGroupView> groups)
    {
        _effortChips.RemoveAllViews();
        var current = state.CurrentModel;
        var group = current is null
            ? null
            : groups.FirstOrDefault(g => string.Equals(g.Id, current.Provider, StringComparison.Ordinal));
        var model = group is null || current is null
            ? null
            : group.Models.FirstOrDefault(m => string.Equals(m.Id, current.Model, StringComparison.Ordinal));
        if (group is null || model is null || model.ReasoningEfforts.Count == 0)
        {
            _effortSection.Visibility = ViewStates.Gone;
            return;
        }

        _effortSection.Visibility = ViewStates.Visible;
        var activeEffort = current!.ReasoningEffort ?? model.DefaultReasoningEffort;
        var inflater = LayoutInflater.From(RequireContext())!;
        foreach (var effort in model.ReasoningEfforts)
        {
            var chip = (Chip)inflater.Inflate(Resource.Layout.item_model_effort_chip, _effortChips, false)!;
            chip.Id = View.GenerateViewId();
            chip.Text = effort.Name;
            chip.Checked = string.Equals(activeEffort, effort.Id, StringComparison.OrdinalIgnoreCase);
            var selection = new ModelSelectionSpec(group.Id, model.Id, effort.Id);
            chip.Click += (_, _) => _ = ApplySelectionAsync(selection);
            _effortChips.AddView(chip);
        }
    }

    private async Task ApplySelectionAsync(ModelSelectionSpec selection)
    {
        if (_isApplying)
        {
            return;
        }

        _isApplying = true;
        Context? context = Context;
        try
        {
            await _vm.SelectModelAsync(selection).ConfigureAwait(false);
            var state = _vm.State;
            Activity?.RunOnUiThread(() =>
            {
                _isApplying = false;
                if (state.FailureCode is { } failureCode)
                {
                    if (View is { } root)
                    {
                        Snackbar.Make(
                            root,
                            Rows.Fmt(RequireContext(), Resource.String.chats_inline_action_failed, failureCode),
                            Snackbar.LengthLong).Show();
                    }
                    return;
                }

                if (context is not null)
                {
                    Toast.MakeText(
                        context,
                        Rows.Fmt(context, Resource.String.chat_model_switched, state.ModelLabel ?? selection.Model),
                        ToastLength.Short)?.Show();
                }
                DismissAllowingStateLoss();
            });
        }
        catch (System.OperationCanceledException)
        {
            Activity?.RunOnUiThread(() => _isApplying = false);
        }
    }

    private abstract record PickerRow;

    private sealed record ProviderHeaderRow(string Title) : PickerRow;

    private sealed record ModelRow(
        ModelProviderGroupView Group,
        ModelInfoView Model,
        bool IsCurrent,
        string? ReasoningEffort) : PickerRow;

    private sealed class PickerAdapter(Action<ModelSelectionSpec> onSelected) : RecyclerView.Adapter
    {
        private IReadOnlyList<PickerRow> _rows = [];

        public void Submit(IReadOnlyList<PickerRow> rows)
        {
            _rows = rows;
            NotifyDataSetChanged();
        }

        public override int ItemCount => _rows.Count;

        public override int GetItemViewType(int position) =>
            _rows[position] is ProviderHeaderRow ? ViewTypeProviderHeader : ViewTypeModel;

        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
        {
            var inflater = LayoutInflater.From(parent.Context)!;
            if (viewType == ViewTypeProviderHeader)
            {
                return new HeaderHolder((TextView)inflater.Inflate(Resource.Layout.item_model_provider_header, parent, false)!);
            }
            return new ModelHolder(inflater.Inflate(Resource.Layout.item_model_picker_row, parent, false)!, onSelected);
        }

        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
        {
            switch (_rows[position])
            {
                case ProviderHeaderRow header:
                    ((HeaderHolder)holder).Bind(header.Title);
                    break;
                case ModelRow row:
                    ((ModelHolder)holder).Bind(row);
                    break;
            }
        }

        private sealed class HeaderHolder(TextView title) : RecyclerView.ViewHolder(title)
        {
            public void Bind(string text) => title.Text = text;
        }

        private sealed class ModelHolder : RecyclerView.ViewHolder
        {
            private readonly TextView _name;
            private readonly View _check;
            private readonly Action<ModelSelectionSpec> _onSelected;
            private ModelSelectionSpec? _spec;

            public ModelHolder(View itemView, Action<ModelSelectionSpec> onSelected) : base(itemView)
            {
                _onSelected = onSelected;
                _name = itemView.FindViewById<TextView>(Resource.Id.model_row_name)!;
                _check = itemView.FindViewById(Resource.Id.model_row_check)!;
                itemView.Click += (_, _) =>
                {
                    if (_spec is { } spec)
                    {
                        _onSelected(spec);
                    }
                };
            }

            public void Bind(ModelRow row)
            {
                _name.Text = string.IsNullOrWhiteSpace(row.Model.Name) ? row.Model.Id : row.Model.Name;
                _name.SetTextColor(new Android.Graphics.Color(Rows.Color(
                    _name,
                    row.IsCurrent ? Resource.Attribute.colorPrimary : Resource.Attribute.colorOnSurface)));
                _check.Visibility = row.IsCurrent ? ViewStates.Visible : ViewStates.Gone;
                _spec = new ModelSelectionSpec(row.Group.Id, row.Model.Id, row.ReasoningEffort);
            }
        }
    }
}
