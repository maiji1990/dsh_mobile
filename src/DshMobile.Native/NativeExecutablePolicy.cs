namespace DshMobile.Native;

/// <summary>
/// Android 10+ W^X rule (migration plan section 8): an app may only execute ELF files that the package installer
/// extracted into <c>nativeLibraryDir</c>; anything under <c>filesDir</c>/<c>cacheDir</c> is writable and therefore
/// not executable. PRoot (<c>libproot.so</c>) and its loader are started from there, and guest binaries are reached
/// only through PRoot. This policy is the single place that decides whether a host path may be exec'd.
/// </summary>
public sealed class NativeExecutablePolicy
{
    private readonly string _nativeLibraryDir;

    public NativeExecutablePolicy(string nativeLibraryDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(nativeLibraryDir);
        _nativeLibraryDir = Normalize(nativeLibraryDir);
    }

    /// <summary>
    /// Resolves <paramref name="libraryFileName"/> (e.g. <c>libproot.so</c>) inside nativeLibraryDir. Only a bare
    /// <c>lib*.so</c> file name is accepted: no separators, no traversal, no other directory.
    /// </summary>
    public NativeResult<string> ResolveExecutable(string libraryFileName)
    {
        if (string.IsNullOrEmpty(libraryFileName)
            || libraryFileName.Contains('/', StringComparison.Ordinal)
            || libraryFileName.Contains('\\', StringComparison.Ordinal)
            || libraryFileName.Contains('\0', StringComparison.Ordinal)
            || libraryFileName is "." or ".."
            || !libraryFileName.StartsWith("lib", StringComparison.Ordinal)
            || !libraryFileName.EndsWith(".so", StringComparison.Ordinal))
        {
            return NativeResult<string>.Fail(NativeErrorCodes.ExecutableNotAllowed, $"not a bare lib*.so name: '{libraryFileName}'");
        }
        return NativeResult<string>.Success(_nativeLibraryDir + "/" + libraryFileName);
    }

    /// <summary>True when <paramref name="path"/> lies directly in nativeLibraryDir (the only executable location).</summary>
    public bool IsExecutableLocation(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }
        var normalized = Normalize(path);
        var slash = normalized.LastIndexOf('/');
        return slash > 0 && string.Equals(normalized[..slash], _nativeLibraryDir, StringComparison.Ordinal);
    }

    /// <summary>
    /// Lexical normalization with POSIX separators. Rejects nothing by itself; callers compare the result. Android
    /// symlinks inside nativeLibraryDir are installer-controlled, so lexical comparison is sufficient here.
    /// </summary>
    private static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var seg in path.Replace('\\', '/').Split('/'))
        {
            if (seg.Length == 0 || seg == ".")
            {
                continue;
            }
            if (seg == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
                continue;
            }
            parts.Add(seg);
        }
        return "/" + string.Join('/', parts);
    }
}
