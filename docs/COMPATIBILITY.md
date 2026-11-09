# 兼容性（COMPATIBILITY）

## 设备边界（V1）

- `minSdk 29`（Android 10），与 DSHBox `common` / `sandbox-manager` 的 `minSdk = 29` 一致。
- ABI：`android-arm64` 主目标；`android-x64` 仅用于模拟器开发冒烟，不保证运行层可用。
- 主测试目标：Android 14 / 16 / 17 真机。

## 工具链锁定（2026-11-09）

| 组件 | 版本 | 来源 |
|---|---|---|
| .NET SDK | `11.0.100-rc.2.26504.105` | `global.json`（`rollForward: disable`） |
| .NET workload set | `11.0.100-rc.2.26513.2`（android `37.2.0-rc.2.84`） | `global.json` `workloadVersion` |
| JDK | Temurin 21.0.12 | 本机 `JAVA_HOME` |
| Gradle | 9.5.0 | `native/gradle/wrapper/gradle-wrapper.properties` |
| AGP / Kotlin | 9.3.1 / 2.4.20 | `third_party/dshbox/gradle/libs.versions.toml` |
| Android compileSdk | 36（native-bridge / DSHBox 模块）；.NET App 使用 workload 默认 API 37（targetSdk 37 = Android 17） | |
| Android build-tools | 37.0.0（`native/gradle.properties` `dshmobile.buildToolsVersion`，覆盖 AGP 9.3.1 默认的 36.0.0） | |
| .NET Android 运行时 | CoreCLR（APK 内 `libcoreclr.so` / `libclrjit.so`，已核对） | |
| Xamarin.Kotlin.StdLib | 2.4.0.1 | `src/DshMobile.NativeBinding` |

> 方案文档基线写的是 .NET 11 **RC3**（2026-10-25）；本机当前仅安装 **RC2**。按方案“GA 后做独立升级 PR”的要求，升级到 RC3 / GA 时修改 `global.json` 并重新跑基线，单独提交。

## 真机矩阵

| 设备 / 系统 | Bridge POC | Runtime | DSH profiles | 备注 |
|---|---|---|---|---|
| Android 10 / ARM64 | 待测 | — | — | |
| Android 14 / ARM64 | 待测 | — | — | |
| Android 16 / ARM64 | 待测 | — | — | |
| Android 17 / ARM64 | 待测 | — | — | |
| x86_64 模拟器 | 待测 | 不保证 | 不保证 | |
