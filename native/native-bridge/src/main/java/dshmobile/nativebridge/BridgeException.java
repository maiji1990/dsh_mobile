package dshmobile.nativebridge;

/** A request failure with a stable protocol error code (see docs/PROVIDER_CONTRACTS.md 1.5). */
final class BridgeException extends Exception {
    final String code;

    BridgeException(String code, String message) {
        super(message);
        this.code = code;
    }
}
