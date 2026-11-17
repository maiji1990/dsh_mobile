using Android.Content;
using Android.Views;
using DshMobile.App.Automation;
using DshMobile.App.Developer;
using DshMobile.App.Remote;
using DshMobile.App.RuntimeUi;
using DshMobile.App.Ui;
using Google.Android.Material.AppBar;
using Google.Android.Material.Dialog;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace DshMobile.App.More;

/// <summary>
/// More tab (ADR-0021): the feature screens, runtime management, about / licences, and — once the user has tapped the
/// version seven times — developer options (diagnostics, probes, PTY self-test, log).
/// </summary>
public sealed class MoreFragment : Fragment, ITabRoot
{
    private LinearLayout _content = null!;
    private int _versionTaps;
    private Toast? _toast;

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        var view = inflater.Inflate(Resource.Layout.fragment_list, container, false)!;
        view.FindViewById<MaterialToolbar>(Resource.Id.toolbar)!.SetTitle(Resource.String.more_title);
        _content = view.FindViewById<LinearLayout>(Resource.Id.list_content)!;
        Build();
        return view;
    }

    public override void OnResume()
    {
        base.OnResume();
        Build(); // developer options may have been turned off on their own screen
    }

    private void Build()
    {
        var ctx = RequireContext();
        _content.RemoveAllViews();
        Rows.Header(_content, GetString(Resource.String.more_section_features));
        Rows.Add(_content, Resource.Drawable.ic_phone, GetString(Resource.String.more_automation), GetString(Resource.String.more_automation_sub), () => Open(typeof(AutomationActivity)), chevron: true);
        Rows.Add(_content, Resource.Drawable.ic_cloud, GetString(Resource.String.more_remote), GetString(Resource.String.more_remote_sub), () => Open(typeof(RemoteActivity)), chevron: true);

        Rows.Header(_content, GetString(Resource.String.more_section_runtime));
        Rows.Add(_content, Resource.Drawable.ic_storage, GetString(Resource.String.more_runtime), GetString(Resource.String.more_runtime_sub), () => Open(typeof(RuntimeActivity)), chevron: true);

        Rows.Header(_content, GetString(Resource.String.more_section_app));
        Rows.Add(_content, Resource.Drawable.ic_info, GetString(Resource.String.more_about), Rows.Fmt(ctx, Resource.String.more_about_sub, AppVersion(ctx)), OnVersionTapped);
        Rows.Add(_content, Resource.Drawable.ic_article, GetString(Resource.String.more_licenses), GetString(Resource.String.more_licenses_sub), ShowLicenses, chevron: true);

        if (UiPrefs.DeveloperEnabled(ctx))
        {
            Rows.Header(_content, GetString(Resource.String.more_section_developer));
            Rows.Add(_content, Resource.Drawable.ic_code, GetString(Resource.String.developer_title), GetString(Resource.String.more_developer_sub), () => Open(typeof(DeveloperActivity)), chevron: true);
        }
    }

    private void Open(Type activity) => StartActivity(new Intent(RequireContext(), activity));

    internal static string AppVersion(Context ctx)
    {
        var info = ctx.PackageManager!.GetPackageInfo(ctx.PackageName!, 0);
        return info?.VersionName ?? "?";
    }

    /// <summary>Like Android's own developer options: seven taps on the version.</summary>
    private void OnVersionTapped()
    {
        var ctx = RequireContext();
        if (UiPrefs.DeveloperEnabled(ctx))
        {
            Toast(GetString(Resource.String.developer_already));
            return;
        }
        _versionTaps++;
        var left = UiPrefs.DeveloperTaps - _versionTaps;
        if (left <= 0)
        {
            UiPrefs.SetDeveloperEnabled(ctx, true);
            _versionTaps = 0;
            Toast(GetString(Resource.String.developer_enabled));
            Build();
        }
        else if (left <= 3)
        {
            Toast(Rows.Fmt(ctx, Resource.String.developer_steps, left));
        }
    }

    private void Toast(string text)
    {
        _toast?.Cancel();
        _toast = Android.Widget.Toast.MakeText(RequireContext(), text, ToastLength.Short);
        _toast!.Show();
    }

    private void ShowLicenses()
    {
        var ctx = RequireContext();
        var view = (TextView)LayoutInflater.From(ctx)!.Inflate(Resource.Layout.view_mono_text, null, false)!;
        view.SetTextAppearance(Resource.Style.TextAppearance_Material3_BodyMedium);
        view.Text = GetString(Resource.String.licenses_body);
        var scroll = new ScrollView(ctx);
        var pad = Rows.Dp(ctx, 24);
        scroll.SetPadding(pad, Rows.Dp(ctx, 8), pad, 0);
        scroll.AddView(view);
        new MaterialAlertDialogBuilder(ctx)
            .SetTitle(Resource.String.more_licenses)!
            .SetView(scroll)!
            .SetPositiveButton(Android.Resource.String.Ok, (_, _) => { })!
            .Show();
    }
}
