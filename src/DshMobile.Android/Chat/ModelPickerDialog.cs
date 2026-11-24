using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using DshMobile.Agent.Domain;
using DshMobile.App.Ui;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Button;
using Google.Android.Material.Card;
using Google.Android.Material.Chip;

namespace DshMobile.App.Chat;

/// <summary>
/// PR-05 / UI Slice 2 模型与推理档位底部选择器（<c>BottomSheetDialog</c> + 全 XML <c>sheet_model_picker.xml</c> / <c>item_model_picker_row.xml</c> / <c>item_model_effort_chip.xml</c>）：
/// 全宽弹窗支持拖拽手势、Provider 分组标识、右上角直达凭据配置，并保留 Filter Chip 单选高亮。
/// </summary>
internal static class ModelPickerDialog
{
    public static void Show(
        Context ctx,
        IReadOnlyList<ModelProviderGroupView>? groups,
        ModelSelectionSpec? current,
        Action<ModelSelectionSpec> onSelected,
        Action onOpenCredentials)
    {
        var content = LayoutInflater.From(ctx)!.Inflate(Resource.Layout.sheet_model_picker, null)!;
        Edges.Pad(content, top: false, bottom: true);

        var emptyText = content.FindViewById<TextView>(Resource.Id.model_picker_empty)!;
        var list = content.FindViewById<RecyclerView>(Resource.Id.model_picker_list)!;
        var credsBtn = content.FindViewById<MaterialButton>(Resource.Id.model_picker_creds_btn)!;

        var rows = new List<(ModelProviderGroupView Group, ModelInfoView Model)>();
        if (groups is not null)
        {
            foreach (var g in groups)
            {
                foreach (var m in g.Models)
                {
                    rows.Add((g, m));
                }
            }
        }

        var sheet = new BottomSheetDialog(ctx);
        sheet.SetContentView(content);
        sheet.Behavior.SkipCollapsed = true;
        sheet.Behavior.State = BottomSheetBehavior.StateExpanded;

        credsBtn.Click += (_, _) =>
        {
            sheet.Dismiss();
            onOpenCredentials();
        };

        if (rows.Count == 0)
        {
            emptyText.Visibility = ViewStates.Visible;
            list.Visibility = ViewStates.Gone;
        }
        else
        {
            emptyText.Visibility = ViewStates.Gone;
            list.Visibility = ViewStates.Visible;
            list.SetLayoutManager(new LinearLayoutManager(ctx));
            list.SetAdapter(new Adapter(rows, current, spec =>
            {
                sheet.Dismiss();
                onSelected(spec);
            }));
        }

        sheet.Show();
    }

    private sealed class Adapter(
        IReadOnlyList<(ModelProviderGroupView Group, ModelInfoView Model)> rows,
        ModelSelectionSpec? current,
        Action<ModelSelectionSpec> onPick) : RecyclerView.Adapter
    {
        public override int ItemCount => rows.Count;

        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
        {
            var view = LayoutInflater.From(parent.Context)!.Inflate(Resource.Layout.item_model_picker_row, parent, false)!;
            return new Holder(view, onPick);
        }

        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
        {
            var (group, model) = rows[position];
            var isSelected = current is not null &&
                string.Equals(current.Provider, group.Id, StringComparison.Ordinal) &&
                string.Equals(current.Model, model.Id, StringComparison.Ordinal);
            ((Holder)holder).Bind(group, model, isSelected, current?.ReasoningEffort);
        }

        private sealed class Holder(View itemView, Action<ModelSelectionSpec> onPick) : RecyclerView.ViewHolder(itemView)
        {
            private readonly MaterialCardView _card = itemView.FindViewById<MaterialCardView>(Resource.Id.model_row_card)!;
            private readonly TextView _provider = itemView.FindViewById<TextView>(Resource.Id.model_row_provider)!;
            private readonly TextView _name = itemView.FindViewById<TextView>(Resource.Id.model_row_name)!;
            private readonly TextView _desc = itemView.FindViewById<TextView>(Resource.Id.model_row_desc)!;
            private readonly View _check = itemView.FindViewById(Resource.Id.model_row_check)!;
            private readonly ChipGroup _efforts = itemView.FindViewById<ChipGroup>(Resource.Id.model_row_efforts)!;
            private ModelSelectionSpec? _boundSpec;

            public void Bind(
                ModelProviderGroupView group,
                ModelInfoView model,
                bool isSelected,
                string? currentEffort)
            {
                var ctx = ItemView.Context!;
                _provider.Text = group.Name;
                _name.Text = string.IsNullOrWhiteSpace(model.Name) ? model.Id : model.Name;
                if (string.IsNullOrWhiteSpace(model.Description))
                {
                    _desc.Visibility = ViewStates.Gone;
                }
                else
                {
                    _desc.Visibility = ViewStates.Visible;
                    _desc.Text = model.Description;
                }
                _check.Visibility = isSelected ? ViewStates.Visible : ViewStates.Gone;
                _card.StrokeWidth = Rows.Dp(ctx, isSelected ? 1.5f : 1f);
                _card.StrokeColor = Rows.Color(_card, isSelected ? Resource.Attribute.colorPrimary : Resource.Attribute.colorOutlineVariant);

                ItemView.Click -= OnItemClick;
                _boundSpec = new ModelSelectionSpec(group.Id, model.Id, model.DefaultReasoningEffort);
                ItemView.Click += OnItemClick;

                _efforts.RemoveAllViews();
                if (model.ReasoningEfforts.Count == 0)
                {
                    _efforts.Visibility = ViewStates.Gone;
                    return;
                }

                _efforts.Visibility = ViewStates.Visible;
                var inflater = LayoutInflater.From(ctx)!;
                var activeEffort = currentEffort ?? model.DefaultReasoningEffort;
                foreach (var effort in model.ReasoningEfforts)
                {
                    var chip = (Chip)inflater.Inflate(Resource.Layout.item_model_effort_chip, _efforts, false)!;
                    chip.Id = View.GenerateViewId();
                    chip.Text = effort.Name;
                    chip.Checked = isSelected && string.Equals(activeEffort, effort.Id, StringComparison.OrdinalIgnoreCase);
                    chip.Click += (_, _) => onPick(new ModelSelectionSpec(group.Id, model.Id, effort.Id));
                    _efforts.AddView(chip);
                }
            }

            private void OnItemClick(object? sender, EventArgs e)
            {
                if (_boundSpec is { } spec)
                {
                    onPick(spec);
                }
            }
        }
    }
}
