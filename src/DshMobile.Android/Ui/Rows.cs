using Android.Content;
using Android.Views;
using Google.Android.Material.Color;

namespace DshMobile.App.Ui;

/// <summary>Builds the one list-row layout (<c>item_row.xml</c>) every list screen uses, and small shared view helpers.</summary>
public static class Rows
{
    public sealed class Row(View view)
    {
        public View View { get; } = view;

        public ImageView Icon { get; } = view.FindViewById<ImageView>(Resource.Id.row_icon)!;

        public TextView Title { get; } = view.FindViewById<TextView>(Resource.Id.row_title)!;

        public TextView Subtitle { get; } = view.FindViewById<TextView>(Resource.Id.row_subtitle)!;

        public FrameLayout Trailing { get; } = view.FindViewById<FrameLayout>(Resource.Id.row_trailing)!;

        public void SetSubtitle(string? text)
        {
            Subtitle.Text = text;
            Subtitle.Visibility = string.IsNullOrEmpty(text) ? ViewStates.Gone : ViewStates.Visible;
        }

        /// <summary>Puts <paramref name="view"/> (a button, a switch, a chevron) at the row's end.</summary>
        public void SetTrailing(View? view)
        {
            Trailing.RemoveAllViews();
            if (view is not null)
            {
                Trailing.AddView(view);
            }
            Trailing.Visibility = view is null ? ViewStates.Gone : ViewStates.Visible;
        }
    }

    /// <summary>Inflates a row into <paramref name="parent"/>; <paramref name="onClick"/> makes the whole row a target.</summary>
    public static Row Add(ViewGroup parent, int icon, string title, string? subtitle = null, Action? onClick = null, bool chevron = false)
    {
        var view = LayoutInflater.From(parent.Context)!.Inflate(Resource.Layout.item_row, parent, false)!;
        var row = new Row(view);
        if (icon != 0)
        {
            row.Icon.SetImageResource(icon);
        }
        else
        {
            row.Icon.Visibility = ViewStates.Gone;
        }
        row.Title.Text = title;
        row.SetSubtitle(subtitle);
        if (onClick is not null)
        {
            view.Clickable = true;
            view.Focusable = true;
            view.Click += (_, _) => onClick();
        }
        else
        {
            view.Background = null;
        }
        if (chevron)
        {
            var c = new ImageView(parent.Context);
            c.SetImageResource(Resource.Drawable.ic_chevron_right);
            c.ImportantForAccessibility = ImportantForAccessibility.No;
            row.SetTrailing(c);
        }
        else
        {
            row.SetTrailing(null);
        }
        parent.AddView(view);
        return row;
    }

    /// <summary>A section heading (<c>Widget.DshMobile.SectionHeader</c>).</summary>
    public static TextView Header(ViewGroup parent, string text)
    {
        var view = (TextView)LayoutInflater.From(parent.Context)!.Inflate(Resource.Layout.item_section_header, parent, false)!;
        view.Text = text;
        parent.AddView(view);
        return view;
    }

    /// <summary>A filled card with 16dp side margins holding <paramref name="child"/> (text blocks, logs, outputs).</summary>
    public static Google.Android.Material.Card.MaterialCardView Card(ViewGroup parent, View child, int padDp = 16)
    {
        var context = parent.Context!;
        var card = new Google.Android.Material.Card.MaterialCardView(context, null, Resource.Attribute.materialCardViewFilledStyle);
        card.SetCardBackgroundColor(MaterialColors.GetColor(parent, Resource.Attribute.colorSurfaceContainerLow));
        var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        var m = Dp(context, 16);
        lp.SetMargins(m, Dp(context, 4), m, Dp(context, 4));
        var p = Dp(context, padDp);
        child.SetPadding(p, p, p, p);
        card.AddView(child);
        parent.AddView(card, lp);
        return card;
    }

    /// <summary>A selectable monospace output block (<c>view_mono_text.xml</c>) in a <see cref="Card"/>.</summary>
    public static TextView MonoCard(ViewGroup parent, out View card)
    {
        var text = (TextView)LayoutInflater.From(parent.Context)!.Inflate(Resource.Layout.view_mono_text, parent, false)!;
        card = Card(parent, text);
        return text;
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
}
