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

// Reused DSHBox modules (add one at a time; audit transitive deps each time).
include(":dshbox-common")
project(":dshbox-common").projectDir = dshboxDir.resolve("common")
