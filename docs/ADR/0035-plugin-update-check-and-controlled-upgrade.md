# ADR-0035：插件检查更新与受控升级（Host 版本发现 + Guest 精确安装双通道）

- **状态**：已接受（2026-11-27，vNext 插件更新切片）
- **关联**：[ADR-0033](0033-native-agent-client.md)（原生 Agent 客户端，L0/L1 边界与 Trim/AOT 门禁）、[ADR-0034](0034-plugin-detail-settings-and-webui-compat.md)（插件详情、官方 `settings/*` 与完整 WebUI 兼容模式）、[ADR-0024](0024-online-dsh-layer-update-transaction.md)（DSH 层在线更新事务）、[ADR-0027](0027-remove-bundled-dsh-import-instead.md)（DSH 层导入而非随 APK 打包）、[ADR-0007](0007-source-ownership-csharp-first.md)（C#-first）
- **实测依据**：本机 `@deepseek-ai/dsh@0.2.1-rc.5` 与 `runtime-bundle/dsh` 的 `pluginInventory/list`、`pluginManager/listBundles`、`dsh plugin` CLI 转发源码核查；`docs/MIGRATION.md` 2026-11-17 插件工具链真机记录

---

## 背景

ADR-0033 决策 4 恢复了 L0（只读清单）与 L1（受控启停/装卸），ADR-0034 又补齐了插件详情、官方 `settings/*` 实时配置与完整 WebUI 兼容模式。但**没有任何切片提供“检查更新 / 升级”能力**，且存在两个已确认缺陷：

1. `PluginDetailViewModel` 的 npm 查询被“本地已有文档”短路，已装插件几乎永远拿不到 `LatestCatalogVersion`；
2. `RemoteDshGatewayClient` 的能力探测把 `PluginMutation` 硬编码为 `false`，与运行时实际可变更能力不符。

同时，上游事实约束了可行路径：

- `pluginInventory/list` 只返回 Cordis Loader 的运行时条目（`entryId`/`moduleName`/`meta`/`enabled`/`fiberPhase`），**不含版本号，更不含最新版本**；
- `pluginManager/listBundles` 用 `availability` 区分 `installation`（随 DSH 层提供）与 `profile`（用户安装），内置包 `removable=false`，且同名时安装层优先解析，profile 副本会被遮蔽；
- `dsh plugin` 把参数原样转发给 pnpm，`pnpm outdated` 有更新时退出码为 `1`，而 Guest 进程层以“非 0 即失败”建模；CLI 模式 stdout/stderr 直接继承，C# 侧会把两路输出合并且只保留末 2000 行；
- `dsh --profile web plugin ...` 不会进入插件子命令，正确顺序必须是 `dsh plugin --profile web ...`；
- 官方按需插件（如 `dsh-subagent-*`）的版本跟随运行中的 DSH 版本，不属于普通 npm 语义。

因此不能把 `pnpm outdated` 作为产品主探测，也不能让内置插件走 `pnpm up`。

---

## 决策

### 1. 双通道：Host 发现 + Guest 执行

- **发现（Host）**：`IPluginCatalogSearch.GetLatestVersionsAsync` 由 `NpmRegistryPluginSearchService` 实现，以固定 HTTPS 主机 `registry.npmjs.org`、受控并发（最多 4）与 `Accept: application/vnd.npm.install-v1+json, application/json;q=0.8` 批量读取 `dist-tags.latest`。单个包的 404、超时、网络或 JSON 失败只记为单包失败，不拖垮整批；调用方取消原样抛出。
- **执行（Guest）**：只使用
  `dsh plugin --profile web add <pkg>@<exactSemVer>`
  作为升级命令，经 `PluginCommandRules.TryBuildUpgradeCommand` 逐 token 校验后由 `GuestCommandLine` 单引号转义执行。

**不采用** `pnpm outdated` 作为产品路径：退出码 1 与 Guest 失败语义冲突，输出会被 stderr 污染且可能被截断；仅在后续需要诊断能力时作为独立、非产品路径的可选项。

**不采用** `pnpm up`：它按范围重解析整棵依赖，无法表达精确目标版本，违反版本锁定与可审计原则。

### 2. 严格区分内置与第三方

- `availability == "installation"`：内置，**不可**在插件页升级；UI 显示“随 DSH 运行层更新”并引导至运行环境在线更新（ADR-0024）。
- `availability == "profile"` 且 `removable == true`、非核心保护包：可检查、可升级。
- `Official == true` 的 profile 包（官方按需插件）：本切片**保守禁止**插件级升级，提示随 DSH 版本更新，避免 DSH 版本与插件版本错配。
- 三重防线：Agent `PluginInventoryService` 的候选过滤、Runtime `PluginCommandRules.IsInstallationShippedPackage` 读取 DSH 包 `dependencies` 拦截、`ProtectedPackageNames` 核心保护。

### 3. 数据模型与门禁

- `PluginEntryView` 追加 `Availability`/`LatestVersion`/`HasUpdate`/`CanUpgrade`；`PluginCatalogEntryView` 追加 `HasUpdate`；`PluginMutationResult` 追加 `Kind`/`FromVersion`/`ToVersion`；新增 `PluginMutationKind`、`PluginUpdateCheckResult`、`PluginLatestVersionView`。
- 版本比较统一由纯托管 `PluginVersionComparer`（SemVer 2.0，忽略前导 `v`/`V` 与构建元数据，正式版高于同号预发布版）完成；只有双方均合法且 `latest > installed` 才判定为更新，绝不把降级误判为更新。
- 升级成功后沿用 `RequiresDshRestart` 提示链路，由用户显式重启 DSH 生效。
- 全部 JSON 走 STJ Source Generation；不引入反射、`object`/`dynamic`；不新增 WebView；不进入 L2 动态表单。

### 4. 安全边界

- 只接受精确 SemVer；拒绝范围符、tag、路径、flag 与控制字符。
- 参数只通过 argv 构建并逐 token 单引号转义，禁止把调用方输入拼进 shell 字符串。
- 读取 DSH 层 `package.json` 时做路径边界、文件大小与符号链接逃逸校验；任何异常安全返回 `false`。
- 诊断文本统一经 `SecretRedactor` 脱敏；不向 UI 暴露原始异常消息；不吞 `OperationCanceledException`。

---

## 后果

- **正面**：发现与执行解耦，Host 侧可批量、可缓存、可精确显示“已装 vX → 最新 vY”；Guest 侧只执行确定性命令，失败可如实回显；内置插件不会被误升级。
- **限制**：需要联网访问公网 npm；升级后必须重启 DSH；本切片不提供“一键全部更新”，也不提供回滚到旧插件版本的按钮；官方按需插件的升级仍以 DSH 版本为准。
- **兼容性**：新增字段与方法均带默认值或默认实现，既有调用点不受影响。

---

## 验证

- 单元测试覆盖版本比较、精确版本校验、受控命令、内置包拦截、批量检查容错与 ViewModel 状态流。
- **Android 真机验收（2026-11-27，Xiaomi M2007J1SC / LineageOS 24 / Android 17，Release `6D5D48EC…22F9`）**：第三方插件真实升级（`is-number` 6.0.0 → 7.0.0，经「检查更新 → 可更新筛选 → 受控升级 → 需要重启 DSH 生效」闭环）与内置插件（`cordis:include` 显示「随 DSH 运行层更新」引导且不提供升级按钮）已通过；**断网失败提示的真机复测待补**，按 AGENTS.md 规则 9 在 `docs/MIGRATION.md` 如实标注。
