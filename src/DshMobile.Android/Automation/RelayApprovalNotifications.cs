using Android.Content;
using Android.Util;
using DshMobile.Runtime.Automation.Relay;
using DshMobile.Runtime.Automation.Relay.Interlock;

namespace DshMobile.App.Automation;

/// <summary>
/// The C# Relay approval surface (PR-07 step 4): the same heads-up card as the legacy RelayApproval.kt — "allow once /
/// allow for this session / deny", no overlay permission. Only the C# approval queue posts it, and only once that
/// slice decides (SliceMode.CSharp); under LegacyWithShadow the legacy Relay keeps posting its own card.
/// Notifications blocked → <see cref="CanShow"/> false → the call fails closed; nothing here approves.
/// </summary>
public sealed class RelayApprovalNotifications(Context context) : IApprovalNotificationSurface
{
    private readonly Context _context = context.ApplicationContext ?? context;
    private volatile string _state = ApprovalPostStates.None;

    private NotificationManager? Manager => (NotificationManager?)_context.GetSystemService(Context.NotificationService);

    public string LastPostState() => _state;

    public bool CanShow() => Manager?.AreNotificationsEnabled() == true;

    public void Show(InterlockQueue.Request request, int waitingCount)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Manager is not { } nm)
        {
            _state = ApprovalPostStates.NoManager;
            return;
        }
        if (!nm.AreNotificationsEnabled())
        {
            _state = ApprovalPostStates.Blocked;
            return;
        }
        nm.CreateNotificationChannel(new NotificationChannel(ApprovalNotificationContent.Channel,
            _context.GetString(Resource.String.relay_approval_channel), NotificationImportance.High));
        var capability = request.Prompt.Capability;
        var headline = ApprovalNotificationContent.Text(capability, waitingCount);
        var text = ApprovalNotificationContent.Detail(capability, waitingCount, request.Prompt.TargetLabel, request.Prompt.ParamDetail);
        var launch = _context.PackageManager?.GetLaunchIntentForPackage(_context.PackageName!);
        var open = launch is null ? null : PendingIntent.GetActivity(_context, request.Id, launch, PendingIntentFlags.Immutable);
        var builder = new Notification.Builder(_context, ApprovalNotificationContent.Channel)
            .SetSmallIcon(Android.Resource.Drawable.IcDialogAlert)
            .SetContentTitle(capability)
            .SetContentText(headline)
            .SetStyle(new Notification.BigTextStyle().BigText(text))
            .SetCategory(Notification.CategoryCall)
            .SetOngoing(true)
            .SetAutoCancel(false)
            .SetContentIntent(open);
        var token = RelayApprovalReceiver.Tokens.Issue(request.Id);
        foreach (var choice in ApprovalNotificationContent.Actions)
        {
            builder.AddAction(new Notification.Action.Builder((Android.Graphics.Drawables.Icon?)null, Label(choice),
                Action(request.Id, choice, token, capability, text)).Build());
        }
        try
        {
            nm.Notify(ApprovalNotificationContent.Tag, request.Id, builder.Build());
            _state = ApprovalPostStates.PostedUnverified;
        }
        catch (Exception e) when (e is Java.Lang.Exception or InvalidOperationException)
        {
            _state = ApprovalPostStates.PostFailed;
        }
    }

    public void Cancel()
    {
        _state = ApprovalPostStates.Cancelled;
        RelayApprovalReceiver.Tokens.Clear();
        if (Manager is not { } nm)
        {
            return;
        }
        foreach (var n in nm.GetActiveNotifications() ?? [])
        {
            if (n.Tag == ApprovalNotificationContent.Tag)
            {
                nm.Cancel(ApprovalNotificationContent.Tag, n.Id);
            }
        }
    }

    public void CancelFor(int requestId)
    {
        RelayApprovalReceiver.Tokens.Revoke(requestId);
        Manager?.Cancel(ApprovalNotificationContent.Tag, requestId);
    }

    private string Label(InterlockChoice choice) => _context.GetString(choice switch
    {
        InterlockChoice.AllowOnce => Resource.String.relay_approval_allow_once,
        InterlockChoice.AllowSession => Resource.String.relay_approval_allow_session,
        _ => Resource.String.relay_approval_deny,
    });

    /// <summary>
    /// Deny is a direct broadcast; every allow opens <see cref="RelayApprovalConfirmActivity"/>, where the final press
    /// happens in this app's own window (host gestures are suspended there). Both carry the card's one-time token.
    /// </summary>
    private PendingIntent Action(int requestId, InterlockChoice choice, string token, string title, string text)
    {
        var code = ApprovalNotificationContent.RequestCode(requestId, choice);
        const PendingIntentFlags flags = PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent;
        if (ApprovalNotificationContent.NeedsConfirmation(choice))
        {
            var confirm = new Intent(_context, typeof(RelayApprovalConfirmActivity))
                .AddFlags(ActivityFlags.NewTask)
                .PutExtra(ApprovalNotificationContent.ExtraRequestId, requestId)
                .PutExtra(ApprovalNotificationContent.ExtraChoice, choice.Wire())
                .PutExtra(ApprovalNotificationContent.ExtraToken, token)
                .PutExtra(ApprovalNotificationContent.ExtraTitle, title)
                .PutExtra(ApprovalNotificationContent.ExtraText, text)
                .PutExtra(ApprovalNotificationContent.ExtraReceiver, ApprovalNotificationContent.ReceiverCSharp);
            return PendingIntent.GetActivity(_context, code, confirm, flags)!;
        }
        var intent = new Intent(_context, typeof(RelayApprovalReceiver))
            .SetAction(RelayApprovalReceiver.ActionName)
            .PutExtra(ApprovalNotificationContent.ExtraRequestId, requestId)
            .PutExtra(ApprovalNotificationContent.ExtraChoice, choice.Wire())
            .PutExtra(ApprovalNotificationContent.ExtraToken, token);
        return PendingIntent.GetBroadcast(_context, code, intent, flags)!;
    }
}

/// <summary>
/// Receives the C# card's answers (deny directly, allow via the confirmation page). Not exported, and since guest
/// processes share this app's UID an answer counts only with the card's one-time token (<see cref="Tokens"/>). The answer
/// goes to <see cref="Resolve"/> (the C# queue's ResolveCurrent, pinned to the card's request id); with no sink wired
/// the press is dropped and logged — never treated as an approval.
/// </summary>
[BroadcastReceiver(Name = "dshmobile.app.RelayApprovalReceiver", Exported = false)]
public sealed class RelayApprovalReceiver : BroadcastReceiver
{
    public const string ActionName = "dshmobile.app.RELAY_APPROVAL";

    /// <summary>(choice, requestId) → whether a pending request took it. Wired by the C# Relay container.</summary>
    public static Func<InterlockChoice, int, bool>? Resolve { get; set; }

    /// <summary>One-time tokens of the posted C# cards.</summary>
    public static ApprovalTokens Tokens { get; } = new();

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent?.Action != ActionName)
        {
            return;
        }
        var id = intent.GetIntExtra(ApprovalNotificationContent.ExtraRequestId, -1);
        if (ApprovalNotificationContent.ParseReceived(id, intent.GetStringExtra(ApprovalNotificationContent.ExtraChoice)) is not { } choice)
        {
            return;
        }
        if (!Tokens.Consume(id, intent.GetStringExtra(ApprovalNotificationContent.ExtraToken)))
        {
            Log.Warn("DshRelayApproval", $"request {id}: answer without a valid token dropped");
            return;
        }
        var delivered = Resolve?.Invoke(choice, id) ?? false;
        ((NotificationManager?)context.GetSystemService(Context.NotificationService))?.Cancel(ApprovalNotificationContent.Tag, id);
        Log.Info("DshRelayApproval", $"request {id} -> {choice.Wire()} (delivered={delivered})");
    }
}
