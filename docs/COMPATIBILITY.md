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
| Android 17 / ARM64 | **通过（见下）**，旋转未测 | **通过**（DSH 0.2.1-rc.5 READY） | web 就绪；其余未测 | Honor PTP-AN10，厂商 ROM `11.0.0.117`，4 KB 页，2026-11-09 |
| x86_64 模拟器 | 待测 | 不保证 | 不保证 | |

## 真机记录

### 2026-11-09 · Honor PTP-AN10 · Android 17 (API 37) · arm64-v8a · ROM `PTP-AN10 11.0.0.117(SP2C00E117R102P1)`

构建：Release（full trim，CoreCLR），`org.example.dshmobile` 0.1.0；Bridge 0.1.0，DSHBox `v1.5.1-0-g7c9d33c`。

| TEST_PLAN M1 步骤 | 结果 |
|---|---|
| 1. 启动 + `bridge.info` | 通过：冷启动 287 / 225 / 219 ms，热启动 42 ms；`sdk=37 abis=[arm64-v8a]`；Kotlin 运行时 `2.4.0`（NuGet `Xamarin.Kotlin.StdLib` 提供；编译用 2.4.20、apiVersion 2.2，兼容） |
| 2. DSHBox Kotlin 调用 | 通过：`Versions.compare` → `("0.1.1","0.1.1-rc.2")=1`、`("0.1.0-rc.9","0.1.0-rc.10")=-1`、`("v1.5.1","1.5.1")=0`，与 DSHBox 语义一致 |
| 3. 1000 次压测 | 通过：31 轮全部 `ok=1000 failed=0`，单轮平均约 22 ms（最大 32 ms）；每轮结束 `inFlight C#=0 native=0 orphans=0 malformed=0` |
| 4a. 压测中切后台再回来 | 通过：结果完整，计数一致，无异常 |
| 4b. 压测中旋转屏幕 | **未测**：需手动旋转（未通过修改系统旋转设置来模拟） |
| 5. 后台被杀后重开 | 通过：`am kill` 后进程消失，冷启动 219 ms，Facade 重新初始化并正常响应 |
| 6. 内存（30 轮，每轮强制双端 GC 后测量） | 通过：JNI 全局引用恒为 **37**；Java 存活约 2.6 MB 持平；托管堆 727→810 KiB（与应用内日志缓冲增长一致，上限 400 行）。未强制 GC 时 `dumpsys` 的 Java Heap 会随轮次上涨，属 GC 延迟，不应据此判断泄漏 |
| 回调线程 | 通过：logcat 显示回调在 Bridge 工作线程（非主线程） |

备注：
- 该 ROM 首次 USB 安装需在手机上手动确认“来历不明的应用”提示；覆盖安装未再弹出。
- 该 ROM 的 logcat 环形缓冲很快被冲掉；长压测请把 `adb logcat -s DshMobile:I` 持续写入文件，而不是事后 `logcat -d`。
- 发现并修复：根布局 `fitsSystemWindows` 覆盖了 16dp 内边距（已改为外层包裹）。

### 2026-11-09 · M2 运行时 · 同一设备

APK 110 MB（含 DSH 层 93 MB + android-side 层）；运行环境包 `dshapp-runtime-debian-arm64-0.1.0.zip`（202 MB）经 `adb push` 到 App 专属外部目录后导入。

| 步骤 | 结果 |
|---|---|
| `runtime.prepare`（全新安装） | 通过：APK 无完整分层包（预期），提示需导入 base/node |
| `runtime.importBundle` | 首次失败：`BUNDLE_IMPORT_FAILED … base/var/run: The source file doesn't exist`（上游 `swapRuntimeBody` 在 `runtime-current/` 不存在时的回退路径问题）。调用前创建该目录后通过：**12.0 s**，`runtime=True node=True`，DSH 0.2.1-rc.5 装配 |
| `sandbox.start` | 通过：7 ms |
| `dsh.start` | 通过：**18.5 s / 19.0 s** 到 READY，launch token 已解析，`http://127.0.0.1:3080` |
| `runtime.health` | 通过：process / port / webUi 全 true |
| `runtime.diagnostics`（guest 内真实执行） | `aarch64`、Debian `13.6`、Node `v24.19.0`、`dsh 0.2.1-rc.5` |
| 进程树 | App → 2×`libproot.so` → `bash`(keepalive) / `node`(DSH，RSS ≈ 385 MB)；App 进程 RSS ≈ 350 MB |
| 后台 90 s | 通过：进程全部存活，返回后健康与 `dsh --version` 正常；前台服务 `types=0x40000000`(specialUse) |
| 停止（dsh.stop → sandbox.stop） | 通过：仅剩 App 进程，无孤儿 |
| `am force-stop` | 通过：App UID 下零进程，无孤儿 |

备注：`libproot.so` 非 16 KB 页对齐（构建警告 XA0141），本机为 4 KB 页不受影响。

### 2026-11-09 · TASK-001 DSHBox 原生基线 · 同一设备

构建：DSHBox `v1.5.1`（`7c9d33c`，源码未改）`:app:assembleSystemWebViewRelease`，未配置 release keystore（回退 debug 签名）。
APK SHA-256 `a084dc5fb2559bea84d04caef8050b63f8006f23f7dc8dd639f08c41b9a2a392`，130.6 MB（官方 Release `dshbox-v1.5.1-system-release.apk` 同为 130.6 MB）。
构建前置：SDK `platforms;android-37.2`、`build-tools;36.0.0`（`tooling/install-android-sdk-components.ps1`）；`ndk-build` 不接受含空格的 NDK 路径，需在 clone 的 `local.properties` 设 `ndk.dir` 指向无空格路径（目录联接即可）。
运行环境：与 DSH Mobile 相同的 `dshapp-runtime-debian-arm64-0.1.0.zip`，由用户在 DSHBox「设置 → 离线导入运行环境包」手动导入并手动启动（DSHBox release 构建剥离了自身日志，导入/启动耗时无法从日志取得）。

| 指标 | DSHBox v1.5.1（基线） | DSH Mobile（本项目，M2） | 备注 |
|---|---|---|---|
| APK 大小 | 130.6 MB（含 Compose UI、Browser、Pilot 等） | 110.4 MB（含 DSH 层 93 MB） | 功能范围不同，仅作参考 |
| 冷启动 | 250 ms | 219–315 ms | `am start -W` TotalTime |
| 运行环境导入 | 成功；耗时未测（手动、无日志） | 12.0 s | |
| DSH 启动到就绪 | 成功（「DSH 就绪」）；耗时未测 | 18.5–19.0 s | |
| DSH 端口 | `GET /` → HTTP 401（launch token 鉴权生效） | 健康探测 webUi=true | 同一 DSH 0.2.1-rc.5 |
| 进程树 | App → 2×`libproot.so` → `bash` + `node` | 相同 | |
| 内存（ps RSS） | App 506 MB、node 282 MB（DSH 运行约 9 min） | App 349 MB、node 385 MB（DSH 运行约 1 min） | 采样时刻不同，不可直接比；DSHBox App PSS 161.7 MB（Java 堆 30.7 MB） |

结论：DSHBox 源码在本机可复现构建并在该设备上跑通同一运行层与 DSH 版本，作为 DSH Mobile 的对照基线成立。待补：两者在同一时间点、同一空闲时长下的 PSS 对比；DSHBox 导入/启动的精确计时（需 debug 构建或界面计时）。
