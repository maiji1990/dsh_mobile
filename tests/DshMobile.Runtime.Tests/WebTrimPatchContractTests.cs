using System.Text.RegularExpressions;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// 受控裁剪 <c>--patch</c> 的安全契约（ADR-0033）：裁剪只允许禁用浏览器前端 / 遥测行，
/// 后端控制器、连接层与 <c>ui_*</c> 工具层都不得出现在禁用列表里，否则原生客户端会失去
/// 会话、凭据、插件、工作区等核心能力。任务 2 起生产宿主默认启用该裁剪。
/// </summary>
public sealed class WebTrimPatchContractTests
{
    private static readonly string PatchFile =
        Repo.PathOf("src", "DshMobile.Runtime", "Guest", "Assets", "dsh-web-trim-patch.yml");

    private static readonly Regex IdLine = new(@"^\s*-\s*id:\s*(\S+)\s*$", RegexOptions.Compiled);

    private static readonly Regex DisabledLine = new(@"^\s{2}disabled:\s*true\s*$", RegexOptions.Compiled);

    /// <summary>原生客户端必需的控制器 / 服务 id，必须始终保留。</summary>
    private static readonly string[] RequiredIds =
    [
        "connection",
        "session-controller",
        "settings-controller",
        "plugin-inventory",
        "workspace-controller",
        "mcp-manager",
        "schedule",
        "web-runtime",
    ];

    /// <summary>允许禁用的非 <c>ui-*</c> 浏览器行（前端壳、遥测、本地打开器、HMR、模块面板等）。</summary>
    private static readonly string[] BrowserOnlyAllowlist =
    [
        "desktop-product-telemetry",
        "product-analytics",
        "open-in-app",
        "directory-picker",
        "client-hmr",
        "harness-dev-context",
        "modules",
        "locale",
        "shortcuts",
        "resources",
        "entry-customization",
        // 突破口 C：服务端热更新（plugin-manager 中为 optional peer）与纯遥测，均不承载用户能力。
        "hmr",
        "otel",
        "session-telemetry-otel",
    ];

    private static List<string> DisabledIds() => File.ReadLines(PatchFile)
        .Select(line => IdLine.Match(line))
        .Where(match => match.Success)
        .Select(match => match.Groups[1].Value)
        .ToList();

    [Fact]
    public void Patch_DoesNotDisableRequiredControllersOrServices()
    {
        var disabled = DisabledIds();
        foreach (var id in RequiredIds)
        {
            Assert.DoesNotContain(id, disabled);
        }
    }

    [Fact]
    public void Patch_OnlyDisablesBrowserFacingRows()
    {
        var disabled = DisabledIds();
        Assert.NotEmpty(disabled);

        foreach (var id in disabled)
        {
            Assert.True(
                id.StartsWith("ui-", StringComparison.Ordinal) || BrowserOnlyAllowlist.Contains(id),
                $"裁剪 patch 禁用了未登记的非浏览器行 '{id}'；新增项前必须先确认它不是原生客户端依赖。");
        }
    }

    [Fact]
    public void Patch_CoversTheBrowserUiSet_AndIsASingleWellFormedSequence()
    {
        var disabled = DisabledIds();

        Assert.Equal(disabled.Count, disabled.Distinct(StringComparer.Ordinal).Count());
        Assert.True(disabled.Count >= 59, $"裁剪项应不少于 59 条，实际 {disabled.Count}");
        Assert.True(disabled.Count(id => id.StartsWith("ui-", StringComparison.Ordinal)) >= 55);
        // ui_* 工具层走独立的 MCP 插入行，任何带下划线的工具名都不允许被写进这个裁剪列表。
        Assert.DoesNotContain(disabled, id => id.Contains('_'));

        // 顶层必须是单个 YAML 序列：有效行只能是 "- id: x" 或缩进的 "  disabled: true"（注释/空行除外）。
        var content = File.ReadAllLines(PatchFile)
            .Where(line => line.Length > 0 && !line.TrimStart().StartsWith('#'))
            .ToList();
        Assert.All(content, line => Assert.True(
            IdLine.IsMatch(line) || DisabledLine.IsMatch(line),
            $"裁剪 patch 含意外行：{line}"));
    }

    /// <summary>突破口 C：hmr 在 dsh-plugin-manager 中声明为 optional peer，otel 两项为纯遥测。</summary>
    [Fact]
    public void Patch_DisablesHmrAndTelemetryOnly()
    {
        var disabled = DisabledIds();

        Assert.Contains("hmr", disabled);
        Assert.Contains("otel", disabled);
        Assert.Contains("session-telemetry-otel", disabled);
        // 插件管理器本身（而非 hot-reload 服务）必须保持可用。
        Assert.DoesNotContain("plugin-manager", disabled);
        Assert.DoesNotContain("plugin-inventory", disabled);
    }
}
