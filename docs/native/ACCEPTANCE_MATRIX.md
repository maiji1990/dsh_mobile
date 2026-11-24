# ACCEPTANCE_MATRIX — Android 17 真机总验收（T01–T12）

> 汇总窗口：2026-11-23 ～ 2026-11-24（Xiaomi M2007J1SC / LineageOS 24.0 / Android 17 / API 37 / arm64 / 非 root）
> 规则：没有真机记录的格子不勾通过（AGENTS.md 规则 5 / 规则 9）；「本次复测」列只写 2026-11-24 PR-09 覆盖安装这一轮实际观察到的内容。
> 分项证据：ACCEPTANCE_PR00_PR04_DEVICE.md、ACCEPTANCE_PR05.md、ACCEPTANCE_PR06.md、ACCEPTANCE_PR07.md、ACCEPTANCE_PR08.md、ACCEPTANCE_PR09.md。

| 场景 | 验收目标 | 状态 | 证据来源 | 本次复测（PR-09，2026-11-24） |
|---|---|---|---|---|
| **T01** | 全新安装 → 运行层就绪 → 原生凭据配置 → 首次真实对话 | 部分通过 | PR-05（凭据/真实对话）、PR-08（真实对话）；**全新零凭据安装首次引导未覆盖**（需清数据重装） | 覆盖安装态下真实对话通过（PR09_OK）；未做清数据全新安装 |
| **T02** | 已安装重启 → 0.2s Cache-First 首屏 → 后台就绪无闪烁校准 | 通过 | PR-05：native.activity_visible 90.7 ms、native.cache_first_rendered 96.2 ms，F15–F19 修复后复测关闭 | 冷启直达「会话」，DSH 就绪后列表校准无重复行（5 会话） |
| **T03** | 入队回执与 turn/end 分离，失败轮次如实报错不假成功 | 通过 | PR-04 真机（回执/结束分离、F3 领域错码透传） | 发送 PR09_OK：回执、结束态与正文一致；未构造失败轮次 |
| **T04** | 后台到达审批/问答 → 通知 + 参数回溯 + Hold-to-undo + 拒绝零执行 | 部分通过 | PR-06：通知与页内卡片去重、参数回溯通过；拒绝零执行与 Hold-to-undo 样本待补（F22–F23） | 未复测 |
| **T05** | 多并发 Session 与 Subagent 隔离，隐藏页跳过派生 | 未验证 | 无专项真机记录 | 仅验证多会话列表与恢复，未做并发/子代理隔离 |
| **T06** | 运行中杀 DSH/断线 → 恢复校准无重复消息、不盲目重发 | 通过 | PR-05 F14 修复后复测；PR-08 远程隧道断线（transport.timeout + 未确认发送 + 重连） | 未复测（远程链路由 PR-08 覆盖） |
| **T07** | 6,000 词 + 400 行代码块流式不卡主线程，回看不被抢滚 | 部分通过 | PR-04：长文渲染帧统计 917 帧 / 2 janky / p90 11 ms，负载非固定 6,000 词 | 未复测 |
| **T08** | Relay ui_* 双安全域隔离（FLAG_SECURE）+ 插件装卸边界 | 部分通过 | PR-06 通知隔离、PR-07 L0/L1 边界；FLAG_SECURE 与真实第三方装卸未验证（F20–F21） | 未复测 |
| **T09** | Android 17 loopback / LAN（ADR-0032）/ 公网 / SSH 远程原生对话 | 部分通过 | PR-08：SSH 远程原生对话、断线、重连、HOST_KEY_CHANGED 通过（本机 WSL sshd + adb reverse，非真实 LAN） | 未复测 |
| **T10** | 从旧 WebView 版本覆盖安装保留工作区、凭据与可恢复会话 | **通过** | **PR-09 本轮**：旧包（四栏导航、首页标签）→ 覆盖安装净构建 Release | 首屏「会话/工作区/更多」，无崩溃；5 个会话、`/root/projects` 文件、API 凭据全部保留，发送 PR09_OK 成功 |
| **T11** | 停机/强杀无孤儿进程；32 进程上限与 A17 内存限制清晰归因 | 部分通过 | PR-08：idle-stop 与 force-stop 零残留、31/32 预警；exit 137 与 MemoryLimiter 归因缺失（F25） | 未复测 |
| **T12** | DSH/Runtime 在线更新探针与回滚通过；全仓与 APK 零业务 WebView | 部分通过 | PR-09：源码门禁 + **净构建 APK 产物零业务 WebView**（DshWebActivity/CSharpWebUiController/activity_web 命中 0）；在线更新回滚受 GitHub 不可达限制（PR-08 D04） | APK 产物核查通过；外链二次确认 + 系统浏览器通过；在线更新未验证 |

## 净构建 APK 产物核查（T12 新增证据）

- 包：`io.github.maiji1990.dshmobile` Release，SHA-256 `AC41C634CD78EB23C1223D575DE286F8AD66ED95956263208C15CC405FF15DB1`（设备 `base.apk` 拉回后哈希一致）。
- `classes*.dex`：`DshWebActivity` = 0、`CSharpWebUiController` = 0、`activity_web` = 0、`WebUiPolicy` = 0、`DshOriginPolicy` = 0。
- Manifest 与 `resources.arsc`：`WebView` / `WebKit` = 0。
- 残留 14 处 `WebView` 字样均为 `Mono.Android` 的 `Android.Webkit.WebView` 平台绑定与接口实现类（`mono/android/webkit/WebView_*Implementor`），非业务代码。
- **F27**：增量构建（未清理 `obj/Release`）会残留已删除类型的 Java 调用包装字符串（`DshWebActivity` 6 处、`CSharpWebUiController` 9 处）；清理后净构建归零。发布验收必须以净构建产物为准。

> UI 布局重构切片验收记录：[Slice 1（公共 XML 底座 + P0 缺陷清零）](ACCEPTANCE_UI_SLICE1.md) · [Slice 2（会话与原生聊天主路径重新布局）](ACCEPTANCE_UI_SLICE2.md) · [Slice 3（工作区重新布局与 XML 化）](ACCEPTANCE_UI_SLICE3.md)。

## 仍未覆盖（不勾通过）

- T01 的全新零凭据安装首次引导；T05 的并发/子代理隔离；T04 的拒绝零执行与 Hold-to-undo；T07 的固定 6,000 词负载；T08 的 FLAG_SECURE 与真实第三方插件装卸；T09 的真实 LAN / 公网；T11 的 exit 137 / MemoryLimiter 归因；T12 的在线更新探测提交与回滚。

最后更新：2026-11-24
