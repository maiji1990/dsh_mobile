package dshmobile.nativebridge

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.util.Log
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.concurrent.ConcurrentHashMap
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
        val headline = "DSH 请求执行 $capability" + if (waitingCount > 0) "（另有 $waitingCount 个请求排队）" else ""
        // Security fix (ADR-0008): say what is being approved, not only which capability (upstream already
        // truncates both lines for display). Without it a sys.shell card does not show the command.
        val text = listOfNotNull(
            headline,
            request.prompt.targetLabel?.takeIf { it.isNotBlank() }?.let { "目标：$it" },
            request.prompt.paramDetail?.takeIf { it.isNotBlank() }?.let { "参数：$it" },
        ).joinToString(separator = "\n")
        val token = ApprovalTokens.issue(request.id)
        val notification = Notification.Builder(context, CHANNEL)
            .setSmallIcon(android.R.drawable.ic_dialog_alert)
            .setContentTitle(title)
            .setContentText(headline)
            .setStyle(Notification.BigTextStyle().bigText(text))
            .setCategory(Notification.CATEGORY_CALL)
            .setOngoing(true)
            .setAutoCancel(false)
            .setContentIntent(open)
            .addAction(Notification.Action.Builder(null, "允许一次", confirm(request.id, InterlockChoice.ALLOW_ONCE, token, title, text)).build())
            .addAction(Notification.Action.Builder(null, "本次会话允许", confirm(request.id, InterlockChoice.ALLOW_SESSION, token, title, text)).build())
            .addAction(Notification.Action.Builder(null, "拒绝", deny(request.id, token)).build())
            .build()
        state = runCatching { nm.notify(TAG, request.id, notification); "POSTED_UNVERIFIED" }.getOrElse { "POST_FAILED" }
    }

    override fun cancel() {
        state = "CANCELLED"
        ApprovalTokens.clear()
        manager?.activeNotifications?.filter { it.tag == TAG }?.forEach { manager.cancel(TAG, it.id) }
    }

    override fun cancelFor(requestId: Int) {
        ApprovalTokens.revoke(requestId)
        manager?.cancel(TAG, requestId)
    }

    /**
     * Security fix (ADR-0008): an allow opens the host app's in-app confirmation page
     * (dshmobile.app.RelayApprovalConfirmActivity). The final press happens in our own window, where Relay suspends
     * host gestures (A11yBackend selfWindowVisible → E_GATE_SUSPENDED_BY_HOST), so the agent cannot tap its own
     * approval in the notification shade. The page forwards the card's token back to [RelayApprovalReceiver].
     */
    private fun confirm(requestId: Int, choice: InterlockChoice, token: String, title: String, text: String): PendingIntent {
        val intent = Intent()
            .setComponent(ComponentName(context.packageName, CONFIRM_ACTIVITY))
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            .putExtra(RelayApprovalReceiver.EXTRA_ID, requestId)
            .putExtra(RelayApprovalReceiver.EXTRA_CHOICE, choice.name)
            .putExtra(RelayApprovalReceiver.EXTRA_TOKEN, token)
            .putExtra("title", title)
            .putExtra("text", text)
            .putExtra("receiver", "legacy")
        return PendingIntent.getActivity(context, requestId * 8 + choice.ordinal, intent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
    }

    private fun deny(requestId: Int, token: String): PendingIntent {
        val intent = Intent(context, RelayApprovalReceiver::class.java)
            .setAction(RelayApprovalReceiver.ACTION)
            .putExtra(RelayApprovalReceiver.EXTRA_ID, requestId)
            .putExtra(RelayApprovalReceiver.EXTRA_CHOICE, InterlockChoice.DENY.name)
            .putExtra(RelayApprovalReceiver.EXTRA_TOKEN, token)
        // Distinct request code per (id, choice) so the three PendingIntents don't collapse into one.
        return PendingIntent.getBroadcast(context, requestId * 8 + InterlockChoice.DENY.ordinal, intent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
    }
}

private const val CONFIRM_ACTIVITY = "dshmobile.app.RelayApprovalConfirmActivity"

/**
 * Security fix (ADR-0008): one-time 128-bit answer tokens. Guest processes run under this app's UID and can run
 * /system/bin/am, so "not exported" alone does not keep a forged broadcast out of [RelayApprovalReceiver]; an answer
 * counts only with the token placed in the card's own PendingIntents, and each token answers once.
 */
internal object ApprovalTokens {
    private val tokens = ConcurrentHashMap<Int, String>()
    private val random = SecureRandom()

    fun issue(requestId: Int): String = tokens.computeIfAbsent(requestId) {
        ByteArray(16).also(random::nextBytes).joinToString("") { b -> "%02x".format(b) }
    }

    fun consume(requestId: Int, token: String?): Boolean {
        val expected = tokens[requestId] ?: return false
        if (token == null || !MessageDigest.isEqual(expected.toByteArray(), token.toByteArray())) return false
        return tokens.remove(requestId, expected)
    }

    fun revoke(requestId: Int) {
        tokens.remove(requestId)
    }

    fun clear() = tokens.clear()
}

/**
 * Receives the card's answers (deny directly, allow via the confirmation page). Not exported — but guest processes
 * share our UID, so an answer also needs the card's one-time token ([ApprovalTokens]).
 */
class RelayApprovalReceiver : BroadcastReceiver() {
    companion object {
        const val ACTION = "dshmobile.nativebridge.RELAY_APPROVAL"
        const val EXTRA_ID = "requestId"
        const val EXTRA_CHOICE = "choice"
        const val EXTRA_TOKEN = "token"
        private val ACCEPTED = setOf(InterlockChoice.ALLOW_ONCE, InterlockChoice.ALLOW_SESSION, InterlockChoice.DENY)
    }

    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != ACTION) return
        val id = intent.getIntExtra(EXTRA_ID, -1)
        val choice = runCatching { InterlockChoice.valueOf(intent.getStringExtra(EXTRA_CHOICE) ?: "") }.getOrNull()
        if (id < 0 || choice == null || choice !in ACCEPTED) return
        if (!ApprovalTokens.consume(id, intent.getStringExtra(EXTRA_TOKEN))) {
            Log.w("DshRelayApproval", "request $id: answer without a valid token dropped")
            return
        }
        val delivered = RelayRuntime.resolveApproval(choice, id)
        RelayRuntime.dismissApprovalNotification(id)
        context.getSystemService(NotificationManager::class.java)?.cancel("relay-approval", id)
        Log.i("DshRelayApproval", "request $id -> $choice (delivered=$delivered)")
    }
}
