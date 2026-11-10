package dshmobile.nativebridge

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.util.Log
import interlock.relay.core.interlock.InterlockChoice
import interlock.relay.core.interlock.InterlockQueue
import interlock.relay.core.interlock.OverlayApprovalChannel
import interlock.relay.core.runtime.RelayRuntime
import interlock.relay.core.spi.RelayApprovalNotificationSurface
import interlock.relay.core.interlock.RelayAskSurface
import interlock.relay.core.spi.RelayNotificationChannelSpec
import interlock.relay.core.spi.RelaySurfaces

/**
 * DSH Mobile's Relay consent surface (TASK-010, ADR-0004): every gated call becomes a heads-up notification
 * with "allow once / allow for this session / deny". No overlay permission is needed. If notifications are
 * disabled, [RelayApprovalNotificationSurface.canShow] is false and Relay fails the call closed
 * (E_GATE_NO_FOREGROUND) — nothing is ever auto-approved here.
 */
internal class DshMobileRelaySurfaces(private val appContext: Context) : RelaySurfaces {
    private val notifications = ApprovalNotifications(appContext)

    override fun approvalOverlay(): OverlayApprovalChannel? = null

    override fun approvalNotification(): RelayApprovalNotificationSurface = notifications

    override fun askSurface(): RelayAskSurface? = null

    /**
     * The SPI means "the host's in-page approval card can be shown" (RelaySurfaces KDoc), not "the app is in
     * front". DSH Mobile has no in-page card, so this is always false; otherwise the queue routes the question to
     * PAGE while our app is in front and nobody ever sees it (observed on device: E_AWAITING_CONSENT, surface
     * "foreground"). Core's clipboard / host-in-front checks fall back to their own appInForeground().
     */
    override fun uiInForeground(): Boolean = false

    override fun notificationChannels(): List<RelayNotificationChannelSpec> = RelayNotificationChannelSpec.defaults()
}

private class ApprovalNotifications(private val context: Context) : RelayApprovalNotificationSurface {
    companion object {
        const val CHANNEL = "dshmobile_relay_approval"
        const val TAG = "relay-approval"
    }

    private val manager = context.getSystemService(NotificationManager::class.java)

    /** Honest delivery state: at best "posted", because visibility to the user cannot be proven. */
    @Volatile
    private var state = "NONE"

    override fun lastPostState(): String = state

    override fun canShow(): Boolean = manager?.areNotificationsEnabled() == true

    override fun show(request: InterlockQueue.Request, waitingCount: Int) {
        val nm = manager ?: run { state = "NO_MANAGER"; return }
        if (!nm.areNotificationsEnabled()) {
            state = "BLOCKED"
            return
        }
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "手机操控审批", NotificationManager.IMPORTANCE_HIGH))
        val capability = request.prompt.capability.wire
        val title = runCatching { context.getString(request.prompt.titleRes) }.getOrDefault(capability)
        val open = context.packageManager.getLaunchIntentForPackage(context.packageName)?.let {
            PendingIntent.getActivity(context, request.id, it, PendingIntent.FLAG_IMMUTABLE)
        }
        val text = "DSH 请求执行 $capability" + if (waitingCount > 0) "（另有 $waitingCount 个请求排队）" else ""
        val notification = Notification.Builder(context, CHANNEL)
            .setSmallIcon(android.R.drawable.ic_dialog_alert)
            .setContentTitle(title)
            .setContentText(text)
            .setStyle(Notification.BigTextStyle().bigText(text))
            .setCategory(Notification.CATEGORY_CALL)
            .setOngoing(true)
            .setAutoCancel(false)
            .setContentIntent(open)
            .addAction(Notification.Action.Builder(null, "允许一次", action(request.id, InterlockChoice.ALLOW_ONCE)).build())
            .addAction(Notification.Action.Builder(null, "本次会话允许", action(request.id, InterlockChoice.ALLOW_SESSION)).build())
            .addAction(Notification.Action.Builder(null, "拒绝", action(request.id, InterlockChoice.DENY)).build())
            .build()
        state = runCatching { nm.notify(TAG, request.id, notification); "POSTED_UNVERIFIED" }.getOrElse { "POST_FAILED" }
    }

    override fun cancel() {
        state = "CANCELLED"
        manager?.activeNotifications?.filter { it.tag == TAG }?.forEach { manager.cancel(TAG, it.id) }
    }

    override fun cancelFor(requestId: Int) {
        manager?.cancel(TAG, requestId)
    }

    private fun action(requestId: Int, choice: InterlockChoice): PendingIntent {
        val intent = Intent(context, RelayApprovalReceiver::class.java)
            .setAction(RelayApprovalReceiver.ACTION)
            .putExtra(RelayApprovalReceiver.EXTRA_ID, requestId)
            .putExtra(RelayApprovalReceiver.EXTRA_CHOICE, choice.name)
        // Distinct request code per (id, choice) so the three PendingIntents don't collapse into one.
        return PendingIntent.getBroadcast(context, requestId * 8 + choice.ordinal, intent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
    }
}

/** Receives the notification button presses (not exported: only our own PendingIntents can reach it). */
class RelayApprovalReceiver : BroadcastReceiver() {
    companion object {
        const val ACTION = "dshmobile.nativebridge.RELAY_APPROVAL"
        const val EXTRA_ID = "requestId"
        const val EXTRA_CHOICE = "choice"
        private val ACCEPTED = setOf(InterlockChoice.ALLOW_ONCE, InterlockChoice.ALLOW_SESSION, InterlockChoice.DENY)
    }

    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != ACTION) return
        val id = intent.getIntExtra(EXTRA_ID, -1)
        val choice = runCatching { InterlockChoice.valueOf(intent.getStringExtra(EXTRA_CHOICE) ?: "") }.getOrNull()
        if (id < 0 || choice == null || choice !in ACCEPTED) return
        val delivered = RelayRuntime.resolveApproval(choice, id)
        RelayRuntime.dismissApprovalNotification(id)
        context.getSystemService(NotificationManager::class.java)?.cancel("relay-approval", id)
        Log.i("DshRelayApproval", "request $id -> $choice (delivered=$delivered)")
    }
}
