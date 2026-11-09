# 功能矩阵（FEATURES）

状态取值：`未开始` / `进行中` / `POC` / `通过（附证据）` / `受限（原因）` / `不可用（原因）`。
**只有附真机证据的条目才能标“通过”。**

## 引擎能力（方案 4.1）

| 功能 | 状态 | 证据 / 备注 |
|---|---|---|
| DSH 启动与停止 | 通过（Android 17 真机） | 导入→启动→READY 18.5 s→停止，无孤儿进程；见 COMPATIBILITY.md「M2 运行时」 |
| `web` profile | 通过（Android 17 真机） | `--dump-config` 44.6 KB；运行中的 DSH `GET /` → 401（launch token 鉴权生效）；WebView 承载待 TASK-006 |
| `headless` profile | 受限（需模型凭据） | `--dump-config`、`--help` 通过；真实一次性任务需模型 API Key，未验证 |
| `sdk` profile | 通过（Android 17 真机） | JSON-RPC `initialize` → `deepseek-harness-sdk-runtime`，`shutdown` → `{}`，exit 0；会话/提示需模型凭据，未验证 |
| `sdk-minimal` profile | 通过（Android 17 真机） | 同 sdk 握手（initialize 5.1 s） |
| `acp` profile | 通过（Android 17 真机） | ACP v1 `initialize` → `deepseek-harness-acp`、能力声明；stdin EOF 正常退出 0；会话/提示需模型凭据，未验证 |
| `plugin` 命令 | **不可用（pnpm 缺失）** | `dsh plugin --help` 通过；`plugin list` exit 127「pnpm was not found」。DSHBox 由其 app 层提供 pnpm（corepack + bin 包装 + 缓存），未移植；TASK-009 处理 |
| Cordis profile / bundle / patch | 部分 | 五个 shipped profile 的 `--dump-config` 均成功合成；patch / bundle 切换未测 |
| Model Providers / 凭据 | 未开始 | |
| Session / Resume / Fork | 未开始 | TASK-006 |
| 工具调用与审批 | 未开始 | |
| Subagent / Agent Team | 未开始 | |
| Bash / PTY / 子进程 | 未开始 | TASK-008 |
| 文件、工作目录、附件 | 未开始 | TASK-007 |
| MCP / Skills / 自定义工具 | 未开始 | TASK-010 |
| 更新与诊断 | 部分 | 离线导入 + 固定诊断（arch/debian/node/dsh 版本）+ 健康探测；在线更新与回滚 UI 未做 |

## 宿主基础设施

| 功能 | 状态 | 证据 / 备注 |
|---|---|---|
| .NET 11 Android 空壳（TASK-002） | 通过（Android 17 真机） | Release（full trim，CoreCLR）在 Honor PTP-AN10 / Android 17 运行，冷启动约 220–290 ms；Android 10 未测。证据见 COMPATIBILITY.md 真机记录 |
| C# ↔ Java ↔ DSHBox Kotlin Bridge（TASK-003） | 通过（Android 17 真机），旋转与 Android 10 未测 | 31 轮×1000 次全部成功、双端在途为 0；强制 GC 后 JNI GREF 恒为 37、Java 存活持平。证据见 COMPATIBILITY.md 真机记录 |
| 前台服务 | 通过（Android 17 真机） | `specialUse`，后台 90 s DSH 存活 |
| Android 硬链接兼容垫片（link-shim） | 通过（Android 17 真机） | DSH 以 `node --expose-internals --import /opt/dshbox/link-shim.mjs … --profile web` 运行（与 DSHBox 一致）；TASK-004 初版缺失此垫片，已补 |
| Runtime 层管理（TASK-004） | 通过（Android 17 真机），部分验收未测 | 磁盘不足 / 层缺失 / 更新失败未测；见 milestones/M2.md |

## 可选模块（方案 4.3）

全部 `未开始`：`optional.browser`、`optional.mobile-automation`、`optional.remote-host`、`optional.developer`、`optional.workflow`、`optional.gecko-renderer`。`entertainment.*` **不提供**。
