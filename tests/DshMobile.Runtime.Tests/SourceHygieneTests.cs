namespace DshMobile.Runtime.Tests;

/// <summary>
/// Our own text sources contain no stray C0 control bytes. A scripted edit once turned "native\build.ps1" into
/// "native&lt;BS&gt;uild.ps1" (\b read as an escape), which broke CI's native build step while every local test passed.
/// Escapes belong in source as escapes ("\u001b"), never as raw bytes. Vendored third-party files are not checked.
/// </summary>
public class SourceHygieneTests
{
    private static readonly string[] Roots = ["src", "tests", "native", "tooling", "docs", ".github", "migration"];

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".props", ".targets", ".slnx", ".kt", ".kts", ".java", ".c", ".h", ".ps1", ".sh", ".mjs", ".js",
        ".json", ".xml", ".html", ".css", ".md", ".yml", ".yaml", ".toml", ".txt",
    };

    private static bool Skipped(string relative) =>
        relative.Split('/').Skip(1).Any(part =>
            part.StartsWith('.') // generated / tool caches (.generated NDK copy, .gradle, .cxx)
            || part is "bin" or "obj" or "build" or "vendor" or "node_modules" or "TestResults");

    [Fact]
    public void TextSources_ContainNoRawControlBytes()
    {
        var root = Repo.Root;
        var checkedFiles = 0;
        var offenders = new List<string>();
        foreach (var top in Roots.Select(r => Path.Combine(root, r)).Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(top, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (Skipped(relative) || !TextExtensions.Contains(Path.GetExtension(file)))
                {
                    continue;
                }
                checkedFiles++;
                var bytes = File.ReadAllBytes(file);
                var line = 1;
                foreach (var b in bytes)
                {
                    if (b == (byte)'\n')
                    {
                        line++;
                    }
                    else if (b < 0x20 && b is not (byte)'\t' and not (byte)'\r')
                    {
                        offenders.Add($"{relative}:{line} byte 0x{b:x2}");
                        break;
                    }
                }
            }
        }
        Assert.True(checkedFiles > 100, $"only {checkedFiles} files scanned"); // never pass by scanning nothing
        Assert.Empty(offenders);
    }
}
