using Android.Content.PM;
using Android.Views;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;
using DshMobile.App.Ui;

namespace DshMobile.App.Credentials;

/// <summary>
/// PR-05 原生 Provider 与凭据配置入口 Activity（N-05-01）：
/// 承载 <see cref="CredentialsFragment"/>，不通过 <c>OnSaveInstanceState</c> 保存任何密钥或凭据输入内容。
/// </summary>
[Activity(
    Label = "@string/credentials_title",
    Exported = false,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden,
    WindowSoftInputMode = SoftInput.AdjustResize)]
public sealed class CredentialsActivity : AppCompatActivity
{
    private const string FragmentTag = "native.credentials.fragment";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Edges.Enable(Window!);
        SetContentView(Resource.Layout.activity_credentials);

        var root = FindViewById(Resource.Id.screen_root)!;
        ViewCompat.SetOnApplyWindowInsetsListener(root, new Edges.Listener((v, insets) =>
        {
            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout())!;
            var ime = insets.GetInsets(WindowInsetsCompat.Type.Ime())!;
            v.SetPadding(bars.Left, bars.Top, bars.Right, Math.Max(bars.Bottom, ime.Bottom));
            return insets;
        }));

        if (SupportFragmentManager.FindFragmentByTag(FragmentTag) is null)
        {
            SupportFragmentManager
                .BeginTransaction()
                .Replace(Resource.Id.credentials_fragment_container, new CredentialsFragment(), FragmentTag)
                .Commit();
        }
    }
}
