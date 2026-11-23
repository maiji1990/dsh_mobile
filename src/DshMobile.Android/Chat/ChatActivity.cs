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
/// PR-04 / PR-05 纯原生聊天并行入口 Activity（N-04-03 / N-04-05 / N-05-04）：
/// 承载 <see cref="ChatFragment"/>，配置变更（横竖屏/键盘）不销毁会话状态，深色模式（<c>UiMode</c>）交由系统重建以完整刷新 Material 3 主题（F2）；
/// 在 <see cref="StartupTrace"/> 上记录 <c>native.activity_visible</c> 与 <c>native.cache_first_rendered</c>。
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
    public const string ExtraSessionId = "chat.extra.session_id";
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
            if (Intent?.GetStringExtra(ExtraSessionId) is { Length: > 0 } requestedSessionId)
            {
                var args = new Bundle();
                args.PutString(ExtraSessionId, requestedSessionId);
                fragment.Arguments = args;
            }
            SupportFragmentManager
                .BeginTransaction()
                .Replace(Resource.Id.chat_fragment_container, fragment, ChatFragmentTag)
                .Commit();
        }
    }
}
