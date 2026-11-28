# ADR-0037：实验性第三方插件原生面板（SDUI）

- **状态**：已接受，实验性、默认关闭（2026-11-28）
- **关联**：[ADR-0034](0034-plugin-detail-settings-and-webui-compat.md)（插件详情、官方 `settings/*` 实时配置与完整 WebUI 兼容模式）、[ADR-0036](0036-in-app-custom-tabs-and-loopback-portal-proxy.md)（Custom Tabs 与回环门户代理，本特性的“方案 A”替代路径）、[ADR-0033](0033-native-agent-client.md)（纯原生 Agent 客户端、全仓零业务 WebView）、[ADR-0028](0028-remove-native-terminal-and-plugin-pages.md)（移除原生插件页）、[ADR-0021](0021-product-ui-material3.md)（Material 3）
- **依据**：`DshMobile.Agent.Sdui`（`PluginSduiValidator` / `PluginSduiEvaluator` / `PluginSduiStore` / `PluginSduiGeneratorService` / `PluginSduiViewModel`）、`PluginRpcEndpointValidator` 与 `RemoteDshGatewayClient.InvokePluginRpcAsync`、`GuestPluginService.ReadLocalSduiManifest`、`PluginSduiActivity` / `PluginSduiAdapter` 与 `activity_plugin_sdui.xml`、`PluginDetailFragment.RenderSduiCard`、`UiPrefs.ExperimentalPluginSduiEnabled`

---

## 背景与问题

### 上游事实：页面扩展与业务 RPC 是两件事

`dsh-opencode-go@0.5.2` 这类插件同时提供两种能力：

1. **浏览器页面扩展**（`dsh.client` + 官方 WebApp 的 `modules` / `slots`）：只能在浏览器 DOM 与官方前端运行时里组合，ADR-0036 已用 Custom Tabs + 一次性票据回环代理解决“要页面就走 WebUI”的问题。
2. **Typert Unary RPC**（`lib/types/contracts.d.ts` 声明的扩展端点）：宿主以 `POST /api/<namespace>/<method>` 暴露，**与是否释放 Web 前端模块无关**。

因此存在一条不引入 WebView、也不要求 `TrimmedWeb: false` 的产品路径：把插件的扩展 RPC 与配置数据，用**声明式文档 + 原生 Material 3 控件**渲染成 App 内的一等页面。这也是 ADR-0036「方案 A」的补充而非替代——两者可同时存在，由用户按需选择。

### 为什么不做“通用插件 UI 引擎”

- 插件前端是任意 DOM/JS，复刻等于重写官方 WebApp，违反“不重写 DSH 内核”。
- 直接执行插件代码需要 JS 引擎与 WebView，触碰零业务 WebView 红线。
- 可行边界是：**让 Agent 只读分析插件包，产出一份受严格 Schema 约束的声明式文档**，宿主只认识这份文档，不执行插件代码、不动态创建视图、不做通用表达式求值。

### 需要同时解决的四个问题

1. **获取**：文档从哪来？插件作者未必提供内置清单；Agent 生成有延迟与不确定性。
2. **通道**：如何在不放开完整 WebUI 的前提下调用插件扩展 RPC，且绝不把扩展通道变成核心 API 的后门。
3. **渲染**：如何在全 XML 静态布局、零动态造控件的门禁下表达 9 类区块与 5 类表单字段。
4. **安全**：插件文档来自第三方（或由 Agent 归纳第三方代码），必须视为不可信输入；密钥类字段不得进入日志、状态快照或无障碍树。

---

## 决策

### 1. 默认关闭的开发者实验开关，不改变既有产品路径

- 新增 `UiPrefs.ExperimentalPluginSduiEnabled`（`SharedPreferences` 键 `exp_plugin_sdui_enabled`），**默认 `false`**；仅在「开发者选项」以 `dev_row_exp_sdui` 行暴露，副标题明确“默认关闭”。
- 开关只写 UI 偏好，**不触碰权限、审批或任何安全门禁**；关闭后插件详情页立即 `GONE` 隐藏实验卡片，已生成的版本化缓存**保留**（用户可显式「清除缓存」）。
- 未开启时产品行为与 ADR-0034 / 0036 完全一致：详情页只有官方 `settings/*` 实时配置卡与「浏览器页面扩展说明」（方案 A）卡。

### 2. 三级获取链路：缓存 → 包内置清单 → Agent 生成

**第一级：版本化缓存（`PluginSduiStore`）**

- 路径固定为 `<user-data>/.dsh/dshmobile/sdui-cache/<包名>@<版本>.json`，文件名对包名/版本做字符白名单与 `..` 折叠，并对 `Path.GetFullPath` 结果做目录前缀校验，杜绝路径穿越。
- 单文件上限 **512 KiB**；读取时校验 `pluginPackage`、`pluginVersion` 与 Schema，任一不符即删除该缓存并降级；写入用“临时文件 + `File.Move(overwrite)`”原子替换。
- 缓存是加速路径：写失败不影响本次已校验文档的返回，绝不返回“看似成功”。

**第二级：包内置清单（`GuestPluginService.ReadLocalSduiManifest`）**

- 读取已安装包目录下的 `dshmobile.sdui.json`，先查 `profiles/web/node_modules`，未命中再回退只读层 `layers/dsh/node_modules`。
- **不启动 guest 进程**，直接按宿主文件系统路径解析，并用既有边界函数 `TryResolvePathWithinBoundary` 约束在 `node_modules` 之内；包名先过 `PluginCommandRules.IsValidPackageName`。
- 上限 **256 KiB**，超限返回稳定错误码 `PLUGIN_SDUI_MANIFEST_TOO_LARGE`（不把截断 JSON 交给解析器）；缺失/不可读一律 fail-closed。
- 清单通过完整 Schema 校验且版本匹配时，回写第一级缓存。

**第三级：Agent 生成（`PluginSduiGeneratorService`）**

- 要求网关声明 `Prompt` 能力，否则 `sdui.generator_unavailable`；创建**专用隐藏会话**（立即写入 `HiddenSessionStore`，不污染用户会话列表），结束后尽力 `ArchiveSessionAsync`。
- 提示词明确工作区真实路径：**`/root/projects/.dsh/profiles/web/node_modules/<pkg>`**（即 `$DSH_HOME/profiles/web/node_modules/<pkg>`，宿主侧对应 PRoot 工作区 `projects/.dsh/profiles/web/node_modules`）。要求只读分析 `package.json`、`lib/types/**/*.d.ts`、`README.md`，禁止写入/安装/联网；目录不可读时只允许输出 `{"error":"..."}`。
- 等待策略：单次 `WaitForSession` 上限 **15 秒**，总超时 **3 分钟**；分片超时继续续等；`InputRequired` 立即 `CancelTurn` 并以 `sdui.input_required` 失败。
- 输出处理：取最后一条 `assistant/message` 的 text 片段 → 剥离 Markdown 围栏 → `JsonSerializer`（源生成上下文，AOT/Trim 安全）→ Schema 校验。
- **最多一次同会话纠偏**：仅对 `json_parse_failed` / `schema_invalid` 追加纠偏提示（复用同一 `sessionId`，列出前 8 条错误），仍失败则如实失败；Agent 显式 `{"error":...}` 属于“已明确无法完成”，立即失败且不再纠偏，错误摘要折叠控制字符并限长 200 字符。
- 所有失败路径返回稳定诊断键（`sdui.*` / `plugin_sdui.*`），并映射到 `DshFailureCode`；**绝不生成空面板或占位假数据**。

### 3. 扩展 RPC 直通：`POST /api/<namespace>/<method>`，白名单 + fail-closed

- 新增 `IPluginExtensionGateway.InvokePluginRpcAsync`，默认实现 fail-closed（`Unsupported`），仅由 `RemoteDshGatewayClient` 覆盖。
- `PluginRpcEndpointValidator` 的放行条件：**恰好两段** `namespace/method`；总长 ≤128、单段 ≤64；每段以字母/数字开头，仅含 `[A-Za-z0-9_.-]`；拒绝 `..`、空白与控制字符；命名空间不得属于核心保留集合（`session`、`commands`、`credentials`、`settings`、`pluginInventory`、`pluginManager`、`workspace`、`fs`、`system`、`relay`、`ui`、`remote`、`connection`、`webserver`、`typert`，大小写不敏感）。
- 校验在**联网之前**完成，失败返回 `plugin_sdui.forbidden_rpc_endpoint`；通过后统一 `CallRawAsync(endpoint, args)`，即 `POST /api/<endpoint>`，`Undefined`/`Null` 参数规范化为 `{}`。
- **关键实测结论**：在 `TrimmedWeb: true`（不释放 `modules` 与 `ui-*`）下，该通道仍可直通 `dsh-opencode-go@0.5.2` 的 **7 个 Typert Unary RPC**，无需开启完整 WebUI 兼容模式，也无需 Custom Tabs。原生面板因此不依赖浏览器，也不扩大 Web 攻击面。

### 4. 全 XML 静态槽位：文档只喂数据，宿主不造控件

- `activity_plugin_sdui.xml` 全静态：`CoordinatorLayout` + `AppBarLayout` + `MaterialToolbar` + `LinearProgressIndicator` + **8 枚预置 Chip 槽位**（`sdui_tab_chip_0..7`）+ 反馈横幅 + `RecyclerView`；标签页只按文档声明数量显示/隐藏既有 Chip，不 `new Chip()`。
- `PluginSduiAdapter` 固定 **18 个 ViewType**：9 类 SDUI 区块（`status_banner`、`metric_grid`、`quota_bar`、`key_value_list`、`segmented_switch`、`list_section`、`action_bar`、`external_link`、`settings_bridge`）+ 列表头/空态/行 + 6 类配置字段（bool/number/text/select/secret/json）。每个 ViewType 对应一张 `item_sdui_*.xml`，`Inflate` 后仅做属性绑定。
- 文档上限同时约束渲染复杂度：tabs 1..8、blocks ≤64、sources ≤16、actions ≤32、`refreshIntervalSec ≥15`（防止第三方文档把手机变成刷新器）。
- 视图模型输出**强类型不可变** `SduiRenderItem` 列表；宿主侧不存在通用表达式求值、反射调用或脚本执行。`$bind` 仅允许 `sources` / `ui` / `item` / `form` / `result` 五个根，`$gen` 仅允许 `hex32` / `uuid`。
- 条件显示只支持 `visibleWhen` 的受限比较；外部链接与 `open_url` 仅允许绝对 `https://`；`open_form` 的 `submitAction` 必须引用 `actions` 中已有 id，禁止内联动作对象。

### 5. 安全红线

1. **零业务 WebView 不放宽**：`NoBusinessWebViewGateTests`、`XmlFirstUiGateTests` 与 APK 产物门禁保持原样；本切片零新增 WebView、零 JS 引擎。
2. **扩展通道不是核心 API 后门**：15 个核心命名空间永久拒绝；两段式与字符白名单在联网前 fail-closed。
3. **第三方文档视为不可信输入**：先校验后渲染；不含任何“解析失败也显示部分内容”的宽松路径。
4. **只读生成**：提示词禁止写文件、装包、联网；生成会话独立且隐藏，结束即归档。
5. **密钥不落状态、不进日志、不进无障碍文本**：`secret`/`password` 字段以 `InputType` 掩码渲染，表单明文只存在于提交瞬间的局部变量，提交后不写回 `PluginSduiUiState`，也不写入诊断。
6. **不伪造成功**：缓存、清单、生成、RPC 任一失败都返回稳定诊断键并在 UI 可见；`0` 用例不算通过。

---

## 后果与权衡

### 正面

- 第三方插件的核心数据与常用操作可在 App 内以原生 Material 3 呈现，**无需 WebView、无需 Custom Tabs、无需 `TrimmedWeb: false`**，启动与交互延迟远低于打开浏览器。
- 三级链路兼顾“快”和“可离线”：命中缓存毫秒级；作者提供清单时零生成成本；都没有时才付出一次 Agent 生成（数十秒）。
- 文档 Schema 有明确上限与白名单，渲染复杂度可控，低端机不会因第三方文档卡顿。
- 实验开关默认关闭，失败面被限制在开发者选项之内；关闭后仅隐藏卡片，不删除缓存，便于复现与回退。
- 与 ADR-0036 的方案 A 并存：需要完整页面/第三方 JS 时走 WebUI，只需要数据与操作时走原生面板。

### 限制（如实标注，均来自 Android 17 真机（M2007J1SC）实测）

1. **`analytics` 数据源的参数绑定不足**：生成的文档对 `analytics` 端点只给了部分/扁平参数，宿主网关返回 `gateway/input-invalid`，面板显示“数据源 analytics 加载失败”。这是**生成质量**问题而非通道故障——同一通道的模型目录 RPC 正常返回。根因是提示词只要求“按实际类型包裹”，未强制逐端点核对必填字段与枚举取值范围，Schema 亦无法在离线阶段验证业务参数。
2. **`settings_bridge` 未挂载**：`dsh-opencode-go@0.5.2` 声明了官方配置命名空间，但生成的文档没有产出 `settings_bridge` 区块，面板内因此没有出现「插件配置」字段区。`PluginSduiViewModel.EnsureSettingsBridgeViewsAsync` 只在文档声明该区块时才会拉取并挂载字段，属“文档缺项”而非渲染缺陷；官方配置仍可在详情页「实时配置」卡完成。
3. **生成完成后的前台恢复刷新延迟**：生成在后台完成（或用户切走再回来）时，详情页卡片不会立即从“正在生成”切到“已生成”，需要一次手动「重新生成」/重进页面或等待下一次状态刷新才更新。当前实现依赖页面自身状态回调，未在 `OnResume` 主动重读缓存。
4. **`uiautomator dump` 仍可读取 `password="true"` 编辑框的输入内容**：Android 17 真机（M2007J1SC）实测中，`API Key` 编辑框具有 `password="true"`，但当前 `uiautomator dump` 仍能读到其输入内容（同时 App 进程 `DshMobile` 日志与界面状态均 **0 命中**）。这说明掩码只作用于视觉显示，未隔离无障碍树中的文本；因此不能把 `password="true"` 当作“无明文泄露”的正面证据，后续应补充无障碍遮罩或 `importantForAccessibility` 评估。
5. 面板只覆盖声明式 Schema 能表达的能力：复杂图表、拖拽、富文本、插件自定义交互仍必须回到 WebUI（方案 A）。
6. 生成依赖模型质量与插件类型声明的完整度；对混淆/无 `.d.ts` 的包，产出稳定性会下降，最坏情况如实失败。

### 改进建议（后续切片）

- **参数级校验**：在提示词中要求逐端点列出 `input`/`command`/`query` 的必填字段与枚举；生成后对每个 `rpc` 数据源做一次“空参数探针”，把 `gateway/input-invalid` 类错误在保存前暴露，并纳入纠偏提示。
- **`settings_bridge` 强制项**：当 `package.json` 声明配置命名空间时，校验器对“文档缺少 `settings_bridge`”给出可纠偏的校验错误，而不是静默通过。
- **生命周期刷新**：`PluginDetailFragment.OnResume` 与 `PluginSduiActivity.OnResume` 主动重读缓存/状态，消除后台完成后的陈旧显示。
- **验收手段**：密码类字段改用“提交后副作用断言”（如账户列表新增条目）验证，不依赖 dump 读值；同时针对上述 `uiautomator dump` 可读问题，补充无障碍遮罩或 `importantForAccessibility` 评估，确认掩码字段不会把明文暴露到无障碍树。
- **文档同步**：按 AGENTS.md 规则 9，在 `docs/MIGRATION.md` 标注本特性为实验性、默认关闭，并登记上述真机限制。

---

## 验证

**单元测试（`tests/DshMobile.Agent.Tests`）**

- `PluginSduiCoreTests`：三级链路（缓存未命中回退清单、版本升级不复用旧缓存、损坏缓存删除后回退）、生成结果拒绝包名不一致且不落盘、围栏 JSON 保存、隐藏并归档会话、同会话一次纠偏、非法 JSON/保留端点不写缓存、缺少 Prompt 能力 fail-closed、`InputRequired` 取消、Agent 显式报错立即失败。
- `PluginSduiViewModelTests`：并发加载 settings 与全部 RPC 数据源并渲染全部区块类型、`segmented_switch` 变更后带新参数刷新、表单提交使用生成命令且明文密钥不进入状态、数据源失败不抛异常而产出错误项、`settings_bridge` 字段保存后同步刷新。
- `PluginRpcEndpointValidatorTests`：两段式、保留命名空间、路径穿越、控制字符、超长与非法字符全部拒绝。
- `GuestPluginServiceTests` / `RemoteDshGatewayClientTests`：清单读取边界与 256 KiB 上限、扩展 RPC 校验失败不发起请求、合法端点直通 `POST /api/<endpoint>`。
- `NoBusinessWebViewGateTests`、`XmlFirstUiGateTests` 保持通过，未放宽。

**真机验收（2026-11-28，Android 17 真机 M2007J1SC / LineageOS 24 / API 37）**

- 默认态：`dev_row_exp_sdui` 的 `row_switch` 为 `checked=false`，未做任何写操作即满足预期。
- 关闭态：`PluginDetailActivity` 完整视图树中 `contains(@resource-id,"sdui")` 计数为 **0**，即 `plugin_detail_sdui_card` 与 `plugin_detail_sdui_primary_btn` **彻底不在视图树中**（`GONE` 而非 `INVISIBLE`）；「浏览器页面扩展说明」卡与「开启完整 WebUI 兼容模式并重启 DSH」按钮均正常存在。
- 开启态：生成并写入版本化缓存（详情页显示“已生成原生面板缓存（插件 v0.5.2，来源：版本化缓存）”），面板页渲染「DSH OpenCode Go 控制台」多标签页。
- RPC 直通：7 个 Typert Unary RPC 均在 `TrimmedWeb: true` 下经 `POST /api/opencode-go/<method>` 到达网关；模型目录成功，`analytics` 返回 `gateway/input-invalid`、`usage` 返回 `opencode-go/usage-unavailable`（见限制 1）。
- 表单：`添加账户` 弹窗包含账户标识、显示名称、`API Key`（`password="true"`）与订阅套餐；提交路径可用，明文不回显。
- 缓存清理：详情页「清除缓存」返回“已清除该插件的 SDUI 缓存”，随后重新生成可恢复。

**待补**：`analytics` / `usage` 参数修正后的端到端复测、`settings_bridge` 挂载真机验证、前台恢复刷新时机、以及打开完整 WebUI 兼容模式（方案 A）与本面板并存时的切换体验。

---

## 第 2 轮精修与真机闭环（2026-11-28）

本轮针对上文「限制」1–4 做定点修复，并在 Android 17 真机（M2007J1SC）上完成闭环复测；**限制 1–4 全部关闭**，默认关闭策略与安全红线不变。

### 1. `dsh-opencode-go@0.5.2` `AnalyticsQuery` 真实契约与生成边界

第 1 轮生成的文档把 `opencodeGoAnalytics/read` 参数写成扁平结构、且未给 `$bind` 提供 `$default`，网关按 Typert 输入校验直接拒绝为 `gateway/input-invalid`——这是**生成质量**问题，不是 RPC 通道故障。第 2 轮以插件包内 `lib/types/contracts.d.ts` 为准逐字段核对，真实契约为**单层 `query` 包裹**：

```json
{"query":{"accountRef":"<^[A-Za-z_][A-Za-z0-9_]*$>","range":"today"|"7d"|"30d"|"90d","refresh"?:bool}}
```

- `query.accountRef`：必填，须匹配 `^[A-Za-z_][A-Za-z0-9_]*$`（账户引用名，不是邮箱、不是 API Key）；
- `query.range`：必填枚举 `today` / `7d` / `30d` / `90d`；
- `query.refresh`：可选布尔。
- 平铺 `accountRef` / `range`（缺 `query` 包裹），或 `$bind` 引用初始不存在且无 `$default` 的路径，均会被判 `gateway/input-invalid`。

`PluginSduiGeneratorService` 据此收紧生成边界：

| 约束 | 实现要点 |
|---|---|
| 提示词 `.d.ts` 核对 | 强制先只读 `lib/types/**/*.d.ts`（优先 `contracts.d.ts`），逐端点核对输入类型、字段必填性、枚举取值、正则与数值范围；所有可能取不到初始值的 `$bind` 必须给符合类型约束的 `$default`，禁止用 `null`/空串/猜测值敷衍 |
| 禁止越界搜索 | 只允许读 `<workspace>/.dsh/profiles/web/node_modules/<pkg>`；禁止写入/修改/删除、禁止安装命令与联网；目录不可读时只允许输出 `{"error":"..."}` |
| 超时 | 单次生成总超时 **5 分钟**（`DefaultTimeout = TimeSpan.FromMinutes(5)`，分片等待续期） |
| 只读试跑 | 结构与 Schema 通过后，仅用**空 `sources` 根 + 文档初始 UI 状态**只读试跑 `sources(kind=rpc)`；**绝不自动执行 `actions`**，不做任何写操作 |
| 纠偏范围 | 只有协议/输入层错误（如 `gateway/input-invalid`）进入纠偏；业务错误（如 `opencode-go/usage-unavailable`）、网关不可用与超时**不纠偏**，静默放行 |
| 纠偏上限 | 结构/JSON 纠偏与只读试跑纠偏**共享** `MaxRepairRounds = 2` 轮；耗尽仍失败即 fail-closed，绝不落盘或伪造成功 |

### 2. 五处精修实现要点

1. **RPC 默认反馈**：新增 `SduiActionResultRenderItem` 与 `item_sdui_action_result.xml`，动作/刷新结束后在面板内以结果行明确展示成功或失败诊断，不再“点了没反应”。
2. **`settings_bridge` 自动挂载**：插件声明官方配置命名空间时，即使文档缺该区块也自动挂载字段区；`MutateSettingsFieldAsync` 写回后**即时 `RebuildRenderItems`**，无需退出重进即可看到新值。
3. **`PluginSduiGenerationCoordinator` 应用级生命周期**：以包名为粒度合并/复用在途生成任务（`_inFlight`），生成使用协调器自身 `CancellationTokenSource`，页面销毁不取消后台生成；`PluginDetailFragment` / `PluginSduiActivity` 在 `OnResume` 主动同步快照，消除“后台已完成、页面仍显示正在生成”。
4. **`SecretInputProtection`**：密码框挂无障碍委托——`info.Password = true`、文本替换为**等长 `•`**、`ContentDescription = null`、无障碍事件文本清空；承载表单的对话框窗口加 `WindowManagerFlags.Secure`（`FLAG_SECURE`）；关闭弹窗时 `ClearEditText` 清空明文。由此 `uiautomator dump` 不再能读到明文（见下文真机数据）。
5. **`PluginSduiEvaluator.TryGetTimestamp`**：统一兼容秒级/毫秒级整数（按 `MillisecondsThreshold` 判定 13 位 JS 毫秒时间戳）、浮点秒与 ISO 字符串，避免把毫秒当秒解析出离谱时间。

### 3. 托管测试集、净构建与真机验证数据（2026-11-28，Android 17 真机 M2007J1SC）

**托管测试集（Windows 桌面，`dotnet test`，退出码 0，0 失败）**：

- `DshMobile.Core.Tests`：**101/101**；
- `DshMobile.Runtime.Tests`：**1023/1025**（**2 例环境跳过**，均为 `XdgUserDirShimTests` 的 Windows 无 XDG 场景，非本轮失败）；
- `DshMobile.Application.Tests`：**162/162**；
- `DshMobile.Agent.Tests`：**310/310**（含 `PluginSduiCoreTests`、`PluginSduiViewModelTests`、`PluginRpcEndpointValidatorTests`、`GuestPluginServiceTests`、`RemoteDshGatewayClientTests` 及既有质量门禁）。

**净构建 Release APK**：**46,474,417 B**，SHA-256 **`19F6ED84817ABAB1EB449D14AB05761A33968061FA5AC929561F5BC2CD8D60D9`**（本机 `Get-FileHash` 复核一致）；APK 产物门禁 `NoBusinessWebViewGateTests` **4/4** 通过（源码 / Manifest / XML 布局 / `classes*.dex` 字符串表零业务 WebView）。

**真机**：Xiaomi M2007J1SC / LineageOS 24 / Android 17（API 37），`TrimmedWeb: true`（不释放 `modules` 与 `ui-*`）。

| 验证项 | 实测结果 |
|---|---|
| `opencodeGoAnalytics/read` | 返回 `opencode-go/analytics-unavailable`（业务层不可用），**不再出现 `gateway/input-invalid`**，证明 `query` 包裹与 `$default` 契约正确 |
| `opencodeGoModels/probe` | **`908 ms · go`**（模型目录 RPC 正常） |
| `settings_bridge` | 将字段改为 `65000` 后重进页面仍保留；重置为默认后**即时恢复 `60000`** |
| 息屏恢复 | 息屏 10 秒后唤醒，面板与宿主状态正常恢复 |
| 密码框加固 | 在「添加 OpenCode Go 账号」弹窗输入测试串后：完整 `uiautomator dump` 中测试串与片段出现次数均为 **0**，第 2 个 `sdui_form_input_edit` 仅 `text="••••…"`（19 个 `•`，与输入等长）、`password="true"`、`content-desc` 为空；弹窗窗口 `fl=` 含 **`SECURE`**，截屏为黑屏 |
| 日志泄露 | App `logcat` 中测试串 **0 命中**（唯一命中来自 `adbd` 对注入命令本身的回显，非 App 输出） |

### 4. 遗留限制（如实标注）

- 本特性仍**只适用于结构化/表单类的“第二档面板插件”**：Canvas、强自定义图形（图表叠加）、拖拽、富文本与插件自定义交互不在声明式 Schema 覆盖范围内，必须回到 ADR-0036 的 WebUI 路径。
- 生成质量仍依赖插件 `.d.ts` 的完整度与模型能力；对混淆/无类型声明的包，最坏情况如实失败（`sdui.*` 诊断键），不产出空面板。
- 真机证据仅覆盖 Android 17 真机（M2007J1SC）单机（LineageOS），其他 ROM 与 Android 版本待补。
