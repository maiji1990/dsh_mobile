using Android.Content;
using Android.Graphics;
using Android.Util;
using Android.Views;
using Android.Widget;
using DshMobile.Core.Files;

namespace DshMobile.App.Files;

/// <summary>
/// 原生 Diff 单行视图绑定器（N-06-02）：
/// 绑定由 XML 布局（<c>item_git_diff_line.xml</c>）加载的旧/新行号、符号标记与等宽正文，
/// 仅消费后台已预解析的 <see cref="GitDiffLine"/>，主线程零解析。
/// </summary>
internal sealed class DiffLineView(View itemView)
{
    private readonly View _root = itemView.FindViewById(Resource.Id.diff_line_root)!;
    private readonly TextView _oldNo = itemView.FindViewById<TextView>(Resource.Id.diff_line_old_no)!;
    private readonly TextView _newNo = itemView.FindViewById<TextView>(Resource.Id.diff_line_new_no)!;
    private readonly TextView _marker = itemView.FindViewById<TextView>(Resource.Id.diff_line_marker)!;
    private readonly TextView _content = itemView.FindViewById<TextView>(Resource.Id.diff_line_content)!;

    public void Bind(GitDiffLine line)
    {
        var ctx = _root.Context!;
        _oldNo.Text = line.OldLineNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        _newNo.Text = line.NewLineNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        _marker.Text = line.Marker;
        _content.Text = line.Content;

        switch (line.Kind)
        {
            case GitDiffLineKind.Added:
            {
                var bg = new Color(ctx.GetColor(Resource.Color.dshm_diff_add_bg));
                var fg = new Color(ctx.GetColor(Resource.Color.dshm_diff_add_fg));
                ApplyColors(bg, fg);
                break;
            }
            case GitDiffLineKind.Removed:
            {
                var bg = new Color(ctx.GetColor(Resource.Color.dshm_diff_del_bg));
                var fg = new Color(ctx.GetColor(Resource.Color.dshm_diff_del_fg));
                ApplyColors(bg, fg);
                break;
            }
            case GitDiffLineKind.HunkHeader:
            {
                var bg = new Color(ctx.GetColor(Resource.Color.dshm_diff_hunk_bg));
                var fg = new Color(ctx.GetColor(Resource.Color.dshm_diff_hunk_fg));
                ApplyColors(bg, fg);
                break;
            }
            case GitDiffLineKind.TruncationNotice:
            {
                var bg = ResolveAttrColor(ctx, Resource.Attribute.colorTertiaryContainer);
                var fg = ResolveAttrColor(ctx, Resource.Attribute.colorOnTertiaryContainer);
                ApplyColors(bg, fg);
                break;
            }
            case GitDiffLineKind.Meta:
            {
                var fg = ResolveAttrColor(ctx, Resource.Attribute.colorOnSurfaceVariant);
                ApplyColors(Color.Transparent, fg);
                break;
            }
            default:
            {
                var fg = ResolveAttrColor(ctx, Resource.Attribute.colorOnSurface);
                ApplyColors(Color.Transparent, fg);
                break;
            }
        }
    }

    private void ApplyColors(Color bg, Color fg)
    {
        _root.SetBackgroundColor(bg);
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
