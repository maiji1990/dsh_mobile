# vNext PR-07 Harness Agent E2E 真机记录（模板）

> 复制为 `YYYY-MM-DD-<设备>-<系统>-agent-e2e.md` 后填写。步骤见 [docs/TEST_PLAN.md](../../docs/TEST_PLAN.md)「vNext PR-07」。
> 不要贴入任何凭据；`DSHE2E` 行已经过驱动的脱敏，粘贴前仍需人工复核。

| 项 | 值 |
|---|---|
| 日期 / 时间（时区） | |
| 设备 / 系统 / 内核页大小 | |
| App 版本 / 构建（Release？） / commit | |
| Runtime 包 / Node / DSH 版本 | |
| 模型提供方 / 模型（不写密钥） | |
| 执行人 | |

| # | 检查 | 结果（通过 / 失败 / 受限 / 不适用 + 原因） | 证据（日志行、截图文件名、耗时） |
|---|---|---|---|
| 1 | 默认工作区首次创建；`xdg-user-dir DOCUMENTS` | | |
| 2 | 凭据配置、文本会话 | | |
| 3a | `dsh-e2e.mjs` headless（工具 / 文件 / 回答 / 跨进程续接） | | |
| 3b | `dsh-e2e.mjs` sdk | | |
| 3c | `dsh-e2e.mjs` acp（权限请求次数） | | |
| 4 | WebUI 审批：拒绝不执行、允许执行；Resume / Fork；强杀后会话仍在 | | |
| 5 | 插件安装 / 卸载、MCP 工具与 Skill 被模型实际调用 | | |
| 6 | Subagent / Agent Team | | |
| 7 | 锁屏 15 / 60 / 180 分钟；停止无孤儿；强杀后 journal 恢复 | | |
| 8 | WebView 附件上传 / 下载 / 401 重新认证 | | |
| 9 | 清除凭据后重跑：`needs-credential`；前缀检索无残留 | | |

## `DSHE2E` 行

```text
（粘贴第 3 步和第 9 步的最后一行）
```

## 问题与后续
