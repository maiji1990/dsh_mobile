# ADR-0034: 原生插件详情页、官方 `settings/*` 实时配置与页面修改插件 WebUI 兼容模式

- **状态**：Accepted（2026-11-27）
- **修订**：修订 [ADR-0033](0033-native-agent-client.md) 决策 4（将插件原生体验由 L0/L1 演进为 L0+L1+受控官方 L2，并新增显式按需的完整 WebUI 兼容路径），同时完整保留 ADR-0033 决策 2（全仓零业务 WebView）与决策 3（AOT/Trim 安全、纯托管 `DshMobile.Agent`）。

---

## 背景与问题

1. **插件浏览无法查看详情**：插件目录页目前仅在列表卡片上展示极简摘要，无法点击进入查看已安装插件或远端发现插件的完整介绍、README 文档、版本/包信息、扩展类型（后端能力 vs 前端页面扩展）与当前运行诊断。
2. **插件安装后缺少配置入口**：ADR-0033 一期仅开放 L0（只读清单）与 L1（启停/装卸），未接入 DSH 宿主默认已挂载的 `@deepseek-ai/dsh-api-settings-controller`（`settings/describe`、`settings/mutate`、`settings/replace`），导致用户安装可配置插件后无法在 App 内修改参数。
3. **修改页面的插件安装后不生效**：
   - DSH 中修改页面的插件通过 `package.json` 的 `dsh.client` 在浏览器端 `dsh-web-frontend` + `dsh-client-modules` 上运行 React/DOM/Slot 扩展；纯原生 Material 3 客户端不包含浏览器 DOM 引擎。
   - 生产默认以 `TrimmedWeb: true` 启动并附带 `--patch dsh-web-trim-patch.yml`，禁用了 `modules`（`@deepseek-ai/dsh-client-modules`）与全部 55+ `ui-*` 行，导致即使用外部系统浏览器访问本机 `127.0.0.1:3080` 也无法启动官方 WebUI 或加载 `dsh.client` 插件。
   - `GuestPluginService.DetectCustomWebUiFromManifest` 直接跳过符号链接，导致 pnpm `.pnpm` 虚拟存储布局下无法识别第三方 `dsh.client` 与 `dsh.bundle`；非 Bundle 普通 npm 包安装后无法自动挂载到 Cordis 却长期显示“待重启生效”；被 `--patch` 强制裁剪的内置 `ui-*` 行仍允许用户拨动无效开关。

---

## 决策

### 1. 原生插件详情页（L0 增强，零 WebView）
- 修复 `WirePluginLocalizedMeta` 对齐上游 `title` / `description` / `icon` / `error` 字段。
- 接入已默认挂载的 `pluginManager/listBundles` 与 `pluginManager/readBundleDetail(name, language)`，并对本地非 Bundle 包按 `@deepseek-ai/dsh-plugin-detail` 规范在安全路径边界内只读 `package.json`、`locale/<lang>.json` 与 `README.md`；对远端发现插件通过固定 HTTPS 端点 `https://registry.npmjs.org/<pkg>` 拉取完整详情与 README。
- Android 端新建全 XML 化的 `PluginDetailActivity` / `PluginDetailFragment`，复用现有纯托管 Markdig 渲染器展示详情文档，**严禁引入 WebView**。

### 2. 官方 `settings/*` 实时配置表单（受控 L2，严守不自创 Schema 与零 WebView）
- 仅消费 DSH 官方 `@deepseek-ai/dsh-api-settings-controller` 契约：
  - `POST /api/settings/describe`（获取活跃插件的官方 Schemastery JSON、脱敏 `value`/`base`/`user`、密钥槽位 `secrets` 与乐观锁 `revision`）；
  - `POST /api/settings/mutate`（按路径 `set` / `unset`，利用上游 `ConfigEditor` 在无 `hmr` 下同样实现原子写盘与 live reconcile 实时热生效，且不会误删脱敏的 `role('secret')` 密钥槽）；
  - `POST /api/settings/replace`（仅用于无 `role('secret')` 槽位时的整节重置，有密钥槽位时改用逐项 `unset` 保护密钥）。
- **绝不自创专有配置格式或引入重型第三方 JSON Schema 引擎**：仅在纯托管层编写轻量 Schemastery 视图映射器，将 `boolean`/`string`/`number`/`union(const)`/`array`/`role('secret')` 映射为强类型字段视图，复杂嵌套结构降级为受控 JSON 文本编辑或只读展示。
- **严守全 XML 化（`XmlFirstUiGateTests`）**：配置表单采用 `RecyclerView` 多 ViewType 独立 XML 模板渲染，零命令式 `new View(...)`；密钥字段单向写入、绝不回显明文、绝不写入日志或 `SavedState Bundle`。
- 同步将 `PluginInventoryAndViewModelTests` 中历史门禁 `PluginsFeature_HasZeroL2DynamicSchemaFormAndZeroWebView` 演进为 `PluginsFeature_HasZeroWebViewAndUsesOfficialSettingsOnly`：永久锁死零 WebView，并验证不引入自创/外部 JSON Schema 表单库。

### 3. 页面修改插件生效路径与加载链路修复（不破坏零 WebView 门禁）
- **修复安装与识别缺口**：
  - 修复 `GuestPluginService` 扫描 `profiles/web/node_modules/<pkg>` 时安全放行落在 `profiles/web/node_modules` 边界内的 pnpm 符号链接，准确识别 `dsh.client`（`HasCustomWebUi`）与 `dsh.bundle.patch`（`HasBundleManifest`）；
  - 透传非 Bundle 包警告并在 UI 明确标注「非 DSH Bundle，未自动挂载到 Cordis」，消除假“待重启生效”；
  - 识别受 `dsh-web-trim-patch.yml` 强制禁用的行，在精简模式下禁止无效拨动并提示原因。
- **显式按需的「完整 WebUI 兼容模式」+ 系统浏览器安全打开**：
  - 默认继续保持 `TrimmedWeb: true`（原生秒开、低内存占用，不改变默认基线）。
  - 提供用户显式开关 **「完整 WebUI 与页面插件兼容模式」**（持久化在 `UiPrefs`，默认关闭）：开启并重启 DSH 后，宿主以 `TrimmedWeb: false` 启动（释放 `modules` 与 `ui-*`，使 `http://127.0.0.1:<port>` 完整承载官方 WebUI 与所有已安装的 `dsh.client` 页面修改插件）。
  - 在兼容模式就绪时，于插件详情页与设置入口提供 **「在系统浏览器中打开完整 WebUI」**：
    - 仅在用户通过**明确安全二次确认弹窗**（说明仅限本机 `127.0.0.1` 回环、临时将本次启动的 launch token 经浏览 Intent 交由系统浏览器换取 Cookie，停止 DSH 或切回精简模式后令牌立即失效）后，通过 `Intent.ActionView` + `CategoryBrowsable` 打开；
    - 绝不将带 token 的 URI 写入日志、剪贴板或持久化存储；
    - App 内永久保持零业务 WebView（`NoBusinessWebViewGateTests` 全链路不放宽）。

---

## 后果与权衡

- **正面**：用户可直接点击查看任意已装/远端插件的完整文档与元信息；支持对所有暴露官方 `.volatile()` 配置的插件做实时热更新配置；安装 `dsh.client` 页面修改类插件后有真实、100% 原样生效的运行面；默认启动性能与零 WebView 门禁完全不受损。
- **限制（如实标注）**：
  - 非 `.volatile()` 静态配置字段由上游 `settings/*` 拒绝在线写入，暂不开放手改原始 YAML；
  - `dsh.client` 页面修改效果呈现在系统浏览器承载的本机官方 WebUI 中，原生 Material 3 聊天/工作区界面仍由 C# 原生渲染，不执行第三方前端 JS。
