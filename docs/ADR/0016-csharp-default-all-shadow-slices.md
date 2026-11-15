# ADR-0016：所有影子比对切片一次性改为 C# 默认

- 状态：已接受（2026-11-15；项目负责人决定：「把所有的影子对比切片都改为 c# 默认，一次性改完，改完之后再模拟器测试」）
- 关联：[ADR-0007](0007-source-ownership-csharp-first.md)（C#-first、规则 3「先影子、真机验收后才默认」）、
  [ADR-0010](0010-relay-guest-switch.md)（C# Relay 接管 guest 信箱）、[ADR-0012](0012-csharp-ssh-client-sshnet.md)、
  [ADR-0013](0013-remote-dsh-start-stop-shadow.md)、[ADR-0015](0015-csharp-host-default.md)（C# 宿主默认）、
  `docs/MIGRATION.md` 切片总表、[模拟器证据](../../migration/parity/2026-11-15-emulator-A17-x86_64-csharp-default-all.md)

## 背景

到 PR #30 为止，除启动路径（ADR-0015）外，每个已有 C# 实现的切片都以 `SliceMode.LegacyWithShadow` 运行：旧桥决策，
C# 只读比对并写 `shadow-reports.jsonl`；C# 实现只能经长按 / 选择框逐次选择。ADR-0007 规则 3 要求每个切片单独通过真机验收后
再翻默认，但项目负责人决定**先一次性翻转全部影子切片的默认**，真机验收之后逐切片补齐（目前暂缓真机验证）。
这也让 DSHBox 的移除（所有 PR 完成后统一进行）可以先在默认路径上暴露 C# 实现的缺口。

## 决策

1. **默认翻转**。`MigrationOptions` 中所有曾为 `LegacyWithShadow` 的影子切片默认改为 `SliceMode.CSharp`：
   `BundleImport`、`DshProvision`、`GuestExec`、`PluginList`、`UiTools`、`RelayEnvelopeCodec`、`RelayAdmission`、
   `RelaySurface`、`RelayGate`、`RelayA11yRead`、`RelayA11yWaitFor`、`RelayHost`、`RemoteHosts`、`RemoteConnect`、`RemoteDsh`，
   新增 `Terminal`（默认 C#）。`RuntimeHost` 已为 C#（ADR-0015）。`LayerExtract` 保持 `LegacyWithShadow`：它只是旧导入
   （回退）之后的解包影子，C# 导入自己装后核对（`runtime.layer-install`）。
2. **旧实现保留为显式逐次回退**，入口与 ADR-0015 一致：长按「准备」「导入」「终端」「开启中继」「连接」弹出选择框，
   选「旧实现（回退）」走旧桥。DSHBox AAR、`DshNativeFacade` 与子模块不动，所有 PR 完成后统一移除。
3. **回退路径上影子照常运行**。影子开关的判定由「`== LegacyWithShadow` 才比对」改为「仅 `== Legacy` 时跳过」：
   旧实现（回退）跑完后，C# 仍按原影子比对并写报告，影子报告继续作为切片验收证据。
4. **默认路径对各页的影响**：
   - 诊断：guest 固定命令只走 C# PRoot 启动器（`RuntimeCoordinator.RunGuestDiagnosticsAsync`），失败逐条显示错误码与输出；
     监管观察 / 启动 argv 影子仍对旧实现启动的进程运行。
   - 插件页：列表走 C#（`RuntimeCoordinator.ListPluginsAsync`）；列表失败显示「列出插件失败」，不再显示成「没有已安装的插件」。
     安装 / 卸载**不在本 ADR 范围**（它们没有影子，仍为 C# 可选、旧桥默认）。
   - 手机操控：短按「开启中继」= C# Relay 接管 guest 信箱（ADR-0010）。「协议信封比对」「无障碍读取比对」探针只能对旧 Relay
     运行，C# Relay 接管时提示先长按回退旧 Relay，不伪造比对结果。
   - 远程主机：主机列表的写入（保存 / 删除 / 确认指纹）改由 C#（`RemoteHostStore` + `CsRemoteHosts`）完成，与旧实现
     同一 SharedPreferences 文件与格式，旧连接回退可读到 C# 写入的配置与指纹；本机 SSH 密钥缺失时由 C# 在 AndroidKeyStore
     创建（`AndroidSshKeySource.Ensure`，EC P-256、仅签名、不可导出）。短按「连接」= C#（SSH.NET）；指纹确认只接受
     **C# 连接本次看到的**未知主机密钥（旧 `lastUnknownFingerprint` 规则），主机密钥固定与校验不放松。
5. **C# 路径缺前置条件时返回错误码**（如无运行时布局 → `NotImplemented`），**不**静默回落旧实现。

## 后果

- AGENTS.md 规则 3 在本 ADR 范围内被负责人决定覆盖：`MIGRATION.md` 中这些行的状态写为「C# 默认（ADR-0016，真机验收待补）」，
  真机验收（含截图级 / 日志证据）仍须逐切片补齐，之后才能删除对应 DSHBox 依赖。
- 模拟器（Android 17 x86_64，16 KB 页）冒烟已覆盖：C# 准备、C# 诊断、C# 插件列表、C# 终端打开、C# Relay 启停与 guest 文件发布、
  C# 远程主机保存 / 连接 / 指纹确认 / 删除，以及每条长按回退路径和其影子（`runtime.dsh-provision`、`automation.ui-tools`、
  `remote.host-key` Agree）。限制：模拟器的 guest 运行时是 arm64，guest 内命令在 x86_64 上以 signal 4 退出（旧 / C# 相同），
  因此诊断 / 插件 / 终端只验证了「C# 路径被调用且如实报错」；C# 导入需系统文件选择器，未在模拟器复测（此前已在真机以可选方式通过）。
- 回退仍可用，出问题时用户可逐次切回旧实现；影子报告继续积累。
