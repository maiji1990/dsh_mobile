using Android.Content;

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
    /// ADR-0031: 回到 App 时是否在后台静默启动 DSH（默认关闭）。开关只在用户明确打开后生效，失败只写操作日志。
    /// </summary>
    public static bool EagerWarmupEnabled(Context context) =>
        context.GetSharedPreferences(File, FileCreationMode.Private)!.GetBoolean(EagerWarmupKey, false);

    public static void SetEagerWarmupEnabled(Context context, bool enabled)
    {
        var editor = context.GetSharedPreferences(File, FileCreationMode.Private)!.Edit()!;
        editor.PutBoolean(EagerWarmupKey, enabled);
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
}
