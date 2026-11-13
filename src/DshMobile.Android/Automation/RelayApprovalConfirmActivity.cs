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
/// The in-app page every "allow" button of a Relay approval card opens (legacy RelayApproval.kt and the C# surface).
/// The final allow is pressed here, in this app's own window: while it is on screen Relay refuses every gesture, node
/// action and key it would otherwise run on the host display (A11yBackend <c>selfWindowVisible</c> →
/// E_GATE_SUSPENDED_BY_HOST), so the agent cannot tap its own approval. FLAG_SECURE keeps it out of screen capture and
/// obscured touches are filtered (no overlay tap-jacking). The answer carries the card's one-time token and goes only to
/// one of the two whitelisted receivers; this page itself decides nothing.
/// </summary>
[Activity(
    Name = ApprovalNotificationContent.ConfirmActivity,
    Label = "@string/relay_approval_confirm_title",
    Theme = "@style/Theme.DshMobile.Material3",
    Exported = false,
    ExcludeFromRecents = true,
    NoHistory = true,
    TaskAffinity = "",
    LaunchMode = LaunchMode.SingleTop)]
public sealed class RelayApprovalConfirmActivity : AppCompatActivity
{
    private Intent? _request;

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
        var density = Resources!.DisplayMetrics!.Density;
        int Dp(int v) => (int)(v * density);

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical, FilterTouchesWhenObscured = true };
        root.SetFitsSystemWindows(true);
        root.SetPadding(Dp(24), Dp(24), Dp(24), Dp(24));
        var title = new TextView(this) { Text = intent.GetStringExtra(ApprovalNotificationContent.ExtraTitle) ?? "" };
        title.SetTextAppearance(Resource.Style.TextAppearance_Material3_HeadlineSmall);
        root.AddView(title);
        var body = new TextView(this) { Text = intent.GetStringExtra(ApprovalNotificationContent.ExtraText) ?? "" };
        body.SetTextAppearance(Resource.Style.TextAppearance_Material3_BodyLarge);
        body.SetPadding(0, Dp(12), 0, Dp(12));
        root.AddView(body);
        var hint = new TextView(this) { Text = GetString(Resource.String.relay_approval_confirm_hint) };
        hint.SetTextAppearance(Resource.Style.TextAppearance_Material3_BodyMedium);
        root.AddView(hint);

        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetPadding(0, Dp(24), 0, 0);
        var cancel = new MaterialButton(this, null, Resource.Attribute.materialButtonOutlinedStyle)
        {
            Text = GetString(Resource.String.relay_approval_cancel),
            FilterTouchesWhenObscured = true,
        };
        cancel.Click += (_, _) => Finish();
        var allow = new MaterialButton(this)
        {
            Text = GetString(c == InterlockChoice.AllowSession ? Resource.String.relay_approval_allow_session : Resource.String.relay_approval_allow_once),
            FilterTouchesWhenObscured = true,
        };
        allow.Click += (_, _) => Deliver();
        var lp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { MarginEnd = Dp(8) };
        row.AddView(cancel, lp);
        row.AddView(allow, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        root.AddView(row);
        SetContentView(root);
    }

    /// <summary>
    /// How long after this page is destroyed the answer goes out. Relay runs an approved call at once, and the backend
    /// refuses writes while any window of this app is still in the accessibility window list; a finishing activity stays
    /// there until its window is removed (device run 2026-11-13: answer at 14:07:03.743, window removed 14:07:04.471,
    /// call → E_TASK_SUSPENDED_BY_HOST). The page therefore closes without an exit animation and answers only after
    /// <see cref="Android.App.Activity.OnDestroy"/> plus this margin for the window list to catch up.
    /// </summary>
    private const int AnswerAfterCloseMs = 500;

    private Intent? _answer;

    /// <summary>
    /// The user pressed allow: build the answer from the card's own extras (id, choice, token) for the whitelisted
    /// receiver, close the page, and send it once the page is gone (<see cref="OnDestroy"/>). The receiver checks the token.
    /// </summary>
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
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
        {
            OverrideActivityTransition(OverrideTransition.Close, 0, 0);
        }
        Finish();
        if (!OperatingSystem.IsAndroidVersionAtLeast(34))
        {
#pragma warning disable CA1422 // the pre-34 way to drop the exit animation
            OverridePendingTransition(0, 0);
#pragma warning restore CA1422
        }
    }

    protected override void OnDestroy()
    {
        var answer = _answer;
        _answer = null;
        base.OnDestroy();
        // Only an explicit allow on a finishing page answers; a recreate (rotation) or cancel sends nothing.
        if (answer is not null && IsFinishing)
        {
            var app = ApplicationContext!;
            new Handler(Looper.MainLooper!).PostDelayed(() => app.SendBroadcast(answer), AnswerAfterCloseMs);
        }
    }
}
