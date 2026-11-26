using Android.Content;
using Android.Content.Res;
using Android.Views;
using Google.Android.Material.Color;
using Google.Android.Material.Dialog;
using Google.Android.Material.MaterialSwitch;

namespace DshMobile.App.Ui;

/// <summary>设置行、状态徽标与统计小卡（<c>view_setting_*_row.xml</c> / <c>view_metric_tile.xml</c>）绑定与无状态主题/格式化工具。</summary>
public static class Rows
{
    public enum BadgeTone
    {
        Neutral,
        Primary,
        Success,
        Warning,
        Error,
    }

    public static void BindActionRow(
        View rowView,
        int iconRes,
        string title,
        string? subtitle = null,
        Action? onClick = null,
        bool destructive = false)
    {
        var icon = rowView.FindViewById<ImageView>(Resource.Id.row_icon)!;
        var titleView = rowView.FindViewById<TextView>(Resource.Id.row_title)!;
        var subView = rowView.FindViewById<TextView>(Resource.Id.row_subtitle)!;
        if (iconRes != 0)
        {
            icon.Visibility = ViewStates.Visible;
            icon.SetImageResource(iconRes);
            var tintAttr = destructive ? Android.Resource.Attribute.ColorError : Resource.Attribute.colorOnSurfaceVariant;
            icon.ImageTintList = ColorStateList.ValueOf(new Android.Graphics.Color(Color(rowView, tintAttr)));
        }
        else
        {
            icon.Visibility = ViewStates.Gone;
        }
        titleView.Text = title;
        titleView.SetTextColor(new Android.Graphics.Color(Color(
            rowView,
            destructive ? Android.Resource.Attribute.ColorError : Resource.Attribute.colorOnSurface)));
        SetOptionalText(subView, subtitle);
        if (onClick is not null)
        {
            rowView.Clickable = true;
            rowView.Focusable = true;
            rowView.Click += (_, _) => onClick();
        }
        else
        {
            rowView.Clickable = false;
            rowView.Focusable = false;
        }
    }

    public static void BindNavRow(
        View rowView,
        int iconRes,
        string title,
        string? subtitle = null,
        Action? onClick = null,
        string? badge = null,
        BadgeTone badgeTone = BadgeTone.Neutral)
    {
        BindActionRow(rowView, iconRes, title, subtitle, onClick);
        var badgeView = rowView.FindViewById<TextView>(Resource.Id.row_trailing_badge);
        if (badgeView is not null)
        {
            SetBadge(badgeView, badge, badgeTone);
        }
    }

    public static MaterialSwitch BindSwitchRow(
        View rowView,
        int iconRes,
        string title,
        string? subtitle,
        bool isChecked,
        Action<bool> onChanged)
    {
        BindActionRow(rowView, iconRes, title, subtitle, onClick: null);
        var toggle = rowView.FindViewById<MaterialSwitch>(Resource.Id.row_switch)!;
        toggle.ContentDescription = title;
        toggle.Checked = isChecked;
        toggle.CheckedChange += (_, e) => onChanged(e.IsChecked);
        rowView.Clickable = true;
        rowView.Focusable = true;
        rowView.Click += (_, _) =>
        {
            if (toggle.Enabled)
            {
                toggle.Toggle();
            }
        };
        return toggle;
    }

    public static void BindMetricTile(View tileView, int iconRes, string label, string value)
    {
        tileView.FindViewById<ImageView>(Resource.Id.metric_icon)!.SetImageResource(iconRes);
        tileView.FindViewById<TextView>(Resource.Id.metric_label)!.Text = label;
        tileView.FindViewById<TextView>(Resource.Id.metric_value)!.Text = value;
    }

    public static void SetRowSubtitle(View rowView, string? subtitle) =>
        SetOptionalText(rowView.FindViewById<TextView>(Resource.Id.row_subtitle)!, subtitle);

    public static void SetRowEnabled(View rowView, bool enabled)
    {
        rowView.Enabled = enabled;
        rowView.Alpha = enabled ? 1f : 0.38f;
    }

    public static void SetBadge(TextView badge, string? text, BadgeTone tone = BadgeTone.Neutral)
    {
        if (string.IsNullOrEmpty(text))
        {
            badge.Visibility = ViewStates.Gone;
            return;
        }
        badge.Visibility = ViewStates.Visible;
        badge.Text = text;
        // Polish-P3：设置行已去掉灰底胶囊，纯透明背景只用淡灰文字，不再注入语义色底。
        if (badge.Background is null)
        {
            badge.SetTextColor(new Android.Graphics.Color(badge.Context!.GetColor(Resource.Color.dshm_text_tertiary)));
            return;
        }
        var (bg, fg) = tone switch
        {
            BadgeTone.Primary => (
                Color(badge, Resource.Attribute.colorPrimaryContainer),
                Color(badge, Resource.Attribute.colorOnPrimaryContainer)),
            BadgeTone.Success => (
                Color(badge, Resource.Attribute.colorSecondaryContainer),
                badge.Context!.GetColor(Resource.Color.dshm_running)),
            BadgeTone.Warning => (
                Color(badge, Resource.Attribute.colorTertiaryContainer),
                Color(badge, Resource.Attribute.colorOnTertiaryContainer)),
            BadgeTone.Error => (
                Color(badge, Resource.Attribute.colorErrorContainer),
                Color(badge, Resource.Attribute.colorOnErrorContainer)),
            _ => (
                Color(badge, Resource.Attribute.colorSurfaceContainerHigh),
                Color(badge, Resource.Attribute.colorOnSurfaceVariant)),
        };
        badge.BackgroundTintList = ColorStateList.ValueOf(new Android.Graphics.Color(bg));
        badge.SetTextColor(new Android.Graphics.Color(fg));
    }

    public static void ShowScrollableTextDialog(Context ctx, int titleRes, string text)
    {
        var view = LayoutInflater.From(ctx)!.Inflate(Resource.Layout.dialog_scrollable_text, null, false)!;
        view.FindViewById<TextView>(Resource.Id.dialog_scrollable_text)!.Text = text;
        new MaterialAlertDialogBuilder(ctx)
            .SetTitle(titleRes)!
            .SetView(view)!
            .SetPositiveButton(Android.Resource.String.Ok, (_, _) => { })!
            .Show();
    }

    public static int Dp(Context context, float dp) => (int)(dp * context.Resources!.DisplayMetrics!.Density + 0.5f);

    /// <summary>A theme colour role (e.g. <c>Resource.Attribute.colorPrimary</c>) as an ARGB int.</summary>
    public static int Color(View view, int attr) => MaterialColors.GetColor(view, attr);

    /// <summary>Android <c>%1$s</c> formatting with plain string arguments.</summary>
    public static string Fmt(Context context, int resId, params object[] args) =>
        context.GetString(resId, args.Select(a => a switch
        {
            int i => (Java.Lang.Object)new Java.Lang.Integer(i),
            long l => new Java.Lang.Long(l),
            _ => new Java.Lang.String(a?.ToString() ?? ""),
        }).ToArray()) ?? "";

    /// <summary>The part of a "Label：value" status string after the label (status strings shared with older screens).</summary>
    public static string AfterLabel(string text)
    {
        var i = text.IndexOf('：', StringComparison.Ordinal);
        return i < 0 ? text : text[(i + 1)..];
    }

    private static void SetOptionalText(TextView view, string? text)
    {
        view.Text = text ?? "";
        view.Visibility = string.IsNullOrEmpty(text) ? ViewStates.Gone : ViewStates.Visible;
    }
}
