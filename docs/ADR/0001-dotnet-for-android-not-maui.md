# ADR-0001：首版使用 .NET for Android 原生宿主，而非 MAUI

- 状态：已接受（2026-11-09）
- 来源：方案 1.1 节

## 背景

首版只面向 Android，且重度使用前台 Service、AccessibilityService、Shizuku、WebView、PTY、JNI。

## 决策

- App 使用 `net11.0-android` 原生 Activity / Fragment / AndroidX View，C# ViewModel/State。
- 领域与协议层写成纯 `net11.0` 类库（`DshMobile.Core` 等），可在桌面单测。
- 不引入 MAUI、Blazor Hybrid；不复用 DSHBox 的 Compose UI。
- 运行时使用 .NET 11 Android 默认 CoreCLR；不设置 `UseMonoRuntime`、`RunAOTCompilation`；`PublishAot=false`（NativeAOT 另行评估）。

## 后果

- 未来 Windows/iOS 前端需新增宿主项目，但协议层不动。
- 原生 View 代码量比 MAUI XAML 多，但生命周期与系统服务控制更直接。
