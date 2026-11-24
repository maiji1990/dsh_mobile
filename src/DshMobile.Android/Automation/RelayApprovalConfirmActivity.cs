using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidX.AppCompat.App;
using DshMobile.Runtime.Automation.Relay;
using DshMobile.Runtime.Automation.Relay.Interlock;
using Google.Android.Material.Button;

namespace DshMobile.App.Automation;

/// <summary>
/// 通知审批卡「允许」的站内二次确认页（UI Slice 4 全 XML 化，布局 <c>activity_relay_approval_confirm.xml</c>）：
/// 通过 <c>FLAG_SECURE</c> 防截屏与 <c>filterTouchesWhenObscured</c> 防叠加点击劫持，
/// 并在页面销毁窗口移除 500ms 后投递一次性口令至白名单接收器。
/// </summary>
[Activity(
    Name = ApprovalNotificationContent.ConfirmActivity,
    Label = "@string/relay_approval_confirm_title",
    Theme = "@style/Theme.DshMobile",
    Exported = false,
    ExcludeFromRecents = true,
    NoHistory = true,
    TaskAffinity = "",
    LaunchMode = LaunchMode.SingleTop)]
public sealed class RelayApprovalConfirmActivity : AppCompatActivity
{
    private const int AnswerAfterCloseMs = 500;

    private Intent? _request;
    private Intent? _answer;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.AddFlags(WindowManagerFlags.Secure);
        Show(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Show(intent);
    }

    private void Show(Intent? intent)
    {
        _request = intent;
        var id = intent?.GetIntExtra(ApprovalNotificationContent.ExtraRequestId, -1) ?? -1;
        var choice = ApprovalNotificationContent.ParseReceived(id, intent?.GetStringExtra(ApprovalNotificationContent.ExtraChoice));
        if (choice is not { } c || !ApprovalNotificationContent.NeedsConfirmation(c)
            || ApprovalNotificationContent.ConfirmTarget(intent!.GetStringExtra(ApprovalNotificationContent.ExtraReceiver)) is null)
        {
            Finish();
            return;
        }

        SetContentView(Resource.Layout.activity_relay_approval_confirm);
        FindViewById(Resource.Id.relay_confirm_root)!.FilterTouchesWhenObscured = true;
        FindViewById<TextView>(Resource.Id.relay_confirm_title)!.Text = intent.GetStringExtra(ApprovalNotificationContent.ExtraTitle) ?? "";
        FindViewById<TextView>(Resource.Id.relay_confirm_body)!.Text = intent.GetStringExtra(ApprovalNotificationContent.ExtraText) ?? "";

        var cancel = FindViewById<MaterialButton>(Resource.Id.relay_confirm_cancel)!;
        cancel.FilterTouchesWhenObscured = true;
        cancel.Click += (_, _) => Finish();

        var allow = FindViewById<MaterialButton>(Resource.Id.relay_confirm_allow)!;
        allow.FilterTouchesWhenObscured = true;
        allow.SetText(c == InterlockChoice.AllowSession ? Resource.String.relay_approval_allow_session : Resource.String.relay_approval_allow_once);
        allow.Click += (_, _) => Deliver();
    }

    private void Deliver()
    {
        var request = _request;
        if (_answer is null && request is not null
            && ApprovalNotificationContent.ConfirmTarget(request.GetStringExtra(ApprovalNotificationContent.ExtraReceiver)) is { } target)
        {
            _answer = new Intent(target.Action)
                .SetClassName(PackageName!, target.ClassName)
                .PutExtra(ApprovalNotificationContent.ExtraRequestId, request.GetIntExtra(ApprovalNotificationContent.ExtraRequestId, -1))
                .PutExtra(ApprovalNotificationContent.ExtraChoice, request.GetStringExtra(ApprovalNotificationContent.ExtraChoice))
                .PutExtra(ApprovalNotificationContent.ExtraToken, request.GetStringExtra(ApprovalNotificationContent.ExtraToken));
        }
        Finish();
#pragma warning disable CS0618, CA1422
        OverridePendingTransition(0, 0);
#pragma warning restore CS0618, CA1422
    }

    protected override void OnDestroy()
    {
        var answer = _answer;
        _answer = null;
        if (answer is not null)
        {
            var app = ApplicationContext!;
            new Handler(Looper.MainLooper!).PostDelayed(() => app.SendBroadcast(answer), AnswerAfterCloseMs);
        }
        base.OnDestroy();
    }
}
