using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Text.Method;
using Android.Text.Style;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.Core.Content;
using DshMobile.Agent.Rendering;
using DshMobile.App.Ui;
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
    /// <summary>代码块默认折叠时展示的最大行数。</summary>
    private const int CodeCollapsedLineCount = 12;

    /// <summary>触发“自动换行”入口的长行阈值（字符数）。</summary>
    private const int CodeLongLineThreshold = 48;

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

        // Polish-P4：行内代码只保留等宽字体与略小字号，彻底移除 BackgroundColorSpan，
        // 不再产生跨行灰底矩形补丁；底色由正文纸面自然承载。
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
                    builder.SetSpan(new RelativeSizeSpan(0.92f), start, end, SpanTypes.ExclusiveExclusive);
                    builder.SetSpan(new TypefaceSpan("monospace"), start, end, SpanTypes.ExclusiveExclusive);
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
        var copyBtn = card.FindViewById<MaterialButton>(Resource.Id.code_block_copy)!;
        var wrapBtn = card.FindViewById<MaterialButton>(Resource.Id.code_block_wrap)!;
        var languageView = card.FindViewById<TextView>(Resource.Id.code_block_language)!;
        var scrollView = card.FindViewById<HorizontalScrollView>(Resource.Id.code_block_scroll)!;
        var bodyView = card.FindViewById<TextView>(Resource.Id.code_block_body)!;
        var wrappedBodyView = card.FindViewById<TextView>(Resource.Id.code_block_body_wrapped)!;
        var foldDivider = card.FindViewById<View>(Resource.Id.code_block_fold_divider)!;
        var foldToggle = card.FindViewById<MaterialButton>(Resource.Id.code_block_fold_toggle)!;

        var language = string.IsNullOrWhiteSpace(seg.Language)
            ? ctx.GetString(Resource.String.chat_code_language_fallback)
            : seg.Language.Trim();
        languageView.Text = language;

        // 统一换行符后去掉首尾空行（含只由空格 / Tab 组成的空白行），再按行折叠，
        // 避免 Header 与首行代码之间、或代码末尾与 14dp 底距之间多出空行。
        var normalizedCode = seg.Code
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var normalizedLines = normalizedCode.Split('\n');
        var firstCodeLine = 0;
        var lastCodeLine = normalizedLines.Length - 1;
        while (firstCodeLine <= lastCodeLine && string.IsNullOrWhiteSpace(normalizedLines[firstCodeLine]))
        {
            firstCodeLine++;
        }
        while (lastCodeLine >= firstCodeLine && string.IsNullOrWhiteSpace(normalizedLines[lastCodeLine]))
        {
            lastCodeLine--;
        }
        var fullText = firstCodeLine > lastCodeLine
            ? string.Empty
            : string.Join("\n", normalizedLines, firstCodeLine, lastCodeLine - firstCodeLine + 1);
        var lines = fullText.Split('\n');
        // Markdown 围栏代码常以单个换行结尾；末尾空行不计入“总行数”，避免 12 行代码误显示为 13 行。
        var totalLines = lines.Length > 1 && lines[^1].Length == 0
            ? lines.Length - 1
            : lines.Length;
        var canFold = totalLines > CodeCollapsedLineCount;
        var foldedText = canFold
            ? string.Join("\n", lines, 0, CodeCollapsedLineCount)
            : fullText;
        var hasLongLine = HasLongCodeLine(lines);

        var isExpanded = false;
        var isWrapped = false;
        RotateDrawable? rotatingFoldIcon = null;

        // 两种正文视图共用同一份文本与语法着色：切换换行只改可见性，避免 RecyclerView 内闪烁。
        void ApplyBodyText()
        {
            var text = isExpanded ? fullText : foldedText;
            ApplyCodeSyntax(ctx, bodyView, text, seg.Language);
            ApplyCodeSyntax(ctx, wrappedBodyView, text, seg.Language);
        }

        void ApplyFoldState()
        {
            foldDivider.Visibility = canFold ? ViewStates.Visible : ViewStates.Gone;
            foldToggle.Visibility = canFold ? ViewStates.Visible : ViewStates.Gone;
            if (!canFold)
            {
                return;
            }

            foldToggle.Text = isExpanded
                ? ctx.GetString(Resource.String.chat_code_collapse)
                : ctx.GetString(Resource.String.chat_code_expand_all_fmt, totalLines);
            if (rotatingFoldIcon is not null)
            {
                // 0..10000 对应 0°..180°：展开时箭头朝上，折叠时朝下。
                rotatingFoldIcon.SetLevel(isExpanded ? 10000 : 0);
            }
        }

        void ApplyWrapState()
        {
            scrollView.Visibility = isWrapped ? ViewStates.Gone : ViewStates.Visible;
            wrappedBodyView.Visibility = isWrapped ? ViewStates.Visible : ViewStates.Gone;
            wrapBtn.Selected = isWrapped;
            wrapBtn.ContentDescription = ctx.GetString(
                isWrapped ? Resource.String.chat_code_wrap_off : Resource.String.chat_code_wrap);
        }

        wrapBtn.Visibility = hasLongLine ? ViewStates.Visible : ViewStates.Gone;

        var expandIcon = AndroidX.AppCompat.Content.Res.AppCompatResources.GetDrawable(ctx, Resource.Drawable.ic_expand_more);
        if (expandIcon is not null)
        {
            rotatingFoldIcon = new RotateDrawable();
            rotatingFoldIcon.Drawable = expandIcon;
            rotatingFoldIcon.FromDegrees = 0f;
            rotatingFoldIcon.ToDegrees = 180f;
            rotatingFoldIcon.SetLevel(0);
            foldToggle.Icon = rotatingFoldIcon;
        }

        ApplyBodyText();
        ApplyFoldState();
        ApplyWrapState();

        copyBtn.Click += (_, _) =>
        {
            var clipboard = (global::Android.Content.ClipboardManager?)ctx.GetSystemService(Context.ClipboardService);
            clipboard?.PrimaryClip = ClipData.NewPlainText("code", seg.Code);
            Snackbar.Make(card, Resource.String.chat_code_copied, Snackbar.LengthShort).Show();
        };

        foldToggle.Click += (_, _) =>
        {
            if (!canFold)
            {
                return;
            }
            isExpanded = !isExpanded;
            ApplyBodyText();
            ApplyFoldState();
        };

        wrapBtn.Click += (_, _) =>
        {
            if (!hasLongLine)
            {
                return;
            }
            isWrapped = !isWrapped;
            ApplyWrapState();
        };
    }

    /// <summary>判断代码中是否存在需要自动换行入口的超长行。</summary>
    private static bool HasLongCodeLine(string[] lines)
    {
        foreach (var line in lines)
        {
            if (line.Length > CodeLongLineThreshold)
            {
                return true;
            }
        }
        return false;
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
        // 让短表格（尤其 2 列）在 fillViewport 下均分铺满卡片宽度，避免右侧留白。
        grid.StretchAllColumns = true;

        if (seg.Headers.Count > 0)
        {
            var headerRow = (TableRow)inflater.Inflate(Resource.Layout.item_chat_table_row, grid, false)!;
            foreach (var h in seg.Headers)
            {
                var cell = (TextView)inflater.Inflate(Resource.Layout.item_chat_table_cell, headerRow, false)!;
                cell.Text = h;
                AllowCellWrap(cell);
                cell.SetTypeface(cell.Typeface, TypefaceStyle.Bold);
                // 表头使用 4–5% 中性微底（与行内代码同一层雾），保持极淡且不抢正文。
                cell.SetBackgroundColor(new Color(ctx.GetColor(Resource.Color.dshm_inline_code_bg)));
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
                AllowCellWrap(cell);
                tr.AddView(cell);
            }
            grid.AddView(tr);
        }
    }

    /// <summary>单元格内允许换行并取消省略号；超出屏幕宽度时由外层 HorizontalScrollView 横向滚动查看。</summary>
    private static void AllowCellWrap(TextView cell)
    {
        cell.SetSingleLine(false);
        cell.Ellipsize = null;
    }

    /// <summary>
    /// 把纯托管语法扫描结果映射为 Android 文本 Span；着色只覆盖 token 本身，不改变字号与行高。
    /// </summary>
    internal static void ApplyCodeSyntax(Context ctx, TextView view, string text, string? language) =>
        ApplyTokens(ctx, view, text, CodeSyntaxHighlighter.Highlight(text, language));

    /// <summary>
    /// 终端卡片专用着色：仅对 <c>$ </c> 命令行着色，stdout / stderr 保持中性正文色，
    /// 防止普通英文错误消息被误识别成关键字。
    /// </summary>
    internal static void ApplyTerminalSyntax(Context ctx, TextView view, string text) =>
        ApplyTokens(ctx, view, text, CodeSyntaxHighlighter.HighlightTerminal(text));

    private static void ApplyTokens(
        Context ctx,
        TextView view,
        string text,
        IReadOnlyList<CodeSyntaxToken> tokens)
    {
        var builder = new SpannableStringBuilder(text);
        foreach (var token in tokens)
        {
            var color = new Color(ctx.GetColor(ResolveSyntaxColor(token.Kind)));
            builder.SetSpan(
                new ForegroundColorSpan(color),
                token.Start,
                token.Start + token.Length,
                SpanTypes.ExclusiveExclusive);
        }
        view.SetText(builder, TextView.BufferType.Spannable);
    }

    private static int ResolveSyntaxColor(CodeSyntaxTokenKind kind) => kind switch
    {
        CodeSyntaxTokenKind.Comment => Resource.Color.dshm_syntax_comment,
        CodeSyntaxTokenKind.String => Resource.Color.dshm_syntax_string,
        CodeSyntaxTokenKind.Keyword => Resource.Color.dshm_syntax_keyword,
        CodeSyntaxTokenKind.Number => Resource.Color.dshm_syntax_number,
        CodeSyntaxTokenKind.Prompt => Resource.Color.dshm_syntax_prompt,
        _ => Resource.Color.dshm_syntax_path,
    };

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
