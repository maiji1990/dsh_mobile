using System.Text;
using System.Text.RegularExpressions;

namespace DshMobile.Agent.Tests.Quality;

/// <summary>
/// UI Slice 4 静态防回退门禁：
/// 1. 禁止再通过 <c>Rows.Add</c>/<c>Header</c>/<c>Card</c>/<c>MonoCard</c> 或 <c>fragment_list</c>/<c>item_row</c> 命令式拼装界面；
/// 2. 除 Markdown AST 原生渲染器（<c>ChatMarkdownBinder.cs</c>）外，Android UI 代码禁止实例化命令式控件/容器（统一用 XML）；
/// 3. 安全审批 XML 必须显式声明 <c>android:filterTouchesWhenObscured="true"</c> 防叠加点击劫持。
/// </summary>
public sealed partial class XmlFirstUiGateTests
{
    private static readonly string[] ForbiddenLegacyTokens =
    [
        "Rows.Add(",
        "Rows.Header(",
        "Rows.Card(",
        "Rows.MonoCard(",
        "fragment_list",
        "item_row",
        "item_section_header",
    ];

    [GeneratedRegex(@"\bnew\s+(?:LinearLayout|FrameLayout|RelativeLayout|ScrollView|NestedScrollView|MaterialCardView|MaterialButton|MaterialSwitch|SwitchMaterial|TextView|EditText|ImageView|LinearProgressIndicator|TextInputLayout|TextInputEditText|Chip|ChipGroup)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex ImperativeViewCtorRegex();

    [Fact]
    public void AndroidUiCode_HasZeroImperativeViewConstructionOrLegacyRowsBuilders()
    {
        var repoRoot = RepoLocator.FindRepoRoot();
        var androidRoot = Path.Combine(repoRoot, "src", "DshMobile.Android");

        foreach (var file in Directory.EnumerateFiles(androidRoot, "*.cs", SearchOption.AllDirectories))
        {
            var normalized = file.Replace('\\', '/');
            if (normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var codeOnly = StripCSharpComments(File.ReadAllText(file));
            foreach (var token in ForbiddenLegacyTokens)
            {
                Assert.False(
                    codeOnly.Contains(token, StringComparison.Ordinal),
                    $"发现已废弃的命令式 UI 构造符号 '{token}' 于 {normalized}");
            }

            if (normalized.EndsWith("/Chat/ChatMarkdownBinder.cs", StringComparison.OrdinalIgnoreCase))
            {
                // Markdown 代码块/表格的动态文本段落允许使用 span/布局容器；普通页面与弹窗一律禁止。
                continue;
            }

            var match = ImperativeViewCtorRegex().Match(codeOnly);
            Assert.False(
                match.Success,
                $"禁止在 Android 业务代码直接实例化控件 '{match.Value}'，请迁移至 XML 布局 ({normalized})");
        }
    }

    [Fact]
    public void ObsoleteListRowXmlRemoved_AndApprovalSecurityAttributesDeclared()
    {
        var repoRoot = RepoLocator.FindRepoRoot();
        var layoutRoot = Path.Combine(repoRoot, "src", "DshMobile.Android", "Resources", "layout");

        Assert.False(File.Exists(Path.Combine(layoutRoot, "fragment_list.xml")), "fragment_list.xml 应在 Slice 4 删除");
        Assert.False(File.Exists(Path.Combine(layoutRoot, "item_row.xml")), "item_row.xml 应在 Slice 4 删除");
        Assert.False(File.Exists(Path.Combine(layoutRoot, "item_section_header.xml")), "item_section_header.xml 应在 Slice 4 删除");

        var cardXml = File.ReadAllText(Path.Combine(layoutRoot, "view_relay_approval_card.xml"));
        var confirmXml = File.ReadAllText(Path.Combine(layoutRoot, "activity_relay_approval_confirm.xml"));

        Assert.Contains("android:filterTouchesWhenObscured=\"true\"", cardXml, StringComparison.Ordinal);
        Assert.Contains("android:filterTouchesWhenObscured=\"true\"", confirmXml, StringComparison.Ordinal);
    }

    [Fact]
    public void LandscapeNoExtractUiAndResponsiveMaxWidth_AreDeclaredAndConsumed()
    {
        var repoRoot = RepoLocator.FindRepoRoot();
        var layoutRoot = Path.Combine(repoRoot, "src", "DshMobile.Android", "Resources", "layout");
        var resRoot = Path.Combine(repoRoot, "src", "DshMobile.Android", "Resources");

        var chatXml = File.ReadAllText(Path.Combine(layoutRoot, "fragment_chat.xml"));
        var searchXml = File.ReadAllText(Path.Combine(layoutRoot, "view_search_pill.xml"));
        var editorXml = File.ReadAllText(Path.Combine(layoutRoot, "activity_file_editor.xml"));

        Assert.Contains("flagNoExtractUi", chatXml, StringComparison.Ordinal);
        Assert.Contains("flagNoExtractUi", searchXml, StringComparison.Ordinal);
        Assert.Contains("flagNoExtractUi", editorXml, StringComparison.Ordinal);

        var narrowDimens = File.ReadAllText(Path.Combine(resRoot, "values", "dimens.xml"));
        var wideDimens = File.ReadAllText(Path.Combine(resRoot, "values-w600dp", "dimens.xml"));
        var edgesCs = File.ReadAllText(Path.Combine(repoRoot, "src", "DshMobile.Android", "Ui", "Edges.cs"));

        Assert.Contains("dshm_content_max_width", narrowDimens, StringComparison.Ordinal);
        Assert.Contains("dshm_content_max_width", wideDimens, StringComparison.Ordinal);
        Assert.Contains("ClampMaxWidth", edgesCs, StringComparison.Ordinal);
        Assert.Contains("dshm_content_max_width", edgesCs, StringComparison.Ordinal);
    }

    private static string StripCSharpComments(string source)
    {
        var sb = new StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            if (source[i] == '/' && i + 1 < source.Length)
            {
                if (source[i + 1] == '/')
                {
                    while (i < source.Length && source[i] != '\n')
                    {
                        i++;
                    }
                    continue;
                }
                if (source[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                    {
                        i++;
                    }
                    i += 2;
                    continue;
                }
            }
            sb.Append(source[i++]);
        }
        return sb.ToString();
    }
}
