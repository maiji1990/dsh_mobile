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
}

dependencies {
    implementation(project(":dshbox-common"))
    testImplementation(libs.junit)
    // android.jar's org.json is a stub in JVM unit tests; use the real implementation.
    testImplementation("org.json:json:20250517")
}
