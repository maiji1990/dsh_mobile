using System.Reflection;
using System.Text.RegularExpressions;
using DshMobile.Core.Bridge;

namespace DshMobile.Core.Tests;

/// <summary>
/// Keeps the Java facade and the C# client in lockstep without running Android:
/// scans the Java source for error codes and method names.
/// </summary>
public partial class CrossLanguageContractTests
{
    private static string FacadeSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        var path = Path.Combine(dir.FullName, "native", "native-bridge", "src", "main", "java", "dshmobile", "nativebridge", "DshNativeFacade.java");
        return File.ReadAllText(path);
    }

    [GeneratedRegex("""static final String [A-Z_]+ = "([A-Z_]+)";""")]
    private static partial Regex JavaErrorCode();

    [GeneratedRegex("""case "([a-z][a-z0-9.]*)":""")]
    private static partial Regex JavaMethodCase();

    [Fact]
    public void EveryJavaErrorCode_IsKnownToCSharp()
    {
        var javaCodes = JavaErrorCode().Matches(FacadeSource()).Select(m => m.Groups[1].Value).ToHashSet();
        Assert.NotEmpty(javaCodes);
        Assert.Subset(BridgeErrorCodes.All.ToHashSet(), javaCodes);
    }

    [Fact]
    public void JavaMethods_MatchCSharpMethodConstants()
    {
        var javaMethods = JavaMethodCase().Matches(FacadeSource()).Select(m => m.Groups[1].Value).ToHashSet();
        var csharpMethods = typeof(BridgeMethods)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet();
        Assert.Equal(csharpMethods, javaMethods);
    }

    [Fact]
    public void ProtocolVersion_Matches()
    {
        Assert.Contains($"PROTOCOL_VERSION = {BridgeProtocol.Version};", FacadeSource());
    }
}
