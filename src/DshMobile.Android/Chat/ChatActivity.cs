using Android.Content.PM;
using Android.Views;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;
using DshMobile.App.Ui;

namespace DshMobile.App.Chat;

/// <summary>
/// PR-04 纯原生聊天并行入口 Activity（N-04-03 / N-04-05）：
/// 承载 <see cref="ChatFragment"/>，配置变更（横竖屏/键盘）不销毁会话状态，深色模式（<c>UiMode</c>）交由系统重建以完整刷新 Material 3 主题（F2）；
/// 原 <see cref="DshWebActivity"/> 完整保留作为回滚路径。
/// </summary>
[Activity(
    Label = "@string/chat_title",
    Exported = false,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden,
    WindowSoftInputMode = SoftInput.AdjustResize)]
public sealed class ChatActivity : AppCompatActivity
{
    private const string ChatFragmentTag = "native.chat.fragment";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Edges.Enable(Window!);
        SetContentView(Resource.Layout.activity_chat);

        var root = FindViewById(Resource.Id.screen_root)!;
        ViewCompat.SetOnApplyWindowInsetsListener(root, new Edges.Listener((v, insets) =>
        {
            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout())!;
            var ime = insets.GetInsets(WindowInsetsCompat.Type.Ime())!;
            v.SetPadding(bars.Left, bars.Top, bars.Right, Math.Max(bars.Bottom, ime.Bottom));
            return insets;
        }));

        if (SupportFragmentManager.FindFragmentByTag(ChatFragmentTag) is null)
        {
            SupportFragmentManager
                .BeginTransaction()
                .Replace(Resource.Id.chat_fragment_container, new ChatFragment(), ChatFragmentTag)
                .Commit();
        }
    }
}
