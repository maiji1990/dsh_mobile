package dshmobile.nativebridge;

/** Terminal session list changes (TASK-008). Called on the main thread. */
public interface TerminalListener {
    /**
     * {@code json} is an array of {@code {"id","title","running","exited","exitCode","active"}}; it carries no
     * terminal content.
     */
    void onTerminalSessionsChanged(String json);
}
