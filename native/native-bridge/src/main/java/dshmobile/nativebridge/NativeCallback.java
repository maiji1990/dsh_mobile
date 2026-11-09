package dshmobile.nativebridge;

/**
 * Receives bridge messages as JSON (protocol v1). Implemented on the .NET side.
 *
 * <p>Always invoked on a bridge worker thread, never on the thread that called
 * {@link DshNativeFacade#execute}. Implementations must not block.
 */
public interface NativeCallback {
    void onEvent(String json);
}
