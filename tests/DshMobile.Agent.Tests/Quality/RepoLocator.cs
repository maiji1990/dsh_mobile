namespace DshMobile.Agent.Tests.Quality;

internal static class RepoLocator
{
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DshMobile.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate DshMobile.slnx above " + AppContext.BaseDirectory);
    }

    public static IEnumerable<string> EnumerateSourceFiles(string projectRelativeDir, string pattern = "*.cs")
    {
        var root = Path.Combine(FindRepoRoot(), projectRelativeDir);
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file);
            var segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => string.Equals(s, "bin", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(s, "obj", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            yield return file;
        }
    }
}
