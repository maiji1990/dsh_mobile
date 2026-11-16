using System.Xml.Linq;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// ADR-0023: the permanent package identity lives in one file and is never re-declared. Changing ApplicationId after a
/// release makes a different app (no upgrade, no data), so this pin fails loudly if anyone edits it. versionCode is
/// derived by PackageIdentity.props (checked by the Android build itself, which prints and validates it).
/// </summary>
public sealed class PackageIdentityTests
{
    private static readonly string Props = Repo.PathOf("src", "DshMobile.Android", "PackageIdentity.props");
    private static readonly string Csproj = Repo.PathOf("src", "DshMobile.Android", "DshMobile.Android.csproj");

    private static IEnumerable<XElement> Properties(string file, string name) =>
        XDocument.Load(file).Descendants().Where(e => e.Name.LocalName == name);

    [Fact]
    public void ApplicationId_IsThePermanentOne()
    {
        var id = Assert.Single(Properties(Props, "ApplicationId"));
        Assert.Equal("io.github.maiji1990.dshmobile", id.Value);
        Assert.DoesNotContain("dshbox", id.Value, StringComparison.OrdinalIgnoreCase); // AGENTS.md rule 4
    }

    [Fact]
    public void Csproj_ImportsTheIdentity_AndDoesNotRedeclareIt()
    {
        Assert.Contains(XDocument.Load(Csproj).Root!.Elements().Where(e => e.Name.LocalName == "Import"),
            e => (string?)e.Attribute("Project") == "PackageIdentity.props");
        foreach (var name in (string[])["ApplicationId", "ApplicationVersion", "ApplicationDisplayVersion"])
        {
            Assert.Empty(Properties(Csproj, name));
        }
    }

    [Fact]
    public void VersionCode_IsDerived_NotAHandWrittenNumber()
    {
        var code = Assert.Single(Properties(Props, "ApplicationVersion"));
        Assert.Contains("$(_DshVersionMajor)", code.Value, StringComparison.Ordinal);
        Assert.Contains("$(DshVersionBuild)", code.Value, StringComparison.Ordinal);
        Assert.Matches(@"^\d{1,3}\.\d{1,2}\.\d{1,2}$", Assert.Single(Properties(Props, "ApplicationDisplayVersion")).Value);
    }
}
