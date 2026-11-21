# 「检查更新」失败的根因与修复 · PTP-AN10 · 2026-11-21

## 现象

首页状态卡显示：

```
上次「检查更新」未成功
EXCEPTION: TaskCanceledException: net_http_request_timedout, 30
```

「检查更新」30 s 超时，且以裸 EXCEPTION 形式出现在 UI 上（不是稳定错误码）。

## 根因（设备上可复现）

Honor PTP-AN10 / Android 17，移动数据（cmnet，无代理）：

| 目标 | 结果 |
|---|---|
| `ping github.com` | 通（100 ms，ICMP 可达） |
| `curl https://github.com/maiji1990/dsh_mobile/releases/download/channel-preview/release-manifest.json` | **超时 25 s，0 字节**（解析到 `20.247.166.64`） |
| `curl https://api.github.com/repos/maiji1990/dsh_mobile` | **HTTP 200，0.70 s** |
| `curl https://objects.githubusercontent.com/` | **HTTP 404，0.90 s**（主机可达） |
| `curl -H "Accept: application/octet-stream" https://api.github.com/repos/maiji1990/dsh_mobile/releases/assets/774109808` | **HTTP 200，1.77 s，1120 字节**（就是清单本身） |

即：**`github.com` 的 release 下载边缘在这条网络上是黑洞**，而 `api.github.com` 与 CDN
`objects.githubusercontent.com` 都正常。应用只使用 `github.com/.../releases/download/...`，
于是 HttpClient 30 s 超时；`HttpReleaseSource` 没有把 `TaskCanceledException` 映射成稳定错误码，
异常冒到 `BridgeHost.RunExclusive` 的兜底 catch，UI 就显示 `EXCEPTION`。

发布侧本身没问题：`channel-preview` release 存在，`release-manifest.json` + `.sig` 于 2026-11-21 02:13 更新。

## 修复（本提交）

1. **`GitHubAssetResolver`**（`DshMobile.Runtime.Updates`）：把 `github.com/<owner>/<repo>/releases/download/<tag>/<asset>`
   拆成四段；用 `GET https://api.github.com/repos/<owner>/<repo>/releases/tags/<tag>` 找同名制品的
   `assets[].url`（校验 host 必须是 `api.github.com`、https），再以 `Accept: application/octet-stream` 下载。
2. **`HttpReleaseSource`**：直连给 **6 s** 预算；失败/超时才回退到 API。两条路都失败时抛
   `MANIFEST_FETCH_FAILED`，消息写明「github.com 超时且 api.github.com 回退未解析」，不再以裸异常形式冒泡。
3. **`ArtifactDownloader`**：同样的 6 s 预算 + 回退，只用于**新下载**（`have == 0`）；已有 `.part` 的续传保持原
   URL 的 `Range`/`If-Range`，避免跨 URL 拼接。
4. **安全边界不变**：回退只改传输方式；清单仍验签、制品仍校验 SHA-256 与大小；URL 仍必须 https 且 host 受限。
5. 文档：`docs/ADR/0022` 增加 2026-11-21 修订。

## 验证

- 单元测试（`GitHubAssetResolverTests`，4 个）：URL 解析正/负例、按名字精确匹配 asset id、API 404 → null、
  **下载主机抛异常时清单与签名都从 API 路径取回**（并断言 `application/octet-stream` 与 `api.github.com` 调用）。
- `dotnet test tests/DshMobile.Runtime.Tests` → **828 passed / 0 failed / 2 skipped**。
- 设备侧旁证：上述 curl 已证明回退链路在这条网络上可用（api.github.com 0.7 s，asset 下载 1.77 s）。
- APK：`B0556B40120D…`（Release + `DshDiagnostics=true`）已覆盖安装到设备。

## 未完成的验证（如实记录）

端到端「点检查更新 → 成功列出可用更新」的 UI 确认没有做完：uiautomator 在检查进行中反复报
`ERROR: could not get idle state`，脚本化的通道选择点击未能稳定命中，in-app 日志也没能滚到可视区。
因此本报告的结论建立在「网络事实 + 单元测试 + 已安装构建」上，**不是**一次成功的应用内检查。
需要一次手动点击（更多 → 开发者选项 → 检查更新（只读）→ preview）来补这条证据。

## 用户侧临时处理（在修复生效前）

1. **开全局 VPN / TUN 代理**：`.NET HttpClient` 不走 Android 的 Wi-Fi 代理设置，只有全局 VPN 或系统级 TUN 才影响它。
2. **离线导入**：在电脑上下载运行层/DSH 层制品，用「更多 → 运行环境 → 本地导入」安装（不依赖 GitHub 可达）。
3. 该故障只影响**在线检查/在线更新**，不影响 DSH 启动与使用。
