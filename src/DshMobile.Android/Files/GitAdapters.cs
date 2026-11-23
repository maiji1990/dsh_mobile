using Android.Content;
using Android.Graphics;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using DshMobile.App.Ui;
using DshMobile.Core.Files;

namespace DshMobile.App.Files;

/// <summary>Git 变更文件列表 RecyclerView 适配器（基于 <c>item_git_change.xml</c> 与 <see cref="DiffUtil"/>）。</summary>
internal sealed class GitChangesAdapter(Action<GitChangeEntry> onSelect) : RecyclerView.Adapter
{
    private List<GitChangeEntry> _items = [];

    public override int ItemCount => _items.Count;

    public void Submit(IReadOnlyList<GitChangeEntry> next)
    {
        var old = _items;
        var nextList = next.ToList();
        var diff = DiffUtil.CalculateDiff(new ChangeDiffCallback(old, nextList));
        _items = nextList;
        diff.DispatchUpdatesTo(this);
    }

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var view = LayoutInflater.From(parent.Context)!.Inflate(Resource.Layout.item_git_change, parent, false)!;
        var holder = new ChangeViewHolder(view);
        view.Click += (_, _) =>
        {
            if (holder.Entry is { } entry)
            {
                onSelect(entry);
            }
        };
        return holder;
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position) =>
        ((ChangeViewHolder)holder).Bind(_items[position]);

    private sealed class ChangeViewHolder(View itemView) : RecyclerView.ViewHolder(itemView)
    {
        private readonly TextView _badge = itemView.FindViewById<TextView>(Resource.Id.git_change_badge)!;
        private readonly TextView _path = itemView.FindViewById<TextView>(Resource.Id.git_change_path)!;
        private readonly TextView _meta = itemView.FindViewById<TextView>(Resource.Id.git_change_meta)!;

        public GitChangeEntry? Entry { get; private set; }

        public void Bind(GitChangeEntry entry)
        {
            Entry = entry;
            var ctx = ItemView.Context!;
            _path.Text = entry.RelativePath;
            _badge.Text = entry.Kind switch
            {
                GitChangeKind.Added => "A",
                GitChangeKind.Deleted => "D",
                GitChangeKind.Renamed => "R",
                GitChangeKind.Copied => "C",
                GitChangeKind.Untracked => "?",
                GitChangeKind.Unmerged => "U",
                GitChangeKind.Modified => "M",
                _ => entry.PorcelainCode.Trim(),
            };

            var (bg, fg) = entry.Kind switch
            {
                GitChangeKind.Added or GitChangeKind.Untracked => (
                    new Color(ctx.GetColor(Resource.Color.dshm_diff_add_bg)),
                    new Color(ctx.GetColor(Resource.Color.dshm_diff_add_fg))),
                GitChangeKind.Deleted or GitChangeKind.Unmerged => (
                    new Color(ctx.GetColor(Resource.Color.dshm_diff_del_bg)),
                    new Color(ctx.GetColor(Resource.Color.dshm_diff_del_fg))),
                _ => (
                    ResolveAttrColor(ctx, Resource.Attribute.colorSecondaryContainer),
                    ResolveAttrColor(ctx, Resource.Attribute.colorOnSecondaryContainer)),
            };
            _badge.SetBackgroundColor(bg);
            _badge.SetTextColor(fg);

            var parts = new List<string>(3);
            if (entry.Kind == GitChangeKind.Untracked)
            {
                parts.Add(ctx.GetString(Resource.String.git_change_untracked));
            }
            else
            {
                if (entry.IsStaged)
                {
                    parts.Add(ctx.GetString(Resource.String.git_change_staged));
                }
                if (entry.IsUnstaged)
                {
                    parts.Add(ctx.GetString(Resource.String.git_change_unstaged));
                }
            }
            if (!string.IsNullOrWhiteSpace(entry.PreviousRelativePath))
            {
                parts.Add(Rows.Fmt(ctx, Resource.String.git_change_renamed_from, entry.PreviousRelativePath!));
            }
            _meta.Text = string.Join(" · ", parts);
        }

        private static Color ResolveAttrColor(Context ctx, int attrId)
        {
            var tv = new TypedValue();
            return ctx.Theme?.ResolveAttribute(attrId, tv, true) == true
                ? new Color(tv.Data)
                : new Color(ctx.GetColor(Android.Resource.Color.Black));
        }
    }

    private sealed class ChangeDiffCallback(
        IReadOnlyList<GitChangeEntry> oldItems,
        IReadOnlyList<GitChangeEntry> newItems) : DiffUtil.Callback
    {
        public override int OldListSize => oldItems.Count;
        public override int NewListSize => newItems.Count;

        public override bool AreItemsTheSame(int oldItemPosition, int newItemPosition) =>
            string.Equals(oldItems[oldItemPosition].RelativePath, newItems[newItemPosition].RelativePath, StringComparison.Ordinal);

        public override bool AreContentsTheSame(int oldItemPosition, int newItemPosition) =>
            oldItems[oldItemPosition] == newItems[newItemPosition];
    }
}

/// <summary>原生 Diff 行 RecyclerView 适配器（复用 <see cref="DiffLineView"/> 绑定预解析行）。</summary>
internal sealed class GitDiffAdapter : RecyclerView.Adapter
{
    private List<GitDiffLine> _lines = [];

    public override int ItemCount => _lines.Count;

    public void Submit(IReadOnlyList<GitDiffLine> next)
    {
        var old = _lines;
        var nextList = next.ToList();
        if (old.Count > 2000 || nextList.Count > 2000)
        {
            _lines = nextList;
            NotifyDataSetChanged();
            return;
        }
        var diff = DiffUtil.CalculateDiff(new LineDiffCallback(old, nextList));
        _lines = nextList;
        diff.DispatchUpdatesTo(this);
    }

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var view = LayoutInflater.From(parent.Context)!.Inflate(Resource.Layout.item_git_diff_line, parent, false)!;
        return new LineViewHolder(view);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position) =>
        ((LineViewHolder)holder).DiffLine.Bind(_lines[position]);

    private sealed class LineViewHolder(View itemView) : RecyclerView.ViewHolder(itemView)
    {
        public DiffLineView DiffLine { get; } = new(itemView);
    }

    private sealed class LineDiffCallback(
        IReadOnlyList<GitDiffLine> oldItems,
        IReadOnlyList<GitDiffLine> newItems) : DiffUtil.Callback
    {
        public override int OldListSize => oldItems.Count;
        public override int NewListSize => newItems.Count;

        public override bool AreItemsTheSame(int oldItemPosition, int newItemPosition) =>
            oldItemPosition == newItemPosition && oldItems[oldItemPosition].Kind == newItems[newItemPosition].Kind;

        public override bool AreContentsTheSame(int oldItemPosition, int newItemPosition) =>
            oldItems[oldItemPosition] == newItems[newItemPosition];
    }
}
