# 2026-11-12 · Honor PTP-AN10 · Android 17 (API 37) · Relay 审批前准入与回包编码影子（PR-07 第三段）真机记录

设备：PTP-AN10，`11.0.0.117(SP2C00E117R102P1)`。构建：Release（`-t:Rebuild`）APK，基于 `ed4a22a`（#17）+ 未提交的 PR-07 第三段改动。
运行时：沙箱 / DSH 未启动；探针只用宿主侧信箱 `files/pilot/run/`。无障碍：本 App 服务未开启（`a11y=False`）。

操作：「手机操控（实验）」→「协议信封比对（开发者）」（中继未开）→「开启中继」→「协议信封比对」→「关闭中继」，再「开启中继」→「协议信封比对」→「关闭中继」。
证据为 `adb logcat -s DshMobile:I`。

## 探针做什么

一次按钮跑两组，回包与 C# 逐字节比较（只把 `"elapsedMs":N` 归一为 0）：

1. `automation.relay-envelope`（第二段，本段改为整包字节比较）：16 条解码必拒请求；C# 期望文本 = `RelayEncoding.Encode(RelayResponse.Failure(…))`（v1）
   或 `EncodeControlRejection(id, "code/cause")`（v2）。
2. `automation.relay-admission`（本段新增）：12 条**合法信封**、但在执行面裁决与审批闸门之前必被准入拒绝的请求：

| 文件 | 请求 | 期望（C#） |
|---|---|---|
| `pa-tap` / `pa-shell` | `ui.tap {x,y}` / `sys.shell {verb:"id"}` | `E_NOT_IMPLEMENTED`，reason 为能力名（未注册：V1 只注册 `ui.snapshot` / `ui.node`） |
| `pa-unknown` / `pa-display` | `ui.snapshot {"foo":1}` / `{"display":0}` | `E_TRANSPORT_MALFORMED`，后者是带破折号的执行面说明（UTF-8 原样） |
| `pa-takes` | `ui.node {"bogus":true}` | `unknown arg: bogus — this call takes: nodeId, relative, selector` |
| `pa-idstr` / `pa-idfrac` / `pa-idnull` | `nodeId` 为 `"5"` / `1.5` / `null` | `must be a number, got "5" (string)` / `must be a whole number, got 1.5 (Double)` / `got nothing` |
| `pa-selarr` | `{"selector":[1,"a/b"]}` | `got [1,"a\/b"] (JSONArray)`——libcore 的 `/` 转义，再被回包编码转义一次 |
| `pa-self` / `pa-selfx` | 参数里出现本 App 包名（`selector.package` / `x`） | `E_GATE_HOST_DENIED`，自查先于参数校验 |
| `pa-first` | `{"zzz":1,"aaa":2}` | 报文档顺序的第一个未知键 `zzz`（libcore LinkedHashMap） |

另外回包带 `retryPolicy` / `effectState`（`safe_retry` / `not_started`；未注册能力无 `retryPolicy`）。

安全：写入前 C# 先解码（必须是合法信封）再跑准入（必须拒绝），任意一条会被 C# 放行就整组不写（单测覆盖）；每条带 `ttlMs: 5000`，
即使误过准入，信箱也会在约 3.75 s 内以超时收场，没人能来得及批准。只动探针自己的文件名，未领走的请求收回。

## 结果

```text
12:58:01.605 shadow automation.relay-envelope: Inconclusive (0/16 answered within 20s: Relay is not serving the mailbox (start the relay); requests taken back)
12:58:21.643 shadow automation.relay-admission: Inconclusive (0/12 equal; no answer within 20s for pa-tap.json, pa-shell.json, pa-unknown.json, …)
12:58:23.135 relay.start: phase=STARTING channel=False a11y=False
12:58:28.181 shadow automation.relay-envelope: Agree (16/16 answers byte-identical (v1 ×8, v2 ×8; causes: version, id mismatch, bad id, unknown capability, args missing, not an object, oversized, too deep, digest, target, op, options count))
12:58:28.387 shadow automation.relay-admission: Agree (12/12 answers byte-identical (E_NOT_IMPLEMENTED ×2, E_TRANSPORT_MALFORMED ×8, E_GATE_HOST_DENIED ×2))
12:59:11.266 relay.stop: phase=STOPPED channel=False a11y=False
12:59:24.403 relay.start: phase=STARTING channel=False a11y=False
12:59:29.279 shadow automation.relay-envelope: Agree (16/16 answers byte-identical (…同上…))
12:59:29.483 shadow automation.relay-admission: Agree (12/12 answers byte-identical (…同上…))
12:59:31.390 relay.stop: phase=STOPPED channel=False a11y=False
```

- 中继未开：两组都 **Inconclusive** 并收回请求，不冒充 Agree。
- 中继开启：两轮 **16/16 + 12/12 逐字节一致**。这同时确认了 libcore 的输出细节：键插入顺序、`/` → `\/`、非 ASCII 原样、
  `1.5 (Double)` 的数字类型、文档顺序的首个未知键——这些在 JVM（Maven org.json）单测里无法对拍，只有 C# 向量与本次真机。
- 两轮期间 `dumpsys notification` 无本 App 通知：28 条请求都停在审批之前。
- 第一段 `automation.ui-tools` 影子同时继续 Agree。

## 未覆盖

- 入口防灌：计数在 Relay 进程内，真机只能识别「撞限」（探针把 `E_RATE_LIMITED/entry_flood` 报 Inconclusive），逐次计数只有向量（新旧双跑）。
- 准入**放行**的请求（向量里的 `null` 例）：真机上会进入执行面裁决与审批，探针刻意不发。
- 准入之后的执行面裁决、闸门 / 审批、执行配额、无障碍后端：未移植。
