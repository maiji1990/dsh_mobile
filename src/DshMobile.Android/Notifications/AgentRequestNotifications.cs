using Android.Content;
using Android.OS;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;
using DshMobile.Agent.Presentation;
using DshMobile.App.Chat;
using DshMobile.App.Ui;
using Log = Android.Util.Log;

namespace DshMobile.App.Notifications;

/// <summary>
/// DSH Agent 后台审批/问答系统通知渠道实现（N-06-04）：
/// 使用独立高优先级渠道（<c>dshmobile_agent_requests</c>）与不导出接收器，
/// 仅展示脱敏工具名与安全摘要；与 Relay <c>ui_*</c> 审批通道（<c>dshmobile_relay_approval</c>）物理隔离，绝不跨域放行。
/// </summary>
public sealed class AgentRequestNotificationSurface(Context context) : IBackgroundRequestNotificationSurface
{
    public const string ChannelId = "dshmobile_agent_requests";
    public const string NotificationTag = "agent-request";

    private readonly Context _context = context.ApplicationContext ?? context;

    private NotificationManager? Manager => (NotificationManager?)_context.GetSystemService(Context.NotificationService);

    public bool CanShow() => Manager?.AreNotificationsEnabled() == true;

    public void ShowOrUpdate(AgentRequestNotificationPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (Manager is not { } nm || !nm.AreNotificationsEnabled())
        {
            return;
        }

        nm.CreateNotificationChannel(new NotificationChannel(
            ChannelId,
            _context.GetString(Resource.String.agent_request_channel),
            NotificationImportance.High));

        var title = payload.Kind == PendingRequestKind.Approval
            ? Rows.Fmt(_context, Resource.String.agent_notify_approval_title, payload.SanitizedToolOrTopic)
            : Rows.Fmt(_context, Resource.String.agent_notify_question_title, payload.SanitizedToolOrTopic);
        var sessionLine = Rows.Fmt(_context, Resource.String.agent_notify_session_prefix, payload.SessionTitle);
        var bodyText = string.IsNullOrWhiteSpace(payload.SanitizedSummary)
            ? sessionLine
            : $"{sessionLine}\n{payload.SanitizedSummary}";

        var openIntent = new Intent(_context, typeof(ChatActivity))
            .AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop)
            .PutExtra(ChatActivity.ExtraSessionId, payload.SessionId)
            .PutExtra(ChatActivity.ExtraFocusEventId, payload.EventId);
        var openPending = PendingIntent.GetActivity(
            _context,
            payload.NotificationId,
            openIntent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var builder = new Notification.Builder(_context, ChannelId)
            .SetSmallIcon(Resource.Drawable.ic_notification)
            .SetContentTitle(title)
            .SetContentText(payload.SanitizedSummary)
            .SetStyle(new Notification.BigTextStyle().BigText(bodyText))
            .SetCategory(Notification.CategoryMessage)
            .SetAutoCancel(true)
            .SetOnlyAlertOnce(payload.IsUpdate)
            .SetContentIntent(openPending);

        if (payload.Kind == PendingRequestKind.Approval)
        {
            builder.AddAction(BuildQuickAction(
                payload,
                ApprovalDecision.AllowedOnce,
                AgentRequestNotificationReceiver.DecisionAllowOnce,
                Resource.String.agent_notify_allow_once,
                slotOffset: 1));
            builder.AddAction(BuildQuickAction(
                payload,
                ApprovalDecision.Rejected,
                AgentRequestNotificationReceiver.DecisionReject,
                Resource.String.agent_notify_reject,
                slotOffset: 2));
        }

        try
        {
            nm.Notify(NotificationTag, payload.NotificationId, builder.Build());
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or InvalidOperationException)
        {
            Log.Warn("DshAgentNotify", $"post notification failed ({ex.GetType().Name})");
        }
    }

    public void CancelForEvent(string eventId, int notificationId)
    {
        try
        {
            Manager?.Cancel(NotificationTag, notificationId);
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or InvalidOperationException)
        {
            Log.Warn("DshAgentNotify", $"cancel notification failed ({ex.GetType().Name})");
        }
    }

    public void CancelAll()
    {
        if (Manager is not { } nm)
        {
            return;
        }
        try
        {
            foreach (var n in nm.GetActiveNotifications() ?? [])
            {
                if (n.Tag == NotificationTag)
                {
                    nm.Cancel(NotificationTag, n.Id);
                }
            }
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or InvalidOperationException)
        {
            Log.Warn("DshAgentNotify", $"cancel all notifications failed ({ex.GetType().Name})");
        }
    }

    private Notification.Action BuildQuickAction(
        AgentRequestNotificationPayload payload,
        ApprovalDecision decision,
        string decisionWire,
        int labelResId,
        int slotOffset)
    {
        var intent = new Intent(_context, typeof(AgentRequestNotificationReceiver))
            .SetAction(AgentRequestNotificationReceiver.ActionAgentRequest)
            .PutExtra(AgentRequestNotificationReceiver.ExtraEventId, payload.EventId)
            .PutExtra(AgentRequestNotificationReceiver.ExtraDecision, decisionWire)
            .PutExtra(AgentRequestNotificationReceiver.ExtraNonce, payload.OneTimeNonce);

        var requestCode = unchecked((payload.NotificationId * 4) + slotOffset);
        var pending = PendingIntent.GetBroadcast(
            _context,
            requestCode,
            intent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;

        return new Notification.Action.Builder(
            (global::Android.Graphics.Drawables.Icon?)null,
            _context.GetString(labelResId),
            pending).Build();
    }
}

/// <summary>
/// 不导出（<c>Exported = false</c>）的 DSH Agent 通知快捷操作接收器（N-06-04）：
/// 验证一次性随机数后统一交由 <see cref="RequestsEngine"/> 原子单写者认领；
/// 与 Relay <c>ui_*</c> 审批接收器完全隔离，绝不跨域放行。
/// </summary>
[BroadcastReceiver(Name = "dshmobile.app.AgentRequestNotificationReceiver", Exported = false)]
public sealed class AgentRequestNotificationReceiver : BroadcastReceiver
{
    public const string ActionAgentRequest = "dshmobile.app.AGENT_REQUEST_ACTION";
    public const string ExtraEventId = "dshmobile.agent.extra.event_id";
    public const string ExtraDecision = "dshmobile.agent.extra.decision";
    public const string ExtraNonce = "dshmobile.agent.extra.nonce";
    public const string DecisionAllowOnce = "allow_once";
    public const string DecisionReject = "reject";

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent?.Action != ActionAgentRequest)
        {
            return;
        }

        var eventId = intent.GetStringExtra(ExtraEventId);
        var decisionWire = intent.GetStringExtra(ExtraDecision);
        var nonce = intent.GetStringExtra(ExtraNonce);

        ApprovalDecision? decision = decisionWire switch
        {
            DecisionAllowOnce => ApprovalDecision.AllowedOnce,
            DecisionReject => ApprovalDecision.Rejected,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(eventId) || decision is null || string.IsNullOrWhiteSpace(nonce))
        {
            return;
        }

        var coordinator = DshMobileApplication.Current?.Bridge?.Agent?.RequestNotifications;
        if (coordinator is null)
        {
            return;
        }

        if (!coordinator.TryClaimNotificationAction(eventId, decision.Value, nonce, out var dispatchTask))
        {
            Log.Warn("DshAgentNotify", $"dropped stale or unauthorized action ({decisionWire})");
            return;
        }

        Log.Info("DshAgentNotify", $"claimed action ({decisionWire})");
        var pendingResult = GoAsync();
        if (pendingResult is not null)
        {
            _ = dispatchTask.ContinueWith(
                _ => pendingResult.Finish(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
