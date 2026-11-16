# ADR-0025：终端 V2——输出流控（页面确认）与终端链接策略

- 状态：已接受（2026-11-16，vNext PR-08 第一段）；**无真机证据**，真机验收见下「验收」
- 关联：PR-06 C# 终端（xterm.js + C# PTY，WebMessagePort 传字节）；[ADR-0021](0021-product-ui-material3.md)（界面）；
  AGENTS.md 规则 9（真机证据）、11（安全不削弱）、12（Material 3 与截图级验收）

## 背景

C# 终端把 PTY 字节经无界队列 → 64 KiB base64 批 → WebMessagePort 送进 xterm.js。读取端从不等待：页面跟不上时（大量输出、
WebView 渲染慢、页面冻结），字节在 App 内存里无限堆积。方案 PR-08 要求「大输出限速与背压」「100 MB 输出压力测试」，并保留 xterm.js +
C# PTY（性能不达标先加背压而不是重写 PTY）。同时引入 web-links 插件后，终端输出中的任意文本都可能变成可点的链接。

## 决策

1. **字节原样、只解码一次**：`TerminalOutputPump`（Application）只做批处理与 base64，不碰 UTF-8；页面把 base64 还原为
   `Uint8Array` 交给 `term.write`，由 xterm.js 跨批次解码。被 PTY 读或批处理切开的多字节字符在页面上只解码一次（测试用真实 vendored
   xterm.js 6.0.0 headless 验证）。
2. **页面确认 + 窗口**：页面每渲染完一批回 `{t:'ack',n}`；「已排队 + 已发未确认」达到 1 MiB 时，会话的 PTY 读取端在下一次读之前等待
   （`TerminalSessions.Attach(id, ITerminalOutputSink)`）。PTY 缓冲满后 shell 的写阻塞——与真实终端相同的限速方式，内存有上界。
   没有视图附着时读取端照常读入 256 KiB 环形回放区，shell 不会因为没人看而阻塞。
3. **页面停摆不得冻住 shell**：窗口满且 5 s 没有任何确认时解除节流（计数 `Stalls`），直到下一次确认再恢复。代价是停摆期间退回旧行为
   （排队增长），换来「页面卡死 / 渲染进程消失时后台任务继续跑」。视图分离时先分离再释放等待者。
4. **链接只是请求**：web-links 的默认处理（`window.open`）不使用；页面只把 http(s) 链接作为 `{t:'link',uri}` 交给原生侧。原生侧
   `TerminalLinks` 再校验（绝对 http/https、有主机、≤ 2048、无空白 / 控制字符），弹框单独显示**真实主机**（IDN 转 punycode），
   URL 含 `user@` 时额外警告，用户确认后才交给系统浏览器（`ACTION_VIEW` + `CATEGORY_BROWSABLE`）。页面内仍不允许任何导航（不变）。

## 后果

- 正面：100 MiB 输出在 App 内存中只占约 1 MiB 在途数据；压力工具（`tooling/terminal-stress.mjs`）在本机 Node 上 100 MiB ascii / utf8 /
  ansi 为 41.9 / 36.4 / 23.8 MiB/s、末行完整——**这是 Node 上的 xterm.js，不是 Android WebView**。
- 代价：输出慢的页面会让 shell 变慢（这是目的）；停摆 5 s 后才解除节流，期间 shell 的输出写入会阻塞最多 5 s。
- 未做：IME 组合文本改进、手势、行距 / 主题、会话摘要持久化（见 VNEXT_STATUS §11）。

## 验收（待做，真机）

Android 17 arm64 真机：`yes | head -c 100000000`（或 `seq 1 5000000`）期间界面可操作、`dumpsys meminfo` 中 App 内存不随输出持续增长、
结束后提示符正常；输出中切到别的 App 再回来，命令未被冻结；`echo https://example.com` 点按 → 确认框显示主机 → 浏览器打开；
`echo https://google.com@example.com` 显示 user@ 警告。附截图（规则 12）。
