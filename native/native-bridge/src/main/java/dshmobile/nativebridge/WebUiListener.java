package dshmobile.nativebridge;

/**
 * WebUI state notifications for the .NET host (TASK-006). Called on the main thread.
 *
 * <p>{@code state} is one of: WAITING_TOKEN, LOADING, LOADED, UNAUTHORIZED, UNREACHABLE,
 * EXTERNAL_LINK, BLOCKED_LINK, FILE_CHOOSER_UNSUPPORTED, DOWNLOAD_UNSUPPORTED, DETACHED.
 * {@code detail} never contains the launch token or a query string (paths only).
 */
public interface WebUiListener {
    void onWebUiState(String state, String detail);
}
