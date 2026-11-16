using System.Formats.Tar;
using System.IO.Compression;

namespace DshMobile.Core.Files;

public enum ArchiveFormat
{
    Zip,
    Tar,
    TarGz,
}

/// <summary>Bounds on one extraction (counted from what is really written, not from headers).</summary>
public sealed record ArchiveLimits(int MaxEntries = 100_000, long MaxTotalBytes = 4L * 1024 * 1024 * 1024);

/// <summary>An extraction: the new directory, what was written, and entries left out on purpose (links, devices).</summary>
public sealed record ExtractResult(string Directory, int Files, int Directories, long Bytes, IReadOnlyList<string> Skipped);

public sealed record ZipResult(string Path, int Files, int SkippedLinks);

/// <summary>
/// vNext PR-09: zip / tar / tar.gz into and out of the workspace. An archive is untrusted input:
/// <list type="bullet">
/// <item>it is unpacked into a fresh staging directory next to the target and renamed into place only when every entry
/// passed; any failure deletes the staging tree, so a refused archive leaves nothing behind;</item>
/// <item>entry names go through <see cref="WorkspaceFileSystem.NormalizeRelative"/> (no absolute paths, drive letters,
/// <c>..</c>, NUL) and every component must be a clean file name — otherwise the whole archive is
/// <see cref="WorkspaceErrors.ArchiveUnsafe"/>;</item>
/// <item>symlinks, hard links and devices are never created (listed in <see cref="ExtractResult.Skipped"/>); a duplicate
/// or file/directory clash aborts; no setuid / setgid bits, only "executable or not";</item>
/// <item>the entry count and the bytes actually written are capped (<see cref="ArchiveLimits"/>).</item>
/// </list>
/// Creating a zip never follows symlinks and never reads <c>.dsh</c>.
/// </summary>
public sealed partial class WorkspaceProvider
{
    public static ArchiveFormat? FormatOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var n = name.ToLowerInvariant();
        return n.EndsWith(".zip", StringComparison.Ordinal) ? ArchiveFormat.Zip
            : n.EndsWith(".tar.gz", StringComparison.Ordinal) || n.EndsWith(".tgz", StringComparison.Ordinal) ? ArchiveFormat.TarGz
            : n.EndsWith(".tar", StringComparison.Ordinal) ? ArchiveFormat.Tar
            : null;
    }

    private static string StemOf(string name)
    {
        var n = name.ToLowerInvariant();
        foreach (var ext in new[] { ".tar.gz", ".tgz", ".tar", ".zip" })
        {
            if (n.EndsWith(ext, StringComparison.Ordinal) && name.Length > ext.Length)
            {
                return name[..^ext.Length];
            }
        }
        return name + " (extracted)";
    }

    public Task<ExtractResult> ExtractAsync(string archive, string targetDirectory, ArchiveLimits? limits, CancellationToken ct) =>
        Task.Run(() => Extract(archive, targetDirectory, limits, ct), ct);

    public ExtractResult Extract(string archive, string targetDirectory, ArchiveLimits? limits = null, CancellationToken ct = default)
    {
        limits ??= new ArchiveLimits();
        var archiveNorm = WorkspaceFileSystem.NormalizeRelative(archive);
        var format = FormatOf(archiveNorm) ?? throw new WorkspaceException(WorkspaceErrors.ArchiveUnsupported, $"'{archiveNorm}' is not a .zip / .tar / .tar.gz");
        var targetNorm = WorkspaceFileSystem.NormalizeRelative(targetDirectory);
        if (WorkspaceFileSystem.IsProtected(targetNorm) || targetNorm == ".dsh")
        {
            throw new WorkspaceException(WorkspaceErrors.Protected, $"'{targetNorm}' is protected");
        }
        var targetDir = RequireDirectory(targetNorm);
        var stem = WorkspaceFileSystem.SanitizeFileName(StemOf(Split(archiveNorm).Name));
        var staging = Path.Combine(targetDir, $".{stem}.{Guid.NewGuid():N}.extract");
        var state = new ExtractState(staging, limits);
        try
        {
            Directory.CreateDirectory(staging);
            using (var input = _fs.OpenRead(archiveNorm))
            {
                switch (format)
                {
                    case ArchiveFormat.Zip:
                        ExtractZip(input, state, ct);
                        break;
                    case ArchiveFormat.Tar:
                        ExtractTar(input, state, ct);
                        break;
                    default:
                        using (var gz = new GZipStream(input, CompressionMode.Decompress))
                        {
                            ExtractTar(gz, state, ct);
                        }
                        break;
                }
            }
            var finalName = UniqueName(targetDir, stem);
            Directory.Move(staging, Path.Combine(targetDir, finalName));
            return new ExtractResult(Join(targetNorm, finalName), state.Files, state.Directories, state.Bytes, state.Skipped);
        }
        catch (InvalidDataException e)
        {
            throw new WorkspaceException(WorkspaceErrors.ArchiveUnsupported, "damaged or unsupported archive: " + e.Message);
        }
        catch (FormatException e)
        {
            throw new WorkspaceException(WorkspaceErrors.ArchiveUnsupported, "damaged or unsupported archive: " + e.Message);
        }
        finally
        {
            DeleteQuietly(staging);
        }
    }

    private sealed class ExtractState(string staging, ArchiveLimits limits)
    {
        public readonly List<string> Skipped = [];
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
        public int Files;
        public int Directories;
        public long Bytes;
        private int _entries;

        /// <summary>Validates one entry name; null for the archive's own root ("./").</summary>
        public string? Target(string rawName, bool directory)
        {
            if (++_entries > limits.MaxEntries)
            {
                throw new WorkspaceException(WorkspaceErrors.ArchiveTooLarge, $"more than {limits.MaxEntries} entries");
            }
            string norm;
            try
            {
                norm = WorkspaceFileSystem.NormalizeRelative(rawName);
            }
            catch (WorkspaceException e)
            {
                throw new WorkspaceException(WorkspaceErrors.ArchiveUnsafe, $"entry '{rawName}': {e.Message}");
            }
            if (norm.Length == 0)
            {
                return null;
            }
            foreach (var part in norm.Split('/'))
            {
                string clean;
                try
                {
                    clean = WorkspaceFileSystem.SanitizeFileName(part);
                }
                catch (WorkspaceException)
                {
                    clean = "";
                }
                if (clean != part)
                {
                    throw new WorkspaceException(WorkspaceErrors.ArchiveUnsafe, $"entry '{rawName}' has an unusable name");
                }
            }
            // Case-insensitive on purpose: "A.txt" and "a.txt" would collide on many filesystems and at export.
            if (!_seen.Add(norm) && !directory)
            {
                throw new WorkspaceException(WorkspaceErrors.ArchiveUnsafe, $"duplicate entry '{norm}'");
            }
            return Path.Combine(staging, norm.Replace('/', Path.DirectorySeparatorChar));
        }

        public void WriteDir(string full)
        {
            if (System.IO.File.Exists(full))
            {
                throw new WorkspaceException(WorkspaceErrors.ArchiveUnsafe, $"'{Path.GetFileName(full)}' is both a file and a directory");
            }
            if (!Directory.Exists(full))
            {
                Directories++;
            }
            Directory.CreateDirectory(full);
        }

        public void WriteFile(string full, Stream data, bool executable, DateTimeOffset? modified, CancellationToken ct)
        {
            var parent = Path.GetDirectoryName(full)!;
            if (System.IO.File.Exists(parent))
            {
                throw new WorkspaceException(WorkspaceErrors.ArchiveUnsafe, $"'{Path.GetFileName(parent)}' is both a file and a directory");
            }
            Directory.CreateDirectory(parent);
            if (Directory.Exists(full))
            {
                throw new WorkspaceException(WorkspaceErrors.ArchiveUnsafe, $"'{Path.GetFileName(full)}' is both a file and a directory");
            }
            using (var output = new FileStream(full, FileMode.CreateNew, FileAccess.Write))
            {
                var buffer = new byte[81920];
                int n;
                while ((n = data.Read(buffer)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    Bytes += n;
                    if (Bytes > limits.MaxTotalBytes)
                    {
                        throw new WorkspaceException(WorkspaceErrors.ArchiveTooLarge, $"more than {limits.MaxTotalBytes} bytes uncompressed");
                    }
                    output.Write(buffer, 0, n);
                }
            }
            if (!OperatingSystem.IsWindows())
            {
                const UnixFileMode baseMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
                System.IO.File.SetUnixFileMode(full, executable
                    ? baseMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute
                    : baseMode);
            }
            if (modified is { } m && m.Year >= 1980)
            {
                System.IO.File.SetLastWriteTimeUtc(full, m.UtcDateTime);
            }
            Files++;
        }
    }

    private static void ExtractZip(Stream input, ExtractState state, CancellationToken ct)
    {
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var isDir = name.EndsWith('/');
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType is 0xA000 or 0x2000 or 0x6000 or 0x1000 or 0xC000)
            {
                state.Target(name, isDir); // still validated and counted
                state.Skipped.Add(name + (unixType == 0xA000 ? " (symlink)" : " (special file)"));
                continue;
            }
            var target = state.Target(name, isDir);
            if (target is null)
            {
                continue;
            }
            if (isDir)
            {
                state.WriteDir(target);
                continue;
            }
            using var data = entry.Open();
            var executable = (entry.ExternalAttributes >> 16 & 0x49) != 0; // any x bit of a Unix-made entry
            state.WriteFile(target, data, executable, entry.LastWriteTime, ct);
        }
    }

    private static void ExtractTar(Stream input, ExtractState state, CancellationToken ct)
    {
        using var tar = new TarReader(input, leaveOpen: true);
        while (tar.GetNextEntry() is { } entry)
        {
            ct.ThrowIfCancellationRequested();
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    if (state.Target(entry.Name, directory: true) is { } dir)
                    {
                        state.WriteDir(dir);
                    }
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                    if (state.Target(entry.Name, directory: false) is { } file)
                    {
                        using var empty = new MemoryStream();
                        state.WriteFile(file, entry.DataStream ?? empty, (entry.Mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0,
                            entry.ModificationTime, ct);
                    }
                    break;
                case TarEntryType.SymbolicLink or TarEntryType.HardLink:
                    state.Target(entry.Name, directory: false);
                    state.Skipped.Add($"{entry.Name} ({(entry.EntryType == TarEntryType.SymbolicLink ? "symlink" : "hard link")} → {entry.LinkName})");
                    break;
                case TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes:
                    break; // metadata for the next entry; the reader applies it
                default:
                    state.Target(entry.Name, directory: false);
                    state.Skipped.Add($"{entry.Name} ({entry.EntryType})");
                    break;
            }
        }
    }

    public Task<ZipResult> CreateZipAsync(IReadOnlyList<string> paths, string targetDirectory, string zipName, CancellationToken ct) =>
        Task.Run(() => CreateZip(paths, targetDirectory, zipName, ct), ct);

    /// <summary>Zips the given entries (each under its own name) into <paramref name="targetDirectory"/>.</summary>
    public ZipResult CreateZip(IReadOnlyList<string> paths, string targetDirectory, string zipName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            throw new WorkspaceException(WorkspaceErrors.NotFound, "nothing selected");
        }
        var name = StrictName(zipName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? zipName : zipName + ".zip");
        var targetNorm = WorkspaceFileSystem.NormalizeRelative(targetDirectory);
        RequireUnprotected(Join(targetNorm, name));
        var targetDir = RequireDirectory(targetNorm);
        var sources = new List<(string Full, string EntryName, bool Dir)>();
        foreach (var p in paths)
        {
            var norm = RequireChangeable(p);
            RequireUnprotected(norm);
            var (parent, entryName) = Split(norm);
            var full = Path.Combine(RequireDirectory(parent), entryName);
            if (WorkspaceFileSystem.IsLink(full) || !Exists(full))
            {
                throw new WorkspaceException(WorkspaceFileSystem.IsLink(full) ? WorkspaceErrors.ThroughSymlink : WorkspaceErrors.NotFound, norm);
            }
            sources.Add((full, entryName, Directory.Exists(full)));
        }
        var tmp = Path.Combine(targetDir, $".{name}.{Guid.NewGuid():N}.part");
        var files = 0;
        var skipped = 0;
        try
        {
            using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
            {
                void AddFile(string full, string entry)
                {
                    ct.ThrowIfCancellationRequested();
                    zip.CreateEntryFromFile(full, entry, CompressionLevel.Optimal);
                    files++;
                }
                void AddTree(string dir, string prefix)
                {
                    zip.CreateEntry(prefix + "/");
                    foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                    {
                        if (info.FullName == tmp)
                        {
                            continue; // the zip being written, when it lands inside a selected folder
                        }
                        if (info.LinkTarget is not null)
                        {
                            skipped++;
                        }
                        else if (info is DirectoryInfo)
                        {
                            AddTree(info.FullName, prefix + "/" + info.Name);
                        }
                        else if (info is FileInfo)
                        {
                            AddFile(info.FullName, prefix + "/" + info.Name);
                        }
                    }
                }
                foreach (var (full, entryName, dir) in sources)
                {
                    if (dir)
                    {
                        AddTree(full, entryName);
                    }
                    else
                    {
                        AddFile(full, entryName);
                    }
                }
            }
            var finalName = UniqueName(targetDir, name);
            File.Move(tmp, Path.Combine(targetDir, finalName));
            return new ZipResult(Join(targetNorm, finalName), files, skipped);
        }
        finally
        {
            DeleteQuietly(tmp);
        }
    }
}
