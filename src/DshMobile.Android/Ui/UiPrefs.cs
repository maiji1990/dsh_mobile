using Android.Content;
using AndroidX.AppCompat.App;

namespace DshMobile.App.Ui;

/// <summary>
/// UI-only preferences (ADR-0021). Developer options (diagnostics, probes, PTY self-test, log, Relay surface preference)
/// are hidden until the user taps the version row seven times, like Android's own developer options. Nothing here
/// changes a permission or a security gate.
/// </summary>
public static class UiPrefs
{
    private const string File = "dshmobile-ui";
    private const string DeveloperKey = "developer_options";

    /// <summary>Taps on the version row that turn developer options on.</summary>
    public const int DeveloperTaps = 7;

    public static bool DeveloperEnabled(Context context) =>
        context.GetSharedPreferences(File, FileCreationMode.Private)!.GetBoolean(DeveloperKey, false);

    public static void SetDeveloperEnabled(Context context, bool on)
    {
        var editor = context.GetSharedPreferences(File, FileCreationMode.Private)!.Edit()!;
        editor.PutBoolean(DeveloperKey, on);
        editor.Apply();
    }

    private const string StartupTraceKey = "startup_trace";

    /// <summary>
    /// Detailed startup timing (docs/STARTUP_PERF.md). Off unless developer options are on, so a normal install pays
    /// nothing; read once per start, never cached. There is no separate switch in the UI yet - the key exists so a
    /// future developer-options row can override it without touching the callers.
    /// </summary>
    public static bool StartupTraceEnabled(Context context) =>
        context.GetSharedPreferences(File, FileCreationMode.Private)!.GetBoolean(StartupTraceKey, DeveloperEnabled(context));

    public static void SetStartupTraceEnabled(Context context, bool on)
    {
        var editor = context.GetSharedPreferences(File, FileCreationMode.Private)!.Edit()!;
        editor.PutBoolean(StartupTraceKey, on);
        editor.Apply();
    }

    private const string ChannelKey = "update_channel";

    /// <summary>The release channel the user follows (vNext PR-06): <c>stable</c> (default) or <c>preview</c>.</summary>
    public static string UpdateChannel(Context context) =>
        context.GetSharedPreferences(File, FileCreationMode.Private)!.GetString(ChannelKey, null) is "preview" ? "preview" : "stable";

    public static void SetUpdateChannel(Context context, string channel)
    {
        var editor = context.GetSharedPreferences(File, FileCreationMode.Private)!.Edit()!;
        editor.PutString(ChannelKey, channel == "preview" ? "preview" : "stable");
        editor.Apply();
    }

    private const string AutoCheckKey = "auto_check_updates";
    private const string LastAutoCheckKey = "last_auto_check_utc_ms";

    /// <summary>ADR-0029: 自动检查通道清单的最小节流间隔（6 小时）。</summary>
    public static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(6);

    /// <summary>启动 / 回到前台时是否静默检查签名通道清单（默认开启；只读拉取清单，不自动安装）。</summary>
    public static bool AutoCheckUpdatesEnabled(Context context) =>
        context.GetSharedPreferences(File, FileCreationMode.Private)!.GetBoolean(AutoCheckKey, true);

    public static void SetAutoCheckUpdatesEnabled(Context context, bool enabled)
    {
        var editor = context.GetSharedPreferences(File, FileCreationMode.Private)!.Edit()!;
        editor.PutBoolean(AutoCheckKey, enabled);
        editor.Apply();
    }

    public static long LastAutoCheckUtcMs(Context context) =>
        context.GetSharedPreferences(File, FileCreationMode.Private)!.GetLong(LastAutoCheckKey, 0L);

    public static void SetLastAutoCheckUtcMs(Context context, long utcMs)
    {
        var editor = context.GetSharedPreferences(File, FileCreationMode.Private)!.Edit()!;
        editor.PutLong(LastAutoCheckKey, utcMs);
        editor.Apply();
    }

    private const string EagerWarmupKey = "eager_warmup";

    /// <summary>
    /// 5A：回到 App 时是否在后台静默启动 DSH（默认开启）。仍受低电量（≤15% 未充电）、省电模式、
    /// 温控阶梯与“用户按过停止”约束；用户可随时在「更多 → 运行环境」关闭。失败只写操作日志。
    /// </summary>
    public static bool EagerWarmupEnabled(Context context) =>
        context.GetSharedPreferences(File, FileCreationMode.Private)!.GetBoolean(EagerWarmupKey, true);

    public static void SetEagerWarmupEnabled(Context context, bool enabled)
    {
        var editor = context.GetSharedPreferences(File, FileCreationMode.Private)!.Edit()!;
        editor.PutBoolean(EagerWarmupKey, enabled);
        editor.Apply();
    }

    private const string WebInventoryDismissedKey = "web_profile_inventory_dismissed";

    /// <summary>PR-05 N-05-05: 仅记录用户是否已关闭只读升级迁移横幅（布尔偏好，不含任何路径或凭据内容）。</summary>
    public static bool WebInventoryDismissed(Context context) =>
        context.GetSharedPreferences(File, FileCreationMode.Private)!.GetBoolean(WebInventoryDismissedKey, false);

    public static void SetWebInventoryDismissed(Context context, bool dismissed)
    {
        var editor = context.GetSharedPreferences(File, FileCreationMode.Private)!.Edit()!;
        editor.PutBoolean(WebInventoryDismissedKey, dismissed);
        editor.Apply();
    }

    /// <summary>ADR-0031 电源约束：低电量（≤15% 且未充电）或省电模式下不预热，把决定交给用户。</summary>
    public static (bool LowBattery, bool PowerSave) WarmupPower(Context context)
    {
        var power = (Android.OS.PowerManager?)context.GetSystemService(Context.PowerService);
        var battery = (Android.OS.BatteryManager?)context.GetSystemService(Context.BatteryService);
        var capacity = battery?.GetIntProperty((int)Android.OS.BatteryProperty.Capacity) ?? 100;
        var charging = battery?.IsCharging ?? true;
        return (capacity <= 15 && !charging, power?.IsPowerSaveMode ?? false);
    }

    public static bool ShouldAutoCheckUpdates(Context context, DateTimeOffset nowUtc)
    {
        if (!AutoCheckUpdatesEnabled(context))
        {
            return false;
        }
        var lastMs = LastAutoCheckUtcMs(context);
        return nowUtc.ToUnixTimeMilliseconds() - lastMs >= (long)AutoCheckInterval.TotalMilliseconds;
    }

    private const string LanAskedKey = "lan_permission_asked";

    /// <summary>
    /// ADR-0032: the LAN permission is offered once, on the first DSH start. Afterwards the prompt does not reappear on
    /// every start - the "更多 → 局域网访问" row is the explicit way back (a system dialog the user keeps dismissing is
    /// not a feature).
    /// </summary>
    public static bool LanPermissionAsked(Context context) =>
        context.GetSharedPreferences(File, FileCreationMode.Private)!.GetBoolean(LanAskedKey, false);

    public static void SetLanPermissionAsked(Context context, bool asked)
    {
        var editor = context.GetSharedPreferences(File, FileCreationMode.Private)!.Edit()!;
        editor.PutBoolean(LanAskedKey, asked);
        editor.Apply();
    }

    private const string DynamicColorKey = "dynamic_color_enabled";

    /// <summary>
    /// 是否启用 Material You 壁纸动态取色。默认关闭，避免医院灰绿等壁纸把品牌色盘染成不可控色调；
    /// 关闭时始终使用 colors.xml 中的 Graphite 中性品牌色盘。
    /// </summary>
    public static bool DynamicColorEnabled(Context context) =>
        context.GetSharedPreferences(File, FileCreationMode.Private)!.GetBoolean(DynamicColorKey, false);

    public static void SetDynamicColorEnabled(Context context, bool enabled)
    {
        var editor = context.GetSharedPreferences(File, FileCreationMode.Private)!.Edit()!;
        editor.PutBoolean(DynamicColorKey, enabled);
        editor.Apply();
    }

    private const string ThemeModeKey = "theme_mode";

    /// <summary>外观模式：跟随系统。</summary>
    public const string ThemeModeSystem = "system";

    /// <summary>外观模式：始终浅色。</summary>
    public const string ThemeModeLight = "light";

    /// <summary>外观模式：始终深色。</summary>
    public const string ThemeModeDark = "dark";

    /// <summary>
    /// 读取外观模式偏好。仅接受 system / light / dark，未知或缺失值安全兜底为 system（跟随系统）。
    /// </summary>
    public static string ThemeMode(Context context)
    {
        var value = context.GetSharedPreferences(File, FileCreationMode.Private)!.GetString(ThemeModeKey, ThemeModeSystem);
        return value is ThemeModeLight or ThemeModeDark ? value : ThemeModeSystem;
    }

    /// <summary>写入外观模式偏好；非法值统一落回 system，保证持久化内容始终可解析。</summary>
    public static void SetThemeMode(Context context, string mode)
    {
        var normalized = mode is ThemeModeLight or ThemeModeDark ? mode : ThemeModeSystem;
        var editor = context.GetSharedPreferences(File, FileCreationMode.Private)!.Edit()!;
        editor.PutString(ThemeModeKey, normalized);
        editor.Apply();
    }

    /// <summary>
    /// 是否启用「完整 WebUI 与页面插件兼容模式」（ADR-0034，默认关闭）。
    /// 开启并重启 DSH 后宿主以非精简模式启动，释放 <c>modules</c> 与 <c>ui-*</c> 前端模块，
    /// 使 <c>dsh.client</c> 页面修改类插件可通过系统浏览器在本机官方 WebUI 中生效。
    /// </summary>
    private const string FullWebUiCompatKey = "full_webui_compat_enabled";

    public static bool FullWebUiCompatEnabled(Context context) =>
        context.GetSharedPreferences(File, FileCreationMode.Private)!.GetBoolean(FullWebUiCompatKey, false);

    public static void SetFullWebUiCompatEnabled(Context context, bool enabled)
    {
        var editor = context.GetSharedPreferences(File, FileCreationMode.Private)!.Edit()!;
        editor.PutBoolean(FullWebUiCompatKey, enabled);
        editor.Apply();
    }

    private const string ExperimentalPluginSduiKey = "exp_plugin_sdui_enabled";

    /// <summary>
    /// 实验开关：是否允许在插件详情页为支持扩展 RPC 的第三方插件提取并原生渲染 SDUI 面板（默认关闭）。
    /// 仅影响界面展示与按需生成，不改变任何权限审批、写操作门禁或宿主安全策略。
    /// </summary>
    public static bool ExperimentalPluginSduiEnabled(Context context) =>
        context.GetSharedPreferences(File, FileCreationMode.Private)!.GetBoolean(ExperimentalPluginSduiKey, false);

    /// <summary>写入实验开关偏好；关闭后插件详情页会立即隐藏 SDUI 卡片，已生成的缓存保留。</summary>
    public static void SetExperimentalPluginSduiEnabled(Context context, bool enabled)
    {
        var editor = context.GetSharedPreferences(File, FileCreationMode.Private)!.Edit()!;
        editor.PutBoolean(ExperimentalPluginSduiKey, enabled);
        editor.Apply();
    }

    /// <summary>
    /// 把外观模式偏好映射到 AppCompat 的默认夜间模式。Application 启动时调用一次即可覆盖后续 Activity。
    /// </summary>
    public static void ApplyThemeMode(Context context)
    {
        AppCompatDelegate.DefaultNightMode = ThemeMode(context) switch
        {
            ThemeModeLight => AppCompatDelegate.ModeNightNo,
            ThemeModeDark => AppCompatDelegate.ModeNightYes,
            _ => AppCompatDelegate.ModeNightFollowSystem,
        };
    }
}
