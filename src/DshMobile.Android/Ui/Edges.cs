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

    internal sealed class Listener(Func<View, WindowInsetsCompat, WindowInsetsCompat> apply) : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat OnApplyWindowInsets(View v, WindowInsetsCompat insets) => apply(v, insets);
    }
}
