# The facade is called from .NET via JNI; keep its public surface.
-keep public class dshmobile.nativebridge.DshNativeFacade { public *; }
-keep public interface dshmobile.nativebridge.NativeCallback { *; }
# Called reflectively-free from Java, but keep the DSHBox Kotlin object used by the facade.
-keep class com.dshbox.app.common.Versions { *; }
