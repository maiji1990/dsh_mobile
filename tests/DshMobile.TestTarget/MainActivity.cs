using Android.Views;
using AndroidX.AppCompat.App;
using Google.Android.Material.Button;
using Google.Android.Material.TextField;

namespace DshMobile.TestTarget;

/// <summary>
/// The one screen the relay host shadow acts on. The marker texts / descriptions are the selectors in
/// DshMobile.Application RelayHostShadowCheck (TitleText, TapText, InputDesc, SeekDesc, ListDesc); keep them in step.
/// Every control only changes this screen's own state.
/// </summary>
[Activity(Label = "DSH Mobile test target", MainLauncher = true, Exported = true, Theme = "@style/Theme.Material3.DayNight.NoActionBar")]
public sealed class MainActivity : AppCompatActivity
{
    private int _taps;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var dp = Resources!.DisplayMetrics!.Density;
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetFitsSystemWindows(true);
        root.SetPadding((int)(16 * dp), (int)(16 * dp), (int)(16 * dp), (int)(16 * dp));

        root.AddView(new TextView(this) { Text = "dshm-target", TextSize = 22 });

        var counter = new TextView(this) { Text = "taps: 0" };
        var tap = new MaterialButton(this) { Text = "dshm-tap" };
        tap.Click += (_, _) => counter.Text = "taps: " + (++_taps).ToString(System.Globalization.CultureInfo.InvariantCulture);
        root.AddView(tap);
        root.AddView(counter);

        var inputLayout = new TextInputLayout(this) { Hint = "input" };
        var input = new TextInputEditText(inputLayout.Context!) { ContentDescription = "dshm-input" };
        inputLayout.AddView(input);
        root.AddView(inputLayout);

        var seek = new SeekBar(this) { Max = 100, ContentDescription = "dshm-seek" };
        root.AddView(seek);

        var list = new LinearLayout(this) { Orientation = Orientation.Vertical };
        for (var i = 1; i <= 200; i++)
        {
            list.AddView(new TextView(this) { Text = "row " + i.ToString(System.Globalization.CultureInfo.InvariantCulture), TextSize = 16 });
        }
        var scroll = new ScrollView(this) { ContentDescription = "dshm-list" };
        scroll.AddView(list);

        // Start state for the shadow's state-dependent calls (each relay resets before acting).
        var reset = new MaterialButton(this) { Text = "dshm-reset" };
        reset.Click += (_, _) =>
        {
            input.Text = "";
            input.ClearFocus();
            seek.Progress = 0;
            scroll.ScrollTo(0, 0);
        };
        root.AddView(reset);
        root.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        SetContentView(root);
    }
}
