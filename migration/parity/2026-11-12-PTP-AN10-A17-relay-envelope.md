# 2026-11-12 · Honor PTP-AN10 · Android 17 (API 37) · Relay 信箱信封解码影子（PR-07 第二段）真机记录

设备：PTP-AN10，`11.0.0.117(SP2C00E117R102P1)`。构建：Release（`-t:Rebuild`）APK，基于 `b10ea14` + 未提交的 PR-07 第二段改动。
运行时：沙箱 / DSH 未启动——探针只用宿主侧信箱目录 `files/pilot/run/{inbox,outbox,control-inbox,control-outbox}`，与 guest 是否运行无关。
无障碍：本 App 服务未开启（`a11y=False`）。信封解码在任何审批 / 闸门 / 后端之前，与无障碍无关。

操作：主界面「手机操控（实验）」→「协议信封比对（开发者）」（中继未开）→「开启中继」→「协议信封比对（开发者）」×2 →「关闭中继」。
证据为 `adb logcat -s DshMobile:I`。

## 探针做什么

向真实信箱按 guest CLI 的方式（`.<name>.part` 再改名）写入 16 条**故意写错**的请求——v1 能力通道 8 条、v2 控制通道 8 条，
等旧 Relay（DSHBox `interlock-relay-core` `MailboxServer` / `ControlChannelServer`）回包，再与 C# `RelayEnvelope` 对同一文本的判定比对：

- v1：`outbox/<id>.json` 的 `ok:false`、`id`、`error.code`；
- v2：`control-outbox/<id>.json` 的 `ok:false`、`id`、`op:"unknown"`、`cause`（`"<code>/<cause>"`，逐字比较）。

16 条覆盖 12 种拒绝原因：version、id mismatch、bad id、unknown capability（v1 / v2）、args missing、not an object（v1 / v2）、
oversized（v1 200001 字符 / v2 64001 字符）、too deep（嵌套 25）、digest（大写）、target、op、options count。

安全：每条都在信封解码阶段被拒，到不了审批、闸门与后端（向量标 `probe`，单测保证它们永远是拒绝；探针写入前先用 C# 解码，
C# 会接受的一条都不写）。只创建 / 删除探针用例自己的文件名；没被领走的请求收回。

## 结果

```text
11:41:24.222 shadow automation.relay-envelope: Inconclusive (0/16 answered within 20s, all equal; no answer for pv1-version.json, …)
11:41:34.838 relay.start: phase=STARTING channel=False a11y=False
11:41:34.839 shadow automation.ui-tools: Agree (after relay.start: registered=True conflict=False a11y=False, layer byte-identical, static facts equal)
11:41:39.709 shadow automation.relay-envelope: Agree (16/16 rejections equal (v1 error.code ×8, v2 code/cause ×8; causes: version, id mismatch, bad id, unknown capability, args missing, not an object, oversized, too deep, digest, target, op, options count))
11:41:51.668 shadow automation.relay-envelope: Agree (16/16 rejections equal (…同上…))
11:41:56.662 relay.stop: phase=STOPPED channel=False a11y=False
11:41:56.662 shadow automation.ui-tools: Agree (after relay.stop: registered=False conflict=False a11y=False, static facts equal)
```

- 中继未开时：信箱目录在（上次运行留下），但没人服务 → **Inconclusive**，16 条请求被收回；不冒充 Agree。
  （该行措辞随后改为 `0/16 answered within 20s: Relay is not serving the mailbox (start the relay); requests taken back`，逻辑不变。）
- 中继开启后两次 **16/16 Agree**，从写入到全部回包约 1 s。
- 期间 `dumpsys notification` 无本 App 的审批通知：拒绝发生在解码阶段，未进入审批。
- `ui-tools` 第一段影子同时继续 Agree。

## 未覆盖

- **合法请求**的解码（字段取值、TTL 夹取、ask 选项 trim 等）：真机上合法请求会进入闸门 / 审批，探针刻意不发；
  只有向量证据（`relay-envelope.json` request 37 + control 39 例，新旧双绿）。
- Android libcore org.json 与 JVM Maven org.json 不同的行为（`"id":null` → `"null"`、重名键后者生效、`" 1 "` / `"1d"` 版本号）
  只在 C# 侧按 libcore 语义锁定（`csharpOnly` 7 例），未在真机逐条对拍——它们都是合法请求。
- 响应编码（`encode` / `encodeControl`）、`MailboxServer` 的认领 / 超时 / 满队、`ControlChannelServer` 的 submit / status / cancel / ask 状态机：未移植。
