# 2026-11-11 · Honor PTP-AN10 · Android 17 (API 37) · C# 插件安装守卫 / 卸载核对（PR-05）真机记录

构建：Release（`-t:Rebuild`）APK，安装 17:51:50（含 `guard:` 诊断行的第二个构建）。前置：旧实现启动 DSH web（READY，0.2.1-rc.5）。
操作：插件页长按列表项 / 长按「安装」→ 对话框标题带「（C#）」→ 确认。每次点击前确认 DSH Mobile 在前台；输入用按键事件
（第三方输入法 cyime 会吞 `input text` 的字符）。被测包 `is-number`：普通 npm 包，没有 `dsh` 字段，不是 DSH 插件。

## 1. C# 卸载 + 核对

```text
17:53:11.262 csharp plugin.remove is-number: ok in 1.0 s; guard: dependencies after=1 still-present=False
17:53:13.558 shadow plugins.list: Agree (1 plugin(s) identical, both JSON lists: @local/browser-use@file:../../dshbox/dshbox-plugins/browser-use/plugin; csharp 1087 ms)
```

## 2. C# 安装非插件包 → 校验失败 → 回收

```text
17:54:33.259 csharp plugin.add is-number: INSTALL_VERIFY_FAILED (rolled back) in 3.2 s; guard: dependencies before=1 after=2 new=[is-number]
17:54:36.078 shadow plugins.list: Agree (1 plugin(s) identical, both JSON lists: @local/browser-use@…)
```

屏幕结果（节选）：`dsh: warning: is-number declares no dsh.bundle — installed as a plain dependency, not a profile layer`（DSH 只警告、退出码 0）
→ `guard: dependencies before=1 after=2 new=[is-number]` → 「它的 package.json 里没有 dsh 字段（不是 DSH 插件）。已执行回收」
→ `rollback: dsh plugin remove 'is-number'` → `- is-number ^7.0.0`。回收后旧桥与 C# 列表一致，只剩 `@local/browser-use`。

停止 DSH：`stop: ok`，PRoot / node 进程 0，crash 缓冲区 0 条，logcat 无 launch token。

## 3. 同日早先一次运行（解释“未回收”分支）

17:44 的第一次 C# 安装（上一个构建，尚无 `guard:` 行）返回 `INSTALL_VERIFY_FAILED` **未回收**：`is-number` 早在 TASK-009
（2026-11-10）就已写进 profile 的 `dependencies`（当时 `node_modules` 不在，列表显示 0 个插件），装前 / 装后差集为空 → 退回
spec 中的包名只做校验、按上游规则**绝不回收**。上面第 1 步证明 profile 清单可读，第 2 步在干净状态下差集 = `[is-number]` 并回收。
该次对话框的确认点击不是自动化脚本发出的（脚本当时未找到按钮、未点击）。

结论：`dsh plugin add` 对非插件包报告成功（退出码 0），C# 守卫在真机上识别并回收，回收只针对本次新增的包；C# 卸载后依赖确实移除。
局限：未在真机安装真正的 DSH 插件（成功路径、载体挂载只有单测证据）；未测离线 / 安装中断 / 回收失败。
