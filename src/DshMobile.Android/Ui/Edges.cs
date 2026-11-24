using Android.Views;
using AndroidX.Core.View;

namespace DshMobile.App.Ui;

/// <summary>
/// ADR-0021: windows draw behind the system bars (enforced from Android 15 on); each screen pads its own root with the
/// system-bar, display-cutout and keyboard insets so nothing hides under them on any Android version.
/// </summary>
public static class Edges
{
    public static void Enable(Window window) => WindowCompat.SetDecorFitsSystemWindows(window, false);

    /// <summary>Pads <paramref name="view"/> with the insets of the given sides (the keyboard counts as bottom).</summary>
    public static void Pad(View view, bool top = true, bool bottom = true)
    {
        var (l, t, r, b) = (view.PaddingLeft, view.PaddingTop, view.PaddingRight, view.PaddingBottom);
        ViewCompat.SetOnApplyWindowInsetsListener(view, new Listener((v, insets) =>
        {
            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout())!;
            var ime = insets.GetInsets(WindowInsetsCompat.Type.Ime())!;
            v.SetPadding(l + bars.Left, t + (top ? bars.Top : 0), r + bars.Right, b + (bottom ? Math.Max(bars.Bottom, ime.Bottom) : 0));
            return insets;
        }));
    }

    /// <summary>True while the soft keyboard is shown.</summary>
    public static bool ImeVisible(WindowInsetsCompat insets) => insets.IsVisible(WindowInsetsCompat.Type.Ime());

    /// <summary>
    /// 宽屏/横屏（<c>values-w600dp</c>）内容限宽居中：当可用宽度超过 <c>dshm_content_max_width</c> 时，
    /// 在保留初始水平内边距的基础上对称追加左右留白，保持 720dp 最佳阅读与表单宽度。
    /// </summary>
    public static void ClampMaxWidth(params View?[] views)
    {
        foreach (var view in views)
        {
            if (view is null)
            {
                continue;
            }
            var baseLeft = view.PaddingLeft;
            var baseTop = view.PaddingTop;
            var baseRight = view.PaddingRight;
            var baseBottom = view.PaddingBottom;
            void Update()
            {
                var res = view.Resources;
                if (res is null || view.Width <= 0)
                {
                    return;
                }
                var maxWidth = res.GetDimensionPixelSize(Resource.Dimension.dshm_content_max_width);
                var extra = maxWidth > 0 ? Math.Max(0, (view.Width - baseLeft - baseRight - maxWidth) / 2) : 0;
                var wantLeft = baseLeft + extra;
                var wantRight = baseRight + extra;
                if (view.PaddingLeft != wantLeft || view.PaddingRight != wantRight)
                {
                    view.SetPadding(wantLeft, baseTop, wantRight, baseBottom);
                }
            }
            view.LayoutChange += (_, _) => Update();
            view.Post(Update);
        }
    }

    /// <summary>
    /// 宽屏/横屏（<c>values-w600dp</c>）外边距居中限宽：适用于自带圆角背景/描边的卡片、横幅或浮层；
    /// 严格依据父容器可用宽度计算对称留白，避免修改自身宽度引起重排震荡。
    /// </summary>
    public static void ClampMaxWidthByMargin(params View?[] views)
    {
        foreach (var view in views)
        {
            if (view?.LayoutParameters is not ViewGroup.MarginLayoutParams initialLp)
            {
                continue;
            }
            var baseLeft = initialLp.LeftMargin;
            var baseRight = initialLp.RightMargin;
            void Update()
            {
                if (view.Parent is not View parent || parent.Width <= 0 ||
                    view.LayoutParameters is not ViewGroup.MarginLayoutParams lp)
                {
                    return;
                }
                var res = view.Resources;
                if (res is null)
                {
                    return;
                }
                var maxWidth = res.GetDimensionPixelSize(Resource.Dimension.dshm_content_max_width);
                var extra = maxWidth > 0 ? Math.Max(0, (parent.Width - baseLeft - baseRight - maxWidth) / 2) : 0;
                var wantLeft = baseLeft + extra;
                var wantRight = baseRight + extra;
                if (lp.LeftMargin != wantLeft || lp.RightMargin != wantRight)
                {
                    lp.LeftMargin = wantLeft;
                    lp.RightMargin = wantRight;
                    view.LayoutParameters = lp;
                }
            }
            if (view.Parent is View parentView)
            {
                parentView.LayoutChange += (_, _) => Update();
            }
            view.Post(Update);
        }
    }

    internal sealed class Listener(Func<View, WindowInsetsCompat, WindowInsetsCompat> apply) : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat OnApplyWindowInsets(View v, WindowInsetsCompat insets) => apply(v, insets);
    }
}
