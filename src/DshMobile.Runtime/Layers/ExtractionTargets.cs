namespace DshMobile.Runtime.Layers;

/// <summary>
/// Side effects of a layer extraction. <see cref="LayerExtractor"/> owns all path decisions (confinement, link
/// resolution) on its own model of the tree and calls these with root-relative POSIX paths that are already proven
/// to be inside the root; a target never resolves paths itself.
/// </summary>
public interface IExtractionTarget
{
    void CreateDirectory(string relPath);

    /// <summary>Creates or truncates a regular file and copies <paramref name="data"/> into it.</summary>
    Task WriteFileAsync(string relPath, Stream data, CancellationToken ct);

    void CopyFile(string fromRelPath, string toRelPath);

    /// <summary>Creates a symlink whose target is the literal <paramref name="linkTarget"/> (never rewritten).</summary>
    void CreateSymlink(string relPath, string linkTarget);

    /// <summary>Sets the owner read/write/execute bits only (legacy <c>applyEntryMode</c>); best effort.</summary>
    void SetOwnerMode(string relPath, bool read, bool write, bool execute);
}

/// <summary>Writes nothing: used to compute the manifest of an archive (shadow comparison, tests).</summary>
public sealed class DryRunExtractionTarget : IExtractionTarget
{
    public static DryRunExtractionTarget Instance { get; } = new();

    public void CreateDirectory(string relPath)
    {
    }

    public async Task WriteFileAsync(string relPath, Stream data, CancellationToken ct) =>
        await data.CopyToAsync(Stream.Null, ct).ConfigureAwait(false);

    public void CopyFile(string fromRelPath, string toRelPath)
    {
    }

    public void CreateSymlink(string relPath, string linkTarget)
    {
    }

    public void SetOwnerMode(string relPath, bool read, bool write, bool execute)
    {
    }
}

/// <summary>Extracts into a real directory, which must be empty (the extractor's model starts empty).</summary>
public sealed class DiskExtractionTarget : IExtractionTarget
{
    private const UnixFileMode OwnerBits = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public DiskExtractionTarget(string rootDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDir);
        Directory.CreateDirectory(rootDir);
        if (Directory.EnumerateFileSystemEntries(rootDir).Any())
        {
            throw new IOException($"extraction root is not empty: {rootDir}");
        }
        RootDir = Path.GetFullPath(rootDir);
    }

    public string RootDir { get; }

    private string Full(string rel) => rel.Length == 0 ? RootDir : Path.Combine(RootDir, rel.Replace('/', Path.DirectorySeparatorChar));

    public void CreateDirectory(string relPath) => Directory.CreateDirectory(Full(relPath));

    public async Task WriteFileAsync(string relPath, Stream data, CancellationToken ct)
    {
        var fs = new FileStream(Full(relPath), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: false);
        await using (fs.ConfigureAwait(false))
        {
            await data.CopyToAsync(fs, ct).ConfigureAwait(false);
        }
    }

    public void CopyFile(string fromRelPath, string toRelPath)
    {
        if (fromRelPath == toRelPath)
        {
            // Legacy opens the destination (truncating) before reading the source: a self-link ends up empty.
            using var _ = new FileStream(Full(toRelPath), FileMode.Create, FileAccess.Write);
            return;
        }
        File.Copy(Full(fromRelPath), Full(toRelPath), overwrite: true);
    }

    public void CreateSymlink(string relPath, string linkTarget) => File.CreateSymbolicLink(Full(relPath), linkTarget);

    public void SetOwnerMode(string relPath, bool read, bool write, bool execute)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        try
        {
            var path = Full(relPath);
            var mode = File.GetUnixFileMode(path) & ~OwnerBits;
            if (read)
            {
                mode |= UnixFileMode.UserRead;
            }
            if (write)
            {
                mode |= UnixFileMode.UserWrite;
            }
            if (execute)
            {
                mode |= UnixFileMode.UserExecute;
            }
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort, like the legacy extractor.
        }
    }
}
