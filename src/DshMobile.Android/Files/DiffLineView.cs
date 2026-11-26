using Android.Content;
using Android.Graphics;
using Android.Util;
using Android.Views;
using Android.Widget;
using DshMobile.Core.Files;

namespace DshMobile.App.Files;

/// <summary>
/// UI-S5 原生 Diff 单行视图绑定器：消费核心侧已清洗的 <see cref="GitDiffLine"/>，主线程零解析。
/// 新增/删除行使用 XML 声明的柔和底色 + 左侧 3dp 标记条，行号为 tnum 等宽右对齐；
/// Hunk 头由独立折叠条承载，因此本视图不再输出任何 <c>@@</c> 标记，杜绝 <c>@@@@</c> 拼接。
/// </summary>
internal sealed class DiffLineView(View itemView)
{
    private readonly View _root = itemView.FindViewById(Resource.Id.diff_line_root)!;
    private readonly TextView _oldNo = itemView.FindViewById<TextView>(Resource.Id.diff_line_old_no)!;
    private readonly TextView _newNo = itemView.FindViewById<TextView>(Resource.Id.diff_line_new_no)!;
    private readonly TextView _marker = itemView.FindViewById<TextView>(Resource.Id.diff_line_marker)!;
    private readonly TextView _content = itemView.FindViewById<TextView>(Resource.Id.diff_line_content)!;

    public void Bind(GitDiffLine line, bool wrap)
    {
        _oldNo.Text = line.OldLineNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        _newNo.Text = line.NewLineNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        _marker.Text = line.Marker;
        _content.Text = line.Content;
        ApplyWrap(wrap);

        switch (line.Kind)
        {
            case GitDiffLineKind.Added:
            {
                _root.SetBackgroundResource(Resource.Drawable.bg_diff_line_added);
                ApplyForeground(new Color(_root.Context!.GetColor(Resource.Color.dshm_diff_add_fg)));
                break;
            }
            case GitDiffLineKind.Removed:
            {
                _root.SetBackgroundResource(Resource.Drawable.bg_diff_line_removed);
                ApplyForeground(new Color(_root.Context!.GetColor(Resource.Color.dshm_diff_del_fg)));
                break;
            }
            case GitDiffLineKind.HunkHeader:
            {
                // 正常路径下 Hunk 头由 DiffReviewAdapter 的折叠条渲染；保留分支以防外部直接绑定。
                _root.SetBackgroundColor(new Color(_root.Context!.GetColor(Resource.Color.dshm_diff_hunk_bg)));
                ApplyForeground(new Color(_root.Context!.GetColor(Resource.Color.dshm_diff_hunk_fg)));
                break;
            }
            case GitDiffLineKind.TruncationNotice:
            {
                var ctx = _root.Context!;
                _root.SetBackgroundColor(ResolveAttrColor(ctx, Resource.Attribute.colorTertiaryContainer));
                ApplyForeground(ResolveAttrColor(ctx, Resource.Attribute.colorOnTertiaryContainer));
                break;
            }
            case GitDiffLineKind.Meta:
            {
                _root.SetBackgroundColor(Color.Transparent);
                ApplyForeground(ResolveAttrColor(_root.Context!, Resource.Attribute.colorOnSurfaceVariant));
                break;
            }
            default:
            {
                _root.SetBackgroundColor(Color.Transparent);
                ApplyForeground(ResolveAttrColor(_root.Context!, Resource.Attribute.colorOnSurface));
                break;
            }
        }
    }

    /// <summary>自动换行开关：关闭时单行 + 横向滚动，开启时按视口宽度折行。</summary>
    private void ApplyWrap(bool wrap)
    {
        _content.SetHorizontallyScrolling(!wrap);
        _content.SetMaxLines(wrap ? int.MaxValue : 1);

        // 内容位于横向 LinearLayout 中：折行时必须用 Width=0 + Weight=1 占用行号/标记条之外的剩余宽度；
        // 若继续用 MatchParent，会把右侧 84dp 的固定区域挤出屏幕。
        if (_content.LayoutParameters is LinearLayout.LayoutParams lp)
        {
            var width = wrap ? 0 : ViewGroup.LayoutParams.WrapContent;
            var weight = wrap ? 1f : 0f;
            if (lp.Width != width || lp.Weight != weight)
            {
                lp.Width = width;
                lp.Weight = weight;
                _content.LayoutParameters = lp;
            }
        }
    }

    private void ApplyForeground(Color fg)
    {
        _marker.SetTextColor(fg);
        _content.SetTextColor(fg);
    }

    private static Color ResolveAttrColor(Context ctx, int attrId)
    {
        var tv = new TypedValue();
        return ctx.Theme?.ResolveAttribute(attrId, tv, true) == true
            ? new Color(tv.Data)
            : new Color(ctx.GetColor(Android.Resource.Color.Black));
    }
}
