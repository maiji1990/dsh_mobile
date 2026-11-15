# ADR-0017：其余切片 C# 默认、旧门面懒加载、guest 资产由 C# 供给、Shizuku 直接依赖

- 状态：已接受（2026-11-15；项目负责人决定：「按顺序1234全部，一次性做完，做完以后虚拟机测试」，四项即下文决策 1–4）
- 关联：[ADR-0007](0007-source-ownership-csharp-first.md)（C#-first）、[ADR-0014](0014-in-repo-proot-build.md)（自建 PRoot）、
  [ADR-0015](0015-csharp-host-default.md)（C# 宿主默认）、[ADR-0016](0016-csharp-default-all-shadow-slices.md)（影子切片 C# 默认、逐次回退），
  `docs/MIGRATION.md` 切片总表、[模拟器证据](../../migration/parity/2026-11-15-emulator-A17-x86_64-remaining-slices.md)

## 背景

ADR-0016 之后还有三类东西在默认路径上依赖 DSHBox / 旧桥：

1. 没有影子、因而没被 ADR-0016 翻转的切片：解包（`LayerExtract` 仍为影子开关）、回滚（未建）、运行时**状态来源**（C# 宿主管进程，
   但状态仍读旧 `runtime.status` / 事件）、DSH profile 探测 / 健康 / 事件、WebView 策略（未接入宿主不持有进程时的 WebView）、
   插件安装 / 卸载（C# 可选，旧桥默认）。
2. 默认路径上的旧桥调用：`bridge.info`、版本比较、导入后的 `runtime.status`、`dsh.probe`、插件 add/remove、`runtime.state`
   事件 / 状态行、远程页的旧 `remote.status` / `remote.disconnect`，以及**启动时** `DshNativeFacade.initialize`
   （构造 `RuntimeController` + DSHBox sandbox-manager）。
3. 隐式依赖：旧 `provisionGuestAssets` 在每个进程第一次旧桥调用时写 guest 需要的宿主文件（链接垫片、探针脚本、corepack 缓存、
   `dsh` / `pnpm` 包装）。默认路径不再调用旧桥后，C# 的所有 guest 路径都会缺这些文件。
4. Shizuku 客户端库（`dev.rikka.shizuku`，MIT）只经 DSHBox interlock-relay-core 的 Gradle 导出打进 APK，而 C# Relay 的
   Shizuku 后端直接用它；PR-10 删 AAR 时会一起消失。

## 决策

1. **其余切片 C# 默认**（显式回退仍在，回退时影子照常运行，同 ADR-0016）：
   - 解包：`MigrationOptions.LayerExtract` 默认 `CSharp`（C# 导入用 `LayerExtractor`；旧导入回退后仍干跑比对，`Legacy` 关闭比对）。
   - 回滚：新建 `LayerRollback`（移植 `BundleManager.rollback` 规则）+ 协调器 `RollbackAsync`；入口为长按「导入」→「回滚到上一版」→ 确认。
     运行时须已停止（宿主不持有进程、本进程未经回退启动旧运行时、状态非 Running / Starting / Stopping），否则拒绝；无上一版如实报
     `BUNDLE_NO_ROLLBACK_SLOT`；换回后经 `RuntimeInstallCheck` 核对，不通过记 `RUNTIME_ROLLBACK_UNVERIFIED`。旧实现无用户可触发的回滚，故无回退。
   - 状态来源：`RuntimeStatusReader` 从磁盘读安装事实（安装检查含哨兵自愈、node 层、DSH 层版本、链接垫片、可用空间），进程状态以 C# 宿主为准；
     协调器同步不再读旧 `runtime.status`，旧 `runtime.state` 事件被忽略。**只有**本进程经长按回退启动过旧运行时（`LegacyRuntimeActive`，
     停止成功或清理成功后复位）时才由旧实现决定状态。启动时后台刷新一次 journal。
   - DSH 探测：`DshProbe` 经 C# PRoot 启动器运行同一 `dsh-probe.mjs`（argv 逐 token 引号，规则 11），取最后一行 `DSHPROBE` JSON；
     无结果 `PROBE_NO_RESULT`、JSON 坏 `PROBE_MALFORMED`，不伪造通过。诊断的健康行由 C# `HttpHealthProbe` 给出。长按「探测」→ 旧 `dsh.probe`。
   - WebView：宿主不持有进程且未经回退启动旧运行时时也用 C# WebView（无 token 时 `WAITING_TOKEN`）。
   - 插件安装 / 卸载：新增 `MigrationOptions.PluginChange` 默认 `CSharp`（带安装守卫：非插件包回滚）；长按 = 旧 `plugin.add/remove`，结果标「（旧实现）」。
2. **默认路径不调用旧桥；旧门面懒加载**。`BridgeHost` 构造时不再 `Initialize` 旧门面；`NativeFacadeTransport` 在第一次 `Send`
   前调用 `EnsureLegacy()`：在主线程初始化（后台调用方经主线程等待），日志 `legacy facade initialized on first use`。
   「信息」「版本比较」默认 C#（长按 = 旧 `bridge.info` / 对照 Kotlin）；导入后状态、诊断状态 / 健康由 C# 给出；远程页只有本进程
   经回退建立过旧 JSch 连接（`CsRemoteConnection.LegacyFallbackActive`）时才调用旧 `remote.*`；旧终端 / WebUI 收尾只在用过时执行。
   「旧桥 1000 次压测」按钮本身就是测旧桥，保留并改名标明。
3. **guest 资产由 C# 供给**（`GuestAssetProvisioner.EnsureAsync`）：链接垫片 `.mjs` / `.so`（APK 资产 → `files/dshbox`）、
   `dsh-probe.mjs`（本仓库源文件嵌入程序集 → `guest-cache/dshmobile`）、`ui_*` 脚本（`UiFacadeAssets`）、corepack 缓存
   （`GuestToolchain.ProvisionCorepackAsync`：先核对 `.sha256` 旁车，再以 C# `LayerExtractor` 解到私有 staging 后改名就位）、
   `dsh` / `pnpm` 包装（与 Kotlin 文本逐字节相同）。内容不同才写、临时文件改名；各步独立，失败逐条报告不中断其它步。
   终端、诊断、插件、探测、Relay 自检、C# 宿主启动之前都会先确保一次。`browser-check.mjs` 不供给（浏览器不在迁移范围）。
4. **Shizuku 改为 App 直接依赖**：`DshMobile.Android` 以 `AndroidMavenLibrary` 声明 `dev.rikka.shizuku:{api,provider,shared,aidl}` 13.1.5
   （`Bind="false"`，C# 仍经 JNI 按类名调用），补 `Xamarin.AndroidX.Annotation` 1.11.0（XA4242）；`DshMobile.NativeBinding` 不再收
   `artifacts/native/aars/*.aar`；`native-bridge` 的 Gradle 导出把该组列为 `providedByApp`、不再导出。旧 Relay（回退）用同一份类。

## 不变 / 不做

- 不改上游 AgentLoop / Session Log / Cordis core；不删 DSHBox 子模块、AAR、`DshNativeFacade`（所有 PR 完成后统一移除）。
- 权限审批、无障碍写门禁、SSH 主机密钥固定、不可导出私钥均不变；guest 命令一律 argv + 逐 token 引号。
- 旧 Relay 的清单组件、门面 / 绑定、`DshProotSource=dshbox` 开关保留为回退，PR-10 处理。
- 浏览器（`browser_*`）不迁移，仍走旧实现。

## 后果

- 冷启动不再构造 DSHBox sandbox-manager；不点任何长按回退时整个进程不初始化旧门面（模拟器全流程验证）。
- 回滚槽位只在导入时 `runtime-current/previous/` 存在才产生（旧 `swapRuntimeBody` 不创建它，见 MIGRATION 第 7 节），所以经旧导入后
  回滚通常报 `BUNDLE_NO_ROLLBACK_SLOT`——这是如实结果，不是缺陷。
- APK 资产 `dshbox/link-shim.mjs`、`libdshbox-link.so`、`toolchain-cache/*` 目前仍由 Bridge AAR 打进 APK；生产代码只经 `AssetManager`
  读取，不读 `third_party/`。PR-10 需把这些资产改由 App 自己打包。
- `artifacts/native/aars/` 在下次运行 `native/build-native.*` 前仍是旧产物，但已无项目引用。
- 风险与 ADR-0016 相同：先于真机验收翻转；真机验收逐切片补齐，回退入口保留。
