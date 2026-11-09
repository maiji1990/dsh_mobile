import java.security.MessageDigest

plugins {
    alias(libs.plugins.android.library)
}

/** `git describe` of the pinned DSHBox submodule, baked into BuildConfig so bridge.info reports the real upstream. */
fun dshboxDescribe(): String = try {
    val proc = ProcessBuilder("git", "-C", rootDir.resolve("../third_party/dshbox").path, "describe", "--tags", "--long", "--always")
        .redirectErrorStream(true).start()
    val out = proc.inputStream.bufferedReader().readText().trim()
    if (proc.waitFor() == 0 && out.isNotEmpty()) out else "unknown"
} catch (e: Exception) {
    "unknown"
}

/** Copies exactly DSHBox's link shim files (unmodified) into a generated asset root; returns that root. */
fun stageDshboxShimAssets(): File {
    val from = rootDir.resolve("../third_party/dshbox/plugin-manager/dshbox-plugins/assets/dshbox")
    // Outside build/ so `clean` (which runs after configuration) cannot delete the staged copy.
    val root = rootDir.resolve(".generated/dshbox-shim-assets")
    val to = root.resolve("dshbox").apply { mkdirs() }
    listOf("link-shim.mjs", "libdshbox-link.so").forEach { name ->
        val src = from.resolve(name)
        check(src.isFile) { "DSHBox link shim asset missing: $src" }
        src.copyTo(to.resolve(name), overwrite = true)
    }
    return root
}

extensions.configure<com.android.build.api.dsl.LibraryExtension> {
    namespace = "dshmobile.nativebridge"
    compileSdk = 36

    defaultConfig {
        minSdk = 29
        consumerProguardFiles("consumer-rules.pro")
        buildConfigField("String", "BRIDGE_VERSION", "\"0.1.0\"")
        buildConfigField("String", "DSHBOX_UPSTREAM", "\"${dshboxDescribe()}\"")
    }

    buildFeatures {
        buildConfig = true
    }

    buildTypes {
        release {
            isMinifyEnabled = false
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    testOptions {
        unitTests.isReturnDefaultValues = true
    }

    // Assets shipped inside the AAR (merged into the .NET APK):
    //  - dshbox/link-shim.mjs + libdshbox-link.so: DSHBox's Android hard-link compat shim, taken unmodified
    //    from the pinned submodule (DSHBox puts them in its app assets). Required for DSH session persistence,
    //    write tool, attachments and plugin installs on Android, which refuses hard links.
    //  - dshmobile/: our own guest-side scripts (profile probes).
    //  Only these two files are taken — NOT DSHBox's assets/plugins/* (mobile-pilot, browser-use, …): phone
    //  automation must go through the single ui_* contract (AGENTS.md rule 6), never a parallel stack.
    sourceSets.getByName("main").assets.srcDir(stageDshboxShimAssets())
    sourceSets.getByName("main").assets.srcDir("src/main/guest-assets")
}

dependencies {
    implementation(project(":common"))
    implementation(project(":sandbox-manager"))
    implementation(libs.kotlinx.coroutines.android)
    testImplementation(libs.junit)
    // android.jar's org.json is a stub in JVM unit tests; use the real implementation.
    testImplementation("org.json:json:20250517")
}

// ---------------------------------------------------------------------------------------------
// Runtime dependency export for .NET (ADR-0002). Kotlin/AndroidX come from NuGet bindings; every
// other external jar is exported here and packaged by DshMobile.NativeBinding with Bind="false".
// An unknown group fails the build: new transitive deps must be license-audited and classified.
// ---------------------------------------------------------------------------------------------
val providedByNuGet = listOf("org.jetbrains.kotlin", "org.jetbrains.kotlinx", "org.jetbrains", "androidx.", "com.google.guava")
val exportedGroups = setOf(
    "org.apache.commons", "commons-io", "commons-codec", // commons-compress + its runtime deps
    "org.bouncycastle", // bcpg/bcprov/bcutil: Debian Release.gpg verification
    "org.tukaani", // xz
)

val exportRuntimeJars by tasks.registering {
    group = "dshmobile"
    description = "Export non-NuGet runtime jars + DSHBox prebuilt native libs to artifacts/native/."
    val runtimeClasspath = configurations.named("releaseRuntimeClasspath")
    val outDir = rootDir.resolve("../artifacts/native")
    val dshboxDir = rootDir.resolve("../third_party/dshbox")
    doLast {
        val jarsDir = outDir.resolve("jars").apply { deleteRecursively(); mkdirs() }
        val view = runtimeClasspath.get().incoming.artifactView {
            attributes { attribute(Attribute.of("artifactType", String::class.java), "jar") }
            componentFilter { it is org.gradle.api.artifacts.component.ModuleComponentIdentifier }
        }
        val manifest = StringBuilder("# coordinate  sha256  (exported jars; Kotlin/AndroidX come from NuGet)\n")
        val unknown = mutableListOf<String>()
        view.artifacts.artifacts.forEach { a ->
            val id = a.id.componentIdentifier as org.gradle.api.artifacts.component.ModuleComponentIdentifier
            val coord = "${id.group}:${id.module}:${id.version}"
            when {
                providedByNuGet.any { id.group == it || (it.endsWith(".") && id.group.startsWith(it)) } -> Unit
                id.group in exportedGroups -> {
                    val dst = jarsDir.resolve("${id.module}-${id.version}.jar")
                    a.file.copyTo(dst, overwrite = true)
                    manifest.append("$coord  ${sha256(dst)}\n")
                }
                else -> unknown += coord
            }
        }
        check(unknown.isEmpty()) { "Unclassified runtime dependencies (audit license, then add to exportedGroups or NuGet): $unknown" }
        // Note: :sandbox-manager's file dependency (zstd-jni classes jar) is already embedded in its AAR's
        // libs/, so it is NOT exported here (a second copy breaks the .NET build with XA1014).
        jarsDir.resolve("MANIFEST.txt").writeText(manifest.toString())

        // PRoot/talloc/shmem/zstd-jni prebuilt .so files shipped by DSHBox (GPL-2.0 / LGPL / BSD, see its licenses/).
        val libsOut = outDir.resolve("jniLibs").apply { deleteRecursively(); mkdirs() }
        val libsIn = dshboxDir.resolve("app/src/main/jniLibs")
        check(libsIn.isDirectory) { "DSHBox jniLibs not found at $libsIn" }
        libsIn.copyRecursively(libsOut, overwrite = true)
        logger.lifecycle("exported ${jarsDir.listFiles()!!.size - 1} jars and ${libsOut.walk().count { it.isFile }} native libs")
    }
}

fun sha256(f: File): String = MessageDigest.getInstance("SHA-256")
    .digest(f.readBytes()).joinToString("") { "%02x".format(it) }
