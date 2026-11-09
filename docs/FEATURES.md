# 功能矩阵（FEATURES）

状态取值：`未开始` / `进行中` / `POC` / `通过（附证据）` / `受限（原因）` / `不可用（原因）`。
**只有附真机证据的条目才能标“通过”。**

## 引擎能力（方案 4.1）

| 功能 | 状态 | 证据 / 备注 |
|---|---|---|
| DSH 启动与停止 | 未开始 | TASK-004 |
| `web` profile | 未开始 | TASK-005 |
| `headless` profile | 未开始 | TASK-005 |
| `sdk` profile | 未开始 | TASK-005 |
| `sdk-minimal` profile | 未开始 | TASK-005 |
| `acp` profile | 未开始 | TASK-005 |
| `plugin` 命令 | 未开始 | TASK-009 |
| Cordis profile / bundle / patch | 未开始 | TASK-005 |
| Model Providers / 凭据 | 未开始 | |
| Session / Resume / Fork | 未开始 | TASK-006 |
| 工具调用与审批 | 未开始 | |
| Subagent / Agent Team | 未开始 | |
| Bash / PTY / 子进程 | 未开始 | TASK-008 |
| 文件、工作目录、附件 | 未开始 | TASK-007 |
| MCP / Skills / 自定义工具 | 未开始 | TASK-010 |
| 更新与诊断 | 未开始 | TASK-004 / 013 |

## 宿主基础设施

| 功能 | 状态 | 证据 / 备注 |
|---|---|---|
| .NET 11 Android 空壳（TASK-002） | 通过（Android 17 真机） | Release（full trim，CoreCLR）在 Honor PTP-AN10 / Android 17 运行，冷启动约 220–290 ms；Android 10 未测。证据见 COMPATIBILITY.md 真机记录 |
| C# ↔ Java ↔ DSHBox Kotlin Bridge（TASK-003） | 通过（Android 17 真机），旋转与 Android 10 未测 | 31 轮×1000 次全部成功、双端在途为 0；强制 GC 后 JNI GREF 恒为 37、Java 存活持平。证据见 COMPATIBILITY.md 真机记录 |
| 前台服务 | 未开始 | TASK-004 |

## 可选模块（方案 4.3）

全部 `未开始`：`optional.browser`、`optional.mobile-automation`、`optional.remote-host`、`optional.developer`、`optional.workflow`、`optional.gecko-renderer`。`entertainment.*` **不提供**。
