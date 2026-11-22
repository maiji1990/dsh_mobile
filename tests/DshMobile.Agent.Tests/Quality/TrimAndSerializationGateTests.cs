using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DshMobile.Agent.Tests.Quality;

public sealed partial class TrimAndSerializationGateTests
{
    [GeneratedRegex(@"public\s+(?:sealed\s+|readonly\s+)?(?:partial\s+)?(?:record|class|struct)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Multiline)]
    private static partial Regex PublicProtocolTypeRegex();

    [GeneratedRegex(@"\[JsonSerializable\(\s*typeof\(\s*([A-Za-z0-9_.<>]+)\s*\)\s*\)\]", RegexOptions.Multiline)]
    private static partial Regex JsonSerializableRegex();

    [GeneratedRegex(@"\bobject\b|\bdynamic\b", RegexOptions.Multiline)]
    private static partial Regex UntypedObjectRegex();

    [Fact]
    public void DshMobileAgent_IsTrimmableAndAotCompatible()
    {
        var repoRoot = RepoLocator.FindRepoRoot();
        var csproj = Path.Combine(repoRoot, "src/DshMobile.Agent/DshMobile.Agent.csproj");
        var doc = XDocument.Load(csproj);
        Assert.Equal("true", doc.Descendants("IsTrimmable").Single().Value);
        Assert.Equal("true", doc.Descendants("IsAotCompatible").Single().Value);
    }

    [Fact]
    public void ProtocolDtos_AreAllRegisteredInJsonSerializerContext_WithoutObjectOrReflection()
    {
        var protocolFiles = RepoLocator.EnumerateSourceFiles("src/DshMobile.Agent/Protocol").ToList();
        Assert.NotEmpty(protocolFiles);

        var registeredTypes = new HashSet<string>(StringComparer.Ordinal);
        var declaredDtos = new List<(string File, string TypeName)>();

        foreach (var file in protocolFiles)
        {
            var text = File.ReadAllText(file);

            foreach (Match m in JsonSerializableRegex().Matches(text))
            {
                registeredTypes.Add(m.Groups[1].Value);
            }

            foreach (Match m in PublicProtocolTypeRegex().Matches(text))
            {
                var typeName = m.Groups[1].Value;
                if (typeName.EndsWith("JsonContext", StringComparison.Ordinal) ||
                    typeName.EndsWith("Codec", StringComparison.Ordinal) ||
                    typeName.EndsWith("Client", StringComparison.Ordinal))
                {
                    continue;
                }
                declaredDtos.Add((Path.GetFileName(file), typeName));
            }

            // Strip single-line comments before checking for untyped `object` / `dynamic`.
            var withoutComments = string.Join('\n', text.Split('\n').Select(line =>
            {
                var idx = line.IndexOf("//", StringComparison.Ordinal);
                return idx >= 0 ? line[..idx] : line;
            }));
            Assert.False(UntypedObjectRegex().IsMatch(withoutComments),
                $"Protocol file {Path.GetFileName(file)} must not use untyped 'object' or 'dynamic' (Full Trim rule).");
        }

        Assert.NotEmpty(declaredDtos);
        foreach (var (file, typeName) in declaredDtos)
        {
            Assert.True(registeredTypes.Contains(typeName),
                $"Protocol type '{typeName}' in {file} must be registered via [JsonSerializable(typeof({typeName}))].");
        }
    }

    [Fact]
    public void DshMobileAgent_DoesNotUseReflectionBasedJsonSerializer()
    {
        foreach (var file in RepoLocator.EnumerateSourceFiles("src/DshMobile.Agent"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("new JsonSerializerOptions", text, StringComparison.Ordinal);
            Assert.DoesNotContain("JsonSerializer.Serialize<", text, StringComparison.Ordinal);
            Assert.DoesNotContain("JsonSerializer.Deserialize<", text, StringComparison.Ordinal);
        }
    }
}
