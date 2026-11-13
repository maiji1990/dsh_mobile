# 2026-11-13 · Honor PTP-AN10 · Android 17 (API 37) · ui.waitFor 影子比对（PR-07 第八段）真机记录

设备：PTP-AN10，`11.0.0.117(SP2C00E117R102P1)`，`AN6B024B01054420`。构建：Debug（`dotnet build -t:Install`，FastDev），
基于 `c828027`（第七段，maiji1990/dsh_mobile#22）+ 未提交的第八段改动。包名 `org.example.dshmobile`，uid 10267。

前置条件由用户本人完成：重装后在系统设置里重新开启本 App 的无障碍服务、开启中继、点「无障碍读取比对（开发者）」、
在三张审批卡（`ui.snapshot`、`ui.node`、`ui.waitFor`）上选「本次会话允许」并在确认页按「允许」。
App 和主机都没有代为开启服务或回答审批；运行期间主机端只用 `run-as` / `dumpsys` / `logcat` 观察，没有执行 `uiautomator dump`
（它会让系统解绑无障碍服务，见第七段证据）。

## 比对方式

与第七段同一套 `RelayA11yReadShadowCheck`：C# 读一次活体树 → 同一调用经真实信箱发给旧 Relay（正常闸门与审批）→ C# 再读一次；
屏幕变化或服务暂不在时重试。`ui.waitFor` 的 9 条调用都在第一次轮询就有答案（`timeoutMs:0` 或条件当场成立），
所以 `polls` 两边都是 1、超时文案确定；只有 `waitedMs`（那一次碰巧等了多久）不比，其余字节全比。

## 结果

同一次点按先跑第七段，再跑第八段：

```text
15:55:21  automation.relay-a11y-read     Agree  5/5 byte-identical（snapshot 2682 字节 / 14 节点；nodeId 0 / 3、below、选择器）
15:56:05  automation.relay-a11y-waitfor  Agree  9/9 byte-identical:
  {"nodeId":0,"timeoutMs":0}                                              equal (454 bytes)        满足：根节点 describe
  {"selector":{"text":"dshm-shadow: no such text"},"absent":true,...}     equal (101 bytes)        满足：{"gone":true}
  {"selector":{"text":"dshm-shadow: no such text"},"timeoutMs":0}         equal (E_WAIT_TIMEOUT)   selector matched no node
  {"nodeId":0,"relative":{"side":"above"},"absent":true,"timeoutMs":0}    equal (E_NODE_NOT_FOUND) 相对定位不判消失
  {"nodeId":0,"timeoutMs":15001}                                          equal (E_TRANSPORT_MALFORMED)
  {"selector":{"text":"让沙箱内的 DSH …"}}                                 equal (693 bytes)        满足（默认 3000 ms）
  {"selector":{"text":"让沙箱内的 DSH …"},"absent":true,"timeoutMs":0}     equal (E_WAIT_TIMEOUT)   node still visible
  {"nodeId":3,"text":"dshm-shadow: no such text","timeoutMs":0}           equal (E_WAIT_TIMEOUT)   text differs（只报长度）
  {"nodeId":3,"checked":true,"timeoutMs":0}                               equal (E_WAIT_TIMEOUT)   checked is false, wanted true
```

`ui.waitFor` 的审批在 15:56:04 `ALLOW_SESSION`（`DshRelayApproval … delivered=true`）。期间旧 Relay 照常执行审批，C# 没有发出任何写请求。

## 未覆盖

- 多次轮询后才成立 / 超时（`polls` > 1）、通道预算封顶（`cappedToBudget`、"capped to the remaining channel budget"）、
  树在等待中消失、非布尔 `checked`、非字符串 `text`、超时参数截断：只有 C# 向量（relay-a11y-waitfor.json，43 例，
  独立 Python 实现按上游规则生成，时钟只在 150 ms 轮询间隔推进）。真机上这些答案取决于两边各自的轮询时刻，不能逐字节对拍。
- 只比对了本 App 页面的树；写路径与无障碍服务本身仍未移植。
