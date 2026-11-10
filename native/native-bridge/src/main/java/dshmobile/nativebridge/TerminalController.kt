package dshmobile.nativebridge

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.graphics.Typeface
import android.util.Log
import android.util.TypedValue
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.View
import android.view.inputmethod.InputMethodManager
import com.dshbox.app.sandbox.SandboxConfig
import com.dshbox.terminal.DshTerminalManager
import com.dshbox.terminal.TerminalPathsResolver
import com.termux.terminal.TerminalSession
import com.termux.terminal.TerminalSessionClient
import com.termux.view.TerminalView
import com.termux.view.TerminalViewClient
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.util.concurrent.Executors

/**
 * Terminal (TASK-008): DSHBox's unmodified `terminal-session` (PRoot shell sessions, multi-window
 * bookkeeping) + Termux `terminal-view` / `terminal-emulator`, hosted in a .NET Activity.
 *
 * PTY output goes straight from the native emulator to the native View; nothing terminal-related crosses
 * the JSON control plane (AGENTS.md: high-throughput data never goes through `execute`).
 * Sessions live at process scope (they survive the Activity); the Activity only attaches/detaches a View.
 * All methods run on the main thread (DshTerminalManager binds to the constructing thread's Looper).
 */
internal class TerminalController(
    private val appContext: Context,
    private val config: SandboxConfig,
    private val scope: CoroutineScope,
) {
    companion object {
        private const val TAG = "DshTerminal"
        private const val DEFAULT_TEXT_SP = 13
        private const val MIN_TEXT_SP = 8
        private const val MAX_TEXT_SP = 32

        /** Extra-keys bar names -> Android key codes (TerminalView maps them with the emulator's modes). */
        private val KEYS = mapOf(
            "ESC" to KeyEvent.KEYCODE_ESCAPE, "TAB" to KeyEvent.KEYCODE_TAB,
            "UP" to KeyEvent.KEYCODE_DPAD_UP, "DOWN" to KeyEvent.KEYCODE_DPAD_DOWN,
            "LEFT" to KeyEvent.KEYCODE_DPAD_LEFT, "RIGHT" to KeyEvent.KEYCODE_DPAD_RIGHT,
            "HOME" to KeyEvent.KEYCODE_MOVE_HOME, "END" to KeyEvent.KEYCODE_MOVE_END,
            "PGUP" to KeyEvent.KEYCODE_PAGE_UP, "PGDN" to KeyEvent.KEYCODE_PAGE_DOWN,
        )
    }

    private val manager = DshTerminalManager(
        pathsProvider = { TerminalPathsResolver.resolve(config.appFilesDir, config.nativeLibraryDir) },
        titleFormatter = { kind, order -> if (kind == DshTerminalManager.Kind.SANDBOX) "终端 $order" else "受限 $order" },
    )
    private val clipboard = appContext.getSystemService(ClipboardManager::class.java)
    private val pasteExecutor = Executors.newSingleThreadExecutor { r -> Thread(r, "terminal-paste").apply { isDaemon = true } }
    private val sessionClient = SessionClient()

    private var view: TerminalView? = null
    private var listener: TerminalListener? = null
    private var watchJob: Job? = null
    private var textSp = DEFAULT_TEXT_SP

    @Volatile private var ctrlLatched = false

    /** Creates a View bound to the active session (creating the first one). Null if no runtime is installed. */
    fun createView(activityContext: Context, l: TerminalListener?): View? {
        releaseView()
        if (!manager.runtimeAvailable()) return null
        provisionGuestUser()
        listener = l
        val v = TerminalView(activityContext, null)
        v.setTerminalViewClient(ViewClient(v))
        // setTextSize() creates the renderer; setTypeface() reads it, so the order matters.
        v.setTextSize(spToPx(textSp))
        v.setTypeface(Typeface.MONOSPACE)
        v.isFocusable = true
        v.isFocusableInTouchMode = true
        view = v
        manager.rebindClient(sessionClient)
        if (manager.activeId.value == null) newSession()
        attachActive()
        watchJob = scope.launch(Dispatchers.Main) {
            combine(manager.sessions, manager.activeId) { list, active -> list to active }.collect { (list, active) ->
                attachActive()
                runCatching { listener?.onTerminalSessionsChanged(toJson(list, active)) }
            }
        }
        return v
    }

    fun releaseView() {
        watchJob?.cancel()
        watchJob = null
        view = null
        listener = null
        ctrlLatched = false
    }

    fun newSession(): String? {
        provisionGuestUser()
        val ui = manager.newSandboxSession(sessionClient) ?: return null
        manager.activate(ui.id)
        return ui.id
    }

    fun activate(id: String) = manager.activate(id)

    fun close(id: String) = manager.closeSession(id)

    fun sessionsJson(): String = toJson(manager.sessions.value, manager.activeId.value)

    /** Sends one extra-keys bar key (ESC, TAB, arrows, HOME/END, PGUP/PGDN) or CTRL_C to the active session. */
    fun sendKey(name: String): Boolean {
        val v = view ?: return false
        if (name == "CTRL_C") {
            v.currentSession?.write(byteArrayOf(3), 0, 1) ?: return false
            return true
        }
        val code = KEYS[name] ?: return false
        v.onKeyDown(code, KeyEvent(KeyEvent.ACTION_DOWN, code))
        v.onKeyUp(code, KeyEvent(KeyEvent.ACTION_UP, code))
        return true
    }

    /** Latches Ctrl for the next typed letter (^A..^Z), like DSHBox's extra-keys Ctrl. */
    fun setCtrl(on: Boolean) {
        ctrlLatched = on
    }

    fun paste() {
        val target = view?.currentSession ?: return
        val text = runCatching { clipboard?.primaryClip?.getItemAt(0)?.text?.toString() }.getOrNull() ?: return
        pasteExecutor.execute {
            // Emulator.paste honors bracketed-paste mode and strips control bytes (same as DSHBox).
            target.emulator?.paste(text) ?: target.write(text)
        }
    }

    fun stopAll() = manager.stopAll()

    // ------------------------------------------------------------------ internals

    private fun attachActive() {
        val v = view ?: return
        val id = manager.activeId.value ?: return
        val session = manager.sessionById(id) ?: return
        if (v.currentSession !== session) {
            v.attachSession(session)
            v.onScreenUpdated()
        }
    }

    private fun provisionGuestUser() {
        val base = File(config.runtimeDir, "runtime-current/base")
        val outcome = runCatching { GuestUser.provision(base) }.getOrDefault(GuestUser.Outcome.SKIPPED)
        if (outcome == GuestUser.Outcome.UPDATED) Log.i(TAG, "guest passwd entry written for ${GuestUser.USER_NAME}")
    }

    private fun spToPx(sp: Int): Int =
        TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_SP, sp.toFloat(), appContext.resources.displayMetrics).toInt()

    private fun toJson(list: List<DshTerminalManager.SessionUi>, active: String?): String {
        val arr = JSONArray()
        list.forEach {
            arr.put(JSONObject()
                .put("id", it.id).put("title", it.displayTitle).put("running", it.running)
                .put("exited", it.exited).put("exitCode", it.exitCode).put("active", it.id == active))
        }
        return arr.toString()
    }

    private inner class SessionClient : TerminalSessionClient {
        override fun onTextChanged(changedSession: TerminalSession) {
            view?.takeIf { it.currentSession === changedSession }?.onScreenUpdated()
        }
        override fun onTitleChanged(changedSession: TerminalSession) = Unit
        override fun onSessionFinished(finishedSession: TerminalSession) = manager.notifySessionFinished(finishedSession)
        override fun onCopyTextToClipboard(session: TerminalSession, text: String) {
            clipboard?.setPrimaryClip(ClipData.newPlainText("terminal", text))
        }
        override fun onPasteTextFromClipboard(session: TerminalSession?) = paste()
        override fun onBell(session: TerminalSession) = Unit
        override fun onColorsChanged(session: TerminalSession) { view?.invalidate() }
        override fun onTerminalCursorStateChange(state: Boolean) = Unit
        override fun setTerminalShellPid(session: TerminalSession, pid: Int) = Unit
        override fun getTerminalCursorStyle(): Int? = null
        override fun logError(tag: String, message: String) { Log.e("term-$tag", message) }
        override fun logWarn(tag: String, message: String) { Log.w("term-$tag", message) }
        override fun logInfo(tag: String, message: String) { Log.i("term-$tag", message) }
        override fun logDebug(tag: String, message: String) = Unit
        override fun logVerbose(tag: String, message: String) = Unit
        override fun logStackTraceWithMessage(tag: String, message: String, e: Exception) { Log.e("term-$tag", message, e) }
        override fun logStackTrace(tag: String, e: Exception) { Log.e("term-$tag", "stack trace", e) }
    }

    /** Mirrors DSHBox's DshTerminalViewClient: tap = focus + keyboard, pinch = font size, latched Ctrl. */
    private inner class ViewClient(private val v: TerminalView) : TerminalViewClient {
        override fun onScale(scale: Float): Float =
            if (scale < 0.9f || scale > 1.1f) {
                textSp = (textSp + if (scale > 1f) 2 else -2).coerceIn(MIN_TEXT_SP, MAX_TEXT_SP)
                v.setTextSize(spToPx(textSp))
                1f
            } else {
                scale
            }
        override fun onSingleTapUp(e: MotionEvent) {
            v.requestFocus()
            appContext.getSystemService(InputMethodManager::class.java)?.showSoftInput(v, InputMethodManager.SHOW_IMPLICIT)
        }
        override fun shouldBackButtonBeMappedToEscape(): Boolean = false
        override fun shouldEnforceCharBasedInput(): Boolean = false
        override fun shouldUseCtrlSpaceWorkaround(): Boolean = false
        override fun isTerminalViewSelected(): Boolean = true
        override fun copyModeChanged(copyMode: Boolean) = Unit
        override fun onKeyDown(keyCode: Int, e: KeyEvent, session: TerminalSession?): Boolean = false
        override fun onKeyUp(keyCode: Int, e: KeyEvent): Boolean = false
        override fun onLongPress(event: MotionEvent): Boolean = false
        override fun readControlKey(): Boolean = ctrlLatched
        override fun readAltKey(): Boolean = false
        override fun readShiftKey(): Boolean = false
        override fun readFnKey(): Boolean = false
        override fun onCodePoint(codePoint: Int, ctrlDown: Boolean, session: TerminalSession?): Boolean {
            if (!ctrlDown && !ctrlLatched) return false
            val c = Character.toLowerCase(codePoint)
            if (c in 'a'.code..'z'.code) {
                session?.writeCodePoint(false, c - 'a'.code + 1)
                ctrlLatched = false // one-shot, like a modifier key
                return true
            }
            return false
        }
        override fun onEmulatorSet() = Unit
        override fun logError(tag: String, message: String) { Log.e("termview-$tag", message) }
        override fun logWarn(tag: String, message: String) { Log.w("termview-$tag", message) }
        override fun logInfo(tag: String, message: String) { Log.i("termview-$tag", message) }
        override fun logDebug(tag: String, message: String) = Unit
        override fun logVerbose(tag: String, message: String) = Unit
        override fun logStackTraceWithMessage(tag: String, message: String, e: Exception) { Log.e("termview-$tag", message, e) }
        override fun logStackTrace(tag: String, e: Exception) { Log.e("termview-$tag", "stack trace", e) }
    }
}
