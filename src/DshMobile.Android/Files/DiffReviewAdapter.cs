using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using DshMobile.Core.Files;

namespace DshMobile.App.Files;

/// <summary>
/// UI-S5 全屏 Diff 审查列表适配器：
/// Hunk 头渲染为可点击的折叠/分隔条（点击收起或展开该段正文），正文行复用 <see cref="DiffLineView"/>；
/// 自动换行开关只切换列宽与文本折行，不重新解析 Diff。
/// </summary>
internal sealed class DiffReviewAdapter : RecyclerView.Adapter
{
    private const int TypeLine = 0;
    private const int TypeHunk = 1;

    private readonly List<GitDiffLine> _all = [];
    private readonly List<Row> _rows = [];
    private readonly HashSet<int> _collapsed = [];
    private int _viewportWidth;

    /// <summary>自动换行：true 时按视口宽度折行，false 时单行 + 整表横向滚动。</summary>
    public bool Wrap { get; set; }

    public override int ItemCount => _rows.Count;

    /// <summary>
    /// 记录横向滚动视口的实测宽度。非换行模式下短行以 minWidth 铺满该宽度，
    /// 长行仍按内容宽度继续横向滚动。
    /// </summary>
    public void SetViewportWidth(int width)
    {
        if (_viewportWidth == width)
        {
            return;
        }
        _viewportWidth = width;
        NotifyDataSetChanged();
    }

    /// <summary>提交核心侧已清洗的 Diff 行（含 Hunk 头规范化结果）。</summary>
    public void Submit(IReadOnlyList<GitDiffLine> lines)
    {
        _all.Clear();
        _all.AddRange(lines);
        _collapsed.Clear();
        Rebuild();
    }

    public override int GetItemViewType(int position) =>
        _rows[position].Line.Kind == GitDiffLineKind.HunkHeader ? TypeHunk : TypeLine;

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var inflater = LayoutInflater.From(parent.Context)!;
        if (viewType == TypeHunk)
        {
            var hunkView = inflater.Inflate(Resource.Layout.item_diff_review_hunk, parent, false)!;
            var hunkHolder = new HunkHolder(hunkView);
            hunkView.Click += (_, _) =>
            {
                var position = hunkHolder.BindingAdapterPosition;
                if (position >= 0 && position < _rows.Count)
                {
                    ToggleHunk(_rows[position].HunkIndex);
                }
            };
            return hunkHolder;
        }
        return new LineHolder(inflater.Inflate(Resource.Layout.item_diff_review_line, parent, false)!);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        var row = _rows[position];
        switch (holder)
        {
            case LineHolder lineHolder:
                lineHolder.DiffLine.Bind(row.Line, Wrap);
                ApplyWidth(lineHolder.ItemView);
                break;
            case HunkHolder hunkHolder:
                hunkHolder.Bind(row.Line, row.Collapsed);
                ApplyWidth(hunkHolder.ItemView);
                break;
        }
    }

    private void ApplyWidth(View itemView)
    {
        var lp = itemView.LayoutParameters;
        if (lp is null)
        {
            return;
        }
        var width = Wrap ? ViewGroup.LayoutParams.MatchParent : ViewGroup.LayoutParams.WrapContent;
        if (lp.Width != width)
        {
            lp.Width = width;
            itemView.LayoutParameters = lp;
        }

        // 非换行模式：行根布局至少铺满横向视口，短行背景不再露出右侧白边；
        // 内容长于视口时仍由 wrap_content 撑开并继续横向滚动。
        var minWidth = Wrap ? 0 : _viewportWidth;
        if (itemView.MinimumWidth != minWidth)
        {
            itemView.SetMinimumWidth(minWidth);
        }
    }

    /// <summary>折叠 / 展开指定 Hunk 的正文（Hunk 头本身始终保留）。</summary>
    private void ToggleHunk(int hunkIndex)
    {
        if (!_collapsed.Remove(hunkIndex))
        {
            _collapsed.Add(hunkIndex);
        }
        Rebuild();
    }

    private void Rebuild()
    {
        _rows.Clear();
        var activeHunk = -1;
        var collapsed = false;
        for (var i = 0; i < _all.Count; i++)
        {
            var line = _all[i];
            if (line.Kind == GitDiffLineKind.HunkHeader)
            {
                activeHunk = i;
                collapsed = _collapsed.Contains(i);
                _rows.Add(new Row(line, i, collapsed));
                continue;
            }
            if (collapsed)
            {
                continue;
            }
            _rows.Add(new Row(line, activeHunk, Collapsed: false));
        }
        NotifyDataSetChanged();
    }

    /// <summary>列表行：Diff 行 + 所属 Hunk 起始下标 + Hunk 是否折叠。</summary>
    private readonly record struct Row(GitDiffLine Line, int HunkIndex, bool Collapsed);

    private sealed class LineHolder(View itemView) : RecyclerView.ViewHolder(itemView)
    {
        public DiffLineView DiffLine { get; } = new(itemView);
    }

    private sealed class HunkHolder(View itemView) : RecyclerView.ViewHolder(itemView)
    {
        private readonly TextView _title = itemView.FindViewById<TextView>(Resource.Id.diff_hunk_title)!;
        private readonly ImageView _chevron = itemView.FindViewById<ImageView>(Resource.Id.diff_hunk_chevron)!;

        public void Bind(GitDiffLine line, bool collapsed)
        {
            _title.Text = line.Content;
            // 折叠时箭头指向右侧，展开时指向下方
            _chevron.Rotation = collapsed ? -90f : 0f;
        }
    }
}
