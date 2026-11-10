namespace DshMobile.Core.Files;

/// <summary>One directory entry as shown in the Files screen.</summary>
public sealed record WorkspaceEntry(
    string Name,
    string RelativePath,
    WorkspaceEntryKind Kind,
    long Size,
    DateTimeOffset Modified,
    bool Protected,
    string? LinkTarget);

public enum WorkspaceEntryKind
{
    Directory,
    File,
    /// <summary>A symlink. Never followed on the host: guest links (e.g. to /etc) would resolve to host paths.</summary>
    Symlink,
    Other,
}

/// <summary>Stable error codes for workspace operations (shown in UI, never "success" on failure).</summary>
public static class WorkspaceErrors
{
    public const string OutsideRoot = "OUTSIDE_ROOT";
    public const string ThroughSymlink = "THROUGH_SYMLINK";
    public const string Protected = "PROTECTED";
    public const string NotFound = "NOT_FOUND";
    public const string NotAFile = "NOT_A_FILE";
    public const string NotADirectory = "NOT_A_DIRECTORY";
    public const string TooLarge = "TOO_LARGE";
    public const string Exists = "EXISTS";
    public const string InvalidName = "INVALID_NAME";
}

public sealed class WorkspaceException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Host-side access to the DSH workspace (guest <c>/root/projects</c> = host <c>files/user-data</c>).
///
/// Rules (docs/SECURITY.md):
/// <list type="bullet">
/// <item>Paths are relative to the root; absolute paths, <c>..</c> segments and NUL are rejected.</item>
/// <item>No path component may be a symlink: links are listed but never traversed or opened, because a
/// guest link resolves differently on the host.</item>
/// <item><c>.dsh/</c> (DSH_HOME: profiles, settings, possibly credentials) is protected: listed, but its
/// contents cannot be previewed, exported or overwritten from the Files screen.</item>
/// </list>
/// </summary>
public sealed class WorkspaceFileSystem
{
    public const long DefaultPreviewLimit = 256 * 1024;
    private static readonly string[] ProtectedTopLevel = [".dsh"];

    private readonly string _root;

    public WorkspaceFileSystem(string rootDirectory)
    {
        _root = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(_root))
        {
            throw new WorkspaceException(WorkspaceErrors.NotFound, $"workspace root does not exist: {_root}");
        }
    }

    public string Root => _root;

    /// <summary>Normalizes a caller path ("" = root) to forward-slash relative form, or throws.</summary>
    public static string NormalizeRelative(string? relative)
    {
        var raw = (relative ?? "").Replace('\\', '/');
        if (raw.Contains('\0'))
        {
            throw new WorkspaceException(WorkspaceErrors.InvalidName, "NUL in path");
        }
        if (raw.StartsWith('/') || (raw.Length >= 2 && raw[1] == ':'))
        {
            throw new WorkspaceException(WorkspaceErrors.OutsideRoot, "absolute paths are not allowed");
        }
        var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            if (p == "..")
            {
                throw new WorkspaceException(WorkspaceErrors.OutsideRoot, "'..' is not allowed");
            }
        }
        return string.Join('/', parts.Where(p => p != "."));
    }

    public static bool IsProtected(string normalizedRelative)
    {
        var first = normalizedRelative.Split('/', 2)[0];
        return ProtectedTopLevel.Contains(first, StringComparer.Ordinal);
    }

    /// <summary>
    /// Resolves to an absolute host path. Every existing component below the root must be a real
    /// directory or file, never a symlink.
    /// </summary>
    public string Resolve(string? relative)
    {
        var norm = NormalizeRelative(relative);
        var current = _root;
        foreach (var part in norm.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            // LinkTarget reads the link itself (readlink / reparse point), so dangling links count too.
            if (IsLink(current))
            {
                throw new WorkspaceException(WorkspaceErrors.ThroughSymlink, $"'{norm}' passes through a symlink");
            }
        }
        var full = Path.GetFullPath(current);
        if (!(full == _root || full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            throw new WorkspaceException(WorkspaceErrors.OutsideRoot, "path escapes the workspace");
        }
        return full;
    }

    /// <summary>Lists a directory: directories first, then by name (ordinal, case-insensitive).</summary>
    public IReadOnlyList<WorkspaceEntry> List(string? relative)
    {
        var norm = NormalizeRelative(relative);
        var dir = Resolve(norm);
        if (!Directory.Exists(dir))
        {
            throw new WorkspaceException(File.Exists(dir) ? WorkspaceErrors.NotADirectory : WorkspaceErrors.NotFound, norm);
        }
        var result = new List<WorkspaceEntry>();
        foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            var rel = norm.Length == 0 ? info.Name : norm + "/" + info.Name;
            var kind = info.LinkTarget is not null ? WorkspaceEntryKind.Symlink
                : info is DirectoryInfo ? WorkspaceEntryKind.Directory
                : info is FileInfo ? WorkspaceEntryKind.File
                : WorkspaceEntryKind.Other;
            long size = kind == WorkspaceEntryKind.File ? ((FileInfo)info).Length : 0;
            result.Add(new WorkspaceEntry(info.Name, rel, kind, size, info.LastWriteTimeUtc, IsProtected(rel), info.LinkTarget));
        }
        return result
            .OrderBy(e => e.Kind == WorkspaceEntryKind.Directory ? 0 : 1)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Opens a regular, unprotected file for reading (preview / export).</summary>
    public FileStream OpenRead(string relative, long? maxBytes = null)
    {
        var norm = NormalizeRelative(relative);
        if (IsProtected(norm))
        {
            throw new WorkspaceException(WorkspaceErrors.Protected, $"'{norm}' is protected");
        }
        var path = Resolve(norm);
        if (!File.Exists(path))
        {
            throw new WorkspaceException(Directory.Exists(path) ? WorkspaceErrors.NotAFile : WorkspaceErrors.NotFound, norm);
        }
        var length = new FileInfo(path).Length;
        if (maxBytes is { } limit && length > limit)
        {
            throw new WorkspaceException(WorkspaceErrors.TooLarge, $"{length} bytes > {limit}");
        }
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }

    /// <summary>
    /// Writes a new file into an existing, unprotected directory. Never overwrites (EXISTS), writes to a
    /// temp file first, then renames, so a failed import leaves no partial file behind.
    /// </summary>
    public async Task<string> ImportAsync(string directoryRelative, string fileName, Stream content, CancellationToken ct = default)
    {
        var name = SanitizeFileName(fileName);
        var dirNorm = NormalizeRelative(directoryRelative);
        var targetRel = dirNorm.Length == 0 ? name : dirNorm + "/" + name;
        if (IsProtected(targetRel))
        {
            throw new WorkspaceException(WorkspaceErrors.Protected, $"'{targetRel}' is protected");
        }
        var dir = Resolve(dirNorm);
        if (!Directory.Exists(dir))
        {
            throw new WorkspaceException(WorkspaceErrors.NotADirectory, dirNorm);
        }
        var target = Path.Combine(dir, name);
        if (File.Exists(target) || Directory.Exists(target) || IsLink(target))
        {
            throw new WorkspaceException(WorkspaceErrors.Exists, targetRel);
        }
        var tmp = Path.Combine(dir, $".{name}.{Guid.NewGuid():N}.part");
        try
        {
            await using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
            {
                await content.CopyToAsync(output, ct).ConfigureAwait(false);
            }
            File.Move(tmp, target, overwrite: false);
            return targetRel;
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    private static bool IsLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (IOException)
        {
            return false; // nothing at this path
        }
    }

    /// <summary>Keeps a display name usable as a single path component.</summary>
    public static string SanitizeFileName(string? name)
    {
        var n = (name ?? "").Replace('\\', '/');
        n = n[(n.LastIndexOf('/') + 1)..].Trim();
        n = new string(n.Where(c => c >= 0x20 && c != 0x7F).ToArray());
        if (n.Length == 0 || n == "." || n == "..")
        {
            throw new WorkspaceException(WorkspaceErrors.InvalidName, $"invalid file name '{name}'");
        }
        return n.Length > 200 ? n[..200] : n;
    }
}
