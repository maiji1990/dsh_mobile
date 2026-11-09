package dshmobile.nativebridge;

import android.os.Build;

/** Immutable device facts captured once at initialize(); injectable for JVM unit tests. */
final class PlatformInfo {
    final int sdkInt;
    final String[] abis;
    final String deviceModel;

    PlatformInfo(int sdkInt, String[] abis, String deviceModel) {
        this.sdkInt = sdkInt;
        this.abis = abis == null ? new String[0] : abis.clone();
        this.deviceModel = deviceModel == null ? "" : deviceModel;
    }

    static PlatformInfo fromDevice() {
        return new PlatformInfo(Build.VERSION.SDK_INT, Build.SUPPORTED_ABIS, Build.MODEL);
    }
}
