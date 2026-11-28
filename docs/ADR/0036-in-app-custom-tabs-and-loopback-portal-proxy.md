# ADR-0036：应用内 Custom Tabs 与一次性票据回环门户代理（本机 / 远程完整 WebUI）

- **状态**：已接受（2026-11-27）
- **关联**：[ADR-0033](0033-native-agent-client.md)（纯原生 Agent 客户端、全仓零业务 WebView）、[ADR-0034](0034-plugin-detail-settings-and-webui-compat.md)（插件详情、官方 `settings/*` 与完整 WebUI 兼容模式）、[ADR-0035](0035-plugin-update-check-and-controlled-upgrade.md)（插件检查更新与受控升级）、[ADR-0028](0028-remove-native-terminal-and-plugin-pages.md)（移除原生终端页与原生插件页）、[ADR-0012](0012-csharp-ssh-client-sshnet.md)（C# SSH 客户端与主机密钥固定）、[ADR-0013](0013-remote-dsh-start-stop-shadow.md)（远端 DSH 启停、token 与转发）
- **依据**：上游 `dsh.client` 在官方 WebApp 中按 `modules` / `slots` 动态组合加载的既有事实（ADR-0034 调研）；仓库内 `DshMobile.Runtime.Hosting.LoopbackPortalProxy` 与其 `LoopbackPortalProxyTests`；`CustomTabsPortalLauncher` / `AndroidManifest.xml`；`BridgeHost`、`CsRemoteConnection`、`RemoteActivity` 接线

---

## 背景与问题

### 上游事实：`dsh.client` 扩展只能在官方 WebApp 运行时里组合

- `dsh.client` 插件扩展不是服务端渲染产物。`dsh-host-plugin-inventory` 只负责把插件清单交给前端，`dsh-client-modules` 在浏览器里按 `modules` 动态注入模块，`dsh-web-app` 再把它们挂载到官方 WebApp 的 `ctx.slots`；`dsh-host-frontend-static` 提供承载页面，`dsh-client-connection` 负责与宿主的 RPC/事件通道。整条链路依赖浏览器 DOM 与官方前端运行时，**无法在无 DOM 引擎的原生 Material 3 客户端里直接重构**，也不应重新实现这套组合协议。
- `pluginInventory/list` 只返回 Cordis 运行时条目（`entryId` / `moduleName` / `meta` / `enabled` / `fiberPhase`），**不含“是否网页扩展”的标志**。要识别页面修改类插件，只能在其安装目录的本地 `package.json` 中读取 `dsh.client`（以及 `dsh.bundle.patch`），不能指望宿主清单接口给出答案。

### ADR-0034 留下的两个待补齐缺口

ADR-0034 引入了显式按需的**完整 WebUI 兼容模式**（`TrimmedWeb: false`，不释放 `modules` 与 `ui-*`），并允许经**系统浏览器**打开 `http://127.0.0.1:<port>`。路径可行，但仍有：

1. **启动令牌暴露面**：`Intent.ActionView` 的 URL 直接携带长效、**可重放**的 `launchToken`，由浏览器兑换 Cookie。该 URL 会进入浏览器历史、地址栏，可能进入崩溃/诊断日志或 `Intent` 记录；有效期内任何拿到它的人都能再次换取会话。
2. **应用内体验与远程覆盖**：缺少应用内 Custom Tabs 入口，且并非所有设备都声明 `CustomTabsService`（例如 LineageOS 上的 `org.lineageos.jelly`），必须能自动、无感地回退到普通浏览器；同时远程 SSH 主机（`RemoteSshSession`，ADR-0012 / ADR-0013）虽已有本地转发端口与内存启动令牌，却**尚未打通安全 WebUI 入口**。此外，后台 `IdleStop` 需要感知“浏览器正在使用 WebUI”，避免把正在浏览的页面连同 DSH 一起停掉。

---

## 决策

### 1. 严守零业务 WebView 红线

本切片**不引入任何 WebView**。`NoBusinessWebViewGateTests`（含 `tooling/build-clean-release-apk.ps1` 以 `DSHM_VERIFY_APK_PATH` 校验签名 APK 的产物门禁）与 `XmlFirstUiGateTests` 保持原样、不放宽；零新增 NuGet。WebUI 一律交给系统浏览器 / Custom Tabs 承载，App 进程只做代理与票据管理。

### 2. `LoopbackPortalProxy`：纯 C# 本机一次性票据安全回环代理

在 `DshMobile.Runtime.Hosting` 新增纯托管 `LoopbackPortalProxy`，只依赖 BCL（`System.Net.Sockets` / `System.Net.Http` / `System.Security.Cryptography`），不使用反射，符合 AOT/Trim 约束。

**票据签发与兑换**

- 监听 `TcpListener(IPAddress.Loopback, 0)`，端口由内核分配，只接受 `127.0.0.1` 回环连接；代理侧上游使用 `SocketsHttpHandler`（`UseProxy = false`、`AllowAutoRedirect = false`、`UseCookies = false`）。
- `TryIssueLaunchUri()` **仅当**资格有效（兼容模式已开启 / 远程会话仍活跃）、端口合法、内存中确实持有启动令牌时，才签发 **128-bit 单次 `nonce`**（16 字节，小写 hex）。`nonce` 有效期 **60 秒**，最多保留 **16 个**未消费票据，超出按最旧淘汰；过期票据在签发与兑换时清理。返回的浏览器 URL **不含 `launchToken`**，只有 `http://127.0.0.1:<proxyPort>/__dshmobile/launch?nonce=…`。
- `GET /__dshmobile/launch?nonce=…` 在锁内用 `CryptographicOperations.FixedTimeEquals` 对全部待处理票据做常数时间比对，**原子移除**后消费；重放第二次直接 `403`。
- 兑换只在进程内发起：请求 `http://127.0.0.1:<upstreamPort>/?token=<launchToken>`，从 `Set-Cookie` 中只提取 `dsh-auth-…` 前缀的 Cookie。启动令牌只出现在这条进程内请求里，**绝不进入代理 URL、日志、剪贴板或持久化存储**。
- 兑换成功即签发 **256-bit `__dshmobile_portal` 会话密钥**（32 字节，仅驻留内存），以 **`303 See Other` → `/`** 落回根路径，并带 `Set-Cookie: __dshmobile_portal=…; Path=/; HttpOnly; SameSite=Strict`、`Cache-Control: no-store`、`Referrer-Policy: no-referrer`。

**请求边界与透传**

- 后续所有 HTTP 请求与 `/api/remote.mux` WebSocket 请求都强校验：`Host` 必须严格等于 `127.0.0.1:<proxyPort>`，`Origin` 若存在必须等于 `http://127.0.0.1:<proxyPort>`，且必须携带有效的会话 Cookie；任一不满足返回 `403`/`401`，从而阻断 DNS rebinding 与跨站伪造。
- 转发前改写 `Host` / `Origin` / `Referer`（仅当落在代理 origin 边界内）/ `Cookie`（替换为进程内保存的 `dsh-auth-…`），并剥离 `Connection` / `Proxy-Connection` / `Expect`。
- 支持 `Content-Length`、`Transfer-Encoding: chunked`（含 trailer）与无长度声明的流式转发；普通 HTTP 响应设 `Connection: close`，保留头部之后预读字节的顺序。
- WebSocket 升级：识别 `Upgrade: websocket`，原样回写 `101` 响应头与预读帧字节，之后双向 `CopyToAsync` 泵送；任一方向结束即取消另一方向，避免半开连接挂死；活跃 WebSocket 计入客户端活跃度。

**吊销与空闲保护**

- 订阅 `IDshTokenSource.TokenChanged`，并在宿主停机、重启、切回精简模式、断开 SSH、`Dispose` 时立即 `RevokeAll()`：清空全部票据与会话、递增吊销代次、取消并硬断所有活跃连接（客户端与上游 socket），进行中的请求得到明确的 `401`。
- 兑换期间若令牌 / 资格 / 端口 / 吊销代次变化，在签发会话前的同一把锁内二次核对，避免竞态下签出已失效会话；`Dispose` 保留 1 秒排空窗口后停止监听。
- 提供 `HasActiveOrRecentClients(3 min)`（存在活跃 WebSocket，或 3 分钟内有合法请求）；`BridgeHost` 将其接入 `IdleStopMonitorLoopAsync`，把“浏览器仍在浏览本机 WebUI”算作忙碌，**防止浏览期间后台误杀 DSH**。

### 3. `CustomTabsPortalLauncher` + `<queries>`：优先 Custom Tabs，自动无感回退

- `AndroidManifest.xml` 增加 `<queries>`：`android.support.customtabs.action.CustomTabsService` 用于发现支持 Custom Tabs 的浏览器；`VIEW` + `BROWSABLE` + `http` 用于解析默认浏览器，不申请 `QUERY_ALL_PACKAGES`。
- 启动器只接受**本机回环、固定路径 `/__dshmobile/launch`、合法端口**的地址；**防御性拒绝任何含 `token=` 的 URI**，从源头保证启动令牌不会出现在浏览器地址里。
- 有 `CustomTabsService` 时：优先**默认浏览器**（若它也在支持列表中），否则取第一个支持的包，以 `Intent.ActionView` + `CategoryBrowsable` + 标准 Extras 打开——禁用分享、以主题 `colorSurface` 作为工具栏颜色；从 `Activity` 启动时**不加 `NewTask`**，保持任务栈与返回行为；指定包名失败时清空包名按系统默认浏览器重试一次。
- 无 `CustomTabsService` 的设备（如 LineageOS `org.lineageos.jelly`）：不设包名，自动回退为带同样安全 Extras 的 `ActionView + CategoryBrowsable`，由系统解析默认浏览器；全程无额外弹窗、无错误提示，用户感知为“直接打开了页面”。仅当启动上下文不是 `Activity` 时才补 `NewTask`；确实找不到可处理浏览器时才提示。

### 4. 远程 SSH 主机打通安全 WebUI 入口

- `CsRemoteConnection` 为当前活跃 `RemoteSshSession` **配对维护**独立 `LoopbackPortalProxy`：`Set` 替换会话时先释放旧代理再换新，`Clear` 时先释放代理再关闭会话，停远端 DSH 时立即吊销，杜绝票据跨会话残留。代理上游取 `session.LocalPort`（SSH 本地转发），令牌来源取会话实现的 `IDshTokenSource`；资格要求“仍是当前活跃会话 + SSH 仍连接 + 端口合法 + 内存持有令牌”。
- `activity_remote.xml` 与 `RemoteActivity` 在远端 DSH `ready` 状态展示「打开远程完整 WebUI（含插件页面）」入口；点击后走统一的二次安全确认弹窗，确认时才即时签发一次性票据并交给 `CustomTabsPortalLauncher`；停止远端 DSH 时调用 `RevokeRemoteWebUiPortal()` 立即吊销。

---

## 后果与权衡

### 正面

- **启动令牌彻底离开浏览器地址面**：浏览器只看到短时、一次性、绑定本机回环 `Host` 的 `nonce`；即使 URL 进入历史或日志，60 秒后或首次使用后即失效，无法重放。
- 会话密钥 `__dshmobile_portal` 为 `HttpOnly; SameSite=Strict`，不落盘、不进日志；`Host` / `Origin` 强校验阻断 DNS rebinding 与跨站伪造。
- 应用内 Custom Tabs 提供一体化体验；无 `CustomTabsService` 的设备自动无感降级为普通浏览器，不改变用户操作路径。
- 远程主机获得与本地一致的安全 WebUI 入口，远程令牌不经 URL 暴露；代理与 SSH 会话同生命周期，停止/替换即吊销。
- 空闲自动停机不会误杀正在浏览的 WebUI。
- **无额外 NuGet 依赖、无额外包体积负担**：全部为 BCL 与既有依赖；纯托管、AOT/Trim 安全；零业务 WebView 门禁与 APK 产物门禁不变。

### 限制（如实标注）

- 无 `CustomTabsService` 时的回退仍是**系统浏览器的普通标签页**，没有 Custom Tabs 的工具栏染色、地址栏隐藏等一体化外观；这是有意的兼容降级。
- 本切片只使用 Custom Tabs 的标准 Extras（`SESSION` binder 传 `null`），未引入 `androidx.browser` 的 `CustomTabsClient` 预热，首屏仍取决于浏览器自身冷启动。
- 代理只监听 `127.0.0.1`，定位是“本机浏览器 ↔ 本机/SSH 转发端口”的单用户桥；不提供局域网或非回环访问，也不做多用户隔离。
- 浏览器侧仍执行官方前端与第三方 `dsh.client` 插件代码，其安全边界由官方 WebUI 与插件本身决定；App 只保证令牌与通道安全。
- **真机边界**：桌面单测已覆盖票据、Host/Origin、WebSocket、吊销与空闲保护等边界；**2026-11-27 Android 17 真机（Xiaomi M2007J1SC / LineageOS 24 / API 37）已完成无 `CustomTabsService` 的 `org.lineageos.jelly` 自动无感回退、`303` 跳转落根路径渲染官方 WebUI、`logcat` / `intents` 零令牌泄露、`nonce` 重放 `403`，以及切回精简模式后旧代理端口 `401` 即时吊销**；**支持 `CustomTabsService` 的浏览器（如 Chrome/Edge）样式表现与远程 SSH 主机完整 WebUI 的真机实操待补**，按 AGENTS.md 规则 9 在 `docs/MIGRATION.md` 如实标注，不得以单元测试替代真机结论。

---

## 验证

- `DshMobile.Runtime.Tests.LoopbackPortalProxyTests` 覆盖：非 `nonce` 不含启动令牌、首次兑换返回 `303` 与 `HttpOnly; SameSite=Strict` 门户 Cookie、重放 `403` 且不二次兑换、60 秒 TTL 失效且不触达上游、GET/POST/chunked/流式转发与头部改写、WebSocket 双向透传并计入活跃客户端、伪造 `Origin`/`Host` 拒绝、`HasActiveOrRecentClients` 的活跃与过期、`TokenChanged` 吊销并断开 WebSocket、不合格上游返回 `null` 且请求 `401`、`Dispose` 后 `401`、票据上限 16 的最旧淘汰、无/伪造门户 Cookie 返回 `401` 并清除、`RevokeAll` 断开 WebSocket、代理 origin 之外的 `Referer` 保持不变。
- `DshMobile.Agent.Tests` 的 `NoBusinessWebViewGateTests` 继续锁死零业务 WebView；`XmlFirstUiGateTests` 继续锁死全 XML 化界面。
- Release 净构建由 `tooling/build-clean-release-apk.ps1 -Configuration Release` 执行：清理 `src/DshMobile.Android` 的 `obj/Release`、`bin/Release` 后重建，并对签名 APK 运行产物门禁，输出 APK 路径、字节数与 SHA-256。
- **待补**：支持 `CustomTabsService` 的浏览器（Chrome/Edge 等）样式表现、远程 SSH 主机完整 WebUI 真机实操，以及截图级证据（`org.lineageos.jelly` 回退与本地完整 WebUI 的真机验收已于 2026-11-27 完成，见 `docs/MIGRATION.md`）。
