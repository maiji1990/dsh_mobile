// DSH Mobile native layer.
//
// Versions come straight from the pinned DSHBox submodule's catalog (single
// source of truth, see docs/ADR/0002). DSHBox modules are referenced in place
// and never modified; their build outputs are redirected (see build.gradle.kts).
pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

val dshboxDir = file("../third_party/dshbox")
require(dshboxDir.resolve("settings.gradle.kts").isFile) {
    "DSHBox submodule missing at $dshboxDir. Run: git submodule update --init --depth 1"
}

// DSHBox modules reference `$rootDir/libs/...` (prebuilt zstd-jni classes jar). Mirror that
// directory from the submodule so the unmodified build scripts resolve (native/libs is gitignored).
run {
    val from = dshboxDir.resolve("libs")
    val to = file("libs")
    from.listFiles()?.filter { it.isFile }?.forEach { src ->
        val dst = to.resolve(src.name)
        if (!dst.isFile || dst.length() != src.length()) {
            to.mkdirs()
            src.copyTo(dst, overwrite = true)
        }
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
    }
    versionCatalogs {
        create("libs") {
            from(files(dshboxDir.resolve("gradle/libs.versions.toml")))
        }
    }
}

rootProject.name = "dsh-mobile-native"

include(":native-bridge")

// Reused DSHBox modules, under the SAME project paths DSHBox uses (its scripts reference
// e.g. project(":common")). Add one at a time; audit transitive deps each time (ADR-0002).
include(":common")
project(":common").projectDir = dshboxDir.resolve("common")
include(":sandbox-manager")
project(":sandbox-manager").projectDir = dshboxDir.resolve("sandbox-manager")
// Pure-Kotlin WebUI policies (loopback origin policy, launch-token revision tracking). No UI, no deps.
include(":dsh-renderer:api")
project(":dsh-renderer").projectDir = dshboxDir.resolve("dsh-renderer")
project(":dsh-renderer:api").projectDir = dshboxDir.resolve("dsh-renderer/api")
