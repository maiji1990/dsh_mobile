using Android.Content;
using Android.Content.PM;
using Android.Views;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;
using DshMobile.App.Bridge;
using DshMobile.App.Ui;
using DshMobile.Core.Diagnostics;
using Log = Android.Util.Log;

namespace DshMobile.App.Chat;

/// <summary>
/// PR-04 / PR-05 / PR-06 / PR-09 纯原生聊天 Activity（N-04-03 / N-04-05 / N-05-04 / N-06-03）：
/// 承载 <see cref="ChatFragment"/>，配置变更（横竖屏/键盘）不销毁会话状态，深色模式（<c>UiMode</c>）交由系统重建以完整刷新 Material 3 主题（F2）；
/// 支持从跨会话收件箱、远程主机页或后台系统通知跳转并定位到指定交互卡片（<see cref="ExtraFocusEventId"/>）。
/// </summary>
[Activity(
    Label = "@string/chat_title",
    Exported = false,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden,
    WindowSoftInputMode = SoftInput.AdjustResize)]
public sealed class ChatActivity : AppCompatActivity
{
    public const string ExtraSessionId = "chat.extra.session_id";
    public const string ExtraFocusEventId = "chat.extra.focus_event_id";
    public const string ExtraIsRemote = "chat.extra.is_remote";
    private const string ChatFragmentTag = "native.chat.fragment";

    private StartupTrace? _launchTrace;
    private double _launchMark;
    private int _visibleSpanRecorded;
    private int _cacheSpanRecorded;

    internal void MarkFirstFrameVisible()
    {
        if (Interlocked.Exchange(ref _visibleSpanRecorded, 1) == 0 && _launchTrace is { Enabled: true } trace)
        {
            trace.EmitSpan("native.activity_visible", _launchMark);
        }
    }

    internal void MarkCacheFirstRendered()
    {
        if (Interlocked.Exchange(ref _cacheSpanRecorded, 1) == 0 && _launchTrace is { Enabled: true } trace)
        {
            trace.EmitSpan("native.cache_first_rendered", _launchMark);
        }
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        _launchTrace = UiPrefs.StartupTraceEnabled(this)
            ? StartupTrace.Start(line => Log.Info(BridgeHost.LogTag, line))
            : StartupTrace.Disabled;
        _launchMark = _launchTrace.Mark();

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
        root.Post(MarkFirstFrameVisible);

        if (SupportFragmentManager.FindFragmentByTag(ChatFragmentTag) is null)
        {
            var fragment = new ChatFragment();
            var args = new Bundle();
            if (Intent?.GetStringExtra(ExtraSessionId) is { Length: > 0 } requestedSessionId)
            {
                args.PutString(ExtraSessionId, requestedSessionId);
            }
            if (Intent?.GetStringExtra(ExtraFocusEventId) is { Length: > 0 } focusEventId)
            {
                args.PutString(ExtraFocusEventId, focusEventId);
            }
            if (Intent?.GetBooleanExtra(ExtraIsRemote, false) == true)
            {
                args.PutBoolean(ExtraIsRemote, true);
            }
            if (!args.IsEmpty)
            {
                fragment.Arguments = args;
            }
            SupportFragmentManager
                .BeginTransaction()
                .Replace(Resource.Id.chat_fragment_container, fragment, ChatFragmentTag)
                .Commit();
        }
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (intent is null)
        {
            return;
        }
        Intent = intent;
        if (SupportFragmentManager.FindFragmentByTag(ChatFragmentTag) is ChatFragment fragment)
        {
            fragment.NavigateToSession(
                intent.GetStringExtra(ExtraSessionId),
                intent.GetStringExtra(ExtraFocusEventId));
        }
    }
}
