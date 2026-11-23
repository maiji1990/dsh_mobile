using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using DshMobile.Agent.Domain;
using Google.Android.Material.Chip;
using Google.Android.Material.Dialog;

namespace DshMobile.App.Chat;

/// <summary>
/// PR-05 模型与 Reasoning Effort 切换弹窗（N-05-02）：
/// 全 XML 布局（<c>dialog_model_picker.xml</c> / <c>item_model_picker_row.xml</c>），按能力门控且支持凭据缺失跳转。
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
        var content = LayoutInflater.From(ctx)!.Inflate(Resource.Layout.dialog_model_picker, null)!;
        var emptyText = content.FindViewById<TextView>(Resource.Id.model_picker_empty)!;
        var list = content.FindViewById<RecyclerView>(Resource.Id.model_picker_list)!;

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

        AndroidX.AppCompat.App.AlertDialog? dialog = null;
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
                dialog?.Dismiss();
                onSelected(spec);
            }));
        }

        var builder = new MaterialAlertDialogBuilder(ctx)
            .SetTitle(Resource.String.chat_model_picker_title)!
            .SetView(content)!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!;

        if (rows.Count == 0)
        {
            builder.SetPositiveButton(Resource.String.chat_model_picker_open_creds, (_, _) => onOpenCredentials());
        }

        dialog = builder.Create();
        dialog.Show();
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
            private readonly TextView _provider = itemView.FindViewById<TextView>(Resource.Id.model_row_provider)!;
            private readonly TextView _name = itemView.FindViewById<TextView>(Resource.Id.model_row_name)!;
            private readonly TextView _desc = itemView.FindViewById<TextView>(Resource.Id.model_row_desc)!;
            private readonly View _check = itemView.FindViewById(Resource.Id.model_row_check)!;
            private readonly ChipGroup _efforts = itemView.FindViewById<ChipGroup>(Resource.Id.model_row_efforts)!;

            public void Bind(
                ModelProviderGroupView group,
                ModelInfoView model,
                bool isSelected,
                string? currentEffort)
            {
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
                var inflater = LayoutInflater.From(ItemView.Context)!;
                foreach (var effort in model.ReasoningEfforts)
                {
                    var chip = (Chip)inflater.Inflate(Resource.Layout.item_chat_slash_chip, _efforts, false)!;
                    chip.Text = effort.Name;
                    chip.Checked = isSelected && string.Equals(currentEffort ?? model.DefaultReasoningEffort, effort.Id, StringComparison.Ordinal);
                    chip.Click += (_, _) => onPick(new ModelSelectionSpec(group.Id, model.Id, effort.Id));
                    _efforts.AddView(chip);
                }
            }

            private ModelSelectionSpec? _boundSpec;
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
