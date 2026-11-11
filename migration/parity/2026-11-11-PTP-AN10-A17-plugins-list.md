# 2026-11-11 · Honor PTP-AN10 · Android 17 (API 37) · 插件列表（PR-05 只读影子切片）真机记录

构建：Release（`-t:Rebuild`）APK 126,092,125 B；安装由手机上人工确认系统安装对话框后完成（17:00:20）。
前置：运行时已导入、DSH 已由旧实现启动（`dsh-profile-web`，无第三方插件）。
操作：主界面「插件」（点击前确认 DSH Mobile 在前台）。插件页仍以旧桥 `plugin.list` 的结果渲染；随后 C# 经自己的
PRoot 启动器（`ProotGuestProcess.RunShellAsync`，与旧 `runGuestScript` 同样的 PATH 前缀、逐 token 引号的 argv）
跑同一条 `dsh plugin --profile web list --json --depth 0`，解析两边输出并比对（`plugins.list`）。只读：不安装 / 不卸载。

第一次（16:53:54 安装的构建，旧判定）：

```text
plugin.list exit=0
shadow plugins.list: Agree (0 plugin(s) identical; csharp 891 ms)
```

旧判定只比对解析结果：两边都**无法解析**时也会得到两个空列表而判 Agree。改为要求两边都解析出 JSON 列表
（`GuestPlugins.TryParseList`；单测 `BothUnparsable_IsNotMistakenForTwoEmptyLists`）后重新构建安装复测：

```text
17:07:33.702 plugin.list exit=0
17:07:34.712 shadow plugins.list: Agree (0 plugin(s) identical, both JSON lists; csharp 1005 ms)
```

crash 缓冲区 0 条；logcat 中无 `token=` 字样。

结论：C# 启动器在真机上能跑通 `dsh plugin list`（pnpm 在 PATH 中、退出码 0、输出为合法 JSON 列表），与旧实现结果一致。
局限：本机无第三方插件，列表为空——非空列表的解析只有单测证据（`SameListDespiteNoise_IsAgree`、`DifferentVersion_IsDivergeWithBothSides`）；
安装 / 卸载仍走旧桥，未做 C# 切换。
