using System.Xml.Linq;

namespace DshMobile.Agent.Tests.Quality;

public sealed class ArchitectureBoundaryTests
{
    private static readonly string[] ForbiddenAgentNamespaces =
    [
        "Android.",
        "Java.",
        "Google.Android.",
        "AndroidX.",
        "Xamarin.",
        "DshMobile.App",
        "DshMobile.Runtime",
        "DshMobile.Application",
        "DshMobile.Native",
    ];

    private static readonly string[] PureManagedProjects =
    [
        "src/DshMobile.Core",
        "src/DshMobile.Native",
        "src/DshMobile.Runtime",
        "src/DshMobile.Application",
        "src/DshMobile.Agent",
    ];

    private static readonly string[] ForbiddenUiLayerTokens =
    [
        "ProotGuestProcess",
        "ProotSdkProcess",
        "Process.Start(",
        "ClientWebSocket",
        "JsonRpcLineTransport",
    ];

    [Fact]
    public void PureManagedProjects_TargetNet11AndHaveNoAndroidReferences()
    {
        var repoRoot = RepoLocator.FindRepoRoot();
        foreach (var projDir in PureManagedProjects)
        {
            var csproj = Directory.EnumerateFiles(Path.Combine(repoRoot, projDir), "*.csproj").Single();
            var doc = XDocument.Load(csproj);
            var tfm = doc.Descendants("TargetFramework").Single().Value;
            Assert.Equal("net11.0", tfm);

            var refs = doc.Descendants("PackageReference")
                .Select(e => e.Attribute("Include")?.Value ?? string.Empty)
                .Concat(doc.Descendants("ProjectReference").Select(e => e.Attribute("Include")?.Value ?? string.Empty))
                .ToList();

            foreach (var r in refs)
            {
                Assert.DoesNotContain("Android", r, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Xamarin", r, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void DshMobileAgent_HasZeroAndroidOrRuntimeReferences()
    {
        foreach (var file in RepoLocator.EnumerateSourceFiles("src/DshMobile.Agent"))
        {
            var text = File.ReadAllText(file);
            foreach (var forbidden in ForbiddenAgentNamespaces)
            {
                Assert.DoesNotContain($"using {forbidden}", text, StringComparison.Ordinal);
                Assert.DoesNotContain($"global::{forbidden}", text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void AndroidUiScreens_DoNotReferenceLowLevelProcessOrTransportTypes()
    {
        var repoRoot = RepoLocator.FindRepoRoot();
        var uiDirs = new[]
        {
            "src/DshMobile.Android/Home",
            "src/DshMobile.Android/More",
            "src/DshMobile.Android/Files",
            "src/DshMobile.Android/Remote",
            "src/DshMobile.Android/RuntimeUi",
            "src/DshMobile.Android/Ui",
            "src/DshMobile.Android/Chat",
            "src/DshMobile.Android/Chats",
            "src/DshMobile.Android/Credentials",
            "src/DshMobile.Android/Inbox",
            "src/DshMobile.Android/Plugins",
        };

        var files = uiDirs.SelectMany(d => RepoLocator.EnumerateSourceFiles(d)).ToList();
        var mainActivity = Path.Combine(repoRoot, "src/DshMobile.Android/MainActivity.cs");
        if (File.Exists(mainActivity))
        {
            files.Add(mainActivity);
        }

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var token in ForbiddenUiLayerTokens)
            {
                Assert.DoesNotContain(token, text, StringComparison.Ordinal);
            }
        }
    }
}
