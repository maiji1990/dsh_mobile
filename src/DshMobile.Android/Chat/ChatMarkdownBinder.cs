using Android.Content;
using Android.Graphics;
using Android.Text;
using Android.Text.Method;
using Android.Text.Style;
using Android.Util;
using Android.Views;
using Android.Widget;
using DshMobile.Agent.Rendering;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.Snackbar;

namespace DshMobile.App.Chat;

/// <summary>
/// 将纯托管 <see cref="MarkdownRenderResult"/> 绑定到 XML 模板视图（N-04-02）：
/// 段落/标题/列表/引用转为 <see cref="SpannableStringBuilder"/>，代码块/Diff/表格分别加载对应 XML 视图；
/// 外链统一弹出安全确认后交由系统浏览器打开，绝不内嵌 WebView。
/// </summary>
internal static class ChatMarkdownBinder
{
    public static void BindSegments(
        ViewGroup container,
        IReadOnlyList<MarkdownSegment> segments,
        LayoutInflater inflater)
    {
        container.RemoveAllViews();
        var ctx = container.Context!;
        foreach (var seg in segments)
        {
            switch (seg)
            {
                case MarkdownTextSegment textSeg:
                {
                    var tv = (TextView)inflater.Inflate(Resource.Layout.item_chat_md_text, container, false)!;
                    BindTextSegment(ctx, tv, textSeg);
                    container.AddView(tv);
                    break;
                }
                case MarkdownCodeSegment codeSeg:
                {
                    var card = inflater.Inflate(Resource.Layout.view_chat_code_block, container, false)!;
                    BindCodeBlock(ctx, card, codeSeg);
                    container.AddView(card);
                    break;
                }
                case MarkdownDiffSegment diffSeg:
                {
                    var card = inflater.Inflate(Resource.Layout.view_chat_diff_block, container, false)!;
                    BindDiffBlock(ctx, card, diffSeg, inflater);
                    container.AddView(card);
                    break;
                }
                case MarkdownTableSegment tableSeg:
                {
                    var card = inflater.Inflate(Resource.Layout.view_chat_table_block, container, false)!;
                    BindTableBlock(ctx, card, tableSeg, inflater);
                    container.AddView(card);
                    break;
                }
                case MarkdownThematicBreakSegment:
                {
                    var divider = inflater.Inflate(Resource.Layout.item_chat_md_divider, container, false)!;
                    container.AddView(divider);
                    break;
                }
            }
        }
    }

    private static void BindTextSegment(Context ctx, TextView tv, MarkdownTextSegment seg)
    {
        var prefix = seg.Kind switch
        {
            MarkdownSegmentKind.ListItem => new string(' ', Math.Max(0, seg.ListDepth - 1) * 4) + (seg.ListPrefix ?? "• "),
            MarkdownSegmentKind.Quote => "┃ ",
            _ => string.Empty,
        };
        var fullText = prefix + seg.PlainText;
        var builder = new SpannableStringBuilder(fullText);
        var offset = prefix.Length;

        if (seg.Kind == MarkdownSegmentKind.Heading)
        {
            builder.SetSpan(new StyleSpan(TypefaceStyle.Bold), 0, fullText.Length, SpanTypes.ExclusiveExclusive);
            var scale = seg.HeadingLevel switch
            {
                1 => 1.28f,
                2 => 1.18f,
                3 => 1.08f,
                _ => 1.02f,
            };
            builder.SetSpan(new RelativeSizeSpan(scale), 0, fullText.Length, SpanTypes.ExclusiveExclusive);
        }
        else if (seg.Kind == MarkdownSegmentKind.Quote)
        {
            builder.SetSpan(new ForegroundColorSpan(ResolveAttrColor(ctx, Resource.Attribute.colorOnSurfaceVariant)), 0, fullText.Length, SpanTypes.ExclusiveExclusive);
        }

        var codeBg = ResolveAttrColor(ctx, Resource.Attribute.colorSurfaceContainerHigh);
        var linkColor = ResolveAttrColor(ctx, Resource.Attribute.colorPrimary);

        foreach (var span in seg.Spans)
        {
            var start = offset + span.Start;
            var end = start + span.Length;
            if (start < 0 || end > fullText.Length || start >= end)
            {
                continue;
            }
            switch (span.Kind)
            {
                case MarkdownSpanKind.Strong:
                    builder.SetSpan(new StyleSpan(TypefaceStyle.Bold), start, end, SpanTypes.ExclusiveExclusive);
                    break;
                case MarkdownSpanKind.Emphasis:
                    builder.SetSpan(new StyleSpan(TypefaceStyle.Italic), start, end, SpanTypes.ExclusiveExclusive);
                    break;
                case MarkdownSpanKind.Strikethrough:
                    builder.SetSpan(new StrikethroughSpan(), start, end, SpanTypes.ExclusiveExclusive);
                    break;
                case MarkdownSpanKind.InlineCode:
                    builder.SetSpan(new TypefaceSpan("monospace"), start, end, SpanTypes.ExclusiveExclusive);
                    builder.SetSpan(new BackgroundColorSpan(codeBg), start, end, SpanTypes.ExclusiveExclusive);
                    break;
                case MarkdownSpanKind.Link when !string.IsNullOrWhiteSpace(span.Url):
                    builder.SetSpan(new ExternalUrlSpan(ctx, span.Url!, linkColor), start, end, SpanTypes.ExclusiveExclusive);
                    break;
            }
        }

        tv.SetText(builder, TextView.BufferType.Spannable);
        if (seg.Spans.Any(s => s.Kind == MarkdownSpanKind.Link))
        {
            tv.MovementMethod = LinkMovementMethod.Instance;
        }
    }

    private static void BindCodeBlock(Context ctx, View card, MarkdownCodeSegment seg)
    {
        var langView = card.FindViewById<TextView>(Resource.Id.code_block_lang)!;
        var copyBtn = card.FindViewById<MaterialButton>(Resource.Id.code_block_copy)!;
        var bodyView = card.FindViewById<TextView>(Resource.Id.code_block_body)!;

        langView.Text = string.IsNullOrWhiteSpace(seg.Language) ? "text" : seg.Language;
        bodyView.Text = seg.Code;
        copyBtn.Click += (_, _) =>
        {
            var clipboard = (global::Android.Content.ClipboardManager?)ctx.GetSystemService(Context.ClipboardService);
            clipboard?.PrimaryClip = ClipData.NewPlainText("code", seg.Code);
            Snackbar.Make(card, Resource.String.chat_code_copied, Snackbar.LengthShort).Show();
        };
    }

    private static void BindDiffBlock(Context ctx, View card, MarkdownDiffSegment seg, LayoutInflater inflater)
    {
        var linesContainer = card.FindViewById<LinearLayout>(Resource.Id.diff_block_lines)!;
        linesContainer.RemoveAllViews();
        foreach (var line in seg.Lines)
        {
            var tv = (TextView)inflater.Inflate(Resource.Layout.item_chat_diff_line, linesContainer, false)!;
            tv.Text = line.Text;
            switch (line.LineKind)
            {
                case DiffLineKind.Added:
                    tv.SetBackgroundColor(new Color(ctx.GetColor(Resource.Color.dshm_diff_add_bg)));
                    tv.SetTextColor(new Color(ctx.GetColor(Resource.Color.dshm_diff_add_fg)));
                    break;
                case DiffLineKind.Removed:
                    tv.SetBackgroundColor(new Color(ctx.GetColor(Resource.Color.dshm_diff_del_bg)));
                    tv.SetTextColor(new Color(ctx.GetColor(Resource.Color.dshm_diff_del_fg)));
                    break;
                case DiffLineKind.HunkHeader:
                    tv.SetBackgroundColor(new Color(ctx.GetColor(Resource.Color.dshm_diff_hunk_bg)));
                    tv.SetTextColor(new Color(ctx.GetColor(Resource.Color.dshm_diff_hunk_fg)));
                    break;
                default:
                    tv.SetTextColor(ResolveAttrColor(ctx, Resource.Attribute.colorOnSurface));
                    break;
            }
            linesContainer.AddView(tv);
        }
    }

    private static void BindTableBlock(Context ctx, View card, MarkdownTableSegment seg, LayoutInflater inflater)
    {
        var grid = card.FindViewById<TableLayout>(Resource.Id.table_block_grid)!;
        grid.RemoveAllViews();

        if (seg.Headers.Count > 0)
        {
            var headerRow = (TableRow)inflater.Inflate(Resource.Layout.item_chat_table_row, grid, false)!;
            foreach (var h in seg.Headers)
            {
                var cell = (TextView)inflater.Inflate(Resource.Layout.item_chat_table_cell, headerRow, false)!;
                cell.Text = h;
                cell.SetTypeface(cell.Typeface, TypefaceStyle.Bold);
                cell.SetBackgroundColor(ResolveAttrColor(ctx, Resource.Attribute.colorSurfaceContainerHigh));
                headerRow.AddView(cell);
            }
            grid.AddView(headerRow);
        }

        foreach (var row in seg.Rows)
        {
            var tr = (TableRow)inflater.Inflate(Resource.Layout.item_chat_table_row, grid, false)!;
            foreach (var cellText in row)
            {
                var cell = (TextView)inflater.Inflate(Resource.Layout.item_chat_table_cell, tr, false)!;
                cell.Text = cellText;
                tr.AddView(cell);
            }
            grid.AddView(tr);
        }
    }

    private static Color ResolveAttrColor(Context ctx, int attrId)
    {
        var tv = new TypedValue();
        return ctx.Theme?.ResolveAttribute(attrId, tv, true) == true
            ? new Color(tv.Data)
            : new Color(ctx.GetColor(Android.Resource.Color.Black));
    }

    private sealed class ExternalUrlSpan(Context context, string url, Color color) : ClickableSpan
    {
        public override void UpdateDrawState(TextPaint ds)
        {
            base.UpdateDrawState(ds);
            ds.Color = color;
            ds.UnderlineText = true;
        }

        public override void OnClick(View widget)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return;
            }
            new MaterialAlertDialogBuilder(context)
                .SetTitle(Resource.String.chat_external_link_title)!
                .SetMessage(uri.ToString())!
                .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
                .SetPositiveButton(Resource.String.chat_external_link_open, (_, _) =>
                {
                    var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(uri.ToString()));
                    intent.AddCategory(Intent.CategoryBrowsable);
                    context.StartActivity(intent);
                })!
                .Show();
        }
    }
}
