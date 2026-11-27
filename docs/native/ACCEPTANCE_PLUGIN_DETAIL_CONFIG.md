# 原生插件详情页、官方 `settings/*` 实时配置与完整 WebUI 兼容模式真机验收报告

> 验收范围：[ADR-0034](../ADR/0034-plugin-detail-settings-and-webui-compat.md) 全部新功能——原生插件详情页（已安装 + 远端发现 + 纯托管 Markdown README 渲染）、官方 `settings/*` 实时配置热生效闭环、精简裁剪语义识别，以及显式按需「完整 WebUI 兼容模式」与系统浏览器本机安全打开闭环。
> 规则约束：严格遵守 [AGENTS.md](../../AGENTS.md) 规则 1–12（尤其规则 5 不伪造成功、规则 9 真机证据、规则 11 安全不削弱、规则 12 Material 3 原生 UI）；全仓延续 100% 声明式 XML 与零业务 WebView 门禁（`NoBusinessWebViewGateTests` 4/4 通过）。
> 真机状态：**已真机验证并完成缺陷就地闭环（2026-11-27，Xiaomi `M2007J1SC` / LineageOS 24.0 / Android 17 / API 37 / `1080×2340` @ 440 dpi / arm64）**——最终净构建签名 Release APK `45A7064C…A8C92D2E`（45 604 423 B）使用 `adb install -r` 覆装（未卸载清数据），设备端 `base.apk` SHA-256 完全一致，全流程 `adb logcat -b crash -d` **0 崩溃**。

---

## 1. 构建产物与设备环境信息

| 项目 | 实测记录 |
|---|---|
| 最终签名 Release APK | `src/DshMobile.Android/bin/Release/net11.0-android/io.github.maiji1990.dshmobile-Signed.apk` |
| APK 大小与构建状态 | `45 604 423 B`（0 错误 / 62 警告） |
| 构建产物 SHA-256 | `45A7064C58BA98F5AF2E2FCCCEE822E5B096C5B36CE5C33FAD365272A8C92D2E` |
| 设备端 `base.apk` 实测 SHA-256 | `45a7064c58ba98f5af2e2fcccee822e5b096c5b36ce5c33fad365272a8c92d2e`（**完全一致**） |
| 安装方式 | `adb -s 4bf97d61 install -r`（保留已导入的 DSH 运行层 `0.2.2-rc.5`、历史会话与凭据，未卸载或清数据） |
| 测试设备 | `4bf97d61` — Xiaomi `M2007J1SC`（`lineage_cas`） |
| 系统与屏幕 | Android 17（API 37），物理分辨率 `1080×2340`，密度 `440 dpi` |
| 验收前后设备状态 | 验收前为亮色模式（`night=no`）、竖屏（`user_rotation=0`）、自动旋转开（`accelerometer_rotation=1`）、`stay_on_while_plugged_in=0`、默认精简原生模式；验收结束已将完整 WebUI 兼容模式切回关闭并重启 DSH 回到精简态（`已安装 204`），同时精确恢复全部系统显示/电源设置并退回主界面「对话」页 |

---

## 2. 桌面单测与 APK 门禁基线（最终定稿修复后实测）

| 检查项 | 命令 | 结果 |
|---|---|---|
| Agent 单测（含 Schemastery `{ uid, refs }` 信封回归与全 XML/零 WebView 门禁） | `dotnet test tests/DshMobile.Agent.Tests -c Release --no-restore` | **156 通过 / 0 失败 / 0 跳过** |
| Core 单测 | `dotnet test tests/DshMobile.Core.Tests -c Debug --no-restore` | **101 通过 / 0 失败 / 0 跳过** |
| Application 单测 | `dotnet test tests/DshMobile.Application.Tests -c Debug --no-restore` | **160 通过 / 0 失败 / 0 跳过** |
| Runtime 单测 | `dotnet test tests/DshMobile.Runtime.Tests -c Debug --no-restore` | **957 通过 / 0 失败 / 2 跳过（总计 959）** |
| Guest Node 测试 | `node tooling/run-node-tests.mjs tests/guest` | **63 通过 / 0 失败** |
| 发布前净构建 + APK 级零业务 WebView 门禁 | `pwsh -NoProfile -File .\tooling\build-clean-release-apk.ps1 -NoRestore` | **0 错误 / 62 警告**，通过 `NoBusinessWebViewGateTests` **4/4** |

---

## 3. 本轮真机实测发现并就地闭环修复的缺陷

### 3.1 [P0 功能阻断] Schemastery 3.x `schema.toJSON()` 返回 `{ uid, refs }` 扁平引用信封导致全部插件实时配置解析为 0 字段
- **真机复现现象**：首轮覆装（`EBC75611…053F07`）后，在已安装列表中全部 109 个运行中插件均未出现「可配置」胶囊；进入含 `.volatile()` 配置项的 `dsh-agent-loop` 与 `dsh-llm-deepseek-api-key` 详情页均误显示「此插件未向 DSH 注册在线配置项」。
- **根因定位**：核查上游 `@deepseek-ai/dsh-settings@0.2.2-rc.5` 与 `@deepseek-ai/schemastery@3.19.0` 源码确认，`settings/describe` 的 `schema` 字段是 `form.toJSON()` 产出的 `{ "uid": <rootUid>, "refs": { "<uid>": { ... } } }` 扁平引用信封（`dict`、`inner`、`list` 子节点均为 `uid` 整数/字符串引用），而非直接内联对象树。原 `PluginSettingsSchemaParser` 仅接受内联 `type == "object"` + `dict` 对象子节点，遇到真实信封时直接返回空列表。
- **代码修复**：
  - [PluginSettingsSchemaParser.cs](../../src/DshMobile.Agent/Engines/PluginSettingsSchemaParser.cs)：引入轻量、零反射、Trim/AOT 安全的 `SchemaGraph` 引用表解析器，完整支持 `{ uid, refs }` 信封及向后兼容内联树，覆盖嵌套对象、`transform.inner`、`union.list` 常量选项与 `intersect` 对象分支合并，并在根节点与遍历路径上维护 `visitedUids` 集合彻底防止循环引用死循环；
  - [RouteBWireDtos.cs](../../src/DshMobile.Agent/Protocol/RouteBWireDtos.cs)：为 `WireSettingsNamespaceView.AutoGenerate` 补充默认值 `= false`，向前兼容未来 DSH 版本；
  - [PluginInventoryService.cs](../../src/DshMobile.Agent/Engines/PluginInventoryService.cs) 与 [PluginDetailViewModel.cs](../../src/DshMobile.Agent/Presentation/PluginDetailViewModel.cs)：增强 `entryId` 到 `ns` 的匹配策略，兼容多层 `:` 嵌套前缀的 Cordis 入口 ID；
  - [PluginSettingsSchemaParserTests.cs](../../tests/DshMobile.Agent.Tests/Engines/PluginSettingsSchemaParserTests.cs)：新增回归单测 `ParseFields_ResolvesSchemasteryToJsonUidRefsEnvelope_NestedObjectsUnionsTransformsAndCycles`。
- **真机复验**：覆装修复版后，`dsh-agent-loop` 列表立即呈现「可配置」胶囊，详情页完整渲染 `Max Parallel Tool Calls`（数值范围 `>= 1 · step 1`），`ui-theme` 在兼容模式激活后完整渲染 `Preference`（下拉枚举）、`Font Size` / `Code Font Size` / `Terminal Font Size` 等多类型表单；将 `maxParallelToolCalls` 从 `10` 修改为 `12` 并保存后即刻提示「配置已保存并实时生效」、显示「已修改」徽标与「重置」按钮，点击「重置」立即恢复默认值 `10`（见截图 `02`、`03`–`06`、`13`、`18`）。

### 3.2 [P1 视觉裁切] 插件详情页 5 枚状态胶囊单行排布导致第 5 枚「精简模式已裁剪」右端被裁切
- **视觉审阅发现**：首轮 `11_detail_webui_guide_trimmed.png` 中，`dsh-client-ui-theme` 同屏展示「已禁用」「官方」「已安装」「含 Web 页面扩展」「精简模式已裁剪」5 枚胶囊，因外层容器采用 `<HorizontalScrollView><LinearLayout>`，静止态下第 5 枚胶囊右侧被硬裁切为「精简模式已」。
- **代码修复**：修改 [fragment_plugin_detail.xml](../../src/DshMobile.Android/Resources/layout/fragment_plugin_detail.xml)，将胶囊容器改为声明式多行流式 `<com.google.android.material.chip.ChipGroup app:singleLine="false" app:chipSpacingHorizontal="6dp" app:chipSpacingVertical="6dp">`。
- **真机复验**：复拍 `11_detail_webui_guide_trimmed.png` 确认 5 枚胶囊自动流畅折为两行，「精简模式已裁剪」完整清晰、零裁切、零重叠。

### 3.3 [P2 细节文案与语义] 列表卡片 WebUI 扩展提示末字孤字换行且开启兼容模式后文案未区分状态
- **视觉审阅发现**：首轮 `02` / `02b` 中「含 Web 页面扩展（点进详情启用完整 WebUI 兼容模式）」在 `1080px` 宽度下末尾「式）」孤字换行，且在兼容模式已开启时仍提示“启用”。
- **代码修复**：修改 [strings_workbench_plugins.xml](../../src/DshMobile.Android/Resources/values/strings_workbench_plugins.xml)、[PluginsAdapter.cs](../../src/DshMobile.Android/Plugins/PluginsAdapter.cs) 与 [PluginsFragment.cs](../../src/DshMobile.Android/Plugins/PluginsFragment.cs)，精简为精简态单行文案「含 Web 页面扩展（点进详情开启兼容模式）」与兼容就绪态单行文案「含 Web 页面扩展（兼容模式已就绪，点进详情打开）」，由 `_bridge.IsFullWebUiCompatActive` 实时驱动切换。
- **真机复验**：复拍 `02`、`02b`、`14`、`14b`、`21` 确认两种模式下均单行完整展示、零孤字且语义准确。

---

## 4. 真机端到端验收矩阵

证据目录：[artifacts/screenshots/plugin-detail-2026-11-27/](../../artifacts/screenshots/plugin-detail-2026-11-27/)（共 **24 张**实机 PNG 截图 + [manifest.tsv](../../artifacts/screenshots/plugin-detail-2026-11-27/manifest.tsv)），全部经逐图人工视觉审阅。

| 验收场景 | 实测内容与观察结果 | 结论 | 对应截图 |
|---|---|---|---|
| 1. 插件目录列表（精简模式默认态） | 首屏显示 `已安装 (204)`，`dsh-hmr` 置灰裁剪；搜 `agent-loop` 同屏验证 `dsh-agent-loop` 展示「运行中」+「可配置」胶囊、`dsh-client-ui-settings-agent-loop` 展示「已禁用」+ 置灰开关（`enabled=false`）+「精简模式已裁剪」+ 单行 WebUI 扩展提示；搜 `ui-theme` 同样展示精简裁剪与置灰不可误拨 | **通过** | `01`、`02`、`02b` |
| 2. 已安装插件详情页 + 官方 `settings/*` 实时配置闭环 | 点击 `dsh-agent-loop` 进入 `PluginDetailActivity`，验证顶栏、摘要卡、4 枚语义胶囊、官方实时配置区（`修改后自动热生效` + `恢复默认` + `Max Parallel Tool Calls = 10`）与 `bundle-readme · README.md` 文档卡；点「编辑」改为 `12` 并保存，实时提示「配置已保存并实时生效」、出现「已修改」徽标与「重置」按钮且值更新为 `12`；点击「重置」恢复原值 `10` 并提示「已恢复该项默认值并实时生效」（零配置污染） | **通过** | `03`、`04`、`05`、`06` |
| 3. 远端发现插件详情页 + 纯托管 Markdown README 渲染 | 切到「发现插件」，打开 `@jay3332/js-cord` 详情页验证完整包信息（`v1.1.3`、`作者：jay3332 · 许可证：ISC`、`主页`/`代码仓库`/`npm` 外链、「安装插件」按钮）及 `npm-readme · README.md` 纯托管 Markdig 渲染（标题、超链接、多级缩进列表、`sh`/`js` 语法高亮代码块、复制与自动换行按钮及折叠展开条）；另在 `cordis` 详情页验证外链弹窗与安装安全确认弹窗后取消（未污染设备） | **通过** | `07`、`08`、`08b`、`09`、`10` |
| 4. 「完整 WebUI 兼容模式」与页面修改插件真实生效闭环 | 精简态进入 `dsh-client-ui-theme` 详情页，确认多行胶囊完整显示「含 Web 页面扩展」「精简模式已裁剪」及「浏览器页面扩展说明」引导卡 → 点「开启完整 WebUI 兼容模式并重启 DSH」确认弹窗并重启 DSH → 重启就绪后 `ui-theme` 变为「运行中」+「可配置」且实时配置区展开 `Preference`（枚举选择）/`Font Size` 等多类型字段，引导卡按钮切换为「在系统浏览器中打开完整 WebUI」；插件列表验证 `dsh-client-modules` 与 `dsh-client-ui-theme` 均为「运行中」（`已安装 206`）、无裁剪提示且顶栏溢出菜单出现「在系统浏览器打开 WebUI」；点击打开先弹出明确说明 `127.0.0.1` 回环与一次性令牌移交边界的安全确认弹窗，点「安全打开」拉起系统浏览器（LineageOS Jelly）成功换取 Cookie 重定向至 `http://127.0.0.1:3080/` 并真实渲染出完整官方 WebUI 页面 | **通过** | `11`、`12`、`13`、`14`、`14b`、`15`、`16`、`17` |
| 5. 深色模式回归、还原默认设置与稳定性 | 系统深色模式（`night yes`）下实测 `ui-theme` 多类型配置详情页与 `@jay3332/js-cord` Markdown README，呈现 `#0B1120` 深蓝黑底色 + `#60A5FA` 亮钴蓝高亮与高对比度 1dp 描边；通过顶栏溢出菜单切回关闭「完整 WebUI 兼容模式」并重启 DSH，确认重新恢复精简原生模式（`已安装 204`，`ui-theme` 恢复「已禁用」与「精简模式已裁剪」）；恢复亮色、竖屏、自动旋转开启与 `stay_on_while_plugged_in=0`；全程 `adb logcat -b crash -d` 输出为空 | **通过（0 崩溃）** | `18`、`19`、`20`、`21`、`logcat -b crash` |

---

## 5. 客观未覆盖项与边界说明（如实标注）

1. **远端插件真实安装/卸载写盘与非 Bundle 依赖提示**：为避免污染测试机运行层 `profiles/web/node_modules`，远端安装仅验证至「确认安装远端插件？」安全确认弹窗后主动点击「取消」；非 Bundle 普通 npm 包检测、pnpm 符号链接穿透扫描与 `not-bundle` 警告展示由 `GuestPluginServiceTests` 与 `PluginInventoryAndViewModelTests` 桌面单测锁定覆盖。
2. **密钥槽位（`role('secret')`）与整节「恢复默认」/ 乐观锁冲突实机触发**：当前真机运行层 `0.2.2-rc.5` 默认启用的 `.volatile()` 插件未包含 `role('secret')` 字段（凭据引用走独立 `credentials/*` 控制器）；单向密钥写入不回显、保留密钥的批量非密钥 `unset` 与 `expectedRevision` 乐观锁冲突自动刷新由 `PluginSettingsSchemaParserTests` 与 `PluginDetailViewModelTests` 单测锁定覆盖。
3. **内置 `dsh-base` / `dsh-web-app` 子行文档归属**：当打开属于官方 Bundle 的子行（如 `dsh-agent-loop`）时，按 ADR-0034 文档回退链优先调用宿主 `pluginManager/readBundleDetail` 展示所属 Bundle 的官方文档（`bundle-readme · README.md`），实测工作符合设计预期。
