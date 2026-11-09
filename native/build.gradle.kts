plugins {
    alias(libs.plugins.android.application) apply false
    alias(libs.plugins.android.library) apply false
    alias(libs.plugins.kotlin.android) apply false
}

val upstreamRoot: File = rootDir.resolve("../third_party").canonicalFile

subprojects {
    // Keep the pinned submodule working tree clean: build upstream modules into native/build/upstream/<name>.
    if (projectDir.canonicalFile.startsWith(upstreamRoot)) {
        layout.buildDirectory.set(rootProject.layout.buildDirectory.dir("upstream/$name"))
    }
}

// Pin build-tools for every Android module (incl. unmodified DSHBox modules) instead of AGP's implicit default,
// so builds don't try to auto-install into a possibly read-only SDK. Override with -Pdshmobile.buildToolsVersion=...
val buildToolsPin = providers.gradleProperty("dshmobile.buildToolsVersion").get()
subprojects {
    plugins.withId("com.android.library") {
        extensions.configure<com.android.build.api.dsl.LibraryExtension> {
            buildToolsVersion = buildToolsPin
        }
    }
}

// Same Kotlin language/API level DSHBox's own root build enforces.
subprojects {
    tasks.withType<org.jetbrains.kotlin.gradle.tasks.KotlinCompile>().configureEach {
        compilerOptions {
            languageVersion.set(org.jetbrains.kotlin.gradle.dsl.KotlinVersion.KOTLIN_2_2)
            apiVersion.set(org.jetbrains.kotlin.gradle.dsl.KotlinVersion.KOTLIN_2_2)
        }
    }
}

val exportDir = rootDir.resolve("../artifacts/native")

/** Copies release AARs to artifacts/native/ under stable names and writes SHA-256 sidecars. */
val exportAars by tasks.registering {
    group = "dshmobile"
    description = "Build release AARs and export them to artifacts/native/."
    dependsOn(":native-bridge:assembleRelease", ":dshbox-common:assembleRelease")
    val outputs = mapOf(
        "dsh-native-bridge-release.aar" to project(":native-bridge").layout.buildDirectory.file("outputs/aar/native-bridge-release.aar"),
        "dshbox-common-release.aar" to project(":dshbox-common").layout.buildDirectory.file("outputs/aar/dshbox-common-release.aar"),
    )
    doLast {
        exportDir.mkdirs()
        val manifest = StringBuilder()
        outputs.forEach { (name, src) ->
            val from = src.get().asFile
            check(from.isFile) { "Expected AAR not found: $from" }
            val to = exportDir.resolve(name)
            from.copyTo(to, overwrite = true)
            val sha = java.security.MessageDigest.getInstance("SHA-256")
                .digest(to.readBytes()).joinToString("") { "%02x".format(it) }
            exportDir.resolve("$name.sha256").writeText("$sha  $name\n")
            manifest.append("$sha  $name\n")
            logger.lifecycle("exported $name  sha256=$sha")
        }
        exportDir.resolve("SHA256SUMS.txt").writeText(manifest.toString())
    }
}
