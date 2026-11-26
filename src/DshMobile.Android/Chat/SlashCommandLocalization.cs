namespace DshMobile.App.Chat;

/// <summary>
/// 斜杠命令说明的轻量中文本地化映射：命中常见命令时用简洁中文覆盖英文描述，
/// 未命中（例如插件提供的自定义命令）保留服务端原描述，不伪造能力。
/// </summary>
internal static class SlashCommandLocalization
{
    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/compact"] = "压缩当前会话上下文",
        ["/export"] = "导出当前会话记录",
        ["/feedback"] = "提交使用反馈",
        ["/goal"] = "设置长程持续执行目标",
        ["/clear"] = "清空当前会话上下文",
        ["/help"] = "查看可用命令与用法",
        ["/model"] = "查看或切换当前模型",
        ["/status"] = "查看会话与运行状态",
        ["/permission"] = "切换或查看会话权限模式",
        ["/plan"] = "进入分步规划模式",
        ["/resume"] = "恢复指定会话",
        ["/review"] = "审查当前变更",
        ["/init"] = "初始化项目配置",
        ["/memory"] = "查看或编辑长期记忆",
        ["/cost"] = "查看本次会话用量",
        ["/new"] = "新建会话",
        ["/stop"] = "停止当前轮次",
        ["/retry"] = "重试上一条消息",
        ["/undo"] = "撤销上一步操作",
        ["/diff"] = "查看工作区变更",
        ["/files"] = "浏览工作区文件",
        ["/terminal"] = "打开终端",
        ["/settings"] = "打开设置",
    };

    /// <summary>按命令名返回中文说明；未命中时原样返回服务端描述。</summary>
    public static string? Describe(string commandName, string? original)
    {
        var normalized = Normalize(commandName);
        return Descriptions.TryGetValue(normalized, out var localized) ? localized : original;
    }

    private static string Normalize(string name)
    {
        var trimmed = name.Trim();
        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }
}
